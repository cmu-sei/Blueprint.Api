// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NSubstitute;
using Xunit;

namespace Blueprint.Api.Tests.Infrastructure;

/// <summary>
/// Tests for the harness itself. Each one guards a property the suite's other tests silently rely on, and
/// which would degrade without failing anything: the wrong provider, a shared database, migrations that
/// never ran, requests reaching a database no test owns.
/// </summary>
public class DatabaseHarnessTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    /// <summary>
    /// The guarantee the whole programme rests on. If this ever reports anything but Npgsql, the suite is
    /// proving things against a database production does not use.
    /// </summary>
    [Fact]
    public void TheProviderIsPostgreSql()
    {
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", Db.Database.ProviderName);
        Assert.True(Db.Database.IsNpgsql());
    }

    /// <summary>
    /// All 77 migrations ran, rather than <c>EnsureCreated</c> having quietly produced a schema from the
    /// model. The distinction matters: a migration that does not match the model is exactly the bug a real
    /// migration run catches.
    /// </summary>
    /// <remarks>
    /// This also pins <c>BlueprintContextFactory</c>'s
    /// <c>MigrationsAssembly("Blueprint.Api.Migrations.PostgreSQL")</c>. Without it EF looks for
    /// migrations in <c>Blueprint.Api.Data</c>, where the context lives and no migration does, and
    /// migrating would succeed while creating nothing.
    /// </remarks>
    [Fact]
    public async Task EveryMigrationIsApplied()
    {
        var applied = await Db.Database.GetAppliedMigrationsAsync(Ct);
        var pending = await Db.Database.GetPendingMigrationsAsync(Ct);

        Assert.NotEmpty(applied);
        Assert.Empty(pending);
    }

    /// <summary>
    /// <c>UsePostgresCasing</c> runs only inside the <c>if (Database.IsNpgsql())</c> branch of
    /// <c>BlueprintContext.OnModelCreating</c>, so no other provider would exercise it. Read from the
    /// catalog rather than from the model, so this is what was actually created.
    /// </summary>
    [Fact]
    public async Task TablesAndColumnsUseSnakeCase()
    {
        var columns = await Db.Database
            .SqlQuery<string>(
                $"""
                SELECT column_name FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'organizations'
                """)
            .ToListAsync(Ct);

        Assert.Contains("short_name", columns);
        Assert.Contains("is_template", columns);
        Assert.Contains("msel_id", columns);
        Assert.DoesNotContain("ShortName", columns);
    }

    /// <summary>
    /// <c>AddPostgresUUIDGeneration</c> gives Guid keys a <c>uuid_generate_v4()</c> default, which needs
    /// the <c>uuid-ossp</c> extension and so needs the container's user to stay a superuser.
    /// </summary>
    [Fact]
    public async Task AGuidKeyIsGeneratedByTheStore()
    {
        var organization = new OrganizationEntity { Name = "store-generated", IsTemplate = true };

        await Seed(organization);

        Assert.NotEqual(Guid.Empty, organization.Id);
    }

    /// <summary>
    /// The three roles <c>SystemRoleConfiguration.HasData</c> seeds are in the migrated template, and so
    /// in every test's database.
    /// </summary>
    /// <remarks>
    /// Load-bearing for <see cref="TestActorBuilder.WithAllSystemPermissions"/>, which grants all 28
    /// permissions by pointing a user at the Administrator row rather than writing one. If this fails, so
    /// does every test that needs a privileged actor - and each of those would fail as a 403 that looks
    /// like an authorization bug.
    /// </remarks>
    [Fact]
    public async Task TheSeededSystemRolesAreInEveryDatabase()
    {
        var roles = await Db.SystemRoles.AsNoTracking().ToListAsync(Ct);

        Assert.Equal(3, roles.Count);

        var administrator = Assert.Single(roles, x => x.Id == SystemRoleDefaults.AdministratorRoleId);

        Assert.True(administrator.AllPermissions);
        Assert.True(administrator.Immutable);

        Assert.Contains(roles, x => x.Id == SystemRoleDefaults.ContentDeveloperRoleId);
        Assert.Contains(roles, x => x.Id == SystemRoleDefaults.ObserverRoleId);
    }

    /// <summary>
    /// Real constraints are enforced, which is a large part of why this is a real server. An in-memory
    /// provider accepts this row.
    /// </summary>
    [Fact]
    public async Task AForeignKeyToAMissingMselIsRejected()
    {
        Db.Teams.Add(new TeamEntity { Name = "orphan", MselId = Guid.NewGuid() });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => Db.SaveChangesAsync(Ct));

        var postgres = Assert.IsType<PostgresException>(ex.InnerException);

        Assert.Equal("23503", postgres.SqlState);
    }

    /// <summary>
    /// The property that replaces the usual "scope your assertions to rows you seeded" rule: this test can
    /// assert on the whole table, because no other test's rows are in it.
    /// </summary>
    [Fact]
    public async Task ThisTestSeesOnlyItsOwnRows()
    {
        await Seed(BlueprintAppFactory.Organization(), BlueprintAppFactory.Organization());

        await using var context = NewContext();

        Assert.Equal(2, await context.Organizations.CountAsync(Ct));
    }

    /// <summary>
    /// A save through <see cref="DatabaseTestBase.Db"/> publishes its entity events to
    /// <see cref="DatabaseTestBase.Mediator"/>, which is what lets a service test assert on them without a
    /// host.
    /// </summary>
    /// <remarks>
    /// This is the interceptor wiring in <c>BlueprintContextFactory</c>, and nothing else would notice it
    /// missing: a context with no <c>EntityEventInterceptor</c> saves perfectly well and simply never
    /// notifies anyone.
    /// </remarks>
    [Fact]
    public async Task ASaveOnATestContextPublishesToTheSubstituteMediator()
    {
        await Seed(BlueprintAppFactory.Organization());

        await Mediator.Received().Publish(Arg.Any<INotification>(), Arg.Any<System.Threading.CancellationToken>());
    }

    /// <summary>
    /// A request writes to the database of the test that made it, and not to the throwaway database the
    /// host was given at startup. That fallback exists for a context resolved outside a request, and this
    /// is what keeps it from quietly becoming the database requests use.
    /// </summary>
    [Fact]
    public async Task ARequestWritesToThisTestsDatabase()
    {
        Assert.NotEqual(Factory.HostDatabaseName, Session.DatabaseName);

        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageOrganizations)
            .SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/organizations",
            new { Name = "written-by-a-request", ShortName = "wbar", Email = "wbar@organization.test" },
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Read back through a context of this test's own. The row being here means the request's context
        // reached this test's database, since it is the only one that could hold it.
        await using var context = NewContext();
        var stored = await context.Organizations.AsNoTracking()
            .SingleAsync(x => x.Name == "written-by-a-request", Ct);

        Assert.Equal(actor.Id, stored.CreatedBy);
    }

    /// <summary>
    /// A request with no session header must fail loudly and name the header, rather than fall through to
    /// the host's database. That message is the whole reason routing is by header rather than by an
    /// ambient value.
    /// </summary>
    [Fact]
    public async Task ARequestWithNoSessionHeaderIsRefused()
    {
        using var unrouted = Factory.CreateClientFor(Guid.NewGuid());

        // The developer exception page turns the InvalidOperationException into a 500. What matters is
        // that the request did not quietly succeed against some other database.
        var response = await unrouted.GetAsync("/api/organizations/templates", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }
}
