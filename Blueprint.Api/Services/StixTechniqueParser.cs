// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Exceptions;

namespace Blueprint.Api.Services
{
    /// <summary>
    /// Reads a MITRE ATT&amp;CK STIX 2.x bundle (enterprise-attack.json, mobile-attack.json, ics-attack.json), or a
    /// MITRE ATLAS one (stix-atlas.json) in the same data model, into an <see cref="TechniqueReleaseEntity"/> with its
    /// tactics and techniques.
    /// </summary>
    /// <remarks>
    /// Only what a planner picks from is read: the collection (name and version), the matrix (tactic order),
    /// tactics and techniques. Revoked and deprecated objects are skipped, as are groups, software, mitigations
    /// and data sources. Objects are identified by the external reference of the bundle's own catalogue
    /// ("mitre-attack" for the three ATT&amp;CK domains, "mitre-atlas" for ATLAS); any other reference, such as
    /// the ATT&amp;CK technique an ATLAS one is adapted from, is a cross-reference and ignored. A sub-technique's
    /// parent is found from its id (T1059.001 belongs to T1059, AML.T0000.000 to AML.T0000) rather than from
    /// the subtechnique-of relationships, which say the same thing.
    /// </remarks>
    public static class StixTechniqueParser
    {
        /// <summary>The UUIDv5 namespace every ATT&amp;CK id in Blueprint is derived in.</summary>
        public static readonly Guid IdNamespace = new("08e51b67-7c2e-4e26-85f4-1e8067551015");

        public static async Task<TechniqueReleaseEntity> ParseAsync(Stream stream, string version, CancellationToken ct)
        {
            using var document = await ReleaseFileParser.ReadAsync(stream, ct);
            return Parse(document.RootElement, version);
        }

        /// <summary>True when the JSON is a STIX bundle with its objects, the shape of enterprise-attack.json.</summary>
        public static bool CanParse(JsonElement root) =>
            root.ValueKind == JsonValueKind.Object
            && GetString(root, "type") == "bundle"
            && root.TryGetProperty("objects", out var objects)
            && objects.ValueKind == JsonValueKind.Array;

        public static TechniqueReleaseEntity Parse(JsonElement root, string version)
        {
            if (!CanParse(root))
                throw new InvalidTechniqueBundleException("The file is not a STIX bundle. Upload a release bundle such as enterprise-attack.json, stix-atlas.json or stix-capec.json.");

            return ParseObjects(root.GetProperty("objects"), version);
        }

        private static TechniqueReleaseEntity ParseObjects(JsonElement objects, string version)
        {
            JsonElement? collection = null;
            JsonElement? matrix = null;
            var tactics = new List<JsonElement>();
            var techniques = new List<JsonElement>();

            foreach (var item in objects.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || IsRetired(item))
                    continue;

                switch (GetString(item, "type"))
                {
                    case "x-mitre-collection":
                        collection ??= item;
                        break;
                    case "x-mitre-matrix":
                        matrix ??= item;
                        break;
                    case "x-mitre-tactic":
                        tactics.Add(item);
                        break;
                    case "attack-pattern":
                        techniques.Add(item);
                        break;
                }
            }

            if (techniques.Count == 0)
                throw new InvalidTechniqueBundleException("The bundle has no techniques.");

            // CAPEC is the one catalogue in this data model with no tactics. It is told apart by every pattern
            // carrying a CAPEC id and abstraction level, so a bundle of unidentified patterns is still refused.
            if (tactics.Count == 0 && techniques.All(IsCapecPattern))
                return ParseCapec(techniques, version);

            version = string.IsNullOrWhiteSpace(version)
                ? (collection.HasValue ? GetString(collection.Value, "x_mitre_version") : null)
                : version.Trim();
            if (string.IsNullOrWhiteSpace(version))
                throw new InvalidTechniqueBundleException("The bundle does not name its version. Enter the version and upload it again.");

            // The bundle's own catalogue is the reference source its tactics share. ATLAS tactics also cite the
            // ATT&CK tactic they correspond to, but not every one of them does, so the most common source wins.
            var source = tactics
                .SelectMany(t => GetReferences(t).Select(r => GetString(r, "source_name")))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .GroupBy(s => s)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault()
                ?? throw new InvalidTechniqueBundleException(
                    "The bundle's tactics carry no external reference naming their source (such as mitre-attack or mitre-atlas), so its techniques cannot be identified.");

            // Techniques name their tactics through kill chain phases. The chain is named for the domain
            // (mitre-attack, mitre-mobile-attack, mitre-ics-attack, mitre-atlas), so take the one the bundle uses.
            var killChain = techniques
                .SelectMany(t => GetPhases(t).Select(p => GetString(p, "kill_chain_name")))
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .GroupBy(k => k)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault() ?? source;

            var domain = techniques
                .Select(t => t.TryGetProperty("x_mitre_domains", out var d) && d.ValueKind == JsonValueKind.Array
                    ? d.EnumerateArray().Select(x => x.GetString()).FirstOrDefault()
                    : null)
                .FirstOrDefault(d => !string.IsNullOrWhiteSpace(d))
                ?? throw new InvalidTechniqueBundleException(
                    "The bundle's techniques name no domain (x_mitre_domains, such as enterprise-attack or ATLAS), so the release cannot be identified.");

            var release = new TechniqueReleaseEntity
            {
                Id = ReleaseId(domain, version),
                Domain = domain,
                IsDefensive = false,
                Version = version,
                Name = (collection.HasValue ? GetString(collection.Value, "name") : null) ?? domain,
                Description = collection.HasValue ? GetString(collection.Value, "description") : null,
                ReleaseDate = collection.HasValue ? GetDate(collection.Value, "modified") : null
            };
            // Only a bundle that cites MITRE's own catalogue is MITRE's: any other carries no publisher or notice.
            if (source == "mitre-atlas")
                TechniqueFrameworkNotices.ForAtlas(release);
            else if (source.StartsWith("mitre-", StringComparison.Ordinal) && source.EndsWith("attack", StringComparison.Ordinal))
                TechniqueFrameworkNotices.ForAttack(release);

            // The matrix lists its tactics left to right; a tactic it leaves out goes at the end.
            var order = new Dictionary<string, int>();
            if (matrix.HasValue && matrix.Value.TryGetProperty("tactic_refs", out var refs) && refs.ValueKind == JsonValueKind.Array)
            {
                foreach (var tacticRef in refs.EnumerateArray())
                    order.TryAdd(tacticRef.GetString() ?? "", order.Count);
            }

            foreach (var tactic in tactics)
            {
                var (externalId, url) = GetReference(tactic, source);
                var stixId = GetString(tactic, "id");
                if (externalId == null || stixId == null)
                    continue;

                release.Tactics.Add(new TechniqueTacticEntity
                {
                    Id = ObjectId(domain, version, stixId),
                    TechniqueReleaseId = release.Id,
                    StixId = stixId,
                    ExternalId = externalId,
                    ShortName = GetString(tactic, "x_mitre_shortname"),
                    Name = GetString(tactic, "name"),
                    Description = GetString(tactic, "description"),
                    Url = url,
                    DisplayOrder = order.TryGetValue(stixId, out var position) ? position : order.Count + release.Tactics.Count
                });
            }

            var byExternalId = new Dictionary<string, TechniqueEntity>(StringComparer.OrdinalIgnoreCase);
            var subTechniques = new List<TechniqueEntity>();
            foreach (var technique in techniques)
            {
                var (externalId, url) = GetReference(technique, source);
                var stixId = GetString(technique, "id");
                if (externalId == null || stixId == null || byExternalId.ContainsKey(externalId))
                    continue;

                var phases = GetPhases(technique)
                    .Where(p => GetString(p, "kill_chain_name") == killChain)
                    .Select(p => GetString(p, "phase_name"))
                    .Where(p => p != null);

                var entity = new TechniqueEntity
                {
                    Id = ObjectId(domain, version, stixId),
                    TechniqueReleaseId = release.Id,
                    StixId = stixId,
                    ExternalId = externalId,
                    Name = GetString(technique, "name"),
                    Description = GetString(technique, "description"),
                    Url = url,
                    Tactics = string.Join(",", phases.Distinct()),
                    Platforms = string.Join(",", GetStrings(technique, "x_mitre_platforms"))
                };
                byExternalId[externalId] = entity;
                release.Techniques.Add(entity);
                if (technique.TryGetProperty("x_mitre_is_subtechnique", out var sub) && sub.ValueKind == JsonValueKind.True)
                    subTechniques.Add(entity);
            }

            // A sub-technique's id is its parent's plus a last ".NNN" segment: T1059.001, AML.T0000.000.
            foreach (var technique in subTechniques)
            {
                var dot = technique.ExternalId.LastIndexOf('.');
                if (dot > 0 && byExternalId.TryGetValue(technique.ExternalId[..dot], out var parent))
                {
                    // Set the navigation as well as the key so EF inserts the parent first.
                    technique.ParentId = parent.Id;
                    technique.Parent = parent;
                }
            }

            return release;
        }

        /// <summary>The CAPEC release's domain; its patterns' own catalogue is "capec".</summary>
        public const string CapecDomain = "capec";

        private const string CapecSource = "capec";

        private static bool IsCapecPattern(JsonElement item) =>
            GetString(item, "x_capec_abstraction") != null
            && GetReferences(item).Any(r =>
                GetString(r, "source_name") == CapecSource
                && (GetString(r, "external_id") ?? "").StartsWith("CAPEC-", StringComparison.Ordinal));

        /// <summary>
        /// Reads a CAPEC bundle (stix-capec.json) into a release. CAPEC has no tactics, so its Meta-level patterns
        /// (Injection, Buffer Manipulation, Brute Force) are the release's groups, standing where tactics do for
        /// ATT&amp;CK. The Standard patterns under them are its techniques, and a Detailed pattern is a
        /// sub-technique of the Standard one it refines (SQL Injection under Command Injection). Deprecated and
        /// obsolete patterns are skipped; CAPEC marks them with a status, not the STIX revoked flag.
        /// </summary>
        private static TechniqueReleaseEntity ParseCapec(List<JsonElement> patterns, string version)
        {
            var active = patterns
                .Where(p => GetString(p, "x_capec_status") is not ("Deprecated" or "Obsolete"))
                .Where(p => GetString(p, "id") != null)
                .ToList();
            if (active.Count == 0)
                throw new InvalidTechniqueBundleException("The bundle has no techniques.");

            version = string.IsNullOrWhiteSpace(version)
                ? active
                    .Select(p => GetString(p, "x_capec_version"))
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .GroupBy(v => v)
                    .OrderByDescending(g => g.Count())
                    .Select(g => g.Key)
                    .FirstOrDefault()
                : version.Trim();
            if (string.IsNullOrWhiteSpace(version))
                throw new InvalidTechniqueBundleException("The bundle does not name its version. Enter the version and upload it again.");

            var release = new TechniqueReleaseEntity
            {
                Id = ReleaseId(CapecDomain, version),
                Domain = CapecDomain,
                IsDefensive = false,
                Version = version,
                Name = "MITRE CAPEC",
                Description = "Common Attack Pattern Enumeration and Classification: how attackers exploit weaknesses, "
                    + "from broad categories down to specific patterns such as SQL Injection or Buffer Overflow.",
                ReleaseDate = active.Select(p => GetDate(p, "modified")).Where(d => d.HasValue).Max()
            };
            TechniqueFrameworkNotices.ForCapec(release);

            var byStixId = new Dictionary<string, JsonElement>();
            foreach (var pattern in active)
                byStixId.TryAdd(GetString(pattern, "id"), pattern);

            bool IsMeta(JsonElement p) => GetString(p, "x_capec_abstraction") == "Meta";
            IEnumerable<JsonElement> Parents(JsonElement p) =>
                GetStrings(p, "x_capec_child_of_refs")
                    .Where(id => id != null && byStixId.ContainsKey(id))
                    .Select(id => byStixId[id]);

            // The groups: Meta patterns, alphabetically, since CAPEC gives them no order.
            var groups = active
                .Where(IsMeta)
                .Select(p => (Pattern: p, Id: GetCapecReference(p)))
                .Where(g => g.Id.ExternalId != null)
                .OrderBy(g => GetString(g.Pattern, "name"), StringComparer.OrdinalIgnoreCase)
                .ThenBy(g => g.Id.ExternalId, StringComparer.Ordinal)
                .ToList();
            var shortNames = new Dictionary<string, string>();
            foreach (var (pattern, id) in groups)
            {
                var stixId = GetString(pattern, "id");
                var shortName = id.ExternalId.ToLowerInvariant();
                shortNames[stixId] = shortName;
                release.Tactics.Add(new TechniqueTacticEntity
                {
                    Id = ObjectId(CapecDomain, version, stixId),
                    TechniqueReleaseId = release.Id,
                    StixId = stixId,
                    ExternalId = id.ExternalId,
                    ShortName = shortName,
                    Name = GetString(pattern, "name"),
                    Description = GetString(pattern, "description"),
                    Url = id.Url,
                    DisplayOrder = release.Tactics.Count
                });
            }

            // A pattern's parent technique is the first non-Meta pattern it is a child of; the release's techniques
            // are the ones without one. Parents are followed to the top, so a pattern three levels down is still a
            // sub-technique of the Standard pattern, not of a sub-technique.
            JsonElement? TechniqueParent(JsonElement p) =>
                Parents(p).Where(x => !IsMeta(x) && GetString(x, "id") != GetString(p, "id"))
                    .Select(x => (JsonElement?)x).FirstOrDefault();
            JsonElement Root(JsonElement p)
            {
                var seen = new HashSet<string> { GetString(p, "id") };
                var current = p;
                while (TechniqueParent(current) is { } parent && seen.Add(GetString(parent, "id")))
                    current = parent;
                return current;
            }

            // The Meta patterns above a pattern, reached through any chain of parents.
            HashSet<string> Groups(JsonElement p)
            {
                var found = new HashSet<string>();
                var seen = new HashSet<string>();
                var stack = new Stack<JsonElement>();
                stack.Push(p);
                while (stack.Count > 0)
                {
                    var current = stack.Pop();
                    foreach (var parent in Parents(current))
                    {
                        var id = GetString(parent, "id");
                        if (IsMeta(parent))
                            found.Add(id);
                        else if (seen.Add(id))
                            stack.Push(parent);
                    }
                }
                return found;
            }

            var byExternalId = new Dictionary<string, TechniqueEntity>(StringComparer.OrdinalIgnoreCase);
            var parentOf = new List<(TechniqueEntity Sub, string RootStixId)>();
            foreach (var pattern in active.Where(p => !IsMeta(p)))
            {
                var (externalId, url) = GetCapecReference(pattern);
                var stixId = GetString(pattern, "id");
                if (externalId == null || byExternalId.ContainsKey(externalId))
                    continue;

                var root = Root(pattern);
                var rootStixId = GetString(root, "id");
                var memberOf = Groups(pattern);
                if (rootStixId != stixId)
                    memberOf.UnionWith(Groups(root));

                var entity = new TechniqueEntity
                {
                    Id = ObjectId(CapecDomain, version, stixId),
                    TechniqueReleaseId = release.Id,
                    StixId = stixId,
                    ExternalId = externalId,
                    Name = GetString(pattern, "name"),
                    Description = GetString(pattern, "description"),
                    Url = url,
                    Tactics = string.Join(",", groups
                        .Select(g => GetString(g.Pattern, "id"))
                        .Where(memberOf.Contains)
                        .Select(g => shortNames[g])),
                    // CAPEC names the domains of attack (Software, Hardware, Social Engineering) where ATT&CK
                    // names platforms, and the pool shows the one column for both.
                    Platforms = string.Join(",", GetStrings(pattern, "x_capec_domains")),
                    Weaknesses = string.Join(",", GetReferences(pattern)
                        .Where(r => GetString(r, "source_name") == "cwe")
                        .Select(r => GetString(r, "external_id"))
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct()),
                    ReferenceLinks = CapecLinks(pattern)
                };
                byExternalId[externalId] = entity;
                release.Techniques.Add(entity);
                if (rootStixId != stixId)
                    parentOf.Add((entity, rootStixId));
            }

            var byStix = release.Techniques.ToDictionary(t => t.StixId);
            foreach (var (sub, rootStixId) in parentOf)
            {
                if (byStix.TryGetValue(rootStixId, out var parent))
                {
                    // Set the navigation as well as the key so EF inserts the parent first.
                    sub.ParentId = parent.Id;
                    sub.Parent = parent;
                }
            }

            if (release.Techniques.Count == 0)
                throw new InvalidTechniqueBundleException("The bundle has no techniques.");

            return release;
        }

        private static (string ExternalId, string Url) GetCapecReference(JsonElement item)
        {
            foreach (var reference in GetReferences(item))
            {
                var id = GetString(reference, "external_id");
                if (GetString(reference, "source_name") == CapecSource && id != null && id.StartsWith("CAPEC-", StringComparison.Ordinal))
                    return (id, GetString(reference, "url"));
            }

            return (null, null);
        }

        /// <summary>The pattern's OWASP and WASC references, one "source|label|url" line each; null when it has none.</summary>
        private static string CapecLinks(JsonElement pattern)
        {
            static string Clean(string value) => (value ?? "").Replace('|', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();

            var lines = new List<string>();
            foreach (var reference in GetReferences(pattern))
            {
                var source = GetString(reference, "source_name");
                var url = GetString(reference, "url");
                if (string.IsNullOrWhiteSpace(url) || source is not ("OWASP Attacks" or "WASC"))
                    continue;

                var label = GetString(reference, "description");
                var id = GetString(reference, "external_id");
                if (source == "WASC" && !string.IsNullOrWhiteSpace(id))
                    label = $"WASC-{id} {label}";
                lines.Add($"{(source == "WASC" ? "WASC" : "OWASP")}|{Clean(label)}|{Clean(url)}");
            }

            return lines.Count == 0 ? null : string.Join("\n", lines.Distinct());
        }

        public static Guid ReleaseId(string domain, string version) =>
            CreateNameBasedGuid(IdNamespace, $"{domain}/{version}");

        public static Guid ObjectId(string domain, string version, string stixId) =>
            CreateNameBasedGuid(IdNamespace, $"{domain}/{version}/{stixId}");

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

        private static bool IsRetired(JsonElement item) =>
            (item.TryGetProperty("revoked", out var revoked) && revoked.ValueKind == JsonValueKind.True)
            || (item.TryGetProperty("x_mitre_deprecated", out var deprecated) && deprecated.ValueKind == JsonValueKind.True);

        private static (string ExternalId, string Url) GetReference(JsonElement item, string source)
        {
            foreach (var reference in GetReferences(item))
            {
                if (GetString(reference, "source_name") == source)
                    return (GetString(reference, "external_id"), GetString(reference, "url"));
            }

            return (null, null);
        }

        private static IEnumerable<JsonElement> GetReferences(JsonElement item) => GetArray(item, "external_references");

        private static IEnumerable<JsonElement> GetPhases(JsonElement item) => GetArray(item, "kill_chain_phases");

        private static IEnumerable<JsonElement> GetArray(JsonElement item, string property) =>
            item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray()
                : [];

        private static string GetString(JsonElement item, string property) =>
            item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static IEnumerable<string> GetStrings(JsonElement item, string property) =>
            item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString())
                : [];

        private static DateTime? GetDate(JsonElement item, string property) =>
            DateTime.TryParse(GetString(item, property), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var date)
                ? DateTime.SpecifyKind(date, DateTimeKind.Utc)
                : null;
    }
}
