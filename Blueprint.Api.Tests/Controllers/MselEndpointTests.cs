// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>The MSEL read, create, update, delete and role endpoints, driven over HTTP.</summary>
public class MselEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET msels
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_WithViewMselsPermission_ReturnsEveryMsel()
    {
        var mine = TestData.Msel();
        var somebodyElses = TestData.Msel();
        await Seed(mine, somebodyElses);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var returned = await GetMsels(Client(actor), "/api/msels");

        Assert.Equal(
            [.. new[] { mine.Id, somebodyElses.Id }.Order()],
            returned.Select(x => x.Id).Order());
    }

    /// <summary>
    /// Unlike <c>my-msels</c>, this one has no fallback: it is the administrative list, and a caller
    /// without <see cref="SystemPermission.ViewMsels"/> is refused whatever roles they hold.
    /// </summary>
    [Fact]
    public async Task Get_WithoutViewMselsPermission_Is403()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await Client(actor).GetAsync("/api/msels", Ct)).StatusCode);
    }

    [Fact]
    public async Task Get_ReturnsArchivedMselsToo()
    {
        var archived = TestData.Msel(status: MselItemStatus.Archived);
        await Seed(archived);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Equal(archived.Id, Assert.Single(await GetMsels(Client(actor), "/api/msels")).Id);
    }

    [Fact]
    public async Task Get_FilteredByUserId_ReturnsOnlyThatUsersCreations()
    {
        var creator = Guid.NewGuid();
        var theirs = TestData.Msel(createdBy: creator);
        await Seed(theirs, TestData.Msel());

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var returned = await GetMsels(Client(actor), $"/api/msels?userId={creator}");

        Assert.Equal(theirs.Id, Assert.Single(returned).Id);
    }

    /// <summary>Get filtered by an unparseable user id filters on an empty guid.</summary>
    [Fact]
    public async Task Get_FilteredByAnUnparseableUserId_FiltersOnAnEmptyGuid()
    {
        var orphan = TestData.Msel();
        orphan.CreatedBy = Guid.Empty;
        await Seed(orphan, TestData.Msel());

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var returned = await GetMsels(Client(actor), "/api/msels?userId=not-a-guid");

        Assert.Equal(orphan.Id, Assert.Single(returned).Id);
    }

    [Fact]
    public async Task Get_FilteredByDescription_MatchesASubstring()
    {
        var hurricane = TestData.Msel();
        hurricane.Description = "Hurricane response, 2026";
        var wildfire = TestData.Msel();
        wildfire.Description = "Wildfire response, 2026";
        await Seed(hurricane, wildfire);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var returned = await GetMsels(Client(actor), "/api/msels?description=Hurricane");

        Assert.Equal(hurricane.Id, Assert.Single(returned).Id);
    }

    /// <summary>Get filtered by description is case sensitive.</summary>
    [Fact]
    public async Task Get_FilteredByDescription_IsCaseSensitive()
    {
        var msel = TestData.Msel();
        msel.Description = "Hurricane response";
        await Seed(msel);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Empty(await GetMsels(Client(actor), "/api/msels?description=hurricane"));
    }

    /// <summary>Get filtered by user id and description is also case sensitive.</summary>
    [Fact]
    public async Task Get_FilteredByUserIdAndDescription_IsAlsoCaseSensitive()
    {
        var creator = Guid.NewGuid();
        var msel = TestData.Msel(createdBy: creator);
        msel.Description = "Hurricane response";
        await Seed(msel);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Empty(await GetMsels(
            Client(actor), $"/api/msels?userId={creator}&description=hurricane"));
    }

    [Fact]
    public async Task Get_FilteredByTeamId_ReturnsTheTeamsMsel()
    {
        var msel = TestData.Msel();
        await Seed(msel, TestData.Msel());
        var team = TestData.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var returned = await GetMsels(Client(actor), $"/api/msels?teamId={team.Id}");

        Assert.Equal(msel.Id, Assert.Single(returned).Id);
    }

    /// <summary>Get filtered by an unknown team id is answered with a 500.</summary>
    [Fact]
    public async Task Get_FilteredByAnUnknownTeamId_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels?teamId={Guid.NewGuid()}", Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Object reference not set to an instance of an object.", failure.Title);
        Assert.Contains("MselService.GetAsync", failure.Detail);
    }

    [Fact]
    public async Task Get_FilteredByUserIdAndDescription_AppliesBoth()
    {
        var creator = Guid.NewGuid();

        var wanted = TestData.Msel(createdBy: creator);
        wanted.Description = "Hurricane response";
        var wrongDescription = TestData.Msel(createdBy: creator);
        wrongDescription.Description = "Wildfire response";
        var wrongCreator = TestData.Msel();
        wrongCreator.Description = "Hurricane response";

        await Seed(wanted, wrongDescription, wrongCreator);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var returned = await GetMsels(
            Client(actor), $"/api/msels?userId={creator}&description=Hurricane");

        Assert.Equal(wanted.Id, Assert.Single(returned).Id);
    }

    /// <summary>Enum values go out as names, which the checked-in <c>blueprint.ui</c> client depends
    /// on.</summary>
    [Fact]
    public async Task Get_SerializesTheStatusAsAName()
    {
        await Seed(TestData.Msel(status: MselItemStatus.Approved));

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var body = await Client(actor).GetStringAsync("/api/msels", Ct);

        Assert.Contains("\"status\":\"Approved\"", body);
    }

    // ---------------------------------------------------------------------------------------------
    // GET my-msels
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task MyMsels_ReturnsAnMselTheCallersUnitIsAssignedTo()
    {
        var msel = TestData.Msel();
        await Seed(msel, TestData.Msel());

        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var returned = await GetMsels(Client(actor), "/api/my-msels");

        Assert.Equal(msel.Id, Assert.Single(returned).Id);
    }

    [Fact]
    public async Task MyMsels_ReturnsAnMselTheCallerCreated()
    {
        var actorId = Guid.NewGuid();
        var msel = TestData.Msel(createdBy: actorId);
        await Seed(msel);

        var actor = await Actor().WithId(actorId).SeedAsync();

        var returned = await GetMsels(Client(actor), "/api/my-msels");

        Assert.Equal(msel.Id, Assert.Single(returned).Id);
    }

    /// <summary>
    /// Team membership is not a route into this list - only a unit assignment or having created the MSEL
    /// is - even though <c>MselViewRequirement</c> takes a team member as able to view.
    /// </summary>
    [Fact]
    public async Task MyMsels_DoesNotReturnAnMselTheCallerIsOnlyOnATeamOf()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var team = TestData.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().OnTeam(team).SeedAsync();

        Assert.Empty(await GetMsels(Client(actor), "/api/my-msels"));
    }

    [Fact]
    public async Task MyMsels_ExcludesArchivedMsels()
    {
        var archived = TestData.Msel(status: MselItemStatus.Archived);
        await Seed(archived);

        var actor = await Actor().OnMsel(archived, MselRole.Owner).SeedAsync();

        Assert.Empty(await GetMsels(Client(actor), "/api/my-msels"));
    }

    /// <summary>
    /// Somebody else's templates are included for a caller who could copy one.
    /// </summary>
    [Theory]
    [InlineData(SystemPermission.ViewMsels)]
    [InlineData(SystemPermission.CreateMsels)]
    public async Task MyMsels_WithAViewOrCreatePermission_IncludesOtherPeoplesTemplates(
        SystemPermission permission)
    {
        var template = TestData.Msel(isTemplate: true);
        await Seed(template);

        var actor = await Actor().WithSystemPermissions(permission).SeedAsync();

        var returned = await GetMsels(Client(actor), "/api/my-msels");

        Assert.Equal(template.Id, Assert.Single(returned).Id);
    }

    [Fact]
    public async Task MyMsels_WithNoPermission_DoesNotIncludeOtherPeoplesTemplates()
    {
        await Seed(TestData.Msel(isTemplate: true));

        var actor = await Actor().SeedAsync();

        Assert.Empty(await GetMsels(Client(actor), "/api/my-msels"));
    }

    /// <summary>My MSELs with no permission drops a template the callers unit is assigned to.</summary>
    [Fact]
    public async Task MyMsels_WithNoPermission_DropsATemplateTheCallersUnitIsAssignedTo()
    {
        var template = TestData.Msel(isTemplate: true);
        await Seed(template);

        var actor = await Actor().OnMsel(template, MselRole.Owner).SeedAsync();

        Assert.Empty(await GetMsels(Client(actor), "/api/my-msels"));
    }

    /// <summary>A caller who created any non-template MSEL is shown the templates their unit is assigned
    /// to.</summary>
    [Fact]
    public async Task MyMsels_WithNoPermission_KeepsTheTemplateWhenTheCallerAlsoCreatedAnMsel()
    {
        var template = TestData.Msel(isTemplate: true);
        await Seed(template);

        var actorId = Guid.NewGuid();
        var ownMsel = TestData.Msel(createdBy: actorId);
        await Seed(ownMsel);

        var actor = await Actor().WithId(actorId).OnMsel(template, MselRole.Owner).SeedAsync();

        var returned = await GetMsels(Client(actor), "/api/my-msels");

        Assert.Equal(
            [.. new[] { ownMsel.Id, template.Id }.Order()],
            returned.Select(x => x.Id).Order());
    }

    /// <summary>The creator is reported as an owner without a role row saying so.</summary>
    [Fact]
    public async Task MyMsels_ReportsTheCreatorAsAnOwnerWithoutARow()
    {
        var actorId = Guid.NewGuid();
        var msel = TestData.Msel(createdBy: actorId);
        await Seed(msel);

        var actor = await Actor().WithId(actorId).SeedAsync();

        var returned = Assert.Single(await GetMsels(Client(actor), "/api/my-msels"));
        var role = Assert.Single(returned.UserMselRoles);

        Assert.Equal(MselRole.Owner, role.Role);
        Assert.Equal(actorId, role.UserId);
        Assert.Equal(Guid.Empty, role.Id);
        Assert.False(await ReadBack(rb => rb.UserMselRoles.AnyAsync(x => x.MselId == msel.Id, Ct)));
    }

    // ---------------------------------------------------------------------------------------------
    // GET users/{userId}/msels
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task UserMsels_ForTheCallerThemselves_IsAllowedWithNoPermission()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var returned = await GetMsels(Client(actor), $"/api/users/{actor.Id}/msels");

        Assert.Equal(msel.Id, Assert.Single(returned).Id);
    }

    [Fact]
    public async Task UserMsels_ForSomebodyElse_WithoutManageUsers_Is403()
    {
        var subject = await Actor().SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await Client(actor).GetAsync($"/api/users/{subject.Id}/msels", Ct)).StatusCode);
    }

    [Fact]
    public async Task UserMsels_ForSomebodyElse_WithManageUsers_ReturnsTheirMsels()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var subject = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var returned = await GetMsels(Client(actor), $"/api/users/{subject.Id}/msels");

        Assert.Equal(msel.Id, Assert.Single(returned).Id);
    }

    /// <summary>User MSELs for somebody else answers with the callers template visibility.</summary>
    [Fact]
    public async Task UserMsels_ForSomebodyElse_AnswersWithTheCallersTemplateVisibility()
    {
        await Seed(TestData.Msel(isTemplate: true));

        var subject = await Actor().SeedAsync();
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        Assert.Single(await GetMsels(Client(actor), $"/api/users/{subject.Id}/msels"));
        Assert.Empty(await GetMsels(Client(subject), "/api/my-msels"));
    }

    [Fact]
    public async Task UserMsels_for_the_caller_with_ViewMsels_includes_the_templates()
    {
        var template = TestData.Msel(isTemplate: true);
        await Seed(template);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var returned = await GetMsels(Client(actor), $"/api/users/{actor.Id}/msels");

        Assert.Equal(template.Id, Assert.Single(returned).Id);
    }

    [Fact]
    public async Task UserMsels_for_the_caller_with_CreateMsels_includes_the_templates()
    {
        var template = TestData.Msel(isTemplate: true);
        await Seed(template);

        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var returned = await GetMsels(Client(actor), $"/api/users/{actor.Id}/msels");

        Assert.Equal(template.Id, Assert.Single(returned).Id);
    }

    [Fact]
    public async Task UserMsels_for_the_caller_holding_only_EditMsels_leaves_out_the_templates()
    {
        await Seed(TestData.Msel(isTemplate: true));

        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var returned = await GetMsels(Client(actor), $"/api/users/{actor.Id}/msels");

        Assert.Empty(returned);
    }

    [Fact]
    public async Task UserMsels_ForAUserWithNoRow_IsAnEmptyArray()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        Assert.Empty(await GetMsels(Client(actor), $"/api/users/{Guid.NewGuid()}/msels"));
    }

    // ---------------------------------------------------------------------------------------------
    // GET msels/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetById_AsAViewer_ReturnsIt()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        Assert.Equal(msel.Id, (await GetMsel(Client(actor), msel.Id)).Id);
    }

    [Fact]
    public async Task GetById_WithViewMselsPermissionAndNoRole_ReturnsIt()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Equal(msel.Id, (await GetMsel(Client(actor), msel.Id)).Id);
    }

    [Fact]
    public async Task GetById_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await Client(actor).GetAsync($"/api/msels/{msel.Id}", Ct)).StatusCode);
    }

    /// <summary>
    /// A template is readable by any authenticated caller, which is what makes the copy-from-template
    /// flow work without granting a permission first.
    /// </summary>
    [Fact]
    public async Task GetById_ForATemplate_WithNoRoleOrPermission_ReturnsIt()
    {
        var template = TestData.Msel(isTemplate: true);
        await Seed(template);

        var actor = await Actor().SeedAsync();

        Assert.Equal(template.Id, (await GetMsel(Client(actor), template.Id)).Id);
    }

    /// <summary>Get by id for an unknown MSEL with no permission is answered with a 500.</summary>
    [Fact]
    public async Task GetById_ForAnUnknownMsel_WithNoPermission_Is500()
    {
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{Guid.NewGuid()}", Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Object reference not set to an instance of an object.", failure.Title);
        Assert.Contains("MselService.GetAsync", failure.Detail);
    }

    /// <summary>Get by id for an unknown MSEL with view MSELs permission is answered with a 500.</summary>
    [Fact]
    public async Task GetById_ForAnUnknownMsel_WithViewMselsPermission_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{Guid.NewGuid()}", Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Object reference not set to an instance of an object.", failure.Title);
        Assert.Contains("MselService.GetAsync", failure.Detail);
    }

    [Fact]
    public async Task GetById_IncludesTheUnitsFromTheJoinRows()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var returned = await GetMsel(Client(actor), msel.Id);

        Assert.Equal(actor.MselRole.UnitId, Assert.Single(returned.Units).Id);
    }

    /// <summary>
    /// Only the caller's own roles come back, so one member of an MSEL cannot enumerate the others'
    /// roles from this endpoint.
    /// </summary>
    [Fact]
    public async Task GetById_IncludesOnlyTheCallersOwnRoles()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var other = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var returned = await GetMsel(Client(actor), msel.Id);

        // The MSEL's creator is added to the view model without a row, so they are here too.
        Assert.Equal(
            [.. new[] { (actor.Id, MselRole.Viewer), (msel.CreatedBy, MselRole.Owner) }
                .OrderBy(x => x.Item1)],
            returned.UserMselRoles.Select(x => (x.UserId, x.Role)).OrderBy(x => x.UserId));
        Assert.DoesNotContain(other.Id, returned.UserMselRoles.Select(x => x.UserId));
    }

    /// <summary>A Gallery MSEL's read carries the Gallery parameter and source-type names, filled by the
    /// service after mapping.</summary>
    [Fact]
    public async Task GetById_ForAGalleryMsel_IncludesTheGalleryParameterNames()
    {
        var msel = TestData.Msel();
        msel.UseGallery = true;
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var returned = await GetMsel(Client(actor), msel.Id);

        Assert.Equal(
            [.. Enum.GetNames<GalleryArticleParameter>()],
            returned.GalleryArticleParameters);
        Assert.Equal([.. Enum.GetNames<GallerySourceType>()], returned.GallerySourceTypes);
    }

    [Fact]
    public async Task GetById_ForANonGalleryMsel_LeavesTheGalleryListsEmpty()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var returned = await GetMsel(Client(actor), msel.Id);

        Assert.Empty(returned.GalleryArticleParameters);
        Assert.Empty(returned.GallerySourceTypes);
    }

    // ---------------------------------------------------------------------------------------------
    // GET msels/{id}/data
    // ---------------------------------------------------------------------------------------------

    /// <summary>Get data for any MSEL is answered with a 500 because a data table cannot be serialized.</summary>
    [Fact]
    public async Task GetData_ForAnyMsel_Is500_BecauseADataTableCannotBeSerialized()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/data", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.StartsWith("System.NotSupportedException", body);
        Assert.Contains(
            "Serialization and deserialization of 'System.Type' instances is not supported. " +
            "Path: $.Columns.DataType.",
            body);
    }

    /// <summary>Get data with no role or permission is not forbidden.</summary>
    [Fact]
    public async Task GetData_WithNoRoleOrPermission_IsNotForbidden()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/data", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.StartsWith("System.NotSupportedException: Serialization and deserialization of 'System.Type' instances is not supported.", await response.Content.ReadAsStringAsync(Ct));
    }

    /// <remarks>
    /// The one branch of this endpoint that answers correctly, and the second half of the evidence that
    /// it is ungated: an unknown MSEL is a 404 to a caller holding nothing, where a gated endpoint would
    /// refuse before looking.
    /// </remarks>
    [Fact]
    public async Task GetData_ForAnUnknownMsel_WithNoRoleOrPermission_Is404()
    {
        var actor = await Actor().SeedAsync();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await Client(actor).GetAsync($"/api/msels/{Guid.NewGuid()}/data", Ct)).StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // POST msels
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_WithCreateMselsPermission_Is201()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/msels", new Msel { Name = "Hurricane response" }, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await Read<Msel>(response);

        Assert.Equal("Hurricane response", created.Name);
        Assert.True(await ReadBack(rb => rb.Msels.AnyAsync(x => x.Id == created.Id, Ct)));
    }

    [Fact]
    public async Task Create_PutsTheNewMselsRouteInTheLocationHeader()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/msels", new Msel { Name = "Hurricane response" }, JsonOptions, Ct);

        var created = await Read<Msel>(response);

        Assert.EndsWith($"/api/msels/{created.Id}", response.Headers.Location.ToString());
    }

    [Theory]
    [InlineData(SystemPermission.ViewMsels)]
    [InlineData(SystemPermission.EditMsels)]
    [InlineData(SystemPermission.ManageMsels)]
    public async Task Create_WithoutCreateMselsPermission_Is403(SystemPermission permission)
    {
        var actor = await Actor().WithSystemPermissions(permission).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/msels", new Msel { Name = "Hurricane response" }, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await ReadBack(rb => rb.Msels.ToListAsync(Ct)));
    }

    [Fact]
    public async Task Create_WithoutAName_Is400()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/msels", new Msel(), JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The exercise window is a start time plus a duration, so an end time before the start is exactly a
    /// negative duration and the view model's <c>[Range]</c> rejects it.
    /// </summary>
    [Fact]
    public async Task Create_WithANegativeDuration_Is400()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/msels",
            new Msel { Name = "Hurricane response", DurationSeconds = -1 },
            JsonOptions,
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("must not precede the start time", await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>
    /// Audit fields are stamped by the server whatever the request body says.
    /// </summary>
    /// <remarks>
    /// <c>BlueprintContext.SaveEntries</c> sets them on every save, so the hostile values below cannot
    /// reach the database. Both the controller and <c>CreateAsync</c> also overwrite <c>CreatedBy</c>
    /// with the caller's id first, which is belt and braces for the same thing.
    /// </remarks>
    [Fact]
    public async Task Create_StampsTheAuditFieldsOnTheServer()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var before = DateTime.UtcNow;

        var response = await Client(actor).PostAsJsonAsync(
            "/api/msels",
            new Msel
            {
                Name = "Hurricane response",
                CreatedBy = Guid.NewGuid(),
                DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ModifiedBy = Guid.NewGuid(),
                DateModified = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            JsonOptions,
            Ct);

        var created = await Read<Msel>(response);

        await using var context = NewContext();
        var stored = await context.Msels.AsNoTracking().SingleAsync(x => x.Id == created.Id, Ct);

        Assert.Equal(actor.Id, stored.CreatedBy);
        AssertStampedBetween(stored.DateCreated, before, DateTime.UtcNow);
        Assert.Null(stored.ModifiedBy);
        Assert.Null(stored.DateModified);
    }

    /// <summary>Create with an id in the body uses it.</summary>
    [Fact]
    public async Task Create_WithAnIdInTheBody_UsesIt()
    {
        var id = Guid.NewGuid();
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/msels", new Msel { Id = id, Name = "Hurricane response" }, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(id, (await Read<Msel>(response)).Id);
    }

    /// <summary>A create naming an id that already exists is answered with a 500.</summary>
    [Fact]
    public async Task Create_WithAnIdThatAlreadyExists_Is500()
    {
        var existing = TestData.Msel();
        await Seed(existing);

        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/msels", new Msel { Id = existing.Id, Name = "Hurricane response" }, JsonOptions, Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("An error occurred while saving the entity changes. See the inner exception for details.", failure.Title);
        Assert.Contains("MselService.CreateAsync", failure.Detail);
    }

    /// <summary>
    /// Creating an MSEL does not write a role row for the creator, and the response says they own it
    /// anyway.
    /// </summary>
    [Fact]
    public async Task Create_ReportsTheCallerAsAnOwnerWithoutWritingARole()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/msels", new Msel { Name = "Hurricane response" }, JsonOptions, Ct);

        var created = await Read<Msel>(response);
        var role = Assert.Single(created.UserMselRoles);

        Assert.Equal(actor.Id, role.UserId);
        Assert.Equal(MselRole.Owner, role.Role);
        Assert.Empty(await ReadBack(rb => rb.UserMselRoles.ToListAsync(Ct)));
    }

    [Fact]
    public async Task Create_NotifiesTheMselGroupAndTheAdminGroup()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/msels", new Msel { Name = "Hurricane response" }, JsonOptions, Ct);

        var created = await Read<Msel>(response);

        Assert.Equal(
            [created.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.MselCreated, created.Id));
    }

    // ---------------------------------------------------------------------------------------------
    // PUT msels/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_AsTheOwner_Is200()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var returned = await Update(Client(actor), msel.Id, Body(msel, name: "Renamed"));

        Assert.Equal("Renamed", returned.Name);
        Assert.Equal(
            "Renamed",
            (await ReadBack(rb => rb.Msels.SingleAsync(x => x.Id == msel.Id, Ct))).Name);
    }

    [Fact]
    public async Task Update_AsTheCreator_Is200()
    {
        var actorId = Guid.NewGuid();
        var msel = TestData.Msel(createdBy: actorId);
        await Seed(msel);

        var actor = await Actor().WithId(actorId).SeedAsync();

        Assert.Equal("Renamed", (await Update(Client(actor), msel.Id, Body(msel, "Renamed"))).Name);
    }

    [Fact]
    public async Task Update_WithEditMselsPermissionAndNoRole_Is200()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        Assert.Equal("Renamed", (await Update(Client(actor), msel.Id, Body(msel, "Renamed"))).Name);
    }

    /// <summary>Update as anything but an owner is answered with a 403.</summary>
    [Theory]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.MoveEditor)]
    [InlineData(MselRole.Viewer)]
    [InlineData(MselRole.Evaluator)]
    public async Task Update_AsAnythingButAnOwner_Is403(MselRole role)
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Client(actor).PutAsJsonAsync(
            $"/api/msels/{msel.Id}", Body(msel, "Renamed"), JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Update_WithABlankName_Is400AndLeavesTheNameAlone(string name)
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var body = Body(msel);
        body.Name = name;
        var response = await Client(actor).PutAsJsonAsync(
            $"/api/msels/{msel.Id}", body, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            msel.Name,
            (await ReadBack(rb => rb.Msels.SingleAsync(x => x.Id == msel.Id, Ct))).Name);
    }

    [Fact]
    public async Task Update_ForAnUnknownMsel_WithEditMselsPermission_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var id = Guid.NewGuid();
        var response = await Client(actor).PutAsJsonAsync(
            $"/api/msels/{id}", new Msel { Id = id, Name = "Renamed" }, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Update for an unknown MSEL with no permission is answered with a 500.</summary>
    [Fact]
    public async Task Update_ForAnUnknownMsel_WithNoPermission_Is500()
    {
        var actor = await Actor().SeedAsync();

        var id = Guid.NewGuid();
        var response = await Client(actor).PutAsJsonAsync(
            $"/api/msels/{id}", new Msel { Id = id, Name = "Renamed" }, JsonOptions, Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Object reference not set to an instance of an object.", failure.Title);
        Assert.Contains("MselOwnerRequirement.IsMet", failure.Detail);
    }

    /// <summary>Update with no id in the body is answered with a 500.</summary>
    [Fact]
    public async Task Update_WithNoIdInTheBody_Is500()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).PutAsJsonAsync(
            $"/api/msels/{msel.Id}", new Msel { Name = "Renamed" }, JsonOptions, Ct);

        Assert.Equal("The property 'MselEntity.Id' is part of a key and so cannot be modified or marked as modified. To change the principal of an existing entity with an identifying foreign key, first delete the dependent and invoke 'SaveChanges', and then associate the dependent with the new principal.", (await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
        Assert.Equal(
            msel.Name,
            (await ReadBack(rb => rb.Msels.SingleAsync(x => x.Id == msel.Id, Ct))).Name);
    }

    /// <summary>Update with another MSELs id in the body is answered with a 500.</summary>
    [Fact]
    public async Task Update_WithAnotherMselsIdInTheBody_Is500()
    {
        var msel = TestData.Msel();
        var other = TestData.Msel();
        await Seed(msel, other);

        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).PutAsJsonAsync(
            $"/api/msels/{msel.Id}", Body(other, "Renamed"), JsonOptions, Ct);

        Assert.Equal("The property 'MselEntity.Id' is part of a key and so cannot be modified or marked as modified. To change the principal of an existing entity with an identifying foreign key, first delete the dependent and invoke 'SaveChanges', and then associate the dependent with the new principal.", (await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
    }

    /// <summary>A PUT replaces the whole MSEL: a field the body leaves at its default is written as the
    /// default.</summary>
    [Fact]
    public async Task Update_WithAPartialBody_ResetsTheFieldsItOmits()
    {
        var msel = TestData.Msel(status: MselItemStatus.Approved);
        msel.UseGallery = true;
        await Seed(msel);

        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var returned = await Update(
            Client(actor), msel.Id, new Msel { Id = msel.Id, Name = msel.Name });

        Assert.Equal(MselItemStatus.Pending, returned.Status);
        Assert.False(returned.UseGallery);
    }

    [Fact]
    public async Task Update_StampsTheAuditFieldsAndPreservesCreation()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var createdBy = msel.CreatedBy;
        var dateCreated = (await ReadBack(rb => rb.Msels.SingleAsync(x => x.Id == msel.Id, Ct))).DateCreated;

        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var before = DateTime.UtcNow;

        var body = Body(msel, "Renamed");
        body.CreatedBy = Guid.NewGuid();
        body.DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        body.ModifiedBy = Guid.NewGuid();
        body.DateModified = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await Update(Client(actor), msel.Id, body);

        await using var context = NewContext();
        var stored = await context.Msels.AsNoTracking().SingleAsync(x => x.Id == msel.Id, Ct);

        Assert.Equal(createdBy, stored.CreatedBy);
        Assert.Equal(dateCreated, stored.DateCreated);
        Assert.Equal(actor.Id, stored.ModifiedBy);
        AssertStampedBetween(stored.DateModified, before, DateTime.UtcNow);
    }

    [Fact]
    public async Task Update_NotifiesTheMselGroupWithTheModifiedPropertyNames()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        await Update(Client(actor), msel.Id, Body(msel, "Renamed"));

        Assert.Contains(msel.Id.ToString(), Hub.Recipients(MainHubMethods.MselUpdated, msel.Id));
        var send = Hub.Of(MainHubMethods.MselUpdated, msel.Id).First();

        Assert.Contains("name", (string[])send.Arguments[1]);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT msels/{mselId}/user/{userId}/role/{mselRole}/add and .../remove
    //
    // The controller passes the two ids to the service in the opposite order to its parameters.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Add user role for a real user and MSEL is answered with a 404.</summary>
    [Fact]
    public async Task AddUserRole_ForARealUserAndMsel_Is404()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var subject = await Actor().SeedAsync();
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Client(actor).PutAsync(
            $"/api/msels/{msel.Id}/user/{subject.Id}/role/Owner/add", null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await ReadBack(rb => rb.UserMselRoles.ToListAsync(Ct)));
    }

    /// <summary>Add user role with the ids swapped in the route writes the role.</summary>
    [Fact]
    public async Task AddUserRole_WithTheIdsSwappedInTheRoute_WritesTheRole()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var subject = await Actor().SeedAsync();
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Client(actor).PutAsync(
            $"/api/msels/{subject.Id}/user/{msel.Id}/role/Owner/add", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(msel.Id, (await Read<Msel>(response)).Id);

        var role = Assert.Single(await ReadBack(rb => rb.UserMselRoles.ToListAsync(Ct)));

        Assert.Equal(subject.Id, role.UserId);
        Assert.Equal(msel.Id, role.MselId);
        Assert.Equal(MselRole.Owner, role.Role);
        Assert.Equal(actor.Id, role.CreatedBy);
    }

    /// <summary>Add user role for a role that already exists is answered with a 500.</summary>
    [Fact]
    public async Task AddUserRole_ForARoleThatAlreadyExists_Is500()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var subject = await Actor().SeedAsync();
        await Seed(TestData.UserMselRole(subject.Id, msel.Id, MselRole.Owner));
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).PutAsync($"/api/msels/{subject.Id}/user/{msel.Id}/role/Owner/add", null, Ct);

        Assert.Equal("User/MSEL/Role already exists.", (await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
    }

    /// <summary>Granting a role needs ownership of the MSEL; an editor is refused.</summary>
    [Fact]
    public async Task AddUserRole_WithoutEditMselsPermissionOrOwnership_Is403()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var response = await Client(actor).PutAsync(
            $"/api/msels/{actor.Id}/user/{msel.Id}/role/Owner/add", null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(await ReadBack(rb => rb.UserMselRoles.ToListAsync(Ct)));
    }

    /// <summary>Remove user role for a role the user really holds is answered with a 404 and keeps it.</summary>
    [Fact]
    public async Task RemoveUserRole_ForARoleTheUserReallyHolds_Is404AndKeepsIt()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var subject = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Client(actor).PutAsync(
            $"/api/msels/{msel.Id}/user/{subject.Id}/role/Owner/remove", null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(await ReadBack(rb => rb.UserMselRoles.AnyAsync(
            x => x.UserId == subject.Id && x.MselId == msel.Id, Ct)));
    }

    [Fact]
    public async Task RemoveUserRole_WithTheIdsSwappedInTheRoute_RemovesTheRole()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var subject = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Client(actor).PutAsync(
            $"/api/msels/{subject.Id}/user/{msel.Id}/role/Owner/remove", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await ReadBack(rb => rb.UserMselRoles.ToListAsync(Ct)));
    }

    /// <summary>Add user role with <c>EditMsels</c> and no role on the MSEL writes the role.</summary>
    // Same case as AddUserRole_ForARealUserAndMsel_Is404.
    [Fact]
    public async Task AddUserRole_with_EditMsels_and_no_role_writes_the_role()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var subject = await Actor().SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).PutAsync(
            $"/api/msels/{subject.Id}/user/{msel.Id}/role/Editor/add", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var role = Assert.Single(await ReadBack(rb => rb.UserMselRoles.ToListAsync(Ct)));
        Assert.Equal((subject.Id, msel.Id, MselRole.Editor), (role.UserId, role.MselId, role.Role));
    }

    /// <summary>Remove user role with <c>EditMsels</c> and no role on the MSEL removes the role.</summary>
    // Same case as AddUserRole_ForARealUserAndMsel_Is404.
    [Fact]
    public async Task RemoveUserRole_with_EditMsels_and_no_role_removes_the_role()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var subject = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).PutAsync(
            $"/api/msels/{subject.Id}/user/{msel.Id}/role/Editor/remove", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await ReadBack(rb => rb.UserMselRoles.ToListAsync(Ct)));
    }

    /// <summary>Removing a role needs ownership of the MSEL; an editor is refused.</summary>
    // Same case as AddUserRole_ForARealUserAndMsel_Is404.
    [Fact]
    public async Task RemoveUserRole_is_forbidden_for_an_editor_of_the_msel()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var response = await Client(actor).PutAsync(
            $"/api/msels/{actor.Id}/user/{msel.Id}/role/Editor/remove", null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(await ReadBack(rb => rb.UserMselRoles.ToListAsync(Ct)));
    }

    /// <summary>Removing a role is refused to the owner of another MSEL.</summary>
    // Same case as AddUserRole_ForARealUserAndMsel_Is404.
    [Fact]
    public async Task RemoveUserRole_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var subject = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        var response = await Client(actor).PutAsync(
            $"/api/msels/{subject.Id}/user/{msel.Id}/role/Editor/remove", null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await ReadBack(rb => rb.UserMselRoles.AnyAsync(x => x.UserId == subject.Id && x.MselId == msel.Id, Ct)));
    }

    // Same case as AddUserRole_ForARealUserAndMsel_Is404.
    [Fact]
    public async Task AddUserRole_for_an_owner_of_the_msel_writes_the_role()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var subject = await Actor().SeedAsync();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).PutAsync(
            $"/api/msels/{subject.Id}/user/{msel.Id}/role/Editor/add", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await ReadBack(rb => rb.UserMselRoles.AnyAsync(
            x => x.UserId == subject.Id && x.MselId == msel.Id && x.Role == MselRole.Editor, Ct)));
    }

    // Same case as AddUserRole_ForARealUserAndMsel_Is404.
    [Fact]
    public async Task RemoveUserRole_for_an_owner_of_the_msel_removes_the_role()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var subject = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).PutAsync(
            $"/api/msels/{subject.Id}/user/{msel.Id}/role/Editor/remove", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(await ReadBack(rb => rb.UserMselRoles.AnyAsync(
            x => x.UserId == subject.Id && x.MselId == msel.Id, Ct)));
    }

    /// <summary>An unknown role name in the path is a 400 from binding.</summary>
    [Fact]
    public async Task AddUserRole_WithARoleThatIsNotAnMselRole_Is400()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Client(actor).PutAsync(
            $"/api/msels/{msel.Id}/user/{Guid.NewGuid()}/role/Sovereign/add", null, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE msels/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_AsTheOwner_Is204AndRemovesIt()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/msels/{msel.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await ReadBack(rb => rb.Msels.ToListAsync(Ct)));
    }

    [Fact]
    public async Task Delete_WithEditMselsPermissionAndNoRole_Is204()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await Client(actor).DeleteAsync($"/api/msels/{msel.Id}", Ct)).StatusCode);
    }

    /// <remarks>
    /// The same owner-only fallback as <see cref="Update_AsAnythingButAnOwner_Is403"/>, and worth its own
    /// test because deleting an MSEL destroys an exercise: an editor cannot do it, and anybody holding
    /// <see cref="SystemPermission.EditMsels"/> can do it to every MSEL in the installation.
    /// </remarks>
    [Fact]
    public async Task Delete_AsAnEditor_Is403()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await Client(actor).DeleteAsync($"/api/msels/{msel.Id}", Ct)).StatusCode);
        Assert.Single(await ReadBack(rb => rb.Msels.ToListAsync(Ct)));
    }

    [Fact]
    public async Task Delete_ForAnUnknownMsel_WithEditMselsPermission_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await Client(actor).DeleteAsync($"/api/msels/{Guid.NewGuid()}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Delete_CascadesToTheMselsTeamsAndUnitAssignments()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(TestData.Team(msel.Id));

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Client(actor).DeleteAsync($"/api/msels/{msel.Id}", Ct);

        await using var context = NewContext();

        Assert.Empty(await context.Teams.ToListAsync(Ct));
        Assert.Empty(await context.MselUnits.ToListAsync(Ct));
        Assert.Empty(await context.UserMselRoles.ToListAsync(Ct));
        // The unit itself is not owned by the MSEL, so it stays.
        Assert.Single(await context.Units.ToListAsync(Ct));
    }

    [Fact]
    public async Task Delete_NotifiesTheMselGroupAndTheAdminGroupWithTheId()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Client(actor).DeleteAsync($"/api/msels/{msel.Id}", Ct);

        Assert.Equal(
            [msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.MselDeleted, msel.Id));
        Assert.Equal(msel.Id, Hub.Of(MainHubMethods.MselDeleted, msel.Id).First().Payload);
    }

    // ---------------------------------------------------------------------------------------------
    // Authentication
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Every route on this controller is behind the MVC-wide authorization filter, including the four
    /// that ask <c>IBlueprintAuthorizationService</c> nothing.
    /// </summary>
    [Theory]
    [InlineData("GET", "msels")]
    [InlineData("GET", "my-msels")]
    [InlineData("GET", "my-join-msels")]
    [InlineData("GET", "my-launch-msels")]
    [InlineData("GET", "users/00000000-0000-0000-0000-000000000001/msels")]
    [InlineData("GET", "msels/00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "msels/00000000-0000-0000-0000-000000000001/data")]
    [InlineData("GET", "msels/00000000-0000-0000-0000-000000000001/xlsx")]
    [InlineData("GET", "msels/00000000-0000-0000-0000-000000000001/json")]
    [InlineData("POST", "msels")]
    [InlineData("POST", "msels/00000000-0000-0000-0000-000000000001/copy")]
    [InlineData("PUT", "msels/00000000-0000-0000-0000-000000000001")]
    [InlineData("PUT", "msels/00000000-0000-0000-0000-000000000001/user/" +
        "00000000-0000-0000-0000-000000000002/role/Owner/add")]
    [InlineData("PUT", "msels/00000000-0000-0000-0000-000000000001/user/" +
        "00000000-0000-0000-0000-000000000002/role/Owner/remove")]
    [InlineData("DELETE", "msels/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "msels/00000000-0000-0000-0000-000000000001/archive")]
    public async Task EveryRoute_Unauthenticated_Is401(string method, string route)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/{route}");

        var response = await Client().SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A full update body for <paramref name="entity"/>, which is what the endpoint wants: the mapper
    /// writes every mapped member, so a field the body omits is written as its default.
    /// </summary>
    private static Msel Body(MselEntity entity, string name = null) => new()
    {
        Id = entity.Id,
        Name = name ?? entity.Name,
        Description = entity.Description,
        Status = entity.Status,
        IsTemplate = entity.IsTemplate,
        StartTime = entity.StartTime,
        DurationSeconds = entity.DurationSeconds
    };

    private async Task<List<Msel>> GetMsels(HttpClient client, string route)
    {
        var response = await client.GetAsync(route, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<List<Msel>>(response);
    }

    private async Task<Msel> GetMsel(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/msels/{id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<Msel>(response);
    }

    private async Task<Msel> Update(HttpClient client, Guid id, Msel body)
    {
        var response = await client.PutAsJsonAsync($"/api/msels/{id}", body, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<Msel>(response);
    }

    /// <summary>
    /// Asserts a server-stamped audit timestamp: present, and inside the window the test bracketed.
    /// </summary>
    private static void AssertStampedBetween(DateTime? actual, DateTime notBefore, DateTime notAfter)
    {
        Assert.NotNull(actual);
        Assert.InRange(actual.Value, notBefore, notAfter);
    }

    private async Task<T> Read<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
}
