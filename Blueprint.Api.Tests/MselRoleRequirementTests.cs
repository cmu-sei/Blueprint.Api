// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Tests.Infrastructure;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>MselEditorRequirement</c>, <c>MselApproverRequirement</c>, <c>MoveEditorRequirement</c> and
/// <c>EvaluatorRequirement</c> are the same twenty lines four times over, differing only in the
/// <see cref="MselRole"/> they look for. Testing them as one family says so, and means a change to the
/// shared shape cannot pass in three of the four.
/// </summary>
/// <remarks>
/// The dispatch is a switch rather than <c>TheoryData</c> holding delegates: xUnit's serializability
/// analyzer rejects a non-serializable theory member, and under this repo's
/// <c>TreatWarningsAsErrors</c> that is a build failure rather than a warning.
/// </remarks>
public class MselRoleRequirementTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    [Theory]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.MoveEditor)]
    [InlineData(MselRole.Evaluator)]
    public async Task IsMet_ForAUnitMemberHoldingTheRole_IsTrue(MselRole role)
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        Assert.True(await IsMet(role, actor.Id, msel.Id, Db));
    }

    /// <summary>
    /// Both halves of the conjunction are needed: the unit reaches the MSEL, and only then is the role row
    /// consulted. Membership alone is refused.
    /// </summary>
    [Theory]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.MoveEditor)]
    [InlineData(MselRole.Evaluator)]
    public async Task IsMet_ForAUnitMemberWithNoRole_IsFalse(MselRole role)
    {
        var actor = await Actor().SeedAsync();
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        await Db.AddUnitMembershipAsync(actor.Id, msel.Id, Ct);

        Assert.False(await IsMet(role, actor.Id, msel.Id, Db));
    }

    /// <summary>
    /// And the role alone is refused, because the role query is never reached unless the unit query already
    /// found the user. Granting a <c>UserMselRole</c> without a unit assignment is a no-op - the mistake an
    /// administrator makes when adding somebody to an MSEL by hand.
    /// </summary>
    [Theory]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.MoveEditor)]
    [InlineData(MselRole.Evaluator)]
    public async Task IsMet_ForTheRoleWithoutUnitMembership_IsFalse(MselRole role)
    {
        var actor = await Actor().SeedAsync();
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        await Db.AddMselRoleAsync(actor.Id, msel.Id, role, Ct);

        Assert.False(await IsMet(role, actor.Id, msel.Id, Db));
    }

    /// <summary>
    /// The unit has to be one of <em>this</em> MSEL's units. Membership of some other unit, plus the role,
    /// reaches nothing.
    /// </summary>
    [Theory]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.MoveEditor)]
    [InlineData(MselRole.Evaluator)]
    public async Task IsMet_ForTheRoleAndMembershipOfAnUnrelatedUnit_IsFalse(MselRole role)
    {
        var actor = await Actor().SeedAsync();
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        await Db.AddUnitAsync(actor.Id, Ct);
        await Db.AddMselRoleAsync(actor.Id, msel.Id, role, Ct);

        Assert.False(await IsMet(role, actor.Id, msel.Id, Db));
    }

    /// <summary>
    /// None of the four treats the MSEL's author as special - unlike <c>MselOwnerRequirement</c>,
    /// <c>MselUserRequirement</c> and <c>MselViewRequirement</c>, which all check <c>CreatedBy</c> first. So
    /// the person who created an MSEL cannot edit its moves until somebody assigns them a unit and a role.
    /// Callers make up for it with an <c>||</c> against a <c>SystemPermission</c>.
    /// </summary>
    [Theory]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.MoveEditor)]
    [InlineData(MselRole.Evaluator)]
    public async Task IsMet_ForTheMselsCreator_IsFalse(MselRole role)
    {
        var actor = await Actor().SeedAsync();
        var msel = BlueprintAppFactory.Msel(createdBy: actor.Id);
        await Seed(msel);

        Assert.False(await IsMet(role, actor.Id, msel.Id, Db));
    }

    /// <summary>
    /// Team membership is not a path either - only <c>MselUnits</c> is queried, never <c>TeamUsers</c>.
    /// </summary>
    [Theory]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.MoveEditor)]
    [InlineData(MselRole.Evaluator)]
    public async Task IsMet_ForAMemberOfATeamOnTheMsel_IsFalse(MselRole role)
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().OnTeam(team).SeedAsync();

        Assert.False(await IsMet(role, actor.Id, msel.Id, Db));
    }

    /// <summary>
    /// A missing MSEL is a plain false: unlike <c>MselOwnerRequirement</c> and <c>MselUserRequirement</c>,
    /// these four never load the MSEL row, so there is nothing to dereference.
    /// </summary>
    [Theory]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.MoveEditor)]
    [InlineData(MselRole.Evaluator)]
    public async Task IsMet_ForAMselThatDoesNotExist_IsFalse(MselRole role)
    {
        var actor = await Actor().SeedAsync();

        Assert.False(await IsMet(role, actor.Id, Guid.NewGuid(), Db));
    }

    /// <summary>
    /// Each looks for its own role and no other, so a fully-seeded editor is refused by the other three.
    /// This is what makes the four separate helpers worth having rather than one taking a role argument.
    /// </summary>
    [Fact]
    public async Task IsMet_TheFourRolesDoNotSubstituteForEachOther()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        Assert.True(await MselEditorRequirement.IsMet(actor.Id, msel.Id, Db));
        Assert.False(await MselApproverRequirement.IsMet(actor.Id, msel.Id, Db));
        Assert.False(await MoveEditorRequirement.IsMet(actor.Id, msel.Id, Db));
        Assert.False(await EvaluatorRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    /// <summary>
    /// Roles accumulate rather than replace, so a second <c>UserMselRole</c> row satisfies a second helper
    /// without disturbing the first.
    /// </summary>
    [Fact]
    public async Task IsMet_TwoRolesOnOneMsel_SatisfyBothHelpers()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();
        await Db.AddMselRoleAsync(actor.Id, msel.Id, MselRole.Approver, Ct);

        Assert.True(await MselEditorRequirement.IsMet(actor.Id, msel.Id, Db));
        Assert.True(await MselApproverRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    /// <summary>
    /// Three of the four take a nullable id and answer false for null. <c>MoveEditorRequirement</c> is the
    /// odd one out - its parameter is a plain <c>Guid</c>, so the same call does not compile. An
    /// inconsistency in the family rather than a defect; it is pinned here so a future unification is a
    /// deliberate change.
    /// </summary>
    [Fact]
    public async Task IsMet_ForANullMselId_IsFalse()
    {
        var actor = await Actor().SeedAsync();

        Assert.False(await MselEditorRequirement.IsMet(actor.Id, null, Db));
        Assert.False(await MselApproverRequirement.IsMet(actor.Id, null, Db));
        Assert.False(await EvaluatorRequirement.IsMet(actor.Id, null, Db));
    }

    /// <summary>
    /// System permissions are checked elsewhere and never here, so an actor holding all 28 still satisfies
    /// none of the four.
    /// </summary>
    [Fact]
    public async Task IsMet_ForAnActorHoldingEverySystemPermission_IsStillFalse()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        Assert.False(await MselEditorRequirement.IsMet(actor.Id, msel.Id, Db));
        Assert.False(await MselApproverRequirement.IsMet(actor.Id, msel.Id, Db));
        Assert.False(await MoveEditorRequirement.IsMet(actor.Id, msel.Id, Db));
        Assert.False(await EvaluatorRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    private static Task<bool> IsMet(MselRole role, Guid userId, Guid mselId, BlueprintContext db) =>
        role switch
        {
            MselRole.Editor => MselEditorRequirement.IsMet(userId, mselId, db),
            MselRole.Approver => MselApproverRequirement.IsMet(userId, mselId, db),
            MselRole.MoveEditor => MoveEditorRequirement.IsMet(userId, mselId, db),
            MselRole.Evaluator => EvaluatorRequirement.IsMet(userId, mselId, db),
            _ => throw new ArgumentOutOfRangeException(
                nameof(role), role, "No requirement helper looks for this role on its own.")
        };
}
