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
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary><c>MoveService</c> / <c>MoveController</c> - the five routes behind the Moves tab, and the
/// coarse division of a MSEL's timeline that CITE, Gallery and Steamfitter are all told about.</summary>
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
    public async Task GetByMsel_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        var response = await Client(actor).GetAsync(MovesOf(msel.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Every MSEL role, and unit membership with no role, reads the moves.</summary>
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

    /// <summary>Get by MSEL for a MSEL that is not there is answered with a 500 for a caller without ViewMsels.</summary>
    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_Is500()
    {
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync(MovesOf(Guid.NewGuid()), Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Object reference not set to an instance of an object.", failure.Title);
        Assert.Contains("MselUserRequirement.IsMet", failure.Detail);
    }

    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_WithViewMsels_IsAnEmptyList()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync(MovesOf(Guid.NewGuid()), Ct);

        Assert.Empty(await Read<List<ViewModels.Move>>(response));
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

    /// <summary>A move's integers go out as JSON strings, as <c>JsonIntegerConverter</c> writes every
    /// <c>int</c>.</summary>
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
    public async Task Get_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var msel = await SeedMsel();
        var move = await SeedMove(msel, moveNumber: 1);
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

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

    /// <summary>Get for an id that is not there is answered with a 500.</summary>
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
    public async Task Create_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

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

    /// <summary>A create keeps a non-empty id from the body.</summary>
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

    /// <summary>Create with a move number the MSEL already uses is answered with a 500.</summary>
    [Fact]
    public async Task Create_WithAMoveNumberTheMselAlreadyUses_Is500()
    {
        var msel = await SeedMsel();
        await SeedMove(msel, moveNumber: 2);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id, moveNumber: 2));

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("An error occurred while saving the entity changes. See the inner exception for details.", failure.Title);
        Assert.Contains("MoveService.CreateAsync", failure.Detail);
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

    /// <summary>Create for a MSEL that is not there is answered with a 500 for a caller without EditMsels.</summary>
    [Fact]
    public async Task Create_ForAMselThatIsNotThere_Is500()
    {
        var actor = await Actor().SeedAsync();

        var response = await Post(Client(actor), Body(Guid.NewGuid(), moveNumber: 1));

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Object reference not set to an instance of an object.", failure.Title);
        Assert.Contains("MselOwnerRequirement.IsMet", failure.Detail);
    }

    // Same case as Create_ForAMselThatIsNotThere_Is500.
    [Fact]
    public async Task Create_ForAMselThatIsNotThere_WithEditMsels_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(Client(actor), Body(Guid.NewGuid(), moveNumber: 1));

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("An error occurred while saving the entity changes. See the inner exception for details.", failure.Title);
        Assert.Contains("MoveService.CreateAsync", failure.Detail);
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
            Hub.Recipients(MainHubMethods.MoveCreated, msel.Id));
        Assert.Equal(created.Id, ((ViewModels.Move)Hub.Of(MainHubMethods.MoveCreated, msel.Id)[0].Payload).Id);
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

    /// <summary>Update chooses its permission branch from the request body.</summary>
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

    /// <summary>Update that moves a move to another MSEL tells the old MSEL nothing.</summary>
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
        Assert.Equal([mine.Id.ToString(), MainHub.ADMIN_DATA_GROUP], Hub.Recipients(MainHubMethods.MoveUpdated, mine.Id));
        Assert.Empty(Hub.Of(MainHubMethods.MoveDeleted, mine.Id));
    }

    [Fact]
    public async Task Update_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var msel = await SeedMsel();
        var move = await SeedMove(msel, moveNumber: 1);
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        var response = await Put(Client(actor), move.Id, BodyFor(move) with { Description = "after" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(move.Description, (await Stored(move.Id)).Description);
    }

    [Fact]
    public async Task Update_ForAnIdThatIsNotThere_WithABodyNamingAMselTheCallerOwns_Is404()
    {
        var mine = await SeedMsel();
        var actor = await Actor().OnMsel(mine, MselRole.Owner).SeedAsync();

        var response = await Put(Client(actor), Guid.NewGuid(), Body(mine.Id, moveNumber: 1));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Same case as Update_ChoosesItsPermissionBranchFromTheRequestBody.
    [Fact]
    public async Task Update_ForAnIdThatIsNotThere_WithABodyNamingAnotherMsel_Is403()
    {
        var mine = await SeedMsel();
        var theirs = await SeedMsel();
        var actor = await Actor().OnMsel(mine, MselRole.Owner).SeedAsync();

        var response = await Put(Client(actor), Guid.NewGuid(), Body(theirs.Id, moveNumber: 1));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Update with a body id that is not the routes is answered with a 500.</summary>
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

        Assert.Equal("The property 'MoveEntity.Id' is part of a key and so cannot be modified or marked as modified. To change the principal of an existing entity with an identifying foreign key, first delete the dependent and invoke 'SaveChanges', and then associate the dependent with the new principal.", (await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
        Assert.Equal(move.Description, (await Stored(move.Id)).Description);
    }

    // Same case as Create_WithAMoveNumberTheMselAlreadyUses_Is500.
    [Fact]
    public async Task Update_ToAMoveNumberTheMselAlreadyUses_Is500()
    {
        var msel = await SeedMsel();
        var move = await SeedMove(msel, moveNumber: 1);
        await SeedMove(msel, moveNumber: 2);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Put(Client(actor), move.Id, BodyFor(move) with { MoveNumber = 2 });

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("An error occurred while saving the entity changes. See the inner exception for details.", failure.Title);
        Assert.Contains("MoveService.UpdateAsync", failure.Detail);
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

        var send = Hub.Of(MainHubMethods.MoveUpdated, msel.Id)[0];
        Assert.Equal(move.Id, ((ViewModels.Move)send.Payload).Id);
        Assert.Contains("description", (string[])send.Arguments[1]);
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

    /// <summary>Delete reads the stored row before deciding, so an owner of another MSEL cannot touch the
    /// move.</summary>
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
            Hub.Recipients(MainHubMethods.MoveDeleted, msel.Id));
        Assert.Equal(move.Id, Hub.Of(MainHubMethods.MoveDeleted, msel.Id)[0].Payload);
    }

    /// <summary>Delete of a middle move regroups its scenario events into the preceding move.</summary>
    [Fact]
    public async Task Delete_OfAMiddleMove_RegroupsItsScenarioEventsIntoThePrecedingMove()
    {
        var msel = await SeedMsel();
        await SeedMove(msel, moveNumber: 1, deltaSeconds: 0);
        var second = await SeedMove(msel, moveNumber: 2, deltaSeconds: 3600);
        var scenarioEvent = TestData.ScenarioEvent(msel.Id, deltaSeconds: 5400);
        await Seed(scenarioEvent);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        Assert.Equal(2, (await MovesAndInjects(msel.Id))[scenarioEvent.Id][0]);

        var response = await Client(actor).DeleteAsync(Move(second.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, (await MovesAndInjects(msel.Id))[scenarioEvent.Id][0]);
    }

    /// <summary>Delete of the only move makes the move lookup throw for every scenario event.</summary>
    [Fact]
    public async Task Delete_OfTheOnlyMove_MakesTheMoveLookupThrowForEveryScenarioEvent()
    {
        var msel = await SeedMsel();
        var move = await SeedMove(msel, moveNumber: 1, deltaSeconds: 0);
        await Seed(TestData.ScenarioEvent(msel.Id, deltaSeconds: 60));
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

    /// <summary>Deleting the MSEL deletes its moves by the cascade, with no <c>MoveDeleted</c>
    /// broadcast.</summary>
    [Fact]
    public async Task DeletingTheMsel_TakesItsMovesWithIt()
    {
        var msel = await SeedMsel();
        var move = await SeedMove(msel, moveNumber: 1);
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/msels/{msel.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await Stored(move.Id));
        Assert.Empty(Hub.Of(MainHubMethods.MoveDeleted, msel.Id));
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

        var response = await Client().SendAsync(request, Ct);

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
        var msel = TestData.Msel(createdBy: createdBy);
        await Seed(msel);

        return msel;
    }

    private async Task<MoveEntity> SeedMove(MselEntity msel, int moveNumber = 0, int deltaSeconds = 0)
    {
        var move = TestData.Move(msel.Id, moveNumber, deltaSeconds);
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
