// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary>The critical infrastructure sectors a scenario touches, and each organization's sector, size, industry and country.</summary>
public class MselDraftSectorTests
{
    [Fact]
    public void PickSectors_KeepsCisasNamesOnce_InTheOrderGiven()
    {
        Assert.Equal(["Energy", "Water and Wastewater Systems"], MselDraftRules.PickSectors(["energy", "Water and Wastewater Systems", "Bogus", "ENERGY", null]));
        Assert.Equal(16, MselDraftRules.Sectors.Length);
    }

    /// <summary>The planner's sectors come first, the model's unknown ones are dropped with a warning, and each organization's facts are checked.</summary>
    [Fact]
    public void Normalize_SettlesTheSectorsAndTheOrganizationFacts()
    {
        var template = new MselEntity { Id = System.Guid.NewGuid(), Teams = [], Organizations = [], DataFields = [], Cards = [] };
        var draft = new MselDraft
        {
            Name = "Deep Bite",
            Design = new MselDraftDesign { Sectors = ["Water and Wastewater Systems"] },
            Scenario = new MselDraftScenario { Sectors = ["Energy", "Retail"], PrimarySector = "Energy" },
            Moves = [new() { MoveNumber = 0, Description = "Start", StartMinutes = 0 }],
            Organizations =
            [
                new() { Name = "Harbour Power", ShortName = "HPL", Description = "The utility", Sector = "energy", Size = "large", Industry = " electric utility ", Country = "Island state" },
                new() { Name = "Harbour Herald", ShortName = "NEWS", Description = "The paper", Sector = "None", Size = "huge" }
            ]
        };
        var warnings = new List<string>();

        MselDraftRules.Normalize(draft, template, 60, new Dictionary<string, CompetencyEntity>(), warnings, designOnly: true);

        Assert.Equal(["Water and Wastewater Systems", "Energy"], draft.Scenario.Sectors);
        Assert.Equal("Energy", draft.Scenario.PrimarySector);
        Assert.Contains(warnings, w => w == "Dropped sectors that are not CISA critical infrastructure sectors: Retail.");
        var utility = draft.Organizations.Single(o => o.ShortName == "HPL");
        Assert.Equal(("Energy", "Large", "electric utility", "Island state"), (utility.Sector, utility.Size, utility.Industry, utility.Country));
        Assert.Equal("Energy, Large, electric utility, Island state", MselDraftRules.OrganizationFacts(utility));
        var paper = draft.Organizations.Single(o => o.ShortName == "NEWS");
        Assert.Null(paper.Sector);
        Assert.Null(paper.Size);
        Assert.Equal("", MselDraftRules.OrganizationFacts(paper));
    }

    /// <summary>The MSEL's organization carries the facts on its summary line, and the pages name the sectors.</summary>
    [Fact]
    public void PrepareShell_AndPages_CarryTheSectorsAndFacts()
    {
        var msel = new MselEntity { Id = System.Guid.NewGuid(), Name = "Deep Bite", Teams = [], Organizations = [], DataFields = [], Cards = [], Moves = [], MselCompetencies = [] };
        var draft = new MselDraft
        {
            Name = "Deep Bite",
            Scenario = new MselDraftScenario { Problem = "Cut cables.", Premise = "An anchor.", Sectors = ["Communications", "Energy"] },
            Moves = [new() { MoveNumber = 0, Description = "Start", StartMinutes = 0, Purpose = "Choose a lead" }],
            Organizations = [new() { Name = "Harbour Power", ShortName = "HPL", Description = "<p>The island's utility.</p>", Sector = "Energy", Size = "Large", Industry = "electric utility", Country = "Island state" }]
        };

        MselDraftBuilder.PrepareShell(msel, draft, new Dictionary<string, CompetencyEntity>());

        Assert.Equal("The island's utility. (Energy, Large, electric utility, Island state)", msel.Organizations.Single().Summary);
        Assert.Contains("<b>Critical infrastructure sectors:</b> Communications, Energy", MselDraftPages.Overview(draft, msel, System.DateTime.UtcNow));
        Assert.Contains("<b>Critical infrastructure sectors:</b> Communications, Energy", MselDraftPages.FacilitatorGuide(draft, msel, System.DateTime.UtcNow));
    }
}
