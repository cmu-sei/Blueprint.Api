// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Blueprint.Api.Services;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary>The banner Blueprint puts on every Gallery article of a MSEL, whatever the model wrote.</summary>
public class ArticleMarkingsTests
{
    private const string Article = "<p>Cables cut, says the Herald.</p>";

    [Fact]
    public void Apply_PutsTheExerciseBannerOnTop_ByDefault()
    {
        var html = ArticleMarkings.Apply(Article, null, null, "Deep Bite");

        Assert.StartsWith("<table title=\"Marking\"", html);
        Assert.Contains("EXERCISE · EXERCISE · EXERCISE", html);
        Assert.Contains("Deep Bite · FICTIONAL SCENARIO", html);
        Assert.EndsWith(Article, html);
    }

    [Fact]
    public void Apply_LeavesTheArticleUnmarked_ForNone()
    {
        Assert.Equal(Article, ArticleMarkings.Apply(Article, "none", null, "Deep Bite"));
    }

    [Theory]
    [InlineData("amber", "TLP:AMBER", "#FFC000")]
    [InlineData("TLP:RED", "TLP:RED", "#FF2B2B")]
    [InlineData("purple", "TLP:CLEAR", "#FFFFFF")]
    public void Apply_MarksTheTlpLevel(string level, string banner, string colour)
    {
        var html = ArticleMarkings.Apply(Article, "Tlp", level, "Deep Bite");

        Assert.Contains($"<b>{banner}</b>", html);
        Assert.Contains($"color=\"{colour}\"", html);
        Assert.DoesNotContain("EXERCISE", html);
    }

    /// <summary>A classification banner is the planner's text, at the top and bottom, in its level's colour.</summary>
    [Theory]
    [InlineData("Unclassified // For Exercise Use Only", "#007A33")]
    [InlineData("CUI", "#502B85")]
    [InlineData("SECRET//NOFORN", "#C8102E")]
    [InlineData("TOP SECRET//SCI", "#FCE83A")]
    public void Apply_MarksTheClassificationTopAndBottom(string text, string background)
    {
        var html = ArticleMarkings.Apply(Article, "Classification", text, "Deep Bite");

        Assert.StartsWith("<table title=\"Marking\"", html);
        Assert.EndsWith("</table>", html);
        Assert.Contains(text.ToUpperInvariant(), html);
        Assert.Contains($"bgcolor=\"{background}\"", html);
    }

    /// <summary>A marking already on the article, as when Write with AI rewrites one, is replaced, not repeated.</summary>
    [Fact]
    public void Apply_ReplacesAMarkingAlreadyThere()
    {
        var marked = ArticleMarkings.Apply(Article, "Exercise", null, "Deep Bite");

        var remarked = ArticleMarkings.Apply(marked, "Tlp", "GREEN", "Deep Bite");

        Assert.DoesNotContain("EXERCISE", remarked);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(remarked, "title=\"Marking\""));
    }

    [Fact]
    public void Clean_FallsBackToTheExerciseBanner_ForAClassificationWithNoText()
    {
        Assert.Equal(("Exercise", null), ArticleMarkings.Clean("Classification", " "));
        Assert.Equal(("Exercise", null), ArticleMarkings.Clean("Bogus", "x"));
    }
}
