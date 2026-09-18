// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Infrastructure;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>ScenarioEventService.GetMovesAndInjects</c> - which move and which inject each scenario event
/// belongs to.
/// </summary>
/// <remarks>
/// <para>
/// Thirty-five lines, one caller, and every Gallery article's <c>Move</c> and <c>Inject</c> comes out of
/// it. The service is constructed directly with the test's context and <c>null</c> for its other three
/// dependencies, which is both the smallest arrangement that works and a proof that this method reads
/// nothing but the two tables.
/// </para>
/// <para>
/// The algorithm in a sentence: walk the MSEL's scenario events in <c>DeltaSeconds</c> order, advancing to
/// the next move when an event reaches that move's <c>DeltaSeconds</c>, and counting injects within a move
/// by how many distinct times have been seen.
/// </para>
/// <para>
/// <strong>A MSEL with scenario events and no moves is an <c>IndexOutOfRangeException</c>.</strong> Line
/// 701 computes <c>var moveNumber = moves.Length &gt; m ? moves[m].MoveNumber : 0;</c> - guarded exactly
/// right - and then line 703 ignores it and indexes <c>moves[m]</c> directly. With no moves that is
/// <c>moves[0]</c> on an empty array. So the dead local is the fix, sitting two lines above the defect it
/// would have prevented. See <see cref="GetMovesAndInjects_WithNoMoves_Throws"/>.
/// </para>
/// <para>
/// <strong>It has a private twin that returns something different.</strong>
/// <c>IntegrationService.GetMovesAndGroups</c> is the same algorithm for Steamfitter, and it too computes a
/// correctly-guarded <c>moveNumber</c> local and ignores it - but where this method then uses
/// <c>moves[m].MoveNumber</c>, that one stores the move's <em>index</em>. So the same MSEL yields
/// <c>MoveNumber</c> to Gallery and a zero-based index to Steamfitter, and a MSEL whose moves are numbered
/// 1, 2, 3 has its Steamfitter task names and its CITE and Gallery move-change URLs built from 0, 1, 2.
/// That method is private and is covered with the worker; the contrast is pinned here by
/// <see cref="GetMovesAndInjects_ReturnsTheMovesOwnNumberRatherThanItsIndex"/>.
/// </para>
/// <para>
/// <strong>An event earlier than the first move belongs to it anyway.</strong> The move index starts at 0
/// and only ever advances, so an event whose <c>DeltaSeconds</c> precedes the first move's is reported as
/// being in that move. There is no "before the exercise starts" answer.
/// </para>
/// <para>
/// Per this branch's rule, every test above characterizes rather than fixes, and says what fixing it will do
/// to the test.
/// </para>
/// </remarks>
public class ScenarioEventServiceMovesAndInjectsTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    [Fact]
    public async Task GetMovesAndInjects_WithNoScenarioEvents_IsEmpty()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        await Seed(BlueprintAppFactory.Move(msel.Id, moveNumber: 0, deltaSeconds: 0));

        Assert.Empty(await Service().GetMovesAndInjects(msel.Id, Ct));
    }

    /// <remarks>
    /// Two moves, four events. The first two share a time so they share an inject; the third is later in the
    /// same move so it is the next inject; the fourth reaches the second move's <c>DeltaSeconds</c> so it
    /// starts that move at inject 0.
    /// </remarks>
    [Fact]
    public async Task GetMovesAndInjects_AssignsEveryEventToAMoveAndAnInject()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        await Seed(
            BlueprintAppFactory.Move(msel.Id, moveNumber: 0, deltaSeconds: 0),
            BlueprintAppFactory.Move(msel.Id, moveNumber: 1, deltaSeconds: 3600));

        var first = await SeedEvent(msel.Id, 0);
        var second = await SeedEvent(msel.Id, 0);
        var third = await SeedEvent(msel.Id, 60);
        var fourth = await SeedEvent(msel.Id, 3600);

        var answer = await Service().GetMovesAndInjects(msel.Id, Ct);

        Assert.Equal([0, 0], answer[first.Id]);
        Assert.Equal([0, 0], answer[second.Id]);
        Assert.Equal([0, 1], answer[third.Id]);
        Assert.Equal([1, 0], answer[fourth.Id]);
    }

    [Fact]
    public async Task GetMovesAndInjects_IncrementsTheInjectForEachNewTimeWithinAMove()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        await Seed(BlueprintAppFactory.Move(msel.Id, moveNumber: 0, deltaSeconds: 0));

        var events = new List<ScenarioEventEntity>();

        foreach (var deltaSeconds in new[] { 0, 10, 20, 30 })
        {
            events.Add(await SeedEvent(msel.Id, deltaSeconds));
        }

        var answer = await Service().GetMovesAndInjects(msel.Id, Ct);

        Assert.Equal([0, 1, 2, 3], events.Select(x => answer[x.Id][1]));
    }

    /// <remarks>
    /// The inject is a count of distinct times, not of events, so a whole batch of events scheduled together
    /// is one inject - which is what makes an inject the unit Gallery advances through.
    /// </remarks>
    [Fact]
    public async Task GetMovesAndInjects_KeepsOneInjectForEventsSharingATime()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        await Seed(BlueprintAppFactory.Move(msel.Id, moveNumber: 0, deltaSeconds: 0));

        var events = new List<ScenarioEventEntity>();

        foreach (var _ in Enumerable.Range(0, 3))
        {
            events.Add(await SeedEvent(msel.Id, 120));
        }

        var answer = await Service().GetMovesAndInjects(msel.Id, Ct);

        Assert.All(events, x => Assert.Equal(0, answer[x.Id][1]));
    }

    [Fact]
    public async Task GetMovesAndInjects_ResetsTheInjectToZeroAtAMoveBoundary()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        await Seed(
            BlueprintAppFactory.Move(msel.Id, moveNumber: 0, deltaSeconds: 0),
            BlueprintAppFactory.Move(msel.Id, moveNumber: 1, deltaSeconds: 100));

        await SeedEvent(msel.Id, 0);
        await SeedEvent(msel.Id, 10);
        var acrossTheBoundary = await SeedEvent(msel.Id, 100);

        var answer = await Service().GetMovesAndInjects(msel.Id, Ct);

        Assert.Equal([1, 0], answer[acrossTheBoundary.Id]);
    }

    /// <remarks>
    /// The move's own <c>MoveNumber</c>, which is what a MSEL author sets and what CITE and Gallery are told
    /// to advance to. Numbering the moves 5 and 6 gives 5 and 6 here - and 0 and 1 from
    /// <c>IntegrationService.GetMovesAndGroups</c>, which stores the index instead. Making that method agree
    /// with this one is the fix; this test is what says which of the two is right.
    /// </remarks>
    [Fact]
    public async Task GetMovesAndInjects_ReturnsTheMovesOwnNumberRatherThanItsIndex()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        await Seed(
            BlueprintAppFactory.Move(msel.Id, moveNumber: 5, deltaSeconds: 0),
            BlueprintAppFactory.Move(msel.Id, moveNumber: 6, deltaSeconds: 100));

        var inFirst = await SeedEvent(msel.Id, 0);
        var inSecond = await SeedEvent(msel.Id, 100);

        var answer = await Service().GetMovesAndInjects(msel.Id, Ct);

        Assert.Equal(5, answer[inFirst.Id][0]);
        Assert.Equal(6, answer[inSecond.Id][0]);
    }

    /// <remarks>
    /// <para>
    /// The defect in this class's remarks. <c>moves[m]</c> with <c>m</c> zero and no moves at all. A MSEL
    /// with a timeline and no moves is an ordinary state - moves are a separate tab in the editor - and this
    /// is reached from <c>CreateArticlesAsync</c>, so the Gallery half of a push fails with an
    /// <c>IndexOutOfRangeException</c> naming nothing.
    /// </para>
    /// <para>
    /// Using the guarded local on line 701 instead of re-indexing on line 703 turns this test red and gives
    /// every event move 0. That is what the local was written for.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GetMovesAndInjects_WithNoMoves_Throws()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        await SeedEvent(msel.Id, 0);

        await Assert.ThrowsAsync<IndexOutOfRangeException>(() =>
            Service().GetMovesAndInjects(msel.Id, Ct));
    }

    /// <remarks>
    /// The move index starts at zero and only advances, so there is no answer for "before the first move".
    /// An event scheduled earlier than the first move is reported as being in it, at inject 0.
    /// </remarks>
    [Fact]
    public async Task GetMovesAndInjects_ForAnEventEarlierThanTheFirstMove_PutsItInThatMove()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        await Seed(BlueprintAppFactory.Move(msel.Id, moveNumber: 1, deltaSeconds: 1000));

        var early = await SeedEvent(msel.Id, 0);

        var answer = await Service().GetMovesAndInjects(msel.Id, Ct);

        Assert.Equal([1, 0], answer[early.Id]);
    }

    /// <remarks>
    /// The inner <c>while</c> advances past as many moves as the event has overtaken, so a move with no
    /// events of its own is skipped rather than mis-assigned. This is the one place the algorithm is more
    /// careful than it looks.
    /// </remarks>
    [Fact]
    public async Task GetMovesAndInjects_SkipsAMoveWithNoEventsInIt()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        await Seed(
            BlueprintAppFactory.Move(msel.Id, moveNumber: 0, deltaSeconds: 0),
            BlueprintAppFactory.Move(msel.Id, moveNumber: 1, deltaSeconds: 100),
            BlueprintAppFactory.Move(msel.Id, moveNumber: 2, deltaSeconds: 200));

        var first = await SeedEvent(msel.Id, 0);
        var last = await SeedEvent(msel.Id, 250);

        var answer = await Service().GetMovesAndInjects(msel.Id, Ct);

        Assert.Equal([0, 0], answer[first.Id]);
        Assert.Equal([2, 0], answer[last.Id]);
    }

    /// <remarks>
    /// Both queries are scoped to the MSEL and ordered by <c>DeltaSeconds</c> - not by <c>GroupOrder</c>,
    /// which is what <c>GET msels/{id}/scenarioEvents</c> orders by after it. So the inject numbers here and
    /// the row order the UI shows are two different orderings of the same events, agreeing only when
    /// <c>GroupOrder</c> already follows time.
    /// </remarks>
    [Fact]
    public async Task GetMovesAndInjects_ReadsOnlyTheMselsOwnEventsAndMoves()
    {
        var mine = BlueprintAppFactory.Msel();
        var theirs = BlueprintAppFactory.Msel();
        await Seed(mine, theirs);
        await Seed(
            BlueprintAppFactory.Move(mine.Id, moveNumber: 0, deltaSeconds: 0),
            BlueprintAppFactory.Move(theirs.Id, moveNumber: 9, deltaSeconds: 0));

        var ofMine = await SeedEvent(mine.Id, 0);
        await SeedEvent(theirs.Id, 0);

        var answer = await Service().GetMovesAndInjects(mine.Id, Ct);

        Assert.Equal(ofMine.Id, Assert.Single(answer).Key);
        Assert.Equal([0, 0], answer[ofMine.Id]);
    }

    /// <remarks>
    /// A later-numbered move placed earlier in time wins, because everything is ordered by
    /// <c>DeltaSeconds</c> and nothing checks that <c>MoveNumber</c> agrees. So a MSEL whose move 2 starts
    /// before its move 1 reports its early events as being in move 2, and the numbers Gallery is told go
    /// backwards.
    /// </remarks>
    [Fact]
    public async Task GetMovesAndInjects_OrdersByTimeAndNotByMoveNumber()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        await Seed(
            BlueprintAppFactory.Move(msel.Id, moveNumber: 2, deltaSeconds: 0),
            BlueprintAppFactory.Move(msel.Id, moveNumber: 1, deltaSeconds: 100));

        var early = await SeedEvent(msel.Id, 0);
        var late = await SeedEvent(msel.Id, 100);

        var answer = await Service().GetMovesAndInjects(msel.Id, Ct);

        Assert.Equal(2, answer[early.Id][0]);
        Assert.Equal(1, answer[late.Id][0]);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The service over the test's own context, with nothing else supplied.
    /// </summary>
    /// <remarks>
    /// <c>GetMovesAndInjects</c> reads only <c>_context</c>, so the principal, the mapper and the database
    /// options are <c>null</c>. That turns red the moment the method starts using one of them, which is the
    /// point of passing nulls rather than substitutes.
    /// </remarks>
    private ScenarioEventService Service() => new(Db, null, null, null);

    private async Task<ScenarioEventEntity> SeedEvent(Guid mselId, int deltaSeconds)
    {
        var scenarioEvent = BlueprintAppFactory.ScenarioEvent(mselId, deltaSeconds: deltaSeconds);
        await Seed(scenarioEvent);

        return scenarioEvent;
    }
}
