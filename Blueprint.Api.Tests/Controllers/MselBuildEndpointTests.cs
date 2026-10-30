// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// Building a MSEL with AI in steps: a design and the planner's questions, the MSEL created from the reviewed design,
/// then its injects, each article and profile, and the finish, every step saved into the MSEL as it is written.
/// </summary>
public class MselBuildEndpointTests(DatabaseFixture fixture, AiEnabledFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<AiEnabledFactory>
{
    private readonly IAiModelClient _model = factory.Model;
    private readonly List<(string System, string Prompt, string Schema)> _calls = new();

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _model.ClearSubstitute();
        factory.Cite.ClearSubstitute();
        factory.Cite.GetTeamTypesAsync(Arg.Any<CancellationToken>()).Returns(new List<Cite.Api.Client.TeamType>());
        factory.Cite.GetScoringModelsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>()).Returns(new List<Cite.Api.Client.ScoringModel>());
        // the design and inject steps share the draft tool; the step's system prompt says which is asked for
        _model.InvokeToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Text.Json.Nodes.JsonObject>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var system = call.ArgAt<string>(0);
                _calls.Add((system, call.ArgAt<string>(1), call.ArgAt<System.Text.Json.Nodes.JsonObject>(4).ToJsonString()));
                var answer = call.ArgAt<string>(2) switch
                {
                    "submit_article" => """{"html":"<p>Cables cut, says the Herald.</p>"}""",
                    "submit_profile" => """{"html":"<h2>Security operations</h2><p>Watches the network.</p>"}""",
                    "submit_inject_text" => """{"html":"<p>Check in with each team lead.</p>"}""",
                    _ when system.Contains("first of two steps") => Design(),
                    _ => Injects()
                };
                return JsonDocument.Parse(answer).RootElement.Clone();
            });
    }

    /// <summary>The design step asks for the design and questions only, and saves nothing.</summary>
    [Fact]
    public async Task Design_ReturnsTheDesignAndTheQuestions()
    {
        var template = await SeedTemplate();
        var planner = await Planner();

        var result = await Read<MselDraftResult>(await Client(planner).PostAsJsonAsync("/api/msels/ai-draft/design", new MselDraftRequest { TemplateMselId = template.Id, Prompt = "Cables" }, JsonOptions, Ct));

        var schema = _calls.Single().Schema;
        Assert.Contains("\"questions\"", schema);
        Assert.DoesNotContain("\"injects\"", schema);
        Assert.Equal([("Q1", "Should the outage reach the hospital?", "Stop at the port")], result.Questions.Select(q => (q.Id, q.Question, q.Suggested)));
        Assert.Equal(["Start", "Next day"], result.Draft.Moves.Select(m => m.Description));
        Assert.Empty(result.Draft.Injects);
        Assert.DoesNotContain(result.Warnings, w => w.StartsWith("No discussion questions"));
        Assert.NotEqual(Guid.Empty, result.Draft.DraftId);
    }

    /// <summary>
    /// The MSEL is created from the reviewed design at once, with its moves, teams and cards, and no rows; it is marked as
    /// waiting for its injects, and keeps the design and the planner's answers to write them from.
    /// </summary>
    [Fact]
    public async Task Create_MakesTheMselFromTheDesign()
    {
        var (mselId, _) = await CreateFromDesign();

        await using var db = NewContext();
        var msel = await db.Msels.Include(m => m.Moves).Include(m => m.Teams).Include(m => m.ScenarioEvents).AsSplitQuery().SingleAsync(m => m.Id == mselId, Ct);
        Assert.Equal("Deep Bite", msel.Name);
        Assert.Equal(["Start", "Next day"], msel.Moves.OrderBy(m => m.MoveNumber).Select(m => m.Description));
        Assert.Contains(msel.Teams, t => t.ShortName == "SOC");
        Assert.Empty(msel.ScenarioEvents);
        Assert.Equal("Injects", msel.AiDraftStage);
        var state = MselBuildState.Deserialize(msel.AiDraft);
        Assert.Equal("Cables", state.Prompt);
        Assert.Equal("Stop at the port", state.Answers.Single().Answer);
    }

    /// <summary>
    /// The inject step writes to the approved design and the planner's answers, saves the rows, CITE tasks and read-in into
    /// the MSEL, and lists the articles and profiles still to write.
    /// </summary>
    [Fact]
    public async Task Injects_AreWrittenIntoTheMsel()
    {
        var (mselId, planner) = await CreateFromDesign();

        var status = await Read<MselBuildStatus>(await Client(planner).PostAsync($"/api/msels/{mselId}/ai-draft/injects", null, Ct));

        var injectsCall = _calls.Last();
        Assert.Contains("second of two steps", injectsCall.System);
        Assert.Contains("The approved design:", injectsCall.Prompt);
        Assert.Contains("- Should the outage reach the hospital?: Stop at the port", injectsCall.Prompt);
        Assert.Equal("Articles", status.Stage);
        Assert.Equal(["Cables cut"], status.Articles.Select(a => a.Name));
        Assert.Equal(["SOC"], status.Profiles.Select(p => p.Name));
        // the outline leaves out the long text, which each row then gets on its own
        Assert.DoesNotContain("\"Details\"", injectsCall.Schema);
        Assert.Contains("Check in", status.Injects.Select(i => i.Name));

        await using var db = NewContext();
        var msel = await db.Msels.Include(m => m.ScenarioEvents).Include(m => m.Pages).Include(m => m.Moves).AsSplitQuery().SingleAsync(m => m.Id == mselId, Ct);
        Assert.Equal(2, msel.ScenarioEvents.Count);
        Assert.Contains(msel.ScenarioEvents, e => e.IntegrationTarget == "Gallery");
        Assert.Contains(msel.Pages, p => p.Name == "Deep Bite Read-In");
        Assert.Equal("Articles", msel.AiDraftStage);
        var state = MselBuildState.Deserialize(msel.AiDraft);
        Assert.Equal(["Decide who leads"], state.Draft.Moves.First().ExpectedDecisions);
    }

    /// <summary>
    /// Each article and profile is written and saved on its own, and saved again with its graphics as PNG; the finish adds
    /// the pages and Player applications and marks the MSEL built.
    /// </summary>
    [Fact]
    public async Task ArticlesProfilesAndFinish_CompleteTheMsel()
    {
        var (mselId, planner) = await CreateFromDesign();
        var status = await Read<MselBuildStatus>(await Client(planner).PostAsync($"/api/msels/{mselId}/ai-draft/injects", null, Ct));
        var article = status.Articles.Single();
        var profile = status.Profiles.Single();

        var written = await Read<ArticleDraftResult>(await Client(planner).PostAsync($"/api/msels/{mselId}/ai-draft/articles/{article.Id}", null, Ct));
        Assert.Contains("Cables cut, says the Herald.", written.Html);
        var saved = await Client(planner).PutAsJsonAsync($"/api/msels/{mselId}/ai-draft/articles/{article.Id}", new MselBuildContent { Html = "<p>As PNG</p>" }, JsonOptions, Ct);
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        await Read<ProfileDraftResult>(await Client(planner).PostAsync($"/api/msels/{mselId}/ai-draft/profiles/{profile.Id}", null, Ct));
        foreach (var row in status.Injects)
            await Read<MselBuildContent>(await Client(planner).PostAsync($"/api/msels/{mselId}/ai-draft/rows/{row.Id}", null, Ct));
        var rowCall = _calls.Last(c => c.Prompt.Contains("Its outline:"));
        Assert.Contains("#1, 5, Inject, , : Cables cut", rowCall.Prompt);
        Assert.Contains("Write the Details of inject #2. Its outline:", rowCall.Prompt);
        var remaining = await Read<MselBuildStatus>(await Client(planner).GetAsync($"/api/msels/{mselId}/ai-draft", Ct));
        Assert.Empty(remaining.Articles);
        Assert.Empty(remaining.Profiles);
        Assert.Empty(remaining.Injects);

        var finished = await Read<MselBuildStatus>(await Client(planner).PostAsync($"/api/msels/{mselId}/ai-draft/finish", null, Ct));

        Assert.Null(finished.Stage);
        await using var db = NewContext();
        var msel = await db.Msels.Include(m => m.Pages).Include(m => m.Teams).Include(m => m.ScenarioEvents).ThenInclude(e => e.DataValues).AsSplitQuery().SingleAsync(m => m.Id == mselId, Ct);
        Assert.Null(msel.AiDraftStage);
        Assert.Contains(msel.Pages, p => p.Name == "Exercise Overview");
        Assert.Contains(msel.Pages, p => p.Name == "Facilitator Guide");
        Assert.Contains("Watches the network.", msel.Teams.Single(t => t.ShortName == "SOC").Description);
        Assert.Contains(msel.ScenarioEvents.SelectMany(e => e.DataValues), v => v.Value != null && v.Value.Contains("As PNG"));
        Assert.Contains(msel.ScenarioEvents.SelectMany(e => e.DataValues), v => v.Value != null && v.Value.Contains("Check in with each team lead."));
        Assert.Equal(HttpStatusCode.Conflict, (await Client(planner).PostAsync($"/api/msels/{mselId}/ai-draft/finish", null, Ct)).StatusCode);
    }

    /// <summary>Steps run in order, and only the drafting user, or an EditMsels holder, runs them.</summary>
    [Fact]
    public async Task Steps_RunInOrderForTheirOwner()
    {
        var (mselId, planner) = await CreateFromDesign();
        var stranger = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await Client(stranger).PostAsync($"/api/msels/{mselId}/ai-draft/injects", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Client(planner).PostAsync($"/api/msels/{mselId}/ai-draft/finish", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Client(planner).PostAsync($"/api/msels/{mselId}/ai-draft/articles/{Guid.NewGuid()}", null, Ct)).StatusCode);
    }

    private async Task<(Guid MselId, TestActor Planner)> CreateFromDesign()
    {
        var template = await SeedTemplate();
        var planner = await Planner();
        var design = await Read<MselDraftResult>(await Client(planner).PostAsJsonAsync("/api/msels/ai-draft/design", new MselDraftRequest { TemplateMselId = template.Id, Prompt = "Cables" }, JsonOptions, Ct));
        var build = new MselDesignBuild
        {
            TemplateMselId = template.Id,
            Prompt = "Cables",
            Draft = design.Draft,
            Answers = design.Questions.Select(q => new MselDesignAnswer { Question = q.Question, Answer = q.Suggested }).ToList()
        };
        var created = await Read<MselImportResult>(await Client(planner).PostAsJsonAsync("/api/msels/ai-draft/create", build, JsonOptions, Ct));
        return (created.Msel.Id, planner);
    }

    private Task<TestActor> Planner() => Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

    // a Gallery template with team EXCON and the Title column
    private async Task<MselEntity> SeedTemplate()
    {
        var template = TestData.Msel(isTemplate: true);
        template.UseGallery = true;
        var excon = TestData.Team(template.Id);
        excon.ShortName = "EXCON";
        await Seed(template, excon, TestData.DataField(template.Id, name: "Title", displayOrder: 1));
        return template;
    }

    private static string Design() =>
        JsonSerializer.Serialize(new
        {
            name = "Deep Bite",
            description = "Cables are cut.",
            durationMinutes = 120,
            scenario = new { problem = "Two cables are cut.", premise = "An anchor dragged.", environment = "Island state", escalationCeiling = "No loss of life" },
            objectives = new[] { new { id = "O1", task = "Lead the response", condition = "Outages", standard = "A lead in 30 minutes" } },
            moves = new[]
            {
                new { moveNumber = 0, description = "Start", situationDescription = "Outage", startMinutes = 0, situationTime = "2026-03-04T08:00:00Z", purpose = "Choose a lead" },
                new { moveNumber = 1, description = "Next day", situationDescription = "Press", startMinutes = 60, situationTime = "2026-03-05T09:00:00Z", purpose = "Pressure" }
            },
            teams = new[] { new { name = "Security operations", shortName = "SOC", description = "Watches the network." } },
            organizations = Array.Empty<object>(),
            cards = new[] { new { name = "Exercise Control", description = "Read-in" } },
            questions = new[] { new { question = "Should the outage reach the hospital?", why = "Sets the stakes", suggested = "Stop at the port" } }
        });

    private static string Injects() =>
        JsonSerializer.Serialize(new
        {
            moves = new[] { new { moveNumber = 0, expectedDecisions = new[] { "Decide who leads" } } },
            injects = new object[]
            {
                new { minutes = 5, eventType = "Inject", card = "Exercise Control", values = new Dictionary<string, string> { ["Title"] = "Cables cut", ["SourceType"] = "News" } },
                new { minutes = 70, eventType = "Facilitation", values = new Dictionary<string, string> { ["Title"] = "Check in" } }
            },
            readIn = "<p>Read this.</p>"
        });

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
    }
}
