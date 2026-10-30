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

/// <summary>The VM app, and a map on it, only where the MSEL's teams have machines: Caster builds them and play is hands-on.</summary>
public class MselDraftPlayerAppTests
{
    [Theory]
    [InlineData(false, "Functional", false)]
    [InlineData(true, "Discussion", false)]
    [InlineData(true, "Functional", true)]
    [InlineData(true, "Hybrid", true)]
    public void Finish_KeepsTheVmAppOnlyWhereTeamsHaveMachines(bool useCaster, string format, bool kept)
    {
        var msel = new MselEntity
        {
            Id = Guid.NewGuid(),
            Name = "Deep Bite",
            UsePlayer = true,
            UseCaster = useCaster,
            Teams = [new TeamEntity { Id = Guid.NewGuid(), Name = "Security operations", ShortName = "SOC" }],
            PlayerApplications =
            [
                new PlayerApplicationEntity { Id = Guid.NewGuid(), Name = "Virtual Machines", Url = "{vmUrl}/views/{playerViewId}{theme}" },
                new PlayerApplicationEntity { Id = Guid.NewGuid(), Name = "CITE", Url = "{citeUrl}?evaluation={citeEvaluationId}{theme}" }
            ]
        };
        var draft = new MselDraft { Name = "Deep Bite", Design = new MselDraftDesign { Format = format } };

        MselDraftBuilder.Finish(msel, draft, null);

        Assert.Equal(kept, msel.PlayerApplications.Any(a => a.Name == "Virtual Machines"));
        Assert.Contains(msel.PlayerApplications, a => a.Name == "CITE");
    }
}
