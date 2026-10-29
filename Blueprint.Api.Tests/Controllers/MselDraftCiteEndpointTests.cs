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
/// teams, and the scoring model the planner chose, made or found in CITE.
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
    /// Questions by move: a model with one category a move, shown only in that move, its questions as options with
    /// comments and hidden scores, and the objectives in the last move; the MSEL links it, and each team gets its type.
    /// </summary>
    [Fact]
    public async Task Import_WithQuestionsByMove_CreatesTheModelAndSetsTeamTypes()
    {
        var mselId = await Import("QuestionsByMove");

        Assert.Equal("Deep Bite Questions by Move", _created.Description);
        Assert.True(_created.HideScoresOnScoreSheet);
        Assert.True(_created.DisplayCommentTextBoxes);
        Assert.True(_created.DisplayScoringModelByMoveNumber);
        Assert.Equal(
            [(MselDraftCite.QuestionsHeading, 0, 0, 2), (MselDraftCite.QuestionsHeading, 1, 1, 1), (MselDraftCite.AarHeading, 1, 1, 1)],
            _created.ScoringCategories.Select(c => (c.Description, c.MoveNumberFirstDisplay, c.MoveNumberLastDisplay, c.ScoringOptions.Count)));
        Assert.All(_created.ScoringCategories, c => Assert.Equal(ScoringOptionSelection.None, c.ScoringOptionSelection));
        Assert.Equal(["How will you confirm the outage?", "Who do you tell first?"], _created.ScoringCategories.First().ScoringOptions.Select(o => o.Description));
        Assert.Equal("O1: Lead the response. Standard: A lead within 30 minutes", _created.ScoringCategories.Last().ScoringOptions.Single().Description);

        await using var db = NewContext();
        var msel = await db.Msels.Include(m => m.Teams).SingleAsync(m => m.Id == mselId, Ct);
        Assert.Equal(_created.Id, msel.CiteScoringModelId);
        Assert.Equal("Deep Bite Questions by Move", msel.CiteScoringModelName);
        Assert.Equal(Contributor.Id, msel.Teams.Single(t => t.ShortName == "BLUE").CiteTeamTypeId);
        Assert.Equal(Other.Id, msel.Teams.Single(t => t.ShortName == "EXCON").CiteTeamTypeId);
        Assert.Equal("Other", msel.Teams.Single(t => t.ShortName == "EXCON").CiteTeamTypeName);
    }

    /// <summary>A DoD CTT matrix already in CITE is linked by name, not made again.</summary>
    [Fact]
    public async Task Import_WithAMatrixInCite_LinksIt()
    {
        var existing = new ScoringModel { Id = Guid.NewGuid(), Description = "DoD CTT Mission Impact Assessment" };
        _models.Add(existing);

        var mselId = await Import("MissionImpact");

        await _cite.DidNotReceive().CreateScoringModelAsync(Arg.Any<ScoringModel>(), Arg.Any<CancellationToken>());
        await using var db = NewContext();
        Assert.Equal(existing.Id, (await db.Msels.SingleAsync(m => m.Id == mselId, Ct)).CiteScoringModelId);
    }

    /// <summary>A DoD CTT matrix CITE lacks is made from its built-in definition.</summary>
    [Fact]
    public async Task Import_WithAMatrixCiteLacks_CreatesIt()
    {
        await Import("Likelihood");

        Assert.Equal("DoD CTT Likelihood Assessment Matrix", _created.Description);
        Assert.Equal(RightSideDisplay.HtmlBlock, _created.RightSideDisplay);
        Assert.Contains("Attack Success Likelihood", _created.RightSideHtmlBlock);
        Assert.Equal(["Attack Cost / Level of Effort", "Attack Success Likelihood"], _created.ScoringCategories.Select(c => c.Description));
    }

    /// <summary>A planner who may not make scoring models in CITE still gets the MSEL, told to choose one.</summary>
    [Fact]
    public async Task Import_WhenCiteRefusesTheModel_StillCreatesTheMsel()
    {
        _cite.CreateScoringModelAsync(Arg.Any<ScoringModel>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException("Forbidden", 403, "", new Dictionary<string, IEnumerable<string>>(), null));

        var (mselId, result) = await ImportWithResult("QuestionsByMove");

        await using var db = NewContext();
        Assert.Null((await db.Msels.SingleAsync(m => m.Id == mselId, Ct)).CiteScoringModelId);
        Assert.Contains(result.Changes, c => c.Message == "The CITE scoring model \"Deep Bite Questions by Move\" could not be made (you may not do that in CITE). Create or choose one in CITE before pushing.");
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

    private async Task<Guid> Import(string assessment, ScoringModel templateModel = null) =>
        (await ImportWithResult(assessment, templateModel)).MselId;

    private async Task<(Guid MselId, MselImportResult Result)> ImportWithResult(string assessment, ScoringModel templateModel = null)
    {
        var template = TestData.Msel(isTemplate: true);
        template.UseCite = true;
        template.CiteScoringModelId = templateModel?.Id;
        template.CiteScoringModelName = templateModel?.Description;
        var blue = TestData.Team(template.Id);
        blue.ShortName = "BLUE";
        var excon = TestData.Team(template.Id);
        excon.ShortName = "EXCON";
        await Seed(template, blue, excon, TestData.DataField(template.Id, name: "Title", displayOrder: 1));
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
