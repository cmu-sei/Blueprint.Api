// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// Self-tests for <see cref="TestActorBuilder"/>. Every authorization test in the suite is only as true as
/// the rows this builder writes, so when it is wrong these fail instead of a hundred tests failing for a
/// reason that is not theirs.
/// </summary>
/// <remarks>
/// The claims assertions go through the real <see cref="UserClaimsService"/> rather than restating
/// <c>GetPermissionClaims</c>: what a test needs to trust is that
/// <see cref="TestActorBuilder.WithAllSystemPermissions"/> produces the permissions the application will
/// see, not that it wrote a particular <c>RoleId</c>.
/// </remarks>
public class TestActorTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    [Fact]
    public async Task SeedAsync_WritesTheUser()
    {
        var actor = await Actor().WithName("Ada").SeedAsync();

        var user = await NewContext().Users.SingleAsync(x => x.Id == actor.Id, Ct);

        Assert.Equal("Ada", user.Name);
        Assert.Equal(actor.Id, user.CreatedBy);
        Assert.Null(user.RoleId);
    }

    /// <summary>
    /// No system role is the default, and it is the state of a user who has just logged in for the first
    /// time. Such an actor authenticates and is refused everything, which is how a test tells a 403 from
    /// a 401.
    /// </summary>
    [Fact]
    public async Task SeedAsync_ByDefault_GrantsNoSystemPermissions()
    {
        var actor = await Actor().SeedAsync();

        Assert.Empty(await PermissionsOf(actor));
    }

    [Fact]
    public async Task WithId_FixesTheActorsId()
    {
        var id = Guid.NewGuid();

        var actor = await Actor().WithId(id).SeedAsync();

        Assert.Equal(id, actor.Id);
        Assert.True(await NewContext().Users.AnyAsync(x => x.Id == id, Ct));
    }

    /// <summary>
    /// The claim that makes the builder worth having: all 28 permissions for one seeded row and no
    /// enumeration, because the <c>Administrator</c> role in the migrated template carries
    /// <c>AllPermissions</c>.
    /// </summary>
    [Fact]
    public async Task WithAllSystemPermissions_GrantsEverySystemPermission()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        Assert.Equal(
            [.. Enum.GetValues<SystemPermission>().Select(x => x.ToString()).Order()],
            (await PermissionsOf(actor)).Order());
    }

    [Fact]
    public async Task WithAllSystemPermissions_UsesTheSeededAdministratorRole()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var user = await NewContext().Users.SingleAsync(x => x.Id == actor.Id, Ct);

        Assert.Equal(SystemRoleDefaults.AdministratorRoleId, user.RoleId);
    }

    [Fact]
    public async Task WithSystemPermissions_GrantsExactlyThose()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ViewMsels, SystemPermission.CreateMsels)
            .SeedAsync();

        Assert.Equal(
            [nameof(SystemPermission.CreateMsels), nameof(SystemPermission.ViewMsels)],
            (await PermissionsOf(actor)).Order());
    }

    /// <summary>
    /// The role is minted per actor, so two actors asking for different permissions do not collide on the
    /// uniquely-indexed role name.
    /// </summary>
    [Fact]
    public async Task WithSystemPermissions_MintsARolePerActor()
    {
        var first = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();
        var second = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var db = NewContext();
        var firstRole = (await db.Users.SingleAsync(x => x.Id == first.Id, Ct)).RoleId;
        var secondRole = (await db.Users.SingleAsync(x => x.Id == second.Id, Ct)).RoleId;

        Assert.NotNull(firstRole);
        Assert.NotEqual(firstRole, secondRole);
    }

    [Fact]
    public async Task WithSystemPermissions_WithNone_GrantsNothing()
    {
        var actor = await Actor().WithSystemPermissions().SeedAsync();

        Assert.Empty(await PermissionsOf(actor));
    }

    /// <summary>
    /// A seeded role says what a test means better than a list of permissions does. <c>ContentDeveloper</c>
    /// is the one most tests want: the four MSEL permissions and nothing else.
    /// </summary>
    [Fact]
    public async Task WithRole_GrantsTheRolesPermissions()
    {
        var actor = await Actor().WithRole(SystemRoleDefaults.ContentDeveloperRoleId).SeedAsync();

        Assert.Equal(
            [
                nameof(SystemPermission.CreateMsels),
                nameof(SystemPermission.EditMsels),
                nameof(SystemPermission.ManageMsels),
                nameof(SystemPermission.ViewMsels)
            ],
            (await PermissionsOf(actor)).Order());
    }

    /// <summary>
    /// Both calls decide the same column, so taking both silently would mean one of them did nothing. The
    /// message names the way out.
    /// </summary>
    [Fact]
    public void WithRole_AfterWithSystemPermissions_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Actor().WithSystemPermissions(SystemPermission.ViewMsels)
                .WithRole(SystemRoleDefaults.AdministratorRoleId));

        Assert.Contains("WithRole", exception.Message);
    }

    [Fact]
    public void WithSystemPermissions_AfterWithRole_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Actor().WithRole(SystemRoleDefaults.AdministratorRoleId)
                .WithSystemPermissions(SystemPermission.ViewMsels));

        Assert.Contains("Drop one", exception.Message);
    }

    /// <summary>
    /// All four rows, because three of them satisfy no requirement helper at all.
    /// </summary>
    [Fact]
    public async Task OnMsel_WritesTheWholeUnitAndRoleGraph()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var db = NewContext();
        var unitId = actor.MselRole.UnitId;

        Assert.True(await db.Units.AnyAsync(x => x.Id == unitId, Ct));
        Assert.True(await db.UnitUsers.AnyAsync(x => x.UserId == actor.Id && x.UnitId == unitId, Ct));
        Assert.True(await db.MselUnits.AnyAsync(x => x.UnitId == unitId && x.MselId == msel.Id, Ct));
        Assert.True(await db.UserMselRoles.AnyAsync(
            x => x.UserId == actor.Id && x.MselId == msel.Id && x.Role == MselRole.Owner, Ct));
    }

    /// <summary>
    /// The reason all four rows matter, stated as a requirement rather than as row counts.
    /// </summary>
    [Fact]
    public async Task OnMsel_SatisfiesTheRequirementForTheRoleItGranted()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        Assert.True(await MselEditorRequirement.IsMet(actor.Id, msel.Id, Db));
        Assert.True(await MselViewRequirement.IsMet(actor.Id, msel.Id, Db));
        Assert.False(await MselOwnerRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    /// <summary>
    /// A unit per call, so two declared roles are independent. Sharing one would make membership of one
    /// MSEL's unit reach the other, and a test proving isolation would prove nothing.
    /// </summary>
    [Fact]
    public async Task OnMsel_CalledTwice_MintsAUnitEach()
    {
        var first = BlueprintAppFactory.Msel();
        var second = BlueprintAppFactory.Msel();
        await Seed(first, second);

        var actor = await Actor()
            .OnMsel(first, MselRole.Owner)
            .OnMsel(second, MselRole.Viewer)
            .SeedAsync();

        Assert.Equal(2, actor.MselRoles.Count);
        Assert.NotEqual(actor.On(first.Id).UnitId, actor.On(second.Id).UnitId);
        Assert.True(await MselOwnerRequirement.IsMet(actor.Id, first.Id, Db));
        Assert.False(await MselOwnerRequirement.IsMet(actor.Id, second.Id, Db));
    }

    /// <summary>
    /// Passing a unit reuses it, which is how a test puts two actors in one unit or one actor's unit on two
    /// MSELs.
    /// </summary>
    [Fact]
    public async Task OnMsel_WithAGivenUnit_ReusesIt()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        // Read through Db, the context the builder writes through: a second instance of the same row from
        // NewContext() would look detached to it and be added a second time.
        var unit = await Db.Units.SingleAsync(x => x.Id == owner.MselRole.UnitId, Ct);

        var viewer = await Actor().OnMsel(msel, MselRole.Viewer, unit).SeedAsync();

        Assert.Equal(owner.MselRole.UnitId, viewer.MselRole.UnitId);
        Assert.True(await MselViewRequirement.IsMet(viewer.Id, msel.Id, Db));
        Assert.False(await MselOwnerRequirement.IsMet(viewer.Id, msel.Id, Db));
    }

    /// <summary>
    /// The other reuse shape: one unit assigned to two MSELs, which is how a test proves a role is scoped
    /// to its MSEL rather than to the unit. The actor's membership row is written once - it is uniquely
    /// indexed on <c>(UserId, UnitId)</c>.
    /// </summary>
    [Fact]
    public async Task OnMsel_WithOneUnitOnTwoMsels_WritesOneMembership()
    {
        var first = BlueprintAppFactory.Msel();
        var second = BlueprintAppFactory.Msel();
        await Seed(first, second);

        var unit = new UnitEntity
        {
            Id = Guid.NewGuid(),
            Name = $"unit-{Guid.NewGuid()}",
            ShortName = "unit"
        };

        var actor = await Actor()
            .OnMsel(first, MselRole.Owner, unit)
            .OnMsel(second, MselRole.Viewer, unit)
            .SeedAsync();

        Assert.Equal(unit.Id, actor.On(second.Id).UnitId);
        Assert.Equal(1, await NewContext().UnitUsers
            .CountAsync(x => x.UserId == actor.Id && x.UnitId == unit.Id, Ct));
        Assert.True(await MselOwnerRequirement.IsMet(actor.Id, first.Id, Db));
        Assert.True(await MselViewRequirement.IsMet(actor.Id, second.Id, Db));
        Assert.False(await MselOwnerRequirement.IsMet(actor.Id, second.Id, Db));
    }

    /// <summary>
    /// It does not make the actor the MSEL's creator - which would satisfy three of the helpers whatever
    /// role was granted, and quietly turn every role test into a test of nothing.
    /// </summary>
    [Fact]
    public async Task OnMsel_DoesNotMakeTheActorTheCreator()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        Assert.NotEqual(actor.Id, (await NewContext().Msels.SingleAsync(x => x.Id == msel.Id, Ct)).CreatedBy);
    }

    /// <summary>
    /// The guard exists because an unsaved MSEL has <see cref="Guid.Empty"/> for an id, and the four rows
    /// would then key on nothing - a test that seeded in the wrong order would get a silently unrelated
    /// graph rather than an error.
    /// </summary>
    [Fact]
    public void OnMsel_WithAnUnsavedMsel_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Actor().OnMsel(new MselEntity(), MselRole.Owner));

        Assert.Contains("no id", exception.Message);
    }

    [Fact]
    public void OnMsel_WithNoMsel_Throws() =>
        Assert.Throws<ArgumentNullException>(() => Actor().OnMsel(null, MselRole.Owner));

    [Fact]
    public async Task OnTeam_WritesTheMembership()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().OnTeam(team).SeedAsync();

        Assert.Equal([team.Id], actor.TeamIds);
        Assert.True(await NewContext().TeamUsers.AnyAsync(
            x => x.UserId == actor.Id && x.TeamId == team.Id, Ct));
        Assert.True(await MselViewRequirement.IsMet(actor.Id, msel.Id, Db));
    }

    [Fact]
    public void OnTeam_WithAnUnsavedTeam_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Actor().OnTeam(new TeamEntity()));

        Assert.Contains("no id", exception.Message);
    }

    [Fact]
    public void OnTeam_WithNoTeam_Throws() =>
        Assert.Throws<ArgumentNullException>(() => Actor().OnTeam(null));

    /// <summary>
    /// Asking for the single MSEL role of an actor that has none is a mistake in the test, not a null to
    /// carry forward, so it says so.
    /// </summary>
    [Fact]
    public async Task MselRole_ForAnActorWithNoRole_Throws()
    {
        var actor = await Actor().SeedAsync();

        Assert.Throws<InvalidOperationException>(() => actor.MselRole);
    }

    [Fact]
    public async Task On_ForAnMselTheActorHasNoRoleOn_Throws()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        Assert.Throws<InvalidOperationException>(() => actor.On(Guid.NewGuid()));
    }

    /// <summary>
    /// Two actors seeded from one test are independent - the default id is fresh per builder, so nothing
    /// has to be passed to keep them apart.
    /// </summary>
    [Fact]
    public async Task SeedAsync_GivesEachActorItsOwnId()
    {
        var first = await Actor().SeedAsync();
        var second = await Actor().SeedAsync();

        Assert.NotEqual(first.Id, second.Id);
    }

    /// <summary>
    /// What the real <see cref="AuthorizationClaimsTransformer"/> would put on the caller's principal, read
    /// through the real service against the seeded rows.
    /// </summary>
    private async Task<string[]> PermissionsOf(TestActor actor)
    {
        var service = new UserClaimsService(
            NewContext(),
            new MemoryCache(new MemoryCacheOptions()),
            new ClaimsTransformationOptions { EnableCaching = false, CacheExpirationSeconds = 60 });

        var principal = await service.GetClaimsPrincipal(actor.Id, true);

        return
        [
            .. principal.Claims
                .Where(x => x.Type == AuthorizationConstants.PermissionClaimType)
                .Select(x => x.Value)
        ];
    }
}
