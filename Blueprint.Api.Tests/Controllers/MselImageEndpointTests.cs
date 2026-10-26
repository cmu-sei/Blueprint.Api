// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// <c>MselImageService</c> / <c>MselImageController</c> - the image on a MSEL's card. Reading it wants
/// <c>ViewMsels</c> or <c>MselViewRequirement</c>, which takes in everyone on one of the MSEL's teams;
/// setting and removing it want <c>EditMsels</c> or the MSEL's <c>Owner</c> role, like the MSEL's other details.
/// </summary>
/// <remarks>
/// 401 and 403 for these routes live in <see cref="RouteAuthorizationTests"/>.
/// </remarks>
public class MselImageEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

    // ---------------------------------------------------------------------------------------------
    // PUT msels/{id}/image
    // ---------------------------------------------------------------------------------------------

    /// <summary>Set by the MSEL's owner stores the image, typed from its bytes, and marks the MSEL.</summary>
    [Fact]
    public async Task Set_ForAnOwnerOfTheMsel_StoresTheImageAndMarksTheMsel()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        // the client claims octet-stream; the stored type comes from the PNG signature
        var response = await Client(actor).PutAsync($"api/msels/{msel.Id}/image", ImagePart(Png, "application/octet-stream"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answered = await response.Content.ReadFromJsonAsync<ViewModels.Msel>(JsonOptions, Ct);
        Assert.NotNull(answered.ImageDateModified);
        var stored = await ReadBack(db => db.MselImages.SingleAsync(i => i.MselId == msel.Id, Ct));
        Assert.Equal(Png, stored.Data);
        Assert.Equal("image/png", stored.ContentType);
    }

    /// <summary>
    /// Set answers the whole MSEL, as an update does, including the Owner role a creator holds without a
    /// role row. A bare mapping of the MSEL row leaves that role out.
    /// </summary>
    [Fact]
    public async Task Set_AnswersTheWholeMsel()
    {
        var actor = await Actor().SeedAsync();
        var msel = await SeedMsel(withImage: false, createdBy: actor.Id);

        var response = await Client(actor).PutAsync($"api/msels/{msel.Id}/image", ImagePart(Png, "image/png"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answered = await response.Content.ReadFromJsonAsync<ViewModels.Msel>(JsonOptions, Ct);
        Assert.Contains(answered.UserMselRoles, r => r.UserId == actor.Id && r.Role == MselRole.Owner);
    }

    /// <summary>Set over an existing image replaces it, leaving one row with the new bytes and type.</summary>
    [Fact]
    public async Task Set_OverAnExistingImage_ReplacesIt()
    {
        var msel = await SeedMsel(withImage: true);
        byte[] gif = [0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00];

        var response = await RootClient.PutAsync($"api/msels/{msel.Id}/image", ImagePart(gif, "image/gif"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await ReadBack(db => db.MselImages.SingleAsync(i => i.MselId == msel.Id, Ct));
        Assert.Equal(gif, stored.Data);
        Assert.Equal("image/gif", stored.ContentType);
    }

    /// <summary>Set with an SVG is a 400, because SVG can carry script, and nothing is stored.</summary>
    [Fact]
    public async Task Set_WithAnSvg_Is400()
    {
        var msel = await SeedMsel();
        var svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");

        var response = await RootClient.PutAsync($"api/msels/{msel.Id}/image", ImagePart(svg, "image/svg+xml"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await ReadBack(db => db.MselImages.AnyAsync(i => i.MselId == msel.Id, Ct)));
    }

    /// <summary>Set with an image over the limit is a 400.</summary>
    [Fact]
    public async Task Set_WithAnImageOverTheLimit_Is400()
    {
        var msel = await SeedMsel();
        var tooBig = Png.Concat(new byte[MselImageService.MaxImageBytes]).ToArray();

        var response = await RootClient.PutAsync($"api/msels/{msel.Id}/image", ImagePart(tooBig, "image/png"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Set by an editor who is not the owner is a 403, as for the MSEL's other details.</summary>
    [Fact]
    public async Task Set_ForAnEditorOfTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var response = await Client(actor).PutAsync($"api/msels/{msel.Id}/image", ImagePart(Png, "image/png"), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Set_ForAMselThatIsNotThere_Is404()
    {
        var response = await RootClient.PutAsync($"api/msels/{Guid.NewGuid()}/image", ImagePart(Png, "image/png"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // GET msels/{id}/image
    // ---------------------------------------------------------------------------------------------

    /// <summary>Get for a member of one of the MSEL's teams answers the image, so it shows on the join page.</summary>
    [Fact]
    public async Task Get_ForATeamMember_ReturnsTheImage()
    {
        var msel = await SeedMsel();
        var team = TestData.Team(msel.Id);
        await Seed(team, new MselImageEntity { MselId = msel.Id, Data = Png, ContentType = "image/png" });
        var actor = await Actor().OnTeam(team).SeedAsync();

        var response = await Client(actor).GetAsync($"api/msels/{msel.Id}/image", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    /// <summary>Get for a user with no part in the MSEL is a 403.</summary>
    [Fact]
    public async Task Get_ForAUserNotOnTheMsel_Is403()
    {
        var msel = await SeedMsel();
        await Seed(new MselImageEntity { MselId = msel.Id, Data = Png, ContentType = "image/png" });
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync($"api/msels/{msel.Id}/image", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_ForAMselWithNoImage_Is404()
    {
        var msel = await SeedMsel();

        var response = await RootClient.GetAsync($"api/msels/{msel.Id}/image", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE msels/{id}/image
    // ---------------------------------------------------------------------------------------------

    /// <summary>Delete by the owner removes the image and clears the mark.</summary>
    [Fact]
    public async Task Delete_ForAnOwnerOfTheMsel_RemovesTheImage()
    {
        var msel = await SeedMsel(withImage: true);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync($"api/msels/{msel.Id}/image", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answered = await response.Content.ReadFromJsonAsync<ViewModels.Msel>(JsonOptions, Ct);
        Assert.Null(answered.ImageDateModified);
        Assert.False(await ReadBack(db => db.MselImages.AnyAsync(i => i.MselId == msel.Id, Ct)));
    }

    /// <summary>
    /// Delete answers the whole MSEL, as an update does, including the Owner role a creator holds without a
    /// role row. A bare mapping of the MSEL row leaves that role out.
    /// </summary>
    [Fact]
    public async Task Delete_AnswersTheWholeMsel()
    {
        var actor = await Actor().SeedAsync();
        var msel = await SeedMsel(withImage: true, createdBy: actor.Id);

        var response = await Client(actor).DeleteAsync($"api/msels/{msel.Id}/image", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answered = await response.Content.ReadFromJsonAsync<ViewModels.Msel>(JsonOptions, Ct);
        Assert.Contains(answered.UserMselRoles, r => r.UserId == actor.Id && r.Role == MselRole.Owner);
    }

    // ---------------------------------------------------------------------------------------------
    // POST msels/json
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// An imported file's MSEL ID and image date are the importer's to write, so naming another MSEL's
    /// ID, even one the importer cannot view, must not copy that MSEL's image.
    /// </summary>
    [Fact]
    public async Task Import_NamingAnotherMselsIdAndImageDate_CopiesNoImage()
    {
        var other = await SeedMsel(withImage: true);
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var file = TestData.Msel();
        file.Id = other.Id;
        file.Name = "Imported " + Guid.NewGuid();
        file.ImageDateModified = other.ImageDateModified;
        var json = JsonSerializer.SerializeToNode(file, new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.IgnoreCycles });
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json"), "ToUpload", "copy.json");

        var response = await Client(actor).PostAsync("api/msels/json", form, Ct);

        Assert.True(response.IsSuccessStatusCode, $"import answered {response.StatusCode}");
        var imported = await ReadBack(db => db.Msels.SingleAsync(m => m.Name == file.Name, Ct));
        Assert.Null(imported.ImageDateModified);
        Assert.False(await ReadBack(db => db.MselImages.AnyAsync(i => i.MselId == imported.Id, Ct)));
    }

    // ---------------------------------------------------------------------------------------------
    // POST msels/{id}/copy
    // ---------------------------------------------------------------------------------------------

    /// <summary>A copy of a MSEL carries its image, so an event launched from a template keeps it.</summary>
    [Fact]
    public async Task Copy_OfAMselWithAnImage_CopiesTheImage()
    {
        var msel = await SeedMsel(withImage: true);

        var response = await RootClient.PostAsync($"api/msels/{msel.Id}/copy", null, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var copy = await response.Content.ReadFromJsonAsync<ViewModels.Msel>(JsonOptions, Ct);
        Assert.NotEqual(msel.Id, copy.Id);
        Assert.NotNull(copy.ImageDateModified);
        var stored = await ReadBack(db => db.MselImages.SingleAsync(i => i.MselId == copy.Id, Ct));
        Assert.Equal(Png, stored.Data);
    }

    private async Task<MselEntity> SeedMsel(bool withImage = false, Guid? createdBy = null)
    {
        var msel = TestData.Msel();
        if (createdBy != null)
            msel.CreatedBy = (Guid)createdBy;
        if (withImage)
        {
            msel.ImageDateModified = DateTime.UtcNow;
            await Seed(msel, new MselImageEntity { MselId = msel.Id, Data = Png, ContentType = "image/png" });
        }
        else
        {
            await Seed(msel);
        }

        return msel;
    }

    private static MultipartFormDataContent ImagePart(byte[] data, string contentType)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(data);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(file, "ToUpload", "image");
        return content;
    }
}
