// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
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
/// <c>POST msels/ai-draft/article</c> - one of a reviewed draft's Gallery articles written out as a document of its
/// source type, in HTML Gallery shows as written. Nothing is saved.
/// </summary>
public class ArticleDraftEndpointTests(DatabaseFixture fixture, AiEnabledFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<AiEnabledFactory>
{
    private readonly IAiModelClient _model = factory.Model;
    private readonly IAiImageClient _images = factory.Images;
    private string _system;
    private string _prompt;
    private string _schema;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _model.ClearSubstitute();
        _images.ClearSubstitute();
    }

    /// <summary>Without an image model the article is not offered a photo.</summary>
    [Fact]
    public async Task Draft_WithoutAnImageModel_OffersNoPhoto()
    {
        var template = await SeedTemplate();
        Answer(new { html = "<p>Cables cut.</p>" });

        await Read<ArticleDraftResult>(await Post(template.Id, 0));

        Assert.DoesNotContain("\"photo\"", _schema);
        Assert.DoesNotContain(ArticleDraftService.PhotoMarker, _system);
        Assert.DoesNotContain(_images.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IAiImageClient.PhotoAsync));
    }

    /// <summary>With an image model, the photo the article describes is made and put where it marked, with its caption.</summary>
    [Fact]
    public async Task Draft_WithAnImageModel_PutsThePhotoWhereTheArticleMarkedIt()
    {
        var template = await SeedTemplate();
        _images.IsConfigured.Returns(true);
        _images.PhotoAsync(Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>()).Returns("data:image/jpeg;base64,PHOTO");
        Answer(new
        {
            html = "<table width=\"720\"><tr><td>[[PHOTO]]</td></tr></table><p>Cables cut.</p>",
            photo = new { scene = "crews at a cable landing station at dusk", caption = "Crews at the landing site on Tuesday & Wednesday." }
        });

        var result = await Read<ArticleDraftResult>(await Post(template.Id, 0));

        Assert.Contains("\"photo\"", _schema);
        Assert.Contains("the photo model cannot spell", _system);
        await _images.Received(1).PhotoAsync("crews at a cable landing station at dusk", Arg.Any<System.Threading.CancellationToken>());
        Assert.Contains("<td><img src=\"data:image/jpeg;base64,PHOTO\" width=\"720\" height=\"405\" alt=\"Crews at the landing site on Tuesday &amp; Wednesday.\">" +
            "<br><font color=\"#666666\" size=\"1\">Crews at the landing site on Tuesday &amp; Wednesday.</font></td>", result.Html);
        Assert.DoesNotContain(ArticleDraftService.PhotoMarker, result.Html);
    }

    /// <summary>A photo the image model will not make leaves the article without one, and says why.</summary>
    [Fact]
    public async Task Draft_WhenThePhotoFails_KeepsTheArticleAndSaysWhy()
    {
        var template = await SeedTemplate();
        _images.IsConfigured.Returns(true);
        _images.PhotoAsync(Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>()).Returns<string>(_ => throw new InvalidOperationException("The image model made no photo (CONTENT_FILTERED)."));
        Answer(new { html = "<p>[[PHOTO]]</p><p>Cables cut.</p>", photo = new { scene = "a harbor", caption = "The harbor." } });

        var result = await Read<ArticleDraftResult>(await Post(template.Id, 0));

        Assert.DoesNotContain(ArticleDraftService.PhotoMarker, result.Html);
        Assert.Contains("<p>Cables cut.</p>", result.Html);
        Assert.Contains("No photo was made: The image model made no photo (CONTENT_FILTERED).", result.Notes);
    }

    /// <summary>A photo with no marked place is not made, so it is not paid for.</summary>
    [Fact]
    public async Task Draft_WithNoPlaceForThePhoto_MakesNone()
    {
        var template = await SeedTemplate();
        _images.IsConfigured.Returns(true);
        Answer(new { html = "<p>Cables cut.</p>", photo = new { scene = "a harbor", caption = "The harbor." } });

        var result = await Read<ArticleDraftResult>(await Post(template.Id, 0));

        await _images.DidNotReceive().PhotoAsync(Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>());
        Assert.Contains("The article had no place for its photo, so none was made.", result.Notes);
    }

    /// <summary>The model gets the article's facts and the scenario; its HTML comes back ready for Gallery.</summary>
    [Fact]
    public async Task Draft_WritesTheArticleAsGalleryReadyHtml()
    {
        var template = await SeedTemplate();
        Answer(new
        {
            html = "<table width=\"720\"><tr><td bgcolor=\"#FDB516\" style=\"padding:4px\"><b>EXERCISE</b></td></tr></table>" +
                   "<svg viewBox=\"0 0 200 40\" aria-label=\"Harbour Herald masthead\"><text x=\"0\" y=\"30\">Harbour Herald</text></svg><p>Cables cut.</p>",
            notes = new[] { "Assumed the outlet is regional." }
        });

        var result = await Read<ArticleDraftResult>(await Post(template.Id, 0));

        Assert.Equal("Details", result.FieldName);
        // on white paper with dark ink, so it reads the same in light and dark themes
        // the MSEL's marking first, then the article
        Assert.StartsWith("<table title=\"Marking\"", result.Html);
        Assert.Contains("</table><table width=\"720\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" bgcolor=\"#FFFFFF\"><tbody><tr><td><font color=\"#1A1A1A\">", result.Html);
        Assert.EndsWith("</font></td></tr></tbody></table>", result.Html);
        Assert.Contains("<img src=\"data:image/svg+xml;base64,", result.Html);
        Assert.Contains("alt=\"Harbour Herald masthead\"", result.Html);
        Assert.Empty(GalleryArticleHtml.Problems(result.Html));
        Assert.Contains("Removed 1 style attribute, which Gallery does not show.", result.Notes);
        Assert.Contains("Assumed the outlet is regional.", result.Notes);

        Assert.Contains("Exercise: Deep Bite", _prompt);
        Assert.Contains("- NEWS: Harbour Herald, the regional paper", _prompt);
        Assert.Contains("- BLUE: Blue team, participant team.", _prompt);
        Assert.Contains("Source type: News", _prompt);
        Assert.Contains("Source name: Harbour Herald", _prompt);
        Assert.Contains("Sent to: BLUE", _prompt);
        Assert.Contains("Headline or subject: Cables cut off the islands", _prompt);
        Assert.Contains("Draft text:\nTwo cables were cut overnight.", _prompt.ReplaceLineEndings("\n"));
        // the marking is Blueprint's to add, not the model's
        Assert.Contains("Blueprint adds the exercise's marking to every article itself", _system);
    }

    [Fact]
    public async Task Draft_OfAnInjectNotInTheDraft_IsABadRequest()
    {
        var template = await SeedTemplate();

        var response = await Post(template.Id, 5);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_model.ReceivedCalls());
    }

    [Fact]
    public async Task Draft_WithoutCreateMsels_IsForbidden()
    {
        var template = await SeedTemplate();
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).PostAsJsonAsync("/api/msels/ai-draft/article", Request(template.Id, 0), JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private void Answer(object answer) =>
        _model.InvokeToolAsync(default, default, default, default, default, default)
            .ReturnsForAnyArgs(callInfo =>
            {
                _system = callInfo.ArgAt<string>(0);
                _prompt = callInfo.ArgAt<string>(1);
                _schema = callInfo.ArgAt<System.Text.Json.Nodes.JsonObject>(4).ToJsonString();
                return JsonSerializer.SerializeToElement(answer);
            });

    private async Task<HttpResponseMessage> Post(Guid templateId, int index)
    {
        var planner = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        return await Client(planner).PostAsJsonAsync("/api/msels/ai-draft/article", Request(templateId, index), JsonOptions, Ct);
    }

    private static ArticleDraftRequest Request(Guid templateId, int index) => new()
    {
        TemplateMselId = templateId,
        InjectIndex = index,
        Draft = new MselDraft
        {
            Name = "Deep Bite",
            Description = "Undersea cables are cut.",
            Teams = [new MselDraftTeam { ShortName = "BLUE", Name = "Blue team" }],
            Organizations = [new MselDraftOrganization { ShortName = "NEWS", Name = "Harbour Herald", Description = "the regional paper" }],
            Injects =
            [
                new MselDraftInject
                {
                    Minutes = 10,
                    Values = new Dictionary<string, string>
                    {
                        ["Title"] = "Cables cut off the islands",
                        ["Description"] = "Two islands lose internet.",
                        ["Details"] = "<p>Two cables were cut overnight.</p>",
                        ["SourceType"] = "News",
                        ["Source Name"] = "Harbour Herald",
                        ["From Org"] = "NEWS",
                        ["To Org"] = "BLUE"
                    }
                }
            ]
        }
    };

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
    }

    // a Gallery template with the Standard MSEL's columns; drafting maps them to the article
    private async Task<MselEntity> SeedTemplate()
    {
        var template = TestData.Msel(isTemplate: true);
        template.UseGallery = true;
        await Seed(template,
            TestData.DataField(template.Id, name: "Title", displayOrder: 1),
            TestData.DataField(template.Id, name: "Description", displayOrder: 2),
            TestData.DataField(template.Id, name: "Details", displayOrder: 3),
            TestData.DataField(template.Id, dataType: DataFieldType.Organization, name: "From Org", displayOrder: 4),
            TestData.DataField(template.Id, dataType: DataFieldType.TeamsMultiple, name: "To Org", displayOrder: 5));
        return template;
    }
}
