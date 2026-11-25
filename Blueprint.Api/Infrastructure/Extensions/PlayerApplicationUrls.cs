// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Text.RegularExpressions;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Options;

namespace Blueprint.Api.Infrastructure.Extensions
{
    /// <summary>
    /// A Player application's url, as Blueprint stores it and as it sends it to Player.
    /// </summary>
    /// <remarks>
    /// Player loads an application's url into an iframe, so it accepts only http and https urls, or urls with
    /// no scheme at all, and refuses the rest with a 409: a <c>javascript:</c> url would run in Player's
    /// origin. <see cref="IsAllowed"/> is Player's own rule (<c>Player.Api ApplicationUrls</c>), so Blueprint
    /// refuses what Player would, when the url is saved, rather than failing a push later in the background.
    /// Urls are templates: Blueprint fills in its own placeholders (<see cref="Resolve"/>) and leaves any
    /// other text in braces, such as Player's <c>{viewId}</c> or <c>{teamName}</c>, for Player.
    /// </remarks>
    public static partial class PlayerApplicationUrls
    {
        public const string InvalidMessage =
            "A Player application's Url must be an absolute http or https URL, or a relative one.";

        public const string UnsendableMessage =
            "Blueprint cannot send Player an application Url whose host or port is a placeholder. Use an address, or one of Blueprint's own placeholders such as {vmUrl}.";

        /// <summary>
        /// The ids Blueprint's id placeholders stand in for while a template is checked. Each is set, as a pushed
        /// MSEL's are, so a placeholder somewhere only an id is valid, such as a port, is caught.
        /// </summary>
        private static readonly MselEntity CheckMsel = new()
        {
            Id = Guid.Empty, PlayerViewId = Guid.Empty, CiteEvaluationId = Guid.Empty, GalleryExhibitId = Guid.Empty,
            SteamfitterScenarioId = Guid.Empty, CasterWorkspaceId = Guid.Empty
        };

        /// <summary>The address Blueprint's own url placeholders stand in for while a template is checked.</summary>
        private static readonly ClientOptions CheckAddresses = new()
        {
            PlayerUiUrl = "https://player.check", CiteUiUrl = "https://cite.check", GalleryUiUrl = "https://gallery.check",
            SteamfitterUiUrl = "https://steamfitter.check", CasterUiUrl = "https://caster.check",
            VmUiUrl = "https://vm.check", BlueprintUiUrl = "https://blueprint.check"
        };

        /// <summary>Stands in for each placeholder while the url is checked.</summary>
        private const char Hole = '\0';

        [GeneratedRegex(@"\{[^{}]*\}")]
        private static partial Regex Placeholder();

        /// <summary>A scheme, as a browser reads one before the first colon of a url.</summary>
        [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]*$")]
        private static partial Regex Scheme();

        /// <summary>
        /// Whether Player would accept <paramref name="url"/> as an application's url. No url is allowed.
        /// </summary>
        /// <remarks>
        /// A placeholder before the first colon is assumed to become whatever completes another scheme, as in
        /// Player, though Blueprint's own resolve to guids and the configured UI urls.
        /// </remarks>
        public static bool IsAllowed(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return true;

            // browsers strip the spaces and control characters around a url, and drop tabs and newlines
            // inside it, which can put a scheme back together ("java\tscript:")
            url = url.Trim();
            if (url.Any(char.IsControl))
                return false;

            var template = Placeholder().Replace(url, Hole.ToString());

            var colon = template.IndexOf(':');
            if (colon < 0)
                return true;

            var prefix = template[..colon];

            if (!prefix.Contains(Hole))
                return !Scheme().IsMatch(prefix) ||
                    prefix.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                    prefix.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

            // a scheme starts with a letter, so a prefix whose own first character is anything else can't
            // become one, whatever its placeholders hold
            if (prefix[0] != Hole && !char.IsAsciiLetter(prefix[0]))
                return true;

            return prefix.Replace(Hole.ToString(), string.Empty).TrimStart().Any(c => !IsSchemeCharacter(c));
        }

        /// <summary>
        /// Why Blueprint cannot save <paramref name="url"/> as an application url, or null when it can: Player
        /// would refuse it, or, with Blueprint's placeholders filled in, it is not a url Player's client can
        /// carry. That client sends a <see cref="Uri"/>, which cannot hold a placeholder in place of a host or
        /// port (<c>https://{vm-ui}/views/{viewId}</c>), so such a url would reach Player as none.
        /// </summary>
        public static string Problem(string url)
        {
            if (!IsAllowed(url))
                return InvalidMessage;
            if (string.IsNullOrWhiteSpace(url))
                return null;

            var resolved = Resolve(url, CheckMsel, CheckAddresses);
            return ToPlayerUri(resolved) == null ? UnsendableMessage : null;
        }

        /// <summary>
        /// Why the push of <paramref name="msel"/> would not be able to send <paramref name="url"/>, filled in from
        /// this installation's UI addresses, or null when it can. An integration id the push has not assigned
        /// yet stands in as an empty guid, as the push fills it in once it is assigned.
        /// </summary>
        public static string PushProblem(string url, MselEntity msel, ClientOptions clientOptions)
        {
            var pending = new MselEntity
            {
                Id = msel.Id,
                PlayerViewId = msel.PlayerViewId ?? Guid.Empty,
                CiteEvaluationId = msel.CiteEvaluationId ?? Guid.Empty,
                GalleryExhibitId = msel.GalleryExhibitId ?? Guid.Empty,
                SteamfitterScenarioId = msel.SteamfitterScenarioId ?? Guid.Empty,
                CasterWorkspaceId = msel.CasterWorkspaceId ?? Guid.Empty
            };
            return ResolvedProblem(Resolve(url, pending, clientOptions));
        }

        /// <summary>Why Blueprint cannot send an already filled-in url to Player, or null when it can.</summary>
        public static string ResolvedProblem(string resolvedUrl)
        {
            if (!IsAllowed(resolvedUrl))
                return InvalidMessage;
            if (string.IsNullOrWhiteSpace(resolvedUrl))
                return null;

            return ToPlayerUri(resolvedUrl) == null ? UnsendableMessage : null;
        }

        /// <summary>
        /// <paramref name="url"/> with Blueprint's placeholders filled in from <paramref name="msel"/> and the
        /// configured UI urls. Null stays null.
        /// </summary>
        public static string Resolve(string url, MselEntity msel, ClientOptions clientOptions)
        {
            return url
                ?.Replace("{blueprintMselId}", msel.Id.ToString())
                .Replace("{citeEvaluationId}", msel.CiteEvaluationId.ToString())
                .Replace("{galleryExhibitId}", msel.GalleryExhibitId.ToString())
                .Replace("{steamfitterScenarioId}", msel.SteamfitterScenarioId.ToString())
                .Replace("{casterWorkspaceId}", msel.CasterWorkspaceId.ToString())
                .Replace("{playerViewId}", msel.PlayerViewId.ToString())
                .Replace("{playerUrl}", clientOptions.PlayerUiUrl ?? "")
                .Replace("{citeUrl}", clientOptions.CiteUiUrl ?? "")
                .Replace("{galleryUrl}", clientOptions.GalleryUiUrl ?? "")
                .Replace("{steamfitterUrl}", clientOptions.SteamfitterUiUrl ?? "")
                .Replace("{casterUrl}", clientOptions.CasterUiUrl ?? "")
                .Replace("{vmUrl}", (clientOptions.VmUiUrl ?? "").TrimEnd('/'))
                .Replace("{blueprintUrl}", clientOptions.BlueprintUiUrl ?? "");
        }

        /// <summary>
        /// The resolved url as Player's client sends it, or null when there is none or Player would refuse it.
        /// A relative url is kept, as Player accepts one.
        /// </summary>
        public static Uri ToPlayerUri(string resolvedUrl)
        {
            if (string.IsNullOrWhiteSpace(resolvedUrl) || !IsAllowed(resolvedUrl))
                return null;

            return Uri.TryCreate(resolvedUrl.Trim(), UriKind.RelativeOrAbsolute, out var uri) ? uri : null;
        }

        private static bool IsSchemeCharacter(char c) =>
            char.IsAsciiLetterOrDigit(c) || c is '+' or '.' or '-';
    }
}
