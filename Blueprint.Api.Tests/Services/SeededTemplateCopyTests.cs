// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary>A planner starts an exercise by copying a template, so the integration template from the
/// shipped seed-data.json must come through <c>POST api/msels/{id}/copy</c> whole.</summary>
public class SeededTemplateCopyTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    private static readonly Guid CrucibleMselId = Guid.Parse("73e1cd7e-c37d-4df2-b5e3-db6125d12365");

    [Fact]
    public async Task Copy_OfTheSeededIntegrationTemplate_CopiesWhatEachPushNeeds()
    {
        SeedFromTheShippedSeedFile();
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.CreateMsels, SystemPermission.ViewMsels)
            .SeedAsync();

        var response = await Client(actor).PostAsync($"/api/msels/{CrucibleMselId}/copy", null, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var copy = await response.Content.ReadFromJsonAsync<Msel>(JsonOptions, Ct);
        Assert.False(copy.IsTemplate);

        await using var db = NewContext();
        var teams = await db.Teams.Where(x => x.MselId == copy.Id).ToListAsync(Ct);
        Assert.Equal(2, teams.Count);

        var mapped = await db.DataFields
            .Where(x => x.MselId == copy.Id && x.GalleryArticleParameter != null)
            .Select(x => x.GalleryArticleParameter)
            .ToListAsync(Ct);
        Assert.Equal(
            Enum.GetNames<GalleryArticleParameter>().OrderBy(x => x),
            mapped.OrderBy(x => x));

        Assert.Equal(3, await db.Moves.CountAsync(x => x.MselId == copy.Id && x.SituationTime != null, Ct));

        var applications = await db.PlayerApplications
            .Include(x => x.PlayerApplicationTeams)
            .Where(x => x.MselId == copy.Id)
            .ToListAsync(Ct);
        Assert.Equal(3, applications.Count);
        Assert.All(
            applications.SelectMany(x => x.PlayerApplicationTeams),
            x => Assert.Contains(teams, t => t.Id == x.TeamId));
        Assert.Equal(6, applications.Sum(x => x.PlayerApplicationTeams.Count));

        Assert.All(
            await db.CiteActions.Where(x => x.MselId == copy.Id).ToListAsync(Ct),
            x => Assert.Contains(teams, t => t.Id == x.TeamId));
        Assert.Equal(2, await db.CiteDuties.CountAsync(x => x.MselId == copy.Id, Ct));
        Assert.Equal(2, await db.CardTeams.CountAsync(x => x.Card.MselId == copy.Id, Ct));
    }

    /// <summary>The template pickers list system-wide templates (a template on no MSEL), so every one
    /// the shipped seed-data.json holds must come back from its <c>*/templates</c> endpoint.</summary>
    [Fact]
    public async Task Templates_FromTheShippedSeedFile_ComeBackFromEachTemplatesEndpoint()
    {
        SeedFromTheShippedSeedFile();
        var actor = await Actor().SeedAsync();
        var client = Client(actor);

        await AssertTemplates<Card>(client, "cards", "Cards", x => x.Id, x => x.MselId, x => x.IsTemplate);
        await AssertTemplates<CiteDuty>(client, "citeDuties", "CiteDuties", x => x.Id, x => x.MselId, x => x.IsTemplate);
        await AssertTemplates<CiteAction>(client, "citeActions", "CiteActions", x => x.Id, x => x.MselId, x => x.IsTemplate);
        await AssertTemplates<DataField>(client, "dataFields", "DataFields", x => x.Id, x => x.MselId, x => x.IsTemplate);
        await AssertTemplates<Organization>(client, "organizations", "Organizations", x => x.Id, x => x.MselId, x => x.IsTemplate);

        var duties = await client.GetFromJsonAsync<CiteDuty[]>("/api/citeDuties/templates", JsonOptions, Ct);
        Assert.All(duties, x => Assert.Null(x.TeamId));
        var actions = await client.GetFromJsonAsync<CiteAction[]>("/api/citeActions/templates", JsonOptions, Ct);
        Assert.All(actions, x => Assert.Null(x.TeamId));

        // the MSEL-scoped copies on the integration template are not templates
        var cards = await client.GetFromJsonAsync<Card[]>("/api/cards/templates", JsonOptions, Ct);
        Assert.DoesNotContain(cards, x => x.Name == "Situation Updates");
    }

    /// <summary>Each Gallery source type has an inject type of its name, carrying the Gallery Article
    /// inject type's fields so a catalog inject of that type fills the same MSEL fields.</summary>
    [Fact]
    public async Task InjectTypes_FromTheShippedSeedFile_IncludeOnePerGallerySourceType()
    {
        SeedFromTheShippedSeedFile();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewInjectTypes).SeedAsync();

        var injectTypes = await Client(actor).GetFromJsonAsync<InjectType[]>("/api/injectTypes", JsonOptions, Ct);

        Assert.Contains(injectTypes, x => x.Name == "Gallery Article");
        Assert.All(Enum.GetNames<GallerySourceType>(), name => Assert.Contains(injectTypes, x => x.Name == name));

        await using var db = NewContext();
        var galleryArticle = await db.InjectTypes.SingleAsync(x => x.Name == "Gallery Article", Ct);
        var expected = (await db.DataFields.Where(x => x.InjectTypeId == galleryArticle.Id).ToListAsync(Ct))
            .Select(x => (x.Name, x.DataType)).OrderBy(x => x.Name).ToList();
        foreach (var name in Enum.GetNames<GallerySourceType>())
        {
            var injectType = injectTypes.Single(x => x.Name == name);
            var fields = (await db.DataFields.Where(x => x.InjectTypeId == injectType.Id).ToListAsync(Ct))
                .Select(x => (x.Name, x.DataType)).OrderBy(x => x.Name).ToList();
            Assert.Equal(expected, fields);
        }
    }

    /// <summary>The Crucible MSEL authors NCISS, so a MSEL drafted or copied from it scores with it unless the planner chooses otherwise.</summary>
    [Fact]
    public async Task CrucibleMsel_FromTheShippedSeedFile_AuthorsNciss()
    {
        SeedFromTheShippedSeedFile();

        await using var db = NewContext();
        var model = await db.MselScoringModels.Include(m => m.Categories).SingleAsync(m => m.MselId == CrucibleMselId, Ct);
        Assert.Equal("CISA NCISS", model.Description);
        Assert.Equal(8, model.Categories.Count);
    }

    /// <summary>The Crucible MSEL maps every Gallery article parameter to one column, typed as Gallery shows it:
    /// the article body is HTML and the summary is plain text.</summary>
    [Fact]
    public async Task CrucibleMsel_FromTheShippedSeedFile_MapsEachGalleryArticleParameterOnce()
    {
        SeedFromTheShippedSeedFile();

        await using var db = NewContext();
        var msel = await db.Msels.SingleAsync(x => x.Id == CrucibleMselId, Ct);
        var fields = await db.DataFields.Where(x => x.MselId == CrucibleMselId && x.GalleryArticleParameter != null).ToListAsync(Ct);

        Assert.Equal("Crucible MSEL", msel.Name);
        Assert.Equal(
            Enum.GetNames<GalleryArticleParameter>().Order(),
            fields.Select(x => x.GalleryArticleParameter).Order());
        Assert.Equal(DataFieldType.Html, fields.Single(x => x.GalleryArticleParameter == nameof(GalleryArticleParameter.Description)).DataType);
        Assert.Equal(DataFieldType.String, fields.Single(x => x.GalleryArticleParameter == nameof(GalleryArticleParameter.Summary)).DataType);
        Assert.Equal(DataFieldType.SourceType, fields.Single(x => x.GalleryArticleParameter == nameof(GalleryArticleParameter.SourceType)).DataType);
        Assert.Equal(DataFieldType.DateTime, fields.Single(x => x.GalleryArticleParameter == nameof(GalleryArticleParameter.DatePosted)).DataType);
        Assert.Equal(DataFieldType.Card, fields.Single(x => x.GalleryArticleParameter == nameof(GalleryArticleParameter.CardId)).DataType);
    }

    /// <summary>GETs <c>api/{route}/templates</c> and checks it returns exactly the rows of the shipped
    /// seed file's <paramref name="section"/> that are templates on no MSEL.</summary>
    private async Task AssertTemplates<T>(
        System.Net.Http.HttpClient client, string route, string section,
        Func<T, Guid> id, Func<T, Guid?> mselId, Func<T, bool> isTemplate)
    {
        var response = await client.GetAsync($"/api/{route}/templates", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var returned = await response.Content.ReadFromJsonAsync<T[]>(JsonOptions, Ct);

        var expected = SystemWideTemplateIds(section);
        Assert.NotEmpty(expected);
        Assert.Equal(expected.OrderBy(x => x), returned.Select(id).OrderBy(x => x));
        Assert.All(returned, x => Assert.Null(mselId(x)));
        Assert.All(returned, x => Assert.True(isTemplate(x)));
    }

    /// <summary>The Ids of the rows in a section of the shipped seed file with IsTemplate true and no MselId.</summary>
    private static Guid[] SystemWideTemplateIds(string section)
    {
        using var seed = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "seed-data.json")));

        return seed.RootElement.GetProperty(section).EnumerateArray()
            .Where(x => x.TryGetProperty("IsTemplate", out var t) && t.GetBoolean())
            .Where(x => !x.TryGetProperty("MselId", out var m) || m.ValueKind == JsonValueKind.Null)
            .Select(x => x.GetProperty("Id").GetGuid())
            .ToArray();
    }

    /// <summary>Seeds this test's database the way the image does on start.</summary>
    private void SeedFromTheShippedSeedFile()
    {
        var log = new RecordingLogger<Blueprint.Api.Program>();
        var builder = Host.CreateEmptyApplicationBuilder(
            new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });

        builder.Services.AddSingleton<ILogger<Blueprint.Api.Program>>(log);
        builder.Services.AddSingleton(new DatabaseOptions
        {
            Provider = "PostgreSQL",
            SeedFile = "seed-data.json",
            AutoMigrate = true,
            DevModeRecreate = false,
        });
        builder.Services.AddScoped(_ => Session.CreateContext());

        using (var host = builder.Build())
        {
            host.InitializeDatabase();
        }

        Assert.Empty(log.Entries);
    }
}
