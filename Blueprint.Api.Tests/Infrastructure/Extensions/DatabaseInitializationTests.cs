// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Blueprint.Api.Tests.Infrastructure.Extensions;

/// <summary><c>DatabaseExtensions.InitializeDatabase</c> - the one thing <c>Program.Main</c> does between
/// building the host and running it, and therefore what a fresh blueprint deployment's database is made
/// of.</summary>
public class DatabaseInitializationTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    private const string SeedFileName = "seed.json";

    private DirectoryInfo _contentRoot;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        _contentRoot = Directory.CreateTempSubdirectory("blueprint-seed");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        _contentRoot?.Delete(recursive: true);
    }

    /// <summary>With nothing to migrate or seed, the host comes back and nothing is logged.</summary>
    [Fact]
    public void Initialize_WithNothingToDo_ReturnsTheHostAndSaysNothing()
    {
        var (host, log) = HostFor(Options());

        using (host)
        {
            Assert.Same(host, host.InitializeDatabase());
        }

        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task Initialize_SeedsWhatTheSeedFileHolds()
    {
        WriteSeedFile($$"""
            { "Msels": [ { "Id": "{{Guid.NewGuid()}}", "Name": "Seeded", "Description": "d" } ] }
            """);

        var (host, log) = HostFor(Options());

        using (host)
        {
            host.InitializeDatabase();
        }

        Assert.Empty(log.Entries);
        Assert.Equal("Seeded", (await NewMsels()).Single().Name);
    }

    /// <summary>A seeded MSEL's scoring model is built from the named preset, once, so every MSEL made from it starts with a copy.</summary>
    [Fact]
    public async Task Initialize_SeedsAScoringModelFromItsPreset_Once()
    {
        var mselId = Guid.NewGuid();
        WriteSeedFile($$"""
            { "Msels": [ { "Id": "{{mselId}}", "Name": "Seeded", "Description": "d", "UseCite": true } ],
              "MselScoringModels": [ { "MselId": "{{mselId}}", "Preset": "Nciss" }, { "MselId": "{{Guid.NewGuid()}}", "Preset": "Nciss" }, { "MselId": "{{mselId}}", "Preset": "Bogus" } ] }
            """);

        var (first, log) = HostFor(Options());
        using (first)
        {
            first.InitializeDatabase();
        }
        var (second, _) = HostFor(Options());
        using (second)
        {
            second.InitializeDatabase();
        }

        Assert.Empty(log.Entries);
        await using var context = NewContext();
        var model = await context.MselScoringModels.Include(m => m.Categories).ThenInclude(c => c.Options).AsSplitQuery().SingleAsync(m => m.MselId == mselId, TestContext.Current.CancellationToken);
        Assert.Equal("CISA NCISS", model.Description);
        Assert.Equal(8, model.Categories.Count);
        Assert.Equal(61, model.Categories.Sum(c => c.Options.Count));
        // a preset for a MSEL that is not seeded, or a preset Blueprint does not have, seeds nothing
        Assert.Equal(1, await context.MselScoringModels.CountAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Seeding twice adds each seeded row once and leaves an edited seeded row alone.</summary>
    [Fact]
    public async Task Initialize_Twice_SeedsOnce()
    {
        WriteSeedFile($$"""
            { "Msels": [ { "Id": "{{Guid.NewGuid()}}", "Name": "Seeded", "Description": "d" } ] }
            """);

        var (first, _) = HostFor(Options());
        using (first)
        {
            first.InitializeDatabase();
        }

        var (second, log) = HostFor(Options());
        using (second)
        {
            second.InitializeDatabase();
        }

        Assert.Empty(log.Entries);
        Assert.Single(await NewMsels());
    }

    /// <summary>Initialize with a camel case seed file seeds nothing and says nothing.</summary>
    [Fact]
    public async Task Initialize_WithACamelCaseSeedFile_SeedsNothingAndSaysNothing()
    {
        WriteSeedFile($$"""
            { "msels": [ { "id": "{{Guid.NewGuid()}}", "name": "Seeded", "description": "d" } ] }
            """);

        var (host, log) = HostFor(Options());

        using (host)
        {
            host.InitializeDatabase();
        }

        Assert.Empty(log.Entries);
        Assert.Empty(await NewMsels());
    }

    /// <summary>Initialize when seeding fails part way through keeps what it already saved.</summary>
    [Fact]
    public async Task Initialize_WhenSeedingFailsPartWayThrough_KeepsWhatItAlreadySaved()
    {
        WriteSeedFile($$"""
            {
              "Msels": [ { "Id": "{{Guid.NewGuid()}}", "Name": "Seeded", "Description": "d" } ],
              "Moves": [ { "Id": "{{Guid.NewGuid()}}", "MselId": "{{Guid.NewGuid()}}", "MoveNumber": 1 } ]
            }
            """);

        var (host, log) = HostFor(Options());

        using (host)
        {
            Assert.Same(host, host.InitializeDatabase());
        }

        Assert.Single(log.At(LogLevel.Error));
        Assert.Single(await NewMsels());

        await using var context = NewContext();
        Assert.Empty(await context.Moves.ToListAsync(Ct));
    }

    /// <summary>Initialize with an unreadable seed file logs one error and starts anyway.</summary>
    [Fact]
    public async Task Initialize_WithAnUnreadableSeedFile_LogsOneErrorAndStartsAnyway()
    {
        WriteSeedFile("not json");

        var (host, log) = HostFor(Options());

        using (host)
        {
            Assert.Same(host, host.InitializeDatabase());
        }

        var error = Assert.Single(log.At(LogLevel.Error));

        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Null(error.Exception);
        Assert.Empty(await NewMsels());
    }

    /// <summary>With no context registered, one error is logged and the host is returned.</summary>
    [Fact]
    public void Initialize_WithNoContextRegistered_LogsOneErrorAndStartsAnyway()
    {
        var (host, log) = HostFor(Options(), context: false);

        using (host)
        {
            Assert.Same(host, host.InitializeDatabase());
        }

        Assert.Single(log.At(LogLevel.Error));
    }

    /// <summary>Initialize with no database options blames a null reference.</summary>
    [Fact]
    public void Initialize_WithNoDatabaseOptions_BlamesANullReference()
    {
        var (host, log) = HostFor(options: null);

        using (host)
        {
            host.InitializeDatabase();
        }

        Assert.Contains("Object reference", Assert.Single(log.At(LogLevel.Error)).Message);
    }

    /// <summary>Initialize with an inner exception runs the two messages together.</summary>
    [Fact]
    public void Initialize_WithAnInnerException_RunsTheTwoMessagesTogether()
    {
        var (host, log) = HostFor(
            Options(),
            failure: new InvalidOperationException(
                "The outer sentence.", new InvalidOperationException("The inner sentence.")));

        using (host)
        {
            host.InitializeDatabase();
        }

        Assert.Equal("The outer sentence.The inner sentence.", Assert.Single(log.At(LogLevel.Error)).Message);
    }

    // ---------------------------------------------------------------------------------------------
    // The seed file the image ships
    // ---------------------------------------------------------------------------------------------

    private static readonly Guid IntegratedTemplateId = Guid.Parse("73e1cd7e-c37d-4df2-b5e3-db6125d12365");

    /// <summary>The image seeds from its own seed-data.json, so that file must seed cleanly onto an
    /// empty database.</summary>
    [Fact]
    public async Task Initialize_WithTheShippedSeedFile_SeedsEveryTemplateAndSaysNothing()
    {
        CopyShippedSeedFile();

        var (host, log) = HostFor(Options());

        using (host)
        {
            host.InitializeDatabase();
        }

        Assert.Empty(log.Entries);

        var templates = await NewMsels();
        Assert.All(templates, x => Assert.True(x.IsTemplate));
        Assert.Contains(templates, x => x.Name == "Standard MSEL");
        Assert.Contains(templates, x => x.Id == IntegratedTemplateId);
    }

    /// <summary>A Gallery push stays disabled until every Gallery Article Parameter is on a data field,
    /// so the integration template maps all of them, once each.</summary>
    [Fact]
    public async Task Initialize_WithTheShippedSeedFile_MapsEveryGalleryArticleParameterOnTheIntegrationTemplate()
    {
        CopyShippedSeedFile();
        SeedOnce();

        await using var context = NewContext();
        var mapped = await context.DataFields
            .Where(x => x.MselId == IntegratedTemplateId && x.GalleryArticleParameter != null)
            .Select(x => x.GalleryArticleParameter)
            .ToListAsync(Ct);

        Assert.Equal(
            Enum.GetNames<GalleryArticleParameter>().OrderBy(x => x),
            mapped.OrderBy(x => x));
    }

    /// <summary>The integration template carries what each push needs from the MSEL: CITE reads every
    /// move's situation time, and Player gives each team its applications.</summary>
    [Fact]
    public async Task Initialize_WithTheShippedSeedFile_GivesTheIntegrationTemplateItsTeamsMovesAndApplications()
    {
        CopyShippedSeedFile();
        SeedOnce();

        await using var context = NewContext();
        var msel = await context.Msels.SingleAsync(x => x.Id == IntegratedTemplateId, Ct);
        Assert.True(msel.UsePlayer);
        Assert.True(msel.UseGallery);
        Assert.True(msel.UseCite);
        Assert.True(msel.UseSteamfitter);
        Assert.Null(msel.PlayerViewId);
        Assert.Null(msel.CiteScoringModelId);

        var teams = await context.Teams.Where(x => x.MselId == IntegratedTemplateId).ToListAsync(Ct);
        Assert.Equal(["BLUE", "EXCON"], teams.Select(x => x.ShortName).OrderBy(x => x));

        var moves = await context.Moves.Where(x => x.MselId == IntegratedTemplateId).ToListAsync(Ct);
        Assert.Equal([0, 1, 2], moves.Select(x => x.MoveNumber).OrderBy(x => x));
        Assert.All(moves, x => Assert.NotNull(x.SituationTime));

        var applications = await context.PlayerApplications
            .Include(x => x.PlayerApplicationTeams)
            .Where(x => x.MselId == IntegratedTemplateId)
            .ToListAsync(Ct);
        Assert.Equal(3, applications.Count);
        Assert.All(applications, x => Assert.Equal(
            teams.Select(t => t.Id).OrderBy(t => t),
            x.PlayerApplicationTeams.Select(t => t.TeamId).OrderBy(t => t)));

        var card = await context.Cards.Include(x => x.CardTeams).SingleAsync(x => x.MselId == IntegratedTemplateId, Ct);
        Assert.Equal(2, card.CardTeams.Count);

        // the copy dereferences each CITE row's team, so none may be missing
        Assert.All(
            await context.CiteDuties.Where(x => x.MselId == IntegratedTemplateId).ToListAsync(Ct),
            x => Assert.Contains(teams, t => t.Id == x.TeamId));
        Assert.All(
            await context.CiteActions.Where(x => x.MselId == IntegratedTemplateId).ToListAsync(Ct),
            x => Assert.Contains(teams, t => t.Id == x.TeamId));
    }

    /// <summary>The shipped inject type's fields carry the template's names and types, which is how an
    /// inject from a catalog fills the template's fields.</summary>
    [Fact]
    public async Task Initialize_WithTheShippedSeedFile_SeedsAGalleryArticleInjectTypeThatMatchesTheTemplate()
    {
        CopyShippedSeedFile();
        SeedOnce();

        await using var context = NewContext();
        var injectType = await context.InjectTypes.SingleAsync(x => x.Name == "Gallery Article", Ct);
        var injectTypeFields = await context.DataFields.Where(x => x.InjectTypeId == injectType.Id).ToListAsync(Ct);
        var templateFields = await context.DataFields.Where(x => x.MselId == IntegratedTemplateId).ToListAsync(Ct);

        Assert.NotEmpty(injectTypeFields);
        Assert.All(injectTypeFields, x => Assert.Contains(
            templateFields, t => t.Name == x.Name && t.DataType == x.DataType));
    }

    /// <summary>Restarting over the shipped seed adds nothing and leaves an edited seeded row alone.</summary>
    [Fact]
    public async Task Initialize_WithTheShippedSeedFileTwice_SeedsOnceAndKeepsEdits()
    {
        CopyShippedSeedFile();
        SeedOnce();

        await using (var edit = NewContext())
        {
            var application = await edit.PlayerApplications.FirstAsync(x => x.MselId == IntegratedTemplateId, Ct);
            application.Url = "https://edited.example/";
            await edit.SaveChangesAsync(Ct);
        }

        var before = await Counts();

        var (host, log) = HostFor(Options());
        using (host)
        {
            host.InitializeDatabase();
        }

        Assert.Empty(log.Entries);
        Assert.Equal(before, await Counts());

        await using var context = NewContext();
        Assert.Contains(
            await context.PlayerApplications.Where(x => x.MselId == IntegratedTemplateId).ToListAsync(Ct),
            x => x.Url == "https://edited.example/");
    }

    // ---------------------------------------------------------------------------------------------
    // Seed types and their keys
    // ---------------------------------------------------------------------------------------------

    /// <summary>A team belongs to a MSEL, so a seed file that holds both seeds both on an empty
    /// database.</summary>
    [Fact]
    public async Task Initialize_WithATeamOfASeededMsel_SeedsBoth()
    {
        var mselId = Guid.NewGuid();
        WriteSeedFile($$"""
            {
              "Msels": [ { "Id": "{{mselId}}", "Name": "Seeded", "Description": "d" } ],
              "Teams": [ { "Id": "{{Guid.NewGuid()}}", "MselId": "{{mselId}}", "Name": "Blue Team", "ShortName": "BLUE" } ]
            }
            """);

        var (host, log) = HostFor(Options());
        using (host)
        {
            host.InitializeDatabase();
        }

        Assert.Empty(log.Entries);

        await using var context = NewContext();
        Assert.Equal(mselId, (await context.Teams.SingleAsync(Ct)).MselId);
    }

    /// <summary>Inject type names are unique, so a seeded inject type whose name is already taken is
    /// skipped instead of failing the seed.</summary>
    [Fact]
    public async Task Initialize_WithAnInjectTypeWhoseNameIsTaken_SkipsIt()
    {
        var existing = Guid.NewGuid();
        await using (var context = NewContext())
        {
            context.InjectTypes.Add(new InjectTypeEntity { Id = existing, Name = "Gallery Article", Description = "mine" });
            await context.SaveChangesAsync(Ct);
        }

        WriteSeedFile($$"""
            { "InjectTypes": [ { "Id": "{{Guid.NewGuid()}}", "Name": "Gallery Article", "Description": "seeded" } ] }
            """);

        var (host, log) = HostFor(Options());
        using (host)
        {
            host.InitializeDatabase();
        }

        Assert.Empty(log.Entries);

        await using var read = NewContext();
        var injectType = await read.InjectTypes.SingleAsync(Ct);
        Assert.Equal(existing, injectType.Id);
        Assert.Equal("mine", injectType.Description);
    }

    /// <summary>A site that already has its own "Gallery Article" inject type still gets the whole
    /// integration template: the seeded inject type's fields are skipped with it, instead of failing
    /// the save the template's fields share.</summary>
    [Fact]
    public async Task Initialize_WithTheShippedSeedFile_WhenTheInjectTypeNameIsTaken_StillSeedsTheTemplate()
    {
        var existing = Guid.NewGuid();
        await using (var context = NewContext())
        {
            context.InjectTypes.Add(new InjectTypeEntity { Id = existing, Name = "Gallery Article", Description = "mine" });
            await context.SaveChangesAsync(Ct);
        }

        CopyShippedSeedFile();
        SeedOnce();

        await using var read = NewContext();
        Assert.Equal(existing, (await read.InjectTypes.SingleAsync(x => x.Name == "Gallery Article", Ct)).Id);
        Assert.Empty(await read.DataFields.Where(x => x.InjectTypeId == existing).ToListAsync(Ct));
        Assert.Equal(12, await read.DataFields.CountAsync(x => x.MselId == IntegratedTemplateId, Ct));
        Assert.Equal(3, await read.PlayerApplications.CountAsync(x => x.MselId == IntegratedTemplateId, Ct));
    }

    /// <summary>A team gets an application once and a card once, so a seeded pair that already exists
    /// under another id is skipped.</summary>
    [Fact]
    public async Task Initialize_WithAnApplicationTeamOrCardTeamPairThatExists_SkipsIt()
    {
        var mselId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var applicationId = Guid.NewGuid();
        var cardId = Guid.NewGuid();
        var rows = $$"""
              "Msels": [ { "Id": "{{mselId}}", "Name": "Seeded", "Description": "d" } ],
              "Teams": [ { "Id": "{{teamId}}", "MselId": "{{mselId}}", "Name": "Blue Team", "ShortName": "BLUE" } ],
              "Cards": [ { "Id": "{{cardId}}", "MselId": "{{mselId}}", "Name": "Card" } ],
              "PlayerApplications": [ { "Id": "{{applicationId}}", "MselId": "{{mselId}}", "Name": "App", "Url": "https://app.example/" } ],
            """;

        WriteSeedFile($$"""
            {
            {{rows}}
              "CardTeams": [ { "Id": "{{Guid.NewGuid()}}", "CardId": "{{cardId}}", "TeamId": "{{teamId}}" } ],
              "PlayerApplicationTeams": [ { "Id": "{{Guid.NewGuid()}}", "PlayerApplicationId": "{{applicationId}}", "TeamId": "{{teamId}}" } ]
            }
            """);
        SeedOnce();

        WriteSeedFile($$"""
            {
            {{rows}}
              "CardTeams": [ { "Id": "{{Guid.NewGuid()}}", "CardId": "{{cardId}}", "TeamId": "{{teamId}}" } ],
              "PlayerApplicationTeams": [ { "Id": "{{Guid.NewGuid()}}", "PlayerApplicationId": "{{applicationId}}", "TeamId": "{{teamId}}" } ]
            }
            """);

        var (host, log) = HostFor(Options());
        using (host)
        {
            host.InitializeDatabase();
        }

        Assert.Empty(log.Entries);

        await using var context = NewContext();
        Assert.Single(await context.CardTeams.ToListAsync(Ct));
        Assert.Single(await context.PlayerApplicationTeams.ToListAsync(Ct));
    }

    private void CopyShippedSeedFile() =>
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "seed-data.json"),
            Path.Combine(_contentRoot.FullName, SeedFileName));

    private void SeedOnce()
    {
        var (host, log) = HostFor(Options());
        using (host)
        {
            host.InitializeDatabase();
        }

        Assert.Empty(log.Entries);
    }

    private async Task<string> Counts()
    {
        await using var context = NewContext();

        return string.Join(",",
            await context.Msels.CountAsync(Ct),
            await context.Teams.CountAsync(Ct),
            await context.Moves.CountAsync(Ct),
            await context.InjectTypes.CountAsync(Ct),
            await context.DataFields.CountAsync(Ct),
            await context.DataOptions.CountAsync(Ct),
            await context.Organizations.CountAsync(Ct),
            await context.Cards.CountAsync(Ct),
            await context.CardTeams.CountAsync(Ct),
            await context.CiteDuties.CountAsync(Ct),
            await context.CiteActions.CountAsync(Ct),
            await context.PlayerApplications.CountAsync(Ct),
            await context.PlayerApplicationTeams.CountAsync(Ct));
    }

    private DatabaseOptions Options() => new()
    {
        Provider = "PostgreSQL",
        SeedFile = SeedFileName,
        AutoMigrate = true,
        DevModeRecreate = false,
    };

    private void WriteSeedFile(string contents) =>
        File.WriteAllText(Path.Combine(_contentRoot.FullName, SeedFileName), contents);

    private async Task<System.Collections.Generic.List<Data.Models.MselEntity>> NewMsels()
    {
        await using var context = NewContext();

        return await context.Msels.ToListAsync(Ct);
    }

    /// <summary>
    /// The smallest host <c>InitializeDatabase</c> will accept, over this test's own database.
    /// </summary>
    /// <param name="options">The options, or null to leave <c>DatabaseOptions</c> unregistered.</param>
    /// <param name="context">Whether to register a <c>BlueprintContext</c> at all.</param>
    /// <param name="failure">An exception the context registration throws instead of building one.</param>
    private (IHost Host, RecordingLogger<Blueprint.Api.Program> Log) HostFor(
        DatabaseOptions options, bool context = true, Exception failure = null)
    {
        var log = new RecordingLogger<Blueprint.Api.Program>();

        var builder = Host.CreateEmptyApplicationBuilder(
            new HostApplicationBuilderSettings { ContentRootPath = _contentRoot.FullName });

        builder.Services.AddSingleton<ILogger<Blueprint.Api.Program>>(log);

        if (options is not null)
        {
            builder.Services.AddSingleton(options);
        }

        if (context)
        {
            builder.Services.AddScoped(
                _ => failure is null ? Session.CreateContext() : throw failure);
        }

        return (builder.Build(), log);
    }
}
