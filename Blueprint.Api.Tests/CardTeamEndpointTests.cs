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
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>CardTeamService</c> / <c>CardTeamController</c> - the eight routes over the join row that puts a
/// Gallery card in front of one team and says what that team may do with it.
/// </summary>
/// <remarks>
/// <para>
/// A card team row is three booleans' worth of data and the far end of the card unit's permission story:
/// <c>CardService</c> asks who may change a card, and this service asks who may show one to a team. The two
/// answers do not agree. <strong>Every write here requires <c>EditMsels</c> - the system permission - and
/// consults no MSEL role at all</strong>, so a MSEL owner cannot assign their own MSEL's card to their own
/// MSEL's team (<see cref="Create_ForAMselOwner_Is403"/>), while a caller holding <c>EditMsels</c> and no
/// role anywhere may assign any card to any team, including a team on a different MSEL
/// (<see cref="Create_AcceptsATeamThatBelongsToADifferentMsel"/>). Not one of the four write methods on the
/// service takes a permission argument.
/// </para>
/// <para>
/// <strong>`DELETE teams/{teamId}/cards/{cardId}` never deletes anything.</strong>
/// <c>CardTeamController.cs:192</c> calls <c>DeleteByIdsAsync(teamId, cardId, ct)</c> against a signature of
/// <c>(Guid cardId, Guid teamId)</c>, so the two ids arrive transposed and the lookup matches no row - the
/// route is a 404 for every well-formed request, and returns 204 only when the caller passes the ids the
/// wrong way round. See <see cref="DeleteByIds_IsAlwaysA404BecauseTheIdsAreTransposed"/>. Both arguments are
/// <c>Guid</c>, so nothing about this is a compile error and no test in the estate covered it.
/// </para>
/// <para>
/// <strong>Three of the four reads dereference the card unguarded</strong>, so "that row is not there" and
/// "that card is a template" are both 500s rather than 404s, and one of them - <c>GET teamcards/{id}</c> -
/// answers a <em>different</em> status depending on the caller's permission, because the controller's own
/// null check is only reachable once the service's has already thrown for an ordinary caller. See
/// <see cref="Get_ForAnIdThatIsNotThere_Is500ForAnOrdinaryCallerAndA404ForAPrivilegedOne"/>.
/// </para>
/// <para>
/// <strong>The update route is spelled <c>cardteams/{id}</c></strong> where its seven siblings are spelled
/// <c>teamcards</c>, so <c>PUT teamcards/{id}</c> - the route a reader of the other seven would guess, and
/// the one <c>POST teamcards</c>' own <c>Location</c> pattern implies - is a 405
/// (<see cref="Update_AtTheRouteTheOtherSevenImply_Is405"/>). And because <c>CardTeamProfile</c> maps
/// <c>Id</c>, a body that omits it writes <c>Guid.Empty</c> onto the key and the request is a 500.
/// </para>
/// <para>
/// <c>CardTeamEntity</c> is not a <c>BaseEntity</c>, so there is no record of who showed a card to a team or
/// when - the one entity in the card unit with no audit trail.
/// </para>
/// <para>
/// Per this branch's rule, every test characterizes rather than fixes, and says what fixing it will do to
/// the test.
/// </para>
/// </remarks>
public class CardTeamEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET teamcards
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// <c>GetAsync()</c> has no MSEL scope and no filter of any kind: one <c>ViewMsels</c> holder reads every
    /// card-team row in the installation, across every exercise. The card routes have no equivalent - there
    /// is no "all cards" route - so this is the one place the whole table is exposed. Scoping it to the
    /// caller's MSELs reddens this test.
    /// </remarks>
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
        var row = await Seeded(BlueprintAppFactory.CardTeam(card.Id, team.Id));
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
    public async Task GetByMsel_ForACallerWithNoRoleOnTheMsel_Is403()
    {
        var msel = await SeedMsel();
        await SeedCardTeam(msel);
        var actor = await Actor().SeedAsync();

        Assert.Equal(
            HttpStatusCode.Forbidden, (await Client(actor).GetAsync(TeamCardsOfMsel(msel.Id), Ct)).StatusCode);
    }

    /// <remarks>
    /// Same shape as <c>CardService.GetByMselAsync</c>: <c>MselViewRequirement</c> answers false for a MSEL
    /// that is not there, and the <c>FindAsync</c> below it is dereferenced unguarded. So an unknown MSEL id
    /// is a 500 for an ordinary caller and an empty list for a <c>ViewMsels</c> holder, and neither is the
    /// 404 a client could act on. Adding the null guard reddens both halves.
    /// </remarks>
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
        var wanted = await Seeded(BlueprintAppFactory.CardTeam(card.Id, (await SeedTeam(msel)).Id));
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
        await Seeded(BlueprintAppFactory.CardTeam(card.Id, team.Id, isShownOnWall: true, canPostArticles: true));
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var row = Assert.Single(await Read<List<ViewModels.CardTeam>>(
            await Client(actor).GetAsync(TeamCardsOfCard(card.Id), Ct)));

        Assert.True(row.IsShownOnWall);
        Assert.True(row.CanPostArticles);
        Assert.Equal(team.Id, row.TeamId);
    }

    [Fact]
    public async Task GetByCard_ForACallerWithNoRoleOnTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel);
        var actor = await Actor().SeedAsync();

        Assert.Equal(
            HttpStatusCode.Forbidden, (await Client(actor).GetAsync(TeamCardsOfCard(card.Id), Ct)).StatusCode);
    }

    /// <remarks>
    /// <c>GetByCardAsync</c> looks the card up with <c>FirstOrDefaultAsync</c> and then reads
    /// <c>card.MselId</c> without checking the result - but only inside the permission branch, which
    /// <c>ViewMsels</c> short-circuits. So the two callers get two different answers to "there is no such
    /// card": an ordinary one gets a 500 from the NRE, a privileged one gets 200 and an empty list, and
    /// neither gets the 404 the id deserves. Guarding the lookup makes both the same answer and reddens the
    /// test.
    /// </remarks>
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

    /// <remarks>
    /// A template card has a null <c>MselId</c>, which this route hands to <c>MselViewRequirement</c> (false,
    /// for a MSEL that is not there) and then to <c>FindAsync</c>, whose null result is dereferenced. So the
    /// rows of a template card are a 500 for an ordinary caller, and readable only by a <c>ViewMsels</c>
    /// holder who never enters the branch. Nothing stops a template card from having card-team rows -
    /// <see cref="Create_AcceptsATemplateCard"/> puts one there - so this is a state the API produces and
    /// then half-refuses to read.
    /// </remarks>
    [Fact]
    public async Task GetByCard_ForATemplateCard_Is500ForAnOrdinaryCaller()
    {
        var card = await SeedCard(null, isTemplate: true);
        var team = await SeedTeam(await SeedMsel());
        var row = await Seeded(BlueprintAppFactory.CardTeam(card.Id, team.Id));
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
    public async Task Get_ForACallerWithNoRoleOnTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var row = await SeedCardTeam(msel);
        var actor = await Actor().SeedAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await Client(actor).GetAsync(TeamCard(row.Id), Ct)).StatusCode);
    }

    /// <remarks>
    /// The service's <c>SingleOrDefaultAsync</c> is checked for null by nothing and its <c>item.Card.MselId</c>
    /// throws, so the controller's own <c>if (team == null) throw new EntityNotFoundException&lt;CardTeam&gt;()</c>
    /// at <c>:106-107</c> is only reachable once the service has returned - which for an unknown id happens
    /// only when the permission check is skipped. So <strong>the 404 exists and only a privileged caller can
    /// see it</strong>. Guarding the service turns the ordinary caller's answer into a 404 too, and reddens
    /// this test.
    /// </remarks>
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

    /// <remarks>
    /// A row pointing at a template card is a 500 here for the same reason it is on
    /// <see cref="GetByCard_ForATemplateCard_Is500ForAnOrdinaryCaller"/>: the null <c>MselId</c> reaches
    /// <c>FindAsync</c> and the result is dereferenced. So a card-team row the API will happily create cannot
    /// be read back by id except by a <c>ViewMsels</c> holder, who skips the branch entirely.
    /// </remarks>
    [Fact]
    public async Task Get_ForARowOnATemplateCard_Is500ForAnOrdinaryCaller()
    {
        var card = await SeedCard(null, isTemplate: true);
        var team = await SeedTeam(await SeedMsel());
        var row = await Seeded(BlueprintAppFactory.CardTeam(card.Id, team.Id));
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

    /// <remarks>
    /// <strong>The permission asymmetry that defines this service.</strong> <c>CreateAsync</c> takes no
    /// permission argument and the controller resolves <c>EditMsels</c> alone, so no MSEL role reaches this
    /// route - the owner of the MSEL, who may create the card
    /// (<c>CardEndpointTests.Create_ForAMselOwner_Is201AndStoresTheCard</c>) and delete it, may not decide
    /// which of their own teams sees it. Every role is refused. Adding the owner/editor branch the card
    /// routes have reddens these cases.
    /// </remarks>
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

    /// <remarks>
    /// Nothing requires the team to belong to the card's MSEL, so one <c>EditMsels</c> holder may show any
    /// exercise's card to any other exercise's team - and because the row is listed under the <em>card's</em>
    /// MSEL (<see cref="GetByMsel_ListsARowWhoseTeamBelongsToAnotherMsel"/>), the MSEL that owns the team
    /// never sees it. Checking that the team's <c>MselId</c> matches the card's reddens this test.
    /// </remarks>
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

    /// <remarks>
    /// <c>CardTeamConfiguration</c> declares <c>(TeamId, CardId)</c> unique, so asking twice is a Postgres
    /// constraint violation surfacing as a 500 rather than the 409 - or the idempotent 200 - a client could
    /// act on. The UI's "show this card to this team" toggle has no way to tell "already shown" from "the
    /// server is broken".
    /// </remarks>
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

    /// <remarks>
    /// Neither id is checked against its table, so a card id that is not there is a foreign-key violation -
    /// a 500 where the rest of the API answers 404 for a parent that is not there. Same for a team id.
    /// </remarks>
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
        Hub.Clear();

        await Post(Client(actor), Body(card.Id, team.Id));

        Assert.Equal(
            [msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.CardTeamCreated));
    }

    /// <remarks>
    /// <c>CardTeamHandler.GetGroups</c> looks the card up to find the MSEL to broadcast to, and a template
    /// card's null <c>MselId</c> becomes the empty string - so the one row type this service creates that
    /// nobody can read back is also the one nobody is told about. Same defect as <c>CardHandler</c>'s.
    /// </remarks>
    [Fact]
    public async Task Create_OnATemplateCard_BroadcastsToAGroupNamedByTheEmptyString()
    {
        var card = await SeedCard(null, isTemplate: true);
        var team = await SeedTeam(await SeedMsel());
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        Hub.Clear();

        await Post(Client(actor), Body(card.Id, team.Id));

        Assert.Equal(
            [string.Empty, MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.CardTeamCreated));
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

    /// <remarks>
    /// Seven of the eight routes in this controller are spelled <c>teamcards</c>; this one is spelled
    /// <c>cardteams</c> (<c>CardTeamController.cs:144</c>). So the route the create's own <c>Location</c>
    /// header implies has no PUT and answers 405, and a client following the header to update what it just
    /// created has to know about the inconsistency. Renaming the route reddens this test - and is a breaking
    /// change for <c>blueprint.ui</c>, which is presumably why it has not happened.
    /// </remarks>
    [Fact]
    public async Task Update_AtTheRouteTheOtherSevenImply_Is405()
    {
        var msel = await SeedMsel();
        var row = await SeedCardTeam(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).PutAsJsonAsync(TeamCard(row.Id), BodyFor(row), Ct);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    /// <remarks>
    /// <c>CardTeamProfile</c> is two bare <c>CreateMap</c> calls, so <c>Id</c> is mapped onto the tracked row
    /// and a key cannot be modified. A body that omits <c>Id</c> sends <c>Guid.Empty</c>, which is as fatal as
    /// a wrong one - both are 500s where the controller's remarks promise the route validates the match.
    /// Ignoring <c>Id</c> in the profile makes both succeed and reddens the test.
    /// </remarks>
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
        Hub.Clear();

        await Put(Client(actor), row.Id, BodyFor(row) with { IsShownOnWall = true });

        var send = Hub.Of(MainHubMethods.CardTeamUpdated).Single(x => x.Group == msel.Id.ToString());
        Assert.Contains("isShownOnWall", Assert.IsType<string[]>(send.Args[1]));
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
        Hub.Clear();

        await Client(actor).DeleteAsync(TeamCard(row.Id), Ct);

        Assert.Equal(
            [msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.CardTeamDeleted));
        Assert.Equal(row.Id, Assert.IsType<Guid>(Hub.Of(MainHubMethods.CardTeamDeleted)[0].Payload));
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE teams/{teamId}/cards/{cardId}
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// <strong>The defect this file exists to record.</strong> <c>CardTeamController.cs:192</c> calls
    /// <c>DeleteByIdsAsync(teamId, cardId, ct)</c> and the service's signature is
    /// <c>DeleteByIdsAsync(Guid cardId, Guid teamId, …)</c>, so the route's own ids arrive swapped, the
    /// <c>Where</c> matches nothing, and the caller is told the row does not exist. Both parameters are
    /// <c>Guid</c>, so the compiler has nothing to say. Swapping the arguments at the call site makes the
    /// first half of this test a 204 and the second a 404, reddening both.
    /// </remarks>
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

    /// <remarks>
    /// Reached only by passing the ids the wrong way round, which is the only way this route works at all.
    /// The broadcast and the group are right; it is the arguments that are not.
    /// </remarks>
    [Fact]
    public async Task DeleteByIds_WhenItWorks_BroadcastsToTheMselGroup()
    {
        var msel = await SeedMsel();
        var row = await SeedCardTeam(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        Hub.Clear();

        await Client(actor).DeleteAsync(CardOfTeam(row.CardId, row.TeamId), Ct);

        Assert.Equal(
            [msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.CardTeamDeleted));
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

        var response = await AnonymousClient.SendAsync(request, Ct);

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
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        return msel;
    }

    private async Task<CardEntity> SeedCard(Guid? mselId, bool isTemplate = false)
    {
        var card = BlueprintAppFactory.Card(mselId, isTemplate: isTemplate);
        await Seed(card);

        return card;
    }

    private Task<CardEntity> SeedCard(MselEntity msel) => SeedCard(msel.Id);

    private async Task<TeamEntity> SeedTeam(MselEntity msel)
    {
        var team = BlueprintAppFactory.Team(msel.Id);
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

        return await Seeded(BlueprintAppFactory.CardTeam(card.Id, team.Id, isShownOnWall, canPostArticles));
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
