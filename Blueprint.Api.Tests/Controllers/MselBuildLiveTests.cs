// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// A host with AI drafting on and the real model client, for the live run below. The model and region come from
/// BLUEPRINT_LIVE_BEDROCK_MODEL and BLUEPRINT_LIVE_BEDROCK_REGION; credentials from the default AWS chain.
/// </summary>
public class LiveBedrockFactory(DatabaseFixture database) : BlueprintAppFactory(database)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Ai:Enabled", "true");
        builder.UseSetting("Ai:ModelId", Environment.GetEnvironmentVariable("BLUEPRINT_LIVE_BEDROCK_MODEL") ?? "us.anthropic.claude-sonnet-5-5");
        builder.UseSetting("Ai:Region", Environment.GetEnvironmentVariable("BLUEPRINT_LIVE_BEDROCK_REGION") ?? "us-east-1");
        builder.UseSetting("Ai:Prices:us.anthropic.claude-sonnet-5-5:InputPerMillion", "2");
        builder.UseSetting("Ai:Prices:us.anthropic.claude-sonnet-5-5:OutputPerMillion", "10");
        builder.UseSetting("Ai:Prices:us.anthropic.claude-sonnet-5-5:CacheReadPerMillion", "0.2");
        if (Environment.GetEnvironmentVariable("BLUEPRINT_LIVE_STRICT") is string strict && strict.Length > 0)
            builder.UseSetting("Ai:Strict", strict);
    }
}

/// <summary>
/// A whole drafted MSEL built against the real model, step by step through the endpoints the browser calls: the design,
/// the MSEL created from it, the inject outline, then some rows, articles and a profile. Each step's time and the calls'
/// tokens and cost are written to BLUEPRINT_LIVE_OUT. It costs real money, so it runs only where BLUEPRINT_LIVE_BEDROCK is set.
/// </summary>
public class MselBuildLiveTests(DatabaseFixture fixture, LiveBedrockFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<LiveBedrockFactory>
{
    private const string Prompt =
        "A four-move discussion tabletop for a regional hospital's incident response team, a state National Guard cyber team and a " +
        "state coordination cell. A fictional ransomware group gets in through a managed service provider, encrypts the hospital's " +
        "records system, and is then seen at the water utility the provider also serves. Teams must contain it, keep patients safe, " +
        "decide how to use the Guard, and brief the governor's office. Two hours of play over three days of scenario time.";

    [Fact]
    public async Task Live_BuildsAMselStepByStep()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BLUEPRINT_LIVE_BEDROCK")), "BLUEPRINT_LIVE_BEDROCK is not set.");
        factory.Cite.GetTeamTypesAsync(Arg.Any<CancellationToken>()).Returns(new List<Cite.Api.Client.TeamType>());
        var report = new StringBuilder();
        var template = await SeedTemplate();
        // a framework the size of a real one, so the prompt is as long as a planner's
        var framework = TestData.CompetencyFramework();
        var competencies = Enumerable.Range(1, int.Parse(Environment.GetEnvironmentVariable("BLUEPRINT_LIVE_COMPETENCIES") ?? "1500"))
            .Select(i => { var c = TestData.Competency(framework.Id, idNumber: $"T{i:0000}"); c.ShortName = $"Task {i}: respond to incident activity of kind {i % 37}"; return c; })
            .ToList();
        await Seed([framework, .. competencies]);
        report.AppendLine($"strict: {Environment.GetEnvironmentVariable("BLUEPRINT_LIVE_STRICT") ?? "default (on)"}, competencies: {competencies.Count}");
        var planner = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var client = Client(planner);
        client.Timeout = TimeSpan.FromMinutes(20);
        var design = new MselDraftDesign { Format = "Discussion", CiteAssessment = "QuestionsByMove", Moves = 4, DurationMinutes = 120, ProfilePages = true };

        var designed = await Step(report, "design", () => client.PostAsJsonAsync("/api/msels/ai-draft/design",
            new MselDraftRequest { TemplateMselId = template.Id, Prompt = Prompt, Design = design, CompetencyFrameworkIds = [framework.Id] }, JsonOptions, Ct), r => Read<MselDraftResult>(r));
        report.AppendLine($"  {designed.Draft.Moves.Count} moves, {designed.Draft.Teams.Count} teams, {designed.Draft.Organizations.Count} organizations, " +
            $"{designed.Draft.Cards.Count} cards, {designed.Questions.Count} questions; warnings: {string.Join(" | ", designed.Warnings)}");
        Assert.True(designed.Draft.Moves.Count >= 4, report.ToString());
        Assert.NotEmpty(designed.Draft.Teams);

        var created = await Step(report, "create", () => client.PostAsJsonAsync("/api/msels/ai-draft/create", new MselDesignBuild
        {
            TemplateMselId = template.Id,
            Prompt = Prompt,
            Draft = designed.Draft,
            CompetencyFrameworkIds = [framework.Id],
            Answers = designed.Questions.Select(q => new MselDesignAnswer { Question = q.Question, Answer = q.Suggested }).ToList()
        }, JsonOptions, Ct), r => Read<MselImportResult>(r));
        var mselId = created.Msel.Id;

        var status = await Step(report, "outline", () => client.PostAsync($"/api/msels/{mselId}/ai-draft/injects", null, Ct), r => Read<MselBuildStatus>(r));
        report.AppendLine($"  {status.Injects.Count} rows, {status.Articles.Count} articles, {status.Profiles.Count} profiles to write; warnings: {string.Join(" | ", status.Warnings)}");
        Assert.NotEmpty(status.Articles);

        // a sample of the writing stage, at once as the browser writes them
        var sample = status.Injects.Take(2).Select(i => ($"/api/msels/{mselId}/ai-draft/rows/{i.Id}", "row"))
            .Concat(status.Articles.Take(3).Select(a => ($"/api/msels/{mselId}/ai-draft/articles/{a.Id}", "article")))
            .Concat(status.Profiles.Take(1).Select(p => ($"/api/msels/{mselId}/ai-draft/profiles/{p.Id}", "profile")))
            .ToList();
        var watch = Stopwatch.StartNew();
        var results = await Task.WhenAll(sample.Select(async s =>
        {
            var item = Stopwatch.StartNew();
            var response = await client.PostAsync(s.Item1, null, Ct);
            return (s.Item2, response.IsSuccessStatusCode, item.Elapsed, response.IsSuccessStatusCode ? "" : await response.Content.ReadAsStringAsync(Ct));
        }));
        report.AppendLine($"writing {sample.Count} at once: {watch.Elapsed.TotalSeconds:F0} s");
        foreach (var r in results)
            report.AppendLine($"  {r.Item1}: {(r.Item2 ? "ok" : "FAILED " + r.Item4[..Math.Min(300, r.Item4.Length)])} in {r.Item3.TotalSeconds:F0} s");

        await using var db = NewContext();
        var usage = await db.AiUsages.Where(u => u.MselId == mselId || u.DraftId == designed.Draft.DraftId).ToListAsync(Ct);
        report.AppendLine("calls by step (input / output tokens, stop reasons, cost at $2/$10 per million):");
        foreach (var g in usage.GroupBy(u => u.Operation).OrderBy(g => g.Key))
            report.AppendLine($"  {g.Key}: {g.Count()} calls, {g.Sum(u => u.InputTokens):N0} / {g.Sum(u => u.OutputTokens):N0}, " +
                $"{string.Join(",", g.Select(u => u.StopReason).Distinct())}, ${(g.Sum(u => u.InputTokens) * 2m + g.Sum(u => u.OutputTokens) * 10m) / 1_000_000m:F2}");

        // what a constrained answer once left out
        var msel = await db.Msels.Include(m => m.Cards).Include(m => m.CiteActions).Include(m => m.CiteDuties).AsSplitQuery().SingleAsync(m => m.Id == mselId, Ct);
        report.AppendLine($"MSEL: {msel.Cards.Count} cards, {msel.CiteActions.Count} CITE tasks, {msel.CiteDuties.Count} duties; " +
            $"metadata: level {msel.EducationalLevel}, use {msel.EducationalUse}, mode {msel.CourseMode}, language {msel.Language}, subject {msel.Subject}, keywords {msel.Keywords}");
        if (Environment.GetEnvironmentVariable("BLUEPRINT_LIVE_OUT") is string path && path.Length > 0)
            await System.IO.File.WriteAllTextAsync(path, report.ToString(), Ct);
        Assert.All(results, r => Assert.True(r.Item2, report.ToString()));
        Assert.True(msel.Cards.Count > 1, report.ToString());
        Assert.NotEmpty(msel.CiteActions);
        Assert.NotEmpty(msel.CiteDuties);
        Assert.False(string.IsNullOrWhiteSpace(msel.Language), report.ToString());
        Assert.False(string.IsNullOrWhiteSpace(msel.Subject), report.ToString());
    }

    private static async Task<T> Step<T>(StringBuilder report, string name, Func<Task<HttpResponseMessage>> send, Func<HttpResponseMessage, Task<T>> read)
    {
        var watch = Stopwatch.StartNew();
        var response = await send();
        report.AppendLine($"{name}: {(int)response.StatusCode} in {watch.Elapsed.TotalSeconds:F0} s");
        if (!response.IsSuccessStatusCode)
            report.AppendLine("  " + await response.Content.ReadAsStringAsync());
        return await read(response);
    }

    // a Gallery and CITE template with exercise control and a Title column; the draft adds the rest of the Gallery columns
    private async Task<MselEntity> SeedTemplate()
    {
        var template = TestData.Msel(isTemplate: true);
        template.UseGallery = true;
        template.UseCite = true;
        var excon = TestData.Team(template.Id);
        excon.ShortName = "EXCON";
        excon.Name = "Exercise Control";
        await Seed(template, excon, TestData.DataField(template.Id, name: "Title", displayOrder: 1));
        return template;
    }

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
    }
}
