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

    /// <summary>
    /// Import adds an Exercise Overview participants see, a Facilitator Guide only exercise control sees, and in
    /// Player a Read-In application on the read-in page and an Exercise Timeline, for every team.
    /// </summary>
    [Fact]
    public async Task Import_AddsTheOverviewFacilitatorGuideAndPlayerApplications()
    {
        var template = await SeedTemplate(useCite: false, usePlayer: true);
        var draft = JsonSerializer.Deserialize<MselDraft>(Draft(), JsonOptions);
        draft.Design = new MselDraftDesign { Format = "Discussion", ProfilePages = false };
        var planner = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(planner).PostAsJsonAsync("/api/msels/ai-draft/import", new MselDraftImport { TemplateMselId = template.Id, Draft = draft }, JsonOptions, Ct);

        var mselId = (await Read<MselImportResult>(response)).Msel.Id;
        await using var db = NewContext();
        var pages = await db.MselPages.Where(p => p.MselId == mselId).ToDictionaryAsync(p => p.Name, Ct);
        Assert.Equal(["Deep Bite Read-In", "Exercise Overview", "Facilitator Guide"], pages.Keys.Order());

        var overview = pages["Exercise Overview"];
        Assert.True(overview.AllCanView);
        Assert.Contains("<h2>Deep Bite: Exercise Overview</h2>", overview.Content);
        Assert.Contains("A discussion-based tabletop exercise", overview.Content);
        Assert.Contains("<td valign=\"top\">Lead the response</td><td valign=\"top\">With partial outages</td>", overview.Content);
        Assert.Contains("<b>Security operations (SOC)</b>", overview.Content);

        var guide = pages["Facilitator Guide"];
        Assert.False(guide.AllCanView);
        Assert.False(guide.IncludeInPlaybook);
        Assert.Contains("<b>Escalation ceiling:</b> No loss of life", guide.Content);
        Assert.Contains("<h3>Move 0: Start</h3>", guide.Content);
        Assert.Contains("<li>Decide who leads the response</li>", guide.Content);
        Assert.Contains("<b>Controller notes:</b> Hold the vendor call until they ask.", guide.Content);
        Assert.Contains("<td valign=\"top\">Outage</td><td valign=\"top\">Open an incident and assign a lead</td><td valign=\"top\">O1 Lead the response</td>", guide.Content);
        Assert.Contains("<h3>After-action review</h3>", guide.Content);

        var apps = await db.PlayerApplications.Include(a => a.PlayerApplicationTeams).Where(a => a.MselId == mselId).ToDictionaryAsync(a => a.Name, Ct);
        Assert.Equal($"{{blueprintUrl}}/mselpage/{pages["Deep Bite Read-In"].Id}{{theme}}", apps["Read-In"].Url);
        Assert.Equal("{blueprintUrl}/msel/{blueprintMselId}/view{theme}", apps["Exercise Timeline"].Url);
        var teams = await db.Teams.Where(t => t.MselId == mselId).Select(t => t.Id).OrderBy(id => id).ToListAsync(Ct);
        Assert.Equal(2, teams.Count);
        Assert.Equal(teams, apps["Read-In"].PlayerApplicationTeams.Select(t => t.TeamId).Order());
        Assert.Equal(teams, apps["Exercise Timeline"].PlayerApplicationTeams.Select(t => t.TeamId).Order());
    }

    /// <summary>
    /// The push sends only rows whose integration target names the application: Gallery articles get Gallery; with
    /// Steamfitter, STARTEX and ENDEX rows carry a Player notification each.
    /// </summary>
    [Fact]
    public async Task Import_TargetsArticlesAtGalleryAndNotifiesAtStartexAndEndex()
    {
        var template = await SeedTemplate(useCite: false, useGallery: true, useSteamfitter: true);
        var draft = JsonSerializer.Deserialize<MselDraft>(Draft(), JsonOptions);
        draft.Injects[0].Values["SourceType"] = "News";
        var planner = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(planner).PostAsJsonAsync("/api/msels/ai-draft/import", new MselDraftImport { TemplateMselId = template.Id, Draft = draft }, JsonOptions, Ct);

        var mselId = (await Read<MselImportResult>(response)).Msel.Id;
        await using var db = NewContext();
        var title = await db.DataFields.SingleAsync(f => f.MselId == mselId && f.Name == "Title", Ct);
        var events = await db.ScenarioEvents.Include(e => e.DataValues).Include(e => e.SteamfitterTask)
            .Where(e => e.MselId == mselId).OrderBy(e => e.DeltaSeconds).ThenBy(e => e.GroupOrder).ToListAsync(Ct);
        string Title(ScenarioEventEntity e) => e.DataValues.Single(v => v.DataFieldId == title.Id).Value;
        Assert.Equal(["STARTEX", "Outage", "Press call", "ENDEX"], events.Select(Title));
        Assert.Equal(["Steamfitter", "Gallery", null, "Steamfitter"], events.Select(e => e.IntegrationTarget));
        Assert.Equal([0, 5 * 60, 70 * 60, 120 * 60], events.Select(e => e.DeltaSeconds));
        var startex = events[0].SteamfitterTask;
        Assert.Equal(SteamfitterIntegrationType.Notification, startex.TaskType);
        Assert.Equal(SteamfitterTaskTrigger.Time, startex.TriggerCondition);
        Assert.Equal("STARTEX notification", startex.Name);
        Assert.StartsWith("STARTEX: the exercise has started.", startex.ActionParameters["notificationText"]);
        Assert.StartsWith("ENDEX:", events[3].SteamfitterTask.ActionParameters["notificationText"]);
        Assert.Null(events[1].SteamfitterTask);
    }

    /// <summary>Copying a MSEL keeps a Player application that links to one of its pages pointing at the copy's page.</summary>
    [Fact]
    public async Task Copy_PointsAPageApplicationAtTheCopysPage()
    {
        var msel = TestData.Msel();
        var page = TestData.MselPage(msel.Id, name: "Read-In");
        var app = TestData.PlayerApplication(msel.Id, name: "Read-In", url: $"{{blueprintUrl}}/mselpage/{page.Id}{{theme}}");
        await Seed(msel, page, app);
        var planner = await Actor().WithSystemPermissions(SystemPermission.CreateMsels, SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(planner).PostAsync($"/api/msels/{msel.Id}/copy", null, Ct);

        var copy = await Read<Msel>(response);
        await using var db = NewContext();
        var copiedPage = await db.MselPages.SingleAsync(p => p.MselId == copy.Id, Ct);
        var copiedApp = await db.PlayerApplications.SingleAsync(a => a.MselId == copy.Id, Ct);
        Assert.NotEqual(page.Id, copiedPage.Id);
        Assert.Equal($"{{blueprintUrl}}/mselpage/{copiedPage.Id}{{theme}}", copiedApp.Url);
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
    private async Task<MselEntity> SeedTemplate(bool useCite, bool usePlayer = false, bool useGallery = false, bool useSteamfitter = false)
    {
        var template = TestData.Msel(isTemplate: true);
        template.UseCite = useCite;
        template.UsePlayer = usePlayer;
        template.UseGallery = useGallery;
        template.UseSteamfitter = useSteamfitter;
        var blue = TestData.Team(template.Id);
        blue.ShortName = "BLUE";
        await Seed(template, blue, TestData.DataField(template.Id, name: "Title", displayOrder: 1));
        return template;
    }
}
