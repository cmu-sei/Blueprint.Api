// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Net.Http;
using System.Threading.Tasks;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Options;
using IdentityModel.Client;

namespace Blueprint.Api.Tests.Infrastructure;

/// <summary>
/// A bearer token produced the way production produces one, for the tests that assert an integration
/// client carries it.
/// </summary>
/// <remarks>
/// Real rather than hand-built: <see cref="ApiClientsExtensions.RequestTokenAsync"/> runs against a
/// <see cref="TestHttpHandler"/> standing in for the identity provider, so the resulting
/// <c>TokenResponse</c> is one IdentityModel parsed rather than one a test asserted into existence. That
/// matters because <c>GetHttpClient</c> interpolates <c>TokenType</c> and <c>AccessToken</c> into a header
/// value and refuses some of what it might be handed - see <c>ApiClientsExtensionsTests</c>.
/// </remarks>
public static class Tokens
{
    /// <summary>The access token every <see cref="Bearer"/> carries, so tests can assert the header.</summary>
    public const string AccessToken = "abc123";

    /// <summary>The whole header value <c>GetHttpClient</c> writes for a <see cref="Bearer"/> token.</summary>
    public const string Header = "Bearer " + AccessToken;

    private const string Authority = "http://localhost:8080/realms/crucible";

    private const string Discovery = """
        {
          "issuer": "http://localhost:8080/realms/crucible",
          "authorization_endpoint": "http://localhost:8080/realms/crucible/protocol/openid-connect/auth",
          "token_endpoint": "http://localhost:8080/realms/crucible/protocol/openid-connect/token",
          "jwks_uri": "http://localhost:8080/realms/crucible/protocol/openid-connect/certs",
          "response_types_supported": ["code"],
          "subject_types_supported": ["public"],
          "id_token_signing_alg_values_supported": ["RS256"]
        }
        """;

    public static async Task<TokenResponse> Bearer()
    {
        var idp = new TestHttpHandler()
            .AnswersJson("realms/crucible/.well-known/openid-configuration", Discovery)
            .AnswersJson("realms/crucible/protocol/openid-connect/certs", """{"keys":[]}""")
            .AnswersJson(
                "realms/crucible/protocol/openid-connect/token",
                $$"""{"access_token":"{{AccessToken}}","token_type":"Bearer","expires_in":300}""");

        using var client = new HttpClient(idp, disposeHandler: false);

        return await ApiClientsExtensions.RequestTokenAsync(
            new ResourceOwnerAuthorizationOptions
            {
                Authority = Authority,
                ClientId = "blueprint-admin",
                UserName = "blueprint-admin",
                Password = string.Empty,
                Scope = "player player-vm cite gallery steamfitter"
            },
            client);
    }
}
