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
/// <see cref="MselViewRequirement"/> without its team path: the creator, CreateMsels for a template, or a unit
/// member holding a contributor role. Being on one of the MSEL's teams makes a user a participant, not a contributor.
/// </summary>
public class MselContributorRequirementTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    /// <summary>Starts describing an actor to seed over this test's database.</summary>
    private TestActorBuilder Actor() => new(Db, Ct);

    /// <summary>A unit member holding any role from the Contributors tab is a contributor.</summary>
    [Theory]
    [InlineData(MselRole.Owner)]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.MoveEditor)]
    [InlineData(MselRole.Evaluator)]
    [InlineData(MselRole.Viewer)]
    public async Task IsMet_ForAUnitMemberHoldingAContributorRole_IsTrue(MselRole role)
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        Assert.True(await MselContributorRequirement.IsMet(actor.Id, msel.Id, false, Db));
    }

    [Fact]
    public async Task IsMet_ForTheMselsCreator_IsTrue()
    {
        var actor = await Actor().SeedAsync();
        var msel = TestData.Msel(createdBy: actor.Id);
        await Seed(msel);

        Assert.True(await MselContributorRequirement.IsMet(actor.Id, msel.Id, false, Db));
    }

    /// <summary>CreateMsels opens a template, as it does for <see cref="MselViewRequirement"/>, and nothing else.</summary>
    [Fact]
    public async Task IsMet_WithCreateMsels_IsTrueForATemplateOnly()
    {
        var actor = await Actor().SeedAsync();
        var template = TestData.Msel();
        template.IsTemplate = true;
        var msel = TestData.Msel();
        await Seed(template, msel);

        Assert.True(await MselContributorRequirement.IsMet(actor.Id, template.Id, true, Db));
        Assert.False(await MselContributorRequirement.IsMet(actor.Id, msel.Id, true, Db));
    }

    /// <summary>
    /// A participant on one of the MSEL's teams is not a contributor, though <see cref="MselViewRequirement"/> lets them view.
    /// </summary>
    [Fact]
    public async Task IsMet_ForATeamMemberWithNoRole_IsFalse()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var team = TestData.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().OnTeam(team).SeedAsync();

        Assert.True(await MselViewRequirement.IsMet(actor.Id, msel.Id, Db));
        Assert.False(await MselContributorRequirement.IsMet(actor.Id, msel.Id, false, Db));
    }

    /// <summary>Unit membership without a role is not enough, as for <see cref="MselViewRequirement"/>.</summary>
    [Fact]
    public async Task IsMet_ForAUnitMemberWithNoRole_IsFalse()
    {
        var actor = await Actor().SeedAsync();
        var msel = TestData.Msel();
        await Seed(msel);
        await Db.AddUnitMembershipAsync(actor.Id, msel.Id, Ct);

        Assert.False(await MselContributorRequirement.IsMet(actor.Id, msel.Id, false, Db));
    }

    [Fact]
    public async Task IsMet_ForAMselThatIsNotThere_IsFalse()
    {
        var actor = await Actor().SeedAsync();

        Assert.False(await MselContributorRequirement.IsMet(actor.Id, Guid.NewGuid(), false, Db));
    }
}
