// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Blueprint.Api.Infrastructure.Extensions
{
    /// <summary>
    /// A Gallery article's url, as a MSEL's scenario event holds it in its Url article field.
    /// </summary>
    /// <remarks>
    /// Gallery loads an article's url in an iframe and opens it in a new tab, so it accepts an empty url or an
    /// absolute http or https one with a host, and refuses the rest with a 400. <see cref="IsAllowed"/> is
    /// Gallery's own rule (<c>Gallery.Api ArticleUrl</c>), so a push can refuse a MSEL that would fail there.
    /// </remarks>
    public static class GalleryArticleUrls
    {
        public const string InvalidMessage =
            "A Gallery article's URL must be empty, or an absolute http or https URL.";

        public static bool IsAllowed(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return true;

            // a path such as "/a" is read as a file url on Linux, which the scheme check refuses as well
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                !string.IsNullOrEmpty(uri.Host);
        }
    }
}
