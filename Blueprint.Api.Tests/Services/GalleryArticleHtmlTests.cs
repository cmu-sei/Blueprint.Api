// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Blueprint.Api.Services;
using Ganss.Xss;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary><c>GalleryArticleHtml</c> - article HTML in the shape Gallery and Blueprint show as written: no styles,
/// scripts or handlers, and each graphic an image with a size and alt text.</summary>
public class GalleryArticleHtmlTests
{
    private readonly List<string> _notes = new();

    [Fact]
    public void Prepare_RemovesWhatTheSanitizersStrip()
    {
        var html = GalleryArticleHtml.Prepare(
            "<style>p{color:red}</style><p style=\"color:red\" onclick=\"x()\">Port closed</p><script>alert(1)</script>", _notes);

        Assert.Equal("<p>Port closed</p>", html);
        Assert.Contains(_notes, n => n.Contains("Removed 2 script, style or embedded elements"));
        Assert.Contains(_notes, n => n.Contains("Removed 1 style attribute"));
        Assert.Contains(_notes, n => n.Contains("Removed 1 event handler"));
    }

    [Fact]
    public void Prepare_KeepsTheEmailStyleMarkupGalleryShows()
    {
        const string article = "<table width=\"720\" cellpadding=\"12\" cellspacing=\"0\" border=\"0\" bgcolor=\"#FFFFFF\"><tbody><tr>" +
            "<td bgcolor=\"#FDB516\" align=\"center\" valign=\"middle\"><font color=\"#000000\" face=\"Arial, Helvetica, sans-serif\" size=\"2\"><b>EXERCISE</b></font></td>" +
            "</tr></tbody></table>";

        Assert.Equal(article, GalleryArticleHtml.Prepare(article, _notes));
        Assert.Empty(_notes);
    }

    [Fact]
    public void Prepare_TurnsInlineSvgIntoASizedImageWithAltText()
    {
        var html = GalleryArticleHtml.Prepare(
            "<p>Logo</p><svg viewBox=\"0 0 300 60\" aria-label=\"ArvelaNow masthead\"><rect width=\"300\" height=\"60\" fill=\"#6A1B9A\"/>" +
            "<text x=\"10\" y=\"40\" onload=\"x()\">News &middot; Region &amp; World</text></svg>", _notes);

        var image = Regex.Match(html, "<img src=\"data:image/svg\\+xml;base64,([^\"]+)\" width=\"300\" height=\"60\" alt=\"ArvelaNow masthead\">");
        Assert.True(image.Success, html);
        var svg = XDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(image.Groups[1].Value)));
        Assert.Equal("http://www.w3.org/2000/svg", svg.Root.Name.NamespaceName);
        Assert.Equal("300", svg.Root.Attribute("width").Value);
        Assert.Equal("News · Region & World", svg.Root.Descendants().Single(e => e.Name.LocalName == "text").Value);
        Assert.Null(svg.Root.Descendants().Single(e => e.Name.LocalName == "text").Attribute("onload"));
    }

    [Theory]
    [InlineData("<svg width=\"80\" viewBox=\"0 0 40 20\"></svg>", 80, 40)]
    [InlineData("<svg height=\"50\" viewBox=\"0 0 200 100\"></svg>", 100, 50)]
    [InlineData("<svg width=\"64px\" height=\"64px\"></svg>", 64, 64)]
    [InlineData("<svg></svg>", 120, 40)]
    public void Prepare_SizesAnSvgFromWhatItGives(string svg, int width, int height)
    {
        var html = GalleryArticleHtml.Prepare(svg, _notes);

        Assert.Contains($"width=\"{width}\" height=\"{height}\" alt=\"Exercise graphic\"", html);
    }

    [Fact]
    public void Prepare_GivesAnImageAltTextAndDropsOnesFromOtherHosts()
    {
        var html = GalleryArticleHtml.Prepare(
            "<img src=\"data:image/png;base64,AAAA\"><img src=\"http://example.com/a.png\" alt=\"x\"><img src=\"https://example.com/b.png\" alt=\"Map\">", _notes);

        Assert.Equal("<img src=\"data:image/png;base64,AAAA\" alt=\"Exercise image\"><img src=\"https://example.com/b.png\" alt=\"Map\">", html);
        Assert.Contains(_notes, n => n.Contains("Removed an image from http://example.com/a.png"));
    }

    [Fact]
    public void Problems_FindsWhatPrepareRemoves()
    {
        const string raw = "<p style=\"x\">a</p><svg></svg><img src=\"data:image/png;base64,AA\"><script></script>";

        Assert.Equal(4, GalleryArticleHtml.Problems(raw).Count);
        Assert.Empty(GalleryArticleHtml.Problems(GalleryArticleHtml.Prepare(raw)));
    }

    /// <summary>
    /// Team, organization and page HTML is saved through Blueprint's own sanitizer, set up as appsettings sets it (inject
    /// values are not). The prepared HTML comes through, except a font's face, which that HTML must not rely on.
    /// </summary>
    [Fact]
    public void Prepare_OutputSurvivesBlueprintsSanitizerButForFontFaces()
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedSchemes.Add("data");
        var html = GalleryArticleHtml.Prepare(
            "<table width=\"720\" cellpadding=\"0\" cellspacing=\"1\" border=\"0\" bgcolor=\"#D6D6D6\"><tbody><tr><td bgcolor=\"#FFFFFF\">" +
            "<font color=\"#1A1A1A\" face=\"Georgia, serif\" size=\"5\"><b>Headline</b></font>" +
            "<svg viewBox=\"0 0 100 20\"><rect width=\"100\" height=\"20\"/></svg></td></tr></tbody></table>");

        Assert.Equal(html.Replace(" face=\"Georgia, serif\"", ""), sanitizer.Sanitize(html));
    }

    /// <summary>A document is placed on white paper with dark ink, so a dark theme's page colour and text colour do not show through.</summary>
    [Fact]
    public void Prepare_WithAPaperWidth_PutsTheDocumentOnWhitePaperWithDarkInk()
    {
        var html = GalleryArticleHtml.Prepare("<h2>Port closed</h2><p>Ships wait offshore.</p>", _notes, 720);

        Assert.Equal("<table width=\"720\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" bgcolor=\"#FFFFFF\"><tbody><tr><td>" +
            "<font color=\"#1A1A1A\"><h2>Port closed</h2><p>Ships wait offshore.</p></font></td></tr></tbody></table>", html);
    }

    /// <summary>Preparing a document again, as an edit does, does not add a second sheet of paper.</summary>
    [Fact]
    public void Prepare_OfADocumentAlreadyOnPaper_AddsNoMorePaper()
    {
        var once = GalleryArticleHtml.Prepare("<table width=\"720\"><tbody><tr><td bgcolor=\"#003366\"><font color=\"#FFFFFF\">MASTHEAD</font></td></tr></tbody></table>", _notes, 720);

        Assert.Equal(once, GalleryArticleHtml.Prepare(once, _notes, 720));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(once, "bgcolor=\"#FFFFFF\""));
    }

    /// <summary>A document whose own outer table is white still gets the ink, as its text sets no colour.</summary>
    [Fact]
    public void Prepare_OfAWhiteTableWithoutInk_StillAddsTheInk()
    {
        var html = GalleryArticleHtml.Prepare("<table bgcolor=\"#FFFFFF\"><tbody><tr><td>Plain text</td></tr></tbody></table>", _notes, 680);

        Assert.StartsWith("<table width=\"680\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" bgcolor=\"#FFFFFF\"><tbody><tr><td><font color=\"#1A1A1A\"><table", html);
    }
}
