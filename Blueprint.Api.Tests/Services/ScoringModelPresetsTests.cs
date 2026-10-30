// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Services;
using Xunit;
using CiteClient = Cite.Api.Client;

namespace Blueprint.Api.Tests.Services;

/// <summary>
/// <c>ScoringModelPresets</c>, the models a MSEL's own scoring model starts from, and <c>CiteScoringModelConverter</c>,
/// which carries a model to and from the CITE client.
/// </summary>
public class ScoringModelPresetsTests
{
    /// <summary>The questions a caller has, such as a drafted MSEL's, become each move's options, in order.</summary>
    [Fact]
    public void QuestionsByMove_MakesEachMovesQuestionsItsOptions()
    {
        var model = ScoringModelPresets.QuestionsByMove("Drafted",
            [(2, ["Who decides?"]), (1, ["Who calls?", "Who writes?"])], ["Objective 1: notify. Standard: within an hour"]);

        Assert.Equal([1, 2, 3], model.Categories.Select(c => c.DisplayOrder));
        Assert.Equal(["Who calls?", "Who writes?"], model.Categories.First().Options.Select(o => o.Description));
        Assert.Equal([1, 2], model.Categories.First().Options.Select(o => o.DisplayOrder));
        var review = model.Categories.Last();
        Assert.Equal(ScoringModelPresets.AarHeading, review.Description);
        Assert.Equal((2, 2), (review.MoveNumberFirstDisplay, review.MoveNumberLastDisplay));
        Assert.All(model.Categories.SelectMany(c => c.Options), o => Assert.Equal(1, o.Value));
    }

    [Fact]
    public void QuestionsByMove_WithNoAfterActionQuestions_HasNoAfterActionReview()
    {
        var model = ScoringModelPresets.QuestionsByMove("Drafted", [(1, ["Who calls?"])], null);

        Assert.Equal(QuestionsHeadings(1), model.Categories.Select(c => c.Description));
    }

    /// <summary>Each call answers a model of its own, so a caller changing one does not change the next.</summary>
    [Fact]
    public void Presets_AreNewModelsEachTime()
    {
        var first = ScoringModelPresets.Nciss();
        first.Categories.First().Description = "changed";

        Assert.Equal("Functional Impact", ScoringModelPresets.Nciss().Categories.First().Description);
    }

    /// <summary>
    /// The enums go by name both ways: Blueprint stores CITE's values (Multiple = 10, Scoresheet = 30), the client
    /// numbers its own copies from 0.
    /// </summary>
    [Theory]
    [InlineData(CiteScoringOptionSelection.Single, CiteRightSideDisplay.ScoreSummary)]
    [InlineData(CiteScoringOptionSelection.Multiple, CiteRightSideDisplay.HtmlBlock)]
    [InlineData(CiteScoringOptionSelection.None, CiteRightSideDisplay.Scoresheet)]
    [InlineData(CiteScoringOptionSelection.None, CiteRightSideDisplay.EmbeddedUrl)]
    [InlineData(CiteScoringOptionSelection.None, CiteRightSideDisplay.None)]
    public void Converter_CarriesTheEnumsByName(CiteScoringOptionSelection selection, CiteRightSideDisplay display)
    {
        var model = ScoringModelPresets.MissionImpact();
        model.RightSideDisplay = display;
        model.Categories.First().ScoringOptionSelection = selection;

        var cite = CiteScoringModelConverter.ToCite(model);

        Assert.Equal(display.ToString(), cite.RightSideDisplay.ToString());
        Assert.Equal(selection.ToString(), cite.ScoringCategories.First().ScoringOptionSelection.ToString());
        Assert.Equal(CiteClient.ItemStatus.Active, cite.Status);
        var back = CiteScoringModelConverter.FromCite(cite);
        Assert.Equal(display, back.RightSideDisplay);
        Assert.Equal(selection, back.Categories.First().ScoringOptionSelection);
    }

    /// <summary>The model goes to CITE with every setting, category and option, and comes back the same.</summary>
    [Fact]
    public void Converter_RoundTripsThePresets()
    {
        foreach (var preset in new[] { ScoringModelPresets.MissionImpact(), ScoringModelPresets.Likelihood(), ScoringModelPresets.Nciss() })
        {
            var back = CiteScoringModelConverter.FromCite(CiteScoringModelConverter.ToCite(preset));

            Assert.Equal(preset.Description, back.Description);
            Assert.Equal(preset.CalculationEquation, back.CalculationEquation);
            Assert.Equal(preset.RightSideHtmlBlock, back.RightSideHtmlBlock);
            Assert.Equal(preset.UseOfficialScore, back.UseOfficialScore);
            Assert.Equal(
                preset.Categories.Select(c => (c.Description, c.ScoringWeight, c.MoveNumberFirstDisplay, c.MoveNumberLastDisplay, string.Join("|", c.Options.Select(o => $"{o.Description}={o.Value}")))),
                back.Categories.Select(c => (c.Description, c.ScoringWeight, c.MoveNumberFirstDisplay, c.MoveNumberLastDisplay, string.Join("|", c.Options.Select(o => $"{o.Description}={o.Value}")))));
        }
    }

    [Fact]
    public void Converter_FromACiteModelWithNoCategories_HasNone()
    {
        var back = CiteScoringModelConverter.FromCite(new CiteClient.ScoringModel { Description = "empty", ScoringCategories = null });

        Assert.Empty(back.Categories);
    }

    private static string[] QuestionsHeadings(int count) => Enumerable.Repeat(ScoringModelPresets.QuestionsHeading, count).ToArray();
}
