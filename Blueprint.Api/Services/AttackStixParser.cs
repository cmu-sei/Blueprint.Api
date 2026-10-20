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
    /// Reads a MITRE ATT&amp;CK STIX 2.x bundle (enterprise-attack.json, mobile-attack.json, ics-attack.json) into
    /// an <see cref="AttackReleaseEntity"/> with its tactics and techniques.
    /// </summary>
    /// <remarks>
    /// Only what a planner picks from is read: the collection (name and version), the matrix (tactic order),
    /// tactics and techniques. Revoked and deprecated objects are skipped, as are groups, software, mitigations
    /// and data sources. A sub-technique's parent is found from its id (T1059.001 belongs to T1059) rather than
    /// from the subtechnique-of relationships, which say the same thing.
    /// </remarks>
    public static class AttackStixParser
    {
        /// <summary>The UUIDv5 namespace every ATT&amp;CK id in Blueprint is derived in.</summary>
        public static readonly Guid IdNamespace = new("08e51b67-7c2e-4e26-85f4-1e8067551015");

        private const string AttackSource = "mitre-attack";

        public static async Task<AttackReleaseEntity> ParseAsync(Stream stream, string version, CancellationToken ct)
        {
            JsonDocument document;
            try
            {
                document = await JsonDocument.ParseAsync(stream, default, ct);
            }
            catch (JsonException ex)
            {
                throw new InvalidAttackBundleException($"The file is not valid JSON: {ex.Message}");
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || GetString(root, "type") != "bundle"
                    || !root.TryGetProperty("objects", out var objects)
                    || objects.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidAttackBundleException("The file is not a STIX bundle. Upload an ATT&CK release such as enterprise-attack.json.");
                }

                return Parse(objects, version);
            }
        }

        private static AttackReleaseEntity Parse(JsonElement objects, string version)
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
                throw new InvalidAttackBundleException("The bundle has no ATT&CK techniques.");

            version = string.IsNullOrWhiteSpace(version)
                ? (collection.HasValue ? GetString(collection.Value, "x_mitre_version") : null)
                : version.Trim();
            if (string.IsNullOrWhiteSpace(version))
                throw new InvalidAttackBundleException("The bundle does not name its ATT&CK version. Enter the version and upload it again.");

            var domain = techniques
                .Select(t => t.TryGetProperty("x_mitre_domains", out var d) && d.ValueKind == JsonValueKind.Array
                    ? d.EnumerateArray().Select(x => x.GetString()).FirstOrDefault()
                    : null)
                .FirstOrDefault(d => !string.IsNullOrWhiteSpace(d)) ?? "enterprise-attack";

            var release = new AttackReleaseEntity
            {
                Id = ReleaseId(domain, version),
                Domain = domain,
                Version = version,
                Name = (collection.HasValue ? GetString(collection.Value, "name") : null) ?? domain,
                Description = collection.HasValue ? GetString(collection.Value, "description") : null,
                ReleaseDate = collection.HasValue ? GetDate(collection.Value, "modified") : null
            };

            // The matrix lists its tactics left to right; a tactic it leaves out goes at the end.
            var order = new Dictionary<string, int>();
            if (matrix.HasValue && matrix.Value.TryGetProperty("tactic_refs", out var refs) && refs.ValueKind == JsonValueKind.Array)
            {
                foreach (var tacticRef in refs.EnumerateArray())
                    order.TryAdd(tacticRef.GetString() ?? "", order.Count);
            }

            foreach (var tactic in tactics)
            {
                var (externalId, url) = GetAttackReference(tactic);
                var stixId = GetString(tactic, "id");
                if (externalId == null || stixId == null)
                    continue;

                release.Tactics.Add(new AttackTacticEntity
                {
                    Id = ObjectId(domain, version, stixId),
                    AttackReleaseId = release.Id,
                    StixId = stixId,
                    ExternalId = externalId,
                    ShortName = GetString(tactic, "x_mitre_shortname"),
                    Name = GetString(tactic, "name"),
                    Description = GetString(tactic, "description"),
                    Url = url,
                    DisplayOrder = order.TryGetValue(stixId, out var position) ? position : order.Count + release.Tactics.Count
                });
            }

            var byExternalId = new Dictionary<string, AttackTechniqueEntity>(StringComparer.OrdinalIgnoreCase);
            foreach (var technique in techniques)
            {
                var (externalId, url) = GetAttackReference(technique);
                var stixId = GetString(technique, "id");
                if (externalId == null || stixId == null || byExternalId.ContainsKey(externalId))
                    continue;

                var phases = technique.TryGetProperty("kill_chain_phases", out var k) && k.ValueKind == JsonValueKind.Array
                    ? k.EnumerateArray()
                        .Where(p => GetString(p, "kill_chain_name")?.EndsWith("attack", StringComparison.OrdinalIgnoreCase) == true)
                        .Select(p => GetString(p, "phase_name"))
                        .Where(p => p != null)
                    : [];

                var entity = new AttackTechniqueEntity
                {
                    Id = ObjectId(domain, version, stixId),
                    AttackReleaseId = release.Id,
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
            }

            foreach (var technique in release.Techniques)
            {
                var dot = technique.ExternalId.IndexOf('.');
                if (dot > 0 && byExternalId.TryGetValue(technique.ExternalId[..dot], out var parent))
                {
                    // Set the navigation as well as the key so EF inserts the parent first.
                    technique.ParentId = parent.Id;
                    technique.Parent = parent;
                }
            }

            return release;
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

        private static (string ExternalId, string Url) GetAttackReference(JsonElement item)
        {
            if (!item.TryGetProperty("external_references", out var references) || references.ValueKind != JsonValueKind.Array)
                return (null, null);

            foreach (var reference in references.EnumerateArray())
            {
                if (GetString(reference, "source_name") == AttackSource)
                    return (GetString(reference, "external_id"), GetString(reference, "url"));
            }

            return (null, null);
        }

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
