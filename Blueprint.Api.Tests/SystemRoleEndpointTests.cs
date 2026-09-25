// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>SystemRoleService</c> / <c>SystemRolesController</c> - the five routes over the table that decides
/// every caller's 28 permissions. The bottom of the authorization stack: a
/// <c>SystemRoleEntity</c> is what <c>UserClaimsService</c> reads to mint the claims that
/// <c>IBlueprintAuthorizationService</c> then checks on every route in the API, including these five.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The service is 93 lines and checks nothing.</strong> It injects an <c>IPrincipal</c>, casts it
/// to a <c>ClaimsPrincipal</c> and never reads it (<c>SystemRoleService.cs:31</c>, <c>:37</c>) - the only
/// authorization anywhere on these routes is the controller's <c>ViewRoles</c> on the two reads and
/// <c>ManageRoles</c> on the three writes. That is defensible, being a global table with no MSEL to scope
/// it by, and it makes this the one service in unit 3 with no requirement helper in it at all. The
/// consequence worth stating is that <c>ManageRoles</c> is effectively every permission: a holder may give
/// themselves a role with <c>AllPermissions</c> and have all 28
/// (<see cref="Create_WithAllPermissions_GrantsItsHolderEveryPermissionInTheInstallation"/>).
/// </para>
/// <para>
/// <strong><c>Immutable</c> is read nowhere in production.</strong> The column exists, the view model
/// carries it, the seeded Administrator row sets it to <c>true</c>, and not one line reads it - so a
/// <c>ManageRoles</c> holder may rename the seeded Administrator role, clear its <c>AllPermissions</c>
/// flag and empty its permission list (<see cref="Update_MayRewriteTheSeededAdministratorRole"/>) or
/// delete it outright (<see cref="Delete_MayDeleteTheSeededAdministratorRole"/>). The update is the
/// sharper of the two: the caller doing it is normally the holder of that very role, so the request
/// succeeds and their next one is a 403 - a lock-out with no undo, since restoring the role needs
/// <c>ManageRoles</c>, which they have just given up. The flag looks like the guard against exactly that
/// and is decoration. <c>AllPermissions</c>, by contrast, has exactly one reader,
/// <c>UserClaimsService.cs:209</c>.
/// </para>
/// <para>
/// <strong>Two things this service gets right that four of its siblings do not.</strong> Both single-row
/// methods are <c>SingleOrDefaultAsync</c> with the null handled - <c>GetAsync</c> maps the null and lets
/// the controller's check answer 404 (<see cref="Get_ForAnIdThatIsNotThere_Is404"/>), and
/// <c>UpdateAsync</c>/<c>DeleteAsync</c> throw <c>EntityNotFoundException&lt;SystemRole&gt;</c>
/// themselves - where <c>OrganizationService</c>, <c>MoveService</c>, <c>CardService</c> and
/// <c>InjectService</c> all share one `SingleAsync` plus a dead null check whose exception names
/// <c>DataValueEntity</c>. And <c>CreateAsync</c> re-reads by <c>systemRoleEntity.Id</c> rather than by
/// the request body's, so a create that omits the id is a clean 201
/// (<see cref="Create_ThatOmitsTheId_Is201"/>) where <c>UserService.CreateAsync</c> one file over is a
/// 500.
/// </para>
/// <para>
/// <strong><c>UpdateAsync</c> maps the body's <c>Id</c> onto the tracked row, so a PUT whose body does not
/// echo the route's id is a 500.</strong> <c>SystemRoleProfile</c> is two bare <c>CreateMap</c> calls, so
/// <c>Id</c> maps both ways; EF refuses to modify a key and the <c>InvalidOperationException</c> is not an
/// <c>IApiException</c> (<see cref="Update_WhoseBodyNamesADifferentId_Is500"/>,
/// <see cref="Update_ThatOmitsTheId_Is500"/>). The second is the one that bites: a client sending a
/// partial body gets an internal error rather than a 400. This is the same map-the-id defect the branch
/// has recorded seven times, in its mildest form - there is no parent id here to decide a permission
/// from, so all it can do is fail.
/// </para>
/// <para>
/// Also characterized: nothing validates the permission list, so a role may hold the same permission
/// twice and a number the enum does not define, which then crosses the wire as a bare number among names
/// (<see cref="Create_StoresADuplicatedAndAnUndefinedPermission"/>); <c>SystemRoleEntity</c> is not a
/// <c>BaseEntity</c> and <c>ViewModels.SystemRole</c> derives from nothing, so there is no record of who
/// changed the permissions of the role that governs the installation
/// (<see cref="Get_AnswersNoAuditFieldsBecauseNeitherSideHasAny"/>) - correctly declared, unlike
/// <c>TeamUser</c> and <c>UnitUser</c>, but the audit trail a table like this one most wants is the one
/// it does not have; a duplicate name is a 500 from the unique index rather than a 409; and deleting a
/// role somebody holds is a 500 from the foreign key, because <c>UserEntity.RoleId</c> is configured with
/// no delete behaviour at all (<see cref="Delete_ForARoleAUserHolds_Is500"/>) - so the table cannot be
/// tidied without first re-roling every holder, and the API says only "500".
/// </para>
/// <para>
/// One note on the audit stamping, since this is the first entity in the branch whose save has nothing to
/// stamp: <c>BlueprintContext.SaveEntries</c> hard-casts every changed entry to <c>BaseEntity</c> inside a
/// bare <c>catch</c> (<c>BlueprintContext.cs:135-140</c>, <c>:149-154</c>), so saving a system role -
/// like saving a team-user, a unit-user, a card-team or a MSEL-unit row - throws and swallows an
/// <c>InvalidCastException</c> per entry as its normal path. The <c>try</c> is inside the loop, so it
/// costs correctness nothing; it is the mechanism, not a defect, and it is recorded here because it is
/// why none of these tests can assert a stamped field.
/// </para>
/// </remarks>
public class SystemRoleEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET system-roles
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// The actor holds the seeded Observer role rather than one built by
    /// <see cref="TestActorBuilder.WithSystemPermissions"/>, which would write a fourth row and make the
    /// count meaningless. Observer holds every permission whose name begins with "View", so
    /// <c>ViewRoles</c> comes free.
    /// </remarks>
    [Fact]
    public async Task GetAll_ReturnsTheThreeSeededRoles()
    {
        var actor = await Actor().WithRole(SystemRoleDefaults.ObserverRoleId).SeedAsync();

        var roles = await GetRoles(Client(actor));

        Assert.Equal(3, roles.Count);
        Assert.Contains("Administrator", roles.Select(x => x.Name));
        Assert.Contains("Content Developer", roles.Select(x => x.Name));
        Assert.Contains("Observer", roles.Select(x => x.Name));
    }

    [Fact]
    public async Task GetAll_WithoutViewRoles_Is403()
    {
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync(SystemRoles, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// The two shapes a role comes in, both seeded by <c>SystemRoleConfiguration.HasData</c>:
    /// Administrator carries <c>AllPermissions</c> and an <em>empty</em> list, Content Developer carries
    /// four named permissions and the flag cleared. So an empty list does not mean "no permissions" and
    /// a populated one does not mean "only these".
    /// </remarks>
    [Fact]
    public async Task GetAll_AnswersTheSeededPermissionLists()
    {
        var actor = await Actor().WithRole(SystemRoleDefaults.ObserverRoleId).SeedAsync();

        var roles = await GetRoles(Client(actor));

        var administrator = roles.Single(x => x.Id == SystemRoleDefaults.AdministratorRoleId);

        Assert.True(administrator.AllPermissions);
        Assert.Empty(administrator.Permissions);

        var developer = roles.Single(x => x.Id == SystemRoleDefaults.ContentDeveloperRoleId);

        Assert.False(developer.AllPermissions);
        Assert.Equal(4, developer.Permissions.Count);
        Assert.Contains(SystemPermission.ManageMsels, developer.Permissions);
        Assert.DoesNotContain(SystemPermission.ManageRoles, developer.Permissions);
    }

    // ---------------------------------------------------------------------------------------------
    // GET system-roles/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_WithViewRolesOnly_ReturnsTheRole()
    {
        var role = await SeedRole("auditor", SystemPermission.ViewMsels);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewRoles).SeedAsync();

        var answered = await GetRole(Client(actor), role.Id);

        Assert.Equal("auditor", answered.Name);
        Assert.False(answered.AllPermissions);
        Assert.Equal([SystemPermission.ViewMsels], answered.Permissions);
    }

    [Fact]
    public async Task Get_WithoutViewRoles_Is403()
    {
        var role = await SeedRole("auditor");
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync(RoleRoute(role.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// The well-behaved shape, and the contrast worth naming: <c>GetAsync</c> is
    /// <c>SingleOrDefaultAsync</c>, the map of a null answers null and the controller's check at
    /// <c>SystemRoleController.cs:49-50</c> is live - where the four services sharing the `SingleAsync`
    /// copy answer 500 and their identical null checks are dead.
    /// </remarks>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewRoles).SeedAsync();

        var response = await Client(actor).GetAsync(RoleRoute(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <remarks>
    /// Asserted against the raw JSON rather than through <c>JsonOptions</c>: deserializing with the
    /// application's own options would follow the wire format wherever it went, and <c>blueprint.ui</c>'s
    /// checked-in client would not. These names are what a UI matches its permission checkboxes against.
    /// </remarks>
    [Fact]
    public async Task Get_SerializesThePermissionsAsNames()
    {
        var role = await SeedRole("auditor", SystemPermission.ViewMsels, SystemPermission.ManageRoles);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewRoles).SeedAsync();

        var response = await Client(actor).GetAsync(RoleRoute(role.Id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Contains("\"ViewMsels\"", body);
        Assert.Contains("\"ManageRoles\"", body);
        Assert.DoesNotContain("\"permissions\":[2", body);
    }

    /// <remarks>
    /// <c>SystemRoleEntity</c> has no audit columns and <c>ViewModels.SystemRole</c> derives from
    /// nothing, so the surface and the storage agree - unlike <c>ViewModels.TeamUser</c> and
    /// <c>ViewModels.UnitUser</c>, which promise four fields their entities cannot store. Correctly
    /// declared, then, and still the gap that matters most: nothing records who granted a role
    /// <c>AllPermissions</c>, or when.
    /// </remarks>
    [Fact]
    public async Task Get_AnswersNoAuditFieldsBecauseNeitherSideHasAny()
    {
        var role = await SeedRole("auditor");
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewRoles).SeedAsync();

        var response = await Client(actor).GetAsync(RoleRoute(role.Id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.DoesNotContain("createdBy", body);
        Assert.DoesNotContain("dateCreated", body);
        Assert.DoesNotContain("modifiedBy", body);
        Assert.DoesNotContain("dateModified", body);
    }

    // ---------------------------------------------------------------------------------------------
    // POST system-roles
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_WithManageRolesOnly_Is201()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageRoles).SeedAsync();
        var id = Guid.NewGuid();

        var response = await Post(Client(actor), Body("auditor") with
        {
            Id = id,
            Description = "reads everything",
            Permissions = [SystemPermission.ViewMsels]
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.EndsWith($"/api/system-roles/{id}", response.Headers.Location?.ToString());

        var created = await Read<ViewModels.SystemRole>(response);

        Assert.Equal("auditor", created.Name);
        Assert.Equal([SystemPermission.ViewMsels], created.Permissions);

        var stored = await Stored(id);

        Assert.Equal("reads everything", stored.Description);
        Assert.Equal([SystemPermission.ViewMsels], stored.Permissions);
    }

    [Fact]
    public async Task Create_WithoutManageRoles_Is403()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewRoles).SeedAsync();

        var response = await Post(Client(actor), Body("auditor"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("auditor", (await StoredRoles()).Select(x => x.Name));
    }

    /// <remarks>
    /// The positive control for <c>UserService.CreateAsync</c>'s defect one file over.
    /// <c>AddPostgresUUIDGeneration</c> gives the id column a database default, so the entity comes back
    /// with one, and this service re-reads by <c>systemRoleEntity.Id</c> - the generated value - where
    /// <c>UserService</c> re-reads by the request body's and answers 500 when the body omitted it.
    /// </remarks>
    [Fact]
    public async Task Create_ThatOmitsTheId_Is201()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageRoles).SeedAsync();

        var response = await Post(Client(actor), new { name = "auditor" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await Read<ViewModels.SystemRole>(response);

        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.EndsWith($"/api/system-roles/{created.Id}", response.Headers.Location?.ToString());
        Assert.Equal("auditor", (await Stored(created.Id)).Name);
    }

    /// <remarks>
    /// <c>SystemRoleConfiguration</c> declares <c>Name</c> uniquely indexed, so a second "Administrator"
    /// is a <c>DbUpdateException</c> - a 500 where a 409 is the answer, the same shape as every other
    /// duplicate on this branch. The name is also the only thing a UI has to tell two roles apart.
    /// </remarks>
    [Fact]
    public async Task Create_WithADuplicateName_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageRoles).SeedAsync();

        var response = await Post(Client(actor), Body("Administrator"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <remarks>
    /// Nothing validates the list. The same permission twice is stored twice, and a number the enum does
    /// not define is stored and handed back - where <c>JsonStringEnumConverter</c> writes it as a bare
    /// number among names, so one role's <c>permissions</c> array crosses the wire in two dialects. Phase
    /// 2 recorded the reading half of this, <c>UserClaimsService.GetSystemPermissions</c> accepting
    /// <c>"999"</c> from a token; this is where such a value gets into the database in the first place.
    /// </remarks>
    [Fact]
    public async Task Create_StoresADuplicatedAndAnUndefinedPermission()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageRoles).SeedAsync();
        var id = Guid.NewGuid();

        var response = await Post(Client(actor), Body("auditor") with
        {
            Id = id,
            Permissions = [SystemPermission.ViewMsels, SystemPermission.ViewMsels, (SystemPermission)999]
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains("999", await response.Content.ReadAsStringAsync(Ct));

        var stored = await Stored(id);

        Assert.Equal(3, stored.Permissions.Count);
        Assert.Equal(2, stored.Permissions.Count(x => x == SystemPermission.ViewMsels));
        Assert.Contains((SystemPermission)999, stored.Permissions);
    }

    /// <remarks>
    /// What <c>ManageRoles</c> is worth, end to end. The role is created with an <em>empty</em> permission
    /// list and <c>AllPermissions</c> set, then given to a user, and that user's very next request holds
    /// permissions nobody enumerated: <c>ViewRoles</c> here, and <c>ViewUnits</c> to show it is not one
    /// permission but the set. <c>UserClaimsService.cs:209</c> is the single line that does it. So a
    /// <c>ManageRoles</c> holder is an administrator in two requests, and the second one need not even be
    /// about themselves.
    /// </remarks>
    [Fact]
    public async Task Create_WithAllPermissions_GrantsItsHolderEveryPermissionInTheInstallation()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageRoles).SeedAsync();
        var id = Guid.NewGuid();

        var response = await Post(Client(actor), Body("second administrator") with
        {
            Id = id,
            AllPermissions = true,
            Permissions = []
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var holder = BlueprintAppFactory.User(roleId: id);
        await Seed(holder);
        var theirs = Client(holder.Id, holder.Name);

        Assert.Equal(HttpStatusCode.OK, (await theirs.GetAsync(SystemRoles, Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await theirs.GetAsync($"/api/units/{Guid.NewGuid()}/users", Ct)).StatusCode);
    }

    /// <remarks>
    /// <c>SystemRoleCreatedSignalRHandler</c> addresses <c>MainHub.ROLE_GROUP</c> through
    /// <c>Clients.Groups(...)</c> - the <c>params string[]</c> extension - where the other 22 handlers use
    /// <c>Clients.Group(...)</c>; <c>HubRecorder</c> implements both and cannot tell them apart, which is
    /// right, because a client cannot either. The group is joined at <c>MainHub.cs:249</c>. There is no
    /// per-user notification, so a caller whose own role was just rewritten learns nothing until they
    /// make a request and are refused.
    /// </remarks>
    [Fact]
    public async Task Create_TellsTheRoleGroup()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageRoles).SeedAsync();

        var response = await Post(Client(actor), Body("auditor"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal([MainHub.ROLE_GROUP], Hub.Recipients(MainHubMethods.SystemRoleCreated));
    }

    // ---------------------------------------------------------------------------------------------
    // PUT system-roles/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_WithManageRolesOnly_RenamesTheRole()
    {
        var role = await SeedRole("auditor", SystemPermission.ViewMsels);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageRoles).SeedAsync();

        var response = await Put(Client(actor), role.Id, BodyFor(role) with
        {
            Name = "inspector",
            Permissions = [SystemPermission.ViewMsels, SystemPermission.ViewUnits]
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = await Read<ViewModels.SystemRole>(response);

        Assert.Equal("inspector", updated.Name);

        var stored = await Stored(role.Id);

        Assert.Equal("inspector", stored.Name);
        Assert.Equal(2, stored.Permissions.Count);
    }

    [Fact]
    public async Task Update_WithoutManageRoles_Is403()
    {
        var role = await SeedRole("auditor");
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewRoles).SeedAsync();

        var response = await Put(Client(actor), role.Id, BodyFor(role) with { Name = "inspector" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auditor", (await Stored(role.Id)).Name);
    }

    [Fact]
    public async Task Update_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageRoles).SeedAsync();
        var id = Guid.NewGuid();

        var response = await Put(Client(actor), id, Body("auditor") with { Id = id });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <remarks>
    /// <c>SystemRoleProfile</c> maps <c>Id</c> both ways, so the map writes the body's id onto the tracked
    /// row and EF refuses to modify a key. The <c>InvalidOperationException</c> is not an
    /// <c>IApiException</c>, so it is a 500 - and the row named by the route is untouched, because the
    /// save never happened.
    /// </remarks>
    [Fact]
    public async Task Update_WhoseBodyNamesADifferentId_Is500()
    {
        var role = await SeedRole("auditor");
        var other = await SeedRole("inspector");
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageRoles).SeedAsync();

        var response = await Put(
            Client(actor), role.Id, BodyFor(role) with { Id = other.Id, Name = "renamed" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("auditor", (await Stored(role.Id)).Name);
        Assert.Equal("inspector", (await Stored(other.Id)).Name);
    }

    /// <remarks>
    /// The same line from the direction a client actually reaches it. A body that simply does not mention
    /// the id deserializes to <c>Guid.Empty</c>, which the map writes onto the key just the same, so a PUT
    /// is unusable by any client that does not echo the whole row back - and the answer is a 500 rather
    /// than the 400 that would say so. Fixing it - an <c>Ignore</c> on <c>Id</c> in
    /// <c>SystemRoleProfile</c>'s second map - reddens this and
    /// <see cref="Update_WhoseBodyNamesADifferentId_Is500"/> together.
    /// </remarks>
    [Fact]
    public async Task Update_ThatOmitsTheId_Is500()
    {
        var role = await SeedRole("auditor");
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageRoles).SeedAsync();

        var response = await Client(actor)
            .PutAsJsonAsync(RoleRoute(role.Id), new { name = "inspector" }, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("auditor", (await Stored(role.Id)).Name);
    }

    /// <remarks>
    /// The headline, and the one test in this file whose subject is a row the migrations seeded.
    /// <c>Immutable</c> is <c>true</c> on the Administrator role and is read nowhere, so the caller -
    /// who holds that very role, since <c>ManageRoles</c> is how they got here - renames it, clears
    /// <c>AllPermissions</c> and is answered 200. Their next request is a 403, and restoring the role
    /// needs <c>ManageRoles</c>, which they no longer have: an installation can be locked out of its own
    /// role administration by one well-formed PUT. Note <c>Immutable</c> survives the write, which is the
    /// proof it is decoration rather than a guard that was bypassed.
    /// </remarks>
    [Fact]
    public async Task Update_MayRewriteTheSeededAdministratorRole()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();
        var client = Client(actor);

        var response = await Put(client, SystemRoleDefaults.AdministratorRoleId, new RoleBody
        {
            Id = SystemRoleDefaults.AdministratorRoleId,
            Name = "Administrator (retired)",
            AllPermissions = false,
            Immutable = true,
            Permissions = []
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await Stored(SystemRoleDefaults.AdministratorRoleId);

        Assert.Equal("Administrator (retired)", stored.Name);
        Assert.False(stored.AllPermissions);
        Assert.True(stored.Immutable);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(SystemRoles, Ct)).StatusCode);
    }

    [Fact]
    public async Task Update_TellsTheRoleGroup()
    {
        var role = await SeedRole("auditor");
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageRoles).SeedAsync();

        var response = await Put(Client(actor), role.Id, BodyFor(role) with { Name = "inspector" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var sent = Assert.Single(Hub.Of(MainHubMethods.SystemRoleUpdated));

        Assert.Equal(MainHub.ROLE_GROUP, sent.Group);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE system-roles/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_WithManageRolesOnly_Is204()
    {
        var role = await SeedRole("auditor");
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageRoles).SeedAsync();

        var response = await Client(actor).DeleteAsync(RoleRoute(role.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await Stored(role.Id));
    }

    [Fact]
    public async Task Delete_WithoutManageRoles_Is403()
    {
        var role = await SeedRole("auditor");
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewRoles).SeedAsync();

        var response = await Client(actor).DeleteAsync(RoleRoute(role.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(await Stored(role.Id));
    }

    [Fact]
    public async Task Delete_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageRoles).SeedAsync();

        var response = await Client(actor).DeleteAsync(RoleRoute(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <remarks>
    /// <c>UserEntity.RoleId</c> is an optional foreign key with no configured delete behaviour, so EF's
    /// convention leaves the database refusing the delete rather than nulling the column - and the
    /// <c>DbUpdateException</c> is a 500. The service checks no dependents and offers no message, so
    /// removing a role that is still in use is indistinguishable from the server being broken; the fix is
    /// either a dependent count and a 409, or <c>OnDelete(SetNull)</c>, and the choice between them is a
    /// policy question about what happens to the holders.
    /// </remarks>
    [Fact]
    public async Task Delete_ForARoleAUserHolds_Is500()
    {
        var role = await SeedRole("auditor", SystemPermission.ViewMsels);
        await Seed(BlueprintAppFactory.User(roleId: role.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageRoles).SeedAsync();

        var response = await Client(actor).DeleteAsync(RoleRoute(role.Id), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.NotNull(await Stored(role.Id));
    }

    /// <remarks>
    /// The other half of <see cref="Update_MayRewriteTheSeededAdministratorRole"/>: <c>Immutable</c> does
    /// not protect the row from removal either. It succeeds here only because nobody holds the role in
    /// this test's database - in a real installation the foreign key above would refuse it, which is an
    /// accident rather than a guard, and does not apply to the seeded Content Developer or Observer rows
    /// that a fresh installation nobody has been assigned to yet still expects to have.
    /// </remarks>
    [Fact]
    public async Task Delete_MayDeleteTheSeededAdministratorRole()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageRoles).SeedAsync();

        var response = await Client(actor)
            .DeleteAsync(RoleRoute(SystemRoleDefaults.AdministratorRoleId), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await Stored(SystemRoleDefaults.AdministratorRoleId));
    }

    /// <remarks>
    /// The deleted notification's payload is the bare id rather than the view model, which is the same
    /// shape the other handlers use for a delete.
    /// </remarks>
    [Fact]
    public async Task Delete_TellsTheRoleGroupTheId()
    {
        var role = await SeedRole("auditor");
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageRoles).SeedAsync();

        var response = await Client(actor).DeleteAsync(RoleRoute(role.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var sent = Assert.Single(Hub.Of(MainHubMethods.SystemRoleDeleted));

        Assert.Equal(MainHub.ROLE_GROUP, sent.Group);
        Assert.Equal(role.Id, sent.Payload);
    }

    // ---------------------------------------------------------------------------------------------
    // Authentication
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "system-roles")]
    [InlineData("GET", "system-roles/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "system-roles")]
    [InlineData("PUT", "system-roles/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "system-roles/00000000-0000-0000-0000-000000000001")]
    public async Task EveryRouteRefusesAnUnauthenticatedRequest(string method, string route)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/{route}")
        {
            Content = JsonContent.Create(new { })
        };

        var response = await AnonymousClient.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------

    private const string SystemRoles = "/api/system-roles";

    private static string RoleRoute(Guid id) => $"{SystemRoles}/{id}";

    /// <summary>
    /// The wire shape of a role. Every property is non-nullable on both sides and there are no audit
    /// fields, which makes this the simplest body record on the branch.
    /// </summary>
    private sealed record RoleBody
    {
        public Guid Id { get; init; }
        public string Name { get; init; }
        public string Description { get; init; }
        public bool AllPermissions { get; init; }
        public bool Immutable { get; init; }
        public List<SystemPermission> Permissions { get; init; } = [];
    }

    private static RoleBody Body(string name) => new() { Name = name };

    private static RoleBody BodyFor(SystemRoleEntity role) => new()
    {
        Id = role.Id,
        Name = role.Name,
        Description = role.Description,
        AllPermissions = role.AllPermissions,
        Immutable = role.Immutable,
        Permissions = [.. role.Permissions]
    };

    private async Task<SystemRoleEntity> SeedRole(
        string name, params SystemPermission[] permissions)
    {
        var role = BlueprintAppFactory.SystemRole(name, permissions: permissions);
        await Seed(role);

        return role;
    }

    private Task<HttpResponseMessage> Post(HttpClient client, object body) =>
        client.PostAsJsonAsync(SystemRoles, body, Ct);

    private Task<HttpResponseMessage> Put(HttpClient client, Guid id, RoleBody body) =>
        client.PutAsJsonAsync(RoleRoute(id), body, Ct);

    private async Task<List<ViewModels.SystemRole>> GetRoles(HttpClient client)
    {
        var response = await client.GetAsync(SystemRoles, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<List<ViewModels.SystemRole>>(response);
    }

    private async Task<ViewModels.SystemRole> GetRole(HttpClient client, Guid id)
    {
        var response = await client.GetAsync(RoleRoute(id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<ViewModels.SystemRole>(response);
    }

    private async Task<SystemRoleEntity> Stored(Guid id)
    {
        await using var context = NewContext();

        return await context.SystemRoles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, Ct);
    }

    private async Task<List<SystemRoleEntity>> StoredRoles()
    {
        await using var context = NewContext();

        return await context.SystemRoles.AsNoTracking().ToListAsync(Ct);
    }

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }
}
