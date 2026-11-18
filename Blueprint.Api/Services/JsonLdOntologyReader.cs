// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Exceptions;

namespace Blueprint.Api.Services
{
    /// <summary>
    /// Reads a JSON-LD ontology into a <see cref="TechniqueReleaseEntity"/>, as its framework definition says.
    /// </summary>
    /// <remarks>
    /// The ontology is a flat <c>@graph</c> of classes. A technique is a class with the definition's id property
    /// that descends from its technique class; a tactic is a node of its tactic type. A technique's parent is its
    /// direct superclass when that is a technique too. A technique without tactics of its own takes those of the
    /// nearest ancestor that names some.
    /// </remarks>
    public static class JsonLdOntologyReader
    {
        private const string OntologyType = "owl:Ontology";

        /// <summary>True when the JSON is a JSON-LD document with a <c>@graph</c>.</summary>
        public static bool CanRead(JsonElement root) =>
            root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("@graph", out var graph)
            && graph.ValueKind == JsonValueKind.Array;

        /// <summary>What a framework's match conditions are checked against.</summary>
        public sealed class Summary
        {
            public HashSet<string> Properties { get; init; } = new HashSet<string>();
            public string Title { get; init; }
        }

        public static Summary Summarize(JsonElement root)
        {
            var properties = new HashSet<string>(StringComparer.Ordinal);
            string title = null;
            foreach (var node in root.GetProperty("@graph").EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object)
                    continue;
                foreach (var property in node.EnumerateObject())
                    properties.Add(property.Name);
                if (title == null && HasType(node, OntologyType))
                    title = GetText(node, "dcterms:title");
            }

            return new Summary { Properties = properties, Title = title };
        }

        public static TechniqueReleaseEntity Read(JsonElement root, string version, TechniqueFrameworkEntity framework)
        {
            if (!CanRead(root))
                throw new InvalidTechniqueBundleException("The file is not a JSON-LD ontology.");

            var settings = framework.Settings ?? new TechniqueFrameworkSettings();
            var options = settings.JsonLd ?? new JsonLdFormatOptions();
            if (string.IsNullOrWhiteSpace(options.IdProperty) || string.IsNullOrWhiteSpace(options.TechniqueClass) || string.IsNullOrWhiteSpace(options.TacticType))
                throw new InvalidTechniqueBundleException($"The {framework.Name} framework does not say which id property, technique class and tactic type to read.");

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

            var tactics = nodes.Values.Where(n => HasType(n, options.TacticType)).ToList();
            var techniques = nodes.Values
                .Where(n => GetText(n, options.IdProperty) != null && DescendsFrom(n, options.TechniqueClass, nodes))
                .ToList();
            if (tactics.Count == 0 || techniques.Count == 0)
                throw new InvalidTechniqueBundleException("The ontology has no tactics and techniques of the framework's types.");

            version = string.IsNullOrWhiteSpace(version)
                ? (ontology.HasValue ? GetText(ontology.Value, "owl:versionInfo") : null)
                : version.Trim();
            if (string.IsNullOrWhiteSpace(version))
                throw new InvalidTechniqueBundleException("The ontology does not name its version. Enter the version and upload it again.");

            var domain = framework.Domain;
            var release = new TechniqueReleaseEntity
            {
                Id = TechniqueIds.ReleaseId(domain, version),
                Domain = domain,
                IsDefensive = framework.IsDefensive,
                TechniqueFrameworkId = framework.Id,
                Version = version,
                Name = framework.Name,
                Description = (ontology.HasValue ? GetText(ontology.Value, "dcterms:description") : null) ?? framework.Description,
                ReleaseDate = ontology.HasValue && !string.IsNullOrWhiteSpace(options.ReleaseDateProperty) ? GetDate(ontology.Value, options.ReleaseDateProperty) : null
            };
            // the publisher and notice, as the ontology's title and notice property state them
            if (ontology.HasValue)
            {
                var title = GetText(ontology.Value, "dcterms:title");
                release.Source = title?.Split(" - ", 2)[0].Trim();
                if (!string.IsNullOrWhiteSpace(options.NoticeProperty))
                    release.Notice = GetText(ontology.Value, options.NoticeProperty)?.Trim();
            }
            TechniqueFrameworkDefaults.Apply(release, framework);

            var shortNames = new Dictionary<string, string>();
            var position = 0;
            var ordered = string.IsNullOrWhiteSpace(options.DisplayOrderProperty)
                ? tactics.OrderBy(t => GetText(t, "rdfs:label"))
                : tactics.OrderBy(t => GetInt(t, options.DisplayOrderProperty) ?? int.MaxValue).ThenBy(t => GetText(t, "rdfs:label"));
            foreach (var tactic in ordered)
            {
                var id = GetString(tactic, "@id");
                var shortName = LocalName(id).ToLowerInvariant();
                shortNames[id] = shortName;
                release.Tactics.Add(new TechniqueTacticEntity
                {
                    Id = TechniqueIds.ObjectId(domain, version, id),
                    TechniqueReleaseId = release.Id,
                    StixId = id,
                    // Tactics have no id of their own, so the class id stands in.
                    ExternalId = id,
                    ShortName = shortName,
                    Name = GetText(tactic, "rdfs:label") ?? LocalName(id),
                    Description = Definition(tactic, options),
                    Url = TechniqueFrameworkDefaults.Link(settings.TacticUrlTemplate, id, id),
                    DisplayOrder = position++
                });
            }

            var byNodeId = new Dictionary<string, TechniqueEntity>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var technique in techniques)
            {
                var id = GetString(technique, "@id");
                var externalId = GetText(technique, options.IdProperty);
                if (!seen.Add(externalId))
                    continue;

                var entity = new TechniqueEntity
                {
                    Id = TechniqueIds.ObjectId(domain, version, id),
                    TechniqueReleaseId = release.Id,
                    StixId = id,
                    ExternalId = externalId,
                    Name = GetText(technique, "rdfs:label") ?? LocalName(id),
                    Description = Definition(technique, options),
                    Url = TechniqueFrameworkDefaults.Link(settings.TechniqueUrlTemplate, externalId, id),
                    Tactics = string.Join(",", Tactics(technique, nodes, options).Select(t => shortNames.TryGetValue(t, out var s) ? s : null).Where(s => s != null).Distinct())
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

        private static string Definition(JsonElement node, JsonLdFormatOptions options) =>
            string.IsNullOrWhiteSpace(options.DefinitionProperty) ? null : GetText(node, options.DefinitionProperty);

        /// <summary>The tactics a technique names, or failing that those of the nearest superclass that names any.</summary>
        private static IEnumerable<string> Tactics(JsonElement technique, Dictionary<string, JsonElement> nodes, JsonLdFormatOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.TacticRelation))
                return [];

            var visited = new HashSet<string>();
            var queue = new Queue<JsonElement>();
            queue.Enqueue(technique);
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                var named = GetIds(node, options.TacticRelation)
                    .Where(t => nodes.TryGetValue(t, out var tactic) && HasType(tactic, options.TacticType))
                    .ToList();
                if (named.Count > 0)
                    return named;

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

        /// <summary>The named superclasses of a class; anonymous restrictions are skipped.</summary>
        private static IEnumerable<string> SuperClasses(JsonElement node) =>
            GetIds(node, "rdfs:subClassOf").Where(id => !id.StartsWith("_:", StringComparison.Ordinal));

        private static bool HasType(JsonElement node, string type) =>
            node.TryGetProperty("@type", out var value) && Values(value).Any(v => v.ValueKind == JsonValueKind.String && v.GetString() == type);

        /// <summary>The <c>@id</c>s a property points at, whether it holds one or a list.</summary>
        private static IEnumerable<string> GetIds(JsonElement node, string property) =>
            node.TryGetProperty(property, out var value)
                ? Values(value).Select(v => GetString(v, "@id")).Where(id => id != null)
                : [];

        /// <summary>A JSON-LD value: one item or a list; a literal is a string or a <c>@value</c> object.</summary>
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

        /// <summary>The local name of a prefixed id or a full IRI.</summary>
        private static string LocalName(string id)
        {
            var cut = Math.Max(id.LastIndexOf('#'), Math.Max(id.LastIndexOf('/'), id.LastIndexOf(':')));
            return cut >= 0 ? id[(cut + 1)..] : id;
        }
    }
}
