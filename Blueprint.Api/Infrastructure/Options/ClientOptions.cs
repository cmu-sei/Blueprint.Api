// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Blueprint.Api.Infrastructure.Options
{
    public class ClientOptions
    {
        public string CiteApiUrl { get; set; }
        public string GalleryApiUrl { get; set; }
        public string PlayerApiUrl { get; set; }
        public string SteamfitterApiUrl { get; set; }
        public string CasterApiUrl { get; set; }
        public string RocketChatUrl { get; set; }
        public string MatrixUrl { get; set; }

        public string CiteUiUrl { get; set; }
        public string GalleryUiUrl { get; set; }
        public string PlayerUiUrl { get; set; }
        public string SteamfitterUiUrl { get; set; }
        public string CasterUiUrl { get; set; }
        public string VmUiUrl { get; set; }
        public string BlueprintApiUrl { get; set; }
        public string BlueprintUiUrl { get; set; }

        // Maximum concurrent requests for MSEL push operations
        // Higher values = faster but more database connections
        // Lower values = slower but safer for limited connection pools
        public int CiteMaxConcurrentRequests { get; set; } = 5;
        public int GalleryMaxConcurrentRequests { get; set; } = 5;
        public int PlayerMaxConcurrentRequests { get; set; } = 3;

        // Caster run polling. A terraform apply can take far longer than any other push step.
        public int CasterCheckIntervalSeconds { get; set; } = 10;
        public int CasterPlanMaxWaitMinutes { get; set; } = 15;
        public int CasterApplyMaxWaitMinutes { get; set; } = 120;
        public int CasterDestroyMaxWaitMinutes { get; set; } = 60;

        // Rocket.Chat takes no Keycloak tokens, so a push signs in as a bot user with a
        // personal access token. The bot needs the admin role, or create-p, delete-p,
        // add-user-to-any-p-room and view-full-other-user-info.
        public string RocketChatUserId { get; set; }
        public string RocketChatAuthToken { get; set; }

        // A push signs in to the Matrix homeserver as a bot user with its access token. The bot
        // must be a Synapse server admin, since accounts, joins and room deletes use the admin API.
        public string MatrixAccessToken { get; set; }
        // The Synapse auth provider of the Keycloak sign-in, "oidc-" followed by its idp_id.
        // Accounts made for users who have not signed in yet are linked to their Keycloak
        // subject under it, so their first sign-in lands in that account.
        public string MatrixOidcAuthProvider { get; set; }
    }
}

