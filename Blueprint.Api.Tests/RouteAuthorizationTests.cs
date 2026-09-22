// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
/// <c>{membership}</c>, <c>{user}</c>, <c>{injectType}</c>, <c>{catalog}</c>, <c>{inject}</c>,
/// <c>{spareInject}</c>, <c>{cataloginject}</c>, <c>{unit}</c>, <c>{spareUnit}</c>, <c>{catalogunit}</c>,
/// <c>{citeaction}</c>, <c>{citeduty}</c>, and <c>{unique}</c> for a name that has to clear a unique index.
/// So a row exercises the real object rather than an id nothing matches, and the 403 it asserts is the
/// permission check answering rather than a 404 arriving first. The spares exist so a row that creates a
/// join row does not collide with the one already seeded.
/// <para />
/// Rows are strings so xUnit can serialize them and name the cases readably. A body of <c>"@file"</c> is
/// the sentinel for a multipart upload: the route takes a <c>[FromForm] FileForm</c> whose
/// <c>ToUpload</c> is <c>[Required]</c>, and <c>ValidateModelStateFilter</c> answers 400 before the
/// action's authorization check, so a JSON body would assert nothing. Add a row per route as each
/// remaining service is covered; do not add per-service 401/403 tests.
/// <para />
/// Four routes are deliberately absent, each because it resolves no authorization at all and so cannot
/// satisfy <see cref="EveryRoute_WithNoPermissions_Is403"/>: <c>GET lmt/resource/{mselId}</c>
/// (<c>LmtController</c> is <c>[AllowAnonymous]</c> - see <see cref="LmtEndpointTests"/>),
/// <c>GET my-catalogs</c>, <c>GET citeActions/templates</c> and <c>GET citeDuties/templates</c>. The
/// latter three are covered by the happy-path test in their own files, which is where the absence of a
/// check is recorded.
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
        { "DELETE", "api/invitations/{invitation}", "EditMsels", null },

        // CatalogService / CatalogController - reads want ViewCatalogs, writes ManageCatalogs, and the
        // one route scoped to another user wants ManageUsers. GET my-catalogs is absent, see above.
        { "GET", "api/catalogs", "ViewCatalogs", null },
        { "GET", "api/catalogs/{catalog}", "ViewCatalogs", null },
        { "GET", "api/users/{user}/catalogs", "ManageUsers", null },
        { "POST", "api/catalogs", "ManageCatalogs", """{"name":"{unique}","injectTypeId":"{injectType}"}""" },
        { "POST", "api/catalogs/{catalog}/copy", "ManageCatalogs", null },
        {
            "PUT", "api/catalogs/{catalog}", "ManageCatalogs",
            """{"id":"{catalog}","name":"{unique}","injectTypeId":"{injectType}"}"""
        },
        { "DELETE", "api/catalogs/{catalog}", "ManageCatalogs", null },
        { "GET", "api/catalogs/{catalog}/json", "ManageCatalogs", null },
        { "POST", "api/catalogs/json", "ManageCatalogs", "@file" },

        // CatalogInjectService / CatalogInjectController - reads want ViewCatalogs or a unit the catalog
        // is assigned to, writes ManageCatalogs.
        { "GET", "api/catalogs/{catalog}/cataloginjects", "ViewCatalogs", null },
        { "GET", "api/cataloginjects/{cataloginject}", "ViewCatalogs", null },
        {
            "POST", "api/cataloginjects", "ManageCatalogs",
            """{"catalogId":"{catalog}","injectId":"{spareInject}"}"""
        },
        {
            "POST", "api/cataloginjects/multiple", "ManageCatalogs",
            """[{"catalogId":"{catalog}","injectId":"{spareInject}"}]"""
        },
        { "DELETE", "api/cataloginjects/{cataloginject}", "ManageCatalogs", null },
        { "DELETE", "api/catalogs/{catalog}/injects/{inject}", "ManageCatalogs", null },

        // CatalogUnitService / CatalogUnitController - every route wants ManageCatalogs, except the single
        // read, which the service also grants to a member of the unit.
        { "GET", "api/catalogs/{catalog}/catalogunits", "ManageCatalogs", null },
        { "GET", "api/catalogunits/{catalogunit}", "ManageCatalogs", null },
        {
            "POST", "api/catalogunits", "ManageCatalogs",
            """{"catalogId":"{catalog}","unitId":"{spareUnit}"}"""
        },
        {
            "PUT", "api/catalogunits/{catalogunit}", "ManageCatalogs",
            """{"id":"{catalogunit}","catalogId":"{catalog}","unitId":"{spareUnit}"}"""
        },
        { "DELETE", "api/catalogunits/{catalogunit}", "ManageCatalogs", null },
        { "DELETE", "api/catalogs/{catalog}/units/{unit}", "ManageCatalogs", null },

        // CiteActionService / CiteActionController - reads want ViewMsels, writes EditMsels because every
        // body here names a MSEL, and the two file routes ManageCiteActions. templates is absent, above.
        { "GET", "api/msels/{msel}/citeActions", "ViewMsels", null },
        { "GET", "api/citeActions/{citeaction}", "ViewMsels", null },
        { "POST", "api/citeActions", "EditMsels", """{"mselId":"{msel}","teamId":"{team}"}""" },
        {
            "PUT", "api/citeActions/{citeaction}", "EditMsels",
            """{"id":"{citeaction}","mselId":"{msel}","teamId":"{team}"}"""
        },
        { "DELETE", "api/citeActions/{citeaction}", "EditMsels", null },
        { "POST", "api/citeActions/json", "ManageCiteActions", "@file" },
        { "POST", "api/citeActions/json/download", "ManageCiteActions", "[]" },

        // CiteDutyService / CiteDutyController - the twin of the above, permission for permission.
        { "GET", "api/msels/{msel}/citeDuties", "ViewMsels", null },
        { "GET", "api/citeDuties/{citeduty}", "ViewMsels", null },
        { "POST", "api/citeDuties", "EditMsels", """{"mselId":"{msel}","teamId":"{team}"}""" },
        {
            "PUT", "api/citeDuties/{citeduty}", "EditMsels",
            """{"id":"{citeduty}","mselId":"{msel}","teamId":"{team}"}"""
        },
        { "DELETE", "api/citeDuties/{citeduty}", "EditMsels", null },
        { "POST", "api/citeDuties/json", "ManageCiteDuties", "@file" },
        { "POST", "api/citeDuties/json/download", "ManageCiteDuties", "[]" }
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

    /// <remarks>
    /// The content is owned by the returned request, so it outlives this method and is still readable when
    /// the caller awaits <c>SendAsync</c> - which is the constraint that makes the multipart branch work
    /// (see <c>OrganizationEndpointTests.UploadJson</c>).
    /// </remarks>
    private static HttpRequestMessage Request(
        string method, string route, string body, Dictionary<string, string> graph)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), Substitute(route, graph));

        if (body == "@file")
            request.Content = FilePart();
        else if (body is not null)
            request.Content = new StringContent(Substitute(body, graph), Encoding.UTF8, "application/json");

        return request;
    }

    /// <summary>
    /// A multipart body carrying the <c>ToUpload</c> part the <c>[FromForm] FileForm</c> routes require.
    /// The file need not be valid JSON: the authorization check these theories are about runs before the
    /// service reads it.
    /// </summary>
    private static MultipartFormDataContent FilePart()
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("[]"));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "ToUpload", "upload.json");

        return content;
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
        var injectType = BlueprintAppFactory.InjectType();
        var unit = BlueprintAppFactory.Unit();
        var spareUnit = BlueprintAppFactory.Unit();
        await Seed(invitation, injectType, unit, spareUnit);

        var catalog = BlueprintAppFactory.Catalog(injectType.Id);
        var inject = BlueprintAppFactory.Inject(injectType.Id);
        var spareInject = BlueprintAppFactory.Inject(injectType.Id);
        var citeAction = BlueprintAppFactory.CiteAction(msel.Id, team.Id);
        var citeDuty = BlueprintAppFactory.CiteDuty(msel.Id, team.Id);
        await Seed(catalog, inject, spareInject, citeAction, citeDuty);

        var catalogInject = BlueprintAppFactory.CatalogInject(catalog.Id, inject.Id);
        var catalogUnit = BlueprintAppFactory.CatalogUnit(unit.Id, catalog.Id);
        await Seed(catalogInject, catalogUnit);

        return new Dictionary<string, string>
        {
            ["msel"] = msel.Id.ToString(),
            ["team"] = team.Id.ToString(),
            ["invitation"] = invitation.Id.ToString(),
            ["group"] = group.Id.ToString(),
            ["membership"] = membership.Id.ToString(),
            ["user"] = spare.Id.ToString(),
            ["injectType"] = injectType.Id.ToString(),
            ["catalog"] = catalog.Id.ToString(),
            ["inject"] = inject.Id.ToString(),
            ["spareInject"] = spareInject.Id.ToString(),
            ["cataloginject"] = catalogInject.Id.ToString(),
            ["unit"] = unit.Id.ToString(),
            ["spareUnit"] = spareUnit.Id.ToString(),
            ["catalogunit"] = catalogUnit.Id.ToString(),
            ["citeaction"] = citeAction.Id.ToString(),
            ["citeduty"] = citeDuty.Id.ToString(),
            ["unique"] = $"row-{Guid.NewGuid()}"
        };
    }
}
