// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// <c>PlayerApplicationService</c> / <c>PlayerApplicationController</c> - the CRUD routes over the
/// applications a MSEL puts on its teams' Player views, and over the system-wide templates a MSEL's
/// application is copied from. Reads want <c>ViewMsels</c> or membership of the MSEL; writes want
/// <c>EditMsels</c> or the <c>Owner</c> role. A template - a row naming no MSEL - wants
/// <c>ManagePlayerApplications</c> instead, and is read by anyone signed in.
/// </summary>
/// <remarks>
/// 401 and 403 for these routes live in <see cref="RouteAuthorizationTests"/>; this file covers the happy
/// path and the not-found path per route, per the thin protocol, except <c>GET
/// playerApplications/templates</c>, which resolves no permission at all and so cannot be refused.
/// <c>POST playerApplications/push</c> is covered by <see cref="PlayerServiceTests"/>, except its refusal of
/// a template, which is here.
/// <para />
/// The controller throws no <c>ForbiddenException</c> of its own on any of the six actions: it resolves a
/// permission into a <c>bool</c> and lets the service's requirement helpers decide, a different helper
/// per route.
/// </remarks>
public class PlayerApplicationEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET playerApplications/templates
    // ---------------------------------------------------------------------------------------------

    /// <summary>Templates with no permissions returns only the templates.</summary>
    [Fact]
    public async Task Templates_WithNoPermissions_ReturnsOnlyTheTemplates()
    {
        var actor = await Actor().SeedAsync();
        var msel = await SeedMsel();
        var template = TestData.PlayerApplicationTemplate("Virtual Machines");
        await Seed(template, TestData.PlayerApplication(msel.Id, "a msel's"));

        var response = await Client(actor).GetAsync("api/playerApplications/templates", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content
            .ReadFromJsonAsync<List<ViewModels.PlayerApplication>>(JsonOptions, Ct);
        var only = Assert.Single(list);
        Assert.Equal(template.Id, only.Id);
        Assert.Null(only.MselId);
        Assert.True(only.IsTemplate);
        Assert.Equal("Virtual Machines", only.Name);
    }

    /// <summary>
    /// A template belongs to no MSEL, so a MSEL's row flagged as a template - which a create can no longer
    /// write - stays out of the list any signed-in user may read.
    /// </summary>
    [Fact]
    public async Task Templates_LeavesOutAMselsApplicationFlaggedAsATemplate()
    {
        var actor = await Actor().SeedAsync();
        var msel = await SeedMsel();
        var row = TestData.PlayerApplication(msel.Id);
        row.IsTemplate = true;
        await Seed(row);

        var response = await Client(actor).GetAsync("api/playerApplications/templates", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadFromJsonAsync<List<ViewModels.PlayerApplication>>(JsonOptions, Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // GET msels/{mselId}/playerApplications
    // ---------------------------------------------------------------------------------------------

    /// <summary>A MSEL's list never carries the templates, whoever asks.</summary>
    [Fact]
    public async Task GetByMsel_LeavesOutTheTemplates()
    {
        var msel = await SeedMsel();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();
        var mine = TestData.PlayerApplication(msel.Id, "mine");
        await Seed(mine, TestData.PlayerApplicationTemplate());

        var response = await Client(actor).GetAsync($"api/msels/{msel.Id}/playerApplications", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content
            .ReadFromJsonAsync<List<ViewModels.PlayerApplication>>(JsonOptions, Ct);
        Assert.Equal(mine.Id, Assert.Single(list).Id);
    }

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

    /// <summary>Get by MSEL for a MSEL that is not there is answered with a 500 for a caller without ViewMsels.</summary>
    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_Is500()
    {
        var stranger = await Actor().SeedAsync();

        var response = await Client(stranger).GetAsync($"api/msels/{Guid.NewGuid()}/playerApplications", Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Object reference not set to an instance of an object.", failure.Title);
        Assert.Contains("PlayerApplicationService.GetByMselAsync", failure.Detail);
    }

    // Same case as GetByMsel_ForAMselThatIsNotThere_Is500.
    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_WithViewMsels_IsAnEmptyList()
    {
        var privileged = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(privileged).GetAsync($"api/msels/{Guid.NewGuid()}/playerApplications", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadFromJsonAsync<List<ViewModels.PlayerApplication>>(JsonOptions, Ct));
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

    [Fact]
    public async Task Get_is_forbidden_for_a_caller_holding_Viewer_only_in_another_msel()
    {
        var msel = await SeedMsel();
        var application = TestData.PlayerApplication(msel.Id);
        await Seed(application);
        var actor = await Actor().OnNewMsel(MselRole.Viewer).SeedAsync();

        var response = await Client(actor).GetAsync($"api/playerApplications/{application.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
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

    /// <summary>A template names no MSEL, so there is no membership to ask for: anyone signed in reads it.</summary>
    [Fact]
    public async Task Get_ATemplate_WithNoPermissions_Is200()
    {
        var actor = await Actor().SeedAsync();
        var template = TestData.PlayerApplicationTemplate("Map");
        await Seed(template);

        var response = await Client(actor).GetAsync($"api/playerApplications/{template.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answered = await response.Content
            .ReadFromJsonAsync<ViewModels.PlayerApplication>(JsonOptions, Ct);
        Assert.Equal("Map", answered.Name);
        Assert.True(answered.IsTemplate);
    }

    /// <summary>Get for an id that is not there is answered with a 500.</summary>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"api/playerApplications/{Guid.NewGuid()}", Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Sequence contains no elements.", failure.Title);
        Assert.Contains("PlayerApplicationService.GetAsync", failure.Detail);
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

    /// <summary>A body naming no MSEL is a template, and only <c>ManagePlayerApplications</c> is asked
    /// for.</summary>
    [Fact]
    public async Task Create_ATemplate_WithManagePlayerApplications_Is201()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManagePlayerApplications).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "api/playerApplications", Body(null) with { name = "Gallery", isTemplate = true }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content
            .ReadFromJsonAsync<ViewModels.PlayerApplication>(JsonOptions, Ct);
        Assert.Null(created.MselId);
        Assert.True(created.IsTemplate);
        var stored = await ReadBack(rb => rb.PlayerApplications.SingleAsync(x => x.Id == created.Id, Ct));
        Assert.Equal("Gallery", stored.Name);
        Assert.True(stored.IsTemplate);
    }

    /// <summary>
    /// An application belongs to a MSEL or is a template; a body naming no MSEL and not asking for a
    /// template is stored as a template, rather than as a row neither list would show.
    /// </summary>
    [Fact]
    public async Task Create_WithNoMselAndIsTemplateFalse_IsStoredAsATemplate()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManagePlayerApplications).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "api/playerApplications", Body(null) with { isTemplate = false }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content
            .ReadFromJsonAsync<ViewModels.PlayerApplication>(JsonOptions, Ct);
        Assert.True(created.IsTemplate);
        Assert.True((await ReadBack(rb => rb.PlayerApplications.SingleAsync(x => x.Id == created.Id, Ct))).IsTemplate);
    }

    /// <summary>The MSEL permission does not reach the templates.</summary>
    [Fact]
    public async Task Create_ATemplate_WithOnlyEditMsels_Is403AndCreatesNothing()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "api/playerApplications", Body(null) with { isTemplate = true }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await ReadBack(db => db.PlayerApplications.AnyAsync(Ct)));
    }

    /// <summary>
    /// Adding from a template is a create of a MSEL's application carrying the template's values. The copy
    /// gets its own id and the MSEL, is no template, and the template itself is left as it was.
    /// </summary>
    [Fact]
    public async Task Create_FromATemplate_ForAnOwner_CopiesTheValuesIntoANewApplicationOfTheMsel()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var template = TestData.PlayerApplicationTemplate("Virtual Machines", "http://vm.example/views/{viewId}?{theme}", "mdi-monitor");
        template.Embeddable = false;
        template.LoadInBackground = true;
        await Seed(template);

        var response = await Client(actor).PostAsJsonAsync("api/playerApplications", new PlayerApplicationBody
        {
            mselId = msel.Id,
            name = template.Name,
            url = template.Url,
            icon = template.Icon,
            embeddable = template.Embeddable,
            loadInBackground = template.LoadInBackground
        }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var copy = await response.Content.ReadFromJsonAsync<ViewModels.PlayerApplication>(JsonOptions, Ct);
        Assert.NotEqual(template.Id, copy.Id);
        Assert.Equal(msel.Id, copy.MselId);
        Assert.False(copy.IsTemplate);
        Assert.Equal("Virtual Machines", copy.Name);
        Assert.Equal("http://vm.example/views/{viewId}?{theme}", copy.Url);
        Assert.Equal("mdi-monitor", copy.Icon);
        Assert.False(copy.Embeddable);
        Assert.True(copy.LoadInBackground);

        var stored = await ReadBack(db => db.PlayerApplications.SingleAsync(x => x.Id == template.Id, Ct));
        Assert.Null(stored.MselId);
        Assert.True(stored.IsTemplate);
        var listed = await Client(actor).GetFromJsonAsync<List<ViewModels.PlayerApplication>>(
            $"api/msels/{msel.Id}/playerApplications", JsonOptions, Ct);
        Assert.Equal(copy.Id, Assert.Single(listed).Id);
    }

    /// <summary>Create for a MSEL that is not there is answered with a 500.</summary>
    [Fact]
    public async Task Create_ForAMselThatIsNotThere_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "api/playerApplications", Body(Guid.NewGuid()), Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("An error occurred while saving the entity changes. See the inner exception for details.", failure.Title);
        Assert.Contains("PlayerApplicationService.CreateAsync", failure.Detail);
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
    public async Task Update_OfATemplate_WithManagePlayerApplications_StoresTheChange()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManagePlayerApplications).SeedAsync();
        var template = TestData.PlayerApplicationTemplate("before");
        await Seed(template);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/playerApplications/{template.Id}", Body(null) with { id = template.Id, name = "after" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await ReadBack(rb => rb.PlayerApplications.SingleAsync(x => x.Id == template.Id, Ct));
        Assert.Equal("after", stored.Name);
        Assert.True(stored.IsTemplate);
    }

    [Fact]
    public async Task Update_OfATemplate_WithOnlyEditMsels_Is403AndLeavesTheRowAlone()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var template = TestData.PlayerApplicationTemplate("before");
        await Seed(template);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/playerApplications/{template.Id}", Body(null) with { id = template.Id, name = "changed" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("before", (await ReadBack(rb => rb.PlayerApplications.SingleAsync(x => x.Id == template.Id, Ct))).Name);
    }

    /// <summary>
    /// The edit body names neither the MSEL nor the template flag, so a template stays a template whatever
    /// the body says - even an owner of the MSEL it names cannot pull the template into that MSEL.
    /// </summary>
    [Fact]
    public async Task Update_OfATemplate_NamingAMselInTheBody_KeepsItATemplate()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner)
            .WithSystemPermissions(SystemPermission.ManagePlayerApplications).SeedAsync();
        var template = TestData.PlayerApplicationTemplate();
        await Seed(template);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/playerApplications/{template.Id}",
            Body(msel.Id) with { id = template.Id, name = "renamed", isTemplate = false },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await ReadBack(rb => rb.PlayerApplications.SingleAsync(x => x.Id == template.Id, Ct));
        Assert.Null(stored.MselId);
        Assert.True(stored.IsTemplate);
        Assert.Equal("renamed", stored.Name);
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

    /// <summary>
    /// The decision is made on the stored MSEL, so owning the MSEL named in the body buys nothing.
    /// </summary>
    [Fact]
    public async Task Update_AsTheOwnerOfAnotherMsel_NamingItInTheBody_Is403AndLeavesTheRowAlone()
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

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var stored = await ReadBack(db => db.PlayerApplications.SingleAsync(x => x.Id == application.Id, Ct));
        Assert.Equal(theirs.Id, stored.MselId);
        Assert.Equal(application.Name, stored.Name);
    }

    /// <summary>
    /// An owner's edit that names another MSEL changes the row and leaves it in its MSEL.
    /// </summary>
    [Fact]
    public async Task Update_AsAnOwner_NamingAnotherMselInTheBody_KeepsTheRowInItsMsel()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var other = await SeedMsel();
        var application = TestData.PlayerApplication(msel.Id);
        await Seed(application);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/playerApplications/{application.Id}",
            Body(other.Id) with { id = application.Id, name = "renamed" },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await ReadBack(db => db.PlayerApplications.SingleAsync(x => x.Id == application.Id, Ct));
        Assert.Equal(msel.Id, stored.MselId);
        Assert.Equal("renamed", stored.Name);
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

    /// <remarks>
    /// <c>DeleteAsync</c> checks existence <em>before</em> the permission - it has to, to know whether the
    /// row is a MSEL's or a template - so an unknown id is a clean 404 for a MSEL owner as well.
    /// </remarks>
    [Fact]
    public async Task Delete_ForAnIdThatIsNotThere_Is404ForAnOwner()
    {
        var msel = await SeedMsel();
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(owner).DeleteAsync($"api/playerApplications/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_OfATemplate_WithManagePlayerApplications_RemovesIt()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManagePlayerApplications).SeedAsync();
        var template = TestData.PlayerApplicationTemplate();
        await Seed(template);

        var response = await Client(actor).DeleteAsync($"api/playerApplications/{template.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await ReadBack(rb => rb.PlayerApplications.ToListAsync(Ct)));
    }

    [Fact]
    public async Task Delete_OfATemplate_WithOnlyEditMsels_Is403AndKeepsIt()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var template = TestData.PlayerApplicationTemplate();
        await Seed(template);

        var response = await Client(actor).DeleteAsync($"api/playerApplications/{template.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(await ReadBack(rb => rb.PlayerApplications.ToListAsync(Ct)));
    }

    // ---------------------------------------------------------------------------------------------
    // POST playerApplications/push
    // ---------------------------------------------------------------------------------------------

    /// <summary>A template has no MSEL and so no Player view: the push refuses it before anything is
    /// written or queued.</summary>
    [Fact]
    public async Task Push_OfATemplate_Is409AndCreatesNothing()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.EditMsels, SystemPermission.ManagePlayerApplications).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "api/playerApplications/push", Body(null) with { isTemplate = true }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(await ReadBack(db => db.PlayerApplications.AnyAsync(Ct)));
    }

    // ---------------------------------------------------------------------------------------------
    // POST playerApplications/json/download, POST playerApplications/json
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Only templates are exported. A MSEL's application named in the request is left out, so the template
    /// permission does not read a MSEL's rows.
    /// </summary>
    [Fact]
    public async Task DownloadJson_WithManagePlayerApplications_IsAFileOfTheRequestedTemplatesOnly()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManagePlayerApplications).SeedAsync();
        var msel = await SeedMsel();
        var wanted = TestData.PlayerApplicationTemplate("wanted");
        var unwanted = TestData.PlayerApplicationTemplate("unwanted");
        var mselRow = TestData.PlayerApplication(msel.Id, "from the msel");
        await Seed(wanted, unwanted, mselRow);

        var response = await Client(actor).PostAsJsonAsync(
            "api/playerApplications/json/download", new[] { wanted.Id, mselRow.Id }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType.MediaType);
        Assert.Equal("player-application-templates.json", response.Content.Headers.ContentDisposition.FileNameStar);
        var file = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("wanted", file);
        Assert.DoesNotContain("unwanted", file);
        Assert.DoesNotContain("from the msel", file);
    }

    /// <remarks>
    /// The round trip is the test, as for CITE actions: a downloaded file uploads. The upload forces a
    /// fresh id, <c>IsTemplate</c> and a null MSEL, whatever the file says.
    /// </remarks>
    [Fact]
    public async Task UploadJson_TakesADownloadedFileAndMakesTemplatesOfIt()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManagePlayerApplications).SeedAsync();
        var template = TestData.PlayerApplicationTemplate("exported", "http://exported.example/{viewId}", "mdi-map");
        await Seed(template);
        var download = await Client(actor).PostAsJsonAsync(
            "api/playerApplications/json/download", new[] { template.Id }, Ct);
        var file = await download.Content.ReadAsStringAsync(Ct);

        var response = await UploadJson(Client(actor), file);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = Assert.Single(
            await response.Content.ReadFromJsonAsync<List<ViewModels.PlayerApplication>>(JsonOptions, Ct));
        Assert.NotEqual(template.Id, created.Id);
        Assert.Null(created.MselId);
        Assert.True(created.IsTemplate);
        Assert.Equal("exported", created.Name);
        Assert.Equal("http://exported.example/{viewId}", created.Url);
        Assert.Equal("mdi-map", created.Icon);
        Assert.Equal(2, await ReadBack(db => db.PlayerApplications.CountAsync(x => x.IsTemplate && x.MselId == null, Ct)));
    }

    /// <summary>A file naming a MSEL still lands in the templates, not in that MSEL.</summary>
    [Fact]
    public async Task UploadJson_OfAnApplicationNamingAMsel_MakesATemplateOfIt()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManagePlayerApplications).SeedAsync();
        var msel = await SeedMsel();
        var file = $$"""[{"Id":"{{Guid.NewGuid()}}","MselId":"{{msel.Id}}","IsTemplate":false,"Name":"smuggled","Url":"http://x.example/"}]""";

        var response = await UploadJson(Client(actor), file);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await ReadBack(db => db.PlayerApplications.SingleAsync(Ct));
        Assert.Null(stored.MselId);
        Assert.True(stored.IsTemplate);
    }

    [Fact]
    public async Task Delete_ForAnIdThatIsNotThere_WithEditMsels_Is404()
    {
        var privileged = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(privileged).DeleteAsync($"api/playerApplications/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
    private static PlayerApplicationBody Body(Guid? mselId) =>
        new() { mselId = mselId, name = "seeded", url = "http://application.example/" };

    private record PlayerApplicationBody
    {
        public Guid id { get; init; }
        public Guid? mselId { get; init; }
        public bool isTemplate { get; init; }
        public string name { get; init; }
        public string url { get; init; }
        public string icon { get; init; }
        public bool? embeddable { get; init; }
        public bool? loadInBackground { get; init; }
    }

    /// <remarks>
    /// The <c>await</c> before the <c>using</c> falls out of scope is load-bearing; see
    /// <c>OrganizationEndpointTests.UploadJson</c>.
    /// </remarks>
    private async Task<HttpResponseMessage> UploadJson(HttpClient client, string json)
    {
        using var content = new MultipartFormDataContent();

        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "ToUpload", "player-applications.json");

        return await client.PostAsync("api/playerApplications/json", content, Ct);
    }
}
