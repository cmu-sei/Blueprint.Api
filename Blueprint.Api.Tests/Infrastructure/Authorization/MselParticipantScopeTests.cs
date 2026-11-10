// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Tests.Support;
using Xunit;

namespace Blueprint.Api.Tests.Infrastructure.Authorization;

/// <summary>
/// Who sees only the Exercise View of a MSEL: anyone not a contributor (<see cref="MselContributorRequirement"/>),
/// not holding ViewMsels, and not reading a template. In practice that is a member of one of the MSEL's
/// teams holding no role, as the read checks keep everyone else out before this is asked.
/// </summary>
public class MselParticipantScopeTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    /// <summary>Starts describing an actor to seed over this test's database.</summary>
    private TestActorBuilder Actor() => new(Db, Ct);

    [Fact]
    public async Task IsLimited_ForAMemberOfOneOfTheMselsTeamsHoldingNoRole_IsTrue()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var team = TestData.Team(msel.Id);
        await Seed(team);
        var actor = await Actor().OnTeam(team).SeedAsync();

        Assert.True(await MselParticipantScope.IsLimitedAsync(actor.Id, msel.Id, false, Db));
    }

    /// <summary>A unit member is a participant too until they hold a role: the unit alone is not enough.</summary>
    [Fact]
    public async Task IsLimited_ForAUnitMemberHoldingNoRole_IsTrue()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var actor = await Actor().InUnitOf(msel).SeedAsync();

        Assert.True(await MselParticipantScope.IsLimitedAsync(actor.Id, msel.Id, false, Db));
    }

    /// <summary>A team member who also holds any role from the Contributors tab sees the whole MSEL.</summary>
    [Theory]
    [InlineData(MselRole.Owner)]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.MoveEditor)]
    [InlineData(MselRole.Evaluator)]
    [InlineData(MselRole.Viewer)]
    public async Task IsLimited_ForATeamMemberHoldingAContributorRole_IsFalse(MselRole role)
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var team = TestData.Team(msel.Id);
        await Seed(team);
        var actor = await Actor().OnTeam(team).OnMsel(msel, role).SeedAsync();

        Assert.False(await MselParticipantScope.IsLimitedAsync(actor.Id, msel.Id, false, Db));
    }

    [Fact]
    public async Task IsLimited_ForTheMselsCreator_IsFalse()
    {
        var actor = await Actor().SeedAsync();
        var msel = TestData.Msel(createdBy: actor.Id);
        await Seed(msel);

        Assert.False(await MselParticipantScope.IsLimitedAsync(actor.Id, msel.Id, false, Db));
    }

    /// <summary>ViewMsels opens every MSEL, so its holder is never limited, team member or not.</summary>
    [Fact]
    public async Task IsLimited_WithViewMsels_IsFalse()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var team = TestData.Team(msel.Id);
        await Seed(team);
        var actor = await Actor().OnTeam(team).SeedAsync();

        Assert.False(await MselParticipantScope.IsLimitedAsync(actor.Id, msel.Id, true, Db));
    }

    /// <summary>A template is not an exercise anyone plays, so whoever may read it reads all of it.</summary>
    [Fact]
    public async Task IsLimited_ForATemplate_IsFalse()
    {
        var template = TestData.Msel(isTemplate: true);
        await Seed(template);
        var actor = await Actor().SeedAsync();

        Assert.False(await MselParticipantScope.IsLimitedAsync(actor.Id, template.Id, false, Db));
    }

    [Fact]
    public async Task IsLimited_ForANullMselId_IsFalse()
    {
        var actor = await Actor().SeedAsync();

        Assert.False(await MselParticipantScope.IsLimitedAsync(actor.Id, null, false, Db));
    }
}
