// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
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
    /// Creates a Matrix space for the exercise holding a room for everyone and a room for each team.
    /// Written against Synapse: accounts, joins and room deletes use its admin API, so the bot must
    /// be a server admin.
    /// </summary>
    public static class IntegrationMatrixExtensions
    {
        public static bool IsConfigured(ClientOptions clientOptions)
        {
            return !string.IsNullOrWhiteSpace(clientOptions.MatrixUrl)
                && !string.IsNullOrWhiteSpace(clientOptions.MatrixAccessToken)
                && !string.IsNullOrWhiteSpace(clientOptions.MatrixOidcAuthProvider);
        }

        public static HttpClient GetMatrixClient(IHttpClientFactory httpClientFactory, ClientOptions clientOptions)
        {
            // the API paths are relative, so a homeserver served under a path needs the trailing slash
            var url = clientOptions.MatrixUrl.EndsWith("/") ? clientOptions.MatrixUrl : clientOptions.MatrixUrl + "/";
            var client = ApiClientsExtensions.GetHttpClient(httpClientFactory, url);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", clientOptions.MatrixAccessToken);
            return client;
        }

        // Create the space and its rooms, then join each member. Each ID is saved as soon as the
        // room exists, so a push that fails part way can still be pulled. The space links and joins
        // are repeated for rooms that already exist, so pushing again finishes a partial push.
        public static async Task PushToMatrixAsync(MselEntity msel, HttpClient matrixClient, HttpClient keycloakClient, string authProvider, BlueprintContext blueprintContext, CancellationToken ct)
        {
            var serverName = await GetServerNameAsync(matrixClient, ct);
            var teamUserIds = msel.Teams.SelectMany(t => t.TeamUsers.Select(tu => tu.UserId));
            // facilitators, evaluators and other MSEL role holders join the exercise room, not the team rooms
            var mselRoleUserIds = msel.UserMselRoles.Select(r => r.UserId);
            var matrixUsers = await GetMatrixUsersAsync(teamUserIds.Concat(mselRoleUserIds).Distinct(), serverName, authProvider, matrixClient, keycloakClient, ct);
            var everyone = matrixUsers.Values.Distinct().ToList();

            if (msel.MatrixSpaceId == null)
            {
                msel.MatrixSpaceId = await CreateRoomAsync(msel.Name, msel.Name, true, matrixClient, ct);
                await blueprintContext.SaveChangesAsync(ct);
            }
            await JoinAsync(msel.MatrixSpaceId, everyone, matrixClient, ct);

            if (msel.MatrixRoomId == null)
            {
                msel.MatrixRoomId = await CreateRoomAsync($"{msel.Name} - General", msel.Name, false, matrixClient, ct);
                await blueprintContext.SaveChangesAsync(ct);
            }
            await AddToSpaceAsync(msel.MatrixSpaceId, msel.MatrixRoomId, serverName, matrixClient, ct);
            await JoinAsync(msel.MatrixRoomId, everyone, matrixClient, ct);

            foreach (var team in msel.Teams)
            {
                if (team.MatrixRoomId == null)
                {
                    var name = $"{msel.Name} - {team.Name}";
                    team.MatrixRoomId = await CreateRoomAsync(name, name, false, matrixClient, ct);
                    await blueprintContext.SaveChangesAsync(ct);
                }
                await AddToSpaceAsync(msel.MatrixSpaceId, team.MatrixRoomId, serverName, matrixClient, ct);
                var members = team.TeamUsers
                    .Where(tu => matrixUsers.ContainsKey(tu.UserId))
                    .Select(tu => matrixUsers[tu.UserId])
                    .Distinct();
                await JoinAsync(team.MatrixRoomId, members, matrixClient, ct);
            }
        }

        // Delete and purge each room. Synapse finishes a delete in the background.
        public static async Task PullFromMatrixAsync(IEnumerable<string> roomIds, HttpClient matrixClient, CancellationToken ct)
        {
            foreach (var roomId in roomIds.Where(id => !string.IsNullOrEmpty(id)))
            {
                try
                {
                    await SendAsync(HttpMethod.Delete, $"_synapse/admin/v2/rooms/{Uri.EscapeDataString(roomId)}", new { purge = true }, matrixClient, ct);
                }
                catch (System.Exception)
                {
                }
            }
        }

        // Add a user who joined by invitation to the space, the exercise room and their team's room
        public static async Task AddUserToTeamAsync(Guid userId, Guid teamId, HttpClient matrixClient, HttpClient keycloakClient, string authProvider, BlueprintContext blueprintContext, CancellationToken ct)
        {
            var team = await blueprintContext.Teams
                .Include(t => t.Msel)
                .SingleAsync(t => t.Id == teamId, ct);
            var serverName = await GetServerNameAsync(matrixClient, ct);
            var matrixUsers = await GetMatrixUsersAsync(new[] { userId }, serverName, authProvider, matrixClient, keycloakClient, ct);
            if (!matrixUsers.TryGetValue(userId, out var matrixUserId))
                throw new InvalidOperationException($"User {userId} has no Keycloak username that can be a Matrix account, or that account belongs to someone else.");

            foreach (var roomId in new[] { team.Msel.MatrixSpaceId, team.Msel.MatrixRoomId, team.MatrixRoomId }.Where(id => !string.IsNullOrEmpty(id)))
            {
                await JoinAsync(roomId, new[] { matrixUserId }, matrixClient, ct);
            }
        }

        // the server name in user IDs, which need not be the host the homeserver is reached at
        private static async Task<string> GetServerNameAsync(HttpClient matrixClient, CancellationToken ct)
        {
            var userId = (await SendAsync(HttpMethod.Get, "_matrix/client/v3/account/whoami", null, matrixClient, ct))["user_id"].GetValue<string>();
            return userId[(userId.IndexOf(':') + 1)..];
        }

        private static async Task<string> CreateRoomAsync(string name, string topic, bool isSpace, HttpClient matrixClient, CancellationToken ct)
        {
            var body = new Dictionary<string, object>
            {
                ["name"] = name,
                ["topic"] = topic,
                ["preset"] = "private_chat",
                // only moderators invite, so players cannot bring in someone outside the exercise
                ["power_level_content_override"] = new { invite = 50 }
            };
            if (isSpace)
                body["creation_content"] = new { type = "m.space" };
            var response = await SendAsync(HttpMethod.Post, "_matrix/client/v3/createRoom", body, matrixClient, ct);
            return response["room_id"].GetValue<string>();
        }

        private static async Task AddToSpaceAsync(string spaceId, string roomId, string serverName, HttpClient matrixClient, CancellationToken ct)
        {
            await SendAsync(HttpMethod.Put, $"_matrix/client/v3/rooms/{Uri.EscapeDataString(spaceId)}/state/m.space.child/{Uri.EscapeDataString(roomId)}",
                new { via = new[] { serverName } }, matrixClient, ct);
        }

        // Join users without an invite. Synapse refuses to join a member again, so members are skipped.
        private static async Task JoinAsync(string roomId, IEnumerable<string> matrixUserIds, HttpClient matrixClient, CancellationToken ct)
        {
            var room = Uri.EscapeDataString(roomId);
            var members = (await SendAsync(HttpMethod.Get, $"_synapse/admin/v1/rooms/{room}/members", null, matrixClient, ct))["members"]
                .AsArray().Select(m => m.GetValue<string>()).ToHashSet();
            foreach (var matrixUserId in matrixUserIds.Where(id => !members.Contains(id)))
            {
                await SendAsync(HttpMethod.Post, $"_synapse/admin/v1/join/{room}", new { user_id = matrixUserId }, matrixClient, ct);
            }
        }

        /// <summary>
        /// Finds the Matrix user ID of each Blueprint user. A user who has signed in to Matrix through
        /// Keycloak is linked to their Keycloak subject, which is their Blueprint user ID. Anyone else
        /// gets an account named for their Keycloak username and linked to their subject, so their
        /// first sign-in lands in it. Users Keycloak does not know, whose username is not a valid
        /// Matrix name, or whose username is taken by an account that is not theirs are left out.
        /// </summary>
        private static async Task<Dictionary<Guid, string>> GetMatrixUsersAsync(IEnumerable<Guid> userIds, string serverName, string authProvider, HttpClient matrixClient, HttpClient keycloakClient, CancellationToken ct)
        {
            var matrixUsers = new Dictionary<Guid, string>();
            foreach (var userId in userIds)
            {
                try
                {
                    var linked = await SendAsync(HttpMethod.Get, $"_synapse/admin/v1/auth_providers/{Uri.EscapeDataString(authProvider)}/users/{userId}", null, matrixClient, ct);
                    matrixUsers[userId] = linked["user_id"].GetValue<string>();
                    continue;
                }
                catch (MatrixException ex) when (ex.ErrCode == "M_NOT_FOUND")
                {
                }

                var keycloakUser = await IntegrationKeycloakExtensions.GetKeycloakUserAsync(userId, keycloakClient, ct);
                // Synapse allows only these characters in the name part of a user ID
                if (keycloakUser == null || !Regex.IsMatch(keycloakUser.Username, "^[a-z0-9._=/+-]+$"))
                    continue;
                var matrixUserId = $"@{keycloakUser.Username}:{serverName}";
                var userPath = $"_synapse/admin/v2/users/{Uri.EscapeDataString(matrixUserId)}";
                try
                {
                    // the account exists but is not linked to this user, so it is someone else's
                    await SendAsync(HttpMethod.Get, userPath, null, matrixClient, ct);
                    continue;
                }
                catch (MatrixException ex) when (ex.ErrCode == "M_NOT_FOUND")
                {
                }

                var account = new Dictionary<string, object>
                {
                    ["displayname"] = keycloakUser.Name,
                    ["external_ids"] = new[] { new { auth_provider = authProvider, external_id = userId.ToString() } }
                };
                if (!string.IsNullOrEmpty(keycloakUser.Email))
                    account["threepids"] = new[] { new { medium = "email", address = keycloakUser.Email } };
                await SendAsync(HttpMethod.Put, userPath, account, matrixClient, ct);
                matrixUsers[userId] = matrixUserId;
            }

            return matrixUsers;
        }

        // Matrix reports a failure with an error status and an errcode such as M_NOT_FOUND
        private static async Task<JsonNode> SendAsync(HttpMethod method, string path, object body, HttpClient matrixClient, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body != null)
                request.Content = JsonContent.Create(body);
            using var response = await matrixClient.SendAsync(request, ct);
            var content = await response.Content.ReadAsStringAsync(ct);
            JsonNode json = null;
            try
            {
                json = JsonNode.Parse(content);
            }
            catch (JsonException)
            {
            }
            if (!response.IsSuccessStatusCode)
            {
                var errCode = json?["errcode"]?.GetValue<string>();
                var error = json?["error"]?.GetValue<string>() ?? $"{(int)response.StatusCode} {response.ReasonPhrase}";
                throw new MatrixException($"Matrix {method} {path} failed: {error}", errCode);
            }
            return json;
        }
    }

    public class MatrixException : Exception
    {
        public string ErrCode { get; }

        public MatrixException(string message, string errCode) : base(message)
        {
            ErrCode = errCode;
        }
    }
}
