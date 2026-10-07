// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Tests.Support;
using Xunit;

namespace Blueprint.Api.Tests.Infrastructure.Authorization;

/// <summary>
/// "Is this person involved in this MSEL at all" - the loosest of the helpers, and the only one that takes
/// bare unit membership with no role attached.
/// </summary>
public class MselUserRequirementTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    /// <summary>Starts describing an actor to seed over this test's database.</summary>
    private TestActorBuilder Actor() => new(Db, Ct);

    [Fact]
    public async Task IsMet_ForTheMselsCreator_IsTrue()
    {
        var actor = await Actor().SeedAsync();
        var msel = TestData.Msel(createdBy: actor.Id);
        await Seed(msel);

        Assert.True(await MselUserRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    [Fact]
    public async Task IsMet_ForAMemberOfATeamOnTheMsel_IsTrue()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var team = TestData.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().OnTeam(team).SeedAsync();

        Assert.True(await MselUserRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    /// <summary>
    /// The asymmetry that gives this helper its reason to exist: membership of a unit assigned to the MSEL
    /// is enough on its own, where <c>MselViewRequirement</c> would additionally demand a role. Any service
    /// choosing between the two is choosing whether a role-less unit member gets in.
    /// </summary>
    [Fact]
    public async Task IsMet_ForAUnitMemberWithNoRole_IsTrue()
    {
        var actor = await Actor().SeedAsync();
        var msel = TestData.Msel();
        await Seed(msel);
        await Db.AddUnitMembershipAsync(actor.Id, msel.Id, Ct);

        Assert.True(await MselUserRequirement.IsMet(actor.Id, msel.Id, Db));
        Assert.False(await MselViewRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    /// <summary>
    /// Every role reaches it too, because the role is never consulted - only the unit is.
    /// </summary>
    [Theory]
    [InlineData(MselRole.Viewer)]
    [InlineData(MselRole.Evaluator)]
    [InlineData(MselRole.Owner)]
    public async Task IsMet_ForAUnitMemberHoldingAnyRole_IsTrue(MselRole role)
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        Assert.True(await MselUserRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    [Fact]
    public async Task IsMet_ForAMemberOfAnUnrelatedUnit_IsFalse()
    {
        var actor = await Actor().SeedAsync();
        var msel = TestData.Msel();
        await Seed(msel);
        await Db.AddUnitAsync(actor.Id, Ct);

        Assert.False(await MselUserRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    [Fact]
    public async Task IsMet_ForAMemberOfATeamOnAnotherMsel_IsFalse()
    {
        var msel = TestData.Msel();
        var other = TestData.Msel();
        await Seed(msel, other);
        var team = TestData.Team(other.Id);
        await Seed(team);

        var actor = await Actor().OnTeam(team).SeedAsync();

        Assert.False(await MselUserRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    [Fact]
    public async Task IsMet_ForSomeoneWithNoRelationshipToTheMsel_IsFalse()
    {
        var actor = await Actor().SeedAsync();
        var msel = TestData.Msel();
        await Seed(msel);

        Assert.False(await MselUserRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    /// <summary>Is met for a MSEL that does not exist throws.</summary>
    [Fact]
    public async Task IsMet_ForAMselThatDoesNotExist_Throws()
    {
        var actor = await Actor().SeedAsync();

        await Assert.ThrowsAsync<NullReferenceException>(
            () => MselUserRequirement.IsMet(actor.Id, Guid.NewGuid(), Db));
    }

    /// <summary>A null MSEL id throws.</summary>
    [Fact]
    public async Task IsMet_ForANullMselId_Throws()
    {
        var actor = await Actor().SeedAsync();

        await Assert.ThrowsAsync<NullReferenceException>(
            () => MselUserRequirement.IsMet(actor.Id, null, Db));
    }
}
