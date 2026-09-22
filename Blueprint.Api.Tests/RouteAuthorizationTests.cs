// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Tests.Infrastructure;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// The 401 and 403 half of the endpoint programme, for every route the per-service files leave to it.
/// One row per route: the method, the route template, and the system permission that lifts the refusal.
/// </summary>
/// <remarks>
/// Three theories over one table, which is what makes the table worth having: a route is asserted to
/// refuse an anonymous caller, to refuse an authenticated caller holding nothing, and to stop refusing
/// the caller holding the permission named in its row. The third is what keeps the second honest - a
/// guard that refuses everybody passes the first two and fails the third.
/// <para />
/// Each case seeds the same small graph, all of it created by somebody else, and substitutes its ids into
/// the route and the body: <c>{msel}</c>, <c>{team}</c>, <c>{invitation}</c>, <c>{group}</c>,
/// <c>{membership}</c>, <c>{user}</c>, and <c>{unique}</c> for a name that has to clear a unique index.
/// So a row exercises the real object rather than an id nothing matches, and the 403 it asserts is the
/// permission check answering rather than a 404 arriving first.
/// <para />
/// Rows are strings so xUnit can serialize them and name the cases readably. Add a row per route as each
/// remaining service is covered; do not add per-service 401/403 tests.
/// <para />
/// <c>GET lmt/resource/{mselId}</c> is deliberately absent: <c>LmtController</c> is
/// <c>[AllowAnonymous]</c> and has no authorization to assert. See <see cref="LmtEndpointTests"/>.
/// </remarks>
public class RouteAuthorizationTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    /// <summary>
    /// method, route, the permission that lifts the refusal, and the request body (null for none).
    /// </summary>
    public static TheoryData<string, string, string, string> Routes => new()
    {
        // GroupService / GroupController - reads want ViewGroups, writes ManageGroups.
        { "GET", "api/groups", "ViewGroups", null },
        { "GET", "api/groups/{group}", "ViewGroups", null },
        { "GET", "api/groups/{group}/memberships", "ViewGroups", null },
        { "GET", "api/groups/memberships/{membership}", "ViewGroups", null },
        { "POST", "api/groups", "ManageGroups", """{"name":"{unique}"}""" },
        { "PUT", "api/groups/{group}", "ManageGroups", """{"id":"{group}","name":"{unique}"}""" },
        { "DELETE", "api/groups/{group}", "ManageGroups", null },
        { "POST", "api/groups/{group}/memberships", "ManageGroups", """{"userId":"{user}"}""" },
        { "DELETE", "api/groups/memberships/{membership}", "ManageGroups", null },

        // InvitationService / InvitationController - reads want ViewMsels, writes EditMsels, and each
        // falls back to a role on the MSEL, which the caller in these cases does not have.
        { "GET", "api/msels/{msel}/invitations", "ViewMsels", null },
        { "GET", "api/invitations/{invitation}", "ViewMsels", null },
        { "POST", "api/invitations", "EditMsels", """{"mselId":"{msel}","teamId":"{team}","maxUsersAllowed":10}""" },
        {
            "PUT", "api/invitations/{invitation}", "EditMsels",
            """{"id":"{invitation}","mselId":"{msel}","teamId":"{team}","maxUsersAllowed":10}"""
        },
        { "DELETE", "api/invitations/{invitation}", "EditMsels", null }
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryRoute_Anonymously_Is401(string method, string route, string permission, string body)
    {
        _ = permission;
        var graph = await SeedGraph();

        var response = await AnonymousClient.SendAsync(Request(method, route, body, graph), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryRoute_WithNoPermissions_Is403(string method, string route, string permission, string body)
    {
        _ = permission;
        var graph = await SeedGraph();
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).SendAsync(Request(method, route, body, graph), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// Asserts only that the refusal is lifted, not what replaces it - the per-service files say what a
    /// success looks like. A row whose write collides with the seeded graph would answer 500 and still
    /// pass here, which is deliberate: this theory's subject is the guard.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryRoute_WithTheNamedPermission_IsNotRefused(
        string method, string route, string permission, string body)
    {
        var graph = await SeedGraph();
        var actor = await Actor()
            .WithSystemPermissions(Enum.Parse<SystemPermission>(permission))
            .SeedAsync();

        var response = await Client(actor).SendAsync(Request(method, route, body, graph), Ct);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static HttpRequestMessage Request(
        string method, string route, string body, Dictionary<string, string> graph)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), Substitute(route, graph));

        if (body is not null)
            request.Content = new StringContent(Substitute(body, graph), Encoding.UTF8, "application/json");

        return request;
    }

    private static string Substitute(string text, Dictionary<string, string> graph)
    {
        foreach (var (token, value) in graph)
            text = text.Replace($"{{{token}}}", value);

        return text;
    }

    private async Task<Dictionary<string, string>> SeedGraph()
    {
        var msel = BlueprintAppFactory.Msel();
        var group = BlueprintAppFactory.Group();
        var member = BlueprintAppFactory.User();
        var spare = BlueprintAppFactory.User();
        await Seed(msel, group, member, spare);

        var team = BlueprintAppFactory.Team(msel.Id);
        var membership = BlueprintAppFactory.GroupMembership(group.Id, member.Id);
        await Seed(team, membership);

        var invitation = BlueprintAppFactory.Invitation(msel.Id, team.Id);
        await Seed(invitation);

        return new Dictionary<string, string>
        {
            ["msel"] = msel.Id.ToString(),
            ["team"] = team.Id.ToString(),
            ["invitation"] = invitation.Id.ToString(),
            ["group"] = group.Id.ToString(),
            ["membership"] = membership.Id.ToString(),
            ["user"] = spare.Id.ToString(),
            ["unique"] = $"row-{Guid.NewGuid()}"
        };
    }
}
