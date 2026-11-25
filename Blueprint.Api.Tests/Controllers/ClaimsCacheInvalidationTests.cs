// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// The claims cache against the changes that should end it: a change of a user's role, the user's
/// deletion, and a change to the permissions of the role they hold. Each is in force on the user's very
/// next request, though the cache entry would otherwise live for an hour.
/// </summary>
/// <remarks>
/// <c>GET system-roles</c> wants <c>ViewRoles</c>, so its status is the subject's permissions as the
/// host currently believes them. The first request in each test is what fills the cache.
/// </remarks>
public class ClaimsCacheInvalidationTests(DatabaseFixture fixture, ClaimsCachingFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<ClaimsCachingFactory>
{
    private const string SystemRoles = "api/system-roles";

    [Fact]
    public async Task RemovingAUsersRole_TakesEffectOnTheirNextRequest()
    {
        var subject = await Actor().WithSystemPermissions(SystemPermission.ViewRoles).SeedAsync();
        var admin = await Actor().WithAllSystemPermissions().SeedAsync();
        Assert.Equal(HttpStatusCode.OK, (await Client(subject).GetAsync(SystemRoles, Ct)).StatusCode);

        var update = await Client(admin).PutAsJsonAsync(
            $"api/users/{subject.Id}", new { id = subject.Id, name = subject.Name, roleId = (Guid?)null }, Ct);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await Client(subject).GetAsync(SystemRoles, Ct)).StatusCode);
    }

    [Fact]
    public async Task GivingAUserARole_TakesEffectOnTheirNextRequest()
    {
        var subject = await Actor().SeedAsync();
        var admin = await Actor().WithAllSystemPermissions().SeedAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(subject).GetAsync(SystemRoles, Ct)).StatusCode);

        var update = await Client(admin).PutAsJsonAsync(
            $"api/users/{subject.Id}",
            new { id = subject.Id, name = subject.Name, roleId = SystemRoleDefaults.ObserverRoleId },
            Ct);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Client(subject).GetAsync(SystemRoles, Ct)).StatusCode);
    }

    /// <summary>A deleted user's next request is answered from their row as it now stands - re-provisioned
    /// with no role - and not from the claims they held before.</summary>
    [Fact]
    public async Task DeletingAUser_EndsTheirCachedPermissions()
    {
        var subject = await Actor().WithSystemPermissions(SystemPermission.ViewRoles).SeedAsync();
        var admin = await Actor().WithAllSystemPermissions().SeedAsync();
        Assert.Equal(HttpStatusCode.OK, (await Client(subject).GetAsync(SystemRoles, Ct)).StatusCode);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await Client(admin).DeleteAsync($"api/users/{subject.Id}", Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await Client(subject).GetAsync(SystemRoles, Ct)).StatusCode);
    }

    [Fact]
    public async Task ChangingTheRolesPermissions_TakesEffectOnEveryHoldersNextRequest()
    {
        var subject = await Actor().WithSystemPermissions(SystemPermission.ViewRoles).SeedAsync();
        var admin = await Actor().WithAllSystemPermissions().SeedAsync();
        var roleId = (await ReadBack(rb => rb.Users.SingleAsync(x => x.Id == subject.Id, Ct))).RoleId!.Value;
        var role = await ReadBack(rb => rb.SystemRoles.SingleAsync(x => x.Id == roleId, Ct));
        Assert.Equal(HttpStatusCode.OK, (await Client(subject).GetAsync(SystemRoles, Ct)).StatusCode);

        var update = await Client(admin).PutAsJsonAsync(
            $"{SystemRoles}/{roleId}",
            new { id = roleId, name = role.Name, allPermissions = false, permissions = Array.Empty<SystemPermission>() },
            Ct);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await Client(subject).GetAsync(SystemRoles, Ct)).StatusCode);
    }
}
