// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Blueprint.Api.Services;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary>The banner Blueprint puts on every Gallery article of a MSEL, whatever the article already says.</summary>
public class ArticleMarkingsTests
{
    private const string Article = "<p>Cables cut, says the Herald.</p>";

    /// <summary>A MSEL whose marking was never set has none, so nothing changes for one that predates markings.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bogus")]
    public void Apply_LeavesTheArticleUnmarked_ByDefault(string marking)
    {
        Assert.Equal(Article, ArticleMarkings.Apply(Article, marking, null, "Deep Bite"));
    }

    [Fact]
    public void Apply_PutsTheExerciseBannerOnTop()
    {
        var html = ArticleMarkings.Apply(Article, "exercise", null, "Deep Bite");

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

    /// <summary>A marking already on the article, as when it is pushed again, is replaced, not repeated.</summary>
    [Fact]
    public void Apply_ReplacesAMarkingAlreadyThere()
    {
        var marked = ArticleMarkings.Apply(Article, "Exercise", null, "Deep Bite");

        var remarked = ArticleMarkings.Apply(marked, "Tlp", "GREEN", "Deep Bite");

        Assert.DoesNotContain("EXERCISE", remarked);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(remarked, "title=\"Marking\""));
    }

    [Fact]
    public void Apply_TakesAMarkingAlreadyThereOff_ForNone()
    {
        var marked = ArticleMarkings.Apply(Article, "Classification", "SECRET", "Deep Bite");

        Assert.Equal(Article, ArticleMarkings.Apply(marked, "None", null, "Deep Bite"));
    }

    [Fact]
    public void Apply_UsesTheFallback_WhenTheMarkingIsNotSet()
    {
        var html = ArticleMarkings.Apply(Article, null, null, "Deep Bite", ArticleMarkings.Exercise);

        Assert.Contains("EXERCISE · EXERCISE · EXERCISE", html);
    }

    [Fact]
    public void Clean_FallsBackToNone_ForAClassificationWithNoTextOrAMarkingItDoesNotKnow()
    {
        Assert.Equal(("None", null), ArticleMarkings.Clean("Classification", " "));
        Assert.Equal(("None", null), ArticleMarkings.Clean("Bogus", "x"));
        Assert.Equal(("None", null), ArticleMarkings.Clean(null, null));
    }

    [Fact]
    public void Clean_FallsBackToTheExerciseBanner_WhenAskedTo()
    {
        Assert.Equal(("Exercise", null), ArticleMarkings.Clean("Classification", " ", ArticleMarkings.Exercise));
        Assert.Equal(("Exercise", null), ArticleMarkings.Clean("Bogus", "x", ArticleMarkings.Exercise));
    }

    [Fact]
    public void Clean_KeepsTheKnownMarkings_AndDropsTextOnlyATlpOrClassificationUses()
    {
        Assert.Equal(("Exercise", null), ArticleMarkings.Clean(" exercise ", "ignored"));
        Assert.Equal(("Tlp", "AMBER+STRICT"), ArticleMarkings.Clean("tlp", "tlp:amber+strict"));
        Assert.Equal(("Tlp", "CLEAR"), ArticleMarkings.Clean("Tlp", null));
        Assert.Equal(("Classification", new string('x', 120)), ArticleMarkings.Clean("Classification", new string('x', 200)));
    }

    [Fact]
    public void Bar_IsTheBannersWordsAndColours_AndNothingForNone()
    {
        Assert.Null(ArticleMarkings.Bar(null, null));
        Assert.Equal(("TLP:RED", "#000000", "#FF2B2B"), ArticleMarkings.Bar("Tlp", "red"));
        Assert.Equal(("SECRET//NOFORN", "#C8102E", "#FFFFFF"), ArticleMarkings.Bar("Classification", "Secret//NoForn"));
        Assert.Equal(("EXERCISE · EXERCISE · EXERCISE", "#FDB516", "#000000"), ArticleMarkings.Bar("Exercise", null));
    }
}
