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
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary><c>UnitService</c> / <c>UnitController</c> - the nine routes behind the reusable group of
/// people that gets assigned to a MSEL, as against a team, which belongs to one MSEL and cannot be
/// reused.</summary>
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

    /// <summary>Get all for a caller with no permission at all is answered with a 200.</summary>
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
        var member = await Actor().InUnit(unit).SeedAsync();
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
            TestData.UnitUser(actor.Id, mine.Id),
            TestData.UnitUser(other.Id, theirs.Id));

        var units = await GetRows(Client(actor), $"{Api}/my-units");

        Assert.Equal(mine.Id, Assert.Single(units).Id);
    }

    /// <remarks>
    /// Seeding a membership that must <em>not</em> appear is what makes this test killable: a fresh
    /// per-test database has nothing else to return, so an empty-list assertion against an empty database
    /// survives every mutation of the filter.
    /// </remarks>
    [Fact]
    public async Task GetMine_ForACallerInNoUnit_IsAnEmptyList()
    {
        var unit = await SeedUnit();
        var other = await Actor().InUnit(unit).SeedAsync();
        var actor = await Actor().SeedAsync();

        Assert.Empty(await GetRows(Client(actor), $"{Api}/my-units"));
    }

    /// <summary><c>GET my-units</c> is scoped to the caller's own memberships and needs no
    /// permission.</summary>
    [Fact]
    public async Task GetMine_ForACallerWithNoPermissionAtAll_Is200()
    {
        var unit = await SeedUnit();
        var actor = await Actor().InUnit(unit).SeedAsync();

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
        var actor = await Actor().InUnit(unit).SeedAsync();

        Assert.Empty(Assert.Single(await GetRows(Client(actor), $"{Api}/my-units")).Users);
    }

    // ---------------------------------------------------------------------------------------------
    // GET users/{userId}/units
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByUser_ReturnsTheUsersUnits()
    {
        var unit = await SeedUnit();
        var member = await Actor().InUnit(unit).SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var units = await GetRows(Client(actor), UnitsOfUser(member.Id));

        Assert.Equal(unit.Id, Assert.Single(units).Id);
    }

    [Fact]
    public async Task GetByUser_ForAUserThatIsNotThere_IsAnEmptyList()
    {
        var unit = await SeedUnit();
        var member = await Actor().InUnit(unit).SeedAsync();
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
        var actor = await Actor().InUnit(unit).SeedAsync();

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

    /// <summary>An unknown unit is a 404 from the controller's own null check.</summary>
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

    /// <summary>Get for a member of the unit without view units is answered with a 403 though the list route shows it to them.</summary>
    [Fact]
    public async Task Get_ForAMemberOfTheUnitWithoutViewUnits_Is403_ThoughTheListRouteShowsItToThem()
    {
        var unit = await SeedUnit();
        var actor = await Actor().InUnit(unit).SeedAsync();

        var response = await Client(actor).GetAsync(UnitRoute(unit.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(unit.Id, Assert.Single(await GetRows(Client(actor), Units)).Id);
    }

    /// <summary>Get answers an empty users list for a unit that has members.</summary>
    [Fact]
    public async Task Get_AnswersAnEmptyUsersListForAUnitThatHasMembers()
    {
        var unit = await SeedUnit();
        var member = await Actor().InUnit(unit).SeedAsync();
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

    /// <summary>Create with manage units only cannot follow its own location header.</summary>
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

    /// <summary>A create ignores the body's <c>createdBy</c> and <c>dateCreated</c>.</summary>
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

    /// <summary>Two units may share a name and a short name.</summary>
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
    /// <c>MoveEntity</c>'s, where the attribute is on <c>SituationDescription</c> and
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
            Hub.Recipients(MainHubMethods.UnitCreated, created.Id).Order());
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

    /// <summary>Update that omits the id is answered with a 500.</summary>
    [Fact]
    public async Task Update_ThatOmitsTheId_Is500()
    {
        var unit = await SeedUnit();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Put(Client(actor), unit.Id, Body() with { Name = "Renamed" });

        Assert.Equal("The property 'UnitEntity.Id' is part of a key and so cannot be modified or marked as modified. To change the principal of an existing entity with an identifying foreign key, first delete the dependent and invoke 'SaveChanges', and then associate the dependent with the new principal.", (await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
        Assert.NotEqual("Renamed", (await Stored(unit.Id)).Name);
    }

    /// <summary>The update records the caller as <c>ModifiedBy</c> through the service's own assignment.</summary>
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
        var unit = TestData.Unit(createdBy: creator);
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

    /// <summary>Update for a unit whose id is the callers own user id and a body naming another is answered with a 403.</summary>
    [Fact]
    public async Task Update_ForAUnitWhoseIdIsTheCallersOwnUserId_AndABodyNamingAnother_Is403()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();
        var unit = TestData.Unit();
        unit.Id = actor.Id;
        await Seed(unit);

        var response = await Put(Client(actor), unit.Id, BodyFor(unit) with { Id = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("You cannot change your own Id", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Update_ForAUnitWhoseIdIsTheCallersOwnUserId_ThatKeepsTheId_RenamesIt()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();
        var unit = TestData.Unit();
        unit.Id = actor.Id;
        await Seed(unit);

        var response = await Put(Client(actor), unit.Id, BodyFor(unit) with { Name = "Renamed" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Renamed", (await Stored(unit.Id)).Name);
    }

    /// <summary>Update does not mark the MSELs the unit is on modified.</summary>
    [Fact]
    public async Task Update_DoesNotMarkTheMselsTheUnitIsOnModified()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        await Seed(TestData.MselUnit(unit.Id, msel.Id));
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
            TestData.MselUnit(unit.Id, first.Id),
            TestData.MselUnit(unit.Id, second.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        await Put(Client(actor), unit.Id, BodyFor(unit) with { Name = "Renamed" });

        Assert.Equal(
            new[] { unit.Id.ToString(), MainHub.ADMIN_DATA_GROUP, first.Id.ToString(), second.Id.ToString() }.Order(),
            Hub.Recipients(MainHubMethods.UnitUpdated, unit.Id, first.Id, second.Id).Order());
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

    /// <summary>Delete for the callers own user id is answered with a 403 about an account.</summary>
    [Fact]
    public async Task Delete_ForTheCallersOwnUserId_Is403AboutAnAccount()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Client(actor).DeleteAsync(UnitRoute(actor.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(
            "You cannot delete your own account", await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>Deleting a unit removes it from every MSEL by the cascade and leaves the roles its assignments
    /// granted.</summary>
    [Fact]
    public async Task Delete_DropsTheUnitFromEveryMselAndLeavesTheRolesBehind()
    {
        var msel = await SeedMsel();
        var member = await Actor().SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();
        var unit = await SeedUnit();
        await Seed(
            TestData.UnitUser(member.Id, unit.Id),
            TestData.MselUnit(unit.Id, msel.Id));
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

    /// <summary>An upload assigns a fresh id, no members and the caller as creator, whatever the file
    /// says.</summary>
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

    /// <summary>A multipart body with no file part is a 400 from <c>ValidateModelStateFilter</c>.</summary>
    [Fact]
    public async Task UploadJson_WithNoFilePart_Is400()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("not the file"), "Something");

        var response = await Client(actor).PostAsync($"{Units}/json", content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Upload JSON that is not JSON is answered with a 500.</summary>
    [Fact]
    public async Task UploadJson_ThatIsNotJson_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await UploadJson(Client(actor), "not json at all");

        Assert.Equal("'not json at all' is an invalid JSON literal. Expected the literal 'null'. Path: $ | LineNumber: 0 | BytePositionInLine: 1.", (await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
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
        var wanted = TestData.Unit(name: "Wanted");
        await Seed(wanted, TestData.Unit(name: "Unwanted"));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var json = await (await Download(Client(actor), wanted.Id)).Content.ReadAsStringAsync(Ct);

        Assert.Contains("Wanted", json);
        Assert.DoesNotContain("Unwanted", json);
    }

    [Fact]
    public async Task DownloadJson_ForAnIdThatIsNotThere_IsAnEmptyExport()
    {
        await Seed(TestData.Unit(name: "Unwanted"));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var json = await (await Download(Client(actor), Guid.NewGuid())).Content.ReadAsStringAsync(Ct);

        Assert.DoesNotContain("Unwanted", json);
        Assert.Empty(Exported(json));
    }

    /// <summary>The export leaves out the unit's members.</summary>
    [Fact]
    public async Task DownloadJson_StripsTheMembers()
    {
        var unit = await SeedUnit();
        var member = await Actor().WithName("Exported Member").SeedAsync();
        await Seed(TestData.UnitUser(member.Id, unit.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var json = await (await Download(Client(actor), unit.Id)).Content.ReadAsStringAsync(Ct);

        Assert.DoesNotContain("Exported Member", json);
        Assert.Empty(Exported(json).Single().GetProperty("Users").GetProperty("$values").EnumerateArray());
    }

    /// <summary>The export is PascalCase, reference-preserving JSON.</summary>
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
        var unit = TestData.Unit(name: "Round Tripped");
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

        var response = await Client().SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <remarks>
    /// <c>POST units/json</c> is missing from the sweep above because it binds a <c>FileForm</c>, so MVC
    /// infers <c>[Consumes("multipart/form-data")]</c> and a JSON body is refused during endpoint
    /// selection - before <c>AuthorizationMiddleware</c> runs. Sent as multipart it answers 401 like its
    /// siblings, which is what this test asserts; the bare-<c>IFormFile</c> routes answer 415 even then.
    /// </remarks>
    [Fact]
    public async Task UploadJson_AnonymouslyIs401()
    {
        var response = await UploadJson(Client(), """[{ "Name": "Imported" }]""");

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
        var unit = TestData.Unit();
        await Seed(unit);

        return unit;
    }

    private async Task<MselEntity> SeedMsel()
    {
        var msel = TestData.Msel();
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
