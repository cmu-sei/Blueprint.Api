// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Threading.Tasks;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Tests.Infrastructure;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// The only requirement helper that is not about MSELs. Catalogs are the shared library of injects, so this
/// is what decides whether one organisation's content is visible to another's - and it is the one helper
/// with a "public" escape hatch.
/// </summary>
public class CatalogViewRequirementTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    [Fact]
    public async Task IsMet_ForTheCatalogsCreator_IsTrue()
    {
        var actor = await Actor().SeedAsync();
        var catalog = await SeedCatalog(createdBy: actor.Id);

        Assert.True(await CatalogViewRequirement.IsMet(actor.Id, catalog.Id, Db));
    }

    /// <summary>
    /// A public catalog is readable by anyone, with no unit and no authorship. The check runs before both
    /// of the others, so it cannot be narrowed by anything else on the row.
    /// </summary>
    [Fact]
    public async Task IsMet_ForAPublicCatalog_IsTrueForAnyone()
    {
        var actor = await Actor().SeedAsync();
        var catalog = await SeedCatalog(isPublic: true);

        Assert.True(await CatalogViewRequirement.IsMet(actor.Id, catalog.Id, Db));
    }

    /// <summary>
    /// Even for a caller with no <c>UserEntity</c> row at all: nothing about the user is read on this path.
    /// </summary>
    [Fact]
    public async Task IsMet_ForAPublicCatalog_IsTrueForAUserThatDoesNotExist()
    {
        var catalog = await SeedCatalog(isPublic: true);

        Assert.True(await CatalogViewRequirement.IsMet(Guid.NewGuid(), catalog.Id, Db));
    }

    /// <summary>
    /// The unit path: membership of any unit the catalog is assigned to.
    /// </summary>
    [Fact]
    public async Task IsMet_ForAMemberOfAUnitOnTheCatalog_IsTrue()
    {
        var actor = await Actor().SeedAsync();
        var catalog = await SeedCatalog();

        var unit = await Db.AddUnitAsync(actor.Id, Ct);
        Db.CatalogUnits.Add(new CatalogUnitEntity(unit.Id, catalog.Id));
        await Db.SaveChangesAsync(Ct);

        Assert.True(await CatalogViewRequirement.IsMet(actor.Id, catalog.Id, Db));
    }

    /// <summary>
    /// No role is involved - unlike the MSEL helpers, a <c>CatalogUnit</c> row plus a <c>UnitUser</c> row is
    /// the whole conjunction.
    /// </summary>
    [Fact]
    public async Task IsMet_ForAMemberOfAnUnrelatedUnit_IsFalse()
    {
        var actor = await Actor().SeedAsync();
        var catalog = await SeedCatalog();
        await Db.AddUnitAsync(actor.Id, Ct);

        Assert.False(await CatalogViewRequirement.IsMet(actor.Id, catalog.Id, Db));
    }

    /// <summary>
    /// And a unit assigned to the catalog that the actor is not in reaches nothing.
    /// </summary>
    [Fact]
    public async Task IsMet_ForAUnitOnTheCatalogWithoutTheActorInIt_IsFalse()
    {
        var actor = await Actor().SeedAsync();
        var other = await Actor().SeedAsync();
        var catalog = await SeedCatalog();

        var unit = await Db.AddUnitAsync(other.Id, Ct);
        Db.CatalogUnits.Add(new CatalogUnitEntity(unit.Id, catalog.Id));
        await Db.SaveChangesAsync(Ct);

        Assert.False(await CatalogViewRequirement.IsMet(actor.Id, catalog.Id, Db));
    }

    [Fact]
    public async Task IsMet_ForSomeoneWithNoRelationshipToTheCatalog_IsFalse()
    {
        var actor = await Actor().SeedAsync();
        var catalog = await SeedCatalog();

        Assert.False(await CatalogViewRequirement.IsMet(actor.Id, catalog.Id, Db));
    }

    /// <summary>
    /// The null check this helper has and <c>MselOwnerRequirement</c> does not: a missing catalog is false,
    /// not a throw. Same query shape, opposite outcome - which is why the MSEL one is a defect rather than
    /// a house style.
    /// </summary>
    [Fact]
    public async Task IsMet_ForACatalogThatDoesNotExist_IsFalse()
    {
        var actor = await Actor().SeedAsync();

        Assert.False(await CatalogViewRequirement.IsMet(actor.Id, Guid.NewGuid(), Db));
    }

    [Fact]
    public async Task IsMet_ForANullCatalogId_IsFalse()
    {
        var actor = await Actor().SeedAsync();

        Assert.False(await CatalogViewRequirement.IsMet(actor.Id, null, Db));
    }

    /// <summary>
    /// A unit membership reaching one catalog does not reach another sharing the unit's other assignments -
    /// the <c>CatalogUnits</c> filter is on the catalog id.
    /// </summary>
    [Fact]
    public async Task IsMet_DoesNotLeakBetweenCatalogs()
    {
        var actor = await Actor().SeedAsync();
        var injectType = BlueprintAppFactory.InjectType();
        await Seed(injectType);
        var reachable = BlueprintAppFactory.Catalog(injectType.Id);
        var unreachable = BlueprintAppFactory.Catalog(injectType.Id);
        await Seed(reachable, unreachable);

        var unit = await Db.AddUnitAsync(actor.Id, Ct);
        Db.CatalogUnits.Add(new CatalogUnitEntity(unit.Id, reachable.Id));
        await Db.SaveChangesAsync(Ct);

        Assert.True(await CatalogViewRequirement.IsMet(actor.Id, reachable.Id, Db));
        Assert.False(await CatalogViewRequirement.IsMet(actor.Id, unreachable.Id, Db));
    }

    /// <summary>
    /// Nesting is not inherited. A child catalog's visibility says nothing about its parent's and vice
    /// versa: <c>ParentId</c> is never read here, even though the entity carries it and the UI presents
    /// catalogs as a tree.
    /// </summary>
    [Fact]
    public async Task IsMet_DoesNotInheritFromAParentCatalog()
    {
        var actor = await Actor().SeedAsync();
        var injectType = BlueprintAppFactory.InjectType();
        await Seed(injectType);

        var parent = BlueprintAppFactory.Catalog(injectType.Id, createdBy: actor.Id, isPublic: true);
        await Seed(parent);
        var child = BlueprintAppFactory.Catalog(injectType.Id);
        child.ParentId = parent.Id;
        await Seed(child);

        Assert.True(await CatalogViewRequirement.IsMet(actor.Id, parent.Id, Db));
        Assert.False(await CatalogViewRequirement.IsMet(actor.Id, child.Id, Db));
    }

    /// <summary>
    /// System permissions are not consulted, as with every other requirement helper.
    /// </summary>
    [Fact]
    public async Task IsMet_ForAnActorHoldingEverySystemPermission_IsStillFalse()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();
        var catalog = await SeedCatalog();

        Assert.False(await CatalogViewRequirement.IsMet(actor.Id, catalog.Id, Db));
    }

    private async Task<CatalogEntity> SeedCatalog(Guid? createdBy = null, bool isPublic = false)
    {
        var injectType = BlueprintAppFactory.InjectType();
        await Seed(injectType);

        var catalog = BlueprintAppFactory.Catalog(injectType.Id, createdBy, isPublic);
        await Seed(catalog);

        return catalog;
    }
}
