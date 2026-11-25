// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.AspNetCore.Http;

namespace Blueprint.Api.Infrastructure.Extensions
{
    public static class DeprecatedRouteExtensions
    {
        /// <summary>
        /// When the former team-link update routes were deprecated (2026-11-23), as an RFC 9745 date.
        /// </summary>
        public const string TeamLinkUpdateRoutesDeprecated = "@1795392000";

        /// <summary>
        /// Tells the caller that the route it used is deprecated (RFC 9745) and names the route that
        /// replaces it (RFC 5829's successor-version relation), so a client still on the former route
        /// can be found and moved before the route is removed.
        /// </summary>
        public static void MarkDeprecated(this HttpResponse response, string deprecatedAt, string successor)
        {
            response.Headers["Deprecation"] = deprecatedAt;
            response.Headers.Link = $"<{successor}>; rel=\"successor-version\"";
        }
    }
}
