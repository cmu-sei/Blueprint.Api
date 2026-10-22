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
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Infrastructure.Extensions;

/// <summary>
/// <c>IntegrationMatrixExtensions</c> - the space and rooms a push creates on a Synapse homeserver, the
/// accounts it finds or creates for their members, and the join and pull that follow.
/// </summary>
/// <remarks>
/// One <see cref="SiblingApiHandler"/> answers both Synapse and the Keycloak admin API. The error bodies are
/// the ones Synapse sends. Room and user IDs reach Synapse escaped, so the routes spell them that way.
/// </remarks>
public class IntegrationMatrixExtensionsTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    private const string AuthProvider = "oidc-keycloak";
    private const string Bot = "@bpbot:crucible.test";

    [Fact]
    public async Task GetMatrixClient_SignsInAsTheBot()
    {
        var handler = new SiblingApiHandler().Answers("DELETE matrix/_synapse/admin/v2/rooms/*", new { delete_id = "d" });
        var client = IntegrationMatrixExtensions.GetMatrixClient(handler.AsFactory(), new ClientOptions
        {
            MatrixUrl = "http://matrix.example/matrix",
            MatrixAccessToken = "bot-token"
        });

        await IntegrationMatrixExtensions.PullFromMatrixAsync(["!room:crucible.test"], client, Ct);

        var sent = Assert.Single(handler.Sent);
        Assert.Equal($"matrix/_synapse/admin/v2/rooms/{Esc("!room:crucible.test")}", sent.Path);
        Assert.Equal("Bearer bot-token", sent.Authorization);
    }

    [Theory]
    [InlineData("https://matrix.example", "token", "oidc-keycloak", true)]
    [InlineData("", "token", "oidc-keycloak", false)]
    [InlineData("https://matrix.example", "", "oidc-keycloak", false)]
    [InlineData("https://matrix.example", "token", "", false)]
    public void IsConfigured_NeedsTheUrlTokenAndAuthProvider(string url, string token, string authProvider, bool configured)
    {
        var options = new ClientOptions { MatrixUrl = url, MatrixAccessToken = token, MatrixOidcAuthProvider = authProvider };

        Assert.Equal(configured, IntegrationMatrixExtensions.IsConfigured(options));
    }

    /// <remarks>
    /// MSEL role holders (here an evaluator) are in the space and the general room but in no team room.
    /// </remarks>
    [Fact]
    public async Task Push_CreatesASpaceWithAGeneralRoomAndARoomForEachTeam()
    {
        var (msel, team, player) = await SeedMselAsync();
        var evaluator = await SeedUserAsync();
        await Seed(new UserMselRoleEntity(evaluator, msel.Id, MselRole.Evaluator));
        var handler = Synapse()
            .Linked(player, "@jsmith:crucible.test")
            .Linked(evaluator, "@evaluator:crucible.test")
            .CreatesRooms("!space:crucible.test", "!general:crucible.test", "!blue:crucible.test");

        await PushAsync(msel.Id, handler);

        var creates = handler.Sent.Where(s => s.Path == "_matrix/client/v3/createRoom").Select(s => Body(s.Body)).ToList();
        Assert.Equal(["Cyber Flag 27", "Cyber Flag 27 - General", "Cyber Flag 27 - Blue Team"], creates.Select(c => c["name"].GetString()));
        Assert.Equal("m.space", creates[0]["creation_content"].GetProperty("type").GetString());
        Assert.All(creates.Skip(1), c => Assert.False(c.ContainsKey("creation_content")));
        Assert.All(creates, c => Assert.Equal("private_chat", c["preset"].GetString()));
        Assert.All(creates, c => Assert.Equal(50, c["power_level_content_override"].GetProperty("invite").GetInt32()));

        Assert.Equal([
            $"_matrix/client/v3/rooms/{Esc("!space:crucible.test")}/state/m.space.child/{Esc("!general:crucible.test")}",
            $"_matrix/client/v3/rooms/{Esc("!space:crucible.test")}/state/m.space.child/{Esc("!blue:crucible.test")}"
        ], handler.Sent.Where(s => s.Method == HttpMethod.Put && s.Path.Contains("m.space.child")).Select(s => s.Path));
        Assert.All(handler.Sent.Where(s => s.Path.Contains("m.space.child")),
            s => Assert.Equal(["crucible.test"], Body(s.Body)["via"].EnumerateArray().Select(v => v.GetString())));

        var everyone = new[] { "@evaluator:crucible.test", "@jsmith:crucible.test" };
        Assert.Equal(everyone, Joined(handler, "!space:crucible.test"));
        Assert.Equal(everyone, Joined(handler, "!general:crucible.test"));
        Assert.Equal(["@jsmith:crucible.test"], Joined(handler, "!blue:crucible.test"));

        await using var db = NewContext();
        var saved = await db.Msels.SingleAsync(m => m.Id == msel.Id, Ct);
        Assert.Equal("!space:crucible.test", saved.MatrixSpaceId);
        Assert.Equal("!general:crucible.test", saved.MatrixRoomId);
        Assert.Equal("!blue:crucible.test", (await db.Teams.SingleAsync(t => t.Id == team.Id, Ct)).MatrixRoomId);
    }

    /// <remarks>A user linked to their Keycloak subject has signed in, so Keycloak is not asked.</remarks>
    [Fact]
    public async Task Push_FindsAUserWhoHasSignedInByTheirKeycloakSubject()
    {
        var (msel, _, player) = await SeedMselAsync();
        var handler = Synapse()
            .Linked(player, "@jane.smith:crucible.test")
            .CreatesRooms("!space:crucible.test", "!general:crucible.test", "!blue:crucible.test");

        await PushAsync(msel.Id, handler);

        Assert.DoesNotContain(handler.Sent, s => s.Path.StartsWith("admin/realms/"));
        Assert.DoesNotContain(handler.Sent, s => s.Path.StartsWith("_synapse/admin/v2/users/"));
        Assert.Equal(["@jane.smith:crucible.test"], Joined(handler, "!blue:crucible.test"));
    }

    /// <remarks>
    /// The account is linked to the user's Keycloak subject, so their first Keycloak sign-in to Matrix
    /// lands in it.
    /// </remarks>
    [Fact]
    public async Task Push_CreatesALinkedAccountForAUserNotInMatrixYet()
    {
        var (msel, _, player) = await SeedMselAsync();
        var handler = Synapse()
            .KeycloakUser(player, "jsmith", "jsmith@example.com", "Jane", "Smith")
            .NoAccounts()
            .CreatesRooms("!space:crucible.test", "!general:crucible.test", "!blue:crucible.test");

        await PushAsync(msel.Id, handler);

        var create = handler.Sent.Single(s => s.Method == HttpMethod.Put && s.Path.StartsWith("_synapse/admin/v2/users/"));
        Assert.Equal($"_synapse/admin/v2/users/{Esc("@jsmith:crucible.test")}", create.Path);
        var account = Body(create.Body);
        Assert.Equal("Jane Smith", account["displayname"].GetString());
        var link = Assert.Single(account["external_ids"].EnumerateArray());
        Assert.Equal(AuthProvider, link.GetProperty("auth_provider").GetString());
        Assert.Equal(player.ToString(), link.GetProperty("external_id").GetString());
        var email = Assert.Single(account["threepids"].EnumerateArray());
        Assert.Equal("jsmith@example.com", email.GetProperty("address").GetString());
        Assert.Equal(["@jsmith:crucible.test"], Joined(handler, "!blue:crucible.test"));
    }

    /// <remarks>
    /// An account by that name that is not linked to the user signed up some other way. Linking it would
    /// hand someone else's account to this user.
    /// </remarks>
    [Fact]
    public async Task Push_LeavesOutAUserWhoseNameIsSomeoneElsesAccount()
    {
        var (msel, _, player) = await SeedMselAsync();
        var handler = Synapse()
            .KeycloakUser(player, "jsmith")
            .Answers($"GET _synapse/admin/v2/users/{Esc("@jsmith:crucible.test")}", new { name = "@jsmith:crucible.test", external_ids = Array.Empty<object>() })
            .NoAccounts()
            .CreatesRooms("!space:crucible.test", "!general:crucible.test", "!blue:crucible.test");

        await PushAsync(msel.Id, handler);

        Assert.DoesNotContain(handler.Sent, s => s.Method == HttpMethod.Put && s.Path.StartsWith("_synapse/admin/v2/users/"));
        Assert.DoesNotContain(handler.Sent, s => s.Path.StartsWith("_synapse/admin/v1/join/"));
    }

    [Theory]
    [InlineData("jsmith@example.com")]
    [InlineData("Jane Smith")]
    public async Task Push_LeavesOutAUsernameThatIsNotAMatrixName(string username)
    {
        var (msel, _, player) = await SeedMselAsync();
        var handler = Synapse()
            .KeycloakUser(player, username)
            .NoAccounts()
            .CreatesRooms("!space:crucible.test", "!general:crucible.test", "!blue:crucible.test");

        await PushAsync(msel.Id, handler);

        Assert.DoesNotContain(handler.Sent, s => s.Path.StartsWith("_synapse/admin/v2/users/"));
        Assert.DoesNotContain(handler.Sent, s => s.Path.StartsWith("_synapse/admin/v1/join/"));
    }

    [Fact]
    public async Task Push_LeavesOutAUserKeycloakDoesNotKnow()
    {
        var (msel, _, player) = await SeedMselAsync();
        var handler = Synapse()
            .Answers($"admin/realms/crucible/users/{player}", HttpStatusCode.NotFound)
            .NoAccounts()
            .CreatesRooms("!space:crucible.test", "!general:crucible.test", "!blue:crucible.test");

        await PushAsync(msel.Id, handler);

        Assert.DoesNotContain(handler.Sent, s => s.Path.StartsWith("_synapse/admin/v2/users/"));
        Assert.DoesNotContain(handler.Sent, s => s.Path.StartsWith("_synapse/admin/v1/join/"));
    }

    /// <remarks>
    /// A push that failed after saving some rooms is finished by pushing again: the saved rooms are not made
    /// again, but they are still put in the space and their members joined. Synapse refuses to join a
    /// member again, so a member is not asked to join.
    /// </remarks>
    [Fact]
    public async Task Push_FinishesAPartialPushWithoutRejoiningMembers()
    {
        var (msel, _, player) = await SeedMselAsync();
        await Db.Msels.Where(m => m.Id == msel.Id).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.MatrixSpaceId, "!space:crucible.test")
            .SetProperty(m => m.MatrixRoomId, "!general:crucible.test"), Ct);
        var handler = new SiblingApiHandler()
            .Answers("GET _matrix/client/v3/account/whoami", new { user_id = Bot })
            .Answers($"GET _synapse/admin/v1/rooms/{Esc("!space:crucible.test")}/members", new { members = new[] { Bot, "@jsmith:crucible.test" } })
            .Answers("GET _synapse/admin/v1/rooms/*/members", new { members = new[] { Bot } })
            .Answers("POST _synapse/admin/v1/join/*", new { room_id = "joined" })
            .Answers("PUT _matrix/client/v3/rooms/*", new { event_id = "$child" })
            .Linked(player, "@jsmith:crucible.test")
            .CreatesRooms("!blue:crucible.test");

        await PushAsync(msel.Id, handler);

        var creates = handler.Sent.Where(s => s.Path == "_matrix/client/v3/createRoom").Select(s => Body(s.Body)["name"].GetString());
        Assert.Equal(["Cyber Flag 27 - Blue Team"], creates);
        Assert.Empty(Joined(handler, "!space:crucible.test"));
        Assert.Equal(["@jsmith:crucible.test"], Joined(handler, "!general:crucible.test"));
        Assert.Equal(["@jsmith:crucible.test"], Joined(handler, "!blue:crucible.test"));
        Assert.Equal(2, handler.Sent.Count(s => s.Path.Contains("m.space.child")));
    }

    [Fact]
    public async Task Pull_PurgesEachRoomAndSwallowsARefusal()
    {
        var handler = new SiblingApiHandler()
            .AnswersJson("DELETE _synapse/admin/v2/rooms/*", """{"errcode":"M_FORBIDDEN","error":"You are not a server admin"}""", HttpStatusCode.Forbidden, once: true)
            .Answers("DELETE _synapse/admin/v2/rooms/*", new { delete_id = "d" });

        await IntegrationMatrixExtensions.PullFromMatrixAsync(["!blue:crucible.test", null, "!space:crucible.test"], Client(handler), Ct);

        Assert.Equal(
            [$"_synapse/admin/v2/rooms/{Esc("!blue:crucible.test")}", $"_synapse/admin/v2/rooms/{Esc("!space:crucible.test")}"],
            handler.Sent.Select(s => s.Path));
        Assert.All(handler.Sent, s => Assert.True(Body(s.Body)["purge"].GetBoolean()));
    }

    [Fact]
    public async Task AddUserToTeam_JoinsTheSpaceTheGeneralRoomAndTheTeamRoom()
    {
        var (msel, team, _) = await SeedMselAsync();
        var joiner = await SeedUserAsync();
        await Db.Msels.Where(m => m.Id == msel.Id).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.MatrixSpaceId, "!space:crucible.test")
            .SetProperty(m => m.MatrixRoomId, "!general:crucible.test"), Ct);
        await Db.Teams.Where(t => t.Id == team.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.MatrixRoomId, "!blue:crucible.test"), Ct);
        var handler = Synapse().Linked(joiner, "@joiner:crucible.test");

        await IntegrationMatrixExtensions.AddUserToTeamAsync(joiner, team.Id, Client(handler), KeycloakClient(handler), AuthProvider, NewContext(), Ct);

        var joins = handler.Sent.Where(s => s.Path.StartsWith("_synapse/admin/v1/join/")).ToList();
        Assert.Equal(
            new[] { "!space:crucible.test", "!general:crucible.test", "!blue:crucible.test" }.Select(r => $"_synapse/admin/v1/join/{Esc(r)}"),
            joins.Select(s => s.Path));
        Assert.All(joins, s => Assert.Equal("@joiner:crucible.test", Body(s.Body)["user_id"].GetString()));
    }

    [Fact]
    public async Task AddUserToTeam_RefusesAUserWithNoMatrixAccount()
    {
        var (_, team, _) = await SeedMselAsync();
        var joiner = await SeedUserAsync();
        var handler = Synapse().Answers($"admin/realms/crucible/users/{joiner}", HttpStatusCode.NotFound).NoAccounts();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IntegrationMatrixExtensions.AddUserToTeamAsync(joiner, team.Id, Client(handler), KeycloakClient(handler), AuthProvider, NewContext(), Ct));
    }

    // ---------------------------------------------------------------------------------------------

    // a homeserver where the bot is in every room alone and every join and space link succeeds
    private static SiblingApiHandler Synapse() => new SiblingApiHandler()
        .Answers("GET _matrix/client/v3/account/whoami", new { user_id = Bot })
        .Answers("GET _synapse/admin/v1/rooms/*/members", new { members = new[] { Bot } })
        .Answers("POST _synapse/admin/v1/join/*", new { room_id = "joined" })
        .Answers("PUT _matrix/client/v3/rooms/*", new { event_id = "$child" });

    private async Task<(MselEntity Msel, TeamEntity Team, Guid Player)> SeedMselAsync()
    {
        var msel = TestData.Msel();
        msel.Name = "Cyber Flag 27";
        msel.UseMatrix = true;
        var team = TestData.Team(msel.Id);
        team.Name = "Blue Team";
        var player = await SeedUserAsync();
        await Seed(msel, team, TestData.TeamUser(player, team.Id));
        return (msel, team, player);
    }

    private async Task<Guid> SeedUserAsync()
    {
        var user = new UserEntity { Id = Guid.NewGuid(), Name = "Seeded User" };
        await Seed(user);
        return user.Id;
    }

    // a fresh context, so the push sees room IDs a test set with ExecuteUpdateAsync
    private async Task PushAsync(Guid mselId, SiblingApiHandler handler)
    {
        await using var db = NewContext();
        var msel = await db.Msels
            .Include(m => m.Teams).ThenInclude(t => t.TeamUsers)
            .Include(m => m.UserMselRoles)
            .AsSplitQuery()
            .SingleAsync(m => m.Id == mselId, Ct);

        await IntegrationMatrixExtensions.PushToMatrixAsync(msel, Client(handler), KeycloakClient(handler), AuthProvider, db, Ct);
    }

    // the users asked to join a room, sorted
    private static string[] Joined(SiblingApiHandler handler, string roomId) =>
        handler.Sent
            .Where(s => s.Path == $"_synapse/admin/v1/join/{Esc(roomId)}")
            .Select(s => Body(s.Body)["user_id"].GetString())
            .OrderBy(u => u)
            .ToArray();

    private static HttpClient Client(SiblingApiHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://sibling.example/") };

    private static HttpClient KeycloakClient(SiblingApiHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://sibling.example/admin/realms/crucible/") };

    private static string Esc(string id) => Uri.EscapeDataString(id);

    private static Dictionary<string, JsonElement> Body(string body) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
}

internal static class SynapseArrangements
{
    private const string NotFound = """{"errcode":"M_NOT_FOUND","error":"User not found"}""";

    // the user has signed in to Matrix through Keycloak, so their account is linked to their subject
    public static SiblingApiHandler Linked(this SiblingApiHandler handler, Guid userId, string matrixUserId) =>
        handler.Answers($"GET _synapse/admin/v1/auth_providers/oidc-keycloak/users/{userId}", new { user_id = matrixUserId });

    // no other user has signed in, no account by any other name exists, and making one succeeds
    public static SiblingApiHandler NoAccounts(this SiblingApiHandler handler) => handler
        .AnswersJson("GET _synapse/admin/v1/auth_providers/oidc-keycloak/users/*", NotFound, HttpStatusCode.NotFound)
        .AnswersJson("GET _synapse/admin/v2/users/*", NotFound, HttpStatusCode.NotFound)
        .Answers("PUT _synapse/admin/v2/users/*", new { name = "created" });

    // createRoom answers these room IDs in order
    public static SiblingApiHandler CreatesRooms(this SiblingApiHandler handler, params string[] roomIds)
    {
        foreach (var roomId in roomIds)
            handler.AnswersJson("POST _matrix/client/v3/createRoom", JsonSerializer.Serialize(new { room_id = roomId }), once: true);
        return handler;
    }
}
