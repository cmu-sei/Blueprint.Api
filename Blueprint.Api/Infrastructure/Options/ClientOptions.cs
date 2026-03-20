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

        public string CiteUiUrl { get; set; }
        public string GalleryUiUrl { get; set; }
        public string PlayerUiUrl { get; set; }
        public string SteamfitterUiUrl { get; set; }
        public string BlueprintUiUrl { get; set; }

        // Maximum concurrent requests for MSEL push operations
        // Higher values = faster but more database connections
        // Lower values = slower but safer for limited connection pools
        public int CiteMaxConcurrentRequests { get; set; } = 5;
        public int GalleryMaxConcurrentRequests { get; set; } = 5;
        public int PlayerMaxConcurrentRequests { get; set; } = 3;

        // HTTP client timeout in seconds for integration API calls
        // Default 300 seconds (5 minutes) - can be increased for large MSEL push operations
        public int HttpClientTimeoutSeconds { get; set; } = 300;
    }
}

