// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Threading.Tasks;
using Blueprint.Api.Data;

namespace Blueprint.Api.Tests.Support;

/// <summary>
/// Owns the PostgreSQL database for the whole test run: starts it on first use and hands out an isolated
/// session per test.
/// </summary>
/// <remarks>
/// PostgreSQL exercises production's actual database, including the <c>if (Database.IsNpgsql())</c>
/// branch of <c>BlueprintContext.OnModelCreating</c> (snake_case naming, <c>uuid_generate_v4()</c> key
/// defaults) and the real migration history. Nearly every write in the service layer opens an explicit
/// transaction, which no in-memory provider supports. A usable Docker daemon is therefore required by every
/// test that takes a database.
/// </remarks>
public sealed class DatabaseFixture : IAsyncLifetime, ITestDatabaseSessionSource<BlueprintContext>
{
    private readonly PostgresTestDatabase<BlueprintContext> _database = new(new()
    {
        Name = "blueprint",
        TestAssembly = "Blueprint.Api.Tests",
        // Production computes this as {AssemblyName}.Migrations.{provider} in
        // DatabaseExtensions.UseConfiguredDatabase. Without it EF looks in Blueprint.Api.Data and finds none.
        MigrationsAssembly = "Blueprint.Api.Migrations.PostgreSQL",
        CreateContext = BlueprintContextFactory.CreateContext,
        CreateServices = BlueprintContextFactory.CreateServices
    });

    /// <summary>Nothing to do here: the container starts on the first request for a session.</summary>
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public Task<ITestDatabaseSession<BlueprintContext>> BeginSessionAsync() => _database.BeginSessionAsync();

    public ValueTask DisposeAsync() => _database.DisposeAsync();
}
