// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Blueprint.Api.Data.Models;

namespace Blueprint.Api.Services
{
    /// <summary>
    /// Release, tactic and technique ids, derived from the domain, version and the object's id in the file, so the
    /// same release has the same ids on every installation and a MSEL exported from one links its techniques on another.
    /// </summary>
    public static class TechniqueIds
    {
        /// <summary>The UUIDv5 namespace technique ids are derived in.</summary>
        public static readonly Guid IdNamespace = new("08e51b67-7c2e-4e26-85f4-1e8067551015");

        public static Guid ReleaseId(string domain, string version) =>
            CreateNameBasedGuid(IdNamespace, $"{domain}/{version}");

        public static Guid ObjectId(string domain, string version, string sourceId) =>
            CreateNameBasedGuid(IdNamespace, $"{domain}/{version}/{sourceId}");

        /// <summary>A version 5 (SHA-1, name-based) UUID per RFC 9562.</summary>
        public static Guid CreateNameBasedGuid(Guid namespaceId, string name)
        {
            var namespaceBytes = new byte[16];
            namespaceId.TryWriteBytes(namespaceBytes, bigEndian: true, out _);
            var hash = SHA1.HashData([.. namespaceBytes, .. Encoding.UTF8.GetBytes(name)]);

            var bytes = hash[..16];
            bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
            bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
            return new Guid(bytes, bigEndian: true);
        }
    }

    /// <summary>Links a technique to its parent as an import reads them.</summary>
    public static class TechniqueParents
    {
        /// <summary>
        /// Makes <paramref name="parent"/> the technique's parent, unless that would make the technique its own
        /// ancestor. A file can say a class is its own superclass, or that two techniques are each other's
        /// sub-technique; the link that would close the loop is left out, as EF cannot insert a cycle.
        /// </summary>
        /// <returns>True when the link was made.</returns>
        public static bool Link(TechniqueEntity technique, TechniqueEntity parent)
        {
            var seen = new HashSet<TechniqueEntity>(ReferenceEqualityComparer.Instance);
            for (var ancestor = parent; ancestor != null; ancestor = ancestor.Parent)
            {
                if (ReferenceEquals(ancestor, technique) || !seen.Add(ancestor))
                    return false;
            }

            // Set the navigation as well as the key so EF inserts the parent first.
            technique.ParentId = parent.Id;
            technique.Parent = parent;
            return true;
        }
    }

    /// <summary>What a release takes from its framework where the file says nothing.</summary>
    public static class TechniqueFrameworkDefaults
    {
        public static void Apply(TechniqueReleaseEntity release, TechniqueFrameworkEntity framework)
        {
            var settings = framework.Settings ?? new TechniqueFrameworkSettings();
            release.SourceUrl ??= Blank(settings.SourceUrl);
            release.TermsUrl ??= Blank(settings.TermsUrl);
            release.GroupLabel = Blank(settings.GroupLabel) ?? "Tactics";
        }

        /// <summary>A template with {id} and {sourceId} filled in; null when there is no template.</summary>
        public static string Link(string template, string id, string sourceId) =>
            string.IsNullOrWhiteSpace(template)
                ? null
                : template.Replace("{id}", Uri.EscapeDataString(id ?? ""), StringComparison.Ordinal)
                    .Replace("{sourceId}", sourceId ?? "", StringComparison.Ordinal);

        /// <summary>
        /// A file's link, if it is an http or https address; null otherwise. Links go into an href, so a script or data
        /// address in an imported file is dropped, as the framework and notice endpoints refuse one.
        /// </summary>
        public static string WebAddress(string url)
        {
            url = url?.Trim();
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? url
                : null;
        }

        private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
