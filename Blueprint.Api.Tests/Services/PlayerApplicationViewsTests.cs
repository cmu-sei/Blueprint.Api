// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Blueprint.Api.Services;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary>
/// <c>PlayerApplicationViews</c> - which Player applications show the same page, and so may not both be on one team.
/// </summary>
public class PlayerApplicationViewsTests
{
    /// <summary>CITE without a section opens on the dashboard and Gallery on the archive, whatever else the URL carries.</summary>
    [Theory]
    [InlineData("{citeUrl}", "{citeUrl}?section=dashboard")]
    [InlineData("{citeUrl}?evaluation={citeEvaluationId}", "{citeUrl}?section=dashboard&evaluation={citeEvaluationId}")]
    [InlineData("{galleryUrl}", "{galleryUrl}?section=archive&exhibit={galleryExhibitId}")]
    [InlineData("{citeUrl}?section=", "{citeUrl}")]
    public void Same_ForADefaultSection_IsTrue(string url, string other) =>
        Assert.True(PlayerApplicationViews.Same(url, other));

    /// <summary>A different section is a different page.</summary>
    [Theory]
    [InlineData("{citeUrl}?section=scoresheet", "{citeUrl}")]
    [InlineData("{galleryUrl}?section=wall", "{galleryUrl}")]
    [InlineData("{citeUrl}", "{galleryUrl}")]
    public void Same_ForADifferentSection_IsFalse(string url, string other) =>
        Assert.False(PlayerApplicationViews.Same(url, other));

    /// <summary>Case, a trailing slash, a fragment, percent-encoding and the order of the query do not make a page different.</summary>
    [Theory]
    [InlineData("{citeUrl}?SECTION=Dashboard", "{citeUrl}?section=dashboard")]
    [InlineData("{citeUrl}/?section=dashboard", "{citeUrl}?section=dashboard")]
    [InlineData("{citeUrl}?section=dashboard#help", "{citeUrl}?section=dashboard")]
    [InlineData("{galleryUrl}?section=archive#top", "{galleryUrl}")]
    [InlineData("{citeUrl}?section=%64ashboard", "{citeUrl}?section=dashboard")]
    [InlineData("https://host/app/?b=2&a=1", "https://host/app?a=1&b=2")]
    [InlineData("https://host/app/?", "https://host/app")]
    public void Same_ForADifferentSpelling_IsTrue(string url, string other) =>
        Assert.True(PlayerApplicationViews.Same(url, other));

    /// <summary>A page an application routes to by its fragment is not the same page as another fragment, and a value holding & or = is not a second parameter.</summary>
    [Theory]
    [InlineData("https://host/app#/one", "https://host/app#/two")]
    [InlineData("https://host/app?a=1%262", "https://host/app?a=1&2")]
    [InlineData("https://host/app?a=b%3Dc", "https://host/app?a=b&c=")]
    public void Same_ForADifferentFragmentOrEncodedSeparator_IsFalse(string url, string other) =>
        Assert.False(PlayerApplicationViews.Same(url, other));

    /// <summary>Other query values still tell two ordinary pages apart.</summary>
    [Fact]
    public void Same_ForADifferentParameter_IsFalse() =>
        Assert.False(PlayerApplicationViews.Same("https://host/app?a=1", "https://host/app?a=2"));
}
