// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Blueprint.Api.Data;
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
