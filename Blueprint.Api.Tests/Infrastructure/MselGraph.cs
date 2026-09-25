// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;

namespace Blueprint.Api.Tests.Infrastructure;

/// <summary>
/// Writes <em>half</em> of the unit-and-role graph that <see cref="TestActorBuilder.OnMsel"/> writes whole.
/// </summary>
/// <remarks>
/// Every <c>*Requirement.IsMet</c> helper is a conjunction - membership of a unit the MSEL is assigned to
/// <em>and</em> a <c>UserMselRoleEntity</c> naming the role - and the requirement tests have to be able to
/// seed each half on its own to prove that neither suffices. <see cref="TestActorBuilder.OnMsel"/>
/// deliberately cannot do that: it always writes all four rows, because that is what every other test
/// wants and a partially-seeded actor there would be a bug rather than a case.
/// </remarks>
internal static class MselGraph
{
    /// <summary>
    /// Puts <paramref name="userId"/> in a new unit assigned to <paramref name="mselId"/>, and gives them
    /// no role. Half the conjunction: enough to reach the role check, never enough to pass it.
    /// </summary>
    public static async Task<UnitEntity> AddUnitMembershipAsync(
        this BlueprintContext db, Guid userId, Guid mselId, CancellationToken ct)
    {
        var unit = await db.AddUnitAsync(userId, ct);

        db.MselUnits.Add(new MselUnitEntity(unit.Id, mselId));
        await db.SaveChangesAsync(ct);

        return unit;
    }

    /// <summary>
    /// A unit containing <paramref name="userId"/> and assigned to nothing. Used to prove that membership
    /// of some other unit does not reach an MSEL or a catalog.
    /// </summary>
    public static async Task<UnitEntity> AddUnitAsync(
        this BlueprintContext db, Guid userId, CancellationToken ct)
    {
        var unit = new UnitEntity
        {
            Id = Guid.NewGuid(),
            Name = $"unit-{Guid.NewGuid()}",
            ShortName = "unit",
            CreatedBy = userId
        };

        db.Units.Add(unit);
        db.UnitUsers.Add(new UnitUserEntity(userId, unit.Id));
        await db.SaveChangesAsync(ct);

        return unit;
    }

    /// <summary>
    /// Gives <paramref name="userId"/> a role on <paramref name="mselId"/> and nothing else - no unit, so
    /// no path from the MSEL to the user. The other half of the conjunction.
    /// </summary>
    public static async Task AddMselRoleAsync(
        this BlueprintContext db, Guid userId, Guid mselId, MselRole role, CancellationToken ct)
    {
        db.UserMselRoles.Add(new UserMselRoleEntity(userId, mselId, role) { CreatedBy = userId });
        await db.SaveChangesAsync(ct);
    }
}
