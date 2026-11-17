// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using Blueprint.Api.Data.Models;

namespace Blueprint.Api.Services
{
    /// <summary>
    /// Who publishes each technique framework the parsers read and the notice its terms of use ask to be shown with
    /// its content. The parser of a file format stamps these on the release it builds, so the clients show whatever
    /// the release carries and never decide from a domain name. A release a parser does not recognise as MITRE's
    /// carries none. A parser sets <see cref="TechniqueReleaseEntity.ReleaseDate"/> before calling these, as the
    /// copyright year is the release's.
    /// </summary>
    public static class TechniqueFrameworkNotices
    {
        /// <summary>
        /// MITRE's copyright line for a release, in the year the release was published: a release is a snapshot of
        /// MITRE's work, so the year is the release's own and not the one the file was uploaded in.
        /// </summary>
        private static string MitreCopyright(TechniqueReleaseEntity release) =>
            $"© {(release.ReleaseDate ?? DateTime.UtcNow).Year} The MITRE Corporation. " +
            "This work is reproduced and distributed with the permission of The MITRE Corporation.";

        private const string AttackTerms = "https://attack.mitre.org/resources/legal-and-branding/terms-of-use/";

        public static void ForAttack(TechniqueReleaseEntity release)
        {
            release.Source = "MITRE ATT&CK";
            release.SourceUrl = "https://attack.mitre.org/";
            release.Notice = MitreCopyright(release) + "\nMITRE ATT&CK and ATT&CK are registered trademarks of The MITRE Corporation.";
            release.TermsUrl = AttackTerms;
        }

        public static void ForAtlas(TechniqueReleaseEntity release)
        {
            release.Source = "MITRE ATLAS";
            release.SourceUrl = "https://atlas.mitre.org/";
            release.Notice = MitreCopyright(release) + "\nMITRE ATLAS™ is a trademark of The MITRE Corporation.";
            release.TermsUrl = AttackTerms;
        }

        public static void ForCapec(TechniqueReleaseEntity release)
        {
            release.Source = "MITRE CAPEC";
            release.SourceUrl = "https://capec.mitre.org/";
            release.Notice = MitreCopyright(release) + "\nMITRE CAPEC™ is a trademark of The MITRE Corporation.";
            release.TermsUrl = "https://capec.mitre.org/about/termsofuse.html";
            release.GroupLabel = "Categories";
        }

        public static void ForD3fend(TechniqueReleaseEntity release)
        {
            release.Source = "MITRE D3FEND";
            release.SourceUrl = "https://d3fend.mitre.org/";
            release.Notice = MitreCopyright(release) + "\nMITRE D3FEND™ is a trademark of The MITRE Corporation.";
            release.TermsUrl = "https://d3fend.mitre.org/tou/";
        }
    }
}
