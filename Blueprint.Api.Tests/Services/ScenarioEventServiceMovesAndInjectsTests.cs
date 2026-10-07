// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary><c>ScenarioEventService.GetMovesAndInjects</c> - which move and which inject each scenario
/// event belongs to.</summary>
public class ScenarioEventServiceMovesAndInjectsTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    [Fact]
    public async Task GetMovesAndInjects_WithNoScenarioEvents_IsEmpty()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(TestData.Move(msel.Id, moveNumber: 0, deltaSeconds: 0));

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
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(
            TestData.Move(msel.Id, moveNumber: 0, deltaSeconds: 0),
            TestData.Move(msel.Id, moveNumber: 1, deltaSeconds: 3600));

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
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(TestData.Move(msel.Id, moveNumber: 0, deltaSeconds: 0));

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
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(TestData.Move(msel.Id, moveNumber: 0, deltaSeconds: 0));

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
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(
            TestData.Move(msel.Id, moveNumber: 0, deltaSeconds: 0),
            TestData.Move(msel.Id, moveNumber: 1, deltaSeconds: 100));

        await SeedEvent(msel.Id, 0);
        await SeedEvent(msel.Id, 10);
        var acrossTheBoundary = await SeedEvent(msel.Id, 100);

        var answer = await Service().GetMovesAndInjects(msel.Id, Ct);

        Assert.Equal([1, 0], answer[acrossTheBoundary.Id]);
    }

    /// <summary>The move reported is its own <c>MoveNumber</c>, not its index.</summary>
    [Fact]
    public async Task GetMovesAndInjects_ReturnsTheMovesOwnNumberRatherThanItsIndex()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(
            TestData.Move(msel.Id, moveNumber: 5, deltaSeconds: 0),
            TestData.Move(msel.Id, moveNumber: 6, deltaSeconds: 100));

        var inFirst = await SeedEvent(msel.Id, 0);
        var inSecond = await SeedEvent(msel.Id, 100);

        var answer = await Service().GetMovesAndInjects(msel.Id, Ct);

        Assert.Equal(5, answer[inFirst.Id][0]);
        Assert.Equal(6, answer[inSecond.Id][0]);
    }

    /// <summary>A MSEL with scenario events and no moves throws <c>IndexOutOfRangeException</c>.</summary>
    [Fact]
    public async Task GetMovesAndInjects_WithNoMoves_Throws()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        await SeedEvent(msel.Id, 0);

        await Assert.ThrowsAsync<IndexOutOfRangeException>(() =>
            Service().GetMovesAndInjects(msel.Id, Ct));
    }

    /// <summary>An event earlier than the first move is reported in the first move, at inject 0.</summary>
    [Fact]
    public async Task GetMovesAndInjects_ForAnEventEarlierThanTheFirstMove_PutsItInThatMove()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(TestData.Move(msel.Id, moveNumber: 1, deltaSeconds: 1000));

        var early = await SeedEvent(msel.Id, 0);

        var answer = await Service().GetMovesAndInjects(msel.Id, Ct);

        Assert.Equal([1, 0], answer[early.Id]);
    }

    /// <summary>A move with no events of its own is skipped.</summary>
    [Fact]
    public async Task GetMovesAndInjects_SkipsAMoveWithNoEventsInIt()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(
            TestData.Move(msel.Id, moveNumber: 0, deltaSeconds: 0),
            TestData.Move(msel.Id, moveNumber: 1, deltaSeconds: 100),
            TestData.Move(msel.Id, moveNumber: 2, deltaSeconds: 200));

        var first = await SeedEvent(msel.Id, 0);
        var last = await SeedEvent(msel.Id, 250);

        var answer = await Service().GetMovesAndInjects(msel.Id, Ct);

        Assert.Equal([0, 0], answer[first.Id]);
        Assert.Equal([2, 0], answer[last.Id]);
    }

    /// <summary>Only the MSEL's own events and moves are read, ordered by <c>DeltaSeconds</c>.</summary>
    [Fact]
    public async Task GetMovesAndInjects_ReadsOnlyTheMselsOwnEventsAndMoves()
    {
        var mine = TestData.Msel();
        var theirs = TestData.Msel();
        await Seed(mine, theirs);
        await Seed(
            TestData.Move(mine.Id, moveNumber: 0, deltaSeconds: 0),
            TestData.Move(theirs.Id, moveNumber: 9, deltaSeconds: 0));

        var ofMine = await SeedEvent(mine.Id, 0);
        await SeedEvent(theirs.Id, 0);

        var answer = await Service().GetMovesAndInjects(mine.Id, Ct);

        Assert.Equal(ofMine.Id, Assert.Single(answer).Key);
        Assert.Equal([0, 0], answer[ofMine.Id]);
    }

    /// <summary>Moves are ordered by time, so a later-numbered move placed earlier wins.</summary>
    [Fact]
    public async Task GetMovesAndInjects_OrdersByTimeAndNotByMoveNumber()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(
            TestData.Move(msel.Id, moveNumber: 2, deltaSeconds: 0),
            TestData.Move(msel.Id, moveNumber: 1, deltaSeconds: 100));

        var early = await SeedEvent(msel.Id, 0);
        var late = await SeedEvent(msel.Id, 100);

        var answer = await Service().GetMovesAndInjects(msel.Id, Ct);

        Assert.Equal(2, answer[early.Id][0]);
        Assert.Equal(1, answer[late.Id][0]);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>The service over the test's own context, with <c>null</c> for everything else: the method reads only <c>_context</c>.</summary>
    private ScenarioEventService Service() => new(Db, null, null, null);

    private async Task<ScenarioEventEntity> SeedEvent(Guid mselId, int deltaSeconds)
    {
        var scenarioEvent = TestData.ScenarioEvent(mselId, deltaSeconds: deltaSeconds);
        await Seed(scenarioEvent);

        return scenarioEvent;
    }
}
