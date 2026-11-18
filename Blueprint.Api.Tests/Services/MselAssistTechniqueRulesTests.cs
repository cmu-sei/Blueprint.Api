// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary><c>MselAssistRules.Review</c> on techniques: adding one to the MSEL's techniques from the right release,
/// keeping a technique column to the ids the MSEL has, and turning a column of technique names into one of ids.</summary>
public class MselAssistTechniqueRulesTests
{
    private readonly List<string> _warnings = new();

    // Enterprise 17.1 (T1059, T1003) and 18.0 (T1059, T1566), and a defensive release with D3-AL; the MSEL pools nothing
    private readonly TechniqueReleaseEntity _old = Release("enterprise-attack", "Enterprise ATT&CK", "17.1", new DateTime(2025, 4, 22), false, ("T1059", "Command and Scripting Interpreter"), ("T1003", "OS Credential Dumping"));
    private readonly TechniqueReleaseEntity _new = Release("enterprise-attack", "Enterprise ATT&CK", "18.0", new DateTime(2025, 10, 28), false, ("T1059", "Command and Scripting Interpreter"), ("T1566", "Phishing"));
    private readonly TechniqueReleaseEntity _defend = Release("d3fend", "MITRE D3FEND", "1.6.0", new DateTime(2026, 8, 31), true, ("D3-AL", "Application Hardening"));

    [Fact]
    public void Review_AddsATechniqueFromTheNewestReleaseOfItsDomain()
    {
        var change = Assert.Single(Review(State(), new MselChange { Op = "technique.add", Technique = "t1059", Domain = "enterprise-attack" }));

        Assert.Equal("Add technique T1059 (Command and Scripting Interpreter) from Enterprise ATT&CK 18.0", change.Label);
        Assert.Equal(Technique(_new, "T1059").Id, change.TechniqueId);
        Assert.Equal("T1059", change.Technique);
    }

    [Fact]
    public void Review_AddsFromTheReleaseTheMselAlreadyTakesTheDomainFrom()
    {
        var change = Assert.Single(Review(State(pooled: Technique(_old, "T1003")), new MselChange { Op = "technique.add", Technique = "T1059", Domain = "enterprise-attack" }));

        Assert.Equal(Technique(_old, "T1059").Id, change.TechniqueId);
        Assert.EndsWith("from Enterprise ATT&CK 17.1", change.Label);
    }

    [Fact]
    public void Review_LeavesOutATechniqueThePinnedReleaseLacks()
    {
        var kept = Review(State(pooled: Technique(_old, "T1003")), new MselChange { Op = "technique.add", Technique = "T1566", Domain = "enterprise-attack" });

        Assert.Empty(kept);
        Assert.Contains("Add technique T1566: left out, T1566 is not in Enterprise ATT&CK 17.1, the release this MSEL's enterprise-attack techniques come from.", _warnings);
    }

    [Fact]
    public void Review_LeavesOutAnUnknownTechniqueAndOneAlreadyPooled()
    {
        var kept = Review(State(pooled: Technique(_old, "T1003")),
            new MselChange { Op = "technique.add", Technique = "T9999", Domain = "enterprise-attack" },
            new MselChange { Op = "technique.add", Technique = "T1059", Domain = "nowhere" },
            new MselChange { Op = "technique.add", Technique = "T1003", Domain = "enterprise-attack" },
            new MselChange { Op = "technique.add", Technique = "T1059", Domain = "enterprise-attack" },
            new MselChange { Op = "technique.add", Technique = "T1059", Domain = "enterprise-attack" });

        Assert.Equal(["T1059"], kept.Select(c => c.Technique));
        Assert.Contains(_warnings, w => w.Contains("Blueprint has no technique T9999 in enterprise-attack"));
        Assert.Contains(_warnings, w => w.Contains("Blueprint has no nowhere technique release"));
        Assert.Contains(_warnings, w => w.Contains("T1003 is already in the MSEL's techniques"));
        Assert.Contains(_warnings, w => w.Contains("T1059 is already in the MSEL's techniques"));
    }

    [Fact]
    public void Review_LeavesOutATechniqueAddThePlannerMayNotMake()
    {
        var kept = Review(State(), MselAssistAccess.All with { Techniques = false }, new MselChange { Op = "technique.add", Technique = "T1059", Domain = "enterprise-attack" });

        Assert.Empty(kept);
        Assert.Contains(_warnings, w => w.EndsWith("left out, only the MSEL's owner can change its techniques."));
    }

    [Fact]
    public void Review_KeepsATechniqueColumnToTheIdsTheMselHas()
    {
        var state = State(pooled: Technique(_old, "T1003"), techniqueColumn: true);

        var kept = Review(state,
            new MselChange { Op = "technique.add", Technique = "T1059", Domain = "enterprise-attack" },
            new MselChange { Op = "inject.update", Target = "E1", Values = new() { ["Techniques"] = "T1059 Command and Scripting Interpreter; t1003; T9999 Made up; D3-AL" } });

        Assert.Equal("T1059, T1003", kept[1].Values["Techniques"]);
        Assert.Contains("Left out T9999, D3-AL from Techniques on E1: not in the MSEL's attack techniques.", _warnings);
    }

    [Fact]
    public void Review_TurnsAColumnOfTechniqueNamesIntoIds()
    {
        var state = State(pooled: Technique(_old, "T1003"));

        var change = Assert.Single(Review(state, new MselChange { Op = "column.update", Target = "Mapped", DataType = "AttackTechnique" }));

        Assert.Equal("Edit column Mapped: type AttackTechnique, reading the technique ids on 1 injects: 2 techniques, 1 of them added to the MSEL's techniques; not found, so dropped: T9999, D3-AL", change.Label);
        Assert.Equal("T1059, T1003", change.Conversion.Values[state.Injects[0].Inject.Id]);
        Assert.Null(change.Conversion.Values[state.Injects[1].Inject.Id]);
        Assert.Equal([Technique(_old, "T1059").Id], change.Conversion.TechniquesToAdd);
    }

    [Fact]
    public void Review_TurnsAColumnIntoTechniqueIdsOnlyWhenThePlannerMayAddTechniques()
    {
        var kept = Review(State(), MselAssistAccess.All with { Techniques = false }, new MselChange { Op = "column.update", Target = "Mapped", DataType = "AttackTechnique" });

        Assert.Empty(kept);
        Assert.Contains(_warnings, w => w.Contains("adds techniques to the MSEL, which only its owner can do"));
    }

    [Theory]
    [InlineData("T1059, T1003", new[] { "T1059", "T1003" })]
    [InlineData("T1136.002 Create Account: Domain Account; T1078.002 Valid Accounts: Domain Accounts", new[] { "T1136.002", "T1078.002" })]
    [InlineData("D3-AL (Application Hardening)\nD3-AL", new[] { "D3-AL" })]
    [InlineData("  ", new string[0])]
    public void TechniqueIds_ReadsTheIdAtTheStartOfEachItem(string value, string[] ids)
    {
        Assert.Equal(ids, MselAssistRules.TechniqueIds(value));
    }

    private List<MselChange> Review(MselAssistState state, params MselChange[] changes) => Review(state, MselAssistAccess.All, changes);

    // the techniques the changes name are looked up first, as the service does
    private List<MselChange> Review(MselAssistState state, MselAssistAccess access, params MselChange[] changes)
    {
        state.Techniques.AddRange(MselAssistRules.TechniqueIdsNamed(state, changes).Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(id => new[] { _old, _new, _defend }.SelectMany(r => r.Techniques).Where(t => t.ExternalId.Equals(id, StringComparison.OrdinalIgnoreCase))));
        return MselAssistRules.Review(state, changes, access, _warnings);
    }

    // a MSEL with a text column Mapped naming techniques on E1 and E2, and when asked a technique column on E1
    private MselAssistState State(TechniqueEntity pooled = null, bool techniqueColumn = false)
    {
        var msel = new MselEntity { Id = Guid.NewGuid(), Name = "Open Gate" };
        var mapped = new DataFieldEntity { Id = Guid.NewGuid(), MselId = msel.Id, Name = "Mapped", DataType = DataFieldType.String, DisplayOrder = 1 };
        msel.DataFields.Add(mapped);
        var first = new ScenarioEventEntity { Id = Guid.NewGuid(), MselId = msel.Id, DeltaSeconds = 600 };
        first.DataValues.Add(new DataValueEntity { Id = Guid.NewGuid(), DataFieldId = mapped.Id, ScenarioEventId = first.Id, Value = "T1059 Command and Scripting Interpreter; T1003 OS Credential Dumping; T9999 Made up" });
        var second = new ScenarioEventEntity { Id = Guid.NewGuid(), MselId = msel.Id, DeltaSeconds = 1200 };
        second.DataValues.Add(new DataValueEntity { Id = Guid.NewGuid(), DataFieldId = mapped.Id, ScenarioEventId = second.Id, Value = "D3-AL Application Hardening" });
        msel.ScenarioEvents.Add(first);
        msel.ScenarioEvents.Add(second);
        if (techniqueColumn)
            msel.DataFields.Add(new DataFieldEntity { Id = Guid.NewGuid(), MselId = msel.Id, Name = "Techniques", DataType = DataFieldType.AttackTechnique, DisplayOrder = 2 });
        if (pooled != null)
            msel.MselTechniques.Add(new MselTechniqueEntity(msel.Id, pooled.Id) { Id = Guid.NewGuid(), Technique = pooled });
        return new MselAssistState(msel) { Releases = [_old, _new, _defend] };
    }

    private static TechniqueEntity Technique(TechniqueReleaseEntity release, string externalId) =>
        release.Techniques.Single(t => t.ExternalId == externalId);

    private static TechniqueReleaseEntity Release(string domain, string name, string version, DateTime released, bool defensive, params (string Id, string Name)[] techniques)
    {
        var release = new TechniqueReleaseEntity { Id = Guid.NewGuid(), Domain = domain, Name = name, Version = version, ReleaseDate = released, IsDefensive = defensive };
        foreach (var (id, techniqueName) in techniques)
            release.Techniques.Add(new TechniqueEntity { Id = Guid.NewGuid(), TechniqueReleaseId = release.Id, TechniqueRelease = release, ExternalId = id, Name = techniqueName });
        return release;
    }
}
