// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
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

/// <summary><c>POST msels/{mselId}/ai-draft/value</c> - one field of an inject, written from the rest of the MSEL.</summary>
public class DataValueDraftEndpointTests(DatabaseFixture fixture, AiEnabledFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<AiEnabledFactory>
{
    private readonly IAiModelClient _model = factory.Model;
    private string _system;
    private string _prompt;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _model.ClearSubstitute();
        Answer("""{"value":"<p>Port of Avalon closed.</p>","notes":["Assumed the port is in Avalon."]}""");
    }

    [Fact]
    public async Task Draft_ByAnEditor_ReturnsTheModelsValueAndSavesNothing()
    {
        var (msel, title, body, _) = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var response = await Post(actor, msel.Id, Request(body, title, "Port closure"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Read<DataValueDraftResult>(response);
        Assert.Equal("<p>Port of Avalon closed.</p>", result.Value);
        Assert.Equal(["Assumed the port is in Avalon."], result.Notes);
        Assert.False(await ReadBack(rb => rb.DataValues.AnyAsync(v => v.Value == result.Value, Ct)));
    }

    /// <summary>The prompt carries the scenario, the earlier injects and the dialog's unsaved values, and keeps later injects to their titles.</summary>
    [Fact]
    public async Task Draft_WritesFromTheScenarioAndTheOtherInjects()
    {
        var (msel, title, body, _) = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        await Post(actor, msel.Id, Request(body, title, "Port closure", deltaSeconds: 1800));

        Assert.Contains("Hurricane Zeta reaches the coast", _prompt);
        Assert.Contains("Storm warning issued", _prompt);
        Assert.Contains("NWS expects landfall at noon", _prompt);
        Assert.Contains("Power restored", _prompt);
        Assert.DoesNotContain("Crews restore the grid", _prompt);
        Assert.Contains("Port closure", _prompt);
    }

    [Fact]
    public async Task Rewrite_AsAGallerySourceType_AsksForThatKindOfText()
    {
        var (msel, title, body, _) = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();
        var request = Request(body, title, "Port closure");
        request.Values[body.Id] = "<p>The port is closed.</p>";
        request.Action = DataValueDraftAction.Rewrite;
        request.Style = "Email";

        await Post(actor, msel.Id, request);

        Assert.Contains("Rewrite it as an email", _prompt);
        Assert.Contains("<p>The port is closed.</p>", _prompt);
    }

    [Fact]
    public async Task Rewrite_OfAnEmptyField_Is500WithoutAskingTheModel()
    {
        var (msel, title, body, _) = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();
        var request = Request(body, title, "Port closure");
        request.Action = DataValueDraftAction.Shorten;

        var response = await Post(actor, msel.Id, request);

        Assert.False(response.IsSuccessStatusCode);
        Assert.Null(_prompt);
    }

    /// <summary>Only the formatting the editor offers comes back: scripts, attributes and unknown tags go.</summary>
    /// <summary>With Gallery, the article body is written as a document of its source type, and its layout is kept.</summary>
    [Fact]
    public async Task Draft_OfAGalleryArticleBody_WritesTheDocumentAndKeepsItsLayout()
    {
        Answer("""{"value":"<table width=\"720\" style=\"x\"><tr><td bgcolor=\"#FDB516\"><font size=\"2\"><b>EXERCISE</b></font></td></tr></table><svg viewBox=\"0 0 40 40\" aria-label=\"Logo\"></svg>"}""");
        var (msel, title, body, _) = await SeedMsel(useGallery: true);
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var result = await Read<DataValueDraftResult>(await Post(actor, msel.Id, Request(body, title, "Port closure")));

        Assert.Contains("This field is the body of a Gallery article.", _system);
        Assert.Contains("EXERCISE · EXERCISE · EXERCISE", _system);
        Assert.StartsWith("<table width=\"720\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" bgcolor=\"#FFFFFF\"><tbody><tr><td><font color=\"#1A1A1A\"><table width=\"720\"><tbody><tr><td bgcolor=\"#FDB516\"><font size=\"2\"><b>EXERCISE</b></font></td></tr></tbody></table><img src=\"data:image/svg+xml;base64,", result.Value);
        Assert.Contains("Removed 1 style attribute, which Gallery does not show.", result.Notes);
    }

    [Fact]
    public async Task Draft_StripsWhatTheEditorWouldNotWrite()
    {
        Answer("""{"value":"<p onclick=\"x()\">Closed <a href=\"https://evil.example\">here</a><script>alert(1)</script></p>"}""");
        var (msel, title, body, _) = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var result = await Read<DataValueDraftResult>(await Post(actor, msel.Id, Request(body, title, "Port closure")));

        Assert.Equal("<p>Closed here</p>", result.Value);
    }

    [Fact]
    public async Task Draft_ByAViewerOfTheMsel_Is403()
    {
        var (msel, title, body, _) = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var response = await Post(actor, msel.Id, Request(body, title, "Port closure"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(_prompt);
    }

    [Fact]
    public async Task Draft_WithEditMsels_IsAllowedWithoutARole()
    {
        var (msel, title, body, _) = await SeedMsel();
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(actor, msel.Id, Request(body, title, "Port closure"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>A field of another MSEL cannot be used to read that MSEL's injects into the prompt.</summary>
    [Fact]
    public async Task Draft_OfAnotherMselsField_Is404()
    {
        var (msel, title, _, _) = await SeedMsel();
        var (_, _, otherBody, _) = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var response = await Post(actor, msel.Id, Request(otherBody, title, "Port closure"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(_prompt);
    }

    [Fact]
    public async Task Draft_OfAListField_IsRefused()
    {
        var (msel, title, _, sourceType) = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var response = await Post(actor, msel.Id, Request(sourceType, title, "Port closure"));

        Assert.False(response.IsSuccessStatusCode);
        Assert.Null(_prompt);
    }

    // ---------------------------------------------------------------------------------------------

    private void Answer(string json) =>
        _model.InvokeToolAsync(default, default, default, default, default, default)
            .ReturnsForAnyArgs(callInfo =>
            {
                _system = callInfo.ArgAt<string>(0);
                _prompt = callInfo.ArgAt<string>(1);
                return JsonDocument.Parse(json).RootElement.Clone();
            });

    private async Task<T> Read<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);

    private Task<HttpResponseMessage> Post(TestActor actor, Guid mselId, DataValueDraftRequest request) =>
        Client(actor).PostAsJsonAsync($"/api/msels/{mselId}/ai-draft/value", request, JsonOptions, Ct);

    private static DataValueDraftRequest Request(DataFieldEntity field, DataFieldEntity title, string titleValue, int deltaSeconds = 0) =>
        new()
        {
            DataFieldId = field.Id,
            DeltaSeconds = deltaSeconds,
            Values = new Dictionary<Guid, string> { [title.Id] = titleValue },
            Action = DataValueDraftAction.Draft
        };

    /// <summary>A MSEL with a scenario, a title, an Html body and a list field, and one inject either side of 30 minutes.</summary>
    private async Task<(MselEntity, DataFieldEntity Title, DataFieldEntity Body, DataFieldEntity SourceType)> SeedMsel(bool useGallery = false)
    {
        var msel = TestData.Msel();
        msel.UseGallery = useGallery;
        msel.Description = "Hurricane Zeta reaches the coast of Avalon.";
        var title = TestData.DataField(msel.Id, name: "Title", displayOrder: 1);
        title.GalleryArticleParameter = "Name";
        var body = TestData.DataField(msel.Id, dataType: DataFieldType.Html, name: "Description", displayOrder: 2);
        body.GalleryArticleParameter = "Description";
        var sourceType = TestData.DataField(msel.Id, dataType: DataFieldType.SourceType, name: "Source Type", displayOrder: 3);
        sourceType.IsChosenFromList = true;
        var before = TestData.ScenarioEvent(msel.Id, deltaSeconds: 600);
        var after = TestData.ScenarioEvent(msel.Id, deltaSeconds: 3600);
        await Seed(msel, title, body, sourceType, before, after,
            TestData.DataValue(title.Id, before.Id, "Storm warning issued"),
            TestData.DataValue(body.Id, before.Id, "<p>NWS expects landfall at noon.</p>"),
            TestData.DataValue(title.Id, after.Id, "Power restored"),
            TestData.DataValue(body.Id, after.Id, "<p>Crews restore the grid.</p>"));
        return (msel, title, body, sourceType);
    }
}
