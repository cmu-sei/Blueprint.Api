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
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Infrastructure;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>MoveService</c> / <c>MoveController</c> - the five routes behind the Moves tab, and the coarse
/// division of a MSEL's timeline that CITE, Gallery and Steamfitter are all told about.
/// </summary>
/// <remarks>
/// <para>
/// A move is 148 lines of service and yet it is exercise content: <c>MoveNumber</c> is what names every
/// Steamfitter task and every CITE and Gallery move-change URL a push produces, and <c>DeltaSeconds</c> is
/// the only thing associating a scenario event with a move - <c>ScenarioEventEntity</c> has no move column
/// at all. So the two most interesting things about this service are what a write may do to another MSEL's
/// timeline and what a delete does to the events that were in the move.
/// </para>
/// <para>
/// <strong>Deleting a move silently regroups its scenario events.</strong> Nothing references a move, so
/// nothing stops the delete and nothing records that the events have moved:
/// <c>ScenarioEventService.GetMovesAndInjects</c> walks the remaining moves by <c>DeltaSeconds</c>, so
/// everything that was in the deleted move is reported as being in the one before it - and deleting the
/// last remaining move turns that method into an <c>IndexOutOfRangeException</c>, which is how the defect
/// characterized in <c>ScenarioEventServiceMovesAndInjectsTests</c> is reached from the UI. See
/// <see cref="Delete_OfAMiddleMove_RegroupsItsScenarioEventsIntoThePrecedingMove"/> and
/// <see cref="Delete_OfTheOnlyMove_MakesTheMoveLookupThrowForEveryScenarioEvent"/>.
/// </para>
/// <para>
/// <strong><c>UpdateAsync</c> takes its permission decision from the request body's <c>MselId</c></strong>
/// and the mapper then writes that id onto the row - the fourth instance of this shape in the branch, after
/// <c>OrganizationService</c>, <c>ScenarioEventService</c> and <c>DataOptionService</c>. So a caller who
/// owns any MSEL at all may edit and steal every move of every other one, and the MSEL that loses the move
/// is not even marked modified. <c>DeleteAsync</c> one method below reads the <em>stored</em> row and is
/// the model to copy. See <see cref="Update_ChoosesItsPermissionBranchFromTheRequestBody"/> and
/// <see cref="Delete_TakesItsPermissionDecisionFromTheStoredRow"/>.
/// </para>
/// <para>
/// <strong>The body's <c>Id</c> is mapped onto the tracked row too</strong>, and a key cannot be modified,
/// so a PUT whose body carries a different id - or omits it - is a 500 rather than the 400 the controller's
/// own remarks ("The ID from the route MUST MATCH the ID contained in the move parameter") promise. Nothing
/// validates the match. <c>MoveProfile</c> is the only difference between this service and
/// <c>ScenarioEventService</c> here, whose profile ignores <c>Id</c>.
/// </para>
/// <para>
/// <strong>Reading is looser than the rest of the API.</strong> Both reads use
/// <c>MselUserRequirement</c> - the one helper of the eight that accepts a unit member holding no MSEL role
/// at all - so anybody in a unit assigned to the MSEL may read its moves, while writing needs
/// <c>MselOwnerRequirement</c> or <c>MoveEditorRequirement</c>. And <c>MselUserRequirement</c> dereferences
/// a <c>FirstOrDefaultAsync</c>, so a MSEL that is not there is a 500 for an ordinary caller and an empty
/// list for one holding <c>ViewMsels</c>: the answer to "is this MSEL there" depends on who asks.
/// </para>
/// <para>
/// <strong><c>GetAsync</c>'s null check is dead</strong> - <c>SingleAsync</c> has already thrown - and the
/// exception it would have thrown is <c>EntityNotFoundException&lt;DataValueEntity&gt;</c> carrying the
/// message "DataValue not found", copied from <c>DataValueService</c>. So an unknown move id is a 500 where
/// an unknown move id on the PUT and DELETE routes is a 404.
/// </para>
/// <para>
/// Per this branch's rule, every test characterizes rather than fixes, and says what fixing it will do to
/// the test.
/// </para>
/// </remarks>
public class MoveEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET msels/{mselId}/moves
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByMsel_ReturnsEveryMoveOnTheMsel()
    {
        var msel = await SeedMsel();
        await SeedMove(msel, moveNumber: 1);
        await SeedMove(msel, moveNumber: 2);
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var moves = await GetMoves(Client(actor), msel.Id);

        Assert.Equal([1, 2], moves.Select(x => x.MoveNumber).Order());
    }

    [Fact]
    public async Task GetByMsel_DoesNotReturnAnotherMselsMoves()
    {
        var msel = await SeedMsel();
        var mine = await SeedMove(msel, moveNumber: 1);
        await SeedMove(await SeedMsel(), moveNumber: 1);
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var moves = await GetMoves(Client(actor), msel.Id);

        Assert.Equal(mine.Id, Assert.Single(moves).Id);
    }

    /// <remarks>
    /// There is no <c>OrderBy</c> on the query, so the moves arrive in whatever order the database returns
    /// them and the tab's ordering is the UI's business. Asserted as a set for that reason; an
    /// <c>OrderBy(m => m.MoveNumber)</c> - or <c>DeltaSeconds</c>, which is what everything else in the
    /// codebase orders moves by - would leave this test passing, which is the point of writing it this way.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_AnswersTheMovesAsAnUnorderedSet()
    {
        var msel = await SeedMsel();
        await SeedMove(msel, moveNumber: 3, deltaSeconds: 0);
        await SeedMove(msel, moveNumber: 1, deltaSeconds: 100);
        await SeedMove(msel, moveNumber: 2, deltaSeconds: 200);
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var moves = await GetMoves(Client(actor), msel.Id);

        Assert.Equal([1, 2, 3], moves.Select(x => x.MoveNumber).Order());
    }

    [Fact]
    public async Task GetByMsel_ForAMselWithNoMoves_IsAnEmptyList()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        Assert.Empty(await GetMoves(Client(actor), msel.Id));
    }

    [Fact]
    public async Task GetByMsel_WithViewMselsAndNoRoleOnTheMsel_Is200()
    {
        var msel = await SeedMsel();
        await SeedMove(msel, moveNumber: 1);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Single(await GetMoves(Client(actor), msel.Id));
    }

    [Fact]
    public async Task GetByMsel_ForACallerWithNoRoleOnTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync(MovesOf(msel.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// <c>MselUserRequirement</c> is the one helper of the eight that is satisfied by unit membership
    /// alone, so every role - and no role - reads the moves. Contrast the writes below, where only
    /// <c>Owner</c> and <c>MoveEditor</c> are accepted. Requiring a role here turns the last case red.
    /// </remarks>
    [Theory]
    [InlineData(MselRole.Owner)]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.MoveEditor)]
    [InlineData(MselRole.Viewer)]
    [InlineData(MselRole.Evaluator)]
    public async Task GetByMsel_ForEveryMselRole_Is200(MselRole role)
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Client(actor).GetAsync(MovesOf(msel.Id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <remarks>
    /// A unit assigned to the MSEL and no <c>UserMselRole</c> row at all - which is the state an
    /// administrator leaves somebody in by adding them to a unit and stopping there. Every other read in
    /// the API refuses it.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_ForAUnitMemberHoldingNoRole_Is200()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();
        await RemoveTheRoleRows(actor.Id);

        var response = await Client(actor).GetAsync(MovesOf(msel.Id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetByMsel_ForTheMselsCreator_Is200()
    {
        var actor = await Actor().SeedAsync();
        var msel = await SeedMsel(createdBy: actor.Id);

        var response = await Client(actor).GetAsync(MovesOf(msel.Id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <remarks>
    /// <c>MselUserRequirement.IsMet</c> dereferences a <c>FirstOrDefaultAsync</c> to read the MSEL's
    /// <c>CreatedBy</c>, so a MSEL that is not there is a <c>NullReferenceException</c> - a 500 - for a
    /// caller whose permission has to be checked, and an ordinary empty list for one holding
    /// <c>ViewMsels</c>, who never reaches the helper. Guarding the lookup turns the first case into the
    /// 403 or 404 it should have been and leaves the second alone.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_Is500ForAnOrdinaryCallerAnd200ForAPrivilegedOne()
    {
        var mselId = Guid.NewGuid();

        var ordinary = await Client(await Actor().SeedAsync()).GetAsync(MovesOf(mselId), Ct);
        var privileged = await Client(await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync())
            .GetAsync(MovesOf(mselId), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, ordinary.StatusCode);
        Assert.Equal(HttpStatusCode.OK, privileged.StatusCode);
        Assert.Empty(await Read<List<ViewModels.Move>>(privileged));
    }

    // ---------------------------------------------------------------------------------------------
    // GET moves/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ReturnsTheMove()
    {
        var msel = await SeedMsel();
        var move = await SeedMove(msel, moveNumber: 4, deltaSeconds: 3600);
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var answered = await GetMove(Client(actor), move.Id);

        Assert.Equal(move.Id, answered.Id);
        Assert.Equal(4, answered.MoveNumber);
        Assert.Equal(3600, answered.DeltaSeconds);
        Assert.Equal(move.Description, answered.Description);
        Assert.Equal(move.SituationDescription, answered.SituationDescription);
        Assert.Equal(move.SituationTime, answered.SituationTime);
        Assert.Equal(msel.Id, answered.MselId);
    }

    /// <remarks>
    /// <c>Startup</c> adds <c>JsonIntegerConverter</c>, so both of a move's integers cross the wire as JSON
    /// strings - and <c>moveNumber</c> is what blueprint tells CITE, Gallery and Steamfitter to advance to.
    /// Pinned on the raw body rather than through the app's own options, which would follow the format
    /// wherever it went.
    /// </remarks>
    [Fact]
    public async Task Get_SerializesTheIntegersAsStrings()
    {
        var msel = await SeedMsel();
        var move = await SeedMove(msel, moveNumber: 4, deltaSeconds: 3600);
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var body = await (await Client(actor).GetAsync(Move(move.Id), Ct)).Content.ReadAsStringAsync(Ct);

        Assert.Contains("\"moveNumber\":\"4\"", body);
        Assert.Contains("\"deltaSeconds\":\"3600\"", body);
    }

    [Fact]
    public async Task Get_WithViewMselsAndNoRoleOnTheMsel_Is200()
    {
        var msel = await SeedMsel();
        var move = await SeedMove(msel, moveNumber: 1);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync(Move(move.Id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_ForACallerWithNoRoleOnTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var move = await SeedMove(msel, moveNumber: 1);
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync(Move(move.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_ForAMoveOnAnotherMselThatTheCallerCanRead_Is403()
    {
        var mine = await SeedMsel();
        var theirs = await SeedMsel();
        var move = await SeedMove(theirs, moveNumber: 1);
        var actor = await Actor().OnMsel(mine, MselRole.Owner).SeedAsync();

        var response = await Client(actor).GetAsync(Move(move.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// <c>SingleAsync</c> where every sibling service uses <c>SingleOrDefaultAsync</c>, so an id that is
    /// not there is an <c>InvalidOperationException</c> - a 500 - and the null check on the next line is
    /// unreachable. The exception that check would have thrown is
    /// <c>EntityNotFoundException&lt;DataValueEntity&gt;</c> with the message "DataValue not found", copied
    /// from <c>DataValueService</c> along with the shape. Reaching for <c>SingleOrDefaultAsync</c> makes
    /// this a 404 and turns the test red - and fixing the exception type alone changes nothing, which is
    /// what makes the whole block dead rather than merely wrong.
    /// </remarks>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is500RatherThanThe404TheDeadCheckWouldHaveGiven()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync(Move(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("DataValue not found", await Title(response) ?? string.Empty);
    }

    // ---------------------------------------------------------------------------------------------
    // POST moves
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_ForAnMselOwner_Is201AndStoresTheMove()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id, moveNumber: 7, deltaSeconds: 60));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await Read<ViewModels.Move>(response);
        Assert.Equal(7, created.MoveNumber);

        var stored = await Stored(created.Id);
        Assert.Equal(7, stored.MoveNumber);
        Assert.Equal(60, stored.DeltaSeconds);
        Assert.Equal(msel.Id, stored.MselId);
    }

    /// <remarks>
    /// Absolute, and lowercased by <c>RouteOptions.LowercaseUrls</c> (<c>Startup.cs:203-205</c>) even
    /// though the route template spells the segment as it is written here.
    /// </remarks>
    [Fact]
    public async Task Create_AnswersALocationHeaderPointingAtTheNewMove()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id, moveNumber: 1));
        var created = await Read<ViewModels.Move>(response);

        Assert.EndsWith($"/api/moves/{created.Id}", response.Headers.Location.ToString());
    }

    /// <remarks>
    /// Only two of the six roles may add a move, and <c>MselRole.Editor</c> - the role whose name says it
    /// edits the MSEL - is not one of them. The four helpers do not substitute for one another; pinned
    /// across the board in <c>MselRoleRequirementTests</c>, asserted here because this is where it is felt.
    /// </remarks>
    [Theory]
    [InlineData(MselRole.Owner, HttpStatusCode.Created)]
    [InlineData(MselRole.MoveEditor, HttpStatusCode.Created)]
    [InlineData(MselRole.Editor, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Approver, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Viewer, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Evaluator, HttpStatusCode.Forbidden)]
    public async Task Create_TheRolesThatMayAddAMove(MselRole role, HttpStatusCode expected)
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id, moveNumber: 1));

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithEditMselsAndNoRoleOnTheMsel_Is201()
    {
        var msel = await SeedMsel();
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id, moveNumber: 1));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Create_ForTheMselsCreator_Is201()
    {
        var actor = await Actor().SeedAsync();
        var msel = await SeedMsel(createdBy: actor.Id);

        var response = await Post(Client(actor), Body(msel.Id, moveNumber: 1));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Create_ForACallerWithNoRoleOnTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var actor = await Actor().SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id, moveNumber: 1));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountOn(msel.Id));
    }

    /// <remarks>
    /// <c>ViewMsels</c> is not <c>EditMsels</c>, and a caller holding only the read permission falls through
    /// to the two MSEL helpers like anybody else.
    /// </remarks>
    [Fact]
    public async Task Create_WithViewMselsOnly_Is403()
    {
        var msel = await SeedMsel();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id, moveNumber: 1));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_StampsTheAuditFieldsOnTheServer()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var before = DateTime.UtcNow;

        var response = await Post(
            Client(actor),
            Body(msel.Id, moveNumber: 1) with
            {
                CreatedBy = Guid.NewGuid(),
                DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ModifiedBy = Guid.NewGuid(),
                DateModified = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });

        var stored = await Stored((await Read<ViewModels.Move>(response)).Id);

        Assert.Equal(actor.Id, stored.CreatedBy);
        Assert.InRange(stored.DateCreated, before, DateTime.UtcNow);
        Assert.Null(stored.ModifiedBy);
        Assert.Null(stored.DateModified);
    }

    /// <remarks>
    /// The client may choose the primary key: <c>CreateAsync</c> keeps a non-empty <c>Id</c> from the body
    /// and only generates one when the body leaves it at all zeros. Same as the rest of the API, and the
    /// same consequence - a second create naming an id that is already there is a 500 from the database
    /// rather than a 409.
    /// </remarks>
    [Fact]
    public async Task Create_KeepsTheIdFromTheBodyWhenItGivesOne()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var id = Guid.NewGuid();

        var response = await Post(Client(actor), Body(msel.Id, moveNumber: 1) with { Id = id });

        Assert.Equal(id, (await Read<ViewModels.Move>(response)).Id);
        Assert.NotNull(await Stored(id));
    }

    /// <remarks>
    /// <c>(MselId, MoveNumber)</c> is uniquely indexed - the one thing in this table that is - so a second
    /// move numbered the same is a <c>DbUpdateException</c> and a 500 with the constraint's name in it,
    /// where the UI wants a 409 and a sentence. The index is what makes a MSEL's move numbers trustworthy,
    /// so the answer is worth pinning rather than the absence of one.
    /// </remarks>
    [Fact]
    public async Task Create_WithAMoveNumberTheMselAlreadyUses_Is500()
    {
        var msel = await SeedMsel();
        await SeedMove(msel, moveNumber: 2);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id, moveNumber: 2));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(1, await CountOn(msel.Id));
    }

    [Fact]
    public async Task Create_WithAMoveNumberAnotherMselUses_Is201()
    {
        var msel = await SeedMsel();
        await SeedMove(await SeedMsel(), moveNumber: 2);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id, moveNumber: 2));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <remarks>
    /// <c>MselOwnerRequirement.IsMet</c> dereferences its <c>FirstOrDefaultAsync</c>, so a body naming a
    /// MSEL that is not there is a 500 before anything is attempted; a caller holding <c>EditMsels</c>
    /// skips the helper and reaches the insert, which is a 500 from the foreign key instead. Two paths, two
    /// exceptions, one status code and no sentence naming the MSEL either way.
    /// </remarks>
    [Fact]
    public async Task Create_ForAMselThatIsNotThere_Is500WhoeverAsks()
    {
        var mselId = Guid.NewGuid();

        var ordinary = await Post(Client(await Actor().SeedAsync()), Body(mselId, moveNumber: 1));
        var privileged = await Post(
            Client(await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync()),
            Body(mselId, moveNumber: 1));

        Assert.Equal(HttpStatusCode.InternalServerError, ordinary.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, privileged.StatusCode);
    }

    [Fact]
    public async Task Create_MarksTheMselModified()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var before = DateTime.UtcNow;

        await Post(Client(actor), Body(msel.Id, moveNumber: 1));

        var stored = await StoredMsel(msel.Id);

        Assert.Equal(actor.Id, stored.ModifiedBy);
        Assert.NotNull(stored.DateModified);
        Assert.InRange(stored.DateModified.Value, before, DateTime.UtcNow);
    }

    [Fact]
    public async Task Create_BroadcastsMoveCreatedToTheMselGroupAndTheAdminGroup()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id, moveNumber: 1));
        var created = await Read<ViewModels.Move>(response);

        Assert.Equal(
            [msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.MoveCreated));
        Assert.Equal(created.Id, ((ViewModels.Move)Hub.Of(MainHubMethods.MoveCreated)[0].Payload).Id);
    }

    /// <remarks>
    /// <c>SituationDescription</c> carries <c>[SanitizeHtml]</c> and <c>Description</c> does not, so
    /// <c>SanitizerInterceptor</c> strips the script from one and stores the other as sent. Both are free
    /// text the UI renders, and which of them is treated as HTML is decided by one attribute on the entity.
    /// The first end-to-end assertion on the interceptor in the suite; it is covered properly with the
    /// other interceptors in item 8.
    /// </remarks>
    [Fact]
    public async Task Create_SanitizesTheSituationDescriptionAndNotTheDescription()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(
            Client(actor),
            Body(msel.Id, moveNumber: 1) with
            {
                Description = "<script>alert(1)</script>plain",
                SituationDescription = "<script>alert(1)</script>situation"
            });

        var stored = await Stored((await Read<ViewModels.Move>(response)).Id);

        Assert.Equal("<script>alert(1)</script>plain", stored.Description);
        Assert.Equal("situation", stored.SituationDescription);
    }

    [Fact]
    public async Task Create_WithNoBody_Is400()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).PostAsync(Moves, EmptyJson(), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT moves/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_ForAnMselOwner_Is200AndStoresTheChanges()
    {
        var msel = await SeedMsel();
        var move = await SeedMove(msel, moveNumber: 1, deltaSeconds: 0);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Put(
            Client(actor),
            move.Id,
            BodyFor(move) with { MoveNumber = 5, DeltaSeconds = 900, Description = "after" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await Stored(move.Id);
        Assert.Equal(5, stored.MoveNumber);
        Assert.Equal(900, stored.DeltaSeconds);
        Assert.Equal("after", stored.Description);
    }

    [Theory]
    [InlineData(MselRole.Owner, HttpStatusCode.OK)]
    [InlineData(MselRole.MoveEditor, HttpStatusCode.OK)]
    [InlineData(MselRole.Editor, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Approver, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Viewer, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Evaluator, HttpStatusCode.Forbidden)]
    public async Task Update_TheRolesThatMayChangeAMove(MselRole role, HttpStatusCode expected)
    {
        var msel = await SeedMsel();
        var move = await SeedMove(msel, moveNumber: 1);
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Put(Client(actor), move.Id, BodyFor(move) with { Description = "after" });

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Update_WithEditMselsAndNoRoleOnTheMsel_Is200()
    {
        var msel = await SeedMsel();
        var move = await SeedMove(msel, moveNumber: 1);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Put(Client(actor), move.Id, BodyFor(move) with { Description = "after" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Update_StampsTheAuditFieldsAndPreservesCreation()
    {
        var msel = await SeedMsel();
        var move = await SeedMove(msel, moveNumber: 1);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var before = DateTime.UtcNow;

        await Put(
            Client(actor),
            move.Id,
            BodyFor(move) with
            {
                Description = "after",
                CreatedBy = Guid.NewGuid(),
                DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ModifiedBy = Guid.NewGuid(),
                DateModified = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });

        var stored = await Stored(move.Id);

        Assert.Equal(move.CreatedBy, stored.CreatedBy);
        Assert.Equal(actor.Id, stored.ModifiedBy);
        Assert.NotNull(stored.DateModified);
        Assert.InRange(stored.DateModified.Value, before, DateTime.UtcNow);
    }

    /// <remarks>
    /// <para>
    /// <strong>The permission decision is taken from the request body.</strong> A caller who owns one MSEL
    /// puts a body naming <em>their</em> MSEL at another MSEL's move: the owner check passes against the id
    /// in the body, the row is then found by the id in the route, and the mapper writes the body's
    /// <c>MselId</c> onto it. So the move is edited and moved, and one call both steals a row and rewrites
    /// its number.
    /// </para>
    /// <para>
    /// Fourth instance of this shape in the branch, after <c>OrganizationService</c>,
    /// <c>ScenarioEventService</c> and <c>DataOptionService</c>. Checking the stored row's <c>MselId</c>,
    /// as <c>DeleteAsync</c> does one method below, turns this red.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Update_ChoosesItsPermissionBranchFromTheRequestBody()
    {
        var mine = await SeedMsel();
        var theirs = await SeedMsel();
        var move = await SeedMove(theirs, moveNumber: 1);
        var actor = await Actor().OnMsel(mine, MselRole.Owner).SeedAsync();

        var response = await Put(
            Client(actor),
            move.Id,
            BodyFor(move) with { MselId = mine.Id, MoveNumber = 9, Description = "taken" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await Stored(move.Id);
        Assert.Equal(mine.Id, stored.MselId);
        Assert.Equal(9, stored.MoveNumber);
        Assert.Equal(0, await CountOn(theirs.Id));
    }

    /// <remarks>
    /// The other half of the same defect: <c>SetMselModifiedAsync</c> is called with the row's
    /// <em>new</em> MSEL, so the MSEL that lost a move is not marked modified and nothing tells anybody
    /// watching it that its timeline changed. The <c>MoveDeleted</c> broadcast a real removal would send
    /// is not sent either - see <see cref="Update_ThatMovesAMoveToAnotherMsel_TellsTheOldMselNothing"/>.
    /// </remarks>
    [Fact]
    public async Task Update_ThatMovesAMoveToAnotherMsel_TellsTheOldMselNothing()
    {
        var mine = await SeedMsel();
        var theirs = await SeedMsel();
        var move = await SeedMove(theirs, moveNumber: 1);
        var actor = await Actor().OnMsel(mine, MselRole.Owner).SeedAsync();

        await Put(Client(actor), move.Id, BodyFor(move) with { MselId = mine.Id });

        Assert.Null((await StoredMsel(theirs.Id)).DateModified);
        Assert.NotNull((await StoredMsel(mine.Id)).DateModified);
        Assert.Equal([mine.Id.ToString(), MainHub.ADMIN_DATA_GROUP], Hub.Recipients(MainHubMethods.MoveUpdated));
        Assert.Empty(Hub.Of(MainHubMethods.MoveDeleted));
    }

    [Fact]
    public async Task Update_ForACallerWithNoRoleOnTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var move = await SeedMove(msel, moveNumber: 1);
        var actor = await Actor().SeedAsync();

        var response = await Put(Client(actor), move.Id, BodyFor(move) with { Description = "after" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(move.Description, (await Stored(move.Id)).Description);
    }

    /// <remarks>
    /// The permission check runs before the lookup, so which answer an unknown id gets depends on the body
    /// it arrived with: a body naming a MSEL the caller owns reaches the lookup and is a 404, and a body
    /// naming anything else is a 403 that cannot be told from "that move is yours and you may not have
    /// it". Looking the row up first, as <c>DeleteAsync</c> does, turns the second case into a 404 too.
    /// </remarks>
    [Fact]
    public async Task Update_ForAnIdThatIsNotThere_Is404OrA403DependingOnTheBody()
    {
        var mine = await SeedMsel();
        var theirs = await SeedMsel();
        var actor = await Actor().OnMsel(mine, MselRole.Owner).SeedAsync();

        var withMine = await Put(Client(actor), Guid.NewGuid(), Body(mine.Id, moveNumber: 1));
        var withTheirs = await Put(Client(actor), Guid.NewGuid(), Body(theirs.Id, moveNumber: 1));

        Assert.Equal(HttpStatusCode.NotFound, withMine.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, withTheirs.StatusCode);
    }

    /// <remarks>
    /// <c>MoveProfile</c> does not ignore <c>Id</c>, so <c>_mapper.Map(move, moveToUpdate)</c> writes the
    /// body's id onto a tracked row and EF refuses to modify a key - a 500, whether the body carries a
    /// different id or leaves it at all zeros by not mentioning it at all. The controller's own remarks say
    /// "The ID from the route MUST MATCH the ID contained in the move parameter"; nothing checks, and the
    /// answer to not matching is not the 400 that sentence implies. Ignoring <c>Id</c> in the profile - as
    /// <c>ScenarioEventProfile</c> does - turns both cases green and this test red.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Update_WithABodyIdThatIsNotTheRoutes_Is500(bool omitted)
    {
        var msel = await SeedMsel();
        var move = await SeedMove(msel, moveNumber: 1);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Put(
            Client(actor),
            move.Id,
            BodyFor(move) with { Id = omitted ? Guid.Empty : Guid.NewGuid(), Description = "after" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(move.Description, (await Stored(move.Id)).Description);
    }

    [Fact]
    public async Task Update_ToAMoveNumberTheMselAlreadyUses_Is500()
    {
        var msel = await SeedMsel();
        var move = await SeedMove(msel, moveNumber: 1);
        await SeedMove(msel, moveNumber: 2);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Put(Client(actor), move.Id, BodyFor(move) with { MoveNumber = 2 });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(1, (await Stored(move.Id)).MoveNumber);
    }

    [Fact]
    public async Task Update_MarksTheMselModifiedAndBroadcastsWhatChanged()
    {
        var msel = await SeedMsel();
        var move = await SeedMove(msel, moveNumber: 1);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Put(Client(actor), move.Id, BodyFor(move) with { Description = "after" });

        Assert.NotNull((await StoredMsel(msel.Id)).DateModified);

        var send = Hub.Of(MainHubMethods.MoveUpdated)[0];
        Assert.Equal(move.Id, ((ViewModels.Move)send.Payload).Id);
        Assert.Contains("description", (string[])send.Args[1]);
    }

    [Fact]
    public async Task Update_SanitizesTheSituationDescriptionAndNotTheDescription()
    {
        var msel = await SeedMsel();
        var move = await SeedMove(msel, moveNumber: 1);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Put(
            Client(actor),
            move.Id,
            BodyFor(move) with
            {
                Description = "<script>alert(1)</script>plain",
                SituationDescription = "<script>alert(1)</script>situation"
            });

        var stored = await Stored(move.Id);

        Assert.Equal("<script>alert(1)</script>plain", stored.Description);
        Assert.Equal("situation", stored.SituationDescription);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE moves/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_ForAnMselOwner_Is204AndRemovesTheRow()
    {
        var msel = await SeedMsel();
        var move = await SeedMove(msel, moveNumber: 1);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync(Move(move.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await Stored(move.Id));
    }

    [Theory]
    [InlineData(MselRole.Owner, HttpStatusCode.NoContent)]
    [InlineData(MselRole.MoveEditor, HttpStatusCode.NoContent)]
    [InlineData(MselRole.Editor, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Approver, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Viewer, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Evaluator, HttpStatusCode.Forbidden)]
    public async Task Delete_TheRolesThatMayRemoveAMove(MselRole role, HttpStatusCode expected)
    {
        var msel = await SeedMsel();
        var move = await SeedMove(msel, moveNumber: 1);
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Client(actor).DeleteAsync(Move(move.Id), Ct);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Delete_WithEditMselsAndNoRoleOnTheMsel_Is204()
    {
        var msel = await SeedMsel();
        var move = await SeedMove(msel, moveNumber: 1);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync(Move(move.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <remarks>
    /// The one method in this service that reads the stored row before deciding - and so the model for
    /// fixing <see cref="Update_ChoosesItsPermissionBranchFromTheRequestBody"/>. A caller who owns another
    /// MSEL entirely cannot touch this move, and there is no body to say otherwise.
    /// </remarks>
    [Fact]
    public async Task Delete_TakesItsPermissionDecisionFromTheStoredRow()
    {
        var mine = await SeedMsel();
        var theirs = await SeedMsel();
        var move = await SeedMove(theirs, moveNumber: 1);
        var actor = await Actor().OnMsel(mine, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync(Move(move.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(await Stored(move.Id));
    }

    /// <remarks>
    /// The lookup comes first here, so an id that is not there is a 404 for everybody - including a caller
    /// with no permissions at all, who learns from it that the id is unused. The opposite order to
    /// <c>UpdateAsync</c>, and the two cannot both be right.
    /// </remarks>
    [Fact]
    public async Task Delete_ForAnIdThatIsNotThere_Is404EvenForAStranger()
    {
        var response = await Client(await Actor().SeedAsync()).DeleteAsync(Move(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_MarksTheMselModifiedAndBroadcastsTheId()
    {
        var msel = await SeedMsel();
        var move = await SeedMove(msel, moveNumber: 1);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var before = DateTime.UtcNow;

        await Client(actor).DeleteAsync(Move(move.Id), Ct);

        var stored = await StoredMsel(msel.Id);
        Assert.Equal(actor.Id, stored.ModifiedBy);
        Assert.InRange(stored.DateModified.Value, before, DateTime.UtcNow);

        Assert.Equal(
            [msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.MoveDeleted));
        Assert.Equal(move.Id, Hub.Of(MainHubMethods.MoveDeleted)[0].Payload);
    }

    /// <remarks>
    /// <para>
    /// Nothing references a move. A scenario event belongs to one by falling inside its
    /// <c>DeltaSeconds</c> - <c>ScenarioEventEntity</c> has no move column - so removing a move does not
    /// orphan anything, it silently re-parents everything that was in it to the move before.
    /// </para>
    /// <para>
    /// Here move 2 starts an hour in and holds the event at ninety minutes; deleting it makes that event
    /// part of move 1, so its Steamfitter task name and its CITE and Gallery move-change URLs all change
    /// under a running exercise, with nothing warning the person who pressed delete and nothing to undo.
    /// Refusing to delete a move that has events in it, or renumbering, turns this red.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Delete_OfAMiddleMove_RegroupsItsScenarioEventsIntoThePrecedingMove()
    {
        var msel = await SeedMsel();
        await SeedMove(msel, moveNumber: 1, deltaSeconds: 0);
        var second = await SeedMove(msel, moveNumber: 2, deltaSeconds: 3600);
        var scenarioEvent = BlueprintAppFactory.ScenarioEvent(msel.Id, deltaSeconds: 5400);
        await Seed(scenarioEvent);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        Assert.Equal(2, (await MovesAndInjects(msel.Id))[scenarioEvent.Id][0]);

        var response = await Client(actor).DeleteAsync(Move(second.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, (await MovesAndInjects(msel.Id))[scenarioEvent.Id][0]);
    }

    /// <remarks>
    /// And deleting the last one leaves the MSEL in the state
    /// <c>ScenarioEventServiceMovesAndInjectsTests.GetMovesAndInjects_WithNoMoves_Throws</c> describes -
    /// scenario events and no moves, which indexes <c>moves[0]</c> on an empty array. So that
    /// <c>IndexOutOfRangeException</c> is not a theoretical state: one DELETE from the Moves tab reaches
    /// it, and the next Gallery push fails with an exception naming nothing. Guarding the index there, or
    /// refusing the delete here, turns this red.
    /// </remarks>
    [Fact]
    public async Task Delete_OfTheOnlyMove_MakesTheMoveLookupThrowForEveryScenarioEvent()
    {
        var msel = await SeedMsel();
        var move = await SeedMove(msel, moveNumber: 1, deltaSeconds: 0);
        await Seed(BlueprintAppFactory.ScenarioEvent(msel.Id, deltaSeconds: 60));
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync(Move(move.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await Assert.ThrowsAsync<IndexOutOfRangeException>(() => MovesAndInjects(msel.Id));
    }

    /// <remarks>
    /// Nor does a delete renumber what is left, so the Moves tab keeps whatever gaps a deletion makes and
    /// the numbers the sibling applications are told are not contiguous. Same shape as every other
    /// ordering column in the codebase, and unlike <c>ScenarioEventService</c>'s <c>GroupOrder</c>, nothing
    /// here even tries.
    /// </remarks>
    [Fact]
    public async Task Delete_LeavesAGapInTheMoveNumbers()
    {
        var msel = await SeedMsel();
        await SeedMove(msel, moveNumber: 1, deltaSeconds: 0);
        var second = await SeedMove(msel, moveNumber: 2, deltaSeconds: 100);
        await SeedMove(msel, moveNumber: 3, deltaSeconds: 200);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Client(actor).DeleteAsync(Move(second.Id), Ct);

        Assert.Equal([1, 3], (await GetMoves(Client(actor), msel.Id)).Select(x => x.MoveNumber).Order());
    }

    /// <remarks>
    /// The MSEL's own delete cascades - <c>MoveEntityConfiguration</c> says so - so a MSEL taking its moves
    /// with it is the database's answer rather than this service's, and no <c>MoveDeleted</c> broadcast is
    /// sent for any of them.
    /// </remarks>
    [Fact]
    public async Task DeletingTheMsel_TakesItsMovesWithIt()
    {
        var msel = await SeedMsel();
        var move = await SeedMove(msel, moveNumber: 1);
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/msels/{msel.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await Stored(move.Id));
        Assert.Empty(Hub.Of(MainHubMethods.MoveDeleted));
    }

    // ---------------------------------------------------------------------------------------------
    // Authentication
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "msels/00000000-0000-0000-0000-000000000001/moves")]
    [InlineData("GET", "moves/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "moves")]
    [InlineData("PUT", "moves/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "moves/00000000-0000-0000-0000-000000000001")]
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

    private const string Moves = "/api/moves";

    private static string Move(Guid id) => $"{Moves}/{id}";

    private static string MovesOf(Guid mselId) => $"/api/msels/{mselId}/moves";

    /// <summary>
    /// The wire shape of a move. A record rather than an anonymous type so a test can vary one property of
    /// a stored row with a <c>with</c> expression. <c>DateCreated</c> and <c>CreatedBy</c> are
    /// non-nullable on <c>ViewModels.Base</c>, so they are sent as values: a null is a 400 that never
    /// reaches the controller.
    /// </summary>
    private sealed record MoveBody
    {
        public Guid Id { get; init; }
        public int MoveNumber { get; init; }
        public string Description { get; init; }
        public int DeltaSeconds { get; init; }
        public DateTime? MoveStartTime { get; init; }
        public DateTime? SituationTime { get; init; }
        public string SituationDescription { get; init; }
        public Guid MselId { get; init; }

        public Guid CreatedBy { get; init; }
        public DateTime DateCreated { get; init; }
        public Guid? ModifiedBy { get; init; }
        public DateTime? DateModified { get; init; }
    }

    private static MoveBody Body(Guid mselId, int moveNumber = 0, int deltaSeconds = 0) => new()
    {
        MselId = mselId,
        MoveNumber = moveNumber,
        DeltaSeconds = deltaSeconds,
        Description = $"move-{moveNumber}",
        SituationDescription = "posted by the test"
    };

    private static MoveBody BodyFor(MoveEntity move) => new()
    {
        Id = move.Id,
        MselId = move.MselId,
        MoveNumber = move.MoveNumber,
        DeltaSeconds = move.DeltaSeconds,
        Description = move.Description,
        MoveStartTime = move.MoveStartTime,
        SituationTime = move.SituationTime,
        SituationDescription = move.SituationDescription
    };

    private async Task<MselEntity> SeedMsel(Guid? createdBy = null)
    {
        var msel = BlueprintAppFactory.Msel(createdBy: createdBy);
        await Seed(msel);

        return msel;
    }

    private async Task<MoveEntity> SeedMove(MselEntity msel, int moveNumber = 0, int deltaSeconds = 0)
    {
        var move = BlueprintAppFactory.Move(msel.Id, moveNumber, deltaSeconds);
        await Seed(move);

        return move;
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

    private Task<HttpResponseMessage> Post(HttpClient client, MoveBody body) =>
        client.PostAsJsonAsync(Moves, body, Ct);

    private Task<HttpResponseMessage> Put(HttpClient client, Guid id, MoveBody body) =>
        client.PutAsJsonAsync(Move(id), body, Ct);

    private async Task<List<ViewModels.Move>> GetMoves(HttpClient client, Guid mselId)
    {
        var response = await client.GetAsync(MovesOf(mselId), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<List<ViewModels.Move>>(response);
    }

    private async Task<ViewModels.Move> GetMove(HttpClient client, Guid id)
    {
        var response = await client.GetAsync(Move(id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<ViewModels.Move>(response);
    }

    /// <summary>
    /// Which move and which inject each of the MSEL's scenario events belongs to, read the way a Gallery
    /// push reads it. The service is constructed over a fresh context with nulls for its other three
    /// dependencies, as <c>ScenarioEventServiceMovesAndInjectsTests</c> does.
    /// </summary>
    private async Task<Dictionary<Guid, int[]>> MovesAndInjects(Guid mselId)
    {
        await using var context = NewContext();

        return await new ScenarioEventService(context, null, null, null).GetMovesAndInjects(mselId, Ct);
    }

    private async Task<MoveEntity> Stored(Guid id)
    {
        await using var context = NewContext();

        return await context.Moves.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, Ct);
    }

    private async Task<MselEntity> StoredMsel(Guid id)
    {
        await using var context = NewContext();

        return await context.Msels.AsNoTracking().SingleAsync(x => x.Id == id, Ct);
    }

    private async Task<int> CountOn(Guid mselId)
    {
        await using var context = NewContext();

        return await context.Moves.CountAsync(x => x.MselId == mselId, Ct);
    }

    private static StringContent EmptyJson() =>
        new(string.Empty, System.Text.Encoding.UTF8, "application/json");

    private async Task<string> Title(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions, Ct))?.Title;

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }
}
