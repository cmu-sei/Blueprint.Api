// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary>A drafted MSEL's play between a pre-exercise move, before STARTEX, and a post-exercise hot wash, after ENDEX.</summary>
public class MselDraftMovesTests
{
    private static MselDraft Draft(int? lead = null) => new()
    {
        DurationMinutes = 120,
        Design = new MselDraftDesign { LeadMinutes = lead },
        Moves =
        [
            new() { MoveNumber = 0, Description = "Outage", StartMinutes = 0, SituationTime = "2026-03-04T08:00:00Z" },
            new() { MoveNumber = 1, Description = "Pressure", StartMinutes = 60, SituationTime = "2026-03-05T09:00:00Z" }
        ],
        Injects =
        [
            new() { Minutes = 0, EventType = "Information", Values = new Dictionary<string, string> { ["Title"] = "Read the read-in" } },
            new() { Minutes = 10, EventType = "Inject", Values = new Dictionary<string, string> { ["Title"] = "Cables cut" } }
        ],
        CiteActions = [new() { MoveNumber = 1, Team = "SOC", Description = "Brief leadership" }]
    };

    [Fact]
    public void AddPreAndPostMoves_PutsPlayBetweenThePreAndPostExercise()
    {
        var draft = Draft();

        MselDraftRules.AddPreAndPostMoves(draft);

        Assert.Equal([(0, "Pre-exercise", 0), (1, "Outage", 15), (2, "Pressure", 75), (3, "Post-exercise: hot wash", 135)],
            draft.Moves.Select(m => (m.MoveNumber, m.Description, m.StartMinutes)));
        Assert.Equal([MselDraftRules.PreExercise, null, null, MselDraftRules.PostExercise], draft.Moves.Select(m => m.Phase));
        // the read-in notice is there when the doors open; play moves with its moves
        Assert.Equal([0, 25], draft.Injects.Select(i => i.Minutes));
        Assert.Equal(2, draft.CiteActions.Single().MoveNumber);
        // the doors-open lead, the 120 minutes of play and the 30-minute hot wash
        Assert.Equal(165, draft.DurationMinutes);
    }

    [Fact]
    public void AddPreAndPostMoves_TakesThePlannersLeadTime_AndRunsOnce()
    {
        var draft = Draft(lead: 0);

        MselDraftRules.AddPreAndPostMoves(draft);
        MselDraftRules.AddPreAndPostMoves(draft);

        Assert.Equal([0, 0, 60, 120], draft.Moves.Select(m => m.StartMinutes));
        Assert.Equal(4, draft.Moves.Count);
    }
}
