// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Options;

namespace Blueprint.Api.Infrastructure.Extensions
{
    /// <summary>
    /// Creates a private Rocket.Chat room for the exercise and one for each team. Rooms are private
    /// groups, so the group endpoints of the Rocket.Chat REST API are used throughout.
    /// </summary>
    public static class IntegrationRocketChatExtensions
    {
        public static bool IsConfigured(ClientOptions clientOptions)
        {
            return !string.IsNullOrWhiteSpace(clientOptions.RocketChatUrl)
                && !string.IsNullOrWhiteSpace(clientOptions.RocketChatUserId)
                && !string.IsNullOrWhiteSpace(clientOptions.RocketChatAuthToken);
        }

        public static HttpClient GetRocketChatClient(IHttpClientFactory httpClientFactory, ClientOptions clientOptions)
        {
            // the API paths are relative, so a Rocket.Chat served under a path needs the trailing slash
            var url = clientOptions.RocketChatUrl.EndsWith("/") ? clientOptions.RocketChatUrl : clientOptions.RocketChatUrl + "/";
            var client = ApiClientsExtensions.GetHttpClient(httpClientFactory, url);
            client.DefaultRequestHeaders.Add("X-User-Id", clientOptions.RocketChatUserId);
            client.DefaultRequestHeaders.Add("X-Auth-Token", clientOptions.RocketChatAuthToken);
            return client;
        }

        // Create the exercise room and the team rooms. Each room ID is saved as soon as the room
        // exists, so a push that fails part way can still be pulled.
        public static async Task PushToRocketChatAsync(MselEntity msel, HttpClient rocketChatClient, HttpClient keycloakClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            var teamUserIds = msel.Teams.SelectMany(t => t.TeamUsers.Select(tu => tu.UserId));
            // facilitators, evaluators and other MSEL role holders join the exercise room, not the team rooms
            var mselRoleUserIds = msel.UserMselRoles.Select(r => r.UserId);
            var rocketChatUsers = await GetRocketChatUsersAsync(teamUserIds.Concat(mselRoleUserIds).Distinct(), rocketChatClient, keycloakClient, ct);

            var exerciseRoomName = RoomName(msel.Name);
            if (msel.RocketChatRoomId == null)
            {
                var members = rocketChatUsers.Values.Select(u => u.Username).Distinct();
                msel.RocketChatRoomId = await CreateRoomAsync(exerciseRoomName, msel.Id, msel.Name, members, rocketChatClient, ct);
                await blueprintContext.SaveChangesAsync(ct);
            }

            foreach (var team in msel.Teams.Where(t => t.RocketChatRoomId == null))
            {
                var teamLabel = string.IsNullOrWhiteSpace(team.ShortName) ? team.Name : team.ShortName;
                var members = team.TeamUsers
                    .Where(tu => rocketChatUsers.ContainsKey(tu.UserId))
                    .Select(tu => rocketChatUsers[tu.UserId].Username)
                    .Distinct();
                team.RocketChatRoomId = await CreateRoomAsync(exerciseRoomName + "-" + RoomName(teamLabel), msel.Id, $"{msel.Name} - {team.Name}", members, rocketChatClient, ct);
                await blueprintContext.SaveChangesAsync(ct);
            }
        }

        public static async Task PullFromRocketChatAsync(IEnumerable<string> roomIds, HttpClient rocketChatClient, CancellationToken ct)
        {
            foreach (var roomId in roomIds.Where(id => !string.IsNullOrEmpty(id)))
            {
                try
                {
                    await PostAsync("api/v1/groups.delete", new { roomId }, rocketChatClient, ct);
                }
                catch (System.Exception)
                {
                }
            }
        }

        // Add a user who joined by invitation to their team's room and the exercise room
        public static async Task AddUserToTeamAsync(Guid userId, Guid teamId, HttpClient rocketChatClient, HttpClient keycloakClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            var team = await blueprintContext.Teams
                .Include(t => t.Msel)
                .SingleAsync(t => t.Id == teamId, ct);
            var rocketChatUsers = await GetRocketChatUsersAsync(new[] { userId }, rocketChatClient, keycloakClient, ct);
            if (!rocketChatUsers.TryGetValue(userId, out var rocketChatUser))
                throw new InvalidOperationException($"User {userId} has no Keycloak username or email to give a Rocket.Chat account.");

            foreach (var roomId in new[] { team.RocketChatRoomId, team.Msel.RocketChatRoomId }.Where(id => !string.IsNullOrEmpty(id)))
            {
                await PostAsync("api/v1/groups.invite", new { roomId, userId = rocketChatUser.Id }, rocketChatClient, ct);
            }
        }

        // Rocket.Chat room names allow letters, digits, '-', '_' and '.' by default
        public static string RoomName(string name)
        {
            var roomName = Regex.Replace((name ?? "").ToLowerInvariant(), "[^a-z0-9_.]+", "-").Trim('-', '.');
            return roomName.Length == 0 ? "exercise" : roomName;
        }

        private static async Task<string> CreateRoomAsync(string name, Guid mselId, string topic, IEnumerable<string> members, HttpClient rocketChatClient, CancellationToken ct)
        {
            JsonNode response;
            try
            {
                response = await PostAsync("api/v1/groups.create", new { name, members = members.ToArray() }, rocketChatClient, ct);
            }
            catch (RocketChatException ex) when (ex.ErrorType == "error-duplicate-channel-name")
            {
                // room names are unique across the server, so a second launch of the same template collides
                response = await PostAsync("api/v1/groups.create", new { name = $"{name}-{mselId.ToString()[..8]}", members = members.ToArray() }, rocketChatClient, ct);
            }
            var roomId = response["group"]["_id"].GetValue<string>();
            await PostAsync("api/v1/groups.setTopic", new { roomId, topic }, rocketChatClient, ct);
            return roomId;
        }

        /// <summary>
        /// Finds the Rocket.Chat account of each Blueprint user. Both apps take their users from
        /// Keycloak, but Rocket.Chat cannot be searched by subject, so the user is found by their
        /// Keycloak username. A user who has
        /// not signed in to Rocket.Chat yet is created there, and their first Keycloak sign-in is
        /// merged into that account by username. Users Keycloak does not know are left out.
        /// </summary>
        private static async Task<Dictionary<Guid, RocketChatUser>> GetRocketChatUsersAsync(IEnumerable<Guid> userIds, HttpClient rocketChatClient, HttpClient keycloakClient, CancellationToken ct)
        {
            var rocketChatUsers = new Dictionary<Guid, RocketChatUser>();
            foreach (var userId in userIds)
            {
                var keycloakUser = await IntegrationKeycloakExtensions.GetKeycloakUserAsync(userId, keycloakClient, ct);
                if (keycloakUser == null)
                    continue;
                var username = keycloakUser.Username;

                JsonNode user;
                try
                {
                    user = (await GetAsync($"api/v1/users.info?username={Uri.EscapeDataString(username)}", rocketChatClient, ct))["user"];
                }
                // users.info answers a missing user with only this message, no errorType
                catch (RocketChatException ex) when (ex.Error == "User not found.")
                {
                    // Rocket.Chat requires an email, so a Keycloak user without one waits for their own sign-in
                    if (string.IsNullOrEmpty(keycloakUser.Email))
                        continue;
                    user = (await PostAsync("api/v1/users.create", new
                    {
                        username,
                        email = keycloakUser.Email,
                        name = keycloakUser.Name,
                        // never used: the account is signed in to through Keycloak
                        password = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
                        verified = true,
                        joinDefaultChannels = false,
                        requirePasswordChange = false,
                        sendWelcomeEmail = false
                    }, rocketChatClient, ct))["user"];
                }
                rocketChatUsers[userId] = new RocketChatUser(user["_id"].GetValue<string>(), user["username"].GetValue<string>());
            }

            return rocketChatUsers;
        }

        private static async Task<JsonNode> GetAsync(string path, HttpClient rocketChatClient, CancellationToken ct)
        {
            using var response = await rocketChatClient.GetAsync(path, ct);
            return await ReadAsync(path, response, ct);
        }

        private static async Task<JsonNode> PostAsync(string path, object body, HttpClient rocketChatClient, CancellationToken ct)
        {
            using var response = await rocketChatClient.PostAsJsonAsync(path, body, ct);
            return await ReadAsync(path, response, ct);
        }

        // Rocket.Chat reports a failure as success: false with an errorType, usually with a 400
        private static async Task<JsonNode> ReadAsync(string path, HttpResponseMessage response, CancellationToken ct)
        {
            var content = await response.Content.ReadAsStringAsync(ct);
            JsonNode json = null;
            try
            {
                json = JsonNode.Parse(content);
            }
            catch (JsonException)
            {
            }
            if (!response.IsSuccessStatusCode || json?["success"]?.GetValue<bool>() != true)
            {
                var errorType = json?["errorType"]?.GetValue<string>();
                var error = json?["error"]?.GetValue<string>() ?? $"{(int)response.StatusCode} {response.ReasonPhrase}";
                throw new RocketChatException($"Rocket.Chat {path} failed: {error}", error, errorType);
            }
            return json;
        }

        private record RocketChatUser(string Id, string Username);
    }

    public class RocketChatException : Exception
    {
        public string Error { get; }
        public string ErrorType { get; }

        public RocketChatException(string message, string error, string errorType) : base(message)
        {
            Error = error;
            ErrorType = errorType;
        }
    }
}
