// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary>
/// <c>IntegrationService.GetMovesAndGroups</c> - the move and group of each scenario event, which the push uses to
/// build the Steamfitter tasks, including each move's CITE move change.
/// </summary>
public class IntegrationServiceMovesAndGroupsTests
{
    private static readonly Guid MselId = Guid.NewGuid();

    [Fact]
    public void GetMovesAndGroups_WithNoScenarioEvents_IsEmpty()
    {
        Assert.Empty(IntegrationService.GetMovesAndGroups([], Moves((0, 0))));
    }

    [Fact]
    public void GetMovesAndGroups_GroupsEventsByTimeWithinAMove()
    {
        var events = Events(0, 0, 60, 3600);

        var answer = IntegrationService.GetMovesAndGroups(events, Moves((0, 0), (1, 3600)));

        Assert.Equal([0, 0], answer[events[0].Id]);
        Assert.Equal([0, 0], answer[events[1].Id]);
        Assert.Equal([0, 1], answer[events[2].Id]);
        Assert.Equal([1, 0], answer[events[3].Id]);
    }

    /// <summary>
    /// Two moves sharing a start time: every event at that time is in the higher-numbered move. Advancing one move
    /// per event put the first event in Move 0 and the rest in Move 1.
    /// </summary>
    [Fact]
    public void GetMovesAndGroups_ForMovesSharingAStartTime_PutsEveryEventAtItInTheHigherNumberedMove()
    {
        var events = Events(0, 0, 60);

        var answer = IntegrationService.GetMovesAndGroups(events, Moves((0, 0), (1, 0)));

        Assert.All(events, e => Assert.Equal(1, answer[e.Id][0]));
        Assert.Equal([0, 0, 1], events.Select(e => answer[e.Id][1]));
    }

    /// <summary>An event past a move with no events of its own is in its own move, not the empty one.</summary>
    [Fact]
    public void GetMovesAndGroups_SkipsAMoveWithNoEventsInIt()
    {
        var events = Events(0, 250);

        var answer = IntegrationService.GetMovesAndGroups(events, Moves((0, 0), (1, 100), (2, 200)));

        Assert.Equal([0, 0], answer[events[0].Id]);
        Assert.Equal([2, 0], answer[events[1].Id]);
    }

    /// <summary>
    /// The move is its number, not its place in the list: CITE's move change targets the move's number, so a
    /// MSEL numbering its moves from 1 changed CITE to the wrong move.
    /// </summary>
    [Fact]
    public void GetMovesAndGroups_ReturnsTheMovesOwnNumberRatherThanItsIndex()
    {
        var events = Events(0, 100);

        var answer = IntegrationService.GetMovesAndGroups(events, Moves((1, 0), (2, 100)));

        Assert.Equal(1, answer[events[0].Id][0]);
        Assert.Equal(2, answer[events[1].Id][0]);
    }

    [Fact]
    public void GetMovesAndGroups_ForAnEventBeforeTheFirstMove_IsMoveMinusOne()
    {
        var events = Events(0, 100);

        var answer = IntegrationService.GetMovesAndGroups(events, Moves((1, 100)));

        Assert.Equal(-1, answer[events[0].Id][0]);
        Assert.Equal(1, answer[events[1].Id][0]);
    }

    [Fact]
    public void GetMovesAndGroups_WithNoMoves_PutsEveryEventInMoveZero()
    {
        var events = Events(0, 60);

        var answer = IntegrationService.GetMovesAndGroups(events, []);

        Assert.Equal([0, 0], answer[events[0].Id]);
        Assert.Equal([0, 1], answer[events[1].Id]);
    }

    private static List<ScenarioEventEntity> Events(params int[] deltaSeconds) =>
        deltaSeconds.Select(d => TestData.ScenarioEvent(MselId, deltaSeconds: d)).ToList();

    // as the push passes them: ordered by number
    private static List<MoveEntity> Moves(params (int number, int deltaSeconds)[] moves) =>
        moves.Select(m => TestData.Move(MselId, moveNumber: m.number, deltaSeconds: m.deltaSeconds))
            .OrderBy(m => m.MoveNumber).ToList();
}
