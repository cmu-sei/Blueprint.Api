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

/// <summary><c>CardTeamService</c> / <c>CardTeamController</c> - the eight routes over the join row that
/// puts a Gallery card in front of one team and says what that team may do with it.</summary>
public class CardTeamEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET teamcards
    // ---------------------------------------------------------------------------------------------

    /// <summary>Get all with view MSELs returns every row in the installation.</summary>
    [Fact]
    public async Task GetAll_WithViewMsels_ReturnsEveryRowInTheInstallation()
    {
        var mine = await SeedMsel();
        var theirs = await SeedMsel();
        var ours = await SeedCardTeam(mine);
        var others = await SeedCardTeam(theirs);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var rows = await Read<List<ViewModels.CardTeam>>(await Client(actor).GetAsync(TeamCards, Ct));

        var ids = rows.Select(x => x.Id).ToList();
        Assert.Equal(2, ids.Count);
        Assert.Contains(ours.Id, ids);
        Assert.Contains(others.Id, ids);
    }

    [Fact]
    public async Task GetAll_ForAMselOwnerWithoutViewMsels_Is403()
    {
        var msel = await SeedMsel();
        await SeedCardTeam(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).GetAsync(TeamCards, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // GET msels/{mselId}/teamcards
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByMsel_ReturnsTheRowsForThatMselsCards()
    {
        var msel = await SeedMsel();
        var mine = await SeedCardTeam(msel);
        await SeedCardTeam(await SeedMsel());
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var rows = await GetByMsel(Client(actor), msel.Id);

        Assert.Equal(mine.Id, Assert.Single(rows).Id);
    }

    /// <remarks>
    /// The query gathers the MSEL's card ids and then the rows pointing at them, so a row whose <em>team</em>
    /// belongs to another MSEL is still listed here - which is how the create route's missing check
    /// (<see cref="Create_AcceptsATeamThatBelongsToADifferentMsel"/>) becomes visible to the MSEL that did
    /// not ask for it.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_ListsARowWhoseTeamBelongsToAnotherMsel()
    {
        var msel = await SeedMsel();
        var elsewhere = await SeedMsel();
        var card = await SeedCard(msel);
        var team = await SeedTeam(elsewhere);
        var row = await Seeded(TestData.CardTeam(card.Id, team.Id));
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        Assert.Equal(row.Id, Assert.Single(await GetByMsel(Client(actor), msel.Id)).Id);
    }

    [Theory]
    [InlineData(MselRole.Owner, HttpStatusCode.OK)]
    [InlineData(MselRole.Viewer, HttpStatusCode.OK)]
    [InlineData(MselRole.Evaluator, HttpStatusCode.Forbidden)]
    public async Task GetByMsel_TheRolesThatMayListAMselsCardTeams(MselRole role, HttpStatusCode expected)
    {
        var msel = await SeedMsel();
        await SeedCardTeam(msel);
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Client(actor).GetAsync(TeamCardsOfMsel(msel.Id), Ct);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task GetByMsel_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var msel = await SeedMsel();
        await SeedCardTeam(msel);
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        Assert.Equal(
            HttpStatusCode.Forbidden, (await Client(actor).GetAsync(TeamCardsOfMsel(msel.Id), Ct)).StatusCode);
    }

    /// <summary>Get by MSEL for a MSEL that is not there is answered with a 500 for an ordinary caller and empty for a privileged one.</summary>
    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_Is500ForAnOrdinaryCallerAndEmptyForAPrivilegedOne()
    {
        var stranger = await Actor().SeedAsync();
        var viewer = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();
        var unknown = Guid.NewGuid();

        var response = await Client(stranger).GetAsync(TeamCardsOfMsel(unknown), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("Object reference not set to an instance of an object.", await Title(response));
        Assert.Empty(await GetByMsel(Client(viewer), unknown));
    }

    // ---------------------------------------------------------------------------------------------
    // GET cards/{cardId}/teamcards
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByCard_ReturnsTheRowsForThatCard()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel);
        var wanted = await Seeded(TestData.CardTeam(card.Id, (await SeedTeam(msel)).Id));
        await SeedCardTeam(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var rows = await Read<List<ViewModels.CardTeam>>(
            await Client(actor).GetAsync(TeamCardsOfCard(card.Id), Ct));

        Assert.Equal(wanted.Id, Assert.Single(rows).Id);
    }

    [Fact]
    public async Task GetByCard_ForAMselViewer_ReturnsTheRowsFlags()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel);
        var team = await SeedTeam(msel);
        await Seeded(TestData.CardTeam(card.Id, team.Id, isShownOnWall: true, canPostArticles: true));
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var row = Assert.Single(await Read<List<ViewModels.CardTeam>>(
            await Client(actor).GetAsync(TeamCardsOfCard(card.Id), Ct)));

        Assert.True(row.IsShownOnWall);
        Assert.True(row.CanPostArticles);
        Assert.Equal(team.Id, row.TeamId);
    }

    [Fact]
    public async Task GetByCard_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel);
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        Assert.Equal(
            HttpStatusCode.Forbidden, (await Client(actor).GetAsync(TeamCardsOfCard(card.Id), Ct)).StatusCode);
    }

    /// <summary>Get by card for a card that is not there is answered with a 500 for an ordinary caller and an empty list for a privileged one.</summary>
    [Fact]
    public async Task GetByCard_ForACardThatIsNotThere_Is500ForAnOrdinaryCallerAndAnEmptyListForAPrivilegedOne()
    {
        var stranger = await Actor().SeedAsync();
        var viewer = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();
        var unknown = Guid.NewGuid();

        var ordinary = await Client(stranger).GetAsync(TeamCardsOfCard(unknown), Ct);
        var privileged = await Client(viewer).GetAsync(TeamCardsOfCard(unknown), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, ordinary.StatusCode);
        Assert.Equal("Object reference not set to an instance of an object.", await Title(ordinary));
        Assert.Empty(await Read<List<ViewModels.CardTeam>>(privileged));
    }

    /// <summary>Get by card for a template card is answered with a 500 for an ordinary caller.</summary>
    [Fact]
    public async Task GetByCard_ForATemplateCard_Is500ForAnOrdinaryCaller()
    {
        var card = await SeedCard(null, isTemplate: true);
        var team = await SeedTeam(await SeedMsel());
        var row = await Seeded(TestData.CardTeam(card.Id, team.Id));
        var stranger = await Actor().SeedAsync();
        var viewer = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var ordinary = await Client(stranger).GetAsync(TeamCardsOfCard(card.Id), Ct);
        var privileged = await Client(viewer).GetAsync(TeamCardsOfCard(card.Id), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, ordinary.StatusCode);
        Assert.Equal(row.Id, Assert.Single(await Read<List<ViewModels.CardTeam>>(privileged)).Id);
    }

    // ---------------------------------------------------------------------------------------------
    // GET teamcards/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ReturnsTheRow()
    {
        var msel = await SeedMsel();
        var row = await SeedCardTeam(msel, isShownOnWall: true);
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var answer = await Read<ViewModels.CardTeam>(await Client(actor).GetAsync(TeamCard(row.Id), Ct));

        Assert.Equal(row.Id, answer.Id);
        Assert.Equal(row.CardId, answer.CardId);
        Assert.Equal(row.TeamId, answer.TeamId);
        Assert.True(answer.IsShownOnWall);
    }

    /// <remarks>
    /// <c>ViewModels.CardTeam</c> is not a <c>Base</c>, so nothing about who showed this card to this team,
    /// or when, exists to be returned - <c>CardTeamEntity</c> has no audit columns at all. The card it points
    /// at has them; the decision to show it does not.
    /// </remarks>
    [Fact]
    public async Task Get_AnswersNoAuditFieldsBecauseTheEntityHasNone()
    {
        var msel = await SeedMsel();
        var row = await SeedCardTeam(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var body = await (await Client(actor).GetAsync(TeamCard(row.Id), Ct)).Content.ReadAsStringAsync(Ct);

        Assert.DoesNotContain("dateCreated", body);
        Assert.DoesNotContain("createdBy", body);
    }

    [Fact]
    public async Task Get_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var msel = await SeedMsel();
        var row = await SeedCardTeam(msel);
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await Client(actor).GetAsync(TeamCard(row.Id), Ct)).StatusCode);
    }

    /// <summary>Get for an id that is not there is answered with a 500 for an ordinary caller and answered with a 404 for a privileged one.</summary>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is500ForAnOrdinaryCallerAndA404ForAPrivilegedOne()
    {
        var stranger = await Actor().SeedAsync();
        var viewer = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();
        var unknown = Guid.NewGuid();

        var ordinary = await Client(stranger).GetAsync(TeamCard(unknown), Ct);
        var privileged = await Client(viewer).GetAsync(TeamCard(unknown), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, ordinary.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, privileged.StatusCode);
        Assert.Equal("Card Team not found", await Title(privileged));
    }

    /// <summary>Get for a row on a template card is answered with a 500 for an ordinary caller.</summary>
    [Fact]
    public async Task Get_ForARowOnATemplateCard_Is500ForAnOrdinaryCaller()
    {
        var card = await SeedCard(null, isTemplate: true);
        var team = await SeedTeam(await SeedMsel());
        var row = await Seeded(TestData.CardTeam(card.Id, team.Id));
        var stranger = await Actor().SeedAsync();
        var viewer = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var ordinary = await Client(stranger).GetAsync(TeamCard(row.Id), Ct);
        var privileged = await Client(viewer).GetAsync(TeamCard(row.Id), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, ordinary.StatusCode);
        Assert.Equal(row.Id, (await Read<ViewModels.CardTeam>(privileged)).Id);
    }

    // ---------------------------------------------------------------------------------------------
    // POST teamcards
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_WithEditMsels_Is201AndStoresTheRow()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel);
        var team = await SeedTeam(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(Client(actor), Body(card.Id, team.Id, isShownOnWall: true));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await Read<ViewModels.CardTeam>(response);
        var stored = await Stored(created.Id);
        Assert.Equal(card.Id, stored.CardId);
        Assert.Equal(team.Id, stored.TeamId);
        Assert.True(stored.IsShownOnWall);
        Assert.False(stored.CanPostArticles);
    }

    [Fact]
    public async Task Create_AnswersALocationHeaderNamingTheRow()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel);
        var team = await SeedTeam(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(Client(actor), Body(card.Id, team.Id));

        var created = await Read<ViewModels.CardTeam>(response);
        Assert.EndsWith($"/api/teamcards/{created.Id}", response.Headers.Location.ToString());
    }

    [Fact]
    public async Task Create_StoresBothFlags()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel);
        var team = await SeedTeam(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var created = await Read<ViewModels.CardTeam>(await Post(
            Client(actor), Body(card.Id, team.Id, isShownOnWall: true, canPostArticles: true)));

        var stored = await Stored(created.Id);
        Assert.True(stored.IsShownOnWall);
        Assert.True(stored.CanPostArticles);
    }

    /// <summary>Create for a MSEL owner is answered with a 403.</summary>
    [Theory]
    [InlineData(MselRole.Owner)]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.Viewer)]
    public async Task Create_ForAMselOwner_Is403(MselRole role)
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel);
        var team = await SeedTeam(msel);
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Post(Client(actor), Body(card.Id, team.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountOn(card.Id));
    }

    /// <summary>Create accepts a team that belongs to a different MSEL.</summary>
    [Fact]
    public async Task Create_AcceptsATeamThatBelongsToADifferentMsel()
    {
        var msel = await SeedMsel();
        var elsewhere = await SeedMsel();
        var card = await SeedCard(msel);
        var team = await SeedTeam(elsewhere);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(Client(actor), Body(card.Id, team.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(team.Id, (await Stored((await Read<ViewModels.CardTeam>(response)).Id)).TeamId);
    }

    /// <remarks>
    /// A template card belongs to no MSEL and is not exercise content, and nothing stops a team being
    /// attached to one - which creates the row that
    /// <see cref="Get_ForARowOnATemplateCard_Is500ForAnOrdinaryCaller"/> then cannot read back.
    /// </remarks>
    [Fact]
    public async Task Create_AcceptsATemplateCard()
    {
        var card = await SeedCard(null, isTemplate: true);
        var team = await SeedTeam(await SeedMsel());
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(Client(actor), Body(card.Id, team.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, await CountOn(card.Id));
    }

    /// <summary>Create for a pair that already exists is answered with a 500.</summary>
    [Fact]
    public async Task Create_ForAPairThatAlreadyExists_Is500RatherThanA409()
    {
        var msel = await SeedMsel();
        var row = await SeedCardTeam(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(Client(actor), Body(row.CardId, row.TeamId));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(1, await CountOn(row.CardId));
    }

    /// <summary>Create for an id that is not there is answered with a 500.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Create_ForAnIdThatIsNotThere_Is500RatherThanA404(bool cardIsMissing)
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel);
        var team = await SeedTeam(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(Client(actor), cardIsMissing
            ? Body(Guid.NewGuid(), team.Id)
            : Body(card.Id, Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0, await CountOn(card.Id));
    }

    [Fact]
    public async Task Create_BroadcastsToTheMselGroupAndTheAdminGroup()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel);
        var team = await SeedTeam(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        await Post(Client(actor), Body(card.Id, team.Id));

        Assert.Equal(
            [msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.CardTeamCreated, msel.Id));
    }

    /// <summary>Create on a template card broadcasts to a group named by the empty string.</summary>
    [Fact]
    public async Task Create_OnATemplateCard_BroadcastsToAGroupNamedByTheEmptyString()
    {
        var card = await SeedCard(null, isTemplate: true);
        var team = await SeedTeam(await SeedMsel());
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        await Post(Client(actor), Body(card.Id, team.Id));

        Assert.Equal(
            [string.Empty, MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.CardTeamCreated, string.Empty));
    }

    /// <remarks>
    /// No card-team operation marks the MSEL modified either - the service holds no
    /// <c>ServiceUtilities</c> call, exactly as <c>CardService</c> does not.
    /// </remarks>
    [Fact]
    public async Task Create_DoesNotMarkTheMselModified()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel);
        var team = await SeedTeam(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        await Post(Client(actor), Body(card.Id, team.Id));

        Assert.Null((await StoredMsel(msel.Id)).DateModified);
    }

    [Fact]
    public async Task Create_WithNoBody_Is400()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).PostAsync(TeamCards, EmptyJson(), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT cardteams/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_WithEditMsels_Is200AndStoresTheFlags()
    {
        var msel = await SeedMsel();
        var row = await SeedCardTeam(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Put(Client(actor), row.Id, BodyFor(row) with
        {
            IsShownOnWall = true,
            CanPostArticles = true
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await Stored(row.Id);
        Assert.True(stored.IsShownOnWall);
        Assert.True(stored.CanPostArticles);
    }

    /// <summary>Update at the route the other seven imply is answered with a 405.</summary>
    [Fact]
    public async Task Update_AtTheRouteTheOtherSevenImply_Is405()
    {
        var msel = await SeedMsel();
        var row = await SeedCardTeam(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).PutAsJsonAsync(TeamCard(row.Id), BodyFor(row), Ct);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    /// <summary>Update with a body id that is not the routes is answered with a 500.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Update_WithABodyIdThatIsNotTheRoutes_Is500(bool omitted)
    {
        var msel = await SeedMsel();
        var row = await SeedCardTeam(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Put(Client(actor), row.Id, BodyFor(row) with
        {
            Id = omitted ? Guid.Empty : Guid.NewGuid()
        });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.StartsWith("The property 'CardTeamEntity.Id'", await Title(response));
    }

    /// <remarks>
    /// <c>Id</c> is not the only key-shaped thing the profile maps: a PUT may rewrite <c>CardId</c> and
    /// <c>TeamId</c> too, so "update which flags this team has on this card" and "point this row at a
    /// different card entirely" are the same request. No permission is consulted for either, and nothing
    /// checks the new card's MSEL.
    /// </remarks>
    [Fact]
    public async Task Update_MayRepointTheRowAtAnotherMselsCard()
    {
        var msel = await SeedMsel();
        var elsewhere = await SeedMsel();
        var row = await SeedCardTeam(msel);
        var other = await SeedCard(elsewhere);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Put(Client(actor), row.Id, BodyFor(row) with { CardId = other.Id });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(other.Id, (await Stored(row.Id)).CardId);
    }

    [Theory]
    [InlineData(MselRole.Owner)]
    [InlineData(MselRole.Editor)]
    public async Task Update_ForAMselOwnerOrEditor_Is403(MselRole role)
    {
        var msel = await SeedMsel();
        var row = await SeedCardTeam(msel);
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Put(Client(actor), row.Id, BodyFor(row) with { IsShownOnWall = true });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False((await Stored(row.Id)).IsShownOnWall);
    }

    /// <remarks>
    /// The permission check is the controller's and runs before the lookup, so an unknown id is a 404 for a
    /// caller holding <c>EditMsels</c> and a 403 for anybody else - the same order-of-operations asymmetry the
    /// card update route has.
    /// </remarks>
    [Theory]
    [InlineData(true, HttpStatusCode.NotFound)]
    [InlineData(false, HttpStatusCode.Forbidden)]
    public async Task Update_ForAnIdThatIsNotThere_Is404OrA403(bool mayEdit, HttpStatusCode expected)
    {
        var msel = await SeedMsel();
        var row = await SeedCardTeam(msel);
        var builder = Actor();
        var actor = await (mayEdit ? builder.WithSystemPermissions(SystemPermission.EditMsels) : builder)
            .SeedAsync();
        var unknown = Guid.NewGuid();

        var response = await Put(Client(actor), unknown, BodyFor(row) with { Id = unknown });

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Update_BroadcastsWhatChangedToTheMselGroup()
    {
        var msel = await SeedMsel();
        var row = await SeedCardTeam(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        await Put(Client(actor), row.Id, BodyFor(row) with { IsShownOnWall = true });

        var send = Hub.ToGroup(msel.Id).Single(x => x.Method == MainHubMethods.CardTeamUpdated);
        Assert.Contains("isShownOnWall", Assert.IsType<string[]>(send.Arguments[1]));
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE teamcards/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_WithEditMsels_Is204AndRemovesTheRow()
    {
        var msel = await SeedMsel();
        var row = await SeedCardTeam(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync(TeamCard(row.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await Stored(row.Id));
    }

    [Fact]
    public async Task Delete_ForAMselOwner_Is403()
    {
        var msel = await SeedMsel();
        var row = await SeedCardTeam(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        Assert.Equal(
            HttpStatusCode.Forbidden, (await Client(actor).DeleteAsync(TeamCard(row.Id), Ct)).StatusCode);
        Assert.NotNull(await Stored(row.Id));
    }

    /// <remarks>
    /// Existence is checked after permission here as well, so an unknown id is a 403 for a stranger - the
    /// opposite of <c>CardService.DeleteAsync</c>, which is the one method in the unit that looks the row up
    /// first.
    /// </remarks>
    [Theory]
    [InlineData(true, HttpStatusCode.NotFound)]
    [InlineData(false, HttpStatusCode.Forbidden)]
    public async Task Delete_ForAnIdThatIsNotThere_Is404OrA403(bool mayEdit, HttpStatusCode expected)
    {
        var builder = Actor();
        var actor = await (mayEdit ? builder.WithSystemPermissions(SystemPermission.EditMsels) : builder)
            .SeedAsync();

        var response = await Client(actor).DeleteAsync(TeamCard(Guid.NewGuid()), Ct);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Delete_BroadcastsTheIdToTheMselGroup()
    {
        var msel = await SeedMsel();
        var row = await SeedCardTeam(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        await Client(actor).DeleteAsync(TeamCard(row.Id), Ct);

        Assert.Equal(
            [msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.CardTeamDeleted, msel.Id));
        Assert.Equal(row.Id, Assert.IsType<Guid>(Hub.Of(MainHubMethods.CardTeamDeleted, msel.Id)[0].Payload));
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE teams/{teamId}/cards/{cardId}
    // ---------------------------------------------------------------------------------------------

    /// <summary>Delete by ids is always answered with a 404 because the ids are transposed.</summary>
    [Fact]
    public async Task DeleteByIds_IsAlwaysA404BecauseTheIdsAreTransposed()
    {
        var msel = await SeedMsel();
        var row = await SeedCardTeam(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var correct = await Client(actor).DeleteAsync(CardOfTeam(row.TeamId, row.CardId), Ct);

        Assert.Equal(HttpStatusCode.NotFound, correct.StatusCode);
        Assert.Equal("Card Team not found", await Title(correct));
        Assert.NotNull(await Stored(row.Id));

        var transposed = await Client(actor).DeleteAsync(CardOfTeam(row.CardId, row.TeamId), Ct);

        Assert.Equal(HttpStatusCode.NoContent, transposed.StatusCode);
        Assert.Null(await Stored(row.Id));
    }

    [Fact]
    public async Task DeleteByIds_ForAMselOwner_Is403()
    {
        var msel = await SeedMsel();
        var row = await SeedCardTeam(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync(CardOfTeam(row.CardId, row.TeamId), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Deleting a row by its transposed ids broadcasts the deletion to the MSEL group.</summary>
    [Fact]
    public async Task DeleteByIds_WhenItWorks_BroadcastsToTheMselGroup()
    {
        var msel = await SeedMsel();
        var row = await SeedCardTeam(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        await Client(actor).DeleteAsync(CardOfTeam(row.CardId, row.TeamId), Ct);

        Assert.Equal(
            [msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.CardTeamDeleted, msel.Id));
    }

    // ---------------------------------------------------------------------------------------------
    // Authentication
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "teamcards")]
    [InlineData("GET", "msels/00000000-0000-0000-0000-000000000001/teamcards")]
    [InlineData("GET", "cards/00000000-0000-0000-0000-000000000001/teamcards")]
    [InlineData("GET", "teamcards/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "teamcards")]
    [InlineData("PUT", "cardteams/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "teamcards/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "teams/00000000-0000-0000-0000-000000000001/cards/00000000-0000-0000-0000-000000000002")]
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

    private const string TeamCards = "/api/teamcards";

    private static string TeamCard(Guid id) => $"{TeamCards}/{id}";

    /// <summary>
    /// The update route, spelled the way <c>CardTeamController.cs:144</c> spells it and no other route in the
    /// controller does.
    /// </summary>
    private static string CardTeam(Guid id) => $"/api/cardteams/{id}";

    private static string TeamCardsOfMsel(Guid mselId) => $"/api/msels/{mselId}/teamcards";

    private static string TeamCardsOfCard(Guid cardId) => $"/api/cards/{cardId}/teamcards";

    private static string CardOfTeam(Guid teamId, Guid cardId) => $"/api/teams/{teamId}/cards/{cardId}";

    /// <summary>
    /// The wire shape of a card team. No audit fields, because <c>CardTeamEntity</c> is not a
    /// <c>BaseEntity</c> and <c>ViewModels.CardTeam</c> is not a <c>Base</c>.
    /// </summary>
    private sealed record CardTeamBody
    {
        public Guid Id { get; init; }
        public Guid CardId { get; init; }
        public Guid TeamId { get; init; }
        public bool IsShownOnWall { get; init; }
        public bool CanPostArticles { get; init; }
    }

    private static CardTeamBody Body(
        Guid cardId, Guid teamId, bool isShownOnWall = false, bool canPostArticles = false) => new()
    {
        CardId = cardId,
        TeamId = teamId,
        IsShownOnWall = isShownOnWall,
        CanPostArticles = canPostArticles
    };

    private static CardTeamBody BodyFor(CardTeamEntity row) => new()
    {
        Id = row.Id,
        CardId = row.CardId,
        TeamId = row.TeamId,
        IsShownOnWall = row.IsShownOnWall,
        CanPostArticles = row.CanPostArticles
    };

    private async Task<MselEntity> SeedMsel()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        return msel;
    }

    private async Task<CardEntity> SeedCard(Guid? mselId, bool isTemplate = false)
    {
        var card = TestData.Card(mselId, isTemplate: isTemplate);
        await Seed(card);

        return card;
    }

    private Task<CardEntity> SeedCard(MselEntity msel) => SeedCard(msel.Id);

    private async Task<TeamEntity> SeedTeam(MselEntity msel)
    {
        var team = TestData.Team(msel.Id);
        await Seed(team);

        return team;
    }

    /// <summary>
    /// A fresh card, a fresh team and the row joining them, all on <paramref name="msel"/>.
    /// </summary>
    private async Task<CardTeamEntity> SeedCardTeam(
        MselEntity msel, bool isShownOnWall = false, bool canPostArticles = false)
    {
        var card = await SeedCard(msel);
        var team = await SeedTeam(msel);

        return await Seeded(TestData.CardTeam(card.Id, team.Id, isShownOnWall, canPostArticles));
    }

    /// <summary>
    /// <c>DatabaseTestBase.Seed</c> takes a <c>params object[]</c> and returns nothing; this hands the entity
    /// back so a seeded join row can be named in the same expression that writes it.
    /// </summary>
    private async Task<T> Seeded<T>(T entity)
    {
        await Seed(entity);

        return entity;
    }

    private Task<HttpResponseMessage> Post(HttpClient client, CardTeamBody body) =>
        client.PostAsJsonAsync(TeamCards, body, Ct);

    private Task<HttpResponseMessage> Put(HttpClient client, Guid id, CardTeamBody body) =>
        client.PutAsJsonAsync(CardTeam(id), body, Ct);

    private async Task<List<ViewModels.CardTeam>> GetByMsel(HttpClient client, Guid mselId)
    {
        var response = await client.GetAsync(TeamCardsOfMsel(mselId), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<List<ViewModels.CardTeam>>(response);
    }

    private async Task<CardTeamEntity> Stored(Guid id)
    {
        await using var context = NewContext();

        return await context.CardTeams.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, Ct);
    }

    private async Task<MselEntity> StoredMsel(Guid id)
    {
        await using var context = NewContext();

        return await context.Msels.AsNoTracking().SingleAsync(x => x.Id == id, Ct);
    }

    private async Task<int> CountOn(Guid cardId)
    {
        await using var context = NewContext();

        return await context.CardTeams.CountAsync(x => x.CardId == cardId, Ct);
    }

    private static StringContent EmptyJson() =>
        new(string.Empty, Encoding.UTF8, "application/json");

    private async Task<string> Title(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ViewModels.ApiError>(JsonOptions, Ct))?.Title;

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }
}
