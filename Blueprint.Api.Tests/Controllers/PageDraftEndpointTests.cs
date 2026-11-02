// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
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
using NSubstitute;
using NSubstitute.ClearExtensions;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// <c>POST msels/{id}/ai-draft/page</c> - Write with AI on a MSEL page: the model writes the page from the MSEL and the page as
/// the editor holds it, and nothing is saved.
/// </summary>
public class PageDraftEndpointTests(DatabaseFixture fixture, AiEnabledFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<AiEnabledFactory>
{
    private readonly IAiModelClient _model = factory.Model;
    private string _prompt;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _model.ClearSubstitute();
    }

    /// <summary>The owner gets the page written from the MSEL, its moves and other pages, and the unsaved content; script is removed.</summary>
    [Fact]
    public async Task Draft_ByTheOwner_WritesThePageFromTheMsel()
    {
        var (msel, readIn) = await SeedMsel();
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        Answer(new { html = "<section><h2>Read-In</h2><p>Welcome, BLUE.</p><script>alert(1)</script></section>", notes = new[] { "Assumed one shift" } });

        var result = await Read<PageDraftResult>(await Draft(owner, msel.Id,
            new PageDraftRequest { PageId = readIn.Id, Name = "Read-In", Content = "<p>Unsaved draft of the read-in</p>", Instruction = "Add the rules of play" }));

        Assert.Contains("Welcome, BLUE.", result.Html);
        Assert.DoesNotContain("<script", result.Html);
        Assert.Equal(["Assumed one shift", "Removed 1 script, style or embedded element."], result.Notes);
        Assert.Contains("Unsaved draft of the read-in", _prompt);
        Assert.Contains("Add the rules of play", _prompt);
        Assert.Contains("Cables cut", _prompt);
        Assert.Contains("BLUE: Blue team", _prompt);
        Assert.Contains("The MSEL's other pages: Facilitator Guide.", _prompt);
        Assert.DoesNotContain("Read-In;", _prompt);
    }

    /// <summary>A viewer may not have a page written, and the model is not asked.</summary>
    [Fact]
    public async Task Draft_ByAViewer_IsForbiddenWithoutAskingTheModel()
    {
        var (msel, readIn) = await SeedMsel();
        var viewer = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var response = await Draft(viewer, msel.Id, new PageDraftRequest { PageId = readIn.Id, Name = "Read-In" });

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

    private Task<HttpResponseMessage> Draft(TestActor actor, Guid mselId, PageDraftRequest request) =>
        Client(actor).PostAsJsonAsync($"/api/msels/{mselId}/ai-draft/page", request, Ct);

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
    }

    // a MSEL with team BLUE, move 1 "Cables cut", and two pages
    private async Task<(MselEntity, MselPageEntity ReadIn)> SeedMsel()
    {
        var msel = TestData.Msel();
        var blue = TestData.Team(msel.Id);
        blue.ShortName = "BLUE";
        blue.Name = "Blue team";
        var move = new MoveEntity { Id = Guid.NewGuid(), MselId = msel.Id, MoveNumber = 1, Description = "Cables cut", DeltaSeconds = 600 };
        var readIn = new MselPageEntity { Id = Guid.NewGuid(), MselId = msel.Id, Name = "Read-In", Content = "<p>Old</p>" };
        var guide = new MselPageEntity { Id = Guid.NewGuid(), MselId = msel.Id, Name = "Facilitator Guide", Content = "<p>Guide</p>" };
        await Seed(msel, blue, move, readIn, guide);
        return (msel, readIn);
    }
}
