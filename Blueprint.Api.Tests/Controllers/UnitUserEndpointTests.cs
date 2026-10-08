// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary><c>UnitUserService</c> / <c>UnitUserController</c> - the five routes behind the join row that
/// puts a person in a unit, which is the half of every <c>Msel*Requirement</c> conjunction that has to
/// match before the role query is ever reached.</summary>
public class UnitUserEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET unitusers
    // ---------------------------------------------------------------------------------------------

    /// <summary>Get all returns every row in the installation.</summary>
    [Fact]
    public async Task GetAll_ReturnsEveryRowInTheInstallation()
    {
        var mine = await SeedUnit();
        var theirs = await SeedUnit();
        var first = await Actor().SeedAsync();
        var second = await Actor().SeedAsync();
        await Seed(
            TestData.UnitUser(first.Id, mine.Id),
            TestData.UnitUser(second.Id, theirs.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var rows = await GetRows(Client(actor));

        Assert.Equal(
            new[] { first.Id, second.Id }.Order(), rows.Select(x => x.UserId).Order());
    }

    [Fact]
    public async Task GetAll_WithViewUnitsOnly_Is200()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var response = await Client(actor).GetAsync(UnitUsers, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetAll_WithoutViewUnits_Is403()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var response = await Client(actor).GetAsync(UnitUsers, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>The list route answers each row with a null user; the single read includes it.</summary>
    [Fact]
    public async Task GetAll_AnswersANullUserOnEveryRow()
    {
        var unit = await SeedUnit();
        var member = await Actor().WithName("Listed Member").SeedAsync();
        await Seed(TestData.UnitUser(member.Id, unit.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var row = Assert.Single(await GetRows(Client(actor)));

        Assert.Equal(member.Id, row.UserId);
        Assert.Null(row.User);
        Assert.Null(row.Unit);
    }

    // ---------------------------------------------------------------------------------------------
    // GET unitusers/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ReturnsTheRowWithItsUser()
    {
        var unit = await SeedUnit();
        var member = await Actor().WithName("Named Member").SeedAsync();
        var row = TestData.UnitUser(member.Id, unit.Id);
        await Seed(row);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var answered = await GetRow(Client(actor), row.Id);

        Assert.Equal(member.Id, answered.UserId);
        Assert.Equal(unit.Id, answered.UnitId);
        Assert.Equal("Named Member", answered.User.Name);
    }

    /// <summary>A unit membership read by id answers no unit.</summary>
    [Fact]
    public async Task Get_AnswersANullUnit()
    {
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        var row = TestData.UnitUser(member.Id, unit.Id);
        await Seed(row);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        Assert.Null((await GetRow(Client(actor), row.Id)).Unit);
    }

    /// <summary>Get answers audit fields the entity does not have.</summary>
    [Fact]
    public async Task Get_AnswersAuditFieldsTheEntityDoesNotHave()
    {
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        var row = TestData.UnitUser(member.Id, unit.Id);
        await Seed(row);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var answered = await GetRow(Client(actor), row.Id);

        Assert.Equal(Guid.Empty, answered.CreatedBy);
        Assert.Equal(default, answered.DateCreated);
        Assert.Null(answered.ModifiedBy);
        Assert.Null(answered.DateModified);
    }

    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var response = await Client(actor).GetAsync(UnitUserRoute(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_WithViewUnitsOnly_Is200()
    {
        var row = await SeedMembership();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var response = await Client(actor).GetAsync(UnitUserRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_WithoutViewUnits_Is403()
    {
        var row = await SeedMembership();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var response = await Client(actor).GetAsync(UnitUserRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// The route requires <c>ViewUnits</c> outright and the service checks nothing else, so the person
    /// the row is about cannot read it. Same shape as <c>GET units/{id}</c> refusing a member of the
    /// unit.
    /// </remarks>
    [Fact]
    public async Task Get_ForTheUserTheRowIsAbout_Is403()
    {
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        var row = TestData.UnitUser(member.Id, unit.Id);
        await Seed(row);

        var response = await Client(member).GetAsync(UnitUserRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // POST unitusers
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_StoresTheRowAndAnswers201()
    {
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Post(Client(actor), Body(member.Id, unit.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await Read<ViewModels.UnitUser>(response);
        var stored = await Stored(created.Id);

        Assert.Equal(member.Id, stored.UserId);
        Assert.Equal(unit.Id, stored.UnitId);
    }

    [Fact]
    public async Task Create_AnswersALocationHeaderPointingAtTheRow()
    {
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Post(Client(actor), Body(member.Id, unit.Id));
        var created = await Read<ViewModels.UnitUser>(response);

        Assert.EndsWith($"/api/unitusers/{created.Id}", response.Headers.Location.ToString());
    }

    /// <remarks>
    /// <c>createUnitUser</c> requires <c>ManageUnits</c> and its <c>Location</c> header names
    /// <c>getUnitUser</c>, which requires <c>ViewUnits</c> - so the caller who just added the user is
    /// answered 403 by the header they were handed. <c>createTeam</c> and <c>createUnit</c> have the same shape.
    /// </remarks>
    [Fact]
    public async Task Create_WithManageUnitsOnly_CannotFollowItsOwnLocationHeader()
    {
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Post(Client(actor), Body(member.Id, unit.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var followed = await Client(actor).GetAsync(response.Headers.Location, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, followed.StatusCode);
    }

    [Fact]
    public async Task Create_WithoutManageUnits_Is403()
    {
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var response = await Post(Client(actor), Body(member.Id, unit.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await StoredRows());
    }

    /// <summary>Create for a user that is not there is answered with a 500.</summary>
    [Fact]
    public async Task Create_ForAUserThatIsNotThere_Is500()
    {
        var unit = await SeedUnit();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Post(Client(actor), Body(Guid.NewGuid(), unit.Id));

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("An error occurred while saving the entity changes. See the inner exception for details.", failure.Title);
        Assert.Contains("UnitUserService.CreateAsync", failure.Detail);
        Assert.Empty(await StoredRows());
    }

    /// <summary>Create for a unit that is not there is answered with a 500.</summary>
    [Fact]
    public async Task Create_ForAUnitThatIsNotThere_Is500()
    {
        var member = await Actor().SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Post(Client(actor), Body(member.Id, Guid.NewGuid()));

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("An error occurred while saving the entity changes. See the inner exception for details.", failure.Title);
        Assert.Contains("UnitUserService.CreateAsync", failure.Detail);
        Assert.Empty(await StoredRows());
    }

    /// <summary>Create for a pair that is already there is answered with a 500.</summary>
    [Fact]
    public async Task Create_ForAPairThatIsAlreadyThere_Is500()
    {
        var unit = await SeedUnit();
        var member = await Actor().InUnit(unit).SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Post(Client(actor), Body(member.Id, unit.Id));

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("An error occurred while saving the entity changes. See the inner exception for details.", failure.Title);
        Assert.Contains("UnitUserService.CreateAsync", failure.Detail);
        Assert.Single(await StoredRows());
    }

    [Fact]
    public async Task Create_KeepsTheBodysId()
    {
        var id = Guid.NewGuid();
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Post(Client(actor), Body(member.Id, unit.Id) with { Id = id });

        Assert.Equal(id, (await Read<ViewModels.UnitUser>(response)).Id);
        Assert.NotNull(await Stored(id));
    }

    /// <summary>Create records nobody as having added the user.</summary>
    [Fact]
    public async Task Create_RecordsNobodyAsHavingAddedTheUser()
    {
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Post(Client(actor), Body(member.Id, unit.Id) with
        {
            CreatedBy = Guid.NewGuid(),
            DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });

        var created = await Read<ViewModels.UnitUser>(response);

        Assert.Equal(Guid.Empty, created.CreatedBy);
        Assert.Equal(default, created.DateCreated);
    }

    /// <remarks>
    /// <c>UnitUserHandler.GetGroupsAsync</c> names the unit, the user, the admin data group and every
    /// MSEL the unit is assigned to - so a client watching an exercise is told when somebody joins a unit
    /// that contributes to it, which is the notification <c>MselUnitService</c> itself never sends
    /// (there is no <c>MselUnitHandler</c>).
    /// </remarks>
    [Fact]
    public async Task Create_BroadcastsToTheUnitTheUserAndEveryMselTheUnitIsOn()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        await Seed(TestData.MselUnit(unit.Id, msel.Id));
        var member = await Actor().SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        await Post(Client(actor), Body(member.Id, unit.Id));

        Assert.Equal(
            new[]
            {
                unit.Id.ToString(),
                member.Id.ToString(),
                MainHub.ADMIN_DATA_GROUP,
                msel.Id.ToString()
            }.Order(),
            Hub.Recipients(MainHubMethods.UnitUserCreated, unit.Id, member.Id, msel.Id).Order());
    }

    [Fact]
    public async Task Create_WithNoBody_Is400()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        using var content = new StringContent(string.Empty, Encoding.UTF8, "application/json");

        var response = await Client(actor).PostAsync(UnitUsers, content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE unitusers/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_RemovesTheRowAndAnswers204()
    {
        var row = await SeedMembership();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Client(actor).DeleteAsync(UnitUserRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await Stored(row.Id));
    }

    [Fact]
    public async Task Delete_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Client(actor).DeleteAsync(UnitUserRoute(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_WithoutManageUnits_Is403()
    {
        var row = await SeedMembership();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var response = await Client(actor).DeleteAsync(UnitUserRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(await Stored(row.Id));
    }

    [Fact]
    public async Task Delete_BroadcastsToTheUnitTheUserAndEveryMselTheUnitIsOn()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        await Seed(TestData.MselUnit(unit.Id, msel.Id));
        var member = await Actor().SeedAsync();
        var row = TestData.UnitUser(member.Id, unit.Id);
        await Seed(row);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        await Client(actor).DeleteAsync(UnitUserRoute(row.Id), Ct);

        Assert.Equal(
            new[]
            {
                unit.Id.ToString(),
                member.Id.ToString(),
                MainHub.ADMIN_DATA_GROUP,
                msel.Id.ToString()
            }.Order(),
            Hub.Recipients(MainHubMethods.UnitUserDeleted, unit.Id, member.Id, msel.Id).Order());
    }

    /// <summary>Removing a person from a unit leaves their MSEL roles, which then grant nothing.</summary>
    [Fact]
    public async Task Delete_LeavesTheUsersMselRolesBehindWhereTheyGrantNothing()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        await Seed(TestData.MselUnit(unit.Id, msel.Id));
        var member = await Actor().SeedAsync();
        var row = TestData.UnitUser(member.Id, unit.Id);
        await Seed(row);
        await Db.AddMselRoleAsync(member.Id, msel.Id, MselRole.Viewer, Ct);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var before = await Client(member).GetAsync(MselUnitsOf(msel.Id), Ct);

        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        var response = await Client(actor).DeleteAsync(UnitUserRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var after = await Client(member).GetAsync(MselUnitsOf(msel.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);

        await using var context = NewContext();

        Assert.Single(await context.UserMselRoles.Where(x => x.MselId == msel.Id).ToListAsync(Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE units/{unitId}/users/{userId}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task DeleteByIds_RemovesTheRowAndAnswers204()
    {
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        var row = TestData.UnitUser(member.Id, unit.Id);
        await Seed(row);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Client(actor).DeleteAsync(MembershipRoute(unit.Id, member.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await Stored(row.Id));
    }

    /// <remarks>
    /// The positive control. <c>UnitUserController.cs:143</c> passes <c>(unitId, userId)</c> to a method
    /// declared <c>(Guid unitId, Guid userId, …)</c>, so a caller who swaps them matches nothing and is
    /// answered 404 - which is what <c>DELETE teams/{teamId}/cards/{cardId}</c> answers for the
    /// <em>right</em> ids, because its call site transposes them. This test fails if anybody ever
    /// "tidies" the argument order here.
    /// </remarks>
    [Fact]
    public async Task DeleteByIds_WithTheIdsTheOtherWayRound_Is404()
    {
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        var row = TestData.UnitUser(member.Id, unit.Id);
        await Seed(row);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Client(actor).DeleteAsync(MembershipRoute(member.Id, unit.Id), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotNull(await Stored(row.Id));
    }

    [Fact]
    public async Task DeleteByIds_ForAPairThatIsNotThere_Is404()
    {
        var unit = await SeedUnit();
        var member = await Actor().InUnit(unit).SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Client(actor).DeleteAsync(MembershipRoute(unit.Id, Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(await StoredRows());
    }

    [Fact]
    public async Task DeleteByIds_WithoutManageUnits_Is403()
    {
        var unit = await SeedUnit();
        var member = await Actor().InUnit(unit).SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var response = await Client(actor).DeleteAsync(MembershipRoute(unit.Id, member.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(await StoredRows());
    }

    /// <summary>A <c>ManageUnits</c> holder may remove their own membership.</summary>
    [Fact]
    public async Task DeleteByIds_ForTheCallersOwnMembership_Is204()
    {
        var unit = await SeedUnit();
        var member = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();
        await Seed(TestData.UnitUser(member.Id, unit.Id));

        var response = await Client(member).DeleteAsync(MembershipRoute(unit.Id, member.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await StoredRows());
    }

    // ---------------------------------------------------------------------------------------------
    // Authentication
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "unitusers")]
    [InlineData("GET", "unitusers/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "unitusers")]
    [InlineData("DELETE", "unitusers/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "units/00000000-0000-0000-0000-000000000001/users/00000000-0000-0000-0000-000000000002")]
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

    private const string UnitUsers = "/api/unitusers";

    private static string UnitUserRoute(Guid id) => $"{UnitUsers}/{id}";

    private static string MembershipRoute(Guid unitId, Guid userId) =>
        $"/api/units/{unitId}/users/{userId}";

    /// <summary>
    /// The MSEL-scoped read of a unit assignment, used to show what a membership was carrying - it is the
    /// nearest route gated by <c>MselViewRequirement</c> alone, so it answers 403 the moment the unit
    /// membership behind a MSEL role disappears.
    /// </summary>
    private static string MselUnitsOf(Guid mselId) => $"/api/msels/{mselId}/mselunits";

    /// <summary>
    /// The wire shape of a unit user. The four audit properties are non-nullable where
    /// <c>ViewModels.Base</c> declares them so, because a body sending null for <c>createdBy</c> or
    /// <c>dateCreated</c> is a 400 that never reaches the controller.
    /// </summary>
    private sealed record UnitUserBody
    {
        public Guid Id { get; init; }
        public Guid UserId { get; init; }
        public Guid UnitId { get; init; }

        public Guid CreatedBy { get; init; }
        public DateTime DateCreated { get; init; }
        public Guid? ModifiedBy { get; init; }
        public DateTime? DateModified { get; init; }
    }

    private static UnitUserBody Body(Guid userId, Guid unitId) =>
        new() { UserId = userId, UnitId = unitId };

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

    private async Task<UnitUserEntity> SeedMembership()
    {
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        var row = TestData.UnitUser(member.Id, unit.Id);
        await Seed(row);

        return row;
    }

    private Task<HttpResponseMessage> Post(HttpClient client, UnitUserBody body) =>
        client.PostAsJsonAsync(UnitUsers, body, Ct);

    private async Task<List<ViewModels.UnitUser>> GetRows(HttpClient client)
    {
        var response = await client.GetAsync(UnitUsers, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<List<ViewModels.UnitUser>>(response);
    }

    private async Task<ViewModels.UnitUser> GetRow(HttpClient client, Guid id)
    {
        var response = await client.GetAsync(UnitUserRoute(id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<ViewModels.UnitUser>(response);
    }

    private async Task<UnitUserEntity> Stored(Guid id)
    {
        await using var context = NewContext();

        return await context.UnitUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, Ct);
    }

    private async Task<List<UnitUserEntity>> StoredRows()
    {
        await using var context = NewContext();

        return await context.UnitUsers.AsNoTracking().ToListAsync(Ct);
    }

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }
}
