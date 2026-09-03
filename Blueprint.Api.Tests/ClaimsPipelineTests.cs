// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// The claims pipeline end to end: authenticate, <c>AuthorizationClaimsTransformer</c>,
/// <c>UserClaimsService</c>, the MVC-wide scope policy, and back out as JSON.
/// </summary>
/// <remarks>
/// <para>
/// <c>AuthorizationClaimsTransformer</c> has no access modifier, so the test assembly cannot name it. It is
/// covered here instead, through the one endpoint whose entire answer is the caller's own claims -
/// <c>GET api/me/systemPermissions</c>. That the response is right means the transformer ran, wrote the
/// claims, and set the current principal, because nothing else in the pipeline does any of those things.
/// </para>
/// <para>
/// The unit-level behaviour of each stage is pinned in <see cref="UserClaimsServiceTests"/>,
/// <see cref="BlueprintAuthorizationServiceTests"/> and <see cref="ClaimsPrincipalExtensionsTests"/>. What
/// is only visible here is the wiring: that they are all registered, run in the right order, and see the
/// database this request belongs to.
/// </para>
/// </remarks>
public class ClaimsPipelineTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    private const string Route = "api/me/systemPermissions";

    [Fact]
    public async Task GetMine_ForAnActorWithNoRole_IsAnEmptyArray()
    {
        var actor = await Actor().SeedAsync();

        Assert.Empty(await Permissions(actor));
    }

    [Fact]
    public async Task GetMine_ForAnActorWithARole_ReturnsTheRolesPermissions()
    {
        var actor = await Actor().WithRole(SystemRoleDefaults.ContentDeveloperRoleId).SeedAsync();

        // Ordered by the enum's underlying value, not alphabetically.
        Assert.Equal(
            [
                SystemPermission.CreateMsels,
                SystemPermission.ViewMsels,
                SystemPermission.EditMsels,
                SystemPermission.ManageMsels
            ],
            (await Permissions(actor)).Order());
    }

    /// <summary>
    /// The <c>AllPermissions</c> expansion, over HTTP: one seeded <c>RoleId</c> becomes all 28 values.
    /// </summary>
    [Fact]
    public async Task GetMine_ForAnAdministrator_ReturnsEverySystemPermission()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        Assert.Equal(
            [.. Enum.GetValues<SystemPermission>().Order()],
            (await Permissions(actor)).Order());
    }

    [Fact]
    public async Task GetMine_ForAnActorWithNamedPermissions_ReturnsExactlyThose()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ViewMsels, SystemPermission.ViewCatalogs)
            .SeedAsync();

        Assert.Equal(
            [SystemPermission.ViewMsels, SystemPermission.ViewCatalogs],
            (await Permissions(actor)).Order());
    }

    /// <summary>
    /// Permissions are serialized as enum names rather than numbers - <c>Startup</c> adds a
    /// <c>JsonStringEnumConverter</c>, and the checked-in <c>blueprint.ui</c> client compares against
    /// strings. Asserted against the raw body, because deserializing with the application's own options
    /// would follow the wire format wherever it went.
    /// </summary>
    [Fact]
    public async Task GetMine_SerializesPermissionsAsNames()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var body = await Client(actor).GetStringAsync(Route, Ct);

        Assert.Equal("[\"ViewMsels\"]", body);
    }

    [Fact]
    public async Task GetMine_WithoutAToken_Is401()
    {
        var response = await AnonymousClient.GetAsync(Route, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The transformer runs <em>before</em> the endpoint, and <c>ValidateUser</c> provisions on first
    /// sight - so a caller with a valid token and no user row gets one, and is answered rather than
    /// refused. This is how every real user's row comes to exist; blueprint has no user-creation endpoint
    /// that a first-time login goes through.
    /// </summary>
    [Fact]
    public async Task GetMine_ForAUserWithNoRow_ProvisionsThemAndAnswers()
    {
        var userId = Guid.NewGuid();

        var response = await Client(userId, "Grace").GetAsync(Route, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadFromJsonAsync<SystemPermission[]>(JsonOptions, Ct));

        var user = await NewContext().Users.SingleAsync(x => x.Id == userId, Ct);
        Assert.Equal("Grace", user.Name);
        Assert.Null(user.RoleId);
    }

    /// <summary>
    /// And the provisioned row records the caller as its own creator, which is what makes
    /// <c>CreatedBy</c> meaningful on rows they go on to write.
    /// </summary>
    [Fact]
    public async Task GetMine_ForAUserWithNoRow_RecordsThemAsTheirOwnCreator()
    {
        var userId = Guid.NewGuid();

        await Client(userId).GetAsync(Route, Ct);

        var user = await NewContext().Users.SingleAsync(x => x.Id == userId, Ct);

        Assert.Equal(userId, user.CreatedBy);
    }

    /// <summary>
    /// A token with no name provisions the row as <c>Anonymous</c> rather than with a null name, so the
    /// column stays populated for the administration UI that lists users.
    /// </summary>
    [Fact]
    public async Task GetMine_ForAUserWithNoRowAndNoNameClaim_NamesThemAnonymous()
    {
        var userId = Guid.NewGuid();

        await Client(userId).GetAsync(Route, Ct);

        var user = await NewContext().Users.SingleAsync(x => x.Id == userId, Ct);

        Assert.Equal("Anonymous", user.Name);
    }

    /// <summary>
    /// An existing user's name is updated from the token on every request, which is how a rename in the
    /// identity provider reaches blueprint - there is nothing else that would carry it.
    /// </summary>
    [Fact]
    public async Task GetMine_UpdatesTheStoredNameFromTheToken()
    {
        var actor = await Actor().WithName("Before").SeedAsync();

        await Client(actor.Id, "After").GetAsync(Route, Ct);

        var user = await NewContext().Users.SingleAsync(x => x.Id == actor.Id, Ct);

        Assert.Equal("After", user.Name);
    }

    /// <summary>
    /// Two callers on one host see their own claims and not each other's. The claims cache is a host-wide
    /// singleton keyed by user id, which is why <c>BlueprintAppFactory</c> disables it - without that, the
    /// second caller here could be answered with the first's permissions.
    /// </summary>
    [Fact]
    public async Task GetMine_IsAnsweredPerCaller()
    {
        var administrator = await Actor().WithAllSystemPermissions().SeedAsync();
        var nobody = await Actor().SeedAsync();

        Assert.Equal(Enum.GetValues<SystemPermission>().Length, (await Permissions(administrator)).Length);
        Assert.Empty(await Permissions(nobody));
    }

    /// <summary>
    /// And a role granted between two requests is visible on the second: claims are rebuilt from the
    /// database per request rather than cached for the connection.
    /// </summary>
    [Fact]
    public async Task GetMine_ReflectsARoleGrantedBetweenRequests()
    {
        var actor = await Actor().SeedAsync();

        Assert.Empty(await Permissions(actor));

        var user = await Db.Users.SingleAsync(x => x.Id == actor.Id, Ct);
        user.RoleId = SystemRoleDefaults.ObserverRoleId;
        await Db.SaveChangesAsync(Ct);

        Assert.NotEmpty(await Permissions(actor));
    }

    /// <summary>
    /// The <c>Observer</c> role is every permission whose name begins with <c>View</c> - the read-only
    /// actor most endpoint tests use to prove a 403 on a write. Pinned here so a change to the seeded role
    /// fails once, beside the seed data, rather than everywhere it is used.
    /// </summary>
    [Fact]
    public async Task GetMine_ForAnObserver_ReturnsOnlyTheViewPermissions()
    {
        var actor = await Actor().WithRole(SystemRoleDefaults.ObserverRoleId).SeedAsync();

        var permissions = await Permissions(actor);

        Assert.NotEmpty(permissions);
        Assert.All(permissions, x => Assert.StartsWith("View", x.ToString()));
        Assert.Equal(
            Enum.GetValues<SystemPermission>().Count(x => x.ToString().StartsWith("View")),
            permissions.Length);
    }

    /// <summary>
    /// MSEL roles are not system permissions and never become claims: the requirement helpers read them
    /// from the database when a service asks. An actor who owns an MSEL still reports none.
    /// </summary>
    [Fact]
    public async Task GetMine_DoesNotReportMselRoles()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        Assert.Empty(await Permissions(actor));
    }

    private async Task<SystemPermission[]> Permissions(TestActor actor)
    {
        var response = await Client(actor).GetAsync(Route, Ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<SystemPermission[]>(JsonOptions, Ct);
    }
}
