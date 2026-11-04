// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary>
/// The push's network map step: after Caster's apply, a MSEL using Player and Caster gets its workspace's
/// topology drawn as the view's VM map, and a Network Map application. A map that cannot be made is a
/// warning, not a failed push.
/// </summary>
/// <remarks>
/// Driven through <see cref="IntegrationServiceHarness"/> with its VM API url set, as the Caster push tests
/// are without it.
/// </remarks>
public class IntegrationServiceTopologyMapTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    private static readonly Guid StubId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid DirectoryId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid WorkspaceId = Guid.Parse("cccccccc-0000-0000-0000-000000000002");
    private static readonly Guid RunId = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid AdminTeamId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ApplicationId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    /// <summary>The map is made after the apply, from what the apply built, and the push still deploys.</summary>
    [Fact]
    public async Task Push_MakesTheNetworkMapAfterTheApply()
    {
        var msel = await SeedCasterMsel();
        var harness = PushHarness();

        await harness.PushAsync(msel.Id);
        var deployed = await harness.WaitForDeployment(msel.Id, Ct);

        Assert.Null(deployed.IntegrationStatus);
        Assert.Equal(["Caster - applying", "Pushing Network Map to Player"], Statuses(harness, msel.Id).TakeLast(2));
        var paths = harness.Handler.Sent.Select(x => $"{x.Method} {x.Path}").ToList();
        var apply = paths.IndexOf($"POST caster/api/runs/{RunId}/actions/apply");
        var topology = paths.IndexOf($"GET caster/api/workspaces/{WorkspaceId}/topology");
        var map = paths.IndexOf($"POST vm/api/views/{deployed.PlayerViewId}/map");
        Assert.True(apply >= 0 && apply < topology && topology < map, string.Join(", ", paths));

        var body = JsonDocument.Parse(harness.Handler.Sent[map].Body).RootElement;
        Assert.Equal(TopologyMapPublisher.MapId((Guid)deployed.PlayerViewId), body.GetProperty("id").GetGuid());
        Assert.Equal(["Web"], body.GetProperty("coordinates").EnumerateArray().Select(c => c.GetProperty("urls")[0].GetString()));
        Assert.Equal("Bearer abc123", harness.Handler.Sent[map].Authorization);
    }

    /// <summary>The push adds the Network Map application to the view and to the MSEL.</summary>
    [Fact]
    public async Task Push_AddsTheNetworkMapApplication()
    {
        var msel = await SeedCasterMsel();
        var harness = PushHarness();

        await harness.PushAsync(msel.Id);
        var deployed = await harness.WaitForDeployment(msel.Id, Ct);

        var application = JsonDocument.Parse(Assert.Single(harness.Handler.Sent,
            x => x.Method == HttpMethod.Post && x.Path == $"player/api/views/{deployed.PlayerViewId}/applications").Body).RootElement;
        Assert.Equal($"http://vm.test/views/{deployed.PlayerViewId}/map{{theme}}", application.GetProperty("url").GetString());
        Assert.Single(harness.Handler.Sent, x => x.Method == HttpMethod.Post && x.Path == $"player/api/teams/{AdminTeamId}/application-instances");
        await using var db = NewContext();
        Assert.Equal(TopologyMapPublisher.ApplicationUrl, (await db.PlayerApplications.SingleAsync(a => a.MselId == msel.Id, Ct)).Url);
    }

    /// <summary>A map Player refuses leaves a warning and a deployed MSEL.</summary>
    [Fact]
    public async Task Push_WhenTheMapCannotBeMade_WarnsAndStillDeploys()
    {
        var msel = await SeedCasterMsel();
        var harness = PushHarness(x => x.Answers("POST vm/api/views/*/map", HttpStatusCode.InternalServerError));

        await harness.PushAsync(msel.Id);
        var deployed = await harness.WaitForDeployment(msel.Id, Ct);

        Assert.Equal(MselItemStatus.Deployed, deployed.Status);
        Assert.Null(deployed.IntegrationStatus);
        var warning = Statuses(harness, msel.Id).Last();
        Assert.StartsWith("Network map was not made in Player - ", warning);
        Assert.Contains("500", warning);
    }

    /// <summary>A workspace with no machines gets no map, and the push says nothing about one.</summary>
    [Fact]
    public async Task Push_OfAWorkspaceWithNoMachines_MakesNoMap()
    {
        var msel = await SeedCasterMsel();
        var harness = PushHarness(topology: """{"nodes":[{"id":"net","kind":"Network","label":"lan"}],"edges":[],"layout":{}}""");

        await harness.PushAsync(msel.Id);
        await harness.WaitForDeployment(msel.Id, Ct);

        Assert.DoesNotContain(harness.Handler.Sent, x => x.Path.StartsWith("vm/"));
        Assert.DoesNotContain("Pushing Network Map to Player", Statuses(harness, msel.Id));
    }

    /// <summary>With no VM API url there is nowhere to write a map, so Caster is not asked for the topology.</summary>
    [Fact]
    public async Task Push_WithNoVmApiUrl_MakesNoMap()
    {
        var msel = await SeedCasterMsel();
        var harness = PushHarness(withVmApi: false);

        await harness.PushAsync(msel.Id);
        await harness.WaitForDeployment(msel.Id, Ct);

        Assert.DoesNotContain(harness.Handler.Sent, x => x.Path.EndsWith("/topology") || x.Path.StartsWith("vm/"));
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private async Task<MselEntity> SeedCasterMsel()
    {
        var msel = TestData.Msel(status: MselItemStatus.Approved);
        msel.UsePlayer = true;
        msel.UseCaster = true;
        msel.CasterDirectoryId = DirectoryId;
        await Seed(msel);

        var team = TestData.Team(msel.Id);
        team.CasterTeamVariable = "blue";
        await Seed(team);

        return msel;
    }

    private const string WebTopology = """
        {
          "nodes": [
            { "id": "vm.web", "kind": "Machine", "label": "web-01", "console": { "teamIds": [], "teamsUnknown": true, "name": "Web" } },
            { "id": "net.dmz", "kind": "Network", "label": "dmz" }
          ],
          "edges": [ { "from": "vm.web", "to": "net.dmz", "nic": 0 } ],
          "layout": {}
        }
        """;

    /// <remarks>
    /// <paramref name="first"/> is applied before the general rules, because rules are matched in the order
    /// they were added. The network map's rules come before the push's, whose <c>player/api/teams/*</c>
    /// would otherwise answer the application instance reads.
    /// </remarks>
    private IntegrationServiceHarness PushHarness(
        Func<SiblingApiHandler, SiblingApiHandler> first = null, string topology = WebTopology, bool withVmApi = true)
    {
        var handler = first is null ? new SiblingApiHandler() : first(new SiblingApiHandler());
        handler
            .AnswersJson(IntegrationServiceHarness.DiscoveryPath, """
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
            .AnswersJson(IntegrationServiceHarness.JwksPath, """{"keys":[]}""")
            .AnswersJson(IntegrationServiceHarness.TokenPath, """{"access_token":"abc123","token_type":"Bearer","expires_in":300}""")
            // the network map
            .AnswersJson($"GET caster/api/workspaces/{WorkspaceId}/topology", topology)
            .Answers("GET vm/api/views/maps/*", HttpStatusCode.NotFound)
            .AnswersJson("POST vm/api/views/*/map", $$"""{"id":"{{StubId}}"}""", HttpStatusCode.Created)
            .AnswersJson("GET player/api/views/*/applications", "[]")
            .AnswersJson("POST player/api/views/*/applications", $$"""{"id":"{{ApplicationId}}","name":"Network Map"}""", HttpStatusCode.Created)
            .AnswersJson("GET player/api/teams/*/application-instances", "[]")
            .AnswersJson("POST player/api/teams/*/application-instances", $$"""{"id":"{{StubId}}","applicationId":"{{ApplicationId}}"}""", HttpStatusCode.Created)
            // the push, as IntegrationServiceCasterTests arranges it
            .AnswersJson("GET player/api/users", "[]")
            .AnswersJson("GET player/api/views/*/teams", $$"""[{"id":"{{AdminTeamId}}","name":"Admin"}]""")
            .AnswersJson("player/api/views/*/teams", $$"""{"id":"{{StubId}}","name":"t"}""", HttpStatusCode.Created)
            .AnswersJson("player/api/views", $$"""{"id":"{{StubId}}","name":"v","status":"Active"}""", HttpStatusCode.Created)
            .AnswersJson("player/api/teams/*", $$"""{"id":"{{StubId}}","name":"u"}""")
            .AnswersJson("POST player/api/users", $$"""{"id":"{{StubId}}","name":"u"}""", HttpStatusCode.Created)
            .AnswersJson("POST caster/api/workspaces",
                $$"""{"id":"{{WorkspaceId}}","name":"ws","directoryId":"{{DirectoryId}}"}""", HttpStatusCode.Created)
            .AnswersJson("POST caster/api/files",
                $$"""{"id":"{{StubId}}","name":"ws.auto.tfvars","directoryId":"{{DirectoryId}}","workspaceId":"{{WorkspaceId}}"}""",
                HttpStatusCode.Created)
            .AnswersJson("POST caster/api/runs", Run("Queued"), HttpStatusCode.Created)
            .AnswersJson($"GET caster/api/runs/{RunId}", Run("Planned"), once: true)
            .AnswersJson($"GET caster/api/runs/{RunId}", Run("Applied"))
            .AnswersJson($"POST caster/api/runs/{RunId}/actions/apply", "{}");

        return new IntegrationServiceHarness(Session, handler, withVmApi: withVmApi);
    }

    private static string Run(string status) =>
        $$"""{"id":"{{RunId}}","workspaceId":"{{WorkspaceId}}","isDestroy":false,"status":"{{status}}","createdAt":"2026-10-07T00:00:00Z"}""";

    private static List<string> Statuses(IntegrationServiceHarness harness, Guid mselId) =>
        [.. harness.Hub.ToGroup(mselId)
            .Where(x => x.Method == MainHubMethods.IntegrationStatusUpdated)
            .Select(x => (string)x.Arguments[1])];
}
