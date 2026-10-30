// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// What building each MSEL with AI used: every model call is recorded against its MSEL, or its draft until the draft
/// becomes a MSEL, and the totals are priced from the Ai:Prices setting.
/// </summary>
public class AiUsageEndpointTests(DatabaseFixture fixture, AiEnabledFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<AiEnabledFactory>
{
    private readonly IAiModelClient _model = factory.Model;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _model.ClearSubstitute();
    }

    /// <summary>The usage a Messages API answer reports is read whole, a refusal included; an image call counts one image.</summary>
    [Fact]
    public void Read_TakesTheTokensAndStopReasonFromTheAnswer()
    {
        var answer = JsonDocument.Parse("""{"stop_reason":"refusal","usage":{"input_tokens":45565,"output_tokens":39573,"cache_read_input_tokens":12,"cache_creation_input_tokens":34}}""").RootElement;

        var usage = AiUsageRecorder.Read("test-model", answer, 0);
        var photo = AiUsageRecorder.Read("test-image", default, 1);

        Assert.Equal((45565, 39573, 12, 34, "refusal"), (usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, usage.CacheWriteTokens, usage.StopReason));
        Assert.Equal((0, 1), (photo.InputTokens, photo.Images));
    }

    /// <summary>A call is recorded with what the request said it was for, a photo as a photo whatever the request.</summary>
    [Fact]
    public async Task Record_SavesTheCallAgainstTheRequestsMselOrDraft()
    {
        var draftId = Guid.NewGuid();
        // the recorder runs inside a request, whose test database the session header names
        var accessor = Factory.Services.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>();
        var request = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        request.Request.Headers[TestDatabaseScope.HeaderName] = SessionId;
        accessor.HttpContext = request;
        using (var scope = Factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IAiUsageContext>().Use("article", draftId: draftId);
            var recorder = scope.ServiceProvider.GetRequiredService<IAiUsageRecorder>();
            await recorder.RecordAsync("test-model", JsonDocument.Parse("""{"stop_reason":"tool_use","usage":{"input_tokens":100,"output_tokens":50}}""").RootElement, 0, Ct);
            await recorder.RecordAsync("test-image", default, 1, Ct);
        }
        accessor.HttpContext = null;

        await using var db = NewContext();
        var rows = await db.AiUsages.Where(u => u.DraftId == draftId).OrderBy(u => u.Operation).ToListAsync(Ct);
        Assert.Equal([("article", 100, 0), ("photo", 0, 1)], rows.Select(r => (r.Operation, r.InputTokens, r.Images)));
        Assert.All(rows, r => Assert.Null(r.MselId));
    }

    /// <summary>The MSEL's totals and each operation's, priced where every model has a price, for its owner.</summary>
    [Fact]
    public async Task GetByMsel_TotalsAndPricesTheMselsCalls()
    {
        var msel = TestData.Msel();
        await Seed(msel,
            Usage(msel.Id, "draft", "test-model", 45565, 39573),
            Usage(msel.Id, "article", "test-model", 2000, 3000),
            Usage(msel.Id, "article", "test-model", 1000, 1000, stopReason: "refusal"),
            Usage(msel.Id, "photo", "test-image", images: 1));
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var summary = await Read<AiUsageSummary>(await Client(owner).GetAsync($"/api/msels/{msel.Id}/ai-usage", Ct));

        Assert.Equal((4, 1, 48565L, 43573L, 1), (summary.Calls, summary.Refused, summary.InputTokens, summary.OutputTokens, summary.Images));
        // 48,565 × $2 + 43,573 × $10 per million, and one image at $0.04
        Assert.Equal(572_860, summary.EstimatedCostMicros);
        Assert.Empty(summary.Unpriced);
        Assert.Equal(["article", "draft", "photo"], summary.ByOperation.Select(o => o.Name));
        // 3,000 × $2 + 4,000 × $10 per million, the refused call billed too
        Assert.Equal(46_000, summary.ByOperation[0].EstimatedCostMicros);
    }

    /// <summary>A model without a price is left out of the estimate, which totals the rest, and is named so it can be priced.</summary>
    [Fact]
    public async Task GetByMsel_WithAnUnpricedModel_EstimatesThePricedCalls()
    {
        var msel = TestData.Msel();
        await Seed(msel, Usage(msel.Id, "draft", "test-model", 1000, 1000), Usage(msel.Id, "assist", "other-model", 1000, 1000));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var summary = await Read<AiUsageSummary>(await Client(actor).GetAsync($"/api/msels/{msel.Id}/ai-usage", Ct));

        // 1,000 tokens in at $2 and 1,000 out at $10 a million
        Assert.Equal(12_000, summary.EstimatedCostMicros);
        Assert.Equal(["other-model"], summary.Unpriced);
        Assert.Null(summary.ByOperation.Single(o => o.Name == "assist").EstimatedCostMicros);
        Assert.NotNull(summary.ByOperation.Single(o => o.Name == "draft").EstimatedCostMicros);
    }

    /// <summary>What an exercise cost is not for its participants or editors, only its owner and ViewMsels holders.</summary>
    [Theory]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Viewer)]
    public async Task GetByMsel_WithoutOwnership_Is403(MselRole role)
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/ai-usage", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Every MSEL's totals, most expensive first, with ViewMsels only.</summary>
    [Fact]
    public async Task GetAll_ListsEachMselMostExpensiveFirst()
    {
        var cheap = TestData.Msel();
        var dear = TestData.Msel();
        await Seed(cheap, dear, Usage(cheap.Id, "draft", "test-model", 1000, 1000), Usage(dear.Id, "draft", "test-model", 100000, 100000));
        var admin = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();
        var creator = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var all = await Read<List<AiUsageSummary>>(await Client(admin).GetAsync("/api/ai-usage", Ct));

        var mine = all.Where(s => s.MselId == cheap.Id || s.MselId == dear.Id).ToList();
        Assert.Equal([dear.Id, cheap.Id], mine.Select(s => s.MselId));
        Assert.Equal(dear.Name, mine[0].MselName);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(creator).GetAsync("/api/ai-usage", Ct)).StatusCode);
    }

    /// <summary>
    /// A drafted MSEL takes over the calls its draft made, so what writing it cost counts toward it; another user's
    /// calls under the same draft ID are not taken.
    /// </summary>
    [Fact]
    public async Task Import_TakesOverTheDraftsCalls()
    {
        var template = TestData.Msel(isTemplate: true);
        await Seed(template, TestData.DataField(template.Id, name: "Title", displayOrder: 1));
        var planner = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var draftId = Guid.NewGuid();
        var mine = Usage(null, "draft", "test-model", 1000, 1000, draftId: draftId, createdBy: planner.Id);
        var theirs = Usage(null, "article", "test-model", 1000, 1000, draftId: draftId, createdBy: Guid.NewGuid());
        await Seed(mine, theirs);
        var draft = new MselDraft
        {
            DraftId = draftId,
            Name = "Deep Bite",
            DurationMinutes = 60,
            Moves = [new MselDraftMove { MoveNumber = 0, Description = "Start" }],
            Injects = [new MselDraftInject { Minutes = 5, Values = new Dictionary<string, string> { ["Title"] = "Outage" } }]
        };

        var response = await Client(planner).PostAsJsonAsync("/api/msels/ai-draft/import", new MselDraftImport { TemplateMselId = template.Id, Draft = draft }, JsonOptions, Ct);

        var mselId = (await Read<MselImportResult>(response)).Msel.Id;
        await using var db = NewContext();
        Assert.Equal(mselId, (await db.AiUsages.SingleAsync(u => u.Id == mine.Id, Ct)).MselId);
        Assert.Null((await db.AiUsages.SingleAsync(u => u.Id == theirs.Id, Ct)).MselId);
    }

    /// <summary>A draft comes back with its own ID, which its later calls and its import carry.</summary>
    [Fact]
    public async Task Draft_HasADraftId()
    {
        var template = TestData.Msel(isTemplate: true);
        await Seed(template, TestData.DataField(template.Id, name: "Title", displayOrder: 1));
        var planner = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        _model.InvokeToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Text.Json.Nodes.JsonObject>(), Arg.Any<System.Threading.CancellationToken>()).Returns(_ =>
            JsonDocument.Parse("""{"name":"Deep Bite","durationMinutes":60,"moves":[],"teams":[],"organizations":[],"injects":[]}""").RootElement.Clone());

        var result = await Read<MselDraftResult>(await Client(planner).PostAsJsonAsync("/api/msels/ai-draft", new MselDraftRequest { TemplateMselId = template.Id, Prompt = "Cables" }, JsonOptions, Ct));

        Assert.NotEqual(Guid.Empty, result.Draft.DraftId);
    }

    private static AiUsageEntity Usage(Guid? mselId, string operation, string model, int input = 0, int output = 0, int images = 0,
        string stopReason = "tool_use", Guid? draftId = null, Guid? createdBy = null) => new()
        {
            Id = Guid.NewGuid(),
            MselId = mselId,
            DraftId = draftId,
            Operation = operation,
            Model = model,
            InputTokens = input,
            OutputTokens = output,
            Images = images,
            StopReason = stopReason,
            DateCreated = DateTime.UtcNow,
            CreatedBy = createdBy ?? Guid.NewGuid()
        };

    private async Task<T> Read<T>(System.Net.Http.HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
    }
}
