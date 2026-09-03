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
/// The widest of the requirement helpers and the one gating most of the read surface. Five ways to satisfy
/// it, and they are checked in order: the template shortcut, the creator, team membership, then unit
/// membership together with a role.
/// </summary>
public class MselViewRequirementTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    [Fact]
    public async Task IsMet_ForTheMselsCreator_IsTrue()
    {
        var actor = await Actor().SeedAsync();
        var msel = BlueprintAppFactory.Msel(createdBy: actor.Id);
        await Seed(msel);

        Assert.True(await MselViewRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    [Fact]
    public async Task IsMet_ForSomeoneWithNoRelationshipToTheMsel_IsFalse()
    {
        var actor = await Actor().SeedAsync();
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        Assert.False(await MselViewRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    /// <summary>
    /// A missing MSEL is a plain false, not a throw - which is what lets the service layer turn it into a
    /// 403 rather than a 500. <c>MselOwnerRequirement</c> and <c>MselUserRequirement</c> do not do this;
    /// see <see cref="MselOwnerRequirementTests.IsMet_ForAMselThatDoesNotExist_Throws"/>.
    /// </summary>
    [Fact]
    public async Task IsMet_ForAMselThatDoesNotExist_IsFalse()
    {
        var actor = await Actor().SeedAsync();

        Assert.False(await MselViewRequirement.IsMet(actor.Id, Guid.NewGuid(), Db));
    }

    [Fact]
    public async Task IsMet_ForANullMselId_IsFalse()
    {
        var actor = await Actor().SeedAsync();

        Assert.False(await MselViewRequirement.IsMet(actor.Id, null, Db));
    }

    /// <summary>
    /// The template shortcut: anyone who may create MSELs may read any template, because that is what they
    /// would be creating from. No membership of any kind is involved.
    /// </summary>
    [Fact]
    public async Task IsMet_ForATemplateWithTheCreateMselsPermission_IsTrue()
    {
        var actor = await Actor().SeedAsync();
        var msel = BlueprintAppFactory.Msel(isTemplate: true);
        await Seed(msel);

        Assert.True(await MselViewRequirement.IsMet(actor.Id, msel.Id, true, Db));
    }

    /// <summary>
    /// Both halves are needed. The permission alone does not open a non-template MSEL.
    /// </summary>
    [Fact]
    public async Task IsMet_ForANonTemplateWithTheCreateMselsPermission_IsFalse()
    {
        var actor = await Actor().SeedAsync();
        var msel = BlueprintAppFactory.Msel(isTemplate: false);
        await Seed(msel);

        Assert.False(await MselViewRequirement.IsMet(actor.Id, msel.Id, true, Db));
    }

    /// <summary>
    /// And a template is not public: without the permission it falls through to the ordinary checks.
    /// </summary>
    [Fact]
    public async Task IsMet_ForATemplateWithoutTheCreateMselsPermission_IsFalse()
    {
        var actor = await Actor().SeedAsync();
        var msel = BlueprintAppFactory.Msel(isTemplate: true);
        await Seed(msel);

        Assert.False(await MselViewRequirement.IsMet(actor.Id, msel.Id, false, Db));
    }

    /// <summary>
    /// The three-argument overload forwards <c>false</c>, so a caller that forgets to pass the permission
    /// gets the strict answer rather than the permissive one. Worth pinning: the failure mode of the other
    /// default would be silent over-permission.
    /// </summary>
    [Fact]
    public async Task IsMet_TheShortOverloadDoesNotGrantTheTemplateShortcut()
    {
        var actor = await Actor().SeedAsync();
        var msel = BlueprintAppFactory.Msel(isTemplate: true);
        await Seed(msel);

        Assert.False(await MselViewRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    /// <summary>
    /// Team membership alone grants view, with no <c>UserMselRole</c> at all. This is how a participant sees
    /// the exercise they are playing.
    /// </summary>
    [Fact]
    public async Task IsMet_ForAMemberOfATeamOnTheMsel_IsTrue()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().OnTeam(team).SeedAsync();

        Assert.True(await MselViewRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    /// <summary>
    /// A team on a different MSEL reaches nothing - the membership is joined through <c>Team.MselId</c>.
    /// </summary>
    [Fact]
    public async Task IsMet_ForAMemberOfATeamOnAnotherMsel_IsFalse()
    {
        var msel = BlueprintAppFactory.Msel();
        var other = BlueprintAppFactory.Msel();
        await Seed(msel, other);
        var team = BlueprintAppFactory.Team(other.Id);
        await Seed(team);

        var actor = await Actor().OnTeam(team).SeedAsync();

        Assert.False(await MselViewRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    /// <summary>
    /// The unit path, which is the one <see cref="TestActorBuilder.OnMsel"/> seeds: membership of a unit
    /// assigned to the MSEL, plus a role that is on the view list.
    /// </summary>
    [Theory]
    [InlineData(MselRole.Viewer)]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.MoveEditor)]
    [InlineData(MselRole.Owner)]
    public async Task IsMet_ForAUnitMemberHoldingAViewingRole_IsTrue(MselRole role)
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        Assert.True(await MselViewRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    /// <remarks>
    /// Characterization, and the surprise in this file. <see cref="MselRole.Evaluator"/> is the one role
    /// absent from the view list, so an evaluator assigned to an MSEL through a unit cannot view it - while
    /// <c>EvaluatorRequirement</c> says they are an evaluator of it. Every read gated on this helper is
    /// closed to them. Turns red when <c>Evaluator</c> joins the list.
    /// </remarks>
    [Fact]
    public async Task IsMet_ForAUnitMemberHoldingOnlyTheEvaluatorRole_IsFalse()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Evaluator).SeedAsync();

        Assert.False(await MselViewRequirement.IsMet(actor.Id, msel.Id, Db));
        Assert.True(await EvaluatorRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    /// <summary>
    /// Unit membership on its own is not enough - unlike <c>MselUserRequirement</c>, which takes it. The two
    /// helpers differ on exactly this, and services choose between them.
    /// </summary>
    [Fact]
    public async Task IsMet_ForAUnitMemberWithNoRole_IsFalse()
    {
        var actor = await Actor().SeedAsync();
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        await Db.AddUnitMembershipAsync(actor.Id, msel.Id, Ct);

        Assert.False(await MselViewRequirement.IsMet(actor.Id, msel.Id, Db));
        Assert.True(await MselUserRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    /// <summary>
    /// And a role on its own is not enough either: the role row is never consulted unless the unit path
    /// already found the user. A <c>UserMselRole</c> granted without adding the user to one of the MSEL's
    /// units does nothing at all.
    /// </summary>
    [Fact]
    public async Task IsMet_ForARoleWithoutUnitMembership_IsFalse()
    {
        var actor = await Actor().SeedAsync();
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        await Db.AddMselRoleAsync(actor.Id, msel.Id, MselRole.Owner, Ct);

        Assert.False(await MselViewRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    /// <summary>
    /// The unit has to be assigned to <em>this</em> MSEL. Membership of an unrelated unit reaches nothing,
    /// even with a role on the MSEL.
    /// </summary>
    [Fact]
    public async Task IsMet_ForARoleAndMembershipOfAnUnrelatedUnit_IsFalse()
    {
        var actor = await Actor().SeedAsync();
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        await Db.AddUnitAsync(actor.Id, Ct);
        await Db.AddMselRoleAsync(actor.Id, msel.Id, MselRole.Owner, Ct);

        Assert.False(await MselViewRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    /// <summary>
    /// A role on another MSEL does not carry across, even when the same unit is assigned to both - which is
    /// why <see cref="TestActorBuilder.OnMsel"/> mints a unit per call.
    /// </summary>
    [Fact]
    public async Task IsMet_ForARoleOnAnotherMsel_IsFalse()
    {
        var msel = BlueprintAppFactory.Msel();
        var other = BlueprintAppFactory.Msel();
        await Seed(msel, other);

        var actor = await Actor().OnMsel(other, MselRole.Owner).SeedAsync();
        await Db.AddUnitMembershipAsync(actor.Id, msel.Id, Ct);

        Assert.False(await MselViewRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    /// <summary>
    /// The creator check runs before the membership queries, so the MSEL's author can always read it -
    /// including a template they made, and including one they are not a member of.
    /// </summary>
    [Fact]
    public async Task IsMet_ForTheCreatorOfATemplate_IsTrueWithoutThePermission()
    {
        var actor = await Actor().SeedAsync();
        var msel = BlueprintAppFactory.Msel(createdBy: actor.Id, isTemplate: true);
        await Seed(msel);

        Assert.True(await MselViewRequirement.IsMet(actor.Id, msel.Id, false, Db));
    }

    /// <summary>
    /// One user's view of an MSEL says nothing about another's. This is the shape most service-level
    /// authorization tests take, so it is pinned once here at the source.
    /// </summary>
    [Fact]
    public async Task IsMet_IsDecidedPerUser()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var viewer = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();
        var outsider = await Actor().SeedAsync();

        Assert.True(await MselViewRequirement.IsMet(viewer.Id, msel.Id, Db));
        Assert.False(await MselViewRequirement.IsMet(outsider.Id, msel.Id, Db));
    }

    /// <summary>
    /// An unknown user is simply someone with no rows: no user record is needed to be refused.
    /// </summary>
    [Fact]
    public async Task IsMet_ForAUserThatDoesNotExist_IsFalse()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        Assert.False(await MselViewRequirement.IsMet(Guid.NewGuid(), msel.Id, Db));
    }
}
