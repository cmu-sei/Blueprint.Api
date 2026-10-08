// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// App-specific: api/groups is gated on ViewGroups (403 for a caller without it), api/users lists every user
// for a holder of ViewUsers, group names are uniquely indexed, which the concurrency probes rely on, and
// api/organizations stamps CreatedBy with the caller, which the write probe reads back.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Blueprint.Api.Tests.Support;

/// <summary>
/// Tests for the HTTP harness itself: a harness that routes to the wrong database or authorizes everything
/// reads as a green suite.
/// </summary>
public class HttpHarnessTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    private const string SharedName = "Http Isolation Probe";

    [Fact]
    public async Task The_swagger_document_is_served()
    {
        await AssertStatus(HttpStatusCode.OK, await Client().GetAsync("/swagger/v1/swagger.json", Ct));
    }

    /// <summary>A request with no identity is answered with a 401; the anonymous client is the case under test.</summary>
    [Fact]
    public async Task A_request_with_no_identity_is_unauthorized()
    {
        await AssertStatus(HttpStatusCode.Unauthorized, await Client().GetAsync("api/groups", Ct));
    }

    /// <summary>The claims transformer ran and derived nothing, rather than the pipeline granting by default.</summary>
    [Fact]
    public async Task An_actor_with_no_permissions_is_forbidden()
    {
        var actor = await Actor().SeedAsync();

        await AssertStatus(HttpStatusCode.Forbidden, await Client(actor).GetAsync("api/groups", Ct));
    }

    /// <summary>The seeded administrator role becomes real claims, and the request reads this test's database.</summary>
    [Fact]
    public async Task Root_reads_what_the_test_seeded()
    {
        var response = await RootClient.GetAsync("api/users", Ct);

        var users = await ReadAsync<List<IdOnly>>(response);
        Assert.Contains(Root.Id, users.Select(x => x.Id));
    }

    [Fact]
    public Task Concurrent_tests_share_the_host_but_not_the_database_first() => CreateSharedNameGroup();

    [Fact]
    public Task Concurrent_tests_share_the_host_but_not_the_database_second() => CreateSharedNameGroup();

    /// <summary>The row a request wrote is in this test's database and was written as the caller.</summary>
    [Fact]
    public async Task A_request_writes_to_this_tests_database()
    {
        var actor = await Actor().WithSystemPermissions(Data.Enumerations.SystemPermission.ManageOrganizations).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "api/organizations",
            new { name = "written-by-a-request", shortName = "wbar", email = "wbar@organization.test" },
            Ct);

        await AssertStatus(HttpStatusCode.Created, response);
        await using var context = NewContext();
        var stored = await context.Organizations.AsNoTracking().SingleAsync(x => x.Name == "written-by-a-request", Ct);
        Assert.Equal(actor.Id, stored.CreatedBy);
    }

    /// <summary>The host's own database is not this test's, so a request that reached it would be visible.</summary>
    [Fact]
    public void The_host_has_a_database_of_its_own()
    {
        _ = Factory.Services;

        Assert.NotEqual(Factory.HostDatabaseName, Session.DatabaseName);
    }

    /// <summary>
    /// A request that names no test database fails rather than quietly reaching another one. The exception
    /// the routing throws is answered as a 500 by the Development host.
    /// </summary>
    [Fact]
    public async Task A_request_with_no_session_header_is_refused()
    {
        using var unrouted = Factory.CreateClient();
        unrouted.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Root.Id.ToString());

        var response = await unrouted.GetAsync("api/organizations/templates", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    private async Task CreateSharedNameGroup()
    {
        var response = await RootClient.PostAsJsonAsync("api/groups", new { name = SharedName }, Ct);

        await AssertStatus(HttpStatusCode.Created, response);

        await using var context = NewContext();
        Assert.Equal(1, await context.Groups.CountAsync(x => x.Name == SharedName, Ct));
    }

    private sealed record IdOnly(Guid Id);
}
