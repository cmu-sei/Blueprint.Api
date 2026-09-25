// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>DatabaseExtensions.InitializeDatabase</c> - the one thing <c>Program.Main</c> does between building
/// the host and running it, and therefore what a fresh blueprint deployment's database is made of.
/// </summary>
/// <remarks>
/// <para>
/// Driven over a real host built by hand rather than through <see cref="BlueprintAppFactory"/>, because
/// the factory exists to keep this method away from the test database: <c>Program.Main</c> is invoked on
/// a background thread where <c>StopTheHostException</c> is thrown at <c>Build()</c>, so under the factory
/// the method never runs. A hand-built host is the only way to reach it, and the host here is the smallest
/// one it will accept - a <c>DatabaseOptions</c>, a <c>BlueprintContext</c>, an
/// <c>ILogger&lt;Program&gt;</c> and a content root.
/// </para>
/// <para>
/// BUG, recorded here rather than tested because nothing observes it: <c>DatabaseOptions.AutoMigrate</c>
/// is bound from configuration and <c>appsettings.json</c> ships it <c>true</c>, and no line in the
/// repository reads it. <c>Migrate()</c> is unconditional for any provider that is not Sqlite, so the
/// switch that appears to govern whether a deployment migrates itself governs nothing.
/// </para>
/// </remarks>
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

    /// <remarks>
    /// The migration is a no-op here - the test's database is a clone of the fixture's already-migrated
    /// template - so what this pins is the quiet path: the host comes back, and nothing is logged.
    /// Everything below is a departure from it.
    /// </remarks>
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

    /// <remarks>
    /// Every collection is reconciled by id before it is added, so a deployment that restarts does not
    /// duplicate its own seed - and an operator editing a seeded row has their edit preserved rather than
    /// reverted, since a row whose id is already present is skipped entirely.
    /// </remarks>
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

    /// <remarks>
    /// BUG: the seed file is read with a bare <c>JsonSerializer.Deserialize</c>, so its property names are
    /// matched case-sensitively against the CLR names - the only place in blueprint where the wire format
    /// is PascalCase and unforgiving of anything else. A file written in the camelCase every API response
    /// uses deserializes into an object whose every collection is null, which
    /// <c>ProcessSeedDataOptions</c> then walks without complaint. So the whole seed is silently
    /// discarded and the deployment starts empty. One <c>PropertyNameCaseInsensitive = true</c> fixes it.
    /// </remarks>
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

    /// <remarks>
    /// BUG: the collections are saved one at a time, so a seed file that fails part-way through leaves
    /// everything before the failure committed - and the host starts anyway, on a database holding half a
    /// seed. The only evidence is one log line, and the failure is not retried on the next start, because
    /// the rows that did save are then skipped as already present. A single <c>SaveChanges</c> at the end,
    /// or an explicit transaction, would make the seed all-or-nothing.
    /// </remarks>
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

        Assert.Single(log.Errors);
        Assert.Single(await NewMsels());

        await using var context = NewContext();
        Assert.Empty(await context.Moves.ToListAsync(Ct));
    }

    /// <remarks>
    /// BUG: a seed file that is not valid JSON at all is one log line and a successful start. Every
    /// failure this method can suffer is the same - the <c>try</c> wraps the whole body and the
    /// <c>catch</c> logs and falls through to <c>return webHost</c> - so nothing distinguishes "the
    /// database is ready" from "the database could not be migrated". Rethrowing would make a broken
    /// deployment fail to start, which is what an operator wants from a migration step.
    /// </remarks>
    [Fact]
    public async Task Initialize_WithAnUnreadableSeedFile_LogsOneErrorAndStartsAnyway()
    {
        WriteSeedFile("not json");

        var (host, log) = HostFor(Options());

        using (host)
        {
            Assert.Same(host, host.InitializeDatabase());
        }

        var error = Assert.Single(log.Errors);

        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Null(error.Exception);
        Assert.Empty(await NewMsels());
    }

    /// <remarks>
    /// The same swallow from the other end: with no context registered at all the method cannot do
    /// anything whatever, and still answers by returning the host.
    /// </remarks>
    [Fact]
    public void Initialize_WithNoContextRegistered_LogsOneErrorAndStartsAnyway()
    {
        var (host, log) = HostFor(Options(), context: false);

        using (host)
        {
            Assert.Same(host, host.InitializeDatabase());
        }

        Assert.Single(log.Errors);
    }

    /// <remarks>
    /// BUG: <c>DatabaseOptions</c> is fetched with <c>GetService</c> rather than
    /// <c>GetRequiredService</c> and then dereferenced two lines later, so a deployment whose
    /// <c>Database</c> section is missing gets a <c>NullReferenceException</c> - logged, with no mention
    /// of configuration - and starts unmigrated and unseeded. The exception is raised before
    /// <c>Migrate()</c>, so this is the one branch where the swallow hides an unmigrated database rather
    /// than only an unseeded one.
    /// </remarks>
    [Fact]
    public void Initialize_WithNoDatabaseOptions_BlamesANullReference()
    {
        var (host, log) = HostFor(options: null);

        using (host)
        {
            host.InitializeDatabase();
        }

        Assert.Contains("Object reference", Assert.Single(log.Errors).Message);
    }

    /// <remarks>
    /// BUG: the inner exception's message is appended to the outer one with no separator, so the line an
    /// operator reads runs two sentences together - <c>"…saving the changes.An error occurred…"</c>. That
    /// is the shape of every database failure here, <c>DbUpdateException</c> always carrying the provider
    /// exception that explains it, and it is why the part of the message that says what actually went
    /// wrong is the hardest part to find. Only one level is unwrapped, so a third is lost. Logging the
    /// exception rather than its message - <c>logger.LogError(ex, "…")</c>, which is also what the
    /// argument is for - would give the whole chain and a stack trace.
    /// </remarks>
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

        Assert.Equal("The outer sentence.The inner sentence.", Assert.Single(log.Errors).Message);
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
