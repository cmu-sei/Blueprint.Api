// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Blueprint.Api.Tests.Infrastructure;

/// <summary>
/// A seeded user, and the ids of the rows seeded with them.
/// </summary>
/// <remarks>
/// An HTTP test acts as an actor rather than as a hand-built principal:
/// <c>ApiTestBase.Client(actor)</c> puts the id on the request, and the real
/// <c>AuthorizationClaimsTransformer</c> derives the permission claims from these rows. What the actor
/// may do is therefore a property of the database, as it is in production - which matters more in
/// blueprint than in the other Crucible APIs, because authorization here is decided twice. A controller
/// resolves coarse <see cref="SystemPermission"/>s through <c>IBlueprintAuthorizationService</c>, and
/// then the service re-checks with the static <c>Msel*Requirement.IsMet</c> helpers, which read the
/// MSEL / unit / team graph directly and cannot be substituted.
/// </remarks>
public sealed class TestActor
{
    public required Guid Id { get; init; }

    /// <summary>
    /// Sent as the <c>name</c> claim, which <c>UserClaimsService.ValidateUser</c> writes back to the
    /// user row.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// One per <see cref="TestActorBuilder.OnMsel"/> call, in the order they were declared.
    /// </summary>
    public required IReadOnlyList<TestActorMselRole> MselRoles { get; init; }

    /// <summary>The teams this actor is a member of, in the order they were declared.</summary>
    public required IReadOnlyList<Guid> TeamIds { get; init; }

    /// <summary>The first declared MSEL role - the common case of an actor on one MSEL.</summary>
    public TestActorMselRole MselRole => MselRoles.Count > 0
        ? MselRoles[0]
        : throw new InvalidOperationException($"Actor {Id} has no role on any MSEL.");

    /// <summary>The role this actor holds on <paramref name="mselId"/>.</summary>
    public TestActorMselRole On(Guid mselId) =>
        MselRoles.SingleOrDefault(x => x.MselId == mselId)
        ?? throw new InvalidOperationException($"Actor {Id} has no role on MSEL {mselId}.");
}

/// <summary>
/// One seeded MSEL role, and the unit it was granted through.
/// </summary>
/// <remarks>
/// The unit id is here because the requirement helpers reach the actor through it: a
/// <c>UserMselRoleEntity</c> on its own satisfies none of them. <c>MselViewRequirement</c>,
/// <c>MselOwnerRequirement</c> and <c>MselEditorRequirement</c> all check that the user is in a unit the
/// MSEL is assigned to <em>and</em> holds the role, so a test that wants to add a second MSEL to the
/// same unit needs the id.
/// </remarks>
public sealed record TestActorMselRole(Guid MselId, Guid UnitId, MselRole Role);

/// <summary>
/// Seeds a user, the system role that grants their <see cref="SystemPermission"/>s, and the unit, team
/// and MSEL-role rows the requirement helpers read.
/// </summary>
/// <remarks>
/// <para>
/// The mapping from rows to claims is <c>UserClaimsService.GetPermissionClaims</c>: the permission
/// claims come from the user's <see cref="SystemRoleEntity"/>, and nothing else contributes to them.
/// Everything MSEL-scoped is not a claim at all - it is read from the database by the requirement
/// helper, at the moment the service asks.
/// </para>
/// <para>
/// A user with no system role at all is the default, and is what a user who has just logged in for the
/// first time looks like: <c>UserClaimsService.ValidateUser</c> auto-provisions the row with no role.
/// Such an actor authenticates, fails the permission check on every endpoint, and is how a test proves
/// a 403 rather than a 401.
/// </para>
/// </remarks>
public sealed class TestActorBuilder(BlueprintContext db, CancellationToken ct)
{
    private readonly List<PendingMselRole> _mselRoles = [];
    private readonly List<TeamEntity> _teams = [];
    private Guid _id = Guid.NewGuid();
    private string _name = "Test Actor";
    private Guid? _roleId;
    private SystemPermission[] _systemPermissions;

    /// <summary>
    /// Fixes the actor's id, for a test that needs to know it before seeding - a request whose route or
    /// body names the user, or an assertion on a stored <c>CreatedBy</c>.
    /// </summary>
    public TestActorBuilder WithId(Guid id)
    {
        _id = id;
        return this;
    }

    public TestActorBuilder WithName(string name)
    {
        _name = name;
        return this;
    }

    /// <summary>
    /// Gives the actor an existing system role, such as
    /// <see cref="SystemRoleDefaults.ContentDeveloperRoleId"/>.
    /// </summary>
    public TestActorBuilder WithRole(Guid roleId)
    {
        if (_systemPermissions is not null)
        {
            throw new InvalidOperationException(
                "WithRole and WithSystemPermissions both decide the actor's system role. Pass the " +
                "permissions to a role of your own and use WithRole, or drop one of the calls.");
        }

        _roleId = roleId;
        return this;
    }

    /// <summary>
    /// Every system permission, by way of the seeded <c>Administrator</c> role.
    /// </summary>
    /// <remarks>
    /// That role has <c>AllPermissions</c>, which <c>UserClaimsService.GetPermissionClaims</c> expands to
    /// every value of <see cref="SystemPermission"/> - all 28 of them - so this is a superset of naming
    /// them, and it costs no rows at all: <c>SystemRoleConfiguration.HasData</c> seeds the role into the
    /// migrated template.
    /// </remarks>
    public TestActorBuilder WithAllSystemPermissions() => WithRole(SystemRoleDefaults.AdministratorRoleId);

    /// <summary>
    /// Exactly these system permissions, by way of a role minted for this actor.
    /// </summary>
    /// <remarks>
    /// A role of its own rather than one of the three seeded ones, because role names are uniquely
    /// indexed and the seeded rows already hold the obvious names. Where a seeded role says what a test
    /// means - <c>SystemRoleDefaults.ContentDeveloperRoleId</c>, <c>ObserverRoleId</c> - pass its id to
    /// <see cref="WithRole"/> instead.
    /// </remarks>
    public TestActorBuilder WithSystemPermissions(params SystemPermission[] permissions)
    {
        if (_roleId is not null)
        {
            throw new InvalidOperationException(
                "WithSystemPermissions and WithRole both decide the actor's system role. Drop one.");
        }

        _systemPermissions = permissions;
        return this;
    }

    /// <summary>
    /// Gives the actor <paramref name="role"/> on <paramref name="msel"/>, by way of a unit assigned to
    /// it. Pass <paramref name="unit"/> to reuse a unit the test already has; otherwise one is minted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four rows, and all four are needed: a <see cref="UnitEntity"/>, a <see cref="UnitUserEntity"/>
    /// putting the actor in it, a <see cref="MselUnitEntity"/> assigning the unit to the MSEL, and a
    /// <see cref="UserMselRoleEntity"/> naming the role. Every requirement helper reads the unit path and
    /// the role, so three of the four would satisfy none of them. A reused <paramref name="unit"/> may
    /// already carry the membership or the assignment, in which case that row is left alone - all three
    /// join tables are uniquely indexed.
    /// </para>
    /// <para>
    /// Note what this deliberately does <em>not</em> do: it does not make the actor the MSEL's creator.
    /// <c>MselViewRequirement</c> and <c>MselOwnerRequirement</c> both short-circuit on
    /// <c>msel.CreatedBy == userId</c>, so an actor who created the MSEL would satisfy them whatever role
    /// this granted, and a test meaning to prove the role is what did it would prove nothing. Seed the
    /// MSEL with a <c>CreatedBy</c> of someone else - <c>BlueprintAppFactory.Msel</c> does.
    /// </para>
    /// </remarks>
    public TestActorBuilder OnMsel(MselEntity msel, MselRole role, UnitEntity unit = null)
    {
        ArgumentNullException.ThrowIfNull(msel);

        if (msel.Id == Guid.Empty)
        {
            throw new InvalidOperationException(
                "The MSEL has no id, so nothing can be keyed on it. Seed it - or give it an explicit " +
                "id - before declaring a role on it.");
        }

        _mselRoles.Add(new PendingMselRole(msel.Id, role, unit));
        return this;
    }

    /// <summary>
    /// Puts the actor on <paramref name="team"/>.
    /// </summary>
    /// <remarks>
    /// Team membership is a view grant and nothing more: <c>MselViewRequirement</c> takes any member of
    /// any team on the MSEL as able to view it, and no other requirement looks at teams. An actor who
    /// needs to edit or own needs <see cref="OnMsel"/>.
    /// </remarks>
    public TestActorBuilder OnTeam(TeamEntity team)
    {
        ArgumentNullException.ThrowIfNull(team);

        if (team.Id == Guid.Empty)
        {
            throw new InvalidOperationException(
                "The team has no id, so a membership cannot be keyed on it. Seed it - or give it an " +
                "explicit id - before declaring membership.");
        }

        _teams.Add(team);
        return this;
    }

    /// <summary>
    /// Writes the actor and everything above to the database.
    /// </summary>
    /// <remarks>
    /// The MSELs and teams passed to <see cref="OnMsel"/> and <see cref="OnTeam"/> must already be saved
    /// - the rows written here are foreign keys onto them.
    /// </remarks>
    public async Task<TestActor> SeedAsync()
    {
        var user = new UserEntity
        {
            Id = _id,
            Name = _name,
            CreatedBy = _id,
            RoleId = ResolveRoleId()
        };

        db.Users.Add(user);

        List<TestActorMselRole> mselRoles = [];

        // A passed-in unit may already carry some of these rows - from an earlier actor, or from an
        // earlier OnMsel call on this one. All three join tables are uniquely indexed, so writing a
        // duplicate surfaces as a Postgres constraint violation raised from inside this method, which
        // says nothing about the test that caused it. The sets cover rows this call is about to write and
        // the queries cover rows already saved.
        HashSet<Guid> memberships = [];
        HashSet<(Guid UnitId, Guid MselId)> assignments = [];

        foreach (var pending in _mselRoles)
        {
            // A unit per call unless one was passed. Two roles on two MSELs are then independent, which
            // is what a test declaring them separately means; sharing a unit would silently make an
            // actor's membership of one MSEL's unit reach the other.
            var unit = pending.Unit ?? NewUnit();
            var mselId = pending.MselId;

            if (db.Entry(unit).State == EntityState.Detached)
            {
                db.Units.Add(unit);
            }

            if (memberships.Add(unit.Id) &&
                !await db.UnitUsers.AnyAsync(x => x.UserId == _id && x.UnitId == unit.Id, ct))
            {
                db.UnitUsers.Add(new UnitUserEntity(_id, unit.Id));
            }

            if (assignments.Add((unit.Id, mselId)) &&
                !await db.MselUnits.AnyAsync(x => x.UnitId == unit.Id && x.MselId == mselId, ct))
            {
                db.MselUnits.Add(new MselUnitEntity(unit.Id, mselId));
            }

            db.UserMselRoles.Add(new UserMselRoleEntity(_id, pending.MselId, pending.Role)
            {
                CreatedBy = _id
            });

            mselRoles.Add(new TestActorMselRole(pending.MselId, unit.Id, pending.Role));
        }

        foreach (var team in _teams)
        {
            db.TeamUsers.Add(new TeamUserEntity(_id, team.Id));
        }

        await db.SaveChangesAsync(ct);

        return new TestActor
        {
            Id = _id,
            Name = _name,
            MselRoles = mselRoles,
            TeamIds = [.. _teams.Select(x => x.Id)]
        };
    }

    /// <summary>A unit with an explicit id, so callers can key on it before the save.</summary>
    private UnitEntity NewUnit()
    {
        var id = Guid.NewGuid();

        return new UnitEntity
        {
            Id = id,
            Name = $"unit-{id}",
            ShortName = "unit",
            CreatedBy = _id
        };
    }

    private Guid? ResolveRoleId()
    {
        if (_systemPermissions is null)
        {
            return _roleId;
        }

        var id = Guid.NewGuid();

        db.SystemRoles.Add(new SystemRoleEntity
        {
            Id = id,
            // Uniquely indexed, and the three seeded rows hold the readable names.
            Name = $"role-{id}",
            Description = "Minted by TestActorBuilder.WithSystemPermissions",
            AllPermissions = false,
            Immutable = false,
            Permissions = [.. _systemPermissions]
        });

        return id;
    }

    private sealed record PendingMselRole(Guid MselId, MselRole Role, UnitEntity Unit);
}
