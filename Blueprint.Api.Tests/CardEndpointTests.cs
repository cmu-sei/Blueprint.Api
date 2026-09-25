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
/// <c>CardService</c> / <c>CardController</c> - the eight routes behind the Gallery Cards tab, and the
/// column headings a MSEL's articles are filed under when it is pushed to Gallery.
/// </summary>
/// <remarks>
/// <para>
/// A card is exercise content and a reference-data template at the same time, and that is the whole shape of
/// this service: <c>CardEntity.MselId</c> is nullable, so every write method has <em>two</em> permission
/// branches and chooses between them on whether the id is set. A card with a MSEL belongs to that MSEL's
/// owner or editor; a card without one belongs to whoever holds <c>ManageGalleryCards</c>. Nothing keeps
/// <c>IsTemplate</c> in step with <c>MselId</c>, so all four combinations are reachable and one of them - no
/// MSEL, not a template - is reachable by no list route in the API.
/// </para>
/// <para>
/// <strong><c>GET cards/templates</c> asks the caller for nothing at all.</strong> No permission, no MSEL, no
/// role: every card template in the installation is readable by anybody signed in. It is the only route in
/// this file with no authorization of any kind, and the only one in the two card controllers. See
/// <see cref="Templates_ForACallerWithNoPermissionAndNoRoleAnywhere_Is200"/>.
/// </para>
/// <para>
/// <strong><c>UpdateAsync</c> takes its permission decision from the request body</strong> - the fifth
/// instance of this shape in the branch, after <c>OrganizationService</c>, <c>ScenarioEventService</c>,
/// <c>DataOptionService</c> and <c>MoveService</c> - and here it is worse than any of them, because the body
/// decides <em>which</em> of the two branches runs. So there are two escalations rather than one. A caller
/// who owns any MSEL may name their own MSEL in the body and <em>pull</em> any other MSEL's card into it
/// (<see cref="Update_ChoosesItsPermissionBranchFromTheRequestBody"/>); and a caller holding only
/// <c>ManageGalleryCards</c> may omit the id and detach any MSEL's card, which does not make it a template -
/// it makes it unreachable (<see cref="Update_ThatOmitsTheMselId_WithManageGalleryCardsOnly_DetachesTheCard"/>).
/// <c>DeleteAsync</c> eleven lines below reads the <em>stored</em> row and is the model to copy; it is also
/// why a stranger deleting an unknown id gets a 404 and a stranger updating one gets a 403.
/// </para>
/// <para>
/// <strong>No card operation marks its MSEL modified.</strong> Every other service in the exercise-content
/// tier calls <c>ServiceUtilities.SetMselModifiedAsync</c> on every write; this one never does, on any of its
/// five write paths. So a MSEL's <c>DateModified</c> does not move when its Gallery cards change.
/// </para>
/// <para>
/// <strong>Reading a card by id is stricter than listing them</strong>, and inconsistently so. The list uses
/// <c>MselViewRequirement</c> with an <c>IsTemplate</c> fall-through, so a template MSEL's cards are readable
/// by a stranger; the single read uses <c>MselUserRequirement</c>, which throws on a null MSEL id, so a
/// template card - listed to everybody by <c>cards/templates</c> - is a 500 when read by its own id.
/// </para>
/// <para>
/// <strong><c>GetAsync</c>'s null check is dead</strong>, <c>SingleAsync</c> having already thrown, and the
/// exception it would have thrown is <c>EntityNotFoundException&lt;DataValueEntity&gt;</c> carrying
/// "DataValue not found" - the same copied line as <c>MoveService</c> and <c>OrganizationService</c>.
/// </para>
/// <para>
/// Per this branch's rule, every test characterizes rather than fixes, and says what fixing it will do to
/// the test.
/// </para>
/// </remarks>
public class CardEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET cards/templates
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Templates_ReturnsOnlyTheCardsMarkedAsTemplates()
    {
        var template = await SeedCard(null, isTemplate: true);
        await SeedCard((await SeedMsel()).Id);
        var actor = await Actor().SeedAsync();

        var cards = await Read<List<ViewModels.Card>>(await Client(actor).GetAsync(Templates, Ct));

        Assert.Equal(template.Id, Assert.Single(cards).Id);
    }

    /// <remarks>
    /// <c>GetTemplatesAsync</c> is the only method in either card service with no permission check, and
    /// <c>GetCardTemplates</c> the only action in either controller that resolves no <c>SystemPermission</c>.
    /// Fixing it - gating the route on <c>ManageGalleryCards</c>, or on nothing narrower than
    /// <c>ViewMsels</c> - turns this into a 403 and reddens the test.
    /// </remarks>
    [Fact]
    public async Task Templates_ForACallerWithNoPermissionAndNoRoleAnywhere_Is200()
    {
        await SeedCard(null, isTemplate: true);
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync(Templates, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(await Read<List<ViewModels.Card>>(response));
    }

    /// <remarks>
    /// The filter is on <c>IsTemplate</c> alone, so a card that is both a template and attached to a MSEL is
    /// listed here - which is how an exercise's own card reaches the template list a stranger may read.
    /// </remarks>
    [Fact]
    public async Task Templates_IncludesAMselsCardIfItIsFlaggedAsATemplate()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel.Id, isTemplate: true);
        var actor = await Actor().SeedAsync();

        var cards = await Read<List<ViewModels.Card>>(await Client(actor).GetAsync(Templates, Ct));

        Assert.Equal(card.Id, Assert.Single(cards).Id);
        Assert.Equal(msel.Id, Assert.Single(cards).MselId);
    }

    /// <remarks>
    /// <c>Startup.cs:164-167</c> adds <c>JsonIntegerConverter</c>, so both of a card's integers cross the
    /// wire as JSON strings. The download route does not use the MVC options and writes them as numbers; see
    /// <see cref="DownloadJson_WritesPascalCaseNamesAndRawIntegers"/>.
    /// </remarks>
    [Fact]
    public async Task Templates_SerializesTheIntegersAsStrings()
    {
        await SeedCard(null, move: 3, inject: 4, isTemplate: true);
        var actor = await Actor().SeedAsync();

        var body = await (await Client(actor).GetAsync(Templates, Ct)).Content.ReadAsStringAsync(Ct);

        Assert.Contains("\"move\":\"3\"", body);
        Assert.Contains("\"inject\":\"4\"", body);
    }

    // ---------------------------------------------------------------------------------------------
    // GET msels/{mselId}/cards
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByMsel_ReturnsEveryCardOnTheMsel()
    {
        var msel = await SeedMsel();
        await SeedCard(msel.Id, move: 1);
        await SeedCard(msel.Id, move: 2);
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var cards = await GetCards(Client(actor), msel.Id);

        Assert.Equal([1, 2], cards.Select(x => x.Move).Order());
    }

    [Fact]
    public async Task GetByMsel_DoesNotReturnAnotherMselsCards()
    {
        var msel = await SeedMsel();
        var mine = await SeedCard(msel.Id);
        await SeedCard((await SeedMsel()).Id);
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var cards = await GetCards(Client(actor), msel.Id);

        Assert.Equal(mine.Id, Assert.Single(cards).Id);
    }

    /// <remarks>
    /// The filter is <c>MselId == mselId</c>, so a template - whose <c>MselId</c> is null - is in no MSEL's
    /// list, however the flag is set.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_DoesNotReturnTheTemplates()
    {
        var msel = await SeedMsel();
        await SeedCard(null, isTemplate: true);
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        Assert.Empty(await GetCards(Client(actor), msel.Id));
    }

    /// <remarks>
    /// <c>MselViewRequirement</c> accepts a unit member holding any of five roles and refuses
    /// <c>MselRole.Evaluator</c>, which is absent from its list - so the person evaluating a running exercise
    /// cannot read the cards its articles are filed under. Pinned across the board in
    /// <c>MselViewRequirementTests</c>; asserted here because this is where it is felt.
    /// </remarks>
    [Theory]
    [InlineData(MselRole.Owner, HttpStatusCode.OK)]
    [InlineData(MselRole.Editor, HttpStatusCode.OK)]
    [InlineData(MselRole.Approver, HttpStatusCode.OK)]
    [InlineData(MselRole.MoveEditor, HttpStatusCode.OK)]
    [InlineData(MselRole.Viewer, HttpStatusCode.OK)]
    [InlineData(MselRole.Evaluator, HttpStatusCode.Forbidden)]
    public async Task GetByMsel_TheRolesThatMayListAMselsCards(MselRole role, HttpStatusCode expected)
    {
        var msel = await SeedMsel();
        await SeedCard(msel.Id);
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Client(actor).GetAsync(CardsOf(msel.Id), Ct);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task GetByMsel_WithViewMselsAndNoRoleOnTheMsel_Is200()
    {
        var msel = await SeedMsel();
        await SeedCard(msel.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Single(await GetCards(Client(actor), msel.Id));
    }

    [Fact]
    public async Task GetByMsel_ForACallerWithNoRoleOnTheMsel_Is403()
    {
        var msel = await SeedMsel();
        await SeedCard(msel.Id);
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync(CardsOf(msel.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetByMsel_ForTheMselsCreator_Is200()
    {
        var actor = await Actor().SeedAsync();
        var msel = await SeedMsel(createdBy: actor.Id);
        await SeedCard(msel.Id);

        Assert.Single(await GetCards(Client(actor), msel.Id));
    }

    /// <remarks>
    /// The permission branch falls through for a template MSEL, so its cards are world-readable to anybody
    /// signed in. That is presumably deliberate for the template library, but the check is reached only after
    /// <c>MselViewRequirement</c> has already refused, so it also means "this MSEL is a template" is a way
    /// past the MSEL's own access control. Giving templates their own route, or requiring a permission here,
    /// turns this into a 403 and reddens the test.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_ForATemplateMsel_IsReadableByAStranger()
    {
        var msel = BlueprintAppFactory.Msel(isTemplate: true);
        await Seed(msel);
        var card = await SeedCard(msel.Id);
        var actor = await Actor().SeedAsync();

        var cards = await GetCards(Client(actor), msel.Id);

        Assert.Equal(card.Id, Assert.Single(cards).Id);
    }

    /// <remarks>
    /// <c>MselViewRequirement.IsMet</c> answers false for a MSEL that is not there rather than throwing, and
    /// the <c>FindAsync</c> two lines later is then dereferenced unguarded - so the answer to "is this MSEL
    /// there" is a 500 for an ordinary caller and an empty list for one holding <c>ViewMsels</c>
    /// (<see cref="GetByMsel_ForAMselThatIsNotThere_WithViewMsels_IsAnEmptyList"/>). Adding the null guard
    /// makes this a 403 - or a 404, if the guard says so - and reddens the test.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_Is500RatherThanA404()
    {
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync(CardsOf(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("Object reference not set to an instance of an object.", await Title(response));
    }

    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_WithViewMsels_IsAnEmptyList()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Empty(await GetCards(Client(actor), Guid.NewGuid()));
    }

    // ---------------------------------------------------------------------------------------------
    // GET cards/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ReturnsTheCard()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel.Id, move: 5, inject: 6);
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var answer = await GetCard(Client(actor), card.Id);

        Assert.Equal(card.Id, answer.Id);
        Assert.Equal(card.Name, answer.Name);
        Assert.Equal(msel.Id, answer.MselId);
        Assert.Equal(5, answer.Move);
        Assert.Equal(6, answer.Inject);
    }

    [Fact]
    public async Task Get_SerializesTheIntegersAsStrings()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel.Id, move: 3, inject: 4);
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var body = await (await Client(actor).GetAsync(Card(card.Id), Ct)).Content.ReadAsStringAsync(Ct);

        Assert.Contains("\"move\":\"3\"", body);
        Assert.Contains("\"inject\":\"4\"", body);
    }

    /// <remarks>
    /// The single read uses <c>MselUserRequirement</c> where the list uses <c>MselViewRequirement</c>, and
    /// that helper has no role list at all - unit membership alone satisfies it - so every role reads a card
    /// including <c>Evaluator</c>, which cannot list them
    /// (<see cref="GetByMsel_TheRolesThatMayListAMselsCards"/>). Two routes onto the same row disagreeing
    /// about one role is the whole finding; both halves are asserted so the contradiction is on one screen.
    /// </remarks>
    [Theory]
    [InlineData(MselRole.Owner)]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.MoveEditor)]
    [InlineData(MselRole.Viewer)]
    [InlineData(MselRole.Evaluator)]
    public async Task Get_EveryRoleMayReadACardIncludingTheOneThatCannotListThem(MselRole role)
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel.Id);
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Client(actor).GetAsync(Card(card.Id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <remarks>
    /// <c>MselUserRequirement</c> is the one helper of the eight that accepts a unit member holding no MSEL
    /// role, so dropping the role rows <c>TestActorBuilder</c> wrote leaves a caller who may read this card
    /// and nothing else about the MSEL.
    /// </remarks>
    [Fact]
    public async Task Get_ForAUnitMemberHoldingNoRoleOnTheMsel_Is200()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel.Id);
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();
        await RemoveTheRoleRows(actor.Id);

        var response = await Client(actor).GetAsync(Card(card.Id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(actor).GetAsync(CardsOf(msel.Id), Ct)).StatusCode);
    }

    [Fact]
    public async Task Get_ForACallerWithNoRoleOnTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel.Id);
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync(Card(card.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_WithViewMselsAndNoRoleOnTheMsel_Is200()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Equal(card.Id, (await GetCard(Client(actor), card.Id)).Id);
    }

    /// <remarks>
    /// <c>SingleAsync</c> throws before the null check below it can run, so an unknown id is a 500 for
    /// everybody - where an unknown id on the PUT and DELETE routes is a 404. Changing the call to
    /// <c>SingleOrDefaultAsync</c> makes the dead check live and this a 404 (naming <c>DataValue</c>, wrongly),
    /// which reddens the test.
    /// </remarks>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is500RatherThanThe404TheDeadCheckPromises()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync(Card(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("Sequence contains no elements.", await Title(response));
    }

    /// <remarks>
    /// A template card has no <c>MselId</c>, and <c>MselUserRequirement.IsMet</c> dereferences a
    /// <c>FirstOrDefaultAsync</c> that a null id cannot match - so the one card every caller may see in
    /// <c>GET cards/templates</c> is a 500 when read by its own id. Guarding that helper (the fix injected by
    /// batch MV22 for the move unit) turns this into a 403, which reddens the test; making the template
    /// branch here match <c>GetByMselAsync</c>'s <c>IsTemplate</c> fall-through would make it a 200.
    /// </remarks>
    [Fact]
    public async Task Get_ForATemplateCard_Is500ForAnOrdinaryCaller()
    {
        var card = await SeedCard(null, isTemplate: true);
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync(Card(card.Id), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("Object reference not set to an instance of an object.", await Title(response));
    }

    [Fact]
    public async Task Get_ForATemplateCard_WithViewMsels_Is200()
    {
        var card = await SeedCard(null, isTemplate: true);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Equal(card.Id, (await GetCard(Client(actor), card.Id)).Id);
    }

    // ---------------------------------------------------------------------------------------------
    // POST cards
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_ForAMselOwner_Is201AndStoresTheCard()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id, move: 2, inject: 3));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await Read<ViewModels.Card>(response);
        var stored = await Stored(created.Id);
        Assert.Equal(msel.Id, stored.MselId);
        Assert.Equal(2, stored.Move);
        Assert.Equal(3, stored.Inject);
        Assert.False(stored.IsTemplate);
    }

    /// <remarks>
    /// <c>Startup.cs:203-205</c> sets <c>RouteOptions.LowercaseUrls</c>, so the generated header is
    /// <c>/api/cards/{id}</c> whatever case the route template is written in, and it is absolute.
    /// </remarks>
    [Fact]
    public async Task Create_AnswersALocationHeaderNamingTheCard()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id));

        var created = await Read<ViewModels.Card>(response);
        Assert.EndsWith($"/api/cards/{created.Id}", response.Headers.Location.ToString());
    }

    /// <remarks>
    /// <c>CreateAsync</c> keeps a non-empty <c>Id</c> from the body, and <c>CardProfile</c> ignores only
    /// <c>Msel</c>, so the client chooses the key. Harmless in itself; it is the same expression that makes a
    /// PUT with a mismatched body id a 500.
    /// </remarks>
    [Fact]
    public async Task Create_KeepsTheIdFromTheBody()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var chosen = Guid.NewGuid();

        var response = await Post(Client(actor), Body(msel.Id) with { Id = chosen });

        Assert.Equal(chosen, (await Read<ViewModels.Card>(response)).Id);
        Assert.NotNull(await Stored(chosen));
    }

    /// <remarks>
    /// <c>BlueprintContext.SaveEntries</c> stamps every audit field on save, so the hostile 1999 dates and the
    /// bogus <c>ModifiedBy</c> in this body are all discarded. <c>CardController</c> also writes
    /// <c>CreatedBy</c> at :109 and <c>CardService</c> again at :107 - the second makes the first dead, the
    /// same redundancy as <c>MoveController</c>'s.
    /// </remarks>
    [Fact]
    public async Task Create_StampsTheAuditFieldsOnTheServer()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var before = DateTime.UtcNow;

        var response = await Post(Client(actor), Body(msel.Id) with
        {
            CreatedBy = Guid.NewGuid(),
            DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ModifiedBy = Guid.NewGuid(),
            DateModified = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });

        var stored = await Stored((await Read<ViewModels.Card>(response)).Id);
        Assert.Equal(actor.Id, stored.CreatedBy);
        Assert.InRange(stored.DateCreated, before, DateTime.UtcNow);
        Assert.Null(stored.ModifiedBy);
        Assert.Null(stored.DateModified);
    }

    /// <remarks>
    /// The MSEL branch asks for owner or editor, so four of the six roles are refused - including
    /// <c>Approver</c> and <c>MoveEditor</c>, which may change a scenario event and a move respectively.
    /// </remarks>
    [Theory]
    [InlineData(MselRole.Owner, HttpStatusCode.Created)]
    [InlineData(MselRole.Editor, HttpStatusCode.Created)]
    [InlineData(MselRole.Approver, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.MoveEditor, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Viewer, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Evaluator, HttpStatusCode.Forbidden)]
    public async Task Create_TheRolesThatMayAddACardToAMsel(MselRole role, HttpStatusCode expected)
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id));

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithEditMselsAndNoRoleOnTheMsel_Is201()
    {
        var msel = await SeedMsel();
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        Assert.Equal(HttpStatusCode.Created, (await Post(Client(actor), Body(msel.Id))).StatusCode);
    }

    [Fact]
    public async Task Create_ForACallerWithNoRoleOnTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var actor = await Actor().SeedAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await Post(Client(actor), Body(msel.Id))).StatusCode);
    }

    /// <remarks>
    /// Which of the two permissions a caller needs is decided by the body, not by the route, so the
    /// Gallery-card manager - whose whole job is cards - is refused a card that names a MSEL, and the MSEL's
    /// owner is refused one that does not. Two tests, one for each direction.
    /// </remarks>
    [Fact]
    public async Task Create_WithManageGalleryCardsOnly_ForACardNamingAMsel_Is403()
    {
        var msel = await SeedMsel();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGalleryCards).SeedAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await Post(Client(actor), Body(msel.Id))).StatusCode);
    }

    [Fact]
    public async Task Create_WithNoMselId_RequiresManageGalleryCards()
    {
        var msel = await SeedMsel();
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var gallery = await Actor().WithSystemPermissions(SystemPermission.ManageGalleryCards).SeedAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await Post(Client(owner), Body(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Post(Client(gallery), Body(null))).StatusCode);
    }

    /// <remarks>
    /// Nothing sets <c>IsTemplate</c> on this path - only <c>UploadJsonAsync</c> does - so a card created
    /// through the Gallery-card branch with the flag left alone has no MSEL and is not a template. It is
    /// therefore in no MSEL's list (<c>MselId == mselId</c> matches nothing) and not in the template list
    /// (<c>IsTemplate</c> is false), and the only route that reaches it is <c>GET cards/{id}</c>, which is a
    /// 500 for an ordinary caller. Defaulting <c>IsTemplate</c> to true when there is no MSEL, or rejecting
    /// the combination, reddens this test.
    /// </remarks>
    [Fact]
    public async Task Create_WithNoMselIdAndNoTemplateFlag_IsReachableByNoListRoute()
    {
        var gallery = await Actor().WithSystemPermissions(SystemPermission.ManageGalleryCards).SeedAsync();
        var root = await Actor().WithAllSystemPermissions().SeedAsync();

        var created = await Read<ViewModels.Card>(await Post(Client(gallery), Body(null)));

        Assert.False((await Stored(created.Id)).IsTemplate);
        Assert.Empty(await Read<List<ViewModels.Card>>(await Client(root).GetAsync(Templates, Ct)));
        Assert.Equal(created.Id, (await GetCard(Client(root), created.Id)).Id);
    }

    /// <remarks>
    /// <c>CardEntity</c> carries no <c>[SanitizeHtml]</c> attribute on either string property, so
    /// <c>SanitizerInterceptor</c> leaves both alone - unlike <c>MoveEntity.SituationDescription</c>,
    /// <c>OrganizationEntity.Description</c> and the rest of the exercise-content tier. A card's description
    /// is rendered by Gallery, so this is the one of the two that matters. Adding the attribute strips the
    /// script tag and reddens this test.
    /// </remarks>
    [Fact]
    public async Task Create_DoesNotSanitizeTheDescription()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id) with
        {
            Name = "<script>alert('name')</script>",
            Description = "<script>alert('description')</script>ok"
        });

        var stored = await Stored((await Read<ViewModels.Card>(response)).Id);
        Assert.Equal("<script>alert('description')</script>ok", stored.Description);
        Assert.Equal("<script>alert('name')</script>", stored.Name);
    }

    [Fact]
    public async Task Create_BroadcastsToTheMselGroupAndTheAdminGroup()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        Hub.Clear();

        var created = await Read<ViewModels.Card>(await Post(Client(actor), Body(msel.Id)));

        Assert.Equal(
            [msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.CardCreated));
        Assert.Equal(created.Id, Assert.IsType<ViewModels.Card>(Hub.Of(MainHubMethods.CardCreated)[0].Payload).Id);
    }

    /// <remarks>
    /// <c>CardService</c> is the only service in the exercise-content tier that never calls
    /// <c>ServiceUtilities.SetMselModifiedAsync</c> - not on create, update, delete, upload or download. So
    /// nothing records that a MSEL's Gallery cards changed, and anything keyed on the MSEL's
    /// <c>DateModified</c> - a cache, an export, a "last edited" column - does not notice. Adding the call
    /// reddens this test and the two below it.
    /// </remarks>
    [Fact]
    public async Task Create_DoesNotMarkTheMselModified()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Post(Client(actor), Body(msel.Id));

        Assert.Null((await StoredMsel(msel.Id)).DateModified);
    }

    [Fact]
    public async Task Create_WithNoBody_Is400()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).PostAsync(Cards, EmptyJson(), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <remarks>
    /// The body names a MSEL that is not there, so <c>MselOwnerRequirement.IsMet</c> dereferences a
    /// <c>FirstOrDefaultAsync</c> that matched nothing - the Phase 2 table's "missing MSEL: throws" column.
    /// Guarding that helper turns this into the 403 the branch intended and reddens the test.
    /// </remarks>
    [Fact]
    public async Task Create_ForAMselThatIsNotThere_Is500ForAnOrdinaryCaller()
    {
        var actor = await Actor().SeedAsync();

        var response = await Post(Client(actor), Body(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("Object reference not set to an instance of an object.", await Title(response));
    }

    // ---------------------------------------------------------------------------------------------
    // PUT cards/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_ForAMselOwner_Is200AndStoresTheChange()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel.Id);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Put(Client(actor), card.Id, BodyFor(card) with { Name = "renamed", Move = 9 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await Stored(card.Id);
        Assert.Equal("renamed", stored.Name);
        Assert.Equal(9, stored.Move);
        Assert.Equal(msel.Id, stored.MselId);
    }

    [Fact]
    public async Task Update_StampsTheAuditFieldsAndPreservesCreation()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel.Id);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var created = (await Stored(card.Id)).DateCreated;
        var before = DateTime.UtcNow;

        await Put(Client(actor), card.Id, BodyFor(card) with
        {
            Name = "renamed",
            CreatedBy = Guid.NewGuid(),
            DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ModifiedBy = Guid.NewGuid()
        });

        var stored = await Stored(card.Id);
        Assert.Equal(card.CreatedBy, stored.CreatedBy);
        Assert.Equal(created, stored.DateCreated);
        Assert.Equal(actor.Id, stored.ModifiedBy);
        Assert.NotNull(stored.DateModified);
        Assert.InRange(stored.DateModified.Value, before, DateTime.UtcNow);
    }

    [Theory]
    [InlineData(MselRole.Owner, HttpStatusCode.OK)]
    [InlineData(MselRole.Editor, HttpStatusCode.OK)]
    [InlineData(MselRole.Approver, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.MoveEditor, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Viewer, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Evaluator, HttpStatusCode.Forbidden)]
    public async Task Update_TheRolesThatMayChangeACard(MselRole role, HttpStatusCode expected)
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel.Id);
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Put(Client(actor), card.Id, BodyFor(card) with { Name = "renamed" });

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Update_WithEditMselsAndNoRoleOnTheMsel_Is200()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        Assert.Equal(HttpStatusCode.OK, (await Put(Client(actor), card.Id, BodyFor(card))).StatusCode);
    }

    [Fact]
    public async Task Update_ForACallerWithNoRoleOnTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel.Id);
        var actor = await Actor().SeedAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await Put(Client(actor), card.Id, BodyFor(card))).StatusCode);
    }

    /// <remarks>
    /// The permission branch runs before the row is looked up, so which status an unknown id gets depends on
    /// who asks: the 404 is only reachable by a caller who would have been allowed to change the card if it
    /// had been there. <c>DeleteAsync</c> is the other way round, and is the correct model
    /// (<see cref="Delete_ForAnIdThatIsNotThere_Is404EvenForAStranger"/>).
    /// </remarks>
    [Theory]
    [InlineData(true, HttpStatusCode.NotFound)]
    [InlineData(false, HttpStatusCode.Forbidden)]
    public async Task Update_ForAnIdThatIsNotThere_Is404OrA403(bool owns, HttpStatusCode expected)
    {
        var msel = await SeedMsel();
        var builder = Actor();
        var actor = await (owns ? builder.OnMsel(msel, MselRole.Owner) : builder).SeedAsync();
        var id = Guid.NewGuid();

        var response = await Put(Client(actor), id, Body(msel.Id) with { Id = id });

        Assert.Equal(expected, response.StatusCode);
    }

    /// <remarks>
    /// <c>CardProfile</c> maps <c>Id</c> onto the tracked row and a key cannot be modified, so a PUT whose
    /// body carries a different id - or omits it, which sends <c>Guid.Empty</c> - is a 500 rather than the 400
    /// the controller's own remarks ("The ID from the route MUST MATCH the ID contained in the card
    /// parameter") promise. Nothing validates the match. Ignoring <c>Id</c> in the profile, as
    /// <c>ScenarioEventProfile</c> does, makes both of these succeed and reddens the test.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Update_WithABodyIdThatIsNotTheRoutes_Is500(bool omitted)
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel.Id);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Put(
            Client(actor), card.Id, BodyFor(card) with { Id = omitted ? Guid.Empty : Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.StartsWith("The property 'CardEntity.Id'", await Title(response));
    }

    /// <remarks>
    /// <strong>The first of this route's two escalations.</strong> <c>UpdateAsync</c> reads
    /// <c>card.MselId</c> from the <em>request body</em>, so the MSEL whose permissions are checked is the one
    /// the caller asks for rather than the one the card is stored on - and the mapper then writes that id onto
    /// the row. An owner of any MSEL may therefore pull every other MSEL's cards into their own, and the MSEL
    /// that loses the card is told nothing: its <c>DateModified</c> does not move and no broadcast names it.
    /// <c>DeleteAsync</c> eleven lines below reads the stored row first and is the model to copy; making this
    /// method do the same reddens this test.
    /// </remarks>
    [Fact]
    public async Task Update_ChoosesItsPermissionBranchFromTheRequestBody()
    {
        var mine = await SeedMsel();
        var theirs = await SeedMsel();
        var card = await SeedCard(theirs.Id);
        var actor = await Actor().OnMsel(mine, MselRole.Owner).SeedAsync();
        Hub.Clear();

        var response = await Put(Client(actor), card.Id, BodyFor(card) with { MselId = mine.Id, Name = "pulled" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await Stored(card.Id);
        Assert.Equal(mine.Id, stored.MselId);
        Assert.Equal("pulled", stored.Name);
        Assert.Null((await StoredMsel(theirs.Id)).DateModified);
        Assert.DoesNotContain(theirs.Id.ToString(), Hub.Recipients(MainHubMethods.CardUpdated));
    }

    /// <remarks>
    /// <strong>The second escalation, and the more destructive one.</strong> A body that omits
    /// <c>mselId</c> takes the <em>other</em> branch, so a caller holding only <c>ManageGalleryCards</c> - a
    /// permission about the template library, granting nothing on any MSEL - may detach any MSEL's card. The
    /// card is not turned into a template, so afterwards it is in no MSEL's list and not in the template list:
    /// the row survives, its <c>CardTeam</c> rows survive, and no route in the API will list it again. Note
    /// <c>ViewModels.Card.MselId</c> is <c>Guid?</c>, so "omitted" and "null" are the same request here -
    /// unlike <c>MoveService</c>, where the id is non-nullable and a null is a 400.
    /// </remarks>
    [Fact]
    public async Task Update_ThatOmitsTheMselId_WithManageGalleryCardsOnly_DetachesTheCard()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel.Id);
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);
        await Seed(BlueprintAppFactory.CardTeam(card.Id, team.Id));
        var gallery = await Actor().WithSystemPermissions(SystemPermission.ManageGalleryCards).SeedAsync();
        var root = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Put(Client(gallery), card.Id, BodyFor(card) with { MselId = null });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await Stored(card.Id);
        Assert.Null(stored.MselId);
        Assert.False(stored.IsTemplate);
        Assert.Empty(await Read<List<ViewModels.Card>>(await Client(root).GetAsync(Templates, Ct)));
        Assert.Empty(await GetCards(Client(root), msel.Id));
        Assert.Equal(1, await CountCardTeams(card.Id));
    }

    /// <remarks>
    /// <c>CardHandler.GetGroups</c> calls <c>ToString()</c> on a <c>Guid?</c>, so a card with no MSEL
    /// broadcasts to a group named by the empty string - which nobody joins. A detached card therefore
    /// notifies nobody but the admin group, and the MSEL that lost it hears nothing at all: the same defect
    /// <c>OrganizationHandler</c> and <c>DataFieldHandler</c> have. Broadcasting to the MSEL the row
    /// <em>was</em> on reddens this test.
    /// </remarks>
    [Fact]
    public async Task Update_ThatDetachesACard_BroadcastsToAGroupNamedByTheEmptyString()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel.Id);
        var gallery = await Actor().WithSystemPermissions(SystemPermission.ManageGalleryCards).SeedAsync();
        Hub.Clear();

        await Put(Client(gallery), card.Id, BodyFor(card) with { MselId = null });

        Assert.Equal(
            [string.Empty, MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.CardUpdated));
    }

    [Fact]
    public async Task Update_BroadcastsWhatChangedToTheMselGroup()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel.Id);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        Hub.Clear();

        await Put(Client(actor), card.Id, BodyFor(card) with { Name = "renamed" });

        var send = Hub.Of(MainHubMethods.CardUpdated).Single(x => x.Group == msel.Id.ToString());
        Assert.Contains("name", Assert.IsType<string[]>(send.Args[1]));
    }

    [Fact]
    public async Task Update_DoesNotMarkTheMselModified()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel.Id);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Put(Client(actor), card.Id, BodyFor(card) with { Name = "renamed" });

        Assert.Null((await StoredMsel(msel.Id)).DateModified);
    }

    [Fact]
    public async Task Update_DoesNotSanitizeTheDescription()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel.Id);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Put(Client(actor), card.Id, BodyFor(card) with { Description = "<script>alert(1)</script>ok" });

        Assert.Equal("<script>alert(1)</script>ok", (await Stored(card.Id)).Description);
    }

    /// <remarks>
    /// A template card takes the Gallery-card branch, so the template library's manager may edit it and a
    /// MSEL owner may not - and may not adopt it either, because naming a MSEL in the body moves the decision
    /// to that MSEL, which they do own. So the second call succeeds: a MSEL owner cannot edit a template in
    /// place but can take it.
    /// </remarks>
    [Fact]
    public async Task Update_OfATemplateCard_IsRefusedToAMselOwnerUntilTheyClaimIt()
    {
        var msel = await SeedMsel();
        var template = await SeedCard(null, isTemplate: true);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var refused = await Put(Client(actor), template.Id, BodyFor(template) with { Name = "renamed" });
        var allowed = await Put(Client(actor), template.Id, BodyFor(template) with { MselId = msel.Id });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(msel.Id, (await Stored(template.Id)).MselId);
        Assert.True((await Stored(template.Id)).IsTemplate);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE cards/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_ForAMselOwner_Is204AndRemovesTheCard()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel.Id);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync(Card(card.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await Stored(card.Id));
    }

    /// <remarks>
    /// The one write method that reads the stored row before deciding, and so the only one a body cannot
    /// mislead: an owner of another MSEL is refused however they ask. This is the shape
    /// <see cref="Update_ChoosesItsPermissionBranchFromTheRequestBody"/> should have.
    /// </remarks>
    [Fact]
    public async Task Delete_TakesItsPermissionDecisionFromTheStoredRow()
    {
        var mine = await SeedMsel();
        var theirs = await SeedMsel();
        var card = await SeedCard(theirs.Id);
        var actor = await Actor().OnMsel(mine, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync(Card(card.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(await Stored(card.Id));
    }

    [Theory]
    [InlineData(MselRole.Owner, HttpStatusCode.NoContent)]
    [InlineData(MselRole.Editor, HttpStatusCode.NoContent)]
    [InlineData(MselRole.Approver, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.MoveEditor, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Viewer, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Evaluator, HttpStatusCode.Forbidden)]
    public async Task Delete_TheRolesThatMayRemoveACard(MselRole role, HttpStatusCode expected)
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel.Id);
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Client(actor).DeleteAsync(Card(card.Id), Ct);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Delete_WithEditMselsAndNoRoleOnTheMsel_Is204()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await Client(actor).DeleteAsync(Card(card.Id), Ct)).StatusCode);
    }

    /// <remarks>
    /// Existence is checked before permission here, the opposite of <c>UpdateAsync</c>, so an unknown id is a
    /// 404 for everybody - including a caller who would have been refused a card that was there. That is the
    /// right order for the permission decision and the wrong one for not leaking which ids exist; the
    /// asymmetry between the two methods is the finding, not either answer alone.
    /// </remarks>
    [Fact]
    public async Task Delete_ForAnIdThatIsNotThere_Is404EvenForAStranger()
    {
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).DeleteAsync(Card(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Card not found", await Title(response));
    }

    [Fact]
    public async Task Delete_OfATemplateCard_RequiresManageGalleryCards()
    {
        var msel = await SeedMsel();
        var refusedTemplate = await SeedCard(null, isTemplate: true);
        var allowedTemplate = await SeedCard(null, isTemplate: true);
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var gallery = await Actor().WithSystemPermissions(SystemPermission.ManageGalleryCards).SeedAsync();

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await Client(owner).DeleteAsync(Card(refusedTemplate.Id), Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await Client(gallery).DeleteAsync(Card(allowedTemplate.Id), Ct)).StatusCode);
    }

    [Fact]
    public async Task Delete_BroadcastsTheIdToTheMselGroup()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel.Id);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        Hub.Clear();

        await Client(actor).DeleteAsync(Card(card.Id), Ct);

        Assert.Equal(
            [msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.CardDeleted));
        Assert.Equal(card.Id, Assert.IsType<Guid>(Hub.Of(MainHubMethods.CardDeleted)[0].Payload));
    }

    /// <remarks>
    /// <c>CardTeamConfiguration</c> declares the join row's card foreign key <c>DeleteBehavior.Cascade</c>, so
    /// the teams a card was shown to go with it. Nothing in this service does that; it is an EF convention,
    /// which is why no mutation to the two card files can redden this test.
    /// </remarks>
    [Fact]
    public async Task Delete_TakesItsCardTeamRowsWithIt()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel.Id);
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);
        await Seed(BlueprintAppFactory.CardTeam(card.Id, team.Id));
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Client(actor).DeleteAsync(Card(card.Id), Ct);

        Assert.Equal(0, await CountCardTeams(card.Id));
    }

    [Fact]
    public async Task Delete_DoesNotMarkTheMselModified()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel.Id);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Client(actor).DeleteAsync(Card(card.Id), Ct);

        Assert.Null((await StoredMsel(msel.Id)).DateModified);
    }

    /// <remarks>
    /// The MSEL's own cascade, from the other direction: <c>CardEntityConfiguration</c> declares
    /// <c>OnDelete(DeleteBehavior.Cascade)</c> on the MSEL relationship.
    /// </remarks>
    [Fact]
    public async Task DeletingTheMsel_TakesItsCardsWithIt()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel.Id);
        var template = await SeedCard(null, isTemplate: true);

        await using (var context = NewContext())
        {
            context.Msels.Remove(await context.Msels.SingleAsync(x => x.Id == msel.Id, Ct));
            await context.SaveChangesAsync(Ct);
        }

        Assert.Null(await Stored(card.Id));
        Assert.NotNull(await Stored(template.Id));
    }

    // ---------------------------------------------------------------------------------------------
    // POST cards/json
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task UploadJson_CreatesTheCards()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGalleryCards).SeedAsync();

        var created = await Read<List<ViewModels.Card>>(
            await Upload(Client(actor), """
                [{"name":"one","description":"first","move":1,"inject":2},
                 {"name":"two","description":"second","move":3,"inject":4}]
                """));

        var moves = created.Select(x => x.Move).Order().ToList();
        Assert.Equal(["one", "two"], created.Select(x => x.Name).Order());
        Assert.Equal([1, 3], moves);
        Assert.Equal(2, await CountTemplates());
    }

    /// <remarks>
    /// <c>UploadJsonAsync</c> builds its own <c>JsonSerializerOptions</c> (<c>CardService.cs:200-205</c>) with
    /// nothing in it but <c>ReferenceHandler.Preserve</c> and case-insensitive names, so the file is read by
    /// System.Text.Json's defaults for everything else - which is not the format this API writes.
    /// <c>Startup.cs:164-167</c> adds <c>JsonIntegerConverter</c> to the MVC options,
    /// so every <c>int</c> crosses the wire as a JSON <em>string</em> (see
    /// <see cref="Templates_SerializesTheIntegersAsStrings"/>), and a file built from what
    /// <c>GET cards/templates</c> answered is refused with a 500 naming <c>System.Int32</c>. The download route
    /// escapes this only because it does not use the MVC options either
    /// (<see cref="DownloadJson_WritesPascalCaseNamesAndRawIntegers"/>), so the two halves of the round trip
    /// agree with each other and with nothing else. Passing <c>JsonOptions</c> here reddens this test.
    /// </remarks>
    [Fact]
    public async Task UploadJson_RefusesTheStringEncodedIntegersTheApiItselfWrites_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGalleryCards).SeedAsync();

        var response = await Upload(Client(actor), """[{"name":"one","move":"1","inject":"2"}]""");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.StartsWith("The JSON value could not be converted to System.Int32.", await Title(response));
        Assert.Equal(0, await CountTemplates());
    }

    /// <remarks>
    /// Three fields are forced whatever the file says, which is what makes this route an import of
    /// <em>templates</em> rather than of cards: a fresh <c>Id</c>, <c>IsTemplate</c> true and <c>MselId</c>
    /// null. So uploading a file downloaded from a live MSEL (which <c>DownloadJsonAsync</c> will happily
    /// produce - see <see cref="DownloadJson_IncludesAMselsCardsDespiteBeingNamedTemplates"/>) turns its cards
    /// into templates rather than restoring them.
    /// </remarks>
    [Fact]
    public async Task UploadJson_ForcesAFreshIdATemplateFlagAndNoMsel()
    {
        var msel = await SeedMsel();
        var chosen = Guid.NewGuid();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGalleryCards).SeedAsync();

        var created = Assert.Single(await Read<List<ViewModels.Card>>(await Upload(Client(actor), $$"""
            [{"id":"{{chosen}}","mselId":"{{msel.Id}}","name":"one","isTemplate":false}]
            """)));

        Assert.NotEqual(chosen, created.Id);
        Assert.Null(created.MselId);
        Assert.True(created.IsTemplate);
        Assert.Null(await Stored(chosen));
    }

    /// <remarks>
    /// <c>GalleryId</c> is <em>not</em> cleared, so an imported template already points at a card in whatever
    /// Gallery the file came from - and <c>IntegrationGalleryExtensions.CreateCardsAsync</c> writes that
    /// column on a push, reading it back to build every team-card. A template carrying somebody else's id is
    /// therefore how the <c>CardId = (Guid)card.GalleryId</c> finding recorded in <c>28f12e2</c> is reached
    /// without any Gallery failure at all. Clearing it here reddens this test.
    /// </remarks>
    [Fact]
    public async Task UploadJson_KeepsTheGalleryIdFromTheFile()
    {
        var galleryId = Guid.NewGuid();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGalleryCards).SeedAsync();

        var created = Assert.Single(await Read<List<ViewModels.Card>>(await Upload(Client(actor), $$"""
            [{"name":"one","galleryId":"{{galleryId}}"}]
            """)));

        Assert.Equal(galleryId, created.GalleryId);
        Assert.Equal(galleryId, (await Stored(created.Id)).GalleryId);
    }

    [Fact]
    public async Task UploadJson_StampsTheAuditFieldsOnTheServer()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGalleryCards).SeedAsync();
        var before = DateTime.UtcNow;

        var created = Assert.Single(await Read<List<ViewModels.Card>>(await Upload(Client(actor), """
            [{"name":"one","createdBy":"11111111-1111-1111-1111-111111111111",
              "dateCreated":"1999-01-01T00:00:00Z","modifiedBy":"22222222-2222-2222-2222-222222222222"}]
            """)));

        var stored = await Stored(created.Id);
        Assert.Equal(actor.Id, stored.CreatedBy);
        Assert.InRange(stored.DateCreated, before, DateTime.UtcNow);
        Assert.Null(stored.ModifiedBy);
        Assert.Null(stored.DateModified);
    }

    /// <remarks>
    /// The view model returned to the caller is mapped from the entity <em>before</em>
    /// <c>SaveChangesAsync</c>, so its <c>DateCreated</c> is the one <c>UploadJsonAsync</c> set at :215 and
    /// not the one <c>SaveEntries</c> stamped a moment later. The two differ by microseconds, which is
    /// harmless here and would not be if the interceptor ever changed a field the caller acts on. Moving the
    /// map below the save reddens this test.
    /// </remarks>
    [Fact]
    public async Task UploadJson_AnswersADateCreatedThatIsNotTheStoredOne()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGalleryCards).SeedAsync();

        var created = Assert.Single(await Read<List<ViewModels.Card>>(
            await Upload(Client(actor), """[{"name":"one"}]""")));

        Assert.NotEqual((await Stored(created.Id)).DateCreated, created.DateCreated);
    }

    [Fact]
    public async Task UploadJson_DoesNotSanitizeTheDescription()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGalleryCards).SeedAsync();

        var created = Assert.Single(await Read<List<ViewModels.Card>>(await Upload(Client(actor), """
            [{"name":"one","description":"<script>alert(1)</script>ok"}]
            """)));

        Assert.Equal("<script>alert(1)</script>ok", (await Stored(created.Id)).Description);
    }

    /// <remarks>
    /// Every imported card has a null <c>MselId</c> by the time the interceptor publishes, so
    /// <c>CardHandler.GetGroups</c> names the empty-string group for all of them - nobody is told that the
    /// template library changed. The admin group does get the events.
    /// </remarks>
    [Fact]
    public async Task UploadJson_BroadcastsToAGroupNamedByTheEmptyString()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGalleryCards).SeedAsync();
        Hub.Clear();

        await Upload(Client(actor), """[{"name":"one"},{"name":"two"}]""");

        Assert.Equal(
            [string.Empty, MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.CardCreated));
        Assert.Equal(4, Hub.Of(MainHubMethods.CardCreated).Count);
    }

    [Fact]
    public async Task UploadJson_WithAnEmptyArray_CreatesNothing()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGalleryCards).SeedAsync();

        Assert.Empty(await Read<List<ViewModels.Card>>(await Upload(Client(actor), "[]")));
        Assert.Equal(0, await CountTemplates());
    }

    [Fact]
    public async Task UploadJson_WithoutManageGalleryCards_Is403()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner)
            .WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Upload(Client(actor), """[{"name":"one"}]""");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// <c>FileForm.ToUpload</c> carries <c>[Required]</c>, so a multipart request with no file part is a 400
    /// from <c>ValidateModelStateFilter</c> and never reaches the service - which would have dereferenced
    /// null. The part has to be well-formed and named something else: a <c>MultipartFormDataContent</c> with
    /// no parts at all is a different 400, raised by the form reader.
    /// </remarks>
    [Fact]
    public async Task UploadJson_WithNoFilePart_Is400()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGalleryCards).SeedAsync();
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("[]", Encoding.UTF8, "application/json"), "SomethingElse", "cards.json");

        var response = await Client(actor).PostAsync($"{Cards}/json", content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // POST cards/json/download
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task DownloadJson_ReturnsOnlyTheRequestedCards()
    {
        var wanted = await SeedCard(null, isTemplate: true);
        await SeedCard(null, isTemplate: true);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGalleryCards).SeedAsync();

        var names = await Downloaded(Client(actor), wanted.Id);

        Assert.Equal(wanted.Name, Assert.Single(names));
    }

    [Fact]
    public async Task DownloadJson_AnswersAnAttachmentNamedCardTemplates()
    {
        var card = await SeedCard(null, isTemplate: true);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGalleryCards).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync($"{Cards}/json/download", new[] { card.Id }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("card-templates.json", response.Content.Headers.ContentDisposition.FileName);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition.DispositionType);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType.MediaType);
    }

    /// <remarks>
    /// The query filters on the requested ids and nothing else, so a route named <c>json/download</c>
    /// answering a file named <c>card-templates.json</c> will export a live MSEL's cards to anybody holding
    /// <c>ManageGalleryCards</c> - a permission that grants nothing on any MSEL. Adding
    /// <c>Where(c => c.IsTemplate)</c> reddens this test.
    /// </remarks>
    [Fact]
    public async Task DownloadJson_IncludesAMselsCardsDespiteBeingNamedTemplates()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGalleryCards).SeedAsync();

        Assert.Equal(card.Name, Assert.Single(await Downloaded(Client(actor), card.Id)));
    }

    /// <remarks>
    /// <c>DownloadJsonAsync</c> serializes with its own <c>JsonSerializerOptions</c> rather than the MVC ones,
    /// so the file disagrees with the API in two ways: property names are PascalCase where every response is
    /// camelCase, and the integers are numbers where <c>JsonIntegerConverter</c> makes them strings
    /// everywhere else. <c>ReferenceHandler.Preserve</c> also wraps the array in <c>$id</c>/<c>$values</c>.
    /// The upload route reads exactly this dialect back
    /// (<see cref="DownloadJson_RoundTripsThroughTheUploadRoute"/>), so the pair is consistent with itself and
    /// with nothing else - a file hand-written to match the API's own wire format will not import.
    /// </remarks>
    [Fact]
    public async Task DownloadJson_WritesPascalCaseNamesAndRawIntegers()
    {
        var card = await SeedCard(null, move: 3, isTemplate: true);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGalleryCards).SeedAsync();

        var json = await DownloadedJson(Client(actor), card.Id);

        Assert.Contains("\"Name\":", json);
        Assert.Contains("\"Move\": 3", json);
        Assert.Contains("\"$values\":", json);
        Assert.DoesNotContain("\"name\":", json);
        Assert.DoesNotContain("\"Move\": \"3\"", json);
    }

    /// <remarks>
    /// The positive half of the finding above: <c>UploadJsonAsync</c> reads with
    /// <c>ReferenceHandler.Preserve</c> and <c>PropertyNameCaseInsensitive</c>, so the bytes the download
    /// route produces feed straight back into the upload route. The pair round-trips - with the three fields
    /// the upload forces, so a MSEL's card comes back as a template.
    /// </remarks>
    [Fact]
    public async Task DownloadJson_RoundTripsThroughTheUploadRoute()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel.Id, move: 7, inject: 8);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGalleryCards).SeedAsync();

        var reimported = Assert.Single(await Read<List<ViewModels.Card>>(
            await Upload(Client(actor), await DownloadedJson(Client(actor), card.Id))));

        Assert.Equal(card.Name, reimported.Name);
        Assert.Equal(7, reimported.Move);
        Assert.Equal(8, reimported.Inject);
        Assert.True(reimported.IsTemplate);
        Assert.Null(reimported.MselId);
        Assert.NotEqual(card.Id, reimported.Id);
    }

    [Fact]
    public async Task DownloadJson_ForAnEmptyListOfIds_IsAnEmptyPreservedArray()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGalleryCards).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync($"{Cards}/json/download", Array.Empty<Guid>(), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"$values\": []", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task DownloadJson_WithoutManageGalleryCards_Is403()
    {
        var msel = await SeedMsel();
        var card = await SeedCard(msel.Id);
        var actor = await Actor().OnMsel(msel, MselRole.Owner)
            .WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync($"{Cards}/json/download", new[] { card.Id }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Authentication
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "cards/templates")]
    [InlineData("GET", "msels/00000000-0000-0000-0000-000000000001/cards")]
    [InlineData("GET", "cards/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "cards")]
    [InlineData("PUT", "cards/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "cards/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "cards/json/download")]
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
    /// The upload route is not in the sweep above because it takes a multipart body, and its own test has to
    /// send one: a JSON body to it is a 415 raised during endpoint selection, before authentication. Note
    /// <c>FileForm</c> is a complex type, so MVC infers no <c>Consumes</c> for it and a multipart request does
    /// reach the authorization middleware - unlike a route taking a bare <c>IFormFile</c>, which
    /// <c>DataOptionEndpointTests</c> records as answering 415 to an anonymous caller.
    /// </remarks>
    [Fact]
    public async Task UploadJson_Anonymously_Is401()
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("[]", Encoding.UTF8, "application/json"), "ToUpload", "cards.json");

        var response = await AnonymousClient.PostAsync($"{Cards}/json", content, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------

    private const string Cards = "/api/cards";

    private const string Templates = "/api/cards/templates";

    private static string Card(Guid id) => $"{Cards}/{id}";

    private static string CardsOf(Guid mselId) => $"/api/msels/{mselId}/cards";

    /// <summary>
    /// The wire shape of a card. A record rather than an anonymous type so a test can vary one property of a
    /// stored row with a <c>with</c> expression.
    /// </summary>
    /// <remarks>
    /// <c>MselId</c> is <c>Guid?</c> on <c>ViewModels.Card</c>, so a null field and an absent one are the same
    /// request and the field may be typed - the distinction that
    /// <c>MoveEndpointTests</c> had to avoid does not arise here. <c>DateCreated</c> and <c>CreatedBy</c> are
    /// non-nullable on <c>ViewModels.Base</c>, so they are sent as values: a null is a 400 that never reaches
    /// the controller.
    /// </remarks>
    private sealed record CardBody
    {
        public Guid Id { get; init; }
        public Guid? MselId { get; init; }
        public string Name { get; init; }
        public string Description { get; init; }
        public int Move { get; init; }
        public int Inject { get; init; }
        public Guid? GalleryId { get; init; }
        public bool IsTemplate { get; init; }

        public Guid CreatedBy { get; init; }
        public DateTime DateCreated { get; init; }
        public Guid? ModifiedBy { get; init; }
        public DateTime? DateModified { get; init; }
    }

    private static CardBody Body(Guid? mselId, int move = 0, int inject = 0) => new()
    {
        MselId = mselId,
        Name = "posted by the test",
        Description = "description posted by the test",
        Move = move,
        Inject = inject
    };

    private static CardBody BodyFor(CardEntity card) => new()
    {
        Id = card.Id,
        MselId = card.MselId,
        Name = card.Name,
        Description = card.Description,
        Move = card.Move,
        Inject = card.Inject,
        GalleryId = card.GalleryId,
        IsTemplate = card.IsTemplate
    };

    private async Task<MselEntity> SeedMsel(Guid? createdBy = null)
    {
        var msel = BlueprintAppFactory.Msel(createdBy: createdBy);
        await Seed(msel);

        return msel;
    }

    private async Task<CardEntity> SeedCard(
        Guid? mselId, int move = 0, int inject = 0, bool isTemplate = false)
    {
        var card = BlueprintAppFactory.Card(mselId, move, inject, isTemplate);
        await Seed(card);

        return card;
    }

    /// <summary>
    /// Drops an actor's <c>UserMselRole</c> rows, leaving the unit membership <c>TestActorBuilder</c> seeded
    /// alongside them - the state <c>MselUserRequirement</c> accepts and every other helper refuses.
    /// </summary>
    private async Task RemoveTheRoleRows(Guid userId)
    {
        await using var context = NewContext();
        var roles = await context.UserMselRoles.Where(x => x.UserId == userId).ToListAsync(Ct);
        context.UserMselRoles.RemoveRange(roles);
        await context.SaveChangesAsync(Ct);
    }

    private Task<HttpResponseMessage> Post(HttpClient client, CardBody body) =>
        client.PostAsJsonAsync(Cards, body, Ct);

    private Task<HttpResponseMessage> Put(HttpClient client, Guid id, CardBody body) =>
        client.PutAsJsonAsync(Card(id), body, Ct);

    /// <summary>
    /// Posts <paramref name="json"/> as the multipart file part the upload route expects.
    /// </summary>
    /// <remarks>
    /// The content is awaited here rather than returned, because <c>TestServer</c> reads the body inside
    /// <c>SendAsync</c> and a returned task would see it disposed - the Phase 1 gotcha.
    /// </remarks>
    private async Task<HttpResponseMessage> Upload(HttpClient client, string json)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(json, Encoding.UTF8, "application/json"), "ToUpload", "cards.json");

        return await client.PostAsync($"{Cards}/json", content, Ct);
    }

    private async Task<string> DownloadedJson(HttpClient client, params Guid[] ids)
    {
        var response = await client.PostAsJsonAsync($"{Cards}/json/download", ids, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadAsStringAsync(Ct);
    }

    /// <summary>
    /// The names in a downloaded file, read through <c>JsonDocument</c> because the file is PascalCase and
    /// wrapped in <c>ReferenceHandler.Preserve</c>'s <c>$values</c> envelope.
    /// </summary>
    private async Task<List<string>> Downloaded(HttpClient client, params Guid[] ids)
    {
        using var document = JsonDocument.Parse(await DownloadedJson(client, ids));

        return [.. document.RootElement.GetProperty("$values").EnumerateArray()
            .Select(x => x.GetProperty("Name").GetString())];
    }

    private async Task<List<ViewModels.Card>> GetCards(HttpClient client, Guid mselId)
    {
        var response = await client.GetAsync(CardsOf(mselId), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<List<ViewModels.Card>>(response);
    }

    private async Task<ViewModels.Card> GetCard(HttpClient client, Guid id)
    {
        var response = await client.GetAsync(Card(id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<ViewModels.Card>(response);
    }

    private async Task<CardEntity> Stored(Guid id)
    {
        await using var context = NewContext();

        return await context.Cards.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, Ct);
    }

    private async Task<MselEntity> StoredMsel(Guid id)
    {
        await using var context = NewContext();

        return await context.Msels.AsNoTracking().SingleAsync(x => x.Id == id, Ct);
    }

    private async Task<int> CountTemplates()
    {
        await using var context = NewContext();

        return await context.Cards.CountAsync(x => x.IsTemplate, Ct);
    }

    private async Task<int> CountCardTeams(Guid cardId)
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
