// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Tests.Infrastructure;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// The narrowest of the MSEL helpers: the author, or someone holding <see cref="MselRole.Owner"/> through a
/// unit. It gates the destructive operations - delete, archive, the MSEL-wide pushes to Player and CITE.
/// </summary>
public class MselOwnerRequirementTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    [Fact]
    public async Task IsMet_ForTheMselsCreator_IsTrue()
    {
        var actor = await Actor().SeedAsync();
        var msel = BlueprintAppFactory.Msel(createdBy: actor.Id);
        await Seed(msel);

        Assert.True(await MselOwnerRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    [Fact]
    public async Task IsMet_ForAUnitMemberHoldingTheOwnerRole_IsTrue()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        Assert.True(await MselOwnerRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    /// <summary>
    /// Only <see cref="MselRole.Owner"/> counts. Every other role - including the editing ones - is refused,
    /// which is the whole point of the helper existing alongside <c>MselEditorRequirement</c>.
    /// </summary>
    [Theory]
    [InlineData(MselRole.Viewer)]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.MoveEditor)]
    [InlineData(MselRole.Evaluator)]
    public async Task IsMet_ForAUnitMemberHoldingAnyOtherRole_IsFalse(MselRole role)
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        Assert.False(await MselOwnerRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    [Fact]
    public async Task IsMet_ForAUnitMemberWithNoRole_IsFalse()
    {
        var actor = await Actor().SeedAsync();
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        await Db.AddUnitMembershipAsync(actor.Id, msel.Id, Ct);

        Assert.False(await MselOwnerRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    [Fact]
    public async Task IsMet_ForTheOwnerRoleWithoutUnitMembership_IsFalse()
    {
        var actor = await Actor().SeedAsync();
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        await Db.AddMselRoleAsync(actor.Id, msel.Id, MselRole.Owner, Ct);

        Assert.False(await MselOwnerRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    /// <summary>
    /// Team membership is not a path to ownership - unlike <c>MselViewRequirement</c> and
    /// <c>MselUserRequirement</c>, this helper never looks at <c>TeamUsers</c>. A participant cannot delete
    /// the exercise they are playing.
    /// </summary>
    [Fact]
    public async Task IsMet_ForAMemberOfATeamOnTheMsel_IsFalse()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().OnTeam(team).SeedAsync();

        Assert.False(await MselOwnerRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    [Fact]
    public async Task IsMet_ForSomeoneWithNoRelationshipToTheMsel_IsFalse()
    {
        var actor = await Actor().SeedAsync();
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        Assert.False(await MselOwnerRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    /// <remarks>
    /// Characterization. The creator check reads <c>.CreatedBy</c> off the result of
    /// <c>FirstOrDefaultAsync</c> without checking it for null, so an id that names no MSEL throws rather
    /// than answering false. Every caller is a service about to return 403, so the caller's 403 becomes a
    /// 500 - and the two are not equivalent to a client, which retries one and not the other.
    /// <c>MselViewRequirement</c> gets this right; see
    /// <see cref="MselViewRequirementTests.IsMet_ForAMselThatDoesNotExist_IsFalse"/>. Turns red when the
    /// null check arrives.
    /// </remarks>
    [Fact]
    public async Task IsMet_ForAMselThatDoesNotExist_Throws()
    {
        var actor = await Actor().SeedAsync();

        await Assert.ThrowsAsync<NullReferenceException>(
            () => MselOwnerRequirement.IsMet(actor.Id, Guid.NewGuid(), Db));
    }

    /// <remarks>
    /// Characterization, same cause. A null id matches no row, so the same unguarded dereference throws -
    /// and a null <c>mselId</c> is the ordinary shape of a request against an unscoped entity.
    /// </remarks>
    [Fact]
    public async Task IsMet_ForANullMselId_Throws()
    {
        var actor = await Actor().SeedAsync();

        await Assert.ThrowsAsync<NullReferenceException>(
            () => MselOwnerRequirement.IsMet(actor.Id, null, Db));
    }

    [Fact]
    public async Task IsMet_ForTheOwnerOfAnotherMsel_IsFalse()
    {
        var msel = BlueprintAppFactory.Msel();
        var other = BlueprintAppFactory.Msel();
        await Seed(msel, other);

        var actor = await Actor().OnMsel(other, MselRole.Owner).SeedAsync();

        Assert.False(await MselOwnerRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    /// <summary>
    /// Being an owner is not being an administrator: the system role attached to the user is never read
    /// here. Coarse <c>SystemPermission</c>s are checked separately by the controller, and the two are
    /// combined with an <c>||</c> at the call site rather than inside the helper.
    /// </summary>
    [Fact]
    public async Task IsMet_ForAnActorHoldingEverySystemPermission_IsStillFalse()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        Assert.False(await MselOwnerRequirement.IsMet(actor.Id, msel.Id, Db));
    }
}
