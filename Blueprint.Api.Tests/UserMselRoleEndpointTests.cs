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
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>UserMselRoleService</c> / <c>UserMselRoleController</c> - the five routes over the row that says what
/// a person may do on one MSEL. The third file on this branch to touch these rows:
/// <c>MselUnitService</c> grants them wholesale when a unit is assigned (<c>977f578</c>),
/// <c>UnitUserService</c> orphans them when a membership goes, and this is the one service whose whole
/// subject they are.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the well-behaved service of the three in unit 3, and two of its methods are the fix
/// for defects recorded elsewhere.</strong> <c>CreateAsync</c> returns
/// <c>GetAsync(userMselRoleEntity.Id, …)</c> - the entity's id - where <c>UserService.CreateAsync</c>
/// re-reads by the request body's and answers 500 when they differ, so a create here that omits the id
/// is a clean 201 (<see cref="Create_ThatOmitsTheId_Is201"/>). And both writes call
/// <c>ServiceUtilities.SetMselModifiedAsync</c> inside an explicit transaction
/// (<see cref="Create_MarksTheMselModified"/>, <see cref="Delete_MarksTheMselModified"/>), in a tier
/// where six services never mark a MSEL modified on any write path at all. Named as positive controls
/// rather than left implicit, because the contrast is the finding.
/// </para>
/// <para>
/// <strong>The exception is <c>GetAsync</c>, which dereferences its own lookup.</strong>
/// <c>UserMselRoleService.cs:62-66</c> reads <c>SingleOrDefaultAsync</c> and then passes
/// <c>item.MselId</c> to <c>MselViewRequirement</c> with no null check - but the dereference sits on the
/// right of <c>!hasSystemPermission &amp;&amp;</c>, so which answer an unknown id gets depends on who
/// asks: a <c>ViewMsels</c> holder short-circuits past it, the map of a null answers null and the
/// controller's own check at <c>:67</c> produces a 404
/// (<see cref="Get_ForAnIdThatIsNotThere_WithViewMsels_Is404"/>), while everybody else gets a 500 from
/// the <c>NullReferenceException</c> (<see cref="Get_ForAnIdThatIsNotThere_WithoutViewMsels_Is500"/>).
/// So the null check is neither dead nor live; it is live for one class of caller. <c>DeleteAsync</c>
/// eleven lines below checks existence <em>before</em> the permission and is a clean 404 for everybody
/// (<see cref="Delete_ForAnIdThatIsNotThere_Is404ForEverybody"/>) - one file, one question, two answers,
/// and the well-behaved one is the later method.
/// </para>
/// <para>
/// <strong><c>SetIntegrationRolesAsync</c> writes a role that is not a <c>MselRole</c>.</strong> When the
/// pair has no row at all the method back-fills one, but only if the <c>userId</c> in the route is the
/// MSEL's own <c>CreatedBy</c> - anybody else is a 404
/// (<see cref="SetIntegrationRoles_ForAUserWithNoRowWhoIsNotTheCreator_Is404"/>) - and the
/// <c>UserMselRole</c> it builds sets four properties and leaves <c>Role</c> unset. <c>MselRole</c> starts
/// at <c>Owner = 10</c>, so the stored value is <c>(MselRole)0</c>, a number no name maps to: the MSEL's
/// creator ends up holding a role the enum does not define, which every requirement helper's role list
/// therefore refuses (<see cref="SetIntegrationRoles_ForTheMselCreatorWithNoRoleRow_CreatesOneWithARoleTheEnumDoesNotDefine"/>).
/// It crosses the wire as the bare number <c>0</c> where every other enum in the API is a name.
/// </para>
/// <para>
/// <strong>And it rewrites every row for the pair, which the unique index makes plural.</strong>
/// <c>(MselId, UserId, Role)</c> is the unique index rather than <c>(MselId, UserId)</c>, so one person
/// may hold several roles on one MSEL - and setting their CITE evaluation role then writes the same value
/// onto all of them (<see cref="SetIntegrationRoles_UpdatesEveryRowForThePair"/>). Three integration
/// roles duplicated across however many MSEL roles somebody holds, with nothing deciding which row is
/// authoritative; <c>IntegrationCiteExtensions.cs:133</c> matches the first one the database returns
/// (<c>4dd5201</c>).
/// </para>
/// <para>
/// Also characterized: nothing checks that the user is <em>on</em> the MSEL before granting them a role
/// there, and Phase 2 established that a <c>UserMselRoleEntity</c> without a <c>UnitUserEntity</c> is a
/// no-op in every requirement helper - so a create is allowed to write a row that grants nothing and
/// reports 201 (<see cref="Create_ForSomebodyWithNoPathToTheMsel_Is201AndGrantsNothing"/>); the create
/// is owner-only where the read is viewer, so a MSEL's <c>Editor</c> may see every role on it and grant
/// none (<see cref="Create_ForAnEditorOfTheMsel_Is403"/>); a duplicate is a 500 from the index rather
/// than a 409; and an unknown MSEL is a 500 either way, by a foreign key for a <c>ManageMsels</c> holder
/// and by <c>MselOwnerRequirement</c>'s unguarded <c>.CreatedBy</c> for everybody else.
/// </para>
/// </remarks>
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
    public async Task GetByMsel_WithNoRoleOnTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var actor = await Actor().SeedAsync();

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
        var msel = BlueprintAppFactory.Msel(isTemplate: true);
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var roles = await GetRoles(Client(actor), MselRoles(msel.Id));

        Assert.Empty(roles);
    }

    /// <remarks>
    /// Seeds a role on another MSEL that must not appear, so an inverted filter reddens this rather than
    /// answering an empty list from an empty database - <c>4dd5201</c>'s lesson.
    /// </remarks>
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
    public async Task GetByMsel_ForAMselThatIsNotThere_WithoutViewMsels_Is403()
    {
        var actor = await Actor().SeedAsync();

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
        var row = BlueprintAppFactory.UserMselRole(subject.Id, msel.Id, MselRole.Evaluator);
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
    public async Task Get_WithNoRoleOnTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var subject = await SeedUser();
        var row = await SeedRole(subject.Id, msel.Id, MselRole.Editor);
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync(RoleRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// The controller's null check at <c>:67</c> is reachable, but only for a caller who holds
    /// <c>ViewMsels</c>: <c>hasSystemPermission</c> short-circuits the service's unguarded
    /// <c>item.MselId</c>, the map of a null row answers null and the controller turns that into a 404.
    /// Fixing the service - a null check before the permission, as <c>DeleteAsync</c> has - makes this
    /// route answer 404 for everybody and reddens
    /// <see cref="Get_ForAnIdThatIsNotThere_WithoutViewMsels_Is500"/> only.
    /// </remarks>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_WithViewMsels_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync(RoleRoute(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <remarks>
    /// The other side of the same line. A caller without <c>ViewMsels</c> reaches
    /// <c>item.MselId</c> on a null row, so an id that is not there is a
    /// <c>NullReferenceException</c> - not an <c>IApiException</c>, so <c>JsonExceptionFilter</c> answers
    /// 500. A MSEL owner asking about a role row that has been deleted since their screen loaded gets an
    /// internal error where an administrator gets a 404.
    /// </remarks>
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
    /// <c>Base</c>, so unlike <c>TeamUser</c> (<c>4dd5201</c>) and <c>UnitUser</c> (<c>977f578</c>) the
    /// audit fields on this route are real columns - granting somebody a role on an exercise is recorded
    /// where putting them on the team is not.
    /// </remarks>
    [Fact]
    public async Task Get_AnswersAuditFieldsBackedByRealColumns()
    {
        var msel = await SeedMsel();
        var subject = await SeedUser();
        var creator = Guid.NewGuid();
        var row = BlueprintAppFactory.UserMselRole(subject.Id, msel.Id, MselRole.Editor, createdBy: creator);
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

    /// <remarks>
    /// The positive control for <c>UserService.CreateAsync</c>'s defect one file over. The service mints
    /// an id when the body omits one (<c>:80</c>) and then re-reads by
    /// <c>userMselRoleEntity.Id</c> (<c>:91</c>) rather than by the view model's, so the read finds the
    /// row and the <c>Location</c> header names it. The body must be an anonymous object: a typed
    /// <c>Guid Id</c> cannot express "absent".
    /// </remarks>
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
    public async Task Create_WithNoRoleOnTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var actor = await Actor().SeedAsync();
        var subject = await SeedUser();

        var response = await Post(Client(actor), Body(subject.Id, msel.Id, MselRole.Editor));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await StoredRows());
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

    /// <remarks>
    /// Nothing checks that the user has any path to the MSEL, and Phase 2 established that a
    /// <c>UserMselRoleEntity</c> without a <c>UnitUserEntity</c> is a no-op in all eight requirement
    /// helpers - the role query is never reached unless the unit query already found the user. So this
    /// row is stored, broadcast and answered 201, and grants nothing: the second assertion is the same
    /// caller being refused the MSEL's own role list. This is the mistake an administrator makes adding
    /// somebody to an exercise by hand, and the API's answer is indistinguishable from success.
    /// </remarks>
    [Fact]
    public async Task Create_ForSomebodyWithNoPathToTheMsel_Is201AndGrantsNothing()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var outsider = await Actor().SeedAsync();

        var response = await Post(Client(actor), Body(outsider.Id, msel.Id, MselRole.Viewer));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var theirs = await Client(outsider).GetAsync(MselRoles(msel.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, theirs.StatusCode);
    }

    /// <remarks>
    /// <c>(MselId, UserId, Role)</c> is uniquely indexed, so the same role twice is a
    /// <c>DbUpdateException</c> - a 500 where a 409 is the answer, the same shape as every other
    /// duplicate on this branch. What the index does <em>not</em> forbid is the same pair with a
    /// different role; see <see cref="SetIntegrationRoles_UpdatesEveryRowForThePair"/>.
    /// </remarks>
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

    /// <remarks>
    /// With <c>ManageMsels</c> the permission check is skipped and the insert fails on the foreign key;
    /// without it, <c>MselOwnerRequirement.IsMet</c> dereferences a MSEL that is not there. Two unrelated
    /// causes, one status, and neither is the 404 the request deserves.
    /// </remarks>
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
    /// <c>TeamService</c>, <c>TeamUserService</c>, and the three in <c>977f578</c>); this one does, on
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
    /// assignment manages, there being no <c>MselUnitHandler</c> at all (<c>977f578</c>). The payload is
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

        var recipients = Hub.Recipients(MainHubMethods.UserMselRoleCreated);

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
    public async Task Delete_WithNoRoleOnTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var actor = await Actor().SeedAsync();
        var subject = await SeedUser();
        var row = await SeedRole(subject.Id, msel.Id, MselRole.Editor);

        var response = await Client(actor).DeleteAsync(RoleRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(await Stored(row.Id));
    }

    /// <remarks>
    /// Existence before permission, so the answer does not depend on who asks - the shape
    /// <see cref="Get_ForAnIdThatIsNotThere_WithViewMsels_Is404"/> shows this same service getting wrong
    /// thirty lines above, and the model for fixing it.
    /// </remarks>
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

    /// <remarks>
    /// The unique index is <c>(MselId, UserId, Role)</c>, so holding two MSEL roles is allowed - and the
    /// three integration roles then belong to the pair rather than to either row, so this writes both.
    /// Nothing decides which row is authoritative afterwards, and the integration extensions read
    /// whichever the database hands back first.
    /// </remarks>
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
    public async Task SetIntegrationRoles_WithNoRoleOnTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var actor = await Actor().SeedAsync();
        var subject = await SeedUser();
        var row = await SeedRole(subject.Id, msel.Id, MselRole.Editor);

        var response = await SetIntegrationRoles(
            Client(actor), msel.Id, subject.Id, new { citeEvaluationRole = "Inject" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null((await Stored(row.Id)).CiteEvaluationRole);
    }

    /// <remarks>
    /// The headline. With no row for the pair the method back-fills one for the MSEL's creator, setting
    /// the three integration roles and leaving <c>Role</c> at its default - and <c>MselRole</c> starts at
    /// <c>Owner = 10</c>, so the stored value is <c>(MselRole)0</c>, which
    /// <see cref="Enum.IsDefined{TEnum}(TEnum)"/> denies. Every requirement helper matches on a list of
    /// named roles, so the row grants nothing; it crosses the wire as the bare number <c>0</c>, where
    /// every other enum in the API is a name, which is what a client's generated enum type will refuse to
    /// parse. Fixing it - naming <c>Role = MselRole.Owner</c> in the initializer at
    /// <c>UserMselRoleService.cs:133-140</c> - reddens the <c>(MselRole)0</c> assertions here and nothing
    /// else.
    /// </remarks>
    [Fact]
    public async Task SetIntegrationRoles_ForTheMselCreatorWithNoRoleRow_CreatesOneWithARoleTheEnumDoesNotDefine()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();
        var msel = BlueprintAppFactory.Msel(createdBy: actor.Id);
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

    /// <remarks>
    /// A MSEL that is not there takes the creator branch, where <c>SingleOrDefaultAsync</c> answers
    /// <c>Guid.Empty</c> and the comparison against the route's <c>userId</c> fails - so the answer is a
    /// 404 that says a role row is missing rather than that the MSEL is. The lookup takes no
    /// <c>CancellationToken</c>.
    /// </remarks>
    [Fact]
    public async Task SetIntegrationRoles_ForAMselThatIsNotThere_WithManageMsels_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await SetIntegrationRoles(
            Client(actor), Guid.NewGuid(), actor.Id, new { citeEvaluationRole = "Inject" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <remarks>
    /// And without the permission the same request is a 500, because <c>MselOwnerRequirement.IsMet</c>
    /// dereferences the missing MSEL's <c>CreatedBy</c>. The two callers cannot compare notes.
    /// </remarks>
    [Fact]
    public async Task SetIntegrationRoles_ForAMselThatIsNotThere_WithoutManageMsels_Is500()
    {
        var actor = await Actor().SeedAsync();

        var response = await SetIntegrationRoles(
            Client(actor), Guid.NewGuid(), actor.Id, new { citeEvaluationRole = "Inject" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <remarks>
    /// The controller reads the body as <c>update?.CiteEvaluationRole</c>, so an empty JSON object - and
    /// a body that mentions only one of the three - clears the others. There is no way to set one
    /// integration role without restating the other two, and a client that sends a partial body silently
    /// unassigns whatever it left out.
    /// </remarks>
    [Fact]
    public async Task SetIntegrationRoles_WithAPartialBody_ClearsTheRolesItDoesNotMention()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var subject = await SeedUser();
        var row = BlueprintAppFactory.UserMselRole(subject.Id, msel.Id, MselRole.Editor);
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
        var theirs = BlueprintAppFactory.UserMselRole(other.Id, msel.Id, MselRole.Editor);
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

        var response = await AnonymousClient.SendAsync(request, Ct);

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
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        return msel;
    }

    private async Task<UserEntity> SeedUser()
    {
        var user = BlueprintAppFactory.User();
        await Seed(user);

        return user;
    }

    private async Task<UserMselRoleEntity> SeedRole(Guid userId, Guid mselId, MselRole role)
    {
        var entity = BlueprintAppFactory.UserMselRole(userId, mselId, role);
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
