// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Exceptions;

namespace Blueprint.Api.Services
{
    /// <summary>
    /// Reads the MITRE D3FEND ontology (d3fend.json, JSON-LD) into an <see cref="AttackReleaseEntity"/> of the
    /// "d3fend" domain, flagged defensive, with the seven defensive tactics and the defensive techniques.
    /// </summary>
    /// <remarks>
    /// The ontology is a flat <c>@graph</c> of classes. A defensive technique is a class with a
    /// <c>d3f:d3fend-id</c> (D3-CH) under <c>d3f:DefensiveTechnique</c> through <c>rdfs:subClassOf</c>; the graph also
    /// gives d3fend-ids to analytic techniques (D3A-*), which are not countermeasures and are skipped. A technique's
    /// parent is its direct superclass when that is a technique too, so D3FEND's technique levels become the same
    /// technique/sub-technique tree ATT&amp;CK's do. Only a top-level technique names the tactic it
    /// <c>d3f:enables</c>; a sub-technique's tactics are found by walking up to one that does. Ids are derived from
    /// the version and the class id, as the STIX parser's are, so a re-import is the same release.
    /// </remarks>
    public static class D3fendOntologyParser
    {
        public const string Domain = "d3fend";
        public const string ReleaseName = "MITRE D3FEND";
        private const string SiteUrl = "https://d3fend.mitre.org";
        private const string OntologyType = "owl:Ontology";
        private const string TacticType = "d3f:DefensiveTactic";
        private const string TechniqueClass = "d3f:DefensiveTechnique";

        public static async Task<AttackReleaseEntity> ParseAsync(Stream stream, string version, CancellationToken ct)
        {
            using var document = await ReleaseFileParser.ReadAsync(stream, ct);
            return Parse(document.RootElement, version);
        }

        /// <summary>True when the JSON is a JSON-LD document with a <c>@graph</c>, the shape of d3fend.json.</summary>
        public static bool CanParse(JsonElement root) =>
            root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("@graph", out var graph)
            && graph.ValueKind == JsonValueKind.Array;

        public static AttackReleaseEntity Parse(JsonElement root, string version)
        {
            if (!CanParse(root))
                throw new InvalidAttackBundleException("The file is not the D3FEND ontology. Upload d3fend.json from the D3FEND Ontology Resources page.");

            var nodes = new Dictionary<string, JsonElement>();
            JsonElement? ontology = null;
            foreach (var node in root.GetProperty("@graph").EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object)
                    continue;
                if (ontology == null && HasType(node, OntologyType))
                    ontology = node;
                var id = GetString(node, "@id");
                if (id != null)
                    nodes.TryAdd(id, node);
            }

            var tactics = nodes.Values.Where(n => HasType(n, TacticType)).ToList();
            var techniques = nodes.Values
                .Where(n => GetText(n, "d3f:d3fend-id") != null && DescendsFrom(n, TechniqueClass, nodes))
                .ToList();
            if (tactics.Count == 0 || techniques.Count == 0)
                throw new InvalidAttackBundleException("The ontology has no defensive tactics and techniques.");

            version = string.IsNullOrWhiteSpace(version)
                ? (ontology.HasValue ? GetText(ontology.Value, "owl:versionInfo") : null)
                : version.Trim();
            if (string.IsNullOrWhiteSpace(version))
                throw new InvalidAttackBundleException("The ontology does not name its version. Enter the version and upload it again.");

            var release = new AttackReleaseEntity
            {
                Id = AttackStixParser.ReleaseId(Domain, version),
                Domain = Domain,
                IsDefensive = true,
                Version = version,
                Name = ReleaseName,
                Description = ontology.HasValue ? GetText(ontology.Value, "dcterms:description") : null,
                ReleaseDate = ontology.HasValue ? GetDate(ontology.Value, "d3f:release-date") : null
            };

            // The site lists the tactics by d3f:display-order (Model is -1, Harden 0, ... Restore 5).
            var shortNames = new Dictionary<string, string>();
            var position = 0;
            foreach (var tactic in tactics.OrderBy(t => GetInt(t, "d3f:display-order") ?? int.MaxValue).ThenBy(t => GetText(t, "rdfs:label")))
            {
                var id = GetString(tactic, "@id");
                var shortName = LocalName(id).ToLowerInvariant();
                shortNames[id] = shortName;
                release.Tactics.Add(new AttackTacticEntity
                {
                    Id = AttackStixParser.ObjectId(Domain, version, id),
                    AttackReleaseId = release.Id,
                    StixId = id,
                    // D3FEND gives its tactics no id of their own, so the class id stands in.
                    ExternalId = id,
                    ShortName = shortName,
                    Name = GetText(tactic, "rdfs:label") ?? LocalName(id),
                    Description = GetText(tactic, "d3f:definition"),
                    Url = $"{SiteUrl}/tactic/{id}/",
                    DisplayOrder = position++
                });
            }

            // The technique pages hang off the path d3f:DefensiveTechnique declares, /technique/ today.
            var techniquePath = nodes.TryGetValue(TechniqueClass, out var techniqueClass)
                ? GetText(techniqueClass, "d3f:display-baseurl") ?? "/technique/"
                : "/technique/";

            var byNodeId = new Dictionary<string, AttackTechniqueEntity>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var technique in techniques)
            {
                var id = GetString(technique, "@id");
                var externalId = GetText(technique, "d3f:d3fend-id");
                if (!seen.Add(externalId))
                    continue;

                var entity = new AttackTechniqueEntity
                {
                    Id = AttackStixParser.ObjectId(Domain, version, id),
                    AttackReleaseId = release.Id,
                    StixId = id,
                    ExternalId = externalId,
                    Name = GetText(technique, "rdfs:label") ?? LocalName(id),
                    Description = GetText(technique, "d3f:definition"),
                    Url = $"{SiteUrl}{techniquePath}{id}/",
                    Tactics = string.Join(",", EnabledTactics(technique, nodes).Select(t => shortNames.TryGetValue(t, out var s) ? s : null).Where(s => s != null).Distinct())
                };
                byNodeId[id] = entity;
                release.Techniques.Add(entity);
            }

            foreach (var (id, entity) in byNodeId)
            {
                var parentId = SuperClasses(nodes[id]).FirstOrDefault(byNodeId.ContainsKey);
                if (parentId != null)
                {
                    // Set the navigation as well as the key so EF inserts the parent first.
                    entity.ParentId = byNodeId[parentId].Id;
                    entity.Parent = byNodeId[parentId];
                }
            }

            return release;
        }

        /// <summary>The tactics a technique enables, or failing that the nearest superclass that enables any.</summary>
        private static IEnumerable<string> EnabledTactics(JsonElement technique, Dictionary<string, JsonElement> nodes)
        {
            var visited = new HashSet<string>();
            var queue = new Queue<JsonElement>();
            queue.Enqueue(technique);
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                var enabled = GetIds(node, "d3f:enables")
                    .Where(t => nodes.TryGetValue(t, out var tactic) && HasType(tactic, TacticType))
                    .ToList();
                if (enabled.Count > 0)
                    return enabled;

                foreach (var parent in SuperClasses(node))
                {
                    if (visited.Add(parent) && nodes.TryGetValue(parent, out var parentNode))
                        queue.Enqueue(parentNode);
                }
            }

            return [];
        }

        private static bool DescendsFrom(JsonElement node, string ancestor, Dictionary<string, JsonElement> nodes)
        {
            var visited = new HashSet<string>();
            var stack = new Stack<JsonElement>();
            stack.Push(node);
            while (stack.Count > 0)
            {
                foreach (var parent in SuperClasses(stack.Pop()))
                {
                    if (parent == ancestor)
                        return true;
                    if (visited.Add(parent) && nodes.TryGetValue(parent, out var parentNode))
                        stack.Push(parentNode);
                }
            }

            return false;
        }

        /// <summary>The named superclasses of a class; the anonymous restrictions ("_:b0063") are not classes a technique can belong to.</summary>
        private static IEnumerable<string> SuperClasses(JsonElement node) =>
            GetIds(node, "rdfs:subClassOf").Where(id => !id.StartsWith("_:", StringComparison.Ordinal));

        private static bool HasType(JsonElement node, string type) =>
            node.TryGetProperty("@type", out var value) && Values(value).Any(v => v.ValueKind == JsonValueKind.String && v.GetString() == type);

        /// <summary>The <c>@id</c>s a property points at, whether it holds one <c>{"@id": ...}</c> or a list of them.</summary>
        private static IEnumerable<string> GetIds(JsonElement node, string property) =>
            node.TryGetProperty(property, out var value)
                ? Values(value).Select(v => GetString(v, "@id")).Where(id => id != null)
                : [];

        /// <summary>A JSON-LD value: one item or a list; a literal is a string or a <c>{"@value": ...}</c> object.</summary>
        private static IEnumerable<JsonElement> Values(JsonElement value) =>
            value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [value];

        private static string GetText(JsonElement node, string property)
        {
            if (!node.TryGetProperty(property, out var value))
                return null;

            foreach (var item in Values(value))
            {
                if (item.ValueKind == JsonValueKind.String)
                    return item.GetString();
                var literal = GetString(item, "@value");
                if (literal != null)
                    return literal;
            }

            return null;
        }

        private static int? GetInt(JsonElement node, string property)
        {
            if (!node.TryGetProperty(property, out var value))
                return null;

            foreach (var item in Values(value))
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number))
                    return number;
                if (int.TryParse(item.ValueKind == JsonValueKind.String ? item.GetString() : GetString(item, "@value"), out number))
                    return number;
            }

            return null;
        }

        private static string GetString(JsonElement item, string property) =>
            item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static DateTime? GetDate(JsonElement node, string property) =>
            DateTime.TryParse(GetText(node, property), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var date)
                ? DateTime.SpecifyKind(date, DateTimeKind.Utc)
                : null;

        /// <summary>"CredentialHardening" from "d3f:CredentialHardening" or a full IRI.</summary>
        private static string LocalName(string id)
        {
            var cut = Math.Max(id.LastIndexOf('#'), Math.Max(id.LastIndexOf('/'), id.LastIndexOf(':')));
            return cut >= 0 ? id[(cut + 1)..] : id;
        }
    }
}
