// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Blueprint.Api.Tests.Infrastructure;

/// <summary>
/// Owns the database for the whole test run: starts PostgreSQL once, migrates a template, and hands
/// out an isolated database per test.
/// </summary>
/// <remarks>
/// <para>
/// PostgreSQL is the only database these tests run against. There is deliberately no in-memory or
/// SQLite fallback: a fallback that quietly swaps the provider reports a green run that never
/// exercised what production uses. In blueprint that is more than a preference - there is no SQLite
/// migrations project to run, the <c>if (Database.IsNpgsql())</c> branch of
/// <see cref="BlueprintContext.OnModelCreating"/> is what applies snake_case casing and
/// <c>uuid_generate_v4()</c> key defaults, and nearly every write in the service layer wraps an
/// explicit <c>BeginTransactionAsync</c>, which the in-memory provider does not support at all. Docker
/// is therefore a hard requirement, and a machine without it fails the run rather than degrading it.
/// </para>
/// <para>
/// Migrations are applied once, to a template database, and each test gets its own database created
/// from that template. <c>CREATE DATABASE ... TEMPLATE</c> is a file-level copy, so it costs
/// milliseconds where re-running the 77 migrations costs seconds.
/// </para>
/// <para>
/// Isolation is per-database rather than a rolled-back transaction per test on purpose. The
/// <c>EntityEventInterceptor</c> publishes entity events on <c>TransactionCommitted</c> when a
/// transaction is in progress and discards its tracked state on <c>TransactionRolledBack</c>, so
/// wrapping a test in a transaction would silently stop entity events from firing - and in blueprint
/// almost every service method commits one, so it would stop nearly all of them.
/// </para>
/// </remarks>
public sealed class DatabaseFixture : IAsyncLifetime
{
    /// <summary>
    /// Matches the image the player.api and vm.api suites use, so the Crucible suites are not proving
    /// things against different servers.
    /// </summary>
    private const string PostgresImage = "postgres:16-alpine";

    private const string TemplateDatabase = "blueprint_template";

    /// <summary>
    /// Cloning and dropping have to connect to something other than the database being cloned or
    /// dropped. The official image always has this one.
    /// </summary>
    private const string MaintenanceDatabase = "postgres";

    private readonly Lazy<Task> _started;

    /// <remarks>
    /// Assigned in <see cref="StartAsync"/> rather than by a field initializer, and the reason is not
    /// style: <c>PostgreSqlBuilder.Build()</c> resolves the Docker endpoint, so on a machine without
    /// Docker it throws <c>DockerUnavailableException</c> from this fixture's *constructor*. That fails
    /// every test in the assembly - including the ones that never touch a database - and it happens
    /// before the <see cref="InvalidOperationException"/> below can explain any of it.
    /// </remarks>
    private PostgreSqlContainer _container;

    private int _databaseCount;

    public DatabaseFixture()
    {
        // Lazy<Task> caches the resulting Task, faulted included, so a machine without Docker gets one
        // container-start attempt and one error rather than a fresh timeout per test.
        _started = new Lazy<Task>(StartAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <remarks>
    /// Deliberately empty: the container is started by the first caller that actually asks for a
    /// database, not here. An assembly fixture that throws in <c>InitializeAsync</c> fails *every* test
    /// in the assembly, and many of these tests never touch a database - so starting eagerly would mean
    /// a contributor without Docker could not run the unit tests either. Starting lazily keeps the hard
    /// requirement exactly where it belongs: on the tests that need a database.
    /// </remarks>
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Hands out an isolated database for a single caller, cloned from the migrated template. Starts
    /// PostgreSQL and migrates the template on first call.
    /// </summary>
    /// <remarks>
    /// The clone is not serialized. <c>CREATE DATABASE ... TEMPLATE</c> is documented as failing when
    /// another session holds the template, which reads like it needs a lock around it, but PostgreSQL
    /// serializes concurrent clones of an idle template itself. What breaks it is a *connection*, not a
    /// concurrent clone, which <see cref="StartAsync"/> deals with.
    /// </remarks>
    public async Task<TestDatabaseSession> BeginSessionAsync()
    {
        await _started.Value;

        var databaseName = $"blueprint_test_{Interlocked.Increment(ref _databaseCount)}";

        await ExecuteMaintenanceAsync(
            $"""CREATE DATABASE "{databaseName}" TEMPLATE "{TemplateDatabase}";""");

        var (services, mediator) = BlueprintContextFactory.CreateServices();

        return new TestDatabaseSession(this, databaseName, services, mediator);
    }

    public async ValueTask DisposeAsync()
    {
        // Nothing to tear down if no test ever asked for a database, and nothing to tear down if the
        // start faulted before the container was built.
        if (_started.IsValueCreated && _container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <remarks>
    /// No cancellation token is passed to the container start. The token available here belongs to
    /// whichever test happened to ask first, and cancelling that test would poison the cached task for
    /// every test after it; the container has its own start timeout.
    /// </remarks>
    private async Task StartAsync()
    {
        try
        {
            // Both inside the try: building resolves the Docker endpoint and fails without it, so this
            // is where "no Docker" has to be caught, not just where the container fails to start.
            _container = new PostgreSqlBuilder(PostgresImage)
                .WithDatabase(TemplateDatabase)
                .Build();

            await _container.StartAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not start {PostgresImage} in Docker, which these tests require. There is " +
                "deliberately no in-memory or SQLite fallback - one would report a green run that " +
                "never touched the database production uses. Start Docker and run again; see " +
                "docs/Testing.md.",
                ex);
        }

        var (services, _) = BlueprintContextFactory.CreateServices();

        await using (var context = BlueprintContextFactory.CreateContext(
            ConnectionStringFor(TemplateDatabase), services))
        {
            await context.Database.MigrateAsync();
        }

        // Load-bearing. CREATE DATABASE ... TEMPLATE fails while any session is connected to the
        // template, and disposing an NpgsqlConnection returns it to the pool rather than closing the
        // socket - so without this, every clone would fail. Nothing may connect to the template again
        // after this point, which is why the hosted application gets its own database (see
        // BlueprintAppFactory) instead of being pointed at it.
        NpgsqlConnection.ClearAllPools();

        TestContext.Current.SendDiagnosticMessage(
            $"[Blueprint.Api.Tests] {PostgresImage} started; template '{TemplateDatabase}' migrated");
    }

    internal string ConnectionStringFor(string databaseName) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = databaseName
        }.ConnectionString;

    internal async Task DropDatabaseAsync(string databaseName)
    {
        // Only this database's pool. ClearAllPools would churn connections belonging to tests running
        // in parallel.
        await using (var pooled = new NpgsqlConnection(ConnectionStringFor(databaseName)))
        {
            NpgsqlConnection.ClearPool(pooled);
        }

        // FORCE (PostgreSQL 13+) terminates any session that outlived its test rather than failing the
        // drop, which keeps teardown from turning into a flaky failure in an unrelated test.
        await ExecuteMaintenanceAsync($"""DROP DATABASE IF EXISTS "{databaseName}" WITH (FORCE);""");
    }

    private async Task ExecuteMaintenanceAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionStringFor(MaintenanceDatabase));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// One caller's isolated database, dropped when it is disposed.
/// </summary>
public sealed class TestDatabaseSession(
    DatabaseFixture fixture,
    string databaseName,
    IServiceProvider services,
    IMediator mediator) : IAsyncDisposable
{
    /// <summary>
    /// The substituted <see cref="IMediator"/> that <c>BlueprintContext.PublishEventsAsync</c> resolves
    /// for contexts created without an explicit provider. Assert on it to verify entity events.
    /// </summary>
    public IMediator Mediator { get; } = mediator;

    /// <summary>The name of this session's database, for the harness's own tests.</summary>
    public string DatabaseName { get; } = databaseName;

    /// <summary>
    /// This session's database as a connection string, for handing to configuration rather than to a
    /// context - <see cref="BlueprintAppFactory"/> gives it to the hosted application as
    /// <c>ConnectionStrings:PostgreSQL</c>.
    /// </summary>
    public string ConnectionString => fixture.ConnectionStringFor(DatabaseName);

    /// <summary>
    /// A context over this session's database. Call more than once when a test needs to re-read
    /// through a cold change tracker.
    /// </summary>
    public BlueprintContext CreateContext() => CreateContext(services);

    /// <summary>
    /// A context whose <c>ServiceProvider</c> is <paramref name="provider"/>, so that
    /// <c>PublishEventsAsync</c> resolves the mediator out of it.
    /// </summary>
    /// <remarks>
    /// This is how a request gets a context: <see cref="BlueprintAppFactory"/> passes the request scope,
    /// so entity events reach the application's real handlers rather than <see cref="Mediator"/>.
    /// </remarks>
    public BlueprintContext CreateContext(IServiceProvider provider) =>
        BlueprintContextFactory.CreateContext(fixture.ConnectionStringFor(DatabaseName), provider);

    public async ValueTask DisposeAsync() => await fixture.DropDatabaseAsync(DatabaseName);
}
