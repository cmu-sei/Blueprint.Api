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
/// The parts of an AI draft that set the scene: each move's time on the scenario clock, the participants' read-in,
/// and with CITE, what each team should do in each move.
/// </summary>
public class MselDraftScenarioEndpointTests(DatabaseFixture fixture, AiEnabledFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<AiEnabledFactory>
{
    private readonly IAiModelClient _model = factory.Model;
    private string _system;
    private string _schema;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _model.ClearSubstitute();
    }

    /// <summary>
    /// The model is told about the two clocks and asked for a read-in; scenario times are read as UTC, and one
    /// that is not a time or goes backwards is left blank and reported.
    /// </summary>
    [Fact]
    public async Task Draft_KeepsTheScenarioClockInOrderAndPutsTheReadInOnPaper()
    {
        var template = await SeedTemplate(useCite: false);
        Answer(Draft());

        var result = await Read<MselDraftResult>(await PostDraft(template.Id));

        Assert.Contains("two clocks", _system);
        Assert.Contains("\"situationTime\"", _schema);
        Assert.Contains("\"readIn\"", _schema);
        Assert.Equal(["2026-03-04T08:00:00.000Z", "2026-03-05T09:00:00.000Z", null, null], result.Draft.Moves.Select(m => m.SituationTime));
        Assert.Contains(result.Warnings, w => w == "Move 2's scenario time is earlier than the move before; left blank.");
        Assert.Contains(result.Warnings, w => w == "Move 3's scenario time \"soon\" is not a date and time; left blank.");
        Assert.StartsWith("<table", result.Draft.ReadIn);
        Assert.Contains("<h2>Background</h2>", result.Draft.ReadIn);
        Assert.DoesNotContain("<script", result.Draft.ReadIn);
        Assert.Contains(result.Warnings, w => w.StartsWith("Read-in: Removed 1 script"));
    }

    /// <summary>Without CITE the model is not asked for actions, and any it sends are dropped.</summary>
    [Fact]
    public async Task Draft_WithoutCite_HasNoActions()
    {
        var template = await SeedTemplate(useCite: false);
        Answer(Draft());

        var result = await Read<MselDraftResult>(await PostDraft(template.Id));

        Assert.DoesNotContain("\"citeActions\"", _schema);
        Assert.Empty(result.Draft.CiteActions);
    }

    /// <summary>With CITE, actions for a team or move the draft lacks, without a description, or repeated are dropped.</summary>
    [Fact]
    public async Task Draft_WithCite_KeepsActionsForKnownTeamsAndMoves()
    {
        var template = await SeedTemplate(useCite: true);
        Answer(Draft());

        var result = await Read<MselDraftResult>(await PostDraft(template.Id));

        Assert.Contains("\"citeActions\"", _schema);
        Assert.Equal(
            [(0, "BLUE", "Open an incident ticket"), (0, "BLUE", "Brief leadership"), (1, "SOC", "Isolate the affected hosts")],
            result.Draft.CiteActions.Select(a => (a.MoveNumber, a.Team, a.Description)));
        Assert.Contains(result.Warnings, w => w == "Dropped 4 CITE actions with an unknown team or move, no description, or a repeat.");
    }

    /// <summary>
    /// Import keeps each move's scenario time; adds the read-in and the organization and team profiles as pages
    /// participants can see and the playbook includes, on white paper; and numbers the actions after the template's,
    /// noting the teams CITE has no type for.
    /// </summary>
    [Fact]
    public async Task Import_AddsTheReadInPageSituationTimesAndCiteActions()
    {
        var template = await SeedTemplate(useCite: true);
        var draft = JsonSerializer.Deserialize<MselDraft>(Draft(), JsonOptions);
        var planner = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(planner).PostAsJsonAsync("/api/msels/ai-draft/import", new MselDraftImport { TemplateMselId = template.Id, Draft = draft }, JsonOptions, Ct);

        var imported = await Read<MselImportResult>(response);
        var mselId = imported.Msel.Id;
        await using var db = NewContext();
        var moves = await db.Moves.Where(m => m.MselId == mselId).OrderBy(m => m.MoveNumber).ToListAsync(Ct);
        Assert.Equal(
            [new DateTime(2026, 3, 4, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 3, 5, 9, 0, 0, DateTimeKind.Utc), null, null],
            moves.Select(m => m.SituationTime?.ToUniversalTime()));

        var pages = await db.MselPages.Where(p => p.MselId == mselId).OrderBy(p => p.Name).ToListAsync(Ct);
        // no facilitator guide: the draft has no objectives, move purposes or expected actions
        Assert.Equal(["Deep Bite Read-In", "Exercise Overview", "Organization Profiles", "Read-In", "Team Profiles"], pages.Select(p => p.Name));
        Assert.All(pages.Where(p => p.Name != "Read-In"), p =>
        {
            Assert.True(p.AllCanView);
            Assert.True(p.IncludeInPlaybook);
            Assert.StartsWith("<table", p.Content);
        });
        Assert.Contains("<h2>Background</h2>", pages[0].Content);
        Assert.Contains("<h2>Organization Profiles</h2>", pages[2].Content);
        Assert.Contains("<article><h3>Harbor Daily (NEWS)</h3><p><strong>Type:</strong> Local paper</p></article>", pages[2].Content);
        Assert.Contains("<article><h3>Security operations (SOC)</h3><p>Watches the network &amp; the hosts.</p></article>", pages[4].Content);

        var actions = await db.CiteActions.Include(a => a.Team).Where(a => a.MselId == mselId)
            .OrderBy(a => a.MoveNumber).ThenBy(a => a.Team.ShortName).ThenBy(a => a.ActionNumber).ToListAsync(Ct);
        Assert.Equal(
            [(0, "BLUE", 1, "Template action"), (0, "BLUE", 2, "Open an incident ticket"), (0, "BLUE", 3, "Brief leadership"), (1, "SOC", 1, "Isolate the affected hosts")],
            actions.Select(a => (a.MoveNumber, a.Team.ShortName, a.ActionNumber, a.Description)));
        Assert.Contains(imported.Changes, c => c.Message == "Give BLUE, SOC a CITE team type, or CITE will not get their actions.");
        // the template keeps only its own
        Assert.Equal(1, await db.CiteActions.CountAsync(a => a.MselId == template.Id, Ct));
        Assert.Equal(1, await db.MselPages.CountAsync(p => p.MselId == template.Id, Ct));
    }

    /// <summary>
    /// An article dated outside its move's stretch of the scenario clock, or not dated, is dated as far into the move's
    /// scenario time as it is into the move's exercise time; one dated inside its move keeps its date.
    /// </summary>
    [Fact]
    public async Task Draft_WithGallery_DatesEachArticleWithinItsMove()
    {
        var template = await SeedTemplate(useCite: false, useGallery: true);
        Answer(Draft());

        var result = await Read<MselDraftResult>(await PostDraft(template.Id));

        Assert.Equal(
            ["2026-03-04T10:00:00.000Z", "2026-03-04T08:20:00.000Z", "2026-03-05T09:10:00.000Z"],
            result.Draft.Injects.Select(i => i.Values["Simulated Time"]));
        Assert.Contains(result.Warnings, w => w == "Moved Simulated Time into the move's scenario time for articles in move 0.");
        Assert.Contains(result.Warnings, w => w == "Dated 1 undated article by its move's scenario time.");
    }

    private void Answer(string json) =>
        _model.InvokeToolAsync(default, default, default, default, default, default)
            .ReturnsForAnyArgs(callInfo =>
            {
                _system = callInfo.ArgAt<string>(0);
                _schema = callInfo.ArgAt<System.Text.Json.Nodes.JsonObject>(4).ToJsonString();
                return JsonDocument.Parse(json).RootElement.Clone();
            });

    // move 2 goes back in scenario time and move 3's time is not a time; four of the seven actions are bad
    private static string Draft() =>
        JsonSerializer.Serialize(new
        {
            name = "Deep Bite",
            description = "Cables are cut.",
            durationMinutes = 120,
            moves = new[]
            {
                new { moveNumber = 0, description = "Start", situationDescription = "Calm", startMinutes = 0, situationTime = "2026-03-04T08:00:00Z" },
                new { moveNumber = 1, description = "Next day", situationDescription = "Outage", startMinutes = 30, situationTime = "2026-03-05T09:00:00Z" },
                new { moveNumber = 2, description = "Back", situationDescription = "Odd", startMinutes = 60, situationTime = "2026-03-01T09:00:00Z" },
                new { moveNumber = 3, description = "Later", situationDescription = "Recovery", startMinutes = 90, situationTime = "soon" }
            },
            teams = new[] { new { name = "Security operations", shortName = "SOC", description = "Watches the network & the hosts." } },
            organizations = new[] { new { name = "Harbor Daily", shortName = "NEWS", description = "<p><strong>Type:</strong> Local paper</p>" } },
            injects = new object[]
            {
                // move 0 runs from 3/4 08:00 to 3/5 09:00 on the scenario clock
                new { minutes = 10, eventType = "Inject", values = new Dictionary<string, string> { ["Title"] = "Cable cut", ["Simulated Time"] = "2026-03-04T10:00:00Z" } },
                new { minutes = 20, eventType = "Inject", values = new Dictionary<string, string> { ["Title"] = "Too late", ["Simulated Time"] = "2026-03-06T10:00:00Z" } },
                // move 1 opens 3/5 09:00 at minute 30
                new { minutes = 40, eventType = "Inject", values = new Dictionary<string, string> { ["Title"] = "Undated", ["SourceType"] = "News" } }
            },
            readIn = "<h2>Background</h2><p>The cables run under the strait.</p><script>alert(1)</script>",
            citeActions = new[]
            {
                new { moveNumber = 0, team = "blue", description = "Open an incident ticket" },
                new { moveNumber = 0, team = "BLUE", description = "Brief leadership" },
                new { moveNumber = 0, team = "BLUE", description = "open an incident ticket" },
                new { moveNumber = 1, team = "SOC", description = "Isolate the affected hosts" },
                new { moveNumber = 1, team = "RED", description = "Not a team" },
                new { moveNumber = 9, team = "SOC", description = "Not a move" },
                new { moveNumber = 1, team = "SOC", description = " " }
            }
        });

    private async Task<HttpResponseMessage> PostDraft(Guid templateId)
    {
        var planner = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        return await Client(planner).PostAsJsonAsync("/api/msels/ai-draft", new MselDraftRequest { TemplateMselId = templateId, Prompt = "Cables" }, JsonOptions, Ct);
    }

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
    }

    // a template with team BLUE, a Title column, its own Read-In page, and with CITE one action for BLUE in move 0
    private async Task<MselEntity> SeedTemplate(bool useCite, bool useGallery = false)
    {
        var template = TestData.Msel(isTemplate: true);
        template.UseCite = useCite;
        template.UseGallery = useGallery;
        var blue = TestData.Team(template.Id);
        blue.ShortName = "BLUE";
        var title = TestData.DataField(template.Id, name: "Title", displayOrder: 1);
        var page = TestData.MselPage(template.Id, name: "Read-In");
        var action = TestData.CiteAction(template.Id, blue.Id, moveNumber: 0, actionNumber: 1, description: "Template action");
        await Seed(template, blue, title, page, action);
        return template;
    }
}
