// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// App-specific: the isolation probes use system_roles.name, which the migrations index uniquely. Beyond the
// template: store-generated keys, the three seeded system roles, a real foreign key, snake_case columns.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Models;
using Crucible.Common.EntityEvents.Events;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Blueprint.Api.Tests.Support;

/// <summary>
/// Tests for the harness itself. Every other test trusts it, and a harness that quietly does nothing reads
/// as a green suite.
/// </summary>
public class DatabaseHarnessTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    /// <summary>
    /// A value of a uniquely indexed column (<c>system_roles.name</c>): if the two <c>Duplicates_*</c> tests
    /// shared a database, whichever ran second would fail. <see cref="A_second_row_with_the_isolation_key_is_refused"/>
    /// proves the index is there.
    /// </summary>
    private const string SharedKey = "Isolation Probe";

    [Fact]
    public async Task Saved_entities_survive_a_new_context()
    {
        var entity = TestData.User();
        await Seed(entity);

        await using var context = NewContext();
        Assert.NotNull(await context.Users.SingleOrDefaultAsync(x => x.Id == entity.Id, Ct));
    }

    [Fact]
    public async Task Duplicates_across_tests_are_isolated_first()
    {
        await Seed(TestData.SystemRole(SharedKey));

        Assert.Equal(1, await Db.SystemRoles.CountAsync(x => x.Name == SharedKey, Ct));
    }

    [Fact]
    public async Task Duplicates_across_tests_are_isolated_second()
    {
        await Seed(TestData.SystemRole(SharedKey));

        Assert.Equal(1, await Db.SystemRoles.CountAsync(x => x.Name == SharedKey, Ct));
    }

    /// <summary>
    /// The probes above are an isolation check only because the database refuses a second row with the
    /// same key; this proves the unique index on the probe column is in force.
    /// </summary>
    [Fact]
    public async Task A_second_row_with_the_isolation_key_is_refused()
    {
        await Seed(TestData.SystemRole(SharedKey));
        await using var context = NewContext();
        context.SystemRoles.Add(TestData.SystemRole(SharedKey));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [Fact]
    public async Task Explicit_ids_survive_the_round_trip()
    {
        var entity = TestData.User();
        var assignedId = entity.Id;

        await Seed(entity);

        await using var context = NewContext();
        Assert.NotNull(await context.Users.FindAsync([assignedId], Ct));
    }

    [Fact]
    public async Task The_seeded_administrator_role_is_present()
    {
        Assert.True(await Db.SystemRoles.AnyAsync(x => x.Id == TestData.Roles.Administrator, Ct));
    }

    /// <summary>The three system roles the migrations seed, with the administrator's two flags.</summary>
    [Fact]
    public async Task The_three_seeded_system_roles_are_in_every_database()
    {
        var roles = await Db.SystemRoles.AsNoTracking().ToListAsync(Ct);

        var administrator = Assert.Single(roles, x => x.Id == TestData.Roles.Administrator);
        Assert.Equal(3, roles.Count);
        Assert.True(administrator.AllPermissions);
        Assert.True(administrator.Immutable);
        Assert.Contains(roles, x => x.Id == TestData.Roles.ContentDeveloper);
        Assert.Contains(roles, x => x.Id == TestData.Roles.Observer);
    }

    /// <summary>
    /// Entity events must publish. This is what ruled out transaction-per-test isolation: the
    /// interceptor defers publishing to TransactionCommitted and discards it on rollback.
    /// </summary>
    [Fact]
    public async Task Saving_publishes_entity_events()
    {
        await Seed(TestData.User());

        // INotification, not object: the context casts to INotification before publishing, which binds
        // the generic Publish overload, and a substitute records the two separately.
        await Mediator.Received(1).Publish(
            Arg.Is<INotification>(x => x is EntityCreated<UserEntity>),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Uses_the_real_postgres_provider()
    {
        Assert.True(Db.Database.IsNpgsql());
    }

    [Fact]
    public void Applies_postgres_snake_case_naming()
    {
        var entityType = Db.Model.FindEntityType(typeof(UserEntity));

        Assert.Equal("users", entityType.GetTableName());
    }

    /// <summary>The columns in the database itself are snake_case, not only the model's mapping of them.</summary>
    [Fact]
    public async Task The_stored_columns_are_snake_case()
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

    [Fact]
    public async Task Applies_the_real_migration_history()
    {
        // EnsureCreated leaves no history, so this proves the template was built by migrations.
        Assert.NotEmpty(await Db.Database.GetAppliedMigrationsAsync(Ct));
    }

    [Fact]
    public async Task No_migration_is_pending()
    {
        Assert.Empty(await Db.Database.GetPendingMigrationsAsync(Ct));
    }

    /// <summary>The Npgsql branch's <c>uuid_generate_v4()</c> default fills a key the entity left empty.</summary>
    [Fact]
    public async Task A_guid_key_is_generated_by_the_store()
    {
        var organization = new OrganizationEntity { Name = "store-generated", IsTemplate = true };

        await Seed(organization);

        Assert.NotEqual(Guid.Empty, organization.Id);
    }

    [Fact]
    public async Task A_foreign_key_to_a_missing_msel_is_refused()
    {
        Db.Teams.Add(new TeamEntity { Name = "orphan", MselId = Guid.NewGuid() });

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => Db.SaveChangesAsync(Ct));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [Fact]
    public async Task A_test_sees_only_its_own_rows()
    {
        await Seed(TestData.Organization(), TestData.Organization());

        await using var context = NewContext();

        Assert.Equal(2, await context.Organizations.CountAsync(Ct));
    }
}
