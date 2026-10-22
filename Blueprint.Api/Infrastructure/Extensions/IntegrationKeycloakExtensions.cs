// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Blueprint.Api.Infrastructure.Extensions
{
    /// <summary>
    /// Reads users from the Keycloak admin API. Blueprint keeps only a user's subject and display
    /// name, so the chat integrations read the username and email from Keycloak to find or create
    /// the user's chat account. The service account needs the realm-management view-users role.
    /// </summary>
    public static class IntegrationKeycloakExtensions
    {
        // The Keycloak admin API of the realm the service account signs in to
        public static HttpClient GetKeycloakAdminClient(IHttpClientFactory httpClientFactory, string authority, IdentityModel.Client.TokenResponse tokenResponse)
        {
            var adminUrl = authority.TrimEnd('/').Replace("/realms/", "/admin/realms/") + "/";
            return ApiClientsExtensions.GetHttpClient(httpClientFactory, adminUrl, tokenResponse);
        }

        // null when Keycloak does not know the user or they have no username
        public static async Task<KeycloakUser> GetKeycloakUserAsync(Guid userId, HttpClient keycloakClient, CancellationToken ct)
        {
            using var response = await keycloakClient.GetAsync($"users/{userId}", ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();
            var user = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            var username = user["username"]?.GetValue<string>();
            if (string.IsNullOrEmpty(username))
                return null;
            var name = $"{user["firstName"]?.GetValue<string>()} {user["lastName"]?.GetValue<string>()}".Trim();
            return new KeycloakUser(username, user["email"]?.GetValue<string>(), name.Length == 0 ? username : name);
        }
    }

    // Name is "first last", or the username when Keycloak has neither
    public record KeycloakUser(string Username, string Email, string Name);
}
