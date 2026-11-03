// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary>
/// <c>AudioDraftService</c>'s pure parts - what an article reads aloud as, which voice an outlet gets, which picture a reel uses,
/// and how the model's script becomes lines to voice.
/// </summary>
public class AudioDraftScriptTests
{
    private static readonly List<AiVoice> Voices =
    [
        new("Matthew", "Male", "US English", true),
        new("Joanna", "Female", "US English", true),
        new("Gregory", "Male", "US English"),
        new("Ruth", "Female", "US English")
    ];

    private static readonly AudioDraftService.Inject Article = new("Pumps stop", "WPN News", "News", null, null, new Dictionary<Guid, string>());

    /// <summary>The headline, then the paragraphs; the marking, small-print captions, transcripts and media are not read.</summary>
    [Fact]
    public void ReadAloud_ReadsTheHeadlineAndParagraphs()
    {
        var html = "<table title=\"Marking\"><tr><td>EXERCISE · EXERCISE</td></tr></table>" +
            "<table><tr><td><font size=\"6\"><b>WPN</b></font></td></tr><tr><td><h1>Pumps stop</h1>" +
            "<img src=\"data:image/jpeg;base64,AAAA\" alt=\"A pump\"><br><font size=\"1\">Crews at the station. WPN photo.</font>" +
            "<p>Two stations went offline at six</p><p>Crews &amp; officials are on site.</p><ul><li>Fuel is late</li></ul></td></tr></table>" +
            "<audio controls src=\"data:audio/mpeg;base64,AAAA\"></audio><details><summary>Transcript</summary><p>Old words.</p></details>";

        Assert.Equal("Pumps stop. Two stations went offline at six. Crews & officials are on site. Fuel is late.",
            AudioDraftService.ReadAloud("Pumps stop", html));
    }

    /// <summary>An article laid out in table cells with no paragraphs is read whole, with a pause between cells.</summary>
    [Fact]
    public void ReadAloud_OfAnArticleWithoutParagraphs_ReadsItWhole()
    {
        var html = "<table><tr><td>INTSUM 042</td></tr><tr><td><font>The actor used a stolen VPN account. Confidence is moderate</font></td></tr></table>";

        Assert.Equal("Relay compromise. INTSUM 042 The actor used a stolen VPN account. Confidence is moderate.",
            AudioDraftService.ReadAloud("Relay compromise", html));
        Assert.Equal("", AudioDraftService.ReadAloud(null, null));
    }

    /// <summary>The same outlet always gets the same voice, whatever its case or spacing, and every voice is used by some outlet.</summary>
    [Fact]
    public void PickVoice_IsTheSameForTheSameOutlet()
    {
        var voice = AudioDraftService.PickVoice(Voices, "WPN News");

        Assert.Same(voice, AudioDraftService.PickVoice(Voices, " wpn news "));
        Assert.Equal(Voices.Count, Enumerable.Range(0, 200).Select(i => AudioDraftService.PickVoice(Voices, $"Outlet {i}").Name).Distinct().Count());
    }

    /// <summary>A reel uses the inject's largest photo; an SVG graphic is not a photo.</summary>
    [Fact]
    public void ArticlePhoto_IsTheLargestPhotoInTheValues()
    {
        var values = new[]
        {
            "<p>No picture</p>",
            "<img src=\"data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciLz4=\" alt=\"Logo\">",
            "<img width=\"720\" src=\"data:image/png;base64,QUFB\" alt=\"Map\"><img src=\"data:image/jpeg;base64,QUFBQUFBQUFB\" alt=\"Pump\">",
            null
        };

        Assert.Equal("data:image/jpeg;base64,QUFBQUFBQUFB", AudioDraftService.ArticlePhoto(values));
        Assert.Null(AudioDraftService.ArticlePhoto(["<p>None</p>"]));
    }

    [Theory]
    [InlineData("@WPN_News", "WPN News", "@WPN_News")]
    [InlineData("WPN News", null, "@WPNNews")]
    [InlineData("", "WPN News", "@WPNNews")]
    [InlineData(null, "@wpn", "@wpn")]
    [InlineData(null, null, null)]
    public void Handle_IsTheModelsOrMadeFromTheSource(string handle, string source, string expected)
    {
        Assert.Equal(expected, AudioDraftService.Handle(handle, source));
    }

    /// <summary>A speaker keeps their first voice; a second speaker given the same voice gets another; an unknown voice is replaced.</summary>
    [Fact]
    public void ReadScript_OfACall_GivesEachSpeakerTheirOwnVoice()
    {
        var input = Json("""
            {"lines":[{"speaker":"CALLER 1","voice":"Gregory","text":"Are you  seeing it?"},
                      {"speaker":"CALLER 2","voice":"Gregory","text":"I see it."},
                      {"speaker":"caller 1","voice":"Ruth","text":"Good."},
                      {"speaker":"CALLER 3","voice":"Nobody","text":"Hello?"},
                      {"speaker":"CALLER 2","voice":"Gregory","text":"  "}],
             "notes":["Assumed two parties.", " "]}
            """);
        var result = new AudioDraftResult();

        var (lines, scene) = AudioDraftService.ReadScript(input, AudioDraftKind.Call, Voices, Voices[0], Article, result);

        Assert.Equal([("CALLER 1", "Gregory", "Are you seeing it?"), ("CALLER 2", "Matthew", "I see it."), ("caller 1", "Gregory", "Good."), ("CALLER 3", "Joanna", "Hello?")],
            lines.Select(l => (l.Speaker, l.Voice, l.Text)));
        Assert.Equal(["Assumed two parties."], result.Notes);
        Assert.Null(scene);
    }

    /// <summary>A broadcast's anchor is the outlet's voice, which nobody else may have.</summary>
    [Fact]
    public void ReadScript_OfABroadcast_GivesTheAnchorTheOutletsVoice()
    {
        var input = Json("""
            {"lines":[{"speaker":"Anchor","voice":"Gregory","text":"Good evening."},
                      {"speaker":"Mayor Ito","voice":"Joanna","text":"We are safe."}]}
            """);
        var result = new AudioDraftResult();

        var (lines, _) = AudioDraftService.ReadScript(input, AudioDraftKind.Broadcast, Voices, Voices[1], Article, result);

        Assert.Equal([("Anchor", "Joanna"), ("Mayor Ito", "Matthew")], lines.Select(l => (l.Speaker, l.Voice)));
        Assert.Equal("WPN News", result.Outlet);
    }

    /// <summary>A reel is one line in the outlet's voice, with its post, outlet, handle and the scene for its photo.</summary>
    [Fact]
    public void ReadScript_OfAReel_IsTheAnchorsScriptAndItsPost()
    {
        var input = Json("""{"script":"Two stations are down.","post":"Pipeline stations offline. #WPN","outlet":"WPN","handle":"WPNNews","scene":"a pumping station at dawn"}""");
        var result = new AudioDraftResult();

        var (lines, scene) = AudioDraftService.ReadScript(input, AudioDraftKind.Reel, Voices, Voices[2], Article, result);

        Assert.Equal([("Anchor", "Gregory", "Two stations are down.")], lines.Select(l => (l.Speaker, l.Voice, l.Text)));
        Assert.Equal(("Pipeline stations offline. #WPN", "WPN", "@WPNNews"), (result.Post, result.Outlet, result.Handle));
        Assert.Equal("a pumping station at dawn", scene);
    }

    [Fact]
    public void Transcript_LabelsEachLineWithItsSpeaker()
    {
        var html = AudioDraftService.Transcript([new AudioDraftLine { Speaker = "Caller 1", Text = "Is <it> on?" }, new AudioDraftLine { Speaker = "Caller 2", Text = "Yes." }]);

        Assert.Equal("<details><summary><b>Transcript</b></summary><p><b>CALLER 1:</b> Is &lt;it&gt; on?</p><p><b>CALLER 2:</b> Yes.</p></details>", html);
    }

    [Theory]
    [InlineData(null, null, "EXERCISE · EXERCISE · EXERCISE", "#FDB516")]
    [InlineData("Tlp", "amber", "TLP:AMBER", "#000000")]
    [InlineData("Classification", "secret//rel to usa", "SECRET//REL TO USA", "#C8102E")]
    public void MarkingBar_UsesTheArticleBannersWordsAndColours(string marking, string text, string expected, string background)
    {
        var bar = ArticleMarkings.Bar(marking, text);

        Assert.Equal((expected, background), (bar.Value.Text, bar.Value.Background));
        Assert.Null(ArticleMarkings.Bar("None", null));
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
