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
        /// The URL without its trailing slash before the query, with the query's parameters in order; for CITE and Gallery only
        /// the section it opens on (their default when it names none or an empty one) and no fragment, since every other
        /// parameter is the MSEL's own evaluation, exhibit or theme. An ordinary URL keeps its fragment, as an application may
        /// route by it.
        /// </summary>
        public static string View(string url)
        {
            url = (url ?? "").Trim();
            var fragmentAt = url.IndexOf('#');
            var fragment = fragmentAt < 0 ? "" : url[fragmentAt..];
            if (fragmentAt >= 0)
                url = url[..fragmentAt];
            var query = url.IndexOf('?');
            var path = (query < 0 ? url : url[..query]).TrimEnd('/');
            var parameters = (query < 0 ? "" : url[(query + 1)..])
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .Select(p => (Name: Decode(p[0]), Value: p.Length == 2 ? Decode(p[1]) : ""))
                .ToList();
            var app = _defaultSections.Keys.FirstOrDefault(k => path.Equals(k, StringComparison.OrdinalIgnoreCase));
            if (app == null)
            {
                var ordered = parameters.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Value, StringComparer.Ordinal)
                    .Select(p => Uri.EscapeDataString(p.Name) + "=" + Uri.EscapeDataString(p.Value));
                return path + (parameters.Count == 0 ? "" : "?" + string.Join('&', ordered)) + fragment;
            }
            var section = parameters.Where(p => p.Name.Equals("section", StringComparison.OrdinalIgnoreCase) && p.Value.Length > 0)
                .Select(p => p.Value).FirstOrDefault();
            return path + "?section=" + (section ?? _defaultSections[app]).ToLowerInvariant();
        }

        private static string Decode(string value)
        {
            try
            {
                return Uri.UnescapeDataString(value.Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                return value;
            }
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
            // the team's name is read with its applications, so a team with none has nothing to refuse
            if (teamApplications.Any(a => a.PlayerApplicationId == application.Id))
                throw new ConflictException($"{teamApplications[0].Team} already has {application.Name}.");
            var same = teamApplications.FirstOrDefault(a => Same(a.Url, application.Url));
            if (same != null)
                throw new ConflictException($"{same.Team} already has {same.Name}, which shows the same view as {application.Name}.");
        }
    }
}
