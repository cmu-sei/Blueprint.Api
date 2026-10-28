// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary><c>MselAssistRules.Review</c> - what the planner is shown of the model's proposed changes: each one checked
/// against the MSEL as if the earlier ones had been applied, labelled, or left out with the reason.</summary>
public class MselAssistRulesTests
{
    private readonly List<string> _warnings = new();

    [Fact]
    public void Review_AddsATeamWithAnUpperCaseShortNameAndItsNameDefaulted()
    {
        var kept = Review(State(), new MselChange { Op = "team.add", ShortName = " cert " });

        var change = Assert.Single(kept);
        Assert.Equal("CERT", change.ShortName);
        Assert.Equal("CERT", change.Name);
        Assert.Equal("Add team CERT (CERT)", change.Label);
    }

    [Fact]
    public void Review_LeavesOutATeamWhoseShortNameIsTaken()
    {
        var kept = Review(State(), new MselChange { Op = "team.add", ShortName = "blue" }, new MselChange { Op = "team.add", ShortName = "CERT" }, new MselChange { Op = "team.add", ShortName = "cert" });

        Assert.Equal(["CERT"], kept.Select(c => c.ShortName));
        Assert.Contains("Add team BLUE (BLUE): left out, there is already a team BLUE.", _warnings);
        Assert.Contains(_warnings, w => w.Contains("there is already a team CERT"));
    }

    [Fact]
    public void Review_EditsATeamAndFreesItsOldShortName()
    {
        var kept = Review(State(),
            new MselChange { Op = "team.update", Target = "BLUE", ShortName = "LEGAL", Name = "Legal cell" },
            new MselChange { Op = "team.add", ShortName = "BLUE" });

        Assert.Equal(2, kept.Count);
        Assert.Equal("Edit team BLUE: short name LEGAL, name \"Legal cell\"", kept[0].Label);
    }

    [Fact]
    public void Review_LeavesOutAnEditOfATeamThatIsNotThere()
    {
        var kept = Review(State(), new MselChange { Op = "team.update", Target = "RED", Name = "Red cell" });

        Assert.Empty(kept);
        Assert.Contains(_warnings, w => w.Contains("there is no team RED"));
    }

    [Fact]
    public void Review_KeepsTheShortNameEmptyWhenAnEditDoesNotChangeIt()
    {
        var change = Assert.Single(Review(State(), new MselChange { Op = "team.update", Target = "blue", ShortName = "BLUE", Description = "Defenders" }));

        Assert.Null(change.ShortName);
        Assert.Equal("Edit team blue: description", change.Label);
    }

    [Fact]
    public void Review_AddsAnOrganizationAndRefusesItsDuplicate()
    {
        var kept = Review(State(),
            new MselChange { Op = "organization.add", ShortName = "cisa", Name = "Cyber Agency", Summary = "National CERT" },
            new MselChange { Op = "organization.add", ShortName = "NEWS" });

        Assert.Equal("Add organization CISA (Cyber Agency)", Assert.Single(kept).Label);
        Assert.Contains(_warnings, w => w.Contains("there is already an organization NEWS"));
    }

    [Fact]
    public void Review_AddsACardAtMoveZeroWhenNoneIsGiven()
    {
        var change = Assert.Single(Review(State(), new MselChange { Op = "card.add", Name = "Patch Status", Inject = -2 }));

        Assert.Equal(0, change.Move);
        Assert.Equal(0, change.Inject);
        Assert.Equal("Add Gallery card Patch Status (move 0, inject 0)", change.Label);
    }

    [Fact]
    public void Review_RenamesACardButNotOntoAnother()
    {
        var kept = Review(State(),
            new MselChange { Op = "card.update", Target = "Situation", Name = "Wall", Move = 2 },
            new MselChange { Op = "card.update", Target = "Wall", Name = "Situation" });

        Assert.Equal(2, kept.Count);
        Assert.Equal("Edit Gallery card Situation: name \"Wall\", move 2", kept[0].Label);
        Assert.Empty(_warnings);
    }

    [Fact]
    public void Review_AddsACompetencyToATeamAddedEarlierInTheSameProposal()
    {
        var kept = Review(State(),
            new MselChange { Op = "team.add", ShortName = "CERT" },
            new MselChange { Op = "competency.add", Target = "CERT", IdNumber = "cs-511" });

        Assert.Equal(2, kept.Count);
        Assert.Equal("CS-511", kept[1].IdNumber);
        Assert.Equal("Add CS-511 Cyber Defense Analyst to team CERT", kept[1].Label);
    }

    [Fact]
    public void Review_LeavesOutACompetencyNotInAnyFrameworkOrOnATeamThatIsNotThere()
    {
        var kept = Review(State(),
            new MselChange { Op = "competency.add", IdNumber = "ZZ-999" },
            new MselChange { Op = "competency.add", Target = "RED", IdNumber = "CS-511" });

        Assert.Empty(kept);
        Assert.Contains(_warnings, w => w.Contains("there is no competency ZZ-999"));
        Assert.Contains(_warnings, w => w.Contains("there is no team RED"));
    }

    [Fact]
    public void Review_AddsAndRemovesCompetencyLinksOnlyWhereTheyMakeSense()
    {
        var kept = Review(State(),
            new MselChange { Op = "competency.add", IdNumber = "CS-511" },
            new MselChange { Op = "competency.add", Target = "BLUE", IdNumber = "T-0001" },
            new MselChange { Op = "competency.remove", Target = "BLUE", IdNumber = "T-0001" },
            new MselChange { Op = "competency.remove", Target = "BLUE", IdNumber = "CS-511" },
            new MselChange { Op = "competency.remove", IdNumber = "CS-511" });

        Assert.Equal(["Remove T-0001 Triage incidents from team BLUE", "Remove CS-511 Cyber Defense Analyst from the MSEL"], kept.Select(c => c.Label));
        Assert.Contains(_warnings, w => w.Contains("the MSEL already has CS-511"));
        Assert.Contains(_warnings, w => w.Contains("team BLUE already has T-0001"));
        Assert.Contains(_warnings, w => w.Contains("team BLUE does not have CS-511"));
    }

    [Fact]
    public void Review_LeavesOutAChangeItCannotMake()
    {
        var kept = Review(State(), new MselChange { Op = "inject.delete" }, new MselChange());

        Assert.Empty(kept);
        Assert.Contains(_warnings, w => w.Contains("inject.delete is not a change the assistant can make"));
        Assert.Contains(_warnings, w => w.Contains("an empty change is not a change the assistant can make"));
    }

    /// <summary>A move's scenario time is kept in UTC and named in the label; one that is not a time leaves the change out.</summary>
    [Fact]
    public void Review_ReadsAMovesScenarioTimeAsUtc()
    {
        var state = State();
        state.Msel.Moves.Add(new MoveEntity { Id = Guid.NewGuid(), MselId = state.Msel.Id, MoveNumber = 1, Description = "Detection" });

        var kept = Review(state,
            new MselChange { Op = "move.update", Target = "1", SituationTime = "2026-03-05T04:00:00-05:00" },
            new MselChange { Op = "move.add", Move = 2, Description = "Recovery", SituationTime = "next week" });

        var change = Assert.Single(kept);
        Assert.Equal("2026-03-05T09:00:00Z", change.SituationTime);
        Assert.Equal("Edit move 1: scenario time 2026-03-05T09:00:00Z", change.Label);
        Assert.Contains(_warnings, w => w.Contains("left out, \"next week\" is not a date and time"));
    }

    private List<MselChange> Review(MselAssistState state, params MselChange[] changes) =>
        MselAssistRules.Review(state, changes, _warnings);

    // BLUE (with task T-0001), the NEWS organization, a Situation card, and work role CS-511 on the MSEL
    private static MselAssistState State()
    {
        var frameworkId = Guid.NewGuid();
        var role = new CompetencyEntity { Id = Guid.NewGuid(), CompetencyFrameworkId = frameworkId, IdNumber = "CS-511", ShortName = "Cyber Defense Analyst" };
        var task = new CompetencyEntity { Id = Guid.NewGuid(), CompetencyFrameworkId = frameworkId, IdNumber = "T-0001", ShortName = "Triage incidents" };
        var msel = new MselEntity { Id = Guid.NewGuid(), Name = "Open Gate" };
        var blue = new TeamEntity { Id = Guid.NewGuid(), MselId = msel.Id, ShortName = "BLUE", Name = "Blue team" };
        blue.TeamCompetencies.Add(new TeamCompetencyEntity { TeamId = blue.Id, CompetencyId = task.Id, Competency = task });
        msel.Teams.Add(blue);
        msel.Organizations.Add(new OrganizationEntity { Id = Guid.NewGuid(), MselId = msel.Id, ShortName = "NEWS", Name = "Daily News" });
        msel.Cards.Add(new CardEntity { Id = Guid.NewGuid(), MselId = msel.Id, Name = "Situation" });
        msel.MselCompetencies.Add(new MselCompetencyEntity { MselId = msel.Id, CompetencyId = role.Id, Competency = role });

        var state = new MselAssistState(msel);
        state.Competencies["CS-511"] = role;
        state.Competencies["T-0001"] = task;
        return state;
    }
}
