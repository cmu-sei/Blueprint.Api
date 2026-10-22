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
/// <c>IntegrationRocketChatExtensions</c> - the exercise and team rooms a push creates, the accounts it
/// finds or creates for their members, and the join and pull that follow.
/// </summary>
/// <remarks>
/// One <see cref="SiblingApiHandler"/> answers both Rocket.Chat and the Keycloak admin API; their routes do
/// not overlap. The error bodies are the ones Rocket.Chat 8.9 sends.
/// </remarks>
public class IntegrationRocketChatExtensionsTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    private const string UserNotFound = """{"success":false,"error":"User not found."}""";
    private const string DuplicateName = """{"success":false,"error":"A channel with name 'x' exists [error-duplicate-channel-name]","errorType":"error-duplicate-channel-name"}""";

    [Theory]
    [InlineData("Cyber Flag 27 (TTX)", "cyber-flag-27-ttx")]
    [InlineData("Blue_Team.2", "blue_team.2")]
    [InlineData("Équipe Bleue", "quipe-bleue")]
    [InlineData("***", "exercise")]
    public void RoomName_KeepsOnlyWhatRocketChatAccepts(string name, string roomName)
    {
        Assert.Equal(roomName, IntegrationRocketChatExtensions.RoomName(name));
    }

    [Fact]
    public async Task GetRocketChatClient_SignsInAsTheBot()
    {
        var handler = new SiblingApiHandler().Answers("chat/api/v1/groups.delete", new { success = true });
        var client = IntegrationRocketChatExtensions.GetRocketChatClient(handler.AsFactory(), new ClientOptions
        {
            RocketChatUrl = "http://rocketchat.example/chat",
            RocketChatUserId = "bot-id",
            RocketChatAuthToken = "bot-token"
        });

        await IntegrationRocketChatExtensions.PullFromRocketChatAsync(["room"], client, Ct);

        var sent = Assert.Single(handler.Sent);
        Assert.Equal("chat/api/v1/groups.delete", sent.Path);
        Assert.Equal("bot-id", sent.Headers["X-User-Id"]);
        Assert.Equal("bot-token", sent.Headers["X-Auth-Token"]);
    }

    [Fact]
    public void GetKeycloakAdminClient_AddressesTheRealmAdminApi()
    {
        var client = IntegrationKeycloakExtensions.GetKeycloakAdminClient(
            new SiblingApiHandler().AsFactory(), "https://keycloak.example/realms/crucible/", null);

        Assert.Equal("https://keycloak.example/admin/realms/crucible/", client.BaseAddress.ToString());
    }

    /// <remarks>
    /// MSEL role holders (here an evaluator) are in the exercise room but in no team room.
    /// </remarks>
    [Fact]
    public async Task Push_CreatesAnExerciseRoomForEveryoneAndARoomForEachTeam()
    {
        var (msel, team, player) = await SeedMselAsync();
        var evaluator = await SeedUserAsync();
        await Seed(new UserMselRoleEntity(evaluator, msel.Id, MselRole.Evaluator));
        var handler = new SiblingApiHandler().KeycloakUser(player, "jsmith").KeycloakUser(evaluator, "evaluator")
            .AnswersJson("api/v1/users.info", RocketChatUser("rc-jsmith", "jsmith"), once: true)
            .AnswersJson("api/v1/users.info", RocketChatUser("rc-evaluator", "evaluator"), once: true)
            .AnswersJson("api/v1/groups.create", Group("exercise-room"), once: true)
            .AnswersJson("api/v1/groups.create", Group("team-room"), once: true)
            .Answers("api/v1/groups.setTopic", new { success = true });

        await PushAsync(msel.Id, handler);

        var creates = handler.Sent.Where(s => s.Path == "api/v1/groups.create").Select(s => Body(s.Body)).ToList();
        Assert.Equal("cyber-flag-27", creates[0]["name"].GetString());
        Assert.Equal(["evaluator", "jsmith"], Members(creates[0]));
        Assert.Equal("cyber-flag-27-blue", creates[1]["name"].GetString());
        Assert.Equal(["jsmith"], Members(creates[1]));

        var topics = handler.Sent.Where(s => s.Path == "api/v1/groups.setTopic").Select(s => Body(s.Body)["topic"].GetString());
        Assert.Equal(["Cyber Flag 27", "Cyber Flag 27 - Blue Team"], topics);

        await using var db = NewContext();
        Assert.Equal("exercise-room", (await db.Msels.SingleAsync(m => m.Id == msel.Id, Ct)).RocketChatRoomId);
        Assert.Equal("team-room", (await db.Teams.SingleAsync(t => t.Id == team.Id, Ct)).RocketChatRoomId);
    }

    /// <remarks>
    /// The account takes the Keycloak username, so the user's first Keycloak sign-in to Rocket.Chat is
    /// merged into it.
    /// </remarks>
    [Fact]
    public async Task Push_CreatesAnAccountForAUserNotInRocketChatYet()
    {
        var (msel, _, player) = await SeedMselAsync();
        var handler = new SiblingApiHandler().KeycloakUser(player, "jsmith", "jsmith@example.com", "Jane", "Smith")
            .AnswersJson("api/v1/users.info", UserNotFound, HttpStatusCode.BadRequest)
            .AnswersJson("api/v1/users.create", RocketChatUser("rc-jsmith", "jsmith"))
            .AnswersJson("api/v1/groups.create", Group("room"))
            .Answers("api/v1/groups.setTopic", new { success = true });

        await PushAsync(msel.Id, handler);

        var create = Body(handler.Sent.Single(s => s.Path == "api/v1/users.create").Body);
        Assert.Equal("jsmith", create["username"].GetString());
        Assert.Equal("jsmith@example.com", create["email"].GetString());
        Assert.Equal("Jane Smith", create["name"].GetString());
        Assert.True(create["verified"].GetBoolean());
        Assert.False(create["joinDefaultChannels"].GetBoolean());
        Assert.False(create["sendWelcomeEmail"].GetBoolean());
        Assert.All(handler.Sent.Where(s => s.Path == "api/v1/groups.create"), s => Assert.Equal(["jsmith"], Members(Body(s.Body))));
    }

    [Fact]
    public async Task Push_LeavesOutAUserKeycloakDoesNotKnow()
    {
        var (msel, _, player) = await SeedMselAsync();
        var handler = new SiblingApiHandler()
            .Answers($"admin/realms/crucible/users/{player}", HttpStatusCode.NotFound)
            .AnswersJson("api/v1/groups.create", Group("room"))
            .Answers("api/v1/groups.setTopic", new { success = true });

        await PushAsync(msel.Id, handler);

        Assert.DoesNotContain(handler.Sent, s => s.Path.StartsWith("api/v1/users."));
        Assert.All(handler.Sent.Where(s => s.Path == "api/v1/groups.create"), s => Assert.Empty(Members(Body(s.Body))));
    }

    /// <remarks>Room names are unique across the server, so a second launch of one template collides.</remarks>
    [Fact]
    public async Task Push_SuffixesARoomNameAlreadyTaken()
    {
        var (msel, _, player) = await SeedMselAsync();
        var handler = new SiblingApiHandler().KeycloakUser(player, "jsmith")
            .AnswersJson("api/v1/users.info", RocketChatUser("rc-jsmith", "jsmith"))
            .AnswersJson("api/v1/groups.create", DuplicateName, HttpStatusCode.BadRequest, once: true)
            .AnswersJson("api/v1/groups.create", Group("room"))
            .Answers("api/v1/groups.setTopic", new { success = true });

        await PushAsync(msel.Id, handler);

        var names = handler.Sent.Where(s => s.Path == "api/v1/groups.create").Select(s => Body(s.Body)["name"].GetString());
        Assert.Equal(["cyber-flag-27", $"cyber-flag-27-{msel.Id.ToString()[..8]}", "cyber-flag-27-blue"], names);
    }

    [Fact]
    public async Task Pull_DeletesEachRoomAndSwallowsARefusal()
    {
        var handler = new SiblingApiHandler()
            .AnswersJson("api/v1/groups.delete", """{"success":false,"errorType":"error-room-not-found"}""", HttpStatusCode.BadRequest, once: true)
            .Answers("api/v1/groups.delete", new { success = true });

        await IntegrationRocketChatExtensions.PullFromRocketChatAsync(["gone", null, "team-room"], Client(handler), Ct);

        var rooms = handler.Sent.Select(s => Body(s.Body)["roomId"].GetString());
        Assert.Equal(["gone", "team-room"], rooms);
    }

    [Fact]
    public async Task AddUserToTeam_InvitesTheUserToTheTeamAndExerciseRooms()
    {
        var (msel, team, _) = await SeedMselAsync();
        var joiner = await SeedUserAsync();
        await Db.Msels.Where(m => m.Id == msel.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.RocketChatRoomId, "exercise-room"), Ct);
        await Db.Teams.Where(t => t.Id == team.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.RocketChatRoomId, "team-room"), Ct);
        var handler = new SiblingApiHandler().KeycloakUser(joiner, "joiner")
            .AnswersJson("api/v1/users.info", RocketChatUser("rc-joiner", "joiner"))
            .Answers("api/v1/groups.invite", new { success = true });

        await IntegrationRocketChatExtensions.AddUserToTeamAsync(joiner, team.Id, Client(handler), KeycloakClient(handler), NewContext(), Ct);

        var invites = handler.Sent.Where(s => s.Path == "api/v1/groups.invite").Select(s => Body(s.Body)).ToList();
        Assert.Equal(["team-room", "exercise-room"], invites.Select(b => b["roomId"].GetString()));
        Assert.All(invites, b => Assert.Equal("rc-joiner", b["userId"].GetString()));
    }

    // ---------------------------------------------------------------------------------------------

    private async Task<(MselEntity Msel, TeamEntity Team, Guid Player)> SeedMselAsync()
    {
        var msel = TestData.Msel();
        msel.Name = "Cyber Flag 27";
        msel.UseRocketChat = true;
        var team = TestData.Team(msel.Id);
        team.Name = "Blue Team";
        team.ShortName = "BLUE";
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

    private async Task PushAsync(Guid mselId, SiblingApiHandler handler)
    {
        var msel = await Db.Msels
            .Include(m => m.Teams).ThenInclude(t => t.TeamUsers)
            .Include(m => m.UserMselRoles)
            .AsSplitQuery()
            .SingleAsync(m => m.Id == mselId, Ct);

        await IntegrationRocketChatExtensions.PushToRocketChatAsync(msel, Client(handler), KeycloakClient(handler), Db, Ct);
    }

    private static HttpClient Client(SiblingApiHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://sibling.example/") };

    private static HttpClient KeycloakClient(SiblingApiHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://sibling.example/admin/realms/crucible/") };

    private static string RocketChatUser(string id, string username) =>
        JsonSerializer.Serialize(new { success = true, user = new { _id = id, username } });

    private static string Group(string id) =>
        JsonSerializer.Serialize(new { success = true, group = new { _id = id } });

    private static string[] Members(Dictionary<string, JsonElement> body) =>
        body["members"].EnumerateArray().Select(m => m.GetString()).OrderBy(m => m).ToArray();

    private static Dictionary<string, JsonElement> Body(string body) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
}

internal static class KeycloakArrangements
{
    public static SiblingApiHandler KeycloakUser(this SiblingApiHandler handler, Guid userId, string username, string email = null, string firstName = null, string lastName = null) =>
        handler.Answers($"admin/realms/crucible/users/{userId}", new { username, email = email ?? $"{username}@example.com", firstName, lastName });
}
