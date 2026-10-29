// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Blueprint.Api.Services;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary><c>MselDraftRules.ReadDraft</c> - reading the model's draft without losing it to one field it wrote wrongly.</summary>
public class MselDraftReadTests
{
    /// <summary>Each field that cannot be read is left out and named, and the rest of the draft is kept.</summary>
    [Fact]
    public void ReadDraft_LeavesOutWhatItCannotReadAndKeepsTheRest()
    {
        var warnings = new List<string>();
        var input = JsonDocument.Parse("""
            {"name":"Deep Bite","durationMinutes":{"minutes":120},"scenario":"a paragraph",
             "moves":[{"moveNumber":0,"description":"Start"},{"moveNumber":"one","description":"Next"}],
             "injects":[{"minutes":5,"values":{"Title":"Outage"}}]}
            """).RootElement;

        var draft = MselDraftRules.ReadDraft(input, warnings);

        Assert.Equal("Deep Bite", draft.Name);
        Assert.Equal(0, draft.DurationMinutes);
        Assert.Equal(["Start", "Next"], draft.Moves.Select(m => m.Description));
        Assert.Equal("Outage", draft.Injects.Single().Values["Title"]);
        Assert.Equal(3, warnings.Count);
        Assert.Contains(warnings, w => w.StartsWith("Left out the model's durationMinutes,"));
        Assert.Contains(warnings, w => w.StartsWith("Left out the model's scenario,"));
        Assert.Contains(warnings, w => w.StartsWith("Left out the model's moves[1].moveNumber,"));
    }

    /// <summary>A readable draft is read as it is, with no warnings.</summary>
    [Fact]
    public void ReadDraft_OfAReadableDraft_WarnsOfNothing()
    {
        var warnings = new List<string>();

        var draft = MselDraftRules.ReadDraft(JsonDocument.Parse("""{"name":"Deep Bite","moves":[]}""").RootElement, warnings);

        Assert.Equal("Deep Bite", draft.Name);
        Assert.Empty(warnings);
    }
}
