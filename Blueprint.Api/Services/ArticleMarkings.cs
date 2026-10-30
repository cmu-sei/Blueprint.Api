// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Blueprint.Api.Services
{
    /// <summary>
    /// The marking banner on a MSEL's Gallery articles, which Blueprint adds itself so it is exact and the same on every article:
    /// none, a generic exercise banner, a TLP marking, or a classification banner in the planner's words. A marking is a table
    /// titled Marking, so one already on an article is replaced rather than repeated.
    /// </summary>
    public static class ArticleMarkings
    {
        public const string None = "None";
        public const string Exercise = "Exercise";
        public const string Tlp = "Tlp";
        public const string Classification = "Classification";
        public static readonly string[] All = [None, Exercise, Tlp, Classification];
        public static readonly string[] TlpLevels = ["CLEAR", "GREEN", "AMBER", "AMBER+STRICT", "RED"];

        private const string Title = "Marking";
        private static readonly Regex _existing = new(
            @"<table\b[^>]*\btitle=""" + Title + @"""[^>]*>.*?</table>\s*", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        /// <summary>The marking a MSEL may have, defaulting to the exercise banner; the text is a TLP level or the classification</summary>
        public static (string Marking, string Text) Clean(string marking, string text)
        {
            var kind = All.FirstOrDefault(m => m.Equals(marking?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Exercise;
            var value = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            return kind switch
            {
                Tlp => (kind, TlpLevels.FirstOrDefault(l => l.Equals(value?.Replace("TLP:", "", StringComparison.OrdinalIgnoreCase).Trim(), StringComparison.OrdinalIgnoreCase)) ?? "CLEAR"),
                Classification => value == null ? (Exercise, null) : (kind, value.Length > 120 ? value[..120] : value),
                _ => (kind, null)
            };
        }

        /// <summary>The article with its MSEL's marking: at the top, and at the bottom too for a classification</summary>
        public static string Apply(string html, string marking, string text, string exerciseName)
        {
            if (string.IsNullOrWhiteSpace(html))
                return html;
            var body = _existing.Replace(html, "").Trim();
            var (kind, value) = Clean(marking, text);
            return kind switch
            {
                None => body,
                Tlp => TlpBanner(value) + body,
                Classification => ClassificationBanner(value) + body + ClassificationBanner(value),
                _ => ExerciseBanner(exerciseName) + body
            };
        }

        private static string ExerciseBanner(string exerciseName) =>
            Banner("#FDB516", "#000000", "EXERCISE · EXERCISE · EXERCISE",
                $"<br><font color=\"#000000\" face=\"Arial, Helvetica, sans-serif\" size=\"1\">{E(exerciseName)}{(string.IsNullOrWhiteSpace(exerciseName) ? "" : " · ")}FICTIONAL SCENARIO</font>");

        // TLP 2.0: the level's colour on black
        private static string TlpBanner(string level)
        {
            var colour = level switch
            {
                "RED" => "#FF2B2B",
                "AMBER" or "AMBER+STRICT" => "#FFC000",
                "GREEN" => "#33FF00",
                _ => "#FFFFFF"
            };
            return Banner("#000000", colour, "TLP:" + level, null, align: "left");
        }

        // the colours used on US classification banners, by the level the text names
        private static string ClassificationBanner(string text)
        {
            var upper = text.ToUpperInvariant();
            var (background, colour) =
                upper.Contains("TOP SECRET") ? (upper.Contains("SCI") ? "#FCE83A" : "#FF8C00", "#000000")
                : Regex.IsMatch(upper, @"\bSECRET\b") ? ("#C8102E", "#FFFFFF")
                : upper.Contains("CONFIDENTIAL") ? ("#0033A0", "#FFFFFF")
                : Regex.IsMatch(upper, @"\bCUI\b|CONTROLLED UNCLASSIFIED") ? ("#502B85", "#FFFFFF")
                : ("#007A33", "#FFFFFF");
            return Banner(background, colour, upper, null);
        }

        private static string Banner(string background, string colour, string text, string more, string align = "center") =>
            $"<table title=\"{Title}\" width=\"720\" cellpadding=\"6\" cellspacing=\"0\" border=\"0\"><tbody><tr>" +
            $"<td bgcolor=\"{background}\" align=\"{align}\"><font color=\"{colour}\" face=\"Arial, Helvetica, sans-serif\" size=\"2\"><b>{E(text)}</b></font>{more}</td>" +
            "</tr></tbody></table>";

        // only what HTML needs, so a middle dot or a slash stays as written
        private static string E(string text) =>
            (text ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
