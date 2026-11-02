// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// <c>POST msels/{id}/ai-draft/move</c> - Write with AI on a move's situation briefing: written from the moves and the injects
/// before the move, keeping clear of the move's own injects; nothing is saved.
/// </summary>
public class MoveDraftEndpointTests(DatabaseFixture fixture, AiEnabledFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<AiEnabledFactory>
{
    private readonly IAiModelClient _model = factory.Model;
    private string _prompt;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _model.ClearSubstitute();
    }

    /// <summary>The briefing is written from the injects before the move, with the move's own listed to keep clear of, and the dialog's unsaved values.</summary>
    [Fact]
    public async Task Draft_ByAMoveEditor_WritesTheBriefing()
    {
        var (msel, second) = await SeedMsel();
        var editor = await Actor().OnMsel(msel, MselRole.MoveEditor).SeedAsync();
        Answer(new { html = "<h4>Day two</h4><p>The plant runs on manual.</p>" });

        var response = await Client(editor).PostAsJsonAsync($"/api/msels/{msel.Id}/ai-draft/move", new MoveDraftRequest
        {
            MoveId = second.Id, MoveNumber = 2, Description = "Manual operations (unsaved title)", SituationDescription = "<p>Old</p>", Instruction = "Say the plant is on manual"
        }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        var result = await response.Content.ReadFromJsonAsync<MoveDraftResult>(JsonOptions, Ct);

        Assert.Contains("The plant runs on manual.", result.Html);
        Assert.Contains("THIS MOVE 2, 1:00", _prompt);
        Assert.Contains("Manual operations (unsaved title)", _prompt);
        Assert.Contains("Injects before this move, which participants have had:\n- 0:10 Inject: Pumps stop", _prompt.Replace("\r\n", "\n"));
        Assert.Contains("Injects of this move, which the briefing must not give away:\n- 1:10 Inject: Ransom note", _prompt.Replace("\r\n", "\n"));
        Assert.Contains("Say the plant is on manual", _prompt);
    }

    /// <summary>A viewer may not have a briefing written, and the model is not asked.</summary>
    [Fact]
    public async Task Draft_ByAViewer_IsForbiddenWithoutAskingTheModel()
    {
        var (msel, second) = await SeedMsel();
        var viewer = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var response = await Client(viewer).PostAsJsonAsync($"/api/msels/{msel.Id}/ai-draft/move", new MoveDraftRequest { MoveId = second.Id, MoveNumber = 2 }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_model.ReceivedCalls());
    }

    private void Answer(object answer) =>
        _model.InvokeToolAsync(default, default, default, default, default, default)
            .ReturnsForAnyArgs(callInfo =>
            {
                _prompt = callInfo.ArgAt<string>(1);
                return JsonSerializer.SerializeToElement(answer);
            });

    // two moves, an inject in each
    private async Task<(MselEntity, MoveEntity Second)> SeedMsel()
    {
        var msel = TestData.Msel();
        var first = new MoveEntity { Id = Guid.NewGuid(), MselId = msel.Id, MoveNumber = 1, Description = "Outage", DeltaSeconds = 0 };
        var second = new MoveEntity { Id = Guid.NewGuid(), MselId = msel.Id, MoveNumber = 2, Description = "Manual operations", DeltaSeconds = 3600 };
        var title = TestData.DataField(msel.Id, name: "Title", displayOrder: 1);
        var early = TestData.ScenarioEvent(msel.Id);
        early.DeltaSeconds = 600;
        var late = TestData.ScenarioEvent(msel.Id);
        late.DeltaSeconds = 4200;
        await Seed(msel, first, second, title, early, late, TestData.DataValue(title.Id, early.Id, "Pumps stop"), TestData.DataValue(title.Id, late.Id, "Ransom note"));
        return (msel, second);
    }
}
