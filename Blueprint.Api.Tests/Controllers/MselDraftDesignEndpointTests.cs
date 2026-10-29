// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// The design step of an AI draft, as the author-ttx workflow runs it: the planner's design choices go to the model,
/// and the draft comes back with its scenario, objectives, each move's purpose and decisions, and each inject's
/// expected action and objective.
/// </summary>
public class MselDraftDesignEndpointTests(DatabaseFixture fixture, AiEnabledFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<AiEnabledFactory>
{
    private readonly IAiModelClient _model = factory.Model;
    private string _system;
    private string _prompt;
    private string _schema;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _model.ClearSubstitute();
    }

    /// <summary>
    /// The planner's choices reach the model, and open ones are marked open; the draft keeps the planner's design,
    /// not anything the model sends for it.
    /// </summary>
    [Fact]
    public async Task Draft_TellsTheModelThePlannersDesignChoices()
    {
        var template = await SeedTemplate(useCite: true);
        Answer(Draft(design: new { citeAssessment = "None", moves = 9 }));

        var result = await Read<MselDraftResult>(await PostDraft(template.Id, new MselDraftDesign
        {
            Format = "discussion",
            Structure = "Two parallel national CERTs",
            CiteAssessment = "MissionImpact",
            Moves = 40,
            DurationMinutes = 180,
            LeadershipBrief = true
        }));

        Assert.Contains("design and draft", _system);
        Assert.Contains("task, condition and standard", _system);
        Assert.Contains("- Format: a discussion-based tabletop", _prompt);
        Assert.Contains("- Participant structure: Two parallel national CERTs", _prompt);
        Assert.Contains("- CITE assessment: the DoD Cyber Table Top mission impact matrix", _prompt);
        Assert.Contains("- Moves: 12", _prompt);
        Assert.Contains("- Exercise length in real time: 180 minutes", _prompt);
        Assert.Contains("- Scenario time the exercise covers: open", _prompt);
        Assert.Contains("- Final move is a brief to leadership: yes", _prompt);
        Assert.Contains("\"objectives\"", _schema);
        Assert.Contains("\"expectedDecisions\"", _schema);
        Assert.DoesNotContain("\"citeQuestions\"", _schema);
        Assert.Equal("Discussion", result.Draft.Design.Format);
        Assert.Equal("MissionImpact", result.Draft.Design.CiteAssessment);
        Assert.Equal(12, result.Draft.Design.Moves);
    }

    /// <summary>With CITE and no assessment chosen, the draft writes discussion questions for every move.</summary>
    [Fact]
    public async Task Draft_WithCiteAndNoAssessmentChosen_AsksForQuestionsByMove()
    {
        var template = await SeedTemplate(useCite: true);
        Answer(Draft());

        var result = await Read<MselDraftResult>(await PostDraft(template.Id, new MselDraftDesign()));

        Assert.Contains("\"citeQuestions\"", _schema);
        Assert.Contains("- CITE assessment: questions by move", _prompt);
        Assert.Equal(["How will you confirm the outage?", "Who do you tell first?"], result.Draft.Moves[0].CiteQuestions);
        Assert.Contains(result.Warnings, w => w.StartsWith("No discussion questions for move 1;"));
    }

    /// <summary>Without CITE there is no assessment, and questions the model sends anyway are dropped.</summary>
    [Fact]
    public async Task Draft_WithoutCite_HasNoAssessment()
    {
        var template = await SeedTemplate(useCite: false);
        Answer(Draft());

        var result = await Read<MselDraftResult>(await PostDraft(template.Id, new MselDraftDesign { CiteAssessment = "QuestionsByMove" }));

        Assert.Equal("None", result.Draft.Design.CiteAssessment);
        Assert.DoesNotContain("\"citeQuestions\"", _schema);
        Assert.All(result.Draft.Moves, m => Assert.Empty(m.CiteQuestions));
    }

    /// <summary>
    /// Objectives get unique IDs; an inject naming an objective the draft lacks loses it and is reported; the scenario,
    /// each move's purpose and expected decisions, and each inject's expected action come back for review.
    /// </summary>
    [Fact]
    public async Task Draft_KeepsTheScenarioObjectivesAndTraceability()
    {
        var template = await SeedTemplate(useCite: false);
        Answer(Draft());

        var result = await Read<MselDraftResult>(await PostDraft(template.Id, new MselDraftDesign()));

        Assert.Equal("Two cables to the islands are cut.", result.Draft.Scenario.Problem);
        Assert.Equal(["O1", "O2", "O3"], result.Draft.Objectives.Select(o => o.Id));
        Assert.Equal(["Decide who leads the response", "Brief the ministry"], result.Draft.Moves[0].ExpectedDecisions);
        Assert.Equal("Hold the vendor call until they ask.", result.Draft.Moves[0].ControllerNotes);
        Assert.Equal(["O1", null], result.Draft.Injects.Select(i => i.Objective));
        Assert.Equal("Open an incident and assign a lead", result.Draft.Injects[0].ExpectedAction);
        Assert.Contains(result.Warnings, w => w == "Took injects off objectives the draft does not have: O9.");
        Assert.DoesNotContain(result.Fields, f => f.Name == "Expected Actions");
    }

    /// <summary>Import writes each inject's expected action to an Expected Actions column controllers see and participants do not.</summary>
    [Fact]
    public async Task Import_WritesExpectedActionsToAControllerColumn()
    {
        var template = await SeedTemplate(useCite: false);
        var draft = JsonSerializer.Deserialize<MselDraft>(Draft(), JsonOptions);
        var planner = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(planner).PostAsJsonAsync("/api/msels/ai-draft/import", new MselDraftImport { TemplateMselId = template.Id, Draft = draft }, JsonOptions, Ct);

        var mselId = (await Read<MselImportResult>(response)).Msel.Id;
        await using var db = NewContext();
        var column = await db.DataFields.SingleAsync(f => f.MselId == mselId && f.Name == "Expected Actions", Ct);
        Assert.True(column.OnScenarioEventList);
        Assert.False(column.OnExerciseView);
        var values = await db.DataValues.Where(v => v.DataFieldId == column.Id).Select(v => v.Value).ToListAsync(Ct);
        Assert.Contains("Open an incident and assign a lead", values);
        Assert.False(await db.DataFields.AnyAsync(f => f.MselId == template.Id && f.Name == "Expected Actions", Ct));
    }

    private void Answer(string json) =>
        _model.InvokeToolAsync(default, default, default, default, default, default)
            .ReturnsForAnyArgs(callInfo =>
            {
                _system = callInfo.ArgAt<string>(0);
                _prompt = callInfo.ArgAt<string>(1);
                _schema = callInfo.ArgAt<System.Text.Json.Nodes.JsonObject>(4).ToJsonString();
                return JsonDocument.Parse(json).RootElement.Clone();
            });

    // two objectives named O1, one unnamed; the second inject names O9, which the draft does not have
    private static string Draft(object design = null) =>
        JsonSerializer.Serialize(new
        {
            name = "Deep Bite",
            description = "Cables are cut.",
            durationMinutes = 120,
            design,
            scenario = new { problem = "Two cables to the islands are cut.", premise = "A ship dragged its anchor.", environment = "Island state", escalationCeiling = "No loss of life", assumptions = new[] { "Repair takes weeks", " ", "Repair takes weeks" } },
            objectives = new object[]
            {
                new { id = "O1", task = "Lead the response", condition = "With partial outages", standard = "A lead within 30 minutes" },
                new { id = "o1", task = "Inform the public", condition = "With the press calling", standard = "A statement in an hour" },
                new { task = "Coordinate repair", condition = "With one vendor", standard = "A plan by end of day" },
                new { id = "O4", task = " ", condition = "", standard = "" }
            },
            moves = new object[]
            {
                new { moveNumber = 0, description = "Start", situationDescription = "Outage", startMinutes = 0, purpose = "Make them choose a lead",
                      expectedDecisions = new[] { "Decide who leads the response", "Brief the ministry", "" },
                      controllerNotes = " Hold the vendor call until they ask. ", citeQuestions = new[] { "How will you confirm the outage?", "Who do you tell first?" } },
                new { moveNumber = 1, description = "Next", situationDescription = "Press", startMinutes = 60, purpose = "Public pressure", expectedDecisions = Array.Empty<string>() }
            },
            teams = new[] { new { name = "Security operations", shortName = "SOC" } },
            organizations = Array.Empty<object>(),
            injects = new object[]
            {
                new { minutes = 5, eventType = "Inject", objective = "o1", expectedAction = " Open an incident and assign a lead ", values = new Dictionary<string, string> { ["Title"] = "Outage" } },
                new { minutes = 70, eventType = "Inject", objective = "O9", values = new Dictionary<string, string> { ["Title"] = "Press call" } }
            },
            readIn = "<p>Read this.</p>"
        });

    private async Task<HttpResponseMessage> PostDraft(Guid templateId, MselDraftDesign design)
    {
        var planner = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        return await Client(planner).PostAsJsonAsync("/api/msels/ai-draft", new MselDraftRequest { TemplateMselId = templateId, Prompt = "Cables", Design = design }, JsonOptions, Ct);
    }

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
    }

    // a template with team BLUE and a Title column
    private async Task<MselEntity> SeedTemplate(bool useCite)
    {
        var template = TestData.Msel(isTemplate: true);
        template.UseCite = useCite;
        var blue = TestData.Team(template.Id);
        blue.ShortName = "BLUE";
        await Seed(template, blue, TestData.DataField(template.Id, name: "Title", displayOrder: 1));
        return template;
    }
}
