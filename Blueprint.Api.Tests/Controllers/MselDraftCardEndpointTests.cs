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
/// Gallery cards in an AI draft: the model groups the articles on cards, and import puts the cards on the wall for every
/// team and each article on its card, as the exercises built with blueprint-ttx do.
/// </summary>
public class MselDraftCardEndpointTests(DatabaseFixture fixture, AiEnabledFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<AiEnabledFactory>
{
    private readonly IAiModelClient _model = factory.Model;
    private string _prompt;
    private string _schema;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _model.ClearSubstitute();
    }

    /// <summary>With Gallery, the model is asked for cards and a card per article, and told how to group them.</summary>
    [Fact]
    public async Task Draft_WithGallery_AsksForCardsAndShowsTheTemplatesOwn()
    {
        var template = await SeedTemplate(useGallery: true);
        Answer(Draft());

        var result = await Read<MselDraftResult>(await PostDraft(template.Id));

        Assert.Contains("\"cards\"", _schema);
        Assert.Contains("\"card\"", _schema);
        Assert.Contains("Template Gallery cards: Situation Updates (Articles as the incident develops)", _prompt);
        Assert.Equal(["Exercise Control", "Public and Media", "Cable Operator"], result.Draft.Cards.Select(c => c.Name));
        Assert.Equal(["Exercise Control", "Cable Operator", null, null], result.Draft.Injects.Select(i => i.Card));
        Assert.Contains(result.Warnings, w => w.Contains("Took injects off cards the draft does not have: Weather"));
        Assert.Contains(result.Warnings, w => w.Contains("Dropped the second card named \"public and media\""));
    }

    /// <summary>Without Gallery there are no cards to ask for, and any the model sends are dropped.</summary>
    [Fact]
    public async Task Draft_WithoutGallery_HasNoCards()
    {
        var template = await SeedTemplate(useGallery: false);
        Answer(Draft());

        var result = await Read<MselDraftResult>(await PostDraft(template.Id));

        Assert.DoesNotContain("\"cards\"", _schema);
        Assert.Empty(result.Draft.Cards);
        Assert.All(result.Draft.Injects, i => Assert.Null(i.Card));
    }

    /// <summary>
    /// Import adds the cards after the template's, shows every card to every team, puts each article on its card, and
    /// has each drafted card first appear on the wall with its first article.
    /// </summary>
    [Fact]
    public async Task Import_PutsTheCardsOnTheWallAndTheArticlesOnThem()
    {
        var template = await SeedTemplate(useGallery: true);
        var draft = JsonSerializer.Deserialize<MselDraft>(Draft(), JsonOptions);
        var planner = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(planner).PostAsJsonAsync("/api/msels/ai-draft/import", new MselDraftImport { TemplateMselId = template.Id, Draft = draft }, JsonOptions, Ct);

        var mselId = (await Read<MselImportResult>(response)).Msel.Id;
        await using var db = NewContext();
        var cards = await db.Cards.Include(c => c.CardTeams).ThenInclude(ct => ct.Team).Where(c => c.MselId == mselId).OrderBy(c => c.Inject).ToListAsync(Ct);
        // the read-in is the pre-exercise move's inject 0; the cable cut opens move 1, the first of play, as its inject 0;
        // Public and Media has no article yet, so it keeps its place after the template's card
        Assert.Equal(
            new Dictionary<string, (int, int)> { ["Situation Updates"] = (0, 1), ["Exercise Control"] = (0, 0), ["Public and Media"] = (0, 3), ["Cable Operator"] = (1, 0) },
            cards.ToDictionary(c => c.Name, c => (c.Move, c.Inject)));
        Assert.All(cards, c => Assert.Equal(["BLUE", "EXCON"], c.CardTeams.Select(ct => ct.Team.ShortName).Order()));
        Assert.All(cards.SelectMany(c => c.CardTeams), ct => Assert.True(ct.IsShownOnWall));
        Assert.All(cards.SelectMany(c => c.CardTeams), ct => Assert.Equal(ct.Team.ShortName == "EXCON", ct.CanPostArticles));

        var cardField = await db.DataFields.SingleAsync(f => f.MselId == mselId && f.GalleryArticleParameter == "CardId", Ct);
        var titleField = await db.DataFields.SingleAsync(f => f.MselId == mselId && f.Name == "Title", Ct);
        var onCards = await db.ScenarioEvents.Where(e => e.MselId == mselId).Include(e => e.DataValues).ToListAsync(Ct);
        string CardOf(string title)
        {
            var inject = onCards.Single(e => e.DataValues.Any(v => v.DataFieldId == titleField.Id && v.Value == title));
            var id = inject.DataValues.SingleOrDefault(v => v.DataFieldId == cardField.Id)?.Value;
            return id == null ? null : cards.Single(c => c.Id.ToString() == id).Name;
        }
        Assert.Equal("Exercise Control", CardOf("Read-in"));
        Assert.Equal("Cable Operator", CardOf("Cable cut"));
        Assert.Null(CardOf("Controller check"));
        // the template keeps its own card and teams
        Assert.Equal(1, await db.Cards.CountAsync(c => c.MselId == template.Id, Ct));
    }

    private void Answer(string json) =>
        _model.InvokeToolAsync(default, default, default, default, default, default)
            .ReturnsForAnyArgs(callInfo =>
            {
                _prompt = callInfo.ArgAt<string>(1);
                _schema = callInfo.ArgAt<System.Text.Json.Nodes.JsonObject>(4).ToJsonString();
                return JsonDocument.Parse(json).RootElement.Clone();
            });

    private static string Draft() =>
        JsonSerializer.Serialize(new
        {
            name = "Deep Bite",
            description = "Cables are cut.",
            durationMinutes = 120,
            moves = new[] { new { moveNumber = 0, description = "Start", situationDescription = "Calm", startMinutes = 0 } },
            teams = new[] { new { name = "Exercise control", shortName = "EXCON" } },
            organizations = Array.Empty<object>(),
            cards = new[]
            {
                new { name = "Exercise Control", description = "Read-in and facilitator messages" },
                new { name = "Public and Media", description = "News and social posts" },
                new { name = "public and media", description = "A duplicate" },
                new { name = "Cable Operator", description = "The operator's notices" }
            },
            injects = new object[]
            {
                new { minutes = 0, eventType = "Information", card = "exercise control", values = new Dictionary<string, string> { ["Title"] = "Read-in" } },
                new { minutes = 10, eventType = "Inject", card = "Cable Operator", values = new Dictionary<string, string> { ["Title"] = "Cable cut" } },
                new { minutes = 20, eventType = "Inject", card = "Weather", values = new Dictionary<string, string> { ["Title"] = "Storm" } },
                new { minutes = 30, eventType = "Facilitation", values = new Dictionary<string, string> { ["Title"] = "Controller check" } }
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

    // a template with team BLUE, a Title column and, with Gallery, its card Situation Updates shown to BLUE
    private async Task<MselEntity> SeedTemplate(bool useGallery)
    {
        var template = TestData.Msel(isTemplate: true);
        template.UseGallery = useGallery;
        var blue = TestData.Team(template.Id);
        blue.ShortName = "BLUE";
        var title = TestData.DataField(template.Id, name: "Title", displayOrder: 1);
        var card = TestData.Card(template.Id, move: 0, inject: 1, description: "Articles as the incident develops");
        card.Name = "Situation Updates";
        await Seed(template, blue, title, card, TestData.CardTeam(card.Id, blue.Id, isShownOnWall: true));
        return template;
    }
}
