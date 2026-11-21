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
/// Who runs the deployed MSEL (<see cref="MselFacilitatorRequirement"/>: the creator, or a unit member who is
/// its Owner or a Facilitator) and who watches it (<see cref="MselFacilitatorViewRequirement"/>: those, plus
/// its Editors and Approvers).
/// </summary>
public class MselFacilitatorRequirementTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    /// <summary>Starts describing an actor to seed over this test's database.</summary>
    private TestActorBuilder Actor() => new(Db, Ct);

    [Theory]
    [InlineData(MselRole.Owner)]
    [InlineData(MselRole.Facilitator)]
    public async Task IsMet_ForAUnitMemberWhoIsAnOwnerOrAFacilitator_IsTrue(MselRole role)
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        Assert.True(await MselFacilitatorRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    [Fact]
    public async Task IsMet_ForTheMselsCreator_IsTrue()
    {
        var actor = await Actor().SeedAsync();
        var msel = TestData.Msel(createdBy: actor.Id);
        await Seed(msel);

        Assert.True(await MselFacilitatorRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    /// <summary>The roles that watch the Facilitator View, or assess, do not run the exercise.</summary>
    [Theory]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.MoveEditor)]
    [InlineData(MselRole.Evaluator)]
    [InlineData(MselRole.Viewer)]
    public async Task IsMet_ForAUnitMemberHoldingAnotherRole_IsFalse(MselRole role)
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        Assert.False(await MselFacilitatorRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    [Fact]
    public async Task IsMet_ForAFacilitatorOfAnotherMsel_IsFalse()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().OnNewMsel(MselRole.Facilitator).SeedAsync();

        Assert.False(await MselFacilitatorRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    /// <summary>The role counts only for a member of one of the MSEL's units, as every MSEL role does.</summary>
    [Fact]
    public async Task IsMet_ForAFacilitatorOutsideTheMselsUnits_IsFalse()
    {
        var actor = await Actor().SeedAsync();
        var msel = TestData.Msel();
        await Seed(msel);
        Db.UserMselRoles.Add(new Data.Models.UserMselRoleEntity { UserId = actor.Id, MselId = msel.Id, Role = MselRole.Facilitator });
        await Db.SaveChangesAsync(Ct);

        Assert.False(await MselFacilitatorRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    [Fact]
    public async Task IsMet_ForAMselThatIsNotThere_IsFalse()
    {
        var actor = await Actor().SeedAsync();

        Assert.False(await MselFacilitatorRequirement.IsMet(actor.Id, Guid.NewGuid(), Db));
    }

    [Theory]
    [InlineData(MselRole.Owner)]
    [InlineData(MselRole.Facilitator)]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    public async Task ViewIsMet_ForAUnitMemberWhoWatchesTheFacilitatorView_IsTrue(MselRole role)
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        Assert.True(await MselFacilitatorViewRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    [Fact]
    public async Task ViewIsMet_ForTheMselsCreator_IsTrue()
    {
        var actor = await Actor().SeedAsync();
        var msel = TestData.Msel(createdBy: actor.Id);
        await Seed(msel);

        Assert.True(await MselFacilitatorViewRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    /// <summary>An Evaluator keeps the Assessor View only; a Viewer and a Move Editor see no automation.</summary>
    [Theory]
    [InlineData(MselRole.Evaluator)]
    [InlineData(MselRole.Viewer)]
    [InlineData(MselRole.MoveEditor)]
    public async Task ViewIsMet_ForAUnitMemberHoldingAnotherRole_IsFalse(MselRole role)
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        Assert.False(await MselFacilitatorViewRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    [Fact]
    public async Task ViewIsMet_ForAnEditorOfAnotherMsel_IsFalse()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().OnNewMsel(MselRole.Editor).SeedAsync();

        Assert.False(await MselFacilitatorViewRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    /// <summary>A participant on one of the MSEL's teams does not see its automation.</summary>
    [Fact]
    public async Task ViewIsMet_ForATeamMemberWithNoRole_IsFalse()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var team = TestData.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().OnTeam(team).SeedAsync();

        Assert.False(await MselFacilitatorViewRequirement.IsMet(actor.Id, msel.Id, Db));
    }
}
