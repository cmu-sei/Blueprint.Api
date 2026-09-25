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
using System.Text.Json;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>UnitService</c> / <c>UnitController</c> - the nine routes behind the reusable group of people that
/// gets assigned to a MSEL, as against a team, which belongs to one MSEL and cannot be reused.
/// </summary>
/// <remarks>
/// <para>
/// <strong><c>GET units</c> asks the caller for nothing at all.</strong> <c>UnitController.cs:42-49</c>
/// has no <c>AuthorizeAsync</c> call and the service checks nothing, so any authenticated caller reads
/// every unit in the installation - while the route's own remarks say "Only accessible to a SuperUser"
/// and its sibling <c>GET unitusers</c> at least requires <c>ViewUnits</c>. See
/// <see cref="GetAll_ForACallerWithNoPermissionAtAll_Is200"/>. Adding a <c>ViewUnits</c> check turns that
/// test red and leaves every other read in this file alone.
/// </para>
/// <para>
/// <strong>Every unit route answers an empty <c>users</c> list.</strong> Neither <c>GetAsync</c> overload
/// <c>Include</c>s <c>UnitUsers</c>, and <c>UnitProfile</c> maps <c>Users</c> from
/// <c>UnitUsers.Select(y => y.User)</c>, so the collection is always empty however many members the unit
/// has - see <see cref="Get_AnswersAnEmptyUsersListForAUnitThatHasMembers"/>. The MSEL-scoped read of the
/// same unit does <c>ThenInclude</c> all the way to <c>User</c>
/// (<c>MselUnitEndpointTests.GetByMsel_AnswersTheUnitsMembersWhereTheUnitRoutesDoNot</c>), so the one
/// thing the unit's own routes cannot tell you about a unit is who is in it. Note also that
/// <c>UnitProfile</c> declares <c>Users</c> twice, the second call being an <c>ExplicitExpansion</c> that
/// only affects <c>ProjectTo</c> and so does nothing here.
/// </para>
/// <para>
/// <strong>Two of the service's guards were copy-pasted from <c>UserService</c> and compare a unit id
/// against a user id.</strong> <c>UpdateAsync</c> opens with "You cannot change your own Id" and
/// <c>DeleteAsync</c> with "You cannot delete your own account" (<c>UnitService.cs:104-108</c> and
/// <c>:126-129</c>), both testing the route's <em>unit</em> id against <c>_user.GetId()</c>. Both are
/// reachable - see <see cref="Update_ForAUnitWhoseIdIsTheCallersOwnUserId_AndABodyNamingAnother_Is403"/>
/// and <see cref="Delete_ForTheCallersOwnUserId_Is403AboutAnAccount"/> - so a unit whose id happens to
/// equal some user's id can be deleted by everybody except that user, who is told they are about to
/// delete their own account.
/// </para>
/// <para>
/// <strong><c>ManageUnits</c> does not imply <c>ViewUnits</c>, and the create's <c>Location</c> header
/// does not know that.</strong> <c>createUnit</c> answers a header pointing at <c>getUnit</c>, which
/// requires <c>ViewUnits</c>, so the caller who just created the unit is answered 403 by the header they
/// were handed (<see cref="Create_WithManageUnitsOnly_CannotFollowItsOwnLocationHeader"/>). Same shape as
/// <c>createTeam</c>'s header in <c>4dd5201</c>, reached by a system permission rather than a MSEL role.
/// </para>
/// <para>
/// <strong>Nothing here marks a MSEL modified.</strong> Renaming or deleting a unit changes what every
/// MSEL it is assigned to looks like and no write path calls
/// <c>ServiceUtilities.SetMselModifiedAsync</c> - see
/// <see cref="Update_DoesNotMarkTheMselsTheUnitIsOnModified"/>. The handler does broadcast to each of
/// those MSELs' groups, so a client that is listening is told and a client that reloads from
/// <c>DateModified</c> is not.
/// </para>
/// <para>
/// <strong>The download and the upload speak a dialect of their own.</strong> Both build their own
/// <c>JsonSerializerOptions</c> rather than using the MVC ones, so the file is PascalCase behind
/// <c>ReferenceHandler.Preserve</c>'s <c>$id</c>/<c>$values</c> wrapper where every response is camelCase
/// - the same split recorded for cards in <c>b5d2d86</c> and for inject types in <c>3437aed</c>. Here the
/// two halves at least agree with each other, so a downloaded file uploads
/// (<see cref="DownloadJson_RoundTripsThroughUploadJson"/>).
/// </para>
/// <para>
/// Both of the controller's audit assignments are dead: <c>:133</c>'s <c>unit.CreatedBy</c> and
/// <c>:157</c>'s <c>unit.ModifiedBy</c> are re-set by the service at <c>:93</c> and <c>:115</c>. The
/// values are the same, so nothing observable changes and the tests that pin the stamping
/// (<see cref="Create_StampsCreatedByOnTheServer"/>,
/// <see cref="Update_StampsModifiedByOnTheServer"/>) cannot tell which line did it - which is the point:
/// deleting either one is invisible.
/// </para>
/// </remarks>
public class UnitEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET units
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetAll_ReturnsEveryUnitInTheInstallation()
    {
        var first = await SeedUnit();
        var second = await SeedUnit();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var units = await GetRows(Client(actor), Units);

        Assert.Equal(new[] { first.Id, second.Id }.Order(), units.Select(x => x.Id).Order());
    }

    /// <remarks>
    /// The headline finding. There is no <c>AuthorizeAsync</c> call on this action at all, so an actor
    /// holding no system permission and belonging to no unit reads the whole list - including units they
    /// are not in, which is what makes it a disclosure rather than a convenience. The route's remarks say
    /// "Only accessible to a SuperUser". Adding a <c>ViewUnits</c> check turns this red.
    /// </remarks>
    [Fact]
    public async Task GetAll_ForACallerWithNoPermissionAtAll_Is200()
    {
        var unit = await SeedUnit();
        var actor = await Actor().SeedAsync();

        var units = await GetRows(Client(actor), Units);

        Assert.Equal(unit.Id, Assert.Single(units).Id);
    }

    [Fact]
    public async Task GetAll_AnswersAnEmptyUsersListForAUnitThatHasMembers()
    {
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        await Seed(BlueprintAppFactory.UnitUser(member.Id, unit.Id));
        var actor = await Actor().SeedAsync();

        var units = await GetRows(Client(actor), Units);

        Assert.Empty(Assert.Single(units).Users);
    }

    // ---------------------------------------------------------------------------------------------
    // GET my-units
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetMine_ReturnsOnlyTheCallersUnits()
    {
        var mine = await SeedUnit();
        var theirs = await SeedUnit();
        var actor = await Actor().SeedAsync();
        var other = await Actor().SeedAsync();
        await Seed(
            BlueprintAppFactory.UnitUser(actor.Id, mine.Id),
            BlueprintAppFactory.UnitUser(other.Id, theirs.Id));

        var units = await GetRows(Client(actor), $"{Api}/my-units");

        Assert.Equal(mine.Id, Assert.Single(units).Id);
    }

    /// <remarks>
    /// Seeding a membership that must <em>not</em> appear is what makes this test killable: a fresh
    /// per-test database has nothing else to return, so an empty-list assertion against an empty database
    /// survives every mutation of the filter - the lesson recorded in <c>4dd5201</c>.
    /// </remarks>
    [Fact]
    public async Task GetMine_ForACallerInNoUnit_IsAnEmptyList()
    {
        var unit = await SeedUnit();
        var other = await Actor().SeedAsync();
        await Seed(BlueprintAppFactory.UnitUser(other.Id, unit.Id));
        var actor = await Actor().SeedAsync();

        Assert.Empty(await GetRows(Client(actor), $"{Api}/my-units"));
    }

    /// <remarks>
    /// Correct, unlike <see cref="GetAll_ForACallerWithNoPermissionAtAll_Is200"/>: this route is scoped to
    /// the caller's own memberships, so requiring nothing is the right answer rather than an omission.
    /// </remarks>
    [Fact]
    public async Task GetMine_ForACallerWithNoPermissionAtAll_Is200()
    {
        var unit = await SeedUnit();
        var actor = await Actor().SeedAsync();
        await Seed(BlueprintAppFactory.UnitUser(actor.Id, unit.Id));

        Assert.Equal(unit.Id, Assert.Single(await GetRows(Client(actor), $"{Api}/my-units")).Id);
    }

    /// <remarks>
    /// <c>GetMineAsync</c> <c>Include</c>s the unit before projecting to it, which makes no difference to
    /// the answer - the projection loads it either way - and does not reach <c>UnitUsers</c>, so the
    /// caller cannot see their own unit's membership either. <c>GetByUserAsync</c> omits the redundant
    /// <c>Include</c> and answers identically.
    /// </remarks>
    [Fact]
    public async Task GetMine_AnswersAnEmptyUsersListForTheCallersOwnUnit()
    {
        var unit = await SeedUnit();
        var actor = await Actor().SeedAsync();
        await Seed(BlueprintAppFactory.UnitUser(actor.Id, unit.Id));

        Assert.Empty(Assert.Single(await GetRows(Client(actor), $"{Api}/my-units")).Users);
    }

    // ---------------------------------------------------------------------------------------------
    // GET users/{userId}/units
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByUser_ReturnsTheUsersUnits()
    {
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        await Seed(BlueprintAppFactory.UnitUser(member.Id, unit.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var units = await GetRows(Client(actor), UnitsOfUser(member.Id));

        Assert.Equal(unit.Id, Assert.Single(units).Id);
    }

    [Fact]
    public async Task GetByUser_ForAUserThatIsNotThere_IsAnEmptyList()
    {
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        await Seed(BlueprintAppFactory.UnitUser(member.Id, unit.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        Assert.Empty(await GetRows(Client(actor), UnitsOfUser(Guid.NewGuid())));
    }

    [Fact]
    public async Task GetByUser_WithViewUnitsOnly_Is200()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var response = await Client(actor).GetAsync(UnitsOfUser(actor.Id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetByUser_WithoutViewUnits_Is403()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUsers).SeedAsync();

        var response = await Client(actor).GetAsync(UnitsOfUser(actor.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// A caller may not list their own memberships through this route without <c>ViewUnits</c>, although
    /// <c>GET my-units</c> answers exactly that question for nothing. Two routes, one question, two
    /// answers.
    /// </remarks>
    [Fact]
    public async Task GetByUser_ForTheCallersOwnIdWithoutViewUnits_Is403()
    {
        var unit = await SeedUnit();
        var actor = await Actor().SeedAsync();
        await Seed(BlueprintAppFactory.UnitUser(actor.Id, unit.Id));

        var response = await Client(actor).GetAsync(UnitsOfUser(actor.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(await GetRows(Client(actor), $"{Api}/my-units"));
    }

    // ---------------------------------------------------------------------------------------------
    // GET units/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ReturnsTheUnit()
    {
        var unit = await SeedUnit();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var answered = await GetRow(Client(actor), unit.Id);

        Assert.Equal(unit.Name, answered.Name);
        Assert.Equal("unit", answered.ShortName);
    }

    /// <remarks>
    /// <c>GetAsync(id)</c> is <c>SingleOrDefaultAsync</c>, so the controller's null check at
    /// <c>UnitController.cs:109</c> is live and an unknown id is a clean 404. This is the shape the four
    /// services carrying <c>SingleAsync</c> plus a dead null check should have copied - see the sweep
    /// recorded against <c>OrganizationService</c>, <c>MoveService</c>, <c>CardService</c> and
    /// <c>InjectService</c>.
    /// </remarks>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var response = await Client(actor).GetAsync(UnitRoute(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_WithViewUnitsOnly_Is200()
    {
        var unit = await SeedUnit();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var response = await Client(actor).GetAsync(UnitRoute(unit.Id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_WithoutViewUnits_Is403()
    {
        var unit = await SeedUnit();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var response = await Client(actor).GetAsync(UnitRoute(unit.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// The route's remarks promise it is "Accessible to a SuperUser or a User that is a member of a Unit
    /// within the specified Unit", and the second half is not implemented: the service takes no
    /// permission argument and the controller asks for <c>ViewUnits</c> outright. So a member of the unit
    /// cannot read it by id, although <c>GET units</c> - which requires nothing - lists it for them.
    /// Implementing the promised membership check turns this red.
    /// </remarks>
    [Fact]
    public async Task Get_ForAMemberOfTheUnitWithoutViewUnits_Is403_ThoughTheListRouteShowsItToThem()
    {
        var unit = await SeedUnit();
        var actor = await Actor().SeedAsync();
        await Seed(BlueprintAppFactory.UnitUser(actor.Id, unit.Id));

        var response = await Client(actor).GetAsync(UnitRoute(unit.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(unit.Id, Assert.Single(await GetRows(Client(actor), Units)).Id);
    }

    [Fact]
    public async Task Get_AnswersAnEmptyUsersListForAUnitThatHasMembers()
    {
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        await Seed(BlueprintAppFactory.UnitUser(member.Id, unit.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        Assert.Empty((await GetRow(Client(actor), unit.Id)).Users);
    }

    // ---------------------------------------------------------------------------------------------
    // POST units
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_StoresTheUnitAndAnswers201()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Post(Client(actor), Body() with { Name = "Blue Cell", ShortName = "blue" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await Read<ViewModels.Unit>(response);
        var stored = await Stored(created.Id);

        Assert.Equal("Blue Cell", stored.Name);
        Assert.Equal("blue", stored.ShortName);
    }

    [Fact]
    public async Task Create_AnswersALocationHeaderPointingAtTheUnit()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Post(Client(actor), Body());
        var created = await Read<ViewModels.Unit>(response);

        Assert.EndsWith($"/api/units/{created.Id}", response.Headers.Location.ToString());
    }

    /// <remarks>
    /// <c>createUnit</c> requires <c>ManageUnits</c> and its <c>Location</c> header names <c>getUnit</c>,
    /// which requires <c>ViewUnits</c> - a permission <c>ManageUnits</c> does not include. So the caller
    /// who just created the unit is answered 403 by the header they were handed. Same shape as
    /// <c>createTeam</c>'s header in <c>4dd5201</c>. Either granting <c>ManageUnits</c> holders the read
    /// or pointing the header at a route they may use turns this red.
    /// </remarks>
    [Fact]
    public async Task Create_WithManageUnitsOnly_CannotFollowItsOwnLocationHeader()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Post(Client(actor), Body());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var followed = await Client(actor).GetAsync(response.Headers.Location, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, followed.StatusCode);
    }

    [Fact]
    public async Task Create_WithoutManageUnits_Is403()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var response = await Post(Client(actor), Body());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await StoredUnits());
    }

    /// <remarks>
    /// The body's <c>createdBy</c> and <c>dateCreated</c> are both ignored: the service assigns the
    /// caller's id at <c>:93</c> and <c>BlueprintContext.SaveEntries</c> stamps the date. The controller's
    /// own assignment at <c>:133</c> is therefore dead code - deleting it changes nothing, which is why
    /// this test cannot tell the two apart.
    /// </remarks>
    [Fact]
    public async Task Create_StampsCreatedByOnTheServer()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();
        var before = DateTime.UtcNow;

        var response = await Post(Client(actor), Body() with
        {
            CreatedBy = Guid.NewGuid(),
            DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });

        var stored = await Stored((await Read<ViewModels.Unit>(response)).Id);

        Assert.Equal(actor.Id, stored.CreatedBy);
        Assert.InRange(stored.DateCreated, before, DateTime.UtcNow);
        Assert.Null(stored.ModifiedBy);
    }

    [Fact]
    public async Task Create_KeepsTheBodysId()
    {
        var id = Guid.NewGuid();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Post(Client(actor), Body() with { Id = id });

        Assert.Equal(id, (await Read<ViewModels.Unit>(response)).Id);
        Assert.NotNull(await Stored(id));
    }

    /// <remarks>
    /// <c>UnitConfiguration</c> declares nothing but a unique index on the primary key, so two units may
    /// share a name and a short name and nothing in the API distinguishes them - where a duplicate inject
    /// type name is a 500 from its own unique index (<c>3437aed</c>). Which of two identically named
    /// units a person is in is then a question only the ids can answer, and no route answers it in terms
    /// of units at all (<see cref="Get_AnswersAnEmptyUsersListForAUnitThatHasMembers"/>).
    /// </remarks>
    [Fact]
    public async Task Create_WithADuplicateNameAndShortName_Is201()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();
        var body = Body() with { Name = "Twice", ShortName = "twice" };

        var first = await Post(Client(actor), body);
        var second = await Post(Client(actor), body with { Id = Guid.Empty });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(2, (await StoredUnits()).Count(x => x.Name == "Twice"));
    }

    /// <remarks>
    /// <c>[SanitizeHtml]</c> is on <c>Description</c> and on neither name, so the interceptor strips the
    /// script from one field and stores it verbatim in the other two. The same asymmetry as
    /// <c>MoveEntity</c>'s in <c>997a9c4</c>, where the attribute is on <c>SituationDescription</c> and
    /// not on <c>Description</c>.
    /// </remarks>
    [Fact]
    public async Task Create_StripsHtmlFromTheDescriptionAndKeepsItInTheNames()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Post(Client(actor), Body() with
        {
            Name = "<script>alert(1)</script>Named",
            ShortName = "<b>short</b>",
            Description = "<script>alert(1)</script>Described"
        });

        var stored = await Stored((await Read<ViewModels.Unit>(response)).Id);

        Assert.Equal("<script>alert(1)</script>Named", stored.Name);
        Assert.Equal("<b>short</b>", stored.ShortName);
        Assert.Equal("Described", stored.Description);
    }

    /// <remarks>
    /// <c>UnitHandler.GetGroupsAsync</c> names the unit's own id, the admin data group and every MSEL the
    /// unit is assigned to. A brand new unit is on no MSEL, so a create reaches two groups; an update
    /// reaches one per assignment as well (<see cref="Update_BroadcastsToEveryMselTheUnitIsOn"/>).
    /// </remarks>
    [Fact]
    public async Task Create_BroadcastsToTheUnitAndTheAdminDataGroup()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Post(Client(actor), Body());
        var created = await Read<ViewModels.Unit>(response);

        Assert.Equal(
            new[] { created.Id.ToString(), MainHub.ADMIN_DATA_GROUP }.Order(),
            Hub.Recipients(MainHubMethods.UnitCreated).Order());
    }

    [Fact]
    public async Task Create_WithNoBody_Is400()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        using var content = new StringContent(string.Empty, Encoding.UTF8, "application/json");

        var response = await Client(actor).PostAsync(Units, content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT units/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_ChangesTheNameAndAnswers200()
    {
        var unit = await SeedUnit();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Put(Client(actor), unit.Id, BodyFor(unit) with { Name = "Renamed" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Renamed", (await Read<ViewModels.Unit>(response)).Name);
        Assert.Equal("Renamed", (await Stored(unit.Id)).Name);
    }

    [Fact]
    public async Task Update_ForAnIdThatIsNotThere_Is404()
    {
        var id = Guid.NewGuid();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Put(Client(actor), id, Body() with { Id = id });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_WithManageUnitsOnly_Is200()
    {
        var unit = await SeedUnit();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Put(Client(actor), unit.Id, BodyFor(unit));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Update_WithoutManageUnits_Is403()
    {
        var unit = await SeedUnit();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var response = await Put(Client(actor), unit.Id, BodyFor(unit) with { Name = "Renamed" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual("Renamed", (await Stored(unit.Id)).Name);
    }

    /// <remarks>
    /// <c>UnitProfile</c>'s <c>Unit</c> to <c>UnitEntity</c> map includes <c>Id</c> by convention, and
    /// <c>UpdateAsync</c> maps the body onto the <em>tracked</em> row, so a body that does not echo the
    /// id back writes <c>Guid.Empty</c> over a key EF is tracking and the save throws - a 500, not a 400.
    /// The same defect as <c>InjectService</c>'s in <c>3437aed</c>: a PUT is unusable by any client that
    /// does not send the whole row. Ignoring <c>Id</c> on that map turns this red and makes the route
    /// usable.
    /// </remarks>
    [Fact]
    public async Task Update_ThatOmitsTheId_Is500()
    {
        var unit = await SeedUnit();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Put(Client(actor), unit.Id, Body() with { Name = "Renamed" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.NotEqual("Renamed", (await Stored(unit.Id)).Name);
    }

    /// <remarks>
    /// <c>SaveEntries</c> stamps <c>DateModified</c> and never touches <c>ModifiedBy</c>, so what makes
    /// this route record the modifier is the service's own assignment at <c>:115</c> - which
    /// <c>TeamService</c> does not have, and where a PUT therefore lets the client say who made the
    /// change (<c>4dd5201</c>). This is the positive control for that finding.
    /// </remarks>
    [Fact]
    public async Task Update_StampsModifiedByOnTheServer()
    {
        var unit = await SeedUnit();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();
        var before = DateTime.UtcNow;

        await Put(Client(actor), unit.Id, BodyFor(unit) with
        {
            ModifiedBy = Guid.NewGuid(),
            DateModified = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });

        var stored = await Stored(unit.Id);

        Assert.Equal(actor.Id, stored.ModifiedBy);
        Assert.NotNull(stored.DateModified);
        Assert.InRange(stored.DateModified.Value, before, DateTime.UtcNow);
    }

    [Fact]
    public async Task Update_PreservesTheCreationFields()
    {
        var creator = Guid.NewGuid();
        var unit = BlueprintAppFactory.Unit(createdBy: creator);
        await Seed(unit);
        var created = (await Stored(unit.Id)).DateCreated;
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        await Put(Client(actor), unit.Id, BodyFor(unit) with
        {
            CreatedBy = Guid.NewGuid(),
            DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });

        var stored = await Stored(unit.Id);

        Assert.Equal(creator, stored.CreatedBy);
        Assert.Equal(created, stored.DateCreated);
    }

    /// <remarks>
    /// <c>UnitService.cs:104-108</c> is a guard copy-pasted from <c>UserService</c>: it compares the
    /// route's <em>unit</em> id against the caller's <em>user</em> id, and then the body's id against the
    /// route's, refusing with "You cannot change your own Id". The two id spaces are unrelated, so the
    /// only request it can refuse is one whose route id happens to equal the caller's user id <em>and</em>
    /// whose body names some other id - which is exactly the request
    /// <see cref="Update_ThatOmitsTheId_Is500"/> shows is a 500 for everybody else, so the guard's whole
    /// effect is to turn one caller's 500 into a 403 with a message about an id space the route does not
    /// use. All three halves are asserted here because the fact is the contrast: a mismatched body is 403
    /// for that one caller, 500 for anybody else, and a body echoing the id is a plain 200 even for them.
    /// Note the guard fires before the row is looked up, so it answers 403 whether or not the unit exists.
    /// Deleting the guard makes the first request a 500 like the second.
    /// </remarks>
    [Fact]
    public async Task Update_ForAUnitWhoseIdIsTheCallersOwnUserId_AndABodyNamingAnother_Is403()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();
        var unit = BlueprintAppFactory.Unit();
        unit.Id = actor.Id;
        await Seed(unit);
        var other = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var refused = await Put(Client(actor), unit.Id, BodyFor(unit) with { Id = Guid.NewGuid() });
        var forEverybodyElse = await Put(Client(other), unit.Id, BodyFor(unit) with { Id = Guid.NewGuid() });
        var echoed = await Put(Client(actor), unit.Id, BodyFor(unit) with { Name = "Renamed" });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("You cannot change your own Id", await refused.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.InternalServerError, forEverybodyElse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, echoed.StatusCode);
        Assert.Equal("Renamed", (await Stored(unit.Id)).Name);
    }

    /// <remarks>
    /// No write path in this service calls <c>ServiceUtilities.SetMselModifiedAsync</c>, so renaming a
    /// unit leaves every MSEL it is assigned to claiming it has not changed since before the rename -
    /// while the unit's name is what those MSELs display. The fourth service in this tier with that gap
    /// after <c>CardService</c>, <c>CardTeamService</c>, <c>InjectService</c>, <c>TeamService</c> and
    /// <c>TeamUserService</c>. Adding the call turns this red.
    /// </remarks>
    [Fact]
    public async Task Update_DoesNotMarkTheMselsTheUnitIsOnModified()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        await Seed(BlueprintAppFactory.MselUnit(unit.Id, msel.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        await Put(Client(actor), unit.Id, BodyFor(unit) with { Name = "Renamed" });

        Assert.Null((await StoredMsel(msel.Id)).DateModified);
    }

    /// <remarks>
    /// What a listening client does get is a broadcast per MSEL, so the information reaches a live UI and
    /// not a reloading one - the contrast with
    /// <see cref="Update_DoesNotMarkTheMselsTheUnitIsOnModified"/>, and the reason that gap is easy to
    /// miss.
    /// </remarks>
    [Fact]
    public async Task Update_BroadcastsToEveryMselTheUnitIsOn()
    {
        var first = await SeedMsel();
        var second = await SeedMsel();
        var unit = await SeedUnit();
        await Seed(
            BlueprintAppFactory.MselUnit(unit.Id, first.Id),
            BlueprintAppFactory.MselUnit(unit.Id, second.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        await Put(Client(actor), unit.Id, BodyFor(unit) with { Name = "Renamed" });

        Assert.Equal(
            new[] { unit.Id.ToString(), MainHub.ADMIN_DATA_GROUP, first.Id.ToString(), second.Id.ToString() }.Order(),
            Hub.Recipients(MainHubMethods.UnitUpdated).Order());
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE units/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_RemovesTheUnitAndAnswers204()
    {
        var unit = await SeedUnit();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Client(actor).DeleteAsync(UnitRoute(unit.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await Stored(unit.Id));
    }

    [Fact]
    public async Task Delete_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Client(actor).DeleteAsync(UnitRoute(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_WithoutManageUnits_Is403()
    {
        var unit = await SeedUnit();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var response = await Client(actor).DeleteAsync(UnitRoute(unit.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(await Stored(unit.Id));
    }

    /// <remarks>
    /// <c>UnitService.cs:126-129</c> is the second guard copy-pasted from <c>UserService</c>, and this
    /// one needs no unit to exist at all: any caller deleting the unit id that happens to equal their own
    /// user id is told "You cannot delete your own account" on a route that deletes units. Deleting the
    /// guard makes this a 404.
    /// </remarks>
    [Fact]
    public async Task Delete_ForTheCallersOwnUserId_Is403AboutAnAccount()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Client(actor).DeleteAsync(UnitRoute(actor.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(
            "You cannot delete your own account", await response.Content.ReadAsStringAsync(Ct));
    }

    /// <remarks>
    /// <c>MselUnitConfiguration</c> cascades from the unit, so one DELETE silently removes the unit from
    /// every MSEL it was assigned to - and nothing withdraws the <c>UserMselRoleEntity</c> rows that
    /// <c>MselUnitService.CreateAsync</c> granted when it was added, so the roles survive with no unit to
    /// reach the MSEL through. Phase 2 established that such a role is a no-op in every
    /// <c>Msel*Requirement</c>, so what is left behind is an exercise whose member list says a person has
    /// a role and whose permission checks all refuse them. There is no dependent count and no 409.
    /// </remarks>
    [Fact]
    public async Task Delete_DropsTheUnitFromEveryMselAndLeavesTheRolesBehind()
    {
        var msel = await SeedMsel();
        var member = await Actor().SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();
        var unit = await SeedUnit();
        await Seed(
            BlueprintAppFactory.UnitUser(member.Id, unit.Id),
            BlueprintAppFactory.MselUnit(unit.Id, msel.Id));
        await Db.AddMselRoleAsync(member.Id, msel.Id, MselRole.Viewer, Ct);

        var response = await Client(actor).DeleteAsync(UnitRoute(unit.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var context = NewContext();

        Assert.Empty(await context.MselUnits.Where(x => x.MselId == msel.Id).ToListAsync(Ct));
        Assert.Empty(await context.UnitUsers.Where(x => x.UnitId == unit.Id).ToListAsync(Ct));
        Assert.Single(await context.UserMselRoles.Where(x => x.MselId == msel.Id).ToListAsync(Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // POST units/json
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task UploadJson_CreatesTheUnitsAndAnswers200()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await UploadJson(Client(actor), """
            [{ "Name": "Imported", "ShortName": "imp", "Description": "From a file" }]
            """);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var created = Assert.Single(await Read<List<ViewModels.Unit>>(response));
        var stored = await Stored(created.Id);

        Assert.Equal("Imported", stored.Name);
        Assert.Equal("From a file", stored.Description);
    }

    /// <remarks>
    /// <c>UploadJsonAsync</c> overwrites the id, the member list and all four audit fields whatever the
    /// file says, so an import cannot claim a unit was created by somebody else or overwrite an existing
    /// one. The same hardening <c>OrganizationService</c>'s upload has, and the reason a downloaded file
    /// can be re-uploaded rather than being rejected as a duplicate.
    /// </remarks>
    [Fact]
    public async Task UploadJson_ForcesAFreshIdAndTheCallerAsCreator()
    {
        var id = Guid.NewGuid();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();
        var before = DateTime.UtcNow;

        var response = await UploadJson(Client(actor), $$"""
            [{
              "Id": "{{id}}",
              "Name": "Imported",
              "CreatedBy": "{{Guid.NewGuid()}}",
              "DateCreated": "1999-01-01T00:00:00Z",
              "ModifiedBy": "{{Guid.NewGuid()}}",
              "DateModified": "1999-01-01T00:00:00Z"
            }]
            """);

        var created = Assert.Single(await Read<List<ViewModels.Unit>>(response));

        Assert.NotEqual(id, created.Id);
        Assert.Null(await Stored(id));

        var stored = await Stored(created.Id);

        Assert.Equal(actor.Id, stored.CreatedBy);
        Assert.InRange(stored.DateCreated, before, DateTime.UtcNow);
        Assert.Null(stored.ModifiedBy);
        Assert.Null(stored.DateModified);
    }

    /// <remarks>
    /// The service empties <c>Users</c> on every incoming item, so a file naming members imports the unit
    /// and none of them - which is deliberate, and matches <c>DownloadJsonAsync</c> stripping the same
    /// collection on the way out (<see cref="DownloadJson_StripsTheMembers"/>). A unit's membership is
    /// therefore not portable by any route in the API.
    /// </remarks>
    [Fact]
    public async Task UploadJson_DropsTheUsersInTheFile()
    {
        var member = await Actor().SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await UploadJson(Client(actor), $$"""
            [{
              "Name": "Imported",
              "Users": [{ "Id": "{{member.Id}}", "Name": "{{member.Name}}" }]
            }]
            """);

        var created = Assert.Single(await Read<List<ViewModels.Unit>>(response));

        Assert.Empty(created.Users);

        await using var context = NewContext();

        Assert.Empty(await context.UnitUsers.Where(x => x.UnitId == created.Id).ToListAsync(Ct));
    }

    [Fact]
    public async Task UploadJson_WithoutManageUnits_Is403()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var response = await UploadJson(Client(actor), """[{ "Name": "Imported" }]""");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await StoredUnits());
    }

    /// <remarks>
    /// <c>FileForm.ToUpload</c> carries <c>[Required]</c>, so a multipart body with no file part is a 400
    /// from <c>ValidateModelStateFilter</c> and never reaches the service that would have dereferenced
    /// null. The part under another name is what makes this a 400 from the filter rather than from the
    /// form reader - the distinction recorded for the data-option unit in <c>3c31930</c>.
    /// </remarks>
    [Fact]
    public async Task UploadJson_WithNoFilePart_Is400()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("not the file"), "Something");

        var response = await Client(actor).PostAsync($"{Units}/json", content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <remarks>
    /// A <c>JsonException</c> is not an <c>IApiException</c>, so <c>JsonExceptionFilter</c> answers 500
    /// rather than the 400 a malformed upload deserves. Nothing distinguishes "your file is not JSON"
    /// from "the server broke".
    /// </remarks>
    [Fact]
    public async Task UploadJson_ThatIsNotJson_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await UploadJson(Client(actor), "not json at all");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(await StoredUnits());
    }

    // ---------------------------------------------------------------------------------------------
    // POST units/json/download
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task DownloadJson_ReturnsAnAttachmentNamedUnitExport()
    {
        var unit = await SeedUnit();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Download(Client(actor), unit.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType.MediaType);
        Assert.Equal(
            "unit-export.json", response.Content.Headers.ContentDisposition.FileName.Trim('"'));
    }

    [Fact]
    public async Task DownloadJson_ReturnsOnlyTheRequestedUnits()
    {
        var wanted = BlueprintAppFactory.Unit(name: "Wanted");
        await Seed(wanted, BlueprintAppFactory.Unit(name: "Unwanted"));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var json = await (await Download(Client(actor), wanted.Id)).Content.ReadAsStringAsync(Ct);

        Assert.Contains("Wanted", json);
        Assert.DoesNotContain("Unwanted", json);
    }

    [Fact]
    public async Task DownloadJson_ForAnIdThatIsNotThere_IsAnEmptyExport()
    {
        await Seed(BlueprintAppFactory.Unit(name: "Unwanted"));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var json = await (await Download(Client(actor), Guid.NewGuid())).Content.ReadAsStringAsync(Ct);

        Assert.DoesNotContain("Unwanted", json);
        Assert.Empty(Exported(json));
    }

    /// <remarks>
    /// The comment above <c>UnitService.cs:150</c> records the choice - "the user explicitly asked Users
    /// be excluded from Unit exports" - so this is the one deliberate omission in the file rather than a
    /// missing <c>Include</c>. It is also moot: <c>GetAsync</c> never loads <c>UnitUsers</c>, so the
    /// collection the loop clears was already empty.
    /// </remarks>
    [Fact]
    public async Task DownloadJson_StripsTheMembers()
    {
        var unit = await SeedUnit();
        var member = await Actor().WithName("Exported Member").SeedAsync();
        await Seed(BlueprintAppFactory.UnitUser(member.Id, unit.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var json = await (await Download(Client(actor), unit.Id)).Content.ReadAsStringAsync(Ct);

        Assert.DoesNotContain("Exported Member", json);
        Assert.Empty(Exported(json).Single().GetProperty("Users").GetProperty("$values").EnumerateArray());
    }

    /// <remarks>
    /// <c>ReferenceHandler.Preserve</c> wraps the list in <c>$id</c>/<c>$values</c> and the names are
    /// PascalCase, because the service builds its own options rather than using the MVC ones - so no
    /// response in the API is shaped like this file. Pinned because it is a wire format two ways: a human
    /// editing an export sees it, and <c>UploadJsonAsync</c> has to keep reading it.
    /// </remarks>
    [Fact]
    public async Task DownloadJson_WrapsThePascalCaseListForReferencePreservation()
    {
        var unit = await SeedUnit();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var json = await (await Download(Client(actor), unit.Id)).Content.ReadAsStringAsync(Ct);

        using var document = JsonDocument.Parse(json);

        Assert.True(document.RootElement.TryGetProperty("$id", out _));
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("$values").ValueKind);
        Assert.True(
            document.RootElement.GetProperty("$values")[0].TryGetProperty("ShortName", out _),
            "the export is PascalCase, where every response in the API is camelCase");
    }

    [Fact]
    public async Task DownloadJson_WithoutManageUnits_Is403()
    {
        var unit = await SeedUnit();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var response = await Download(Client(actor), unit.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DownloadJson_RoundTripsThroughUploadJson()
    {
        var unit = BlueprintAppFactory.Unit(name: "Round Tripped");
        unit.Description = "kept";
        await Seed(unit);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var downloaded = await Download(Client(actor), unit.Id);
        var response = await UploadJson(
            Client(actor), await downloaded.Content.ReadAsStringAsync(Ct));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var created = Assert.Single(await Read<List<ViewModels.Unit>>(response));

        Assert.Equal("Round Tripped", created.Name);
        Assert.Equal("kept", created.Description);
        Assert.NotEqual(unit.Id, created.Id);
    }

    // ---------------------------------------------------------------------------------------------
    // Authentication
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "units")]
    [InlineData("GET", "my-units")]
    [InlineData("GET", "users/00000000-0000-0000-0000-000000000001/units")]
    [InlineData("GET", "units/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "units")]
    [InlineData("PUT", "units/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "units/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "units/json/download")]
    public async Task EveryRouteRefusesAnUnauthenticatedRequest(string method, string route)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/{route}")
        {
            Content = JsonContent.Create(new { })
        };

        var response = await AnonymousClient.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <remarks>
    /// <c>POST units/json</c> is missing from the sweep above because it binds a <c>FileForm</c>, so MVC
    /// infers <c>[Consumes("multipart/form-data")]</c> and a JSON body is refused during endpoint
    /// selection - before <c>AuthorizationMiddleware</c> runs. Sent as multipart it answers 401 like its
    /// siblings, which is what this test asserts; the bare-<c>IFormFile</c> routes recorded in
    /// <c>3c31930</c> answer 415 even then.
    /// </remarks>
    [Fact]
    public async Task UploadJson_AnonymouslyIs401()
    {
        var response = await UploadJson(AnonymousClient, """[{ "Name": "Imported" }]""");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------

    private const string Api = "/api";

    private const string Units = "/api/units";

    private static string UnitRoute(Guid id) => $"{Units}/{id}";

    private static string UnitsOfUser(Guid userId) => $"{Api}/users/{userId}/units";

    /// <summary>
    /// The wire shape of a unit. <c>Id</c> is writable through <c>UnitProfile</c>'s convention map, which
    /// is what <see cref="Update_ThatOmitsTheId_Is500"/> turns on, and the four audit fields exist on both
    /// sides so a body can try to spoof them.
    /// </summary>
    private sealed record UnitBody
    {
        public Guid Id { get; init; }
        public string Name { get; init; }
        public string ShortName { get; init; }
        public string Description { get; init; }

        public Guid CreatedBy { get; init; }
        public DateTime DateCreated { get; init; }
        public Guid? ModifiedBy { get; init; }
        public DateTime? DateModified { get; init; }
    }

    private static UnitBody Body() => new()
    {
        Name = "A unit",
        ShortName = "unit",
        Description = "Posted by UnitEndpointTests"
    };

    private static UnitBody BodyFor(UnitEntity unit) => new()
    {
        Id = unit.Id,
        Name = unit.Name,
        ShortName = unit.ShortName,
        Description = unit.Description
    };

    private async Task<UnitEntity> SeedUnit()
    {
        var unit = BlueprintAppFactory.Unit();
        await Seed(unit);

        return unit;
    }

    private async Task<MselEntity> SeedMsel()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        return msel;
    }

    private Task<HttpResponseMessage> Post(HttpClient client, UnitBody body) =>
        client.PostAsJsonAsync(Units, body, Ct);

    private Task<HttpResponseMessage> Put(HttpClient client, Guid id, UnitBody body) =>
        client.PutAsJsonAsync(UnitRoute(id), body, Ct);

    private Task<HttpResponseMessage> Download(HttpClient client, params Guid[] ids) =>
        client.PostAsJsonAsync($"{Units}/json/download", ids, Ct);

    /// <remarks>
    /// The <c>await</c> before the <c>using</c> falls out of scope is load-bearing: <c>TestServer</c>
    /// reads the request body inside <c>SendAsync</c>, so returning the task unawaited disposes the
    /// content first and every upload test fails with <c>ObjectDisposedException</c> rather than whatever
    /// it was asserting.
    /// </remarks>
    private async Task<HttpResponseMessage> UploadJson(HttpClient client, string json)
    {
        using var content = new MultipartFormDataContent();

        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "ToUpload", "units.json");

        return await client.PostAsync($"{Units}/json", content, Ct);
    }

    /// <summary>
    /// The items of a download, unwrapped from <c>ReferenceHandler.Preserve</c>'s
    /// <c>$id</c>/<c>$values</c> envelope.
    /// </summary>
    private static List<JsonElement> Exported(string json)
    {
        using var document = JsonDocument.Parse(json);

        // Clone each item: the elements are views onto the document, which is disposed on the way out.
        return [.. document.RootElement.GetProperty("$values").EnumerateArray().Select(x => x.Clone())];
    }

    private async Task<List<ViewModels.Unit>> GetRows(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<List<ViewModels.Unit>>(response);
    }

    private async Task<ViewModels.Unit> GetRow(HttpClient client, Guid id)
    {
        var response = await client.GetAsync(UnitRoute(id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<ViewModels.Unit>(response);
    }

    private async Task<UnitEntity> Stored(Guid id)
    {
        await using var context = NewContext();

        return await context.Units.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, Ct);
    }

    private async Task<List<UnitEntity>> StoredUnits()
    {
        await using var context = NewContext();

        return await context.Units.AsNoTracking().ToListAsync(Ct);
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
