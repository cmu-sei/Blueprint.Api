// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Blueprint.Api.Services
{
    /// <summary>
    /// Makes article HTML survive Gallery's and Blueprint's sanitizers, which strip style attributes, style blocks,
    /// inline SVG, scripts and event handlers. What styling the HTML carries must be presentational attributes on
    /// tables and font tags, and each graphic an image. Inline SVG becomes an SVG data URI image with a size and alt
    /// text; the browser turns those into PNG before saving, for the views that show only PNG.
    /// </summary>
    public static class GalleryArticleHtml
    {
        private const int DefaultSvgWidth = 120;
        private const int DefaultSvgHeight = 40;
        private static readonly HtmlParser Parser = new();
        private static readonly Regex ViewBox = new(@"^\s*[-\d.]+[\s,]+[-\d.]+[\s,]+([\d.]+)[\s,]+([\d.]+)\s*$");

        /// <summary>
        /// Returns the HTML with everything the sanitizers would strip removed, and inline SVG turned into images.
        /// Each correction is added to notes.
        /// </summary>
        public static string Prepare(string html, List<string> notes = null)
        {
            if (string.IsNullOrWhiteSpace(html))
                return html;
            notes ??= new List<string>();
            var document = Parser.ParseDocument($"<body>{html}</body>");
            var body = document.Body;

            var removed = body.QuerySelectorAll("script, style, link, meta, iframe, object, embed").ToList();
            foreach (var element in removed)
                element.Remove();
            if (removed.Count > 0)
                notes.Add($"Removed {removed.Count} script, style or embedded {(removed.Count == 1 ? "element" : "elements")}.");

            var styles = 0;
            var handlers = 0;
            foreach (var element in body.QuerySelectorAll("*").Where(e => !IsInSvg(e)).ToList())
            {
                foreach (var attribute in element.Attributes.ToList())
                {
                    if (attribute.Name.Equals("style", StringComparison.OrdinalIgnoreCase))
                    {
                        element.RemoveAttribute(attribute.Name);
                        styles++;
                    }
                    else if (attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                    {
                        element.RemoveAttribute(attribute.Name);
                        handlers++;
                    }
                }
            }
            if (styles > 0)
                notes.Add($"Removed {styles} style {(styles == 1 ? "attribute" : "attributes")}, which Gallery does not show.");
            if (handlers > 0)
                notes.Add($"Removed {handlers} event {(handlers == 1 ? "handler" : "handlers")}.");

            var svgs = body.QuerySelectorAll("svg").Where(s => s.ParentElement == null || !IsInSvg(s.ParentElement)).ToList();
            foreach (var svg in svgs)
                svg.Replace(ToImage(document, svg));

            foreach (var image in body.QuerySelectorAll("img"))
            {
                if (string.IsNullOrWhiteSpace(image.GetAttribute("alt")))
                    image.SetAttribute("alt", "Exercise image");
                var src = image.GetAttribute("src") ?? "";
                if (!src.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && !src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add($"Removed an image from {Shorten(src)}; only embedded images and https links are kept.");
                    image.Remove();
                }
            }

            return body.InnerHtml.Trim();
        }

        /// <summary>The problems that would keep the HTML from showing in Gallery as written, empty when there are none</summary>
        public static List<string> Problems(string html)
        {
            var problems = new List<string>();
            if (string.IsNullOrWhiteSpace(html))
                return problems;
            var body = Parser.ParseDocument($"<body>{html}</body>").Body;
            if (body.QuerySelector("script, style") != null)
                problems.Add("a script or style element");
            if (body.QuerySelector("svg") != null)
                problems.Add("inline SVG");
            if (body.QuerySelectorAll("*").Any(e => e.Attributes.Any(a => a.Name == "style" || a.Name.StartsWith("on"))))
                problems.Add("a style attribute or event handler");
            if (body.QuerySelectorAll("img").Any(i => string.IsNullOrWhiteSpace(i.GetAttribute("alt"))))
                problems.Add("an image without alt text");
            return problems;
        }

        private static bool IsInSvg(IElement element)
        {
            for (var e = element; e != null; e = e.ParentElement)
            {
                if (e.LocalName == "svg")
                    return true;
            }
            return false;
        }

        // an SVG image needs its namespace and a size, or Firefox will not draw it
        private static IElement ToImage(IDocument document, IElement svg)
        {
            var (width, height) = Size(svg);
            svg.SetAttribute("xmlns", "http://www.w3.org/2000/svg");
            svg.SetAttribute("width", width.ToString(CultureInfo.InvariantCulture));
            svg.SetAttribute("height", height.ToString(CultureInfo.InvariantCulture));
            var alt = svg.GetAttribute("aria-label")
                ?? svg.QuerySelector("title")?.TextContent
                ?? "Exercise graphic";
            foreach (var attribute in svg.QuerySelectorAll("*").SelectMany(e => e.Attributes.Where(a => a.Name.StartsWith("on")).Select(a => (e, a.Name))).ToList())
                attribute.e.RemoveAttribute(attribute.Name);
            foreach (var script in svg.QuerySelectorAll("script, foreignObject").ToList())
                script.Remove();

            // the parser has already turned named entities into characters, so the markup is plain XML
            var markup = svg.OuterHtml;
            var image = document.CreateElement("img");
            image.SetAttribute("src", "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(markup)));
            image.SetAttribute("width", width.ToString(CultureInfo.InvariantCulture));
            image.SetAttribute("height", height.ToString(CultureInfo.InvariantCulture));
            image.SetAttribute("alt", alt.Trim());
            return image;
        }

        private static (int Width, int Height) Size(IElement svg)
        {
            int? Pixels(string value) =>
                value != null && double.TryParse(value.Replace("px", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number > 0
                    ? (int)Math.Round(number) : null;
            var width = Pixels(svg.GetAttribute("width"));
            var height = Pixels(svg.GetAttribute("height"));
            var viewBox = ViewBox.Match(svg.GetAttribute("viewBox") ?? svg.GetAttribute("viewbox") ?? "");
            if (viewBox.Success)
            {
                var vw = double.Parse(viewBox.Groups[1].Value, CultureInfo.InvariantCulture);
                var vh = double.Parse(viewBox.Groups[2].Value, CultureInfo.InvariantCulture);
                if (vw > 0 && vh > 0)
                {
                    width ??= height.HasValue ? (int)Math.Round(height.Value * vw / vh) : (int)Math.Round(vw);
                    height ??= (int)Math.Round(width.Value * vh / vw);
                }
            }
            return (width ?? DefaultSvgWidth, height ?? DefaultSvgHeight);
        }

        private static string Shorten(string value) => value.Length <= 60 ? value : value[..60] + "...";
    }
}
