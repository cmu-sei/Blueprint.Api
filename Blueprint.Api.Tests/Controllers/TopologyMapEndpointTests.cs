// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// <c>GET msels/{id}/topology-map/preview</c> and <c>POST msels/{id}/topology-map</c>, through
/// <c>TopologyMapService</c> out to Caster, Player's VM API and Player at the transport.
/// </summary>
/// <remarks>
/// Caster is read with the caller's own token, so the caller's <c>Authorization</c> header is what goes
/// to Caster; Player and its VM API are written as the service account, whose token the identity
/// provider stub hands out as <see cref="Tokens.Header"/>. Both routes are for a MSEL owner or a caller
/// with <see cref="SystemPermission.ManageMsels"/>, as the push is.
/// </remarks>
public class TopologyMapEndpointTests(DatabaseFixture fixture, TopologyMapAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<TopologyMapAppFactory>
{
    private static readonly Guid WorkspaceId = Guid.Parse("cccccccc-0000-0000-0000-000000000002");
    private static readonly Guid ViewId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AdminTeamId = Guid.Parse("00000000-0000-0000-0000-0000000000ad");
    private static readonly Guid ApplicationId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid MapId = TopologyMapPublisher.MapId(ViewId);

    private new TopologyMapAppFactory Factory => factory;

    // ---------------------------------------------------------------------------------------------
    // GET msels/{id}/topology-map/preview
    // ---------------------------------------------------------------------------------------------

    /// <summary>The drawing and click points, from Caster read as the caller; Player is not asked.</summary>
    [Fact]
    public async Task Preview_DrawsTheWorkspaceWithPlayerNamesAndSendsNothingToPlayer()
    {
        var (msel, team) = await SeedDeployedMsel();
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        Factory.Siblings = Siblings(team.Id);

        var preview = await ReadAsync<ViewModels.TopologyMapPreview>(await Send(owner, HttpMethod.Get, Preview(msel)));

        Assert.Equal(msel.Id, preview.MselId);
        Assert.Equal(WorkspaceId, preview.CasterWorkspaceId);
        Assert.Contains("Domain Controller", preview.Svg);
        Assert.DoesNotContain(">dc-01<", preview.Svg);
        Assert.StartsWith("data:image/svg+xml;base64,", preview.ImageUrl);
        Assert.Equal(["Domain Controller"], preview.ClickPoints.Select(p => p.Urls.Single()));
        Assert.Equal(["router"], preview.MachinesWithoutConsole);
        Assert.Equal(2, preview.MachineCount);
        var sent = Assert.Single(Factory.Siblings.Sent);
        Assert.Equal($"caster/api/workspaces/{WorkspaceId}/topology", sent.Path);
        Assert.Equal($"Bearer {owner.Id}", sent.Authorization);
    }

    [Fact]
    public async Task Preview_ForACallerWithManageMsels_IsAllowed()
    {
        var (msel, team) = await SeedDeployedMsel();
        var developer = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();
        Factory.Siblings = Siblings(team.Id);

        await AssertStatus(HttpStatusCode.OK, await Send(developer, HttpMethod.Get, Preview(msel)));
    }

    /// <summary>An editor is not an owner, and is refused before Caster is asked.</summary>
    [Fact]
    public async Task Preview_ForAnEditor_Is403()
    {
        var (msel, team) = await SeedDeployedMsel();
        var editor = await Actor().OnMsel(msel, MselRole.Editor).WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();
        Factory.Siblings = Siblings(team.Id);

        await AssertStatus(HttpStatusCode.Forbidden, await Send(editor, HttpMethod.Get, Preview(msel)));
        Assert.Empty(Factory.Siblings.Sent);
    }

    [Fact]
    public async Task Preview_Anonymously_Is401()
    {
        var (msel, _) = await SeedDeployedMsel();

        await AssertStatus(HttpStatusCode.Unauthorized, await Client().GetAsync(Preview(msel), Ct));
    }

    /// <summary>What Caster will not show the caller is refused as a 403 naming the workspace.</summary>
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Preview_WhenCasterRefusesTheCaller_Is403(HttpStatusCode casterAnswer)
    {
        var (msel, _) = await SeedDeployedMsel();
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        Factory.Siblings = new SiblingApiHandler().Answers($"GET caster/api/workspaces/{WorkspaceId}/topology", casterAnswer);

        var problem = await AssertJsonError(HttpStatusCode.Forbidden, await Send(owner, HttpMethod.Get, Preview(msel)));

        Assert.Contains(WorkspaceId.ToString(), problem.Title);
    }

    [Fact]
    public async Task Preview_OfAMselWithNoCasterWorkspace_Is409()
    {
        var (msel, _) = await SeedDeployedMsel(hasWorkspace: false);
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        Factory.Siblings = new SiblingApiHandler();

        var problem = await AssertJsonError(HttpStatusCode.Conflict, await Send(owner, HttpMethod.Get, Preview(msel)));

        Assert.Contains("no Caster workspace", problem.Title);
        Assert.Empty(Factory.Siblings.Sent);
    }

    [Fact]
    public async Task Preview_OfAMselThatDoesNotUseCaster_Is409()
    {
        var (msel, _) = await SeedDeployedMsel(useCaster: false);
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        Factory.Siblings = new SiblingApiHandler();

        await AssertJsonError(HttpStatusCode.Conflict, await Send(owner, HttpMethod.Get, Preview(msel)));
    }

    // ---------------------------------------------------------------------------------------------
    // POST msels/{id}/topology-map
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A view with no map gets one, under the view's own map id, with the drawing as its image and a
    /// click point per machine with a Player name, for the teams that can open those machines and the
    /// view's Admin team. Written as the service account.
    /// </summary>
    [Fact]
    public async Task Update_CreatesTheViewsMapWithTheDrawingAndClickPoints()
    {
        var (msel, team) = await SeedDeployedMsel();
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var otherTeam = Guid.NewGuid();
        Factory.Siblings = Siblings(team.Id, otherTeam: otherTeam);

        var result = await ReadAsync<ViewModels.TopologyMapResult>(await Send(owner, HttpMethod.Post, Update(msel)));

        Assert.True(result.Created);
        Assert.Equal(MapId, result.PlayerMapId);
        Assert.Equal([team.Id, AdminTeamId], result.TeamIds);
        var create = Assert.Single(Factory.Siblings.Sent, x => x.Method == HttpMethod.Post && x.Path == $"vm/api/views/{ViewId}/map");
        Assert.Equal(Tokens.Header, create.Authorization);
        var body = Json(create.Body);
        Assert.Equal(MapId, body.GetProperty("id").GetGuid());
        Assert.Equal("Network Map", body.GetProperty("name").GetString());
        Assert.Equal(result.Map.ImageUrl, body.GetProperty("imageUrl").GetString());
        Assert.Equal([team.Id, AdminTeamId], body.GetProperty("teamIds").EnumerateArray().Select(x => x.GetGuid()));
        var point = Assert.Single(body.GetProperty("coordinates").EnumerateArray());
        Assert.Equal(["Domain Controller"], point.GetProperty("urls").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(result.Map.ClickPoints[0].XPosition, point.GetProperty("xPosition").GetDouble());
        Assert.Equal(result.Map.ClickPoints[0].YPosition, point.GetProperty("yPosition").GetDouble());
        Assert.Equal(result.Map.ClickPoints[0].Radius, point.GetProperty("radius").GetDouble());
        Assert.DoesNotContain(Factory.Siblings.Sent, x => x.Method == HttpMethod.Put);
        Assert.Equal($"Bearer {owner.Id}", Assert.Single(Factory.Siblings.Sent, x => x.Path.StartsWith("caster/")).Authorization);
    }

    /// <summary>The map an earlier update made is replaced, not added to.</summary>
    [Fact]
    public async Task Update_ReplacesTheMapAnEarlierUpdateMade()
    {
        var (msel, team) = await SeedDeployedMsel();
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        Factory.Siblings = Siblings(team.Id, mapExists: true);

        var result = await ReadAsync<ViewModels.TopologyMapResult>(await Send(owner, HttpMethod.Post, Update(msel)));

        Assert.False(result.Created);
        var update = Assert.Single(Factory.Siblings.Sent, x => x.Method == HttpMethod.Put);
        Assert.Equal($"vm/api/views/maps/{MapId}", update.Path);
        Assert.Equal(MapId, Json(update.Body).GetProperty("id").GetGuid());
        Assert.DoesNotContain(Factory.Siblings.Sent, x => x.Method == HttpMethod.Post && x.Path.StartsWith("vm/"));
    }

    /// <summary>
    /// A view with no Network Map application gets one, on each of the map's teams, and the MSEL gets the
    /// matching application row so a later push makes it too.
    /// </summary>
    [Fact]
    public async Task Update_AddsTheNetworkMapApplicationToTheViewAndTheMsel()
    {
        var (msel, team) = await SeedDeployedMsel();
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        Factory.Siblings = Siblings(team.Id);

        var result = await ReadAsync<ViewModels.TopologyMapResult>(await Send(owner, HttpMethod.Post, Update(msel)));

        Assert.True(result.ApplicationAdded);
        Assert.Equal([team.Id, AdminTeamId], result.ApplicationTeamIds);
        var application = Json(Assert.Single(Factory.Siblings.Sent, x => x.Method == HttpMethod.Post && x.Path == $"player/api/views/{ViewId}/applications").Body);
        Assert.Equal("Network Map", application.GetProperty("name").GetString());
        Assert.Equal($"http://vm.test/views/{ViewId}/map{{theme}}", application.GetProperty("url").GetString());
        Assert.True(application.GetProperty("embeddable").GetBoolean());
        // Player's own map icon, as the Virtual Machines application has its VM icon
        Assert.EndsWith("/assets/img/SP_Icon_Map.png", application.GetProperty("icon").GetString());
        foreach (var teamId in new[] { team.Id, AdminTeamId })
        {
            var instance = Json(Assert.Single(Factory.Siblings.Sent, x => x.Method == HttpMethod.Post && x.Path == $"player/api/teams/{teamId}/application-instances").Body);
            Assert.Equal(ApplicationId, instance.GetProperty("applicationId").GetGuid());
        }

        await using var db = NewContext();
        var row = await db.PlayerApplications.Include(a => a.PlayerApplicationTeams).SingleAsync(a => a.MselId == msel.Id, Ct);
        Assert.Equal("Network Map", row.Name);
        Assert.Equal(TopologyMapPublisher.ApplicationUrl, row.Url);
        Assert.Equal(TopologyMapPublisher.ApplicationIcon, row.Icon);
        Assert.Equal([team.Id], row.PlayerApplicationTeams.Select(t => t.TeamId));
    }

    /// <summary>One Network Map per view: an update finds the one there and adds neither a second
    /// application nor a second instance, in Player or in the MSEL.</summary>
    [Fact]
    public async Task Update_WhenTheViewHasTheApplication_AddsNoSecond()
    {
        var (msel, team) = await SeedDeployedMsel();
        var existing = TestData.PlayerApplication(msel.Id);
        existing.Name = "Range map";
        existing.Url = "{vmUrl}/views/{playerViewId}/map{theme}";
        await Seed(existing);
        await Seed(TestData.PlayerApplicationTeam(existing.Id, team.Id));
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        Factory.Siblings = Siblings(team.Id, applicationExists: true);

        var result = await ReadAsync<ViewModels.TopologyMapResult>(await Send(owner, HttpMethod.Post, Update(msel)));

        Assert.False(result.ApplicationAdded);
        Assert.Empty(result.ApplicationTeamIds);
        Assert.DoesNotContain(Factory.Siblings.Sent, x => x.Method == HttpMethod.Post && x.Path.StartsWith("player/"));
        // a map application made before it had an icon gets the map icon
        var icon = Json(Assert.Single(Factory.Siblings.Sent, x => x.Method == HttpMethod.Put && x.Path == $"player/api/applications/{ApplicationId}").Body);
        Assert.EndsWith("/assets/img/SP_Icon_Map.png", icon.GetProperty("icon").GetString());
        await using var db = NewContext();
        Assert.Equal(1, await db.PlayerApplications.CountAsync(a => a.MselId == msel.Id, Ct));
        Assert.Equal(1, await db.PlayerApplicationTeams.CountAsync(t => t.PlayerApplicationId == existing.Id, Ct));
    }

    [Fact]
    public async Task Update_OfAWorkspaceWithNoMachines_Is409AndWritesNothing()
    {
        var (msel, team) = await SeedDeployedMsel();
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        Factory.Siblings = Siblings(team.Id, topology: """{"nodes":[{"id":"net","kind":"Network","label":"lan"}],"edges":[],"layout":{}}""");

        var problem = await AssertJsonError(HttpStatusCode.Conflict, await Send(owner, HttpMethod.Post, Update(msel)));

        Assert.Contains("no machines", problem.Title);
        Assert.DoesNotContain(Factory.Siblings.Sent, x => x.Path.StartsWith("vm/") || x.Path.StartsWith("player/"));
    }

    [Fact]
    public async Task Update_OfAMselWithNoPlayerView_Is409()
    {
        var (msel, _) = await SeedDeployedMsel(hasView: false);
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        Factory.Siblings = new SiblingApiHandler();

        var problem = await AssertJsonError(HttpStatusCode.Conflict, await Send(owner, HttpMethod.Post, Update(msel)));

        Assert.Contains("no Player view", problem.Title);
        Assert.Empty(Factory.Siblings.Sent);
    }

    [Fact]
    public async Task Update_ForAnEditor_Is403AndSendsNothing()
    {
        var (msel, team) = await SeedDeployedMsel();
        var editor = await Actor().OnMsel(msel, MselRole.Editor).WithSystemPermissions(SystemPermission.ViewMsels, SystemPermission.EditMsels).SeedAsync();
        Factory.Siblings = Siblings(team.Id);

        await AssertStatus(HttpStatusCode.Forbidden, await Send(editor, HttpMethod.Post, Update(msel)));
        Assert.Empty(Factory.Siblings.Sent);
    }

    /// <summary>A refusal from the VM API comes back as the failure, naming what it refused.</summary>
    [Fact]
    public async Task Update_WhenTheVmApiRefusesTheMap_Fails()
    {
        var (msel, team) = await SeedDeployedMsel();
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        Factory.Siblings = Siblings(team.Id, first: x => x.Answers($"POST vm/api/views/{ViewId}/map", HttpStatusCode.Forbidden));

        var response = await Send(owner, HttpMethod.Post, Update(msel));

        Assert.False(response.IsSuccessStatusCode);
        Assert.Contains("refused to create the map", await response.Content.ReadAsStringAsync(Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static string Preview(MselEntity msel) => $"api/msels/{msel.Id}/topology-map/preview";

    private static string Update(MselEntity msel) => $"api/msels/{msel.Id}/topology-map";

    /// <summary>Sends as <paramref name="actor"/>, with their id as the bearer token Caster is handed.</summary>
    private async Task<HttpResponseMessage> Send(TestActor actor, HttpMethod method, string uri)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", actor.Id.ToString());
        return await Client(actor).SendAsync(request, Ct);
    }

    private async Task<(MselEntity Msel, TeamEntity Team)> SeedDeployedMsel(
        bool useCaster = true, bool hasWorkspace = true, bool hasView = true)
    {
        var msel = TestData.Msel(status: MselItemStatus.Deployed);
        msel.UsePlayer = true;
        msel.UseCaster = useCaster;
        msel.PlayerViewId = hasView ? ViewId : null;
        msel.CasterWorkspaceId = hasWorkspace ? WorkspaceId : null;
        await Seed(msel);
        var team = TestData.Team(msel.Id);
        await Seed(team);
        return (msel, team);
    }

    /// <summary>A topology of a domain controller the team can open and a router with no Player record, on one network.</summary>
    private static string Topology(Guid teamId) => $$"""
        {
          "workspaceId": "{{WorkspaceId}}",
          "source": "State",
          "nodes": [
            { "id": "vm.dc", "kind": "Machine", "label": "dc-01", "console": { "teamIds": ["{{teamId}}"], "name": "Domain Controller", "vmId": "{{Guid.NewGuid()}}" } },
            { "id": "vm.router", "kind": "Machine", "label": "router", "console": null },
            { "id": "net.lan", "kind": "Network", "label": "lan" }
          ],
          "edges": [ { "from": "vm.dc", "to": "net.lan", "nic": 0 }, { "from": "vm.router", "to": "net.lan", "nic": 0 } ],
          "layout": { "vm.dc": { "treeX": 0, "treeY": 100, "role": "DomainController" } }
        }
        """;

    /// <summary>
    /// Caster, the identity provider, Player's teams and applications and the VM API's map routes.
    /// <paramref name="first"/> is applied before the general rules, which are matched in order.
    /// </summary>
    private static SiblingApiHandler Siblings(
        Guid teamId,
        Guid? otherTeam = null,
        bool mapExists = false,
        bool applicationExists = false,
        string topology = null,
        Func<SiblingApiHandler, SiblingApiHandler> first = null)
    {
        var handler = first is null ? new SiblingApiHandler() : first(new SiblingApiHandler());
        var teams = new List<string> { $$"""{"id":"{{teamId}}","name":"Blue","viewId":"{{ViewId}}"}""", $$"""{"id":"{{AdminTeamId}}","name":"Admin","viewId":"{{ViewId}}"}""" };
        if (otherTeam != null)
            teams.Add($$"""{"id":"{{otherTeam}}","name":"Red","viewId":"{{ViewId}}"}""");
        var applications = applicationExists
            ? $$"""[{"id":"{{ApplicationId}}","name":"Range map","viewId":"{{ViewId}}","url":"http://vm.test/views/{{ViewId}}/map{theme}"}]"""
            : "[]";
        var instances = applicationExists
            ? $$"""[{"id":"{{Guid.NewGuid()}}","applicationId":"{{ApplicationId}}","displayOrder":1}]"""
            : "[]";

        handler
            .AnswersJson("realms/crucible/.well-known/openid-configuration", """
                {
                  "issuer": "http://localhost:8080/realms/crucible",
                  "authorization_endpoint": "http://localhost:8080/realms/crucible/protocol/openid-connect/auth",
                  "token_endpoint": "http://localhost:8080/realms/crucible/protocol/openid-connect/token",
                  "jwks_uri": "http://localhost:8080/realms/crucible/protocol/openid-connect/certs",
                  "response_types_supported": ["code"],
                  "subject_types_supported": ["public"],
                  "id_token_signing_alg_values_supported": ["RS256"]
                }
                """)
            .AnswersJson("realms/crucible/protocol/openid-connect/certs", """{"keys":[]}""")
            .AnswersJson("realms/crucible/protocol/openid-connect/token", """{"access_token":"abc123","token_type":"Bearer","expires_in":300}""")
            .AnswersJson($"GET caster/api/workspaces/{WorkspaceId}/topology", topology ?? Topology(teamId))
            .AnswersJson($"GET player/api/views/{ViewId}/teams", "[" + string.Join(",", teams) + "]")
            .AnswersJson($"GET player/api/views/{ViewId}/applications", applications)
            .AnswersJson($"POST player/api/views/{ViewId}/applications",
                $$"""{"id":"{{ApplicationId}}","name":"Network Map","viewId":"{{ViewId}}"}""", HttpStatusCode.Created)
            .AnswersJson($"PUT player/api/applications/{ApplicationId}",
                $$"""{"id":"{{ApplicationId}}","name":"Range map","viewId":"{{ViewId}}","icon":"/assets/img/SP_Icon_Map.png"}""")
            .AnswersJson($"GET player/api/teams/{teamId}/application-instances", instances)
            .AnswersJson($"GET player/api/teams/{AdminTeamId}/application-instances", instances)
            .AnswersJson("POST player/api/teams/*/application-instances",
                $$"""{"id":"{{Guid.NewGuid()}}","applicationId":"{{ApplicationId}}"}""", HttpStatusCode.Created)
            .AnswersJson($"POST vm/api/views/{ViewId}/map", $$"""{"id":"{{MapId}}"}""", HttpStatusCode.Created)
            .AnswersJson($"PUT vm/api/views/maps/{MapId}", $$"""{"id":"{{MapId}}"}""");
        if (mapExists)
            handler.AnswersJson($"GET vm/api/views/maps/{MapId}", $$"""{"id":"{{MapId}}"}""");
        else
            handler.Answers($"GET vm/api/views/maps/{MapId}", HttpStatusCode.NotFound);
        return handler;
    }

    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement;
}

/// <summary>The pure parts of <c>TopologyMapPublisher</c>: the map's id and how the view's Network Map
/// application is recognised.</summary>
public class TopologyMapPublisherTests
{
    [Fact]
    public void MapId_IsTheSameForAViewAndDiffersBetweenViews()
    {
        var view = Guid.NewGuid();

        Assert.Equal(TopologyMapPublisher.MapId(view), TopologyMapPublisher.MapId(view));
        Assert.NotEqual(TopologyMapPublisher.MapId(view), TopologyMapPublisher.MapId(Guid.NewGuid()));
        Assert.NotEqual(view, TopologyMapPublisher.MapId(view));
    }

    [Theory]
    [InlineData("Network Map", "https://elsewhere.test/", true)]
    [InlineData("Map", "{vmUrl}/views/{playerViewId}/map{theme}", true)]
    [InlineData("Map", "https://vm.test/views/6f1f4d1e-8a59-4f0e-9a51-0d2f43f2d5aa/map", true)]
    [InlineData("Map", "https://vm.test/views/6f1f4d1e-8a59-4f0e-9a51-0d2f43f2d5aa/map?theme=dark-theme", true)]
    [InlineData("VM", "{vmUrl}/views/{playerViewId}{theme}", false)]
    [InlineData("VM", "https://vm.test/views/6f1f4d1e-8a59-4f0e-9a51-0d2f43f2d5aa/mapping", false)]
    [InlineData("VM", null, false)]
    public void IsNetworkMapApplication_KnowsTheViewsMapApplication(string name, string url, bool expected) =>
        Assert.Equal(expected, TopologyMapPublisher.IsNetworkMapApplication(name, url));
}
