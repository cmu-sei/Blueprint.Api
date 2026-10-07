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
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary><c>UserMselRoleService</c> / <c>UserMselRoleController</c> - the five routes over the row that
/// says what a person may do on one MSEL.</summary>
public class UserMselRoleEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET msels/{mselId}/usermselroles
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByMsel_WithViewMsels_ReturnsTheRolesOnTheMsel()
    {
        var msel = await SeedMsel();
        var subject = await SeedUser();
        var row = await SeedRole(subject.Id, msel.Id, MselRole.Editor);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var roles = await GetRoles(Client(actor), MselRoles(msel.Id));

        var only = Assert.Single(roles);

        Assert.Equal(row.Id, only.Id);
        Assert.Equal(subject.Id, only.UserId);
        Assert.Equal(MselRole.Editor, only.Role);
    }

    [Fact]
    public async Task GetByMsel_ForAViewerOnTheMsel_Is200()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var roles = await GetRoles(Client(actor), MselRoles(msel.Id));

        Assert.Equal(actor.Id, Assert.Single(roles).UserId);
    }

    [Fact]
    public async Task GetByMsel_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        var response = await Client(actor).GetAsync(MselRoles(msel.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// The route resolves <c>CreateMsels</c> as well and passes it into <c>MselViewRequirement</c>'s
    /// four-argument overload, whose only reader is the template fall-through.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_ForATemplate_WithCreateMsels_Is200()
    {
        var msel = TestData.Msel(isTemplate: true);
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var roles = await GetRoles(Client(actor), MselRoles(msel.Id));

        Assert.Empty(roles);
    }

    /// <summary>An unknown MSEL's role list is empty for a <c>ViewMsels</c> holder.</summary>
    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_WithViewMsels_IsAnEmptyList()
    {
        var other = await SeedMsel();
        var subject = await SeedUser();
        await SeedRole(subject.Id, other.Id, MselRole.Editor);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var roles = await GetRoles(Client(actor), MselRoles(Guid.NewGuid()));

        Assert.Empty(roles);
    }

    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        var response = await Client(actor).GetAsync(MselRoles(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetByMsel_DoesNotReturnAnotherMselsRoles()
    {
        var msel = await SeedMsel();
        var other = await SeedMsel();
        var subject = await SeedUser();
        var elsewhere = await SeedRole(subject.Id, other.Id, MselRole.Editor);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var roles = await GetRoles(Client(actor), MselRoles(msel.Id));

        Assert.DoesNotContain(elsewhere.Id, roles.Select(x => x.Id));
    }

    /// <remarks>
    /// The three integration roles are the only reason this row is read by anything other than an
    /// authorization check: they are what <c>IntegrationCiteExtensions</c>,
    /// <c>IntegrationGalleryExtensions</c> and <c>IntegrationSteamfitterExtensions</c> match by name
    /// against the sibling applications' own role names. All three are free text with no validation.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_AnswersTheIntegrationRoles()
    {
        var msel = await SeedMsel();
        var subject = await SeedUser();
        var row = TestData.UserMselRole(subject.Id, msel.Id, MselRole.Evaluator);
        row.CiteEvaluationRole = "Inject";
        row.GalleryExhibitRole = "Blue";
        row.SteamfitterScenarioRole = "Observer";
        await Seed(row);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var only = Assert.Single(await GetRoles(Client(actor), MselRoles(msel.Id)));

        Assert.Equal("Inject", only.CiteEvaluationRole);
        Assert.Equal("Blue", only.GalleryExhibitRole);
        Assert.Equal("Observer", only.SteamfitterScenarioRole);
    }

    // ---------------------------------------------------------------------------------------------
    // GET usermselroles/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_WithViewMsels_ReturnsTheRole()
    {
        var msel = await SeedMsel();
        var subject = await SeedUser();
        var row = await SeedRole(subject.Id, msel.Id, MselRole.Approver);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var role = await GetRole(Client(actor), row.Id);

        Assert.Equal(msel.Id, role.MselId);
        Assert.Equal(subject.Id, role.UserId);
        Assert.Equal(MselRole.Approver, role.Role);
    }

    [Fact]
    public async Task Get_ForAViewerOnTheMsel_Is200()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();
        var row = Assert.Single(await StoredRows());

        var role = await GetRole(Client(actor), row.Id);

        Assert.Equal(actor.Id, role.UserId);
    }

    [Fact]
    public async Task Get_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var msel = await SeedMsel();
        var subject = await SeedUser();
        var row = await SeedRole(subject.Id, msel.Id, MselRole.Editor);
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        var response = await Client(actor).GetAsync(RoleRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>An unknown role row is a 404 for a <c>ViewMsels</c> holder.</summary>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_WithViewMsels_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync(RoleRoute(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Get for an id that is not there without view MSELs is answered with a 500.</summary>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_WithoutViewMsels_Is500()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).GetAsync(RoleRoute(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <remarks>
    /// <c>UserMselRoleEntity</c> is a <c>BaseEntity</c> and <c>ViewModels.UserMselRole</c> derives from
    /// <c>Base</c>, so unlike <c>TeamUser</c> and <c>UnitUser</c> the
    /// audit fields on this route are real columns - granting somebody a role on an exercise is recorded
    /// where putting them on the team is not.
    /// </remarks>
    [Fact]
    public async Task Get_AnswersAuditFieldsBackedByRealColumns()
    {
        var msel = await SeedMsel();
        var subject = await SeedUser();
        var creator = Guid.NewGuid();
        var row = TestData.UserMselRole(subject.Id, msel.Id, MselRole.Editor, createdBy: creator);
        await Seed(row);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var role = await GetRole(Client(actor), row.Id);

        Assert.Equal(creator, role.CreatedBy);
        Assert.NotEqual(default, role.DateCreated);
        Assert.Null(role.ModifiedBy);
        Assert.Null(role.DateModified);
    }

    // ---------------------------------------------------------------------------------------------
    // POST usermselroles
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_ForAnOwnerOfTheMsel_Is201()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var subject = await SeedUser();
        var id = Guid.NewGuid();

        var response = await Post(Client(actor), Body(subject.Id, msel.Id, MselRole.Editor) with { Id = id });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.EndsWith($"/api/usermselroles/{id}", response.Headers.Location?.ToString());

        var created = await Read<ViewModels.UserMselRole>(response);

        Assert.Equal(id, created.Id);
        Assert.Equal(MselRole.Editor, created.Role);
        Assert.Equal(MselRole.Editor, (await Stored(id)).Role);
    }

    /// <summary>A create that omits the id answers 201 with a minted id.</summary>
    [Fact]
    public async Task Create_ThatOmitsTheId_Is201()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var subject = await SeedUser();

        var response = await Post(
            Client(actor), new { userId = subject.Id, mselId = msel.Id, role = MselRole.Editor });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await Read<ViewModels.UserMselRole>(response);

        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.EndsWith($"/api/usermselroles/{created.Id}", response.Headers.Location?.ToString());
        Assert.Equal(subject.Id, (await Stored(created.Id)).UserId);
    }

    [Fact]
    public async Task Create_WithManageMselsOnly_Is201()
    {
        var msel = await SeedMsel();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();
        var subject = await SeedUser();

        var response = await Post(Client(actor), Body(subject.Id, msel.Id, MselRole.Editor));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Create_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();
        var subject = await SeedUser();

        var response = await Post(Client(actor), Body(subject.Id, msel.Id, MselRole.Editor));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain(await StoredRows(), x => x.UserId == subject.Id);
    }

    /// <remarks>
    /// Owner-only, where the read one route up is viewer. So a MSEL's <c>Editor</c> - who may rewrite its
    /// whole timeline - may see who holds which role on it and change none of them, and an
    /// <c>Approver</c> likewise. <c>MselOwnerRequirement</c> accepts the MSEL's creator and the
    /// <c>Owner</c> role and nothing else.
    /// </remarks>
    [Fact]
    public async Task Create_ForAnEditorOfTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();
        var subject = await SeedUser();

        var response = await Post(Client(actor), Body(subject.Id, msel.Id, MselRole.Approver));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Create for somebody with no path to the MSEL is answered with a 201 and grants nothing.</summary>
    [Fact]
    public async Task Create_ForSomebodyWithNoPathToTheMsel_Is201AndGrantsNothing()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var outsider = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(outsider.Id, msel.Id, MselRole.Viewer));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var theirs = await Client(outsider).GetAsync(MselRoles(msel.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, theirs.StatusCode);
    }

    /// <summary>Create with a duplicate role is answered with a 500.</summary>
    [Fact]
    public async Task Create_WithADuplicateRole_Is500()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var subject = await SeedUser();
        await SeedRole(subject.Id, msel.Id, MselRole.Editor);

        var response = await Post(Client(actor), Body(subject.Id, msel.Id, MselRole.Editor));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>Create for a MSEL that is not there is answered with a 500 for everybody.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Create_ForAMselThatIsNotThere_Is500ForEverybody(bool hasManageMsels)
    {
        var builder = Actor();

        if (hasManageMsels)
        {
            builder = builder.WithSystemPermissions(SystemPermission.ManageMsels);
        }

        var actor = await builder.SeedAsync();
        var subject = await SeedUser();

        var response = await Post(Client(actor), Body(subject.Id, Guid.NewGuid(), MselRole.Editor));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Create_StampsTheAuditFieldsOnTheServer()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var subject = await SeedUser();
        var id = Guid.NewGuid();
        var before = DateTime.UtcNow;

        var response = await Post(Client(actor), Body(subject.Id, msel.Id, MselRole.Editor) with
        {
            Id = id,
            CreatedBy = Guid.NewGuid(),
            DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ModifiedBy = Guid.NewGuid(),
            DateModified = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var stored = await Stored(id);

        Assert.Equal(actor.Id, stored.CreatedBy);
        Assert.InRange(stored.DateCreated, before, DateTime.UtcNow);
        Assert.Null(stored.ModifiedBy);
        Assert.Null(stored.DateModified);
    }

    /// <remarks>
    /// The positive control the tier needs. Six services in the membership and exercise-content tiers
    /// never mark their MSEL modified on any write path (<c>CardService</c>, <c>CardTeamService</c>,
    /// <c>TeamService</c>, <c>TeamUserService</c>, and the unit-membership services); this one does, on
    /// both writes, inside the explicit transaction that makes the stamp and the row one unit. Note the
    /// <c>dateModified</c> argument is dead - <c>SaveEntries</c> overwrites it with <c>UtcNow</c> - but
    /// <c>modifiedBy</c> is not, because <c>SaveEntries</c> never touches it.
    /// </remarks>
    [Fact]
    public async Task Create_MarksTheMselModified()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var subject = await SeedUser();
        var before = DateTime.UtcNow;

        var response = await Post(Client(actor), Body(subject.Id, msel.Id, MselRole.Editor));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var stored = await StoredMsel(msel.Id);

        Assert.Equal(actor.Id, stored.ModifiedBy);
        Assert.InRange(stored.DateModified.Value, before, DateTime.UtcNow);
    }

    /// <remarks>
    /// <c>UserMselRoleHandler.GetGroups</c> names the MSEL's id and <c>ADMIN_DATA_GROUP</c>, so a client
    /// watching one exercise hears about its role changes - which is more than the unit-membership
    /// assignment manages, there being no <c>MselUnitHandler</c> at all. The payload is
    /// the mapped view model; the second argument is the modified-property list, null on a create.
    /// </remarks>
    [Fact]
    public async Task Create_TellsTheMselsGroupAndTheAdminDataGroup()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var subject = await SeedUser();

        var response = await Post(Client(actor), Body(subject.Id, msel.Id, MselRole.Editor));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var recipients = Hub.Recipients(MainHubMethods.UserMselRoleCreated, msel.Id);

        Assert.Contains(msel.Id.ToString(), recipients);
        Assert.Contains(MainHub.ADMIN_DATA_GROUP, recipients);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE usermselroles/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_ForAnOwnerOfTheMsel_Is204()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var subject = await SeedUser();
        var row = await SeedRole(subject.Id, msel.Id, MselRole.Editor);

        var response = await Client(actor).DeleteAsync(RoleRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await Stored(row.Id));
    }

    [Fact]
    public async Task Delete_WithManageMselsOnly_Is204()
    {
        var msel = await SeedMsel();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();
        var subject = await SeedUser();
        var row = await SeedRole(subject.Id, msel.Id, MselRole.Editor);

        var response = await Client(actor).DeleteAsync(RoleRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Delete_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();
        var subject = await SeedUser();
        var row = await SeedRole(subject.Id, msel.Id, MselRole.Editor);

        var response = await Client(actor).DeleteAsync(RoleRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(await Stored(row.Id));
    }

    /// <summary>An unknown role row is a 404 on delete for every caller.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Delete_ForAnIdThatIsNotThere_Is404ForEverybody(bool hasManageMsels)
    {
        var builder = Actor();

        if (hasManageMsels)
        {
            builder = builder.WithSystemPermissions(SystemPermission.ManageMsels);
        }

        var actor = await builder.SeedAsync();

        var response = await Client(actor).DeleteAsync(RoleRoute(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_MarksTheMselModified()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var subject = await SeedUser();
        var row = await SeedRole(subject.Id, msel.Id, MselRole.Editor);
        var before = DateTime.UtcNow;

        var response = await Client(actor).DeleteAsync(RoleRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var stored = await StoredMsel(msel.Id);

        Assert.Equal(actor.Id, stored.ModifiedBy);
        Assert.InRange(stored.DateModified.Value, before, DateTime.UtcNow);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT msels/{mselId}/users/{userId}/integrationroles
    // ---------------------------------------------------------------------------------------------

    /// <summary>Set integration roles updates every row for the pair.</summary>
    [Fact]
    public async Task SetIntegrationRoles_UpdatesEveryRowForThePair()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var subject = await SeedUser();
        await SeedRole(subject.Id, msel.Id, MselRole.Editor);
        await SeedRole(subject.Id, msel.Id, MselRole.Approver);

        var response = await SetIntegrationRoles(Client(actor), msel.Id, subject.Id, new
        {
            citeEvaluationRole = "Inject",
            galleryExhibitRole = "Blue",
            steamfitterScenarioRole = "Observer"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await Read<List<ViewModels.UserMselRole>>(response)).Count);

        var rows = (await StoredRows()).Where(x => x.UserId == subject.Id).ToList();

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal("Inject", row.CiteEvaluationRole);
            Assert.Equal("Blue", row.GalleryExhibitRole);
            Assert.Equal("Observer", row.SteamfitterScenarioRole);
        });
    }

    [Fact]
    public async Task SetIntegrationRoles_WithManageMselsOnly_Is200()
    {
        var msel = await SeedMsel();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();
        var subject = await SeedUser();
        await SeedRole(subject.Id, msel.Id, MselRole.Editor);

        var response = await SetIntegrationRoles(
            Client(actor), msel.Id, subject.Id, new { citeEvaluationRole = "Inject" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SetIntegrationRoles_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();
        var subject = await SeedUser();
        var row = await SeedRole(subject.Id, msel.Id, MselRole.Editor);

        var response = await SetIntegrationRoles(
            Client(actor), msel.Id, subject.Id, new { citeEvaluationRole = "Inject" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null((await Stored(row.Id)).CiteEvaluationRole);
    }

    /// <summary>Set integration roles for the MSEL creator with no role row creates one with a role the enum does not define.</summary>
    [Fact]
    public async Task SetIntegrationRoles_ForTheMselCreatorWithNoRoleRow_CreatesOneWithARoleTheEnumDoesNotDefine()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();
        var msel = TestData.Msel(createdBy: actor.Id);
        await Seed(msel);

        var response = await SetIntegrationRoles(Client(actor), msel.Id, actor.Id, new
        {
            citeEvaluationRole = "Inject",
            galleryExhibitRole = "Blue",
            steamfitterScenarioRole = "Observer"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"role\":0", await response.Content.ReadAsStringAsync(Ct));

        var row = Assert.Single(await StoredRows());

        Assert.Equal(actor.Id, row.UserId);
        Assert.Equal((MselRole)0, row.Role);
        Assert.False(Enum.IsDefined(row.Role));
        Assert.Equal("Inject", row.CiteEvaluationRole);
    }

    /// <remarks>
    /// Anybody other than the creator with no row is an <c>EntityNotFoundException</c>, so the route
    /// cannot be used to set a participant's CITE or Gallery role until somebody has given them a MSEL
    /// role by another request - and the 404 names <c>UserMselRole</c>, which is accurate but reads as
    /// though the <em>user</em> or the MSEL were missing.
    /// </remarks>
    [Fact]
    public async Task SetIntegrationRoles_ForAUserWithNoRowWhoIsNotTheCreator_Is404()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var subject = await SeedUser();

        var response = await SetIntegrationRoles(
            Client(actor), msel.Id, subject.Id, new { citeEvaluationRole = "Inject" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>An unknown MSEL is a 404 about the missing role row for a <c>ManageMsels</c> holder.</summary>
    [Fact]
    public async Task SetIntegrationRoles_ForAMselThatIsNotThere_WithManageMsels_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await SetIntegrationRoles(
            Client(actor), Guid.NewGuid(), actor.Id, new { citeEvaluationRole = "Inject" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Set integration roles for a MSEL that is not there without manage MSELs is answered with a 500.</summary>
    [Fact]
    public async Task SetIntegrationRoles_ForAMselThatIsNotThere_WithoutManageMsels_Is500()
    {
        var actor = await Actor().SeedAsync();

        var response = await SetIntegrationRoles(
            Client(actor), Guid.NewGuid(), actor.Id, new { citeEvaluationRole = "Inject" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>Set integration roles with a partial body clears the roles it does not mention.</summary>
    [Fact]
    public async Task SetIntegrationRoles_WithAPartialBody_ClearsTheRolesItDoesNotMention()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var subject = await SeedUser();
        var row = TestData.UserMselRole(subject.Id, msel.Id, MselRole.Editor);
        row.CiteEvaluationRole = "Inject";
        row.GalleryExhibitRole = "Blue";
        row.SteamfitterScenarioRole = "Observer";
        await Seed(row);

        var response = await SetIntegrationRoles(
            Client(actor), msel.Id, subject.Id, new { galleryExhibitRole = "Red" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await Stored(row.Id);

        Assert.Equal("Red", stored.GalleryExhibitRole);
        Assert.Null(stored.CiteEvaluationRole);
        Assert.Null(stored.SteamfitterScenarioRole);
    }

    [Fact]
    public async Task SetIntegrationRoles_DoesNotTouchAnotherUsersRows()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var subject = await SeedUser();
        await SeedRole(subject.Id, msel.Id, MselRole.Editor);
        var other = await SeedUser();
        var theirs = TestData.UserMselRole(other.Id, msel.Id, MselRole.Editor);
        theirs.CiteEvaluationRole = "Blue";
        await Seed(theirs);

        var response = await SetIntegrationRoles(
            Client(actor), msel.Id, subject.Id, new { citeEvaluationRole = "Inject" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Blue", (await Stored(theirs.Id)).CiteEvaluationRole);
    }

    [Fact]
    public async Task SetIntegrationRoles_MarksTheMselModified()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var subject = await SeedUser();
        await SeedRole(subject.Id, msel.Id, MselRole.Editor);
        var before = DateTime.UtcNow;

        var response = await SetIntegrationRoles(
            Client(actor), msel.Id, subject.Id, new { citeEvaluationRole = "Inject" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await StoredMsel(msel.Id);

        Assert.Equal(actor.Id, stored.ModifiedBy);
        Assert.InRange(stored.DateModified.Value, before, DateTime.UtcNow);
    }

    // ---------------------------------------------------------------------------------------------
    // Authentication
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "msels/00000000-0000-0000-0000-000000000001/usermselroles")]
    [InlineData("GET", "usermselroles/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "usermselroles")]
    [InlineData("DELETE", "usermselroles/00000000-0000-0000-0000-000000000001")]
    [InlineData("PUT", "msels/00000000-0000-0000-0000-000000000001/users/00000000-0000-0000-0000-000000000002/integrationroles")]
    public async Task EveryRouteRefusesAnUnauthenticatedRequest(string method, string route)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/{route}")
        {
            Content = JsonContent.Create(new { })
        };

        var response = await Client().SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------

    private const string UserMselRoles = "/api/usermselroles";

    private static string RoleRoute(Guid id) => $"{UserMselRoles}/{id}";

    private static string MselRoles(Guid mselId) => $"/api/msels/{mselId}/usermselroles";

    private static string IntegrationRolesRoute(Guid mselId, Guid userId) =>
        $"/api/msels/{mselId}/users/{userId}/integrationroles";

    /// <summary>
    /// The wire shape of a role row. The two non-nullable audit properties are non-nullable here too,
    /// because <c>ViewModels.Base</c> declares them so and a body sending null for either is a 400 that
    /// never reaches the controller.
    /// </summary>
    private sealed record RoleBody
    {
        public Guid Id { get; init; }
        public Guid MselId { get; init; }
        public Guid UserId { get; init; }
        public MselRole Role { get; init; }
        public string CiteEvaluationRole { get; init; }
        public string GalleryExhibitRole { get; init; }
        public string SteamfitterScenarioRole { get; init; }

        public Guid CreatedBy { get; init; }
        public DateTime DateCreated { get; init; }
        public Guid? ModifiedBy { get; init; }
        public DateTime? DateModified { get; init; }
    }

    private static RoleBody Body(Guid userId, Guid mselId, MselRole role) =>
        new() { UserId = userId, MselId = mselId, Role = role };

    private async Task<MselEntity> SeedMsel()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        return msel;
    }

    private async Task<UserEntity> SeedUser()
    {
        var user = TestData.User();
        await Seed(user);

        return user;
    }

    private async Task<UserMselRoleEntity> SeedRole(Guid userId, Guid mselId, MselRole role)
    {
        var entity = TestData.UserMselRole(userId, mselId, role);
        await Seed(entity);

        return entity;
    }

    private Task<HttpResponseMessage> Post(HttpClient client, object body) =>
        client.PostAsJsonAsync(UserMselRoles, body, Ct);

    private Task<HttpResponseMessage> SetIntegrationRoles(
        HttpClient client, Guid mselId, Guid userId, object body) =>
        client.PutAsJsonAsync(IntegrationRolesRoute(mselId, userId), body, Ct);

    private async Task<List<ViewModels.UserMselRole>> GetRoles(HttpClient client, string route)
    {
        var response = await client.GetAsync(route, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<List<ViewModels.UserMselRole>>(response);
    }

    private async Task<ViewModels.UserMselRole> GetRole(HttpClient client, Guid id)
    {
        var response = await client.GetAsync(RoleRoute(id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<ViewModels.UserMselRole>(response);
    }

    private async Task<UserMselRoleEntity> Stored(Guid id)
    {
        await using var context = NewContext();

        return await context.UserMselRoles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, Ct);
    }

    private async Task<List<UserMselRoleEntity>> StoredRows()
    {
        await using var context = NewContext();

        return await context.UserMselRoles.AsNoTracking().ToListAsync(Ct);
    }

    private async Task<MselEntity> StoredMsel(Guid id)
    {
        await using var context = NewContext();

        return await context.Msels.AsNoTracking().SingleAsync(x => x.Id == id, Ct);
    }

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }
}
