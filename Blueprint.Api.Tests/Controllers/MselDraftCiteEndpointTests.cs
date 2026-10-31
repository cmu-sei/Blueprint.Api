// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Cite.Api.Client;
using SystemPermission = Blueprint.Api.Data.Enumerations.SystemPermission;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ClearExtensions;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// The CITE side of creating a drafted MSEL, as the blueprint-ttx loader sets it up: CITE's built-in team types on the
/// teams, and the scoring model the planner chose, authored into the MSEL as its own, so it is reviewed in Blueprint and
/// created in CITE only by the push.
/// </summary>
public class MselDraftCiteEndpointTests(DatabaseFixture fixture, AiEnabledFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<AiEnabledFactory>
{
    private readonly ICiteApiClient _cite = factory.Cite;
    private static readonly TeamType Contributor = new() { Id = Guid.NewGuid(), Name = "Official Score Contributor" };
    private static readonly TeamType Other = new() { Id = Guid.NewGuid(), Name = "Other" };
    private static readonly TeamType Standard = new() { Id = Guid.NewGuid(), Name = "Standard" };
    private readonly List<ScoringModel> _models = new();
    private ScoringModel _created;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _cite.ClearSubstitute();
        _cite.GetTeamTypesAsync(Arg.Any<CancellationToken>()).Returns(new List<TeamType> { Standard, Contributor, Other });
        _cite.GetScoringModelsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>()).Returns(_ => _models.ToList());
        _cite.CreateScoringModelAsync(Arg.Any<ScoringModel>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            _created = call.Arg<ScoringModel>();
            _created.Id = Guid.NewGuid();
            _models.Add(_created);
            return _created;
        });
    }

    /// <summary>
    /// Questions by move: the MSEL's own model with one category a move, shown only in that move, its questions as options
    /// with comments and hidden scores, and the objectives in the hot wash; nothing is made in CITE, and each team gets its type.
    /// </summary>
    [Fact]
    public async Task Import_WithQuestionsByMove_AuthorsTheModelAndSetsTeamTypes()
    {
        var mselId = await Import("QuestionsByMove");

        await _cite.DidNotReceive().CreateScoringModelAsync(Arg.Any<ScoringModel>(), Arg.Any<CancellationToken>());
        await using var db = NewContext();
        var model = await Authored(db, mselId);
        Assert.Equal("Deep Bite Questions by Move", model.Description);
        Assert.True(model.HideScoresOnScoreSheet);
        Assert.True(model.DisplayCommentTextBoxes);
        Assert.True(model.DisplayScoringModelByMoveNumber);
        var categories = model.Categories.OrderBy(c => c.DisplayOrder).ToList();
        Assert.Equal(
            // play is moves 1 and 2; the after-action review is the hot wash after ENDEX
            [(MselDraftCite.QuestionsHeading, 1, 1, 2), (MselDraftCite.QuestionsHeading, 2, 2, 1), (MselDraftCite.AarHeading, 3, 3, 1)],
            categories.Select(c => (c.Description, c.MoveNumberFirstDisplay, c.MoveNumberLastDisplay, c.Options.Count)));
        Assert.All(categories, c => Assert.Equal(CiteScoringOptionSelection.None, c.ScoringOptionSelection));
        Assert.Equal(["How will you confirm the outage?", "Who do you tell first?"], categories.First().Options.OrderBy(o => o.DisplayOrder).Select(o => o.Description));
        Assert.Equal("O1: Lead the response. Standard: A lead within 30 minutes", categories.Last().Options.Single().Description);

        var msel = await db.Msels.Include(m => m.Teams).SingleAsync(m => m.Id == mselId, Ct);
        Assert.Null(msel.CiteScoringModelId);
        Assert.Equal(Contributor.Id, msel.Teams.Single(t => t.ShortName == "BLUE").CiteTeamTypeId);
        Assert.Equal(Other.Id, msel.Teams.Single(t => t.ShortName == "EXCON").CiteTeamTypeId);
        Assert.Equal("Other", msel.Teams.Single(t => t.ShortName == "EXCON").CiteTeamTypeName);
    }

    /// <summary>A DoD CTT matrix is the MSEL's own copy of the preset, with the matrix beside the score sheet; CITE is not asked.</summary>
    [Fact]
    public async Task Import_WithAMatrix_AuthorsThePreset()
    {
        var mselId = await Import("Likelihood");

        await _cite.DidNotReceive().GetScoringModelsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>());
        await _cite.DidNotReceive().CreateScoringModelAsync(Arg.Any<ScoringModel>(), Arg.Any<CancellationToken>());
        await using var db = NewContext();
        var model = await Authored(db, mselId);
        Assert.Equal("DoD CTT Likelihood Assessment Matrix", model.Description);
        Assert.Equal(CiteRightSideDisplay.HtmlBlock, model.RightSideDisplay);
        Assert.Contains("Attack Success Likelihood", model.RightSideHtmlBlock);
        Assert.Equal(["Attack Cost / Level of Effort", "Attack Success Likelihood"], model.Categories.OrderBy(c => c.DisplayOrder).Select(c => c.Description));
    }

    /// <summary>NCISS is authored as CITE's own seed data defines it, so the push creates the same model.</summary>
    [Fact]
    public async Task Import_WithNciss_AuthorsItAsCiteDefinesIt()
    {
        var (mselId, result) = await ImportWithResult("Nciss");

        await using var db = NewContext();
        var model = await Authored(db, mselId);
        Assert.Equal("CISA NCISS", model.Description);
        Assert.Equal("100.0 > 100.0 * ({sum} - {minPossible}) / ({maxPossible} - {minPossible}) > 0.0", model.CalculationEquation);
        Assert.Equal(
            [("Functional Impact", 6.0, 8), ("Observed Activity", 5.0, 5), ("Location of Observed Activity", 4.0, 8), ("Actor Characterization", 4.0, 7),
             ("Information Impact", 2.0, 8), ("Recoverability", 4.0, 4), ("Cross Sector Dependency", 3.0, 16), ("Potential Impact", 6.0, 5)],
            model.Categories.OrderBy(c => c.DisplayOrder).Select(c => (c.Description, c.ScoringWeight, c.Options.Count)));
        Assert.All(model.Categories, c => Assert.Equal(CiteScoringOptionSelection.Single, c.ScoringOptionSelection));
        Assert.Contains(result.Changes, c => c.Message == "Authored the CITE scoring model \"CISA NCISS\" in Blueprint. Review it under the CITE settings on the Info tab; the push creates it in CITE.");
    }

    /// <summary>A template that links a scoring model keeps it when the planner chooses nothing else.</summary>
    [Fact]
    public async Task Import_FromATemplateWithAModel_KeepsIt()
    {
        var model = new ScoringModel { Id = Guid.NewGuid(), Description = "House model" };
        _models.Add(model);

        var mselId = await Import(null, templateModel: model);

        await _cite.DidNotReceive().CreateScoringModelAsync(Arg.Any<ScoringModel>(), Arg.Any<CancellationToken>());
        await using var db = NewContext();
        Assert.Equal(model.Id, (await db.Msels.SingleAsync(m => m.Id == mselId, Ct)).CiteScoringModelId);
    }

    private Task<MselScoringModelEntity> Authored(BlueprintContext db, Guid mselId) =>
        db.MselScoringModels.Include(m => m.Categories).ThenInclude(c => c.Options).AsSplitQuery().SingleAsync(m => m.MselId == mselId, Ct);

    /// <summary>
    /// A template that authors its own model, as the seeded Crucible MSEL does with NCISS, hands the drafted MSEL a copy of
    /// it under new ids, whatever questions the draft wrote; nothing is made in CITE.
    /// </summary>
    [Fact]
    public async Task Import_FromATemplateWithAnAuthoredModel_CopiesIt()
    {
        var (mselId, result) = await ImportWithResult("QuestionsByMove", authoredTemplateModel: true);

        await _cite.DidNotReceive().CreateScoringModelAsync(Arg.Any<ScoringModel>(), Arg.Any<CancellationToken>());
        await using var db = NewContext();
        var model = await Authored(db, mselId);
        Assert.Equal("CISA NCISS", model.Description);
        Assert.Equal(8, model.Categories.Count);
        var templateModel = await db.MselScoringModels.Include(m => m.Categories).SingleAsync(m => m.MselId == _templateId, Ct);
        Assert.Empty(model.Categories.Select(c => c.Id).Intersect(templateModel.Categories.Select(c => c.Id)));
        Assert.Contains(result.Changes, c => c.Message.StartsWith("Kept the template's own CITE scoring model \"CISA NCISS\""));
    }

    private Guid _templateId;

    private async Task<Guid> Import(string assessment, ScoringModel templateModel = null) =>
        (await ImportWithResult(assessment, templateModel)).MselId;

    private async Task<(Guid MselId, MselImportResult Result)> ImportWithResult(string assessment, ScoringModel templateModel = null, bool authoredTemplateModel = false)
    {
        var template = TestData.Msel(isTemplate: true);
        template.UseCite = true;
        template.CiteScoringModelId = templateModel?.Id;
        template.CiteScoringModelName = templateModel?.Description;
        _templateId = template.Id;
        var blue = TestData.Team(template.Id);
        blue.ShortName = "BLUE";
        var excon = TestData.Team(template.Id);
        excon.ShortName = "EXCON";
        await Seed(template, blue, excon, TestData.DataField(template.Id, name: "Title", displayOrder: 1));
        if (authoredTemplateModel)
        {
            var authored = ScoringModelPresets.Nciss();
            authored.MselId = template.Id;
            foreach (var category in authored.Categories)
            {
                category.Id = Guid.NewGuid();
                category.MselScoringModelId = template.Id;
                foreach (var option in category.Options)
                {
                    option.Id = Guid.NewGuid();
                    option.MselScoringCategoryId = category.Id;
                }
            }
            await Seed(authored);
        }
        var draft = JsonSerializer.Deserialize<MselDraft>(Draft(), JsonOptions);
        draft.Design = new MselDraftDesign { CiteAssessment = assessment };
        var planner = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(planner).PostAsJsonAsync("/api/msels/ai-draft/import", new MselDraftImport { TemplateMselId = template.Id, Draft = draft }, JsonOptions, Ct);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        var result = await response.Content.ReadFromJsonAsync<MselImportResult>(JsonOptions, Ct);
        return (result.Msel.Id, result);
    }

    private static string Draft() =>
        JsonSerializer.Serialize(new
        {
            name = "Deep Bite",
            description = "Cables are cut.",
            durationMinutes = 120,
            objectives = new[] { new { id = "O1", task = "Lead the response", condition = "With partial outages", standard = "A lead within 30 minutes" } },
            moves = new object[]
            {
                new { moveNumber = 0, description = "Start", startMinutes = 0, citeQuestions = new[] { "How will you confirm the outage?", "Who do you tell first?" } },
                new { moveNumber = 1, description = "Next", startMinutes = 60, citeQuestions = new[] { "What do you tell the press?" } }
            },
            teams = Array.Empty<object>(),
            organizations = Array.Empty<object>(),
            injects = Array.Empty<object>()
        });
}
