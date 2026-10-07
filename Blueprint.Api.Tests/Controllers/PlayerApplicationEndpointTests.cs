// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// <c>PlayerApplicationService</c> / <c>PlayerApplicationController</c> - the five CRUD routes over the
/// applications a MSEL puts on its teams' Player views. Reads want <c>ViewMsels</c> or membership of the
/// MSEL; writes want <c>EditMsels</c> or the <c>Owner</c> role.
/// </summary>
/// <remarks>
/// 401 and 403 for these routes live in <see cref="RouteAuthorizationTests"/>; this file covers the happy
/// path and the not-found path per route, per the thin protocol. <c>POST playerApplications/push</c> is
/// covered by <see cref="PlayerServiceTests"/> and is absent from both files here.
/// <para />
/// The controller throws no <c>ForbiddenException</c> of its own on any of the six actions: it resolves a
/// permission into a <c>bool</c> and lets the service's requirement helpers decide, a different helper
/// per route.
/// </remarks>
public class PlayerApplicationEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET msels/{mselId}/playerApplications
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByMsel_ForAViewerOfTheMsel_ReturnsOnlyThatMselsApplications()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();
        var mine = TestData.PlayerApplication(msel.Id, "mine");
        var otherMsel = await SeedMsel();
        await Seed(mine, TestData.PlayerApplication(otherMsel.Id, "theirs"));

        var response = await Client(actor).GetAsync($"api/msels/{msel.Id}/playerApplications", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content
            .ReadFromJsonAsync<List<ViewModels.PlayerApplication>>(JsonOptions, Ct);
        Assert.Single(list);
        Assert.Equal("mine", list[0].Name);
    }

    /// <summary>Get by MSEL for a MSEL that is not there is answered with a 500 or 200 for a view MSELs holder.</summary>
    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_Is500_Or200ForAViewMselsHolder()
    {
        var stranger = await Actor().SeedAsync();
        var privileged = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();
        var id = Guid.NewGuid();

        var refused = await Client(stranger).GetAsync($"api/msels/{id}/playerApplications", Ct);
        Assert.Equal(HttpStatusCode.InternalServerError, refused.StatusCode);

        var answered = await Client(privileged).GetAsync($"api/msels/{id}/playerApplications", Ct);
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        Assert.Empty(await answered.Content
            .ReadFromJsonAsync<List<ViewModels.PlayerApplication>>(JsonOptions, Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // GET playerApplications/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ForAViewerOfTheMsel_Is200()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();
        var application = TestData.PlayerApplication(msel.Id, "console");
        await Seed(application);

        var response = await Client(actor).GetAsync($"api/playerApplications/{application.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answered = await response.Content
            .ReadFromJsonAsync<ViewModels.PlayerApplication>(JsonOptions, Ct);
        Assert.Equal(application.Id, answered.Id);
        Assert.Equal(msel.Id, answered.MselId);
        Assert.Equal("console", answered.Name);
    }

    /// <summary>Get for a unit member with no role is answered with a 200 though they may not list the same row.</summary>
    [Fact]
    public async Task Get_ForAUnitMemberWithNoRole_Is200_ThoughTheyMayNotListTheSameRow()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();
        await RemoveTheRoleRows(actor.Id);
        var application = TestData.PlayerApplication(msel.Id);
        await Seed(application);

        var read = await Client(actor).GetAsync($"api/playerApplications/{application.Id}", Ct);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        var listed = await Client(actor).GetAsync($"api/msels/{msel.Id}/playerApplications", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, listed.StatusCode);
    }

    /// <summary>Get for an id that is not there is answered with a 500.</summary>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"api/playerApplications/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // POST playerApplications
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_ForAnOwnerOfTheMsel_Is201()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "api/playerApplications", Body(msel.Id) with { name = "created" }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content
            .ReadFromJsonAsync<ViewModels.PlayerApplication>(JsonOptions, Ct);
        Assert.Equal(msel.Id, created.MselId);
        Assert.Equal(actor.Id, created.CreatedBy);
        Assert.EndsWith($"/api/playerapplications/{created.Id}", response.Headers.Location.ToString());

        var stored = await ReadBack(rb => rb.PlayerApplications.SingleAsync(x => x.Id == created.Id, Ct));
        Assert.Equal("created", stored.Name);
    }

    /// <summary>Create for a MSEL that is not there is answered with a 500.</summary>
    [Fact]
    public async Task Create_ForAMselThatIsNotThere_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "api/playerApplications", Body(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT playerApplications/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_ForAnOwnerOfTheMsel_Is200()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var application = TestData.PlayerApplication(msel.Id, "before");
        await Seed(application);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/playerApplications/{application.Id}",
            Body(msel.Id) with { id = application.Id, name = "after" },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await ReadBack(rb => rb.PlayerApplications.SingleAsync(x => x.Id == application.Id, Ct));
        Assert.Equal("after", stored.Name);
        Assert.Equal(actor.Id, stored.ModifiedBy);
    }

    [Fact]
    public async Task Update_ForAnIdThatIsNotThere_Is404()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var id = Guid.NewGuid();

        var response = await Client(actor).PutAsJsonAsync(
            $"api/playerApplications/{id}", Body(msel.Id) with { id = id }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Update decides from the request body and steals the row.</summary>
    [Fact]
    public async Task Update_DecidesFromTheRequestBodyAndStealsTheRow()
    {
        var mine = await SeedMsel();
        var actor = await Actor().OnMsel(mine, MselRole.Owner).SeedAsync();
        var theirs = await SeedMsel();
        var application = TestData.PlayerApplication(theirs.Id);
        await Seed(application);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/playerApplications/{application.Id}",
            Body(mine.Id) with { id = application.Id, name = "stolen" },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await ReadBack(rb => rb.PlayerApplications.SingleAsync(x => x.Id == application.Id, Ct));
        Assert.Equal(mine.Id, stored.MselId);
        Assert.Equal("stolen", stored.Name);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE playerApplications/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_ForAnOwnerOfTheMsel_Is204()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var application = TestData.PlayerApplication(msel.Id);
        await Seed(application);

        var response = await Client(actor).DeleteAsync($"api/playerApplications/{application.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await ReadBack(rb => rb.PlayerApplications.ToListAsync(Ct)));
    }

    /// <summary>Delete for an id that is not there is answered with a 404 with edit MSELs and 500 for an owner.</summary>
    [Fact]
    public async Task Delete_ForAnIdThatIsNotThere_Is404WithEditMselsAnd500ForAnOwner()
    {
        var msel = await SeedMsel();
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var privileged = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var id = Guid.NewGuid();

        var refused = await Client(owner).DeleteAsync($"api/playerApplications/{id}", Ct);
        Assert.Equal(HttpStatusCode.InternalServerError, refused.StatusCode);

        var answered = await Client(privileged).DeleteAsync($"api/playerApplications/{id}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, answered.StatusCode);
    }

    private async Task<MselEntity> SeedMsel()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        return msel;
    }

    /// <remarks>
    /// Drops an actor's <c>UserMselRole</c> rows, leaving the unit membership <c>TestActorBuilder</c>
    /// seeded alongside them - the state <c>MselUserRequirement</c> accepts and every other helper refuses.
    /// </remarks>
    private async Task RemoveTheRoleRows(Guid userId)
    {
        await using var context = NewContext();
        var roles = await context.UserMselRoles.Where(x => x.UserId == userId).ToListAsync(Ct);
        context.UserMselRoles.RemoveRange(roles);
        await context.SaveChangesAsync(Ct);
    }

    /// <remarks>
    /// <c>ViewModels.PlayerApplication</c> derives from <c>Base</c>, whose <c>DateCreated</c> and
    /// <c>CreatedBy</c> are non-nullable, so this record omits them rather than sending nulls.
    /// </remarks>
    private static PlayerApplicationBody Body(Guid mselId) =>
        new() { mselId = mselId, name = "seeded", url = "http://application.example/" };

    private record PlayerApplicationBody
    {
        public Guid id { get; init; }
        public Guid mselId { get; init; }
        public string name { get; init; }
        public string url { get; init; }
        public string icon { get; init; }
        public bool? embeddable { get; init; }
        public bool? loadInBackground { get; init; }
    }
}
