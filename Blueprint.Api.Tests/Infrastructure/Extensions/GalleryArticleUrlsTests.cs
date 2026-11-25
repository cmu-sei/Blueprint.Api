// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Blueprint.Api.Infrastructure.Extensions;
using Xunit;

namespace Blueprint.Api.Tests.Infrastructure.Extensions;

/// <summary>
/// <see cref="GalleryArticleUrls.IsAllowed"/> is Gallery's own rule for an article url. The push that calls it
/// is covered in <c>MselServiceIntegrationTests</c>.
/// </summary>
public class GalleryArticleUrlsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://news.example/a")]
    [InlineData("HTTPS://news.example/b?c=d")]
    public void An_empty_or_absolute_http_url_is_allowed(string url)
    {
        Assert.True(GalleryArticleUrls.IsAllowed(url));
    }

    /// <summary>Unlike a Player application's, an article url cannot be relative.</summary>
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hi")]
    [InlineData("ftp://news.example/a")]
    [InlineData("/news/1")]
    [InlineData("news.example/a")]
    [InlineData("http://")]
    public void Another_scheme_or_a_relative_url_is_refused(string url)
    {
        Assert.False(GalleryArticleUrls.IsAllowed(url));
    }
}
