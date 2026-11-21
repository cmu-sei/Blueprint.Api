// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Blueprint.Api.Automation;
using Xunit;

namespace Blueprint.Api.Tests.Automation;

/// <summary>The parameters one result runs with, and the VMs a mask matches.</summary>
public class AutomationParameterBinderTests
{
    private static readonly AutomationSubstitutions Subs = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        null, 3, 1);

    /// <summary>A task never names its VM: whatever it stored, the result's VM is used.</summary>
    [Fact]
    public void Bind_SetsTheVmToTheResultsVm_WhateverTheTaskStored()
    {
        var vm = Guid.NewGuid();

        var bound = AutomationParameterBinder.Bind(
            new Dictionary<string, string> { ["Moid"] = Guid.NewGuid().ToString(), ["vmname"] = "other" }, null, Subs, vm, "web-01");

        Assert.Equal(vm.ToString(), bound["Moid"]);
        Assert.Equal("web-01", bound["VmName"]);
    }

    [Fact]
    public void Bind_ReplacesTheMarkers_InParametersAndSecrets()
    {
        var bound = AutomationParameterBinder.Bind(
            new Dictionary<string, string> { ["Url"] = "http://cite.example/api/evaluations/{citeEvaluationId}/move/{moveNumber}?view={PlayerViewId}&vm={VmName}" },
            new Dictionary<string, string> { ["Headers"] = "{\"X-Msel\":\"{mselId}\"}" },
            Subs, null, null);

        Assert.Equal("http://cite.example/api/evaluations/33333333-3333-3333-3333-333333333333/move/3?view=22222222-2222-2222-2222-222222222222&vm=", bound["Url"]);
        Assert.Equal("{\"X-Msel\":\"11111111-1111-1111-1111-111111111111\"}", bound["Headers"]);
    }

    [Fact]
    public void Bind_LeavesAMarkerItDoesNotKnow_AndAnIdTheMselLacksIsEmpty()
    {
        var bound = AutomationParameterBinder.Bind(
            new Dictionary<string, string> { ["Body"] = "{galleryExhibitId}|{group}|{groupNumber}" }, null, Subs, null, null);

        Assert.Equal("|{group}|1", bound["Body"]);
    }

    [Fact]
    public void Describe_RedactsEverySecret()
    {
        var shown = JsonSerializer.Deserialize<Dictionary<string, string>>(AutomationParameterBinder.Describe(
            new Dictionary<string, string> { ["Username"] = "admin", ["Password"] = "hunter2", ["Headers"] = "" },
            new HashSet<string>(["Password", "Headers"], StringComparer.OrdinalIgnoreCase)));

        Assert.Equal("admin", shown["Username"]);
        Assert.Equal(AutomationParameterBinder.Redacted, shown["Password"]);
        Assert.Equal("", shown["Headers"]);
    }

    private static readonly AutomationVm Web1 = new(Guid.NewGuid(), "web-01");
    private static readonly AutomationVm Web2 = new(Guid.NewGuid(), "WEB-02");
    private static readonly AutomationVm Db1 = new(Guid.NewGuid(), "db-01");

    [Fact]
    public void Match_OfAName_MatchesTheVmsWhoseNamesContainIt_IgnoringCase()
    {
        Assert.Equal([Web1, Web2], AutomationVmResolver.Match([Db1, Web2, Web1], "web"));
    }

    /// <summary>A mask with a comma is a list of ids, as Steamfitter read it; names are matched as one string.</summary>
    [Fact]
    public void Match_OfIds_MatchesThoseOfTheViewsVms()
    {
        var notInView = Guid.NewGuid();

        Assert.Equal([Db1, Web1], AutomationVmResolver.Match([Web1, Web2, Db1], $"{Web1.Id}, {Db1.Id},{notInView}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Match_OfNoMask_MatchesNothing(string mask)
    {
        Assert.Empty(AutomationVmResolver.Match([Web1], mask));
    }
}
