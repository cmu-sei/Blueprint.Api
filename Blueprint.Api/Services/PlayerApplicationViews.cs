// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Blueprint.Api.Services
{
    /// <summary>
    /// What a Player application shows, so a MSEL and its teams get one application for each view, whatever the
    /// applications are called: CITE without a section opens on the dashboard, as CITE Dashboard does.
    /// </summary>
    public static class PlayerApplicationViews
    {
        // the sections CITE and Gallery open on when the URL names none
        private static readonly Dictionary<string, string> _defaultSections = new(StringComparer.OrdinalIgnoreCase)
        {
            ["{citeUrl}"] = "dashboard",
            ["{galleryUrl}"] = "archive"
        };

        /// <summary>
        /// The URL without its trailing slash before the query; for CITE and Gallery only the section it opens on, their
        /// default when it names none, since every other parameter is the MSEL's own evaluation, exhibit or theme
        /// </summary>
        public static string View(string url)
        {
            url = (url ?? "").Trim();
            var query = url.IndexOf('?');
            var path = (query < 0 ? url : url[..query]).TrimEnd('/');
            var parameters = query < 0 ? "" : url[(query + 1)..];
            var app = _defaultSections.Keys.FirstOrDefault(k => path.Equals(k, StringComparison.OrdinalIgnoreCase));
            if (app == null)
                return path + (query < 0 ? "" : "?" + parameters);
            var section = parameters.Split('&').Select(p => p.Split('=', 2))
                .Where(p => p.Length == 2 && p[0].Equals("section", StringComparison.OrdinalIgnoreCase)).Select(p => p[1]).FirstOrDefault();
            return path + "?section=" + (section ?? _defaultSections[app]).ToLowerInvariant();
        }

        public static bool Same(string url, string other) =>
            string.Equals(View(url), View(other), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// A team shows each view once in Player: refuses giving the team this application when it has it already, or has
        /// another for the same view.
        /// </summary>
        public static async Task EnsureTeamHasNoSameViewAsync(BlueprintContext context, Guid teamId, PlayerApplicationEntity application, CancellationToken ct)
        {
            var teamApplications = await context.PlayerApplicationTeams
                .Where(pt => pt.TeamId == teamId)
                .Select(pt => new { pt.PlayerApplicationId, pt.PlayerApplication.Name, pt.PlayerApplication.Url, Team = pt.Team.ShortName ?? pt.Team.Name })
                .ToListAsync(ct);
            var team = teamApplications.FirstOrDefault()?.Team
                ?? await context.Teams.Where(t => t.Id == teamId).Select(t => t.ShortName ?? t.Name).FirstOrDefaultAsync(ct);
            if (teamApplications.Any(a => a.PlayerApplicationId == application.Id))
                throw new ConflictException($"{team} already has {application.Name}.");
            var same = teamApplications.FirstOrDefault(a => Same(a.Url, application.Url));
            if (same != null)
                throw new ConflictException($"{team} already has {same.Name}, which shows the same view as {application.Name}.");
        }
    }
}
