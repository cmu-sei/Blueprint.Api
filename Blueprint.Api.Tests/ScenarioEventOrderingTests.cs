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
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>ScenarioEventService.ReorderScenarioEvents</c> - what creating, moving and deleting a row does to the
/// <c>GroupOrder</c> of every other row that shares its time.
/// </summary>
/// <remarks>
/// <para>
/// A timeline row is placed by two numbers: <c>DeltaSeconds</c>, the offset from the start of the exercise,
/// and <c>GroupOrder</c>, its position among the rows sharing that offset. The API maintains the second one
/// the way <c>DataFieldService</c> maintains <c>DisplayOrder</c> - a caller says "put this one second" and
/// the service renumbers the rest - but it is a different algorithm, numbered from zero rather than one, and
/// it gets a different set of answers wrong. Every assertion here is on the whole group rather than on one
/// row, written as <c>name=groupOrder</c>, because the interesting failures are a duplicated position or a
/// missing one and reading one row cannot see either.
/// </para>
/// <para>
/// The algorithm is short enough to state. Gather the rows that share the MSEL and the time and hold a
/// position at or after the requested one, excluding the row being placed. If one of them holds the
/// requested position exactly, shift the whole gathered set up to sit immediately after it - which also
/// closes any gaps between them - and the row keeps what it asked for. Otherwise the request is discarded
/// and the row is given <c>last + 1</c>, or zero if the set is empty.
/// </para>
/// <para>
/// That last sentence is where four of the five characterizations live, because "no row holds exactly that
/// position" is the normal case as soon as a group has a gap in it - and deleting a row always leaves one
/// (<see cref="Delete_LeavesAGapWhereTheRowWas"/>), since neither delete path renumbers anything despite
/// both saying they might. Asking for a position past the end of a group is answered with zero
/// (<see cref="Create_WithAPositionPastTheEnd_IsGivenPositionZeroInstead"/>,
/// <see cref="Update_MovingARowPastTheEnd_SendsItToPositionZero"/>), which duplicates the position of
/// whatever was already at the head; asking for a position inside a gap is answered with the end
/// (<see cref="Create_WithAPositionInsideAGap_TakesTheNumberAfterTheLastRow"/>); and changing a row's time
/// slot puts it at zero in the new group whatever position it asked for
/// (<see cref="Update_ChangingTheTimeSlot_PutsTheRowAtPositionZeroOfTheNewGroup"/>).
/// <see cref="Delete_ThenCreatingAtTheVisibleMiddle_LandsAtTheEnd"/> puts the delete and the create
/// together, because that is the sequence a user performs.
/// </para>
/// <para>
/// The fifth is an off-by-one. The gathered set excludes the row being placed but nothing closes the
/// position it vacated, so a row moved <em>down</em> its group always lands one place short of the one it
/// asked for and leaves its old position empty
/// (<see cref="Update_MovingARowDown_LandsItOnePositionShortOfTheOneItAskedFor"/>). A row can therefore
/// never be moved to the end of its group: the last position lands it second to last, and anything beyond
/// that is the previous paragraph. Moving a row <em>up</em> is exact.
/// </para>
/// <para>
/// And one more, in <c>CreateAsync</c> rather than in the helper. A new row with position zero is meant to
/// be appended rather than inserted - the comment at <c>ScenarioEventService.cs:720</c> says so - and the
/// test for "was this row created after the ones already there" reads <c>DateCreated</c> off the request
/// body. But <c>CreateAsync</c> reorders <em>before</em> it saves, and <c>BlueprintContext.SaveEntries</c>
/// is what puts a real timestamp there, so the field is still whatever the client sent: a body that omits
/// it carries <c>DateTime.MinValue</c> and the row is inserted at the head instead
/// (<see cref="Create_WithoutADateCreated_IsInsertedAtTheHeadInstead"/>). The same helper is called from
/// <c>CreateScenarioEventsFromInjectsAsync</c> <em>after</em> its save, where the intended behaviour is what
/// happens - so the append heuristic works in one caller and not the other.
/// </para>
/// </remarks>
public class ScenarioEventOrderingTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // Creating
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_IntoAnEmptyGroup_IsPositionZero()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Create(Client(actor), msel.Id, "N", groupOrder: 0);

        Assert.Equal(["N=0"], await Orders(msel.Id));
    }

    /// <summary>
    /// A new row asking for position zero is appended to the end of its group rather than inserted at the
    /// head, which is deliberate: the grid creates a row without a position and expects it at the bottom.
    /// </summary>
    [Fact]
    public async Task Create_WithAGroupOrderOfZero_IsAppendedToTheEnd()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Create(Client(actor), msel.Id, "N", groupOrder: 0);

        Assert.Equal(["A=0", "B=1", "C=2", "N=3"], await Orders(msel.Id));
    }

    /// <summary>
    /// The same request without a <c>dateCreated</c> in the body is inserted at the head instead.
    /// </summary>
    /// <remarks>
    /// Characterization, and the sharpest one here: two bodies differing only in a field the server is
    /// about to overwrite put the row in different places. The append special case at
    /// <c>ScenarioEventService.cs:722</c> only applies when every row already in the group was created no
    /// later than this one, and it reads the request body's <c>DateCreated</c> - which
    /// <c>CreateAsync</c> has not yet handed to <c>SaveEntries</c> to stamp, so an absent field is
    /// <c>DateTime.MinValue</c> and no existing row can be older than it. The stored value is server time
    /// either way, which is asserted here so the two halves are on one screen. Turns red when the reorder
    /// moves below the save, as it already is in
    /// <c>CreateScenarioEventsFromInjectsAsync</c>.
    /// </remarks>
    [Fact]
    public async Task Create_WithoutADateCreated_IsInsertedAtTheHeadInstead()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var before = DateTime.UtcNow;

        await CreateWithoutADate(Client(actor), msel.Id, "N", groupOrder: 0);

        Assert.Equal(["N=0", "A=1", "B=2", "C=3"], await Orders(msel.Id));

        var stored = await Stored(msel.Id, "N");

        Assert.InRange(stored.DateCreated, before, DateTime.UtcNow);
    }

    [Fact]
    public async Task Create_WithANonZeroGroupOrder_TakesThatPositionAndShiftsTheRestDown()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Create(Client(actor), msel.Id, "N", groupOrder: 1);

        Assert.Equal(["A=0", "N=1", "B=2", "C=3"], await Orders(msel.Id));
    }

    /// <summary>
    /// Asking for a position past the end of the group is answered with position zero, which duplicates
    /// the position of the row already there.
    /// </summary>
    /// <remarks>
    /// Characterization. No row holds position 5, so the request is discarded and the row is given
    /// <c>last + 1</c> over a set that is empty - which is zero, not the end. Two rows now claim the head
    /// of the group and the list route sorts on <c>GroupOrder</c> alone, so which of them the grid draws
    /// first is undefined. Turns red when the fallback counts the whole group rather than the rows at or
    /// after the requested position.
    /// </remarks>
    [Fact]
    public async Task Create_WithAPositionPastTheEnd_IsGivenPositionZeroInstead()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Create(Client(actor), msel.Id, "N", groupOrder: 5);

        Assert.Equal(["A=0", "N=0", "B=1"], await Orders(msel.Id));
    }

    /// <summary>
    /// Asking for a position inside a gap is answered with the end of the group.
    /// </summary>
    /// <remarks>
    /// Characterization, the other half of
    /// <see cref="Create_WithAPositionPastTheEnd_IsGivenPositionZeroInstead"/>: here the set of rows at or
    /// after position 1 is not empty, so the row is given one more than the last of them. The requested
    /// position is free and the row does not get it.
    /// </remarks>
    [Fact]
    public async Task Create_WithAPositionInsideAGap_TakesTheNumberAfterTheLastRow()
    {
        var msel = await SeedMsel();
        await SeedAt(msel, 0, 0, "A");
        await SeedAt(msel, 0, 3, "B");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Create(Client(actor), msel.Id, "N", groupOrder: 1);

        Assert.Equal(["A=0", "B=3", "N=4"], await Orders(msel.Id));
    }

    /// <summary>
    /// A shift renumbers the rows it moves consecutively, so it also closes any gaps between them.
    /// </summary>
    [Fact]
    public async Task Create_ShiftingTheRestClosesTheGapsBetweenThem()
    {
        var msel = await SeedMsel();
        await SeedAt(msel, 0, 0, "A");
        await SeedAt(msel, 0, 5, "B");
        await SeedAt(msel, 0, 9, "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await CreateWithoutADate(Client(actor), msel.Id, "N", groupOrder: 0);

        Assert.Equal(["N=0", "A=1", "B=2", "C=3"], await Orders(msel.Id));
    }

    [Fact]
    public async Task Create_RenumbersOnlyItsOwnTimeSlot()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B");
        await SeedGroup(msel, 60, "X", "Y");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Create(Client(actor), msel.Id, "N", deltaSeconds: 0, groupOrder: 1);

        Assert.Equal(
            ["A=0:0", "N=0:1", "B=0:2", "X=60:0", "Y=60:1"],
            await Timeline(msel.Id));
    }

    [Fact]
    public async Task Create_RenumbersOnlyItsOwnMsel()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B");
        var other = await SeedMsel();
        await SeedGroup(other, 0, "A", "B");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Create(Client(actor), msel.Id, "N", groupOrder: 1);

        Assert.Equal(["A=0", "N=1", "B=2"], await Orders(msel.Id));
        Assert.Equal(["A=0", "B=1"], await Orders(other.Id));
    }

    /// <summary>
    /// The create answers a list rather than the one row it made: the new row first, then every row whose
    /// position it changed, carrying the new positions.
    /// </summary>
    /// <remarks>
    /// The list is the right shape for what the request does - a client that took only the created row
    /// would draw the rest of the group in the wrong order - which is why the 201 the route declares is
    /// the part that is wrong. See the contract notes.
    /// </remarks>
    [Fact]
    public async Task Create_ReturnsTheNewRowFollowedByEveryRowItRenumbered()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var returned = await Create(Client(actor), msel.Id, "N", groupOrder: 1);

        Assert.Equal(
            ["N=1", "B=2", "C=3"],
            returned.Select(x => $"{x.Information}={x.GroupOrder}"));
    }

    [Fact]
    public async Task Create_WhenNothingIsRenumbered_ReturnsOnlyTheNewRow()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var returned = await Create(Client(actor), msel.Id, "N", groupOrder: 0);

        Assert.Equal("N=2", $"{Assert.Single(returned).Information}={returned[0].GroupOrder}");
        Assert.Empty(Hub.Of(MainHubMethods.ScenarioEventUpdated));
    }

    /// <summary>
    /// Every row the renumbering touches is broadcast as an update, and the ones it left alone are not.
    /// </summary>
    /// <remarks>
    /// The renumbering saves inside the request's transaction, so these arrive with the create - a client
    /// redrawing the group from the <c>ScenarioEventCreated</c> message alone would have the other rows'
    /// positions wrong.
    /// </remarks>
    [Fact]
    public async Task Create_BroadcastsAnUpdateForEveryRowItRenumbers()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Create(Client(actor), msel.Id, "N", groupOrder: 1);

        Assert.Equal(["B=2", "C=3"], Broadcast(MainHubMethods.ScenarioEventUpdated, msel));
        Assert.Equal(["N=1"], Broadcast(MainHubMethods.ScenarioEventCreated, msel));
    }

    // ---------------------------------------------------------------------------------------------
    // Moving
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_MovingARowUp_ShiftsTheRowsItPassesDown()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel.Id, "C", toGroupOrder: 1);

        Assert.Equal(["A=0", "C=1", "B=2"], await Orders(msel.Id));
    }

    [Fact]
    public async Task Update_MovingARowToTheHead_ShiftsEverythingDown()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel.Id, "C", toGroupOrder: 0);

        Assert.Equal(["C=0", "A=1", "B=2"], await Orders(msel.Id));
    }

    /// <summary>
    /// A row moved down its group lands one position short of the one it asked for, and its old position
    /// is left empty.
    /// </summary>
    /// <remarks>
    /// Characterization. Asking for the position <c>C</c> holds puts <c>A</c> in front of <c>C</c> rather
    /// than behind it, because the rows to shift are gathered as "at or after the requested position",
    /// which excludes <c>A</c>'s own old position without closing it. So a row can never be moved to the
    /// end of its group: this is as far as it goes, and asking for anything beyond is
    /// <see cref="Update_MovingARowPastTheEnd_SendsItToPositionZero"/>. Turns red when the vacated
    /// position is accounted for.
    /// </remarks>
    [Fact]
    public async Task Update_MovingARowDown_LandsItOnePositionShortOfTheOneItAskedFor()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel.Id, "A", toGroupOrder: 2);

        Assert.Equal(["B=1", "A=2", "C=3"], await Orders(msel.Id));
    }

    /// <summary>
    /// Moving a row past the end of its group sends it to position zero instead, on top of whatever was
    /// at the head.
    /// </summary>
    /// <remarks>
    /// Characterization; the update path's instance of
    /// <see cref="Create_WithAPositionPastTheEnd_IsGivenPositionZeroInstead"/>, and worse here because the
    /// row visibly jumps to the opposite end of the group from the one the user dragged it to. Turns red
    /// with it.
    /// </remarks>
    [Fact]
    public async Task Update_MovingARowPastTheEnd_SendsItToPositionZero()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel.Id, "B", toGroupOrder: 5);

        Assert.Equal(["A=0", "B=0", "C=2"], await Orders(msel.Id));
    }

    /// <summary>
    /// Moving a row to a free position inside a gap sends it to the end of the group.
    /// </summary>
    /// <remarks>
    /// Characterization, pairing with
    /// <see cref="Create_WithAPositionInsideAGap_TakesTheNumberAfterTheLastRow"/> and turning red with it.
    /// </remarks>
    [Fact]
    public async Task Update_ToAPositionInsideAGap_TakesTheNumberAfterTheLastRow()
    {
        var msel = await SeedMsel();
        await SeedAt(msel, 0, 0, "A");
        await SeedAt(msel, 0, 3, "B");
        await SeedAt(msel, 0, 9, "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel.Id, "C", toGroupOrder: 1);

        Assert.Equal(["A=0", "B=3", "C=4"], await Orders(msel.Id));
    }

    [Fact]
    public async Task Update_TheRenumberingClosesGapsBetweenTheRowsItShifts()
    {
        var msel = await SeedMsel();
        await SeedAt(msel, 0, 0, "A");
        await SeedAt(msel, 0, 4, "B");
        await SeedAt(msel, 0, 8, "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel.Id, "C", toGroupOrder: 0);

        Assert.Equal(["C=0", "A=1", "B=2"], await Orders(msel.Id));
    }

    /// <summary>
    /// A row moved to another time slot is given position zero there whatever position it asked for.
    /// </summary>
    /// <remarks>
    /// Characterization. The request keeps <c>GroupOrder</c> 1 and changes only the time, and the new
    /// group has nothing at or after 1, so the fallback answers zero - where <c>X</c> already is. A row
    /// moved between time slots therefore keeps the position it asked for only when the new group happens
    /// to have a row holding it, which is
    /// <see cref="Update_ChangingTheTimeSlot_LandsOnAnOccupiedPositionAndShiftsIt"/>; otherwise it collides
    /// with the head. Turns red with
    /// <see cref="Update_MovingARowPastTheEnd_SendsItToPositionZero"/>.
    /// </remarks>
    [Fact]
    public async Task Update_ChangingTheTimeSlot_PutsTheRowAtPositionZeroOfTheNewGroup()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B");
        await SeedGroup(msel, 60, "X");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel.Id, "B", toDeltaSeconds: 60);

        Assert.Equal(["A=0:0", "B=60:0", "X=60:0"], await Timeline(msel.Id));
    }

    /// <summary>
    /// The one case where a row moved to another time slot keeps the position it asked for: the new group
    /// already has a row holding it, so the shift happens.
    /// </summary>
    /// <remarks>
    /// The contrast with <see cref="Update_ChangingTheTimeSlot_PutsTheRowAtPositionZeroOfTheNewGroup"/> is
    /// the finding rather than either result on its own. The same request against two groups of different
    /// depths places the row differently, and a caller cannot know which it will get without knowing what
    /// is already in the destination.
    /// </remarks>
    [Fact]
    public async Task Update_ChangingTheTimeSlot_LandsOnAnOccupiedPositionAndShiftsIt()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B");
        await SeedGroup(msel, 60, "X", "Y");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel.Id, "B", toDeltaSeconds: 60);

        Assert.Equal(["A=0:0", "X=60:0", "B=60:1", "Y=60:2"], await Timeline(msel.Id));
    }

    /// <summary>
    /// The group the row left is not renumbered, so it keeps the hole.
    /// </summary>
    /// <remarks>
    /// Characterization. <c>ReorderScenarioEvents</c> is called once, against the row's new time, so the
    /// old group is never looked at - and the next row created in it gets the wrong position for the
    /// reason <see cref="Create_WithAPositionInsideAGap_TakesTheNumberAfterTheLastRow"/> gives. Turns red
    /// when the move renumbers both groups.
    /// </remarks>
    [Fact]
    public async Task Update_ChangingTheTimeSlot_LeavesAGapBehind()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel.Id, "B", toDeltaSeconds: 60);

        Assert.Equal(["A=0:0", "C=0:2", "B=60:0"], await Timeline(msel.Id));
    }

    /// <summary>
    /// A PUT that changes neither the time nor the position renumbers nothing, so an ordering that is
    /// already inconsistent stays that way.
    /// </summary>
    [Fact]
    public async Task Update_ThatChangesNeitherTimeNorPosition_RenumbersNothing()
    {
        var msel = await SeedMsel();
        await SeedAt(msel, 0, 0, "A");
        await SeedAt(msel, 0, 0, "B");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var row = await Stored(msel.Id, "A");
        var response = await Client(actor).PutAsJsonAsync(
            $"/api/scenarioEvents/{row.Id}",
            BodyFor(row) with { Information = "A" , IntegrationTarget = "renamed" },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["A=0", "B=0"], await Orders(msel.Id));
    }

    [Fact]
    public async Task Update_RenumbersOnlyItsOwnMsel()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C");
        var other = await SeedMsel();
        await SeedGroup(other, 0, "A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel.Id, "C", toGroupOrder: 0);

        Assert.Equal(["C=0", "A=1", "B=2"], await Orders(msel.Id));
        Assert.Equal(["A=0", "B=1", "C=2"], await Orders(other.Id));
    }

    [Fact]
    public async Task Update_ReturnsTheRowFollowedByEveryRowItRenumbered()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var returned = await Move(Client(actor), msel.Id, "C", toGroupOrder: 1);

        Assert.Equal(
            ["C=1", "B=2"],
            returned.Select(x => $"{x.Information}={x.GroupOrder}"));
    }

    [Fact]
    public async Task Update_BroadcastsAnUpdateForEveryRowItRenumbers()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel.Id, "C", toGroupOrder: 0);

        Assert.Equal(
            ["A=1", "B=2", "C=0"],
            Broadcast(MainHubMethods.ScenarioEventUpdated, msel).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A row that was only shifted out of the way records a modification with no modifier.
    /// </summary>
    /// <remarks>
    /// Characterization. <c>SaveEntries</c> stamps <c>DateModified</c> on every modified row, but
    /// <c>ModifiedBy</c> is the service's job and <c>ReorderScenarioEvents</c> only writes
    /// <c>GroupOrder</c> - so the audit trail of a shifted row says it changed and does not say who
    /// changed it. Turns red when the reorder stamps the caller.
    /// </remarks>
    [Fact]
    public async Task Update_AShiftedRowIsStampedModifiedWithNoModifier()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel.Id, "B", toGroupOrder: 0);

        var shifted = await Stored(msel.Id, "A");

        Assert.NotNull(shifted.DateModified);
        Assert.Null(shifted.ModifiedBy);

        var moved = await Stored(msel.Id, "B");

        Assert.Equal(actor.Id, moved.ModifiedBy);
    }

    // ---------------------------------------------------------------------------------------------
    // Deleting
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Deleting a row leaves its position empty.
    /// </summary>
    /// <remarks>
    /// Characterization, and the one that makes the others reachable: <c>DeleteAsync</c> opens a
    /// transaction "because we may also update DataValues and other scenario events" and then updates
    /// neither, so every delete puts a gap in a group and gaps are what the placement fallback handles
    /// badly. <c>DataFieldService.Delete</c> renumbers; this does not. Turns red when it does.
    /// </remarks>
    [Fact]
    public async Task Delete_LeavesAGapWhereTheRowWas()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var row = await Stored(msel.Id, "B");
        var response = await Client(actor).DeleteAsync($"/api/scenarioEvents/{row.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["A=0", "C=2"], await Orders(msel.Id));
    }

    /// <summary>
    /// A batch delete leaves every position it emptied empty.
    /// </summary>
    /// <remarks>
    /// Characterization, the same as <see cref="Delete_LeavesAGapWhereTheRowWas"/> and turning red with
    /// it. Asserted separately because a batch delete is how the grid removes a selection, so this is the
    /// path that produces several gaps at once.
    /// </remarks>
    [Fact]
    public async Task BatchDelete_LeavesGapsWhereTheRowsWere()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C", "D");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/scenarioEvents/batchDelete",
            new[] { (await Stored(msel.Id, "A")).Id, (await Stored(msel.Id, "C")).Id },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["B=1", "D=3"], await Orders(msel.Id));
    }

    /// <summary>
    /// Deleting a row and then creating one where the grid now shows the middle puts the new row at the
    /// end.
    /// </summary>
    /// <remarks>
    /// Characterization, and the reason the other two matter: this is one user doing two ordinary things.
    /// The delete leaves position 1 empty, so the create's request for it finds no row holding it and
    /// falls back to the end. Turns red when either half is fixed - the delete renumbering, or the
    /// fallback honouring a free position.
    /// </remarks>
    [Fact]
    public async Task Delete_ThenCreatingAtTheVisibleMiddle_LandsAtTheEnd()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var client = Client(actor);

        var row = await Stored(msel.Id, "B");
        await client.DeleteAsync($"/api/scenarioEvents/{row.Id}", Ct);

        await Create(client, msel.Id, "N", groupOrder: 1);

        Assert.Equal(["A=0", "C=2", "N=3"], await Orders(msel.Id));
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The wire shape of a scenario event, trimmed to the fields this file varies. <c>DateCreated</c> and
    /// <c>CreatedBy</c> are non-nullable on <c>ViewModels.Base</c>, so they are sent as values: a null is a
    /// 400 that never reaches the controller.
    /// </summary>
    private sealed record EventBody
    {
        public Guid Id { get; init; }
        public Guid MselId { get; init; }
        public int DeltaSeconds { get; init; }
        public int GroupOrder { get; init; }
        public string Information { get; init; }
        public string IntegrationTarget { get; init; }
        public EventType ScenarioEventType { get; init; }

        public Guid CreatedBy { get; init; }
        public DateTime DateCreated { get; init; }
        public Guid? ModifiedBy { get; init; }
        public DateTime? DateModified { get; init; }
    }

    private static EventBody BodyFor(ScenarioEventEntity scenarioEvent) => new()
    {
        Id = scenarioEvent.Id,
        MselId = scenarioEvent.MselId,
        DeltaSeconds = scenarioEvent.DeltaSeconds,
        GroupOrder = scenarioEvent.GroupOrder,
        Information = scenarioEvent.Information,
        IntegrationTarget = scenarioEvent.IntegrationTarget,
        ScenarioEventType = scenarioEvent.ScenarioEventType,
        DateCreated = scenarioEvent.DateCreated
    };

    private async Task<MselEntity> SeedMsel()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        return msel;
    }

    /// <summary>
    /// One row per name at <paramref name="deltaSeconds"/>, numbered from zero in the order given.
    /// </summary>
    private async Task SeedGroup(MselEntity msel, int deltaSeconds, params string[] names)
    {
        for (var i = 0; i < names.Length; i++)
        {
            await SeedAt(msel, deltaSeconds, i, names[i]);
        }
    }

    /// <summary>
    /// One row at an exact position, for the groups that need a gap in them.
    /// </summary>
    private async Task SeedAt(MselEntity msel, int deltaSeconds, int groupOrder, string name)
    {
        var scenarioEvent = BlueprintAppFactory.ScenarioEvent(msel.Id, deltaSeconds, groupOrder);
        scenarioEvent.Information = name;
        await Seed(scenarioEvent);
    }

    /// <summary>
    /// The rows of one MSEL as <c>name=groupOrder</c>, in the order the grid draws them. The tie-break on
    /// name is the test's, not the API's: the list route sorts on <c>DeltaSeconds</c> and
    /// <c>GroupOrder</c> alone, so two rows sharing both have no defined order and an assertion cannot
    /// depend on one.
    /// </summary>
    private async Task<string[]> Orders(Guid mselId)
    {
        var rows = await Rows(mselId);

        return rows.Select(x => $"{x.Information}={x.GroupOrder}").ToArray();
    }

    /// <summary>
    /// The same set as <see cref="Orders"/> as <c>name=deltaSeconds:groupOrder</c>, for the tests that
    /// span more than one time slot.
    /// </summary>
    private async Task<string[]> Timeline(Guid mselId)
    {
        var rows = await Rows(mselId);

        return rows.Select(x => $"{x.Information}={x.DeltaSeconds}:{x.GroupOrder}").ToArray();
    }

    private async Task<List<ScenarioEventEntity>> Rows(Guid mselId)
    {
        await using var context = NewContext();

        return await context.ScenarioEvents
            .AsNoTracking()
            .Where(x => x.MselId == mselId)
            .OrderBy(x => x.DeltaSeconds)
            .ThenBy(x => x.GroupOrder)
            .ThenBy(x => x.Information)
            .ToListAsync(Ct);
    }

    private async Task<ScenarioEventEntity> Stored(Guid mselId, string name)
    {
        await using var context = NewContext();

        return await context.ScenarioEvents
            .AsNoTracking()
            .SingleAsync(x => x.MselId == mselId && x.Information == name, Ct);
    }

    /// <summary>
    /// The rows carried by one broadcast method, as <c>name=groupOrder</c>.
    /// </summary>
    private string[] Broadcast(string method, MselEntity msel) =>
        Hub.Of(method)
            .Where(x => x.Group == msel.Id.ToString())
            .Select(x => (ScenarioEvent)x.Payload)
            .Select(x => $"{x.Information}={x.GroupOrder}")
            .ToArray();

    /// <summary>
    /// Creates a row, as a client sending the whole view model does: with a <c>dateCreated</c> of now,
    /// which the server overwrites and which decides where a row asking for position zero lands. See
    /// <see cref="Create_WithoutADateCreated_IsInsertedAtTheHeadInstead"/>.
    /// </summary>
    private Task<List<ScenarioEvent>> Create(
        HttpClient client,
        Guid mselId,
        string name,
        int deltaSeconds = 0,
        int groupOrder = 0) =>
        Post(client, mselId, name, deltaSeconds, groupOrder, DateTime.UtcNow);

    /// <summary>
    /// The same request with no <c>dateCreated</c> in the body.
    /// </summary>
    private Task<List<ScenarioEvent>> CreateWithoutADate(
        HttpClient client,
        Guid mselId,
        string name,
        int deltaSeconds = 0,
        int groupOrder = 0) =>
        Post(client, mselId, name, deltaSeconds, groupOrder, default);

    private async Task<List<ScenarioEvent>> Post(
        HttpClient client,
        Guid mselId,
        string name,
        int deltaSeconds,
        int groupOrder,
        DateTime dateCreated)
    {
        var response = await client.PostAsJsonAsync(
            "/api/scenarioEvents",
            new EventBody
            {
                MselId = mselId,
                DeltaSeconds = deltaSeconds,
                GroupOrder = groupOrder,
                Information = name,
                ScenarioEventType = EventType.Inject,
                DateCreated = dateCreated
            },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<List<ScenarioEvent>>(response);
    }

    private async Task<List<ScenarioEvent>> Move(
        HttpClient client,
        Guid mselId,
        string name,
        int? toDeltaSeconds = null,
        int? toGroupOrder = null)
    {
        var row = await Stored(mselId, name);
        var body = BodyFor(row) with
        {
            DeltaSeconds = toDeltaSeconds ?? row.DeltaSeconds,
            GroupOrder = toGroupOrder ?? row.GroupOrder
        };

        var response = await client.PutAsJsonAsync($"/api/scenarioEvents/{row.Id}", body, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<List<ScenarioEvent>>(response);
    }

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }
}
