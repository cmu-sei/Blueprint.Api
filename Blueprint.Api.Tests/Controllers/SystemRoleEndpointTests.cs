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
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary><c>SystemRoleService</c> / <c>SystemRolesController</c> - the five routes over the table that
/// decides every caller's 28 permissions. The bottom of the authorization stack: a <c>SystemRoleEntity</c>
/// is what <c>UserClaimsService</c> reads to mint the claims that <c>IBlueprintAuthorizationService</c>
/// then checks on every route in the API, including these five.</summary>
public class SystemRoleEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET system-roles
    // ---------------------------------------------------------------------------------------------

    /// <summary>The list answers the three seeded roles to a holder of the seeded Observer role.</summary>
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
    public async Task GetAll_with_ViewRoles_only_returns_the_roles()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewRoles).SeedAsync();

        var roles = await GetRoles(Client(actor));

        Assert.Contains(SystemRoleDefaults.AdministratorRoleId, roles.Select(x => x.Id));
    }

    [Fact]
    public async Task GetAll_is_forbidden_for_a_caller_holding_only_ViewUsers()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUsers).SeedAsync();

        var response = await Client(actor).GetAsync(SystemRoles, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetAll_is_forbidden_for_a_caller_holding_only_ManageUsers()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

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
    public async Task Get_is_forbidden_for_a_caller_holding_only_ViewUsers()
    {
        var role = await SeedRole("auditor");
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUsers).SeedAsync();

        var response = await Client(actor).GetAsync(RoleRoute(role.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>An unknown role is a 404 from the controller's own null check.</summary>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewRoles).SeedAsync();

        var response = await Client(actor).GetAsync(RoleRoute(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Permissions go out as names, read from the raw JSON.</summary>
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

    /// <summary>A create that omits the id is answered with a 201 and a generated id.</summary>
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

    /// <summary>Create with a duplicate name is answered with a 500.</summary>
    [Fact]
    public async Task Create_WithADuplicateName_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageRoles).SeedAsync();

        var response = await Post(Client(actor), Body("Administrator"));

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("An error occurred while saving the entity changes. See the inner exception for details.", failure.Title);
        Assert.Contains("SystemRoleService.CreateAsync", failure.Detail);
    }

    /// <summary>A permission listed twice is stored twice, and an undefined number is stored and answered back as a number.</summary>
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

        var holder = TestData.User(roleId: id);
        await Seed(holder);
        var theirs = ClientFor(holder.Id, holder.Name);

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
        Assert.Equal([MainHub.ROLE_GROUP], Hub.Recipients(MainHubMethods.SystemRoleCreated, actor.Id));
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

    /// <summary>Update whose body names a different id is answered with a 500.</summary>
    [Fact]
    public async Task Update_WhoseBodyNamesADifferentId_Is500()
    {
        var role = await SeedRole("auditor");
        var other = await SeedRole("inspector");
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageRoles).SeedAsync();

        var response = await Put(
            Client(actor), role.Id, BodyFor(role) with { Id = other.Id, Name = "renamed" });

        Assert.Equal("The property 'SystemRoleEntity.Id' is part of a key and so cannot be modified or marked as modified. To change the principal of an existing entity with an identifying foreign key, first delete the dependent and invoke 'SaveChanges', and then associate the dependent with the new principal.", (await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
        Assert.Equal("auditor", (await Stored(role.Id)).Name);
        Assert.Equal("inspector", (await Stored(other.Id)).Name);
    }

    /// <summary>Update that omits the id is answered with a 500.</summary>
    [Fact]
    public async Task Update_ThatOmitsTheId_Is500()
    {
        var role = await SeedRole("auditor");
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageRoles).SeedAsync();

        var response = await Client(actor)
            .PutAsJsonAsync(RoleRoute(role.Id), new { name = "inspector" }, Ct);

        Assert.Equal("The property 'SystemRoleEntity.Id' is part of a key and so cannot be modified or marked as modified. To change the principal of an existing entity with an identifying foreign key, first delete the dependent and invoke 'SaveChanges', and then associate the dependent with the new principal.", (await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
        Assert.Equal("auditor", (await Stored(role.Id)).Name);
    }

    /// <summary>Update may rewrite the seeded administrator role.</summary>
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

        Assert.Equal([MainHub.ROLE_GROUP], Hub.Recipients(MainHubMethods.SystemRoleUpdated, role.Id, actor.Id));
        Assert.Single(Hub.Of(MainHubMethods.SystemRoleUpdated, role.Id, actor.Id));
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

    /// <summary>Delete for a role a user holds is answered with a 500.</summary>
    [Fact]
    public async Task Delete_ForARoleAUserHolds_Is500()
    {
        var role = await SeedRole("auditor", SystemPermission.ViewMsels);
        await Seed(TestData.User(roleId: role.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageRoles).SeedAsync();

        var response = await Client(actor).DeleteAsync(RoleRoute(role.Id), Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("An error occurred while saving the entity changes. See the inner exception for details.", failure.Title);
        Assert.Contains("SystemRoleService.DeleteAsync", failure.Detail);
        Assert.NotNull(await Stored(role.Id));
    }

    /// <summary>Delete may delete the seeded administrator role.</summary>
    [Fact]
    public async Task Delete_MayDeleteTheSeededAdministratorRole()
    {
        await NobodyHoldsTheAdministratorRole();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageRoles).SeedAsync();

        var response = await Client(actor)
            .DeleteAsync(RoleRoute(SystemRoleDefaults.AdministratorRoleId), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await Stored(SystemRoleDefaults.AdministratorRoleId));
    }

    /// <summary>The delete broadcast carries the bare id.</summary>
    [Fact]
    public async Task Delete_TellsTheRoleGroupTheId()
    {
        var role = await SeedRole("auditor");
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageRoles).SeedAsync();

        var response = await Client(actor).DeleteAsync(RoleRoute(role.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Equal([MainHub.ROLE_GROUP], Hub.Recipients(MainHubMethods.SystemRoleDeleted, role.Id, actor.Id));
        var sent = Assert.Single(Hub.Of(MainHubMethods.SystemRoleDeleted, role.Id, actor.Id));
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

        var response = await Client().SendAsync(request, Ct);

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
        var role = TestData.SystemRole(name, permissions: permissions);
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

    /// <summary>
    /// Takes the seeded administrator role from <c>Root</c>, the one user who holds it here, so the role is
    /// held by nobody, as in an installation nobody has been assigned it in yet.
    /// </summary>
    private async Task NobodyHoldsTheAdministratorRole()
    {
        await using var context = NewContext();
        await context.Users
            .Where(x => x.Id == Root.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(u => u.RoleId, (Guid?)null), Ct);
    }
}
