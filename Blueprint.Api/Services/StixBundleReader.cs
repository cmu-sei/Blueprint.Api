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
    /// Reads a STIX 2.x bundle into a <see cref="TechniqueReleaseEntity"/>, as its framework definition says.
    /// </summary>
    /// <remarks>
    /// Techniques are attack-pattern objects. They are grouped either by the bundle's x-mitre-tactic objects through
    /// their kill chain phases, or, in a bundle with no tactics, by the top level of a pattern hierarchy. Revoked
    /// and deprecated objects, and objects with a status the definition skips, are left out. An object's id comes
    /// from the external reference the definition names; any other reference is a cross-reference.
    /// </remarks>
    public static class StixBundleReader
    {
        private const string DefaultPlatformsProperty = "x_mitre_platforms";

        /// <summary>True when the JSON is a STIX bundle with its objects.</summary>
        public static bool CanRead(JsonElement root) =>
            root.ValueKind == JsonValueKind.Object
            && GetString(root, "type") == "bundle"
            && root.TryGetProperty("objects", out var objects)
            && objects.ValueKind == JsonValueKind.Array;

        /// <summary>What a framework's match conditions are checked against.</summary>
        public sealed class Summary
        {
            public string Domain { get; init; }
            public string ReferenceSource { get; init; }
            public List<string> ReferenceIds { get; init; } = new List<string>();
            public string CollectionName { get; init; }
        }

        public static Summary Summarize(JsonElement root)
        {
            var techniques = new List<JsonElement>();
            string collectionName = null;
            foreach (var item in root.GetProperty("objects").EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                var type = GetString(item, "type");
                if (type == "x-mitre-collection")
                    collectionName ??= GetString(item, "name");
                else if (type == "attack-pattern" && !IsRetired(item))
                    techniques.Add(item);
            }

            var domain = MostCommon(techniques.Select(t => GetStrings(t, "x_mitre_domains").FirstOrDefault()));
            var firstReferences = techniques.Select(t => GetReferences(t).FirstOrDefault()).Where(r => r.ValueKind == JsonValueKind.Object).ToList();
            var source = MostCommon(firstReferences.Select(r => GetString(r, "source_name")));
            return new Summary
            {
                Domain = domain,
                ReferenceSource = source,
                ReferenceIds = firstReferences
                    .Where(r => GetString(r, "source_name") == source)
                    .Select(r => GetString(r, "external_id"))
                    .Where(id => id != null)
                    .ToList(),
                CollectionName = collectionName
            };
        }

        public static TechniqueReleaseEntity Read(JsonElement root, string version, TechniqueFrameworkEntity framework)
        {
            if (!CanRead(root))
                throw new InvalidTechniqueBundleException("The file is not a STIX bundle.");

            var options = framework.Settings?.Stix ?? new StixFormatOptions();
            var bundle = Collect(root.GetProperty("objects"), options);
            if (bundle.Techniques.Count == 0)
                throw new InvalidTechniqueBundleException("The bundle has no techniques.");

            version = string.IsNullOrWhiteSpace(version)
                ? (bundle.Collection.HasValue ? GetString(bundle.Collection.Value, "x_mitre_version") : null)
                    ?? (string.IsNullOrWhiteSpace(options.VersionProperty)
                        ? null
                        : MostCommon(bundle.Techniques.Select(t => GetString(t, options.VersionProperty))))
                : version.Trim();
            if (string.IsNullOrWhiteSpace(version))
                throw new InvalidTechniqueBundleException("The bundle does not name its version. Enter the version and upload it again.");

            var release = new TechniqueReleaseEntity
            {
                Id = TechniqueIds.ReleaseId(framework.Domain, version),
                Domain = framework.Domain,
                IsDefensive = framework.IsDefensive,
                TechniqueFrameworkId = framework.Id,
                Version = version,
                Name = (bundle.Collection.HasValue ? GetString(bundle.Collection.Value, "name") : null) ?? framework.Name,
                Description = (bundle.Collection.HasValue ? GetString(bundle.Collection.Value, "description") : null) ?? framework.Description,
                ReleaseDate = (bundle.Collection.HasValue ? GetDate(bundle.Collection.Value, "modified") : null)
                    ?? bundle.Techniques.Select(t => GetDate(t, "modified")).Where(d => d.HasValue).Max()
            };
            ApplyPublisher(release, bundle);
            TechniqueFrameworkDefaults.Apply(release, framework);

            if (options.Grouping == StixGrouping.Hierarchy)
                ReadHierarchy(release, bundle, options, framework);
            else
                ReadKillChain(release, bundle, options, framework);

            if (release.Techniques.Count == 0)
                throw new InvalidTechniqueBundleException("No technique in the bundle carries an id from the framework's id source.");

            return release;
        }

        private sealed class Bundle
        {
            public JsonElement? Collection;
            public JsonElement? Matrix;
            public readonly List<JsonElement> Tactics = new();
            public readonly List<JsonElement> Techniques = new();
            public readonly Dictionary<string, string> Statements = new();
            public readonly Dictionary<string, string> Publishers = new();
        }

        private static Bundle Collect(JsonElement objects, StixFormatOptions options)
        {
            var bundle = new Bundle();
            var skip = new HashSet<string>(options.SkipStatuses ?? [], StringComparer.OrdinalIgnoreCase);
            foreach (var item in objects.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || IsRetired(item))
                    continue;
                if (!string.IsNullOrWhiteSpace(options.StatusProperty) && GetString(item, options.StatusProperty) is { } status && skip.Contains(status))
                    continue;

                switch (GetString(item, "type"))
                {
                    case "marking-definition":
                        if (GetString(item, "definition_type") == "statement"
                            && item.TryGetProperty("definition", out var definition)
                            && GetString(definition, "statement") is { } statement
                            && !string.IsNullOrWhiteSpace(statement)
                            && GetString(item, "id") is { } markingId)
                            bundle.Statements.TryAdd(markingId, statement.Trim());
                        break;
                    case "identity":
                        if (GetString(item, "id") is { } identityId && GetString(item, "name") is { } identityName)
                            bundle.Publishers.TryAdd(identityId, identityName);
                        break;
                    case "x-mitre-collection":
                        bundle.Collection ??= item;
                        break;
                    case "x-mitre-matrix":
                        bundle.Matrix ??= item;
                        break;
                    case "x-mitre-tactic":
                        bundle.Tactics.Add(item);
                        break;
                    case "attack-pattern":
                        if (GetString(item, "id") != null)
                            bundle.Techniques.Add(item);
                        break;
                }
            }

            return bundle;
        }

        /// <summary>
        /// The publisher is the identity that created the collection (or the techniques); the notice is the statement
        /// marking them, or the bundle's only statement. Nothing the file does not say is written.
        /// </summary>
        private static void ApplyPublisher(TechniqueReleaseEntity release, Bundle bundle)
        {
            var carriers = new List<JsonElement>();
            if (bundle.Collection.HasValue)
                carriers.Add(bundle.Collection.Value);
            carriers.AddRange(bundle.Techniques);

            var creator = carriers.Select(c => GetString(c, "created_by_ref")).FirstOrDefault(id => id != null && bundle.Publishers.ContainsKey(id));
            release.Source = creator != null ? bundle.Publishers[creator] : null;

            var marking = carriers
                .SelectMany(c => GetStrings(c, "object_marking_refs"))
                .FirstOrDefault(id => id != null && bundle.Statements.ContainsKey(id));
            if (marking != null)
                release.Notice = bundle.Statements[marking];
            else if (bundle.Statements.Count == 1)
                release.Notice = bundle.Statements.Values.First();
        }

        private static void ReadKillChain(TechniqueReleaseEntity release, Bundle bundle, StixFormatOptions options, TechniqueFrameworkEntity framework)
        {
            // Without a configured source, the one most tactics share is the bundle's own catalogue.
            var source = string.IsNullOrWhiteSpace(options.IdSource)
                ? MostCommon(bundle.Tactics.SelectMany(t => GetReferences(t).Select(r => GetString(r, "source_name"))))
                : options.IdSource;
            if (string.IsNullOrWhiteSpace(source))
                throw new InvalidTechniqueBundleException("The bundle's tactics name no id source, and the framework sets none.");

            // Techniques name their tactics through kill chain phases; take the chain the bundle uses.
            var killChain = MostCommon(bundle.Techniques.SelectMany(t => GetPhases(t).Select(p => GetString(p, "kill_chain_name")))) ?? source;
            var settings = framework.Settings ?? new TechniqueFrameworkSettings();

            // The matrix lists its tactics left to right; a tactic it leaves out goes at the end.
            var order = new Dictionary<string, int>();
            if (bundle.Matrix.HasValue && bundle.Matrix.Value.TryGetProperty("tactic_refs", out var refs) && refs.ValueKind == JsonValueKind.Array)
            {
                foreach (var tacticRef in refs.EnumerateArray())
                    order.TryAdd(tacticRef.GetString() ?? "", order.Count);
            }

            foreach (var tactic in bundle.Tactics)
            {
                var (externalId, url) = GetReference(tactic, source, options.IdPrefix);
                var stixId = GetString(tactic, "id");
                if (externalId == null || stixId == null)
                    continue;

                release.Tactics.Add(new TechniqueTacticEntity
                {
                    Id = TechniqueIds.ObjectId(release.Domain, release.Version, stixId),
                    TechniqueReleaseId = release.Id,
                    StixId = stixId,
                    ExternalId = externalId,
                    ShortName = GetString(tactic, "x_mitre_shortname"),
                    Name = GetString(tactic, "name"),
                    Description = GetString(tactic, "description"),
                    Url = url ?? TechniqueFrameworkDefaults.Link(settings.TacticUrlTemplate, externalId, stixId),
                    DisplayOrder = order.TryGetValue(stixId, out var position) ? position : order.Count + release.Tactics.Count
                });
            }

            var byExternalId = new Dictionary<string, TechniqueEntity>(StringComparer.OrdinalIgnoreCase);
            var subTechniques = new List<TechniqueEntity>();
            foreach (var technique in bundle.Techniques)
            {
                var (externalId, url) = GetReference(technique, source, options.IdPrefix);
                var stixId = GetString(technique, "id");
                if (externalId == null || byExternalId.ContainsKey(externalId))
                    continue;

                var phases = GetPhases(technique)
                    .Where(p => GetString(p, "kill_chain_name") == killChain)
                    .Select(p => GetString(p, "phase_name"))
                    .Where(p => p != null);

                var entity = NewTechnique(release, technique, externalId, url, stixId, options, settings);
                entity.Tactics = string.Join(",", phases.Distinct());
                byExternalId[externalId] = entity;
                release.Techniques.Add(entity);
                if (technique.TryGetProperty("x_mitre_is_subtechnique", out var sub) && sub.ValueKind == JsonValueKind.True)
                    subTechniques.Add(entity);
            }

            // A sub-technique's id is its parent's plus a last dotted segment.
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
        }

        /// <summary>
        /// Groups a bundle with no tactics by a pattern hierarchy: patterns at the group level are the groups, a
        /// pattern with no parent below that level is a technique, and a pattern under one is a sub-technique of the
        /// topmost such pattern above it.
        /// </summary>
        private static void ReadHierarchy(TechniqueReleaseEntity release, Bundle bundle, StixFormatOptions options, TechniqueFrameworkEntity framework)
        {
            if (string.IsNullOrWhiteSpace(options.IdSource))
                throw new InvalidTechniqueBundleException("The framework groups by hierarchy but names no id source.");
            if (string.IsNullOrWhiteSpace(options.AbstractionProperty) || string.IsNullOrWhiteSpace(options.ParentRefsProperty))
                throw new InvalidTechniqueBundleException("The framework groups by hierarchy but names no abstraction or parent property.");

            var settings = framework.Settings ?? new TechniqueFrameworkSettings();
            var patterns = bundle.Techniques;
            var byStixId = new Dictionary<string, JsonElement>();
            foreach (var pattern in patterns)
                byStixId.TryAdd(GetString(pattern, "id"), pattern);

            bool IsGroup(JsonElement p) => string.Equals(GetString(p, options.AbstractionProperty), options.GroupAbstraction, StringComparison.OrdinalIgnoreCase);
            IEnumerable<JsonElement> Parents(JsonElement p) =>
                GetStrings(p, options.ParentRefsProperty)
                    .Where(id => id != null && byStixId.ContainsKey(id))
                    .Select(id => byStixId[id]);

            // The groups, alphabetically, as a hierarchy gives them no order.
            var groups = patterns
                .Where(IsGroup)
                .Select(p => (Pattern: p, Id: GetReference(p, options.IdSource, options.IdPrefix)))
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
                    Id = TechniqueIds.ObjectId(release.Domain, release.Version, stixId),
                    TechniqueReleaseId = release.Id,
                    StixId = stixId,
                    ExternalId = id.ExternalId,
                    ShortName = shortName,
                    Name = GetString(pattern, "name"),
                    Description = GetString(pattern, "description"),
                    Url = id.Url ?? TechniqueFrameworkDefaults.Link(settings.TacticUrlTemplate, id.ExternalId, stixId),
                    DisplayOrder = release.Tactics.Count
                });
            }

            // A pattern's parent is the first non-group pattern it is a child of, followed to the top.
            JsonElement? TechniqueParent(JsonElement p) =>
                Parents(p).Where(x => !IsGroup(x) && GetString(x, "id") != GetString(p, "id"))
                    .Select(x => (JsonElement?)x).FirstOrDefault();
            JsonElement Root(JsonElement p)
            {
                var seen = new HashSet<string> { GetString(p, "id") };
                var current = p;
                while (TechniqueParent(current) is { } parent && seen.Add(GetString(parent, "id")))
                    current = parent;
                return current;
            }

            // The groups above a pattern, reached through any chain of parents.
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
                        if (IsGroup(parent))
                            found.Add(id);
                        else if (seen.Add(id))
                            stack.Push(parent);
                    }
                }
                return found;
            }

            var byExternalId = new Dictionary<string, TechniqueEntity>(StringComparer.OrdinalIgnoreCase);
            var parentOf = new List<(TechniqueEntity Sub, string RootStixId)>();
            foreach (var pattern in patterns.Where(p => !IsGroup(p)))
            {
                var (externalId, url) = GetReference(pattern, options.IdSource, options.IdPrefix);
                var stixId = GetString(pattern, "id");
                if (externalId == null || byExternalId.ContainsKey(externalId))
                    continue;

                var root = Root(pattern);
                var rootStixId = GetString(root, "id");
                var memberOf = Groups(pattern);
                if (rootStixId != stixId)
                    memberOf.UnionWith(Groups(root));

                var entity = NewTechnique(release, pattern, externalId, url, stixId, options, settings);
                entity.Tactics = string.Join(",", groups
                    .Select(g => GetString(g.Pattern, "id"))
                    .Where(memberOf.Contains)
                    .Select(g => shortNames[g]));
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
        }

        private static TechniqueEntity NewTechnique(TechniqueReleaseEntity release, JsonElement technique, string externalId, string url,
            string stixId, StixFormatOptions options, TechniqueFrameworkSettings settings) =>
            new TechniqueEntity
            {
                Id = TechniqueIds.ObjectId(release.Domain, release.Version, stixId),
                TechniqueReleaseId = release.Id,
                StixId = stixId,
                ExternalId = externalId,
                Name = GetString(technique, "name"),
                Description = GetString(technique, "description"),
                Url = url ?? TechniqueFrameworkDefaults.Link(settings.TechniqueUrlTemplate, externalId, stixId),
                Platforms = string.Join(",", GetStrings(technique,
                    string.IsNullOrWhiteSpace(options.PlatformsProperty) ? DefaultPlatformsProperty : options.PlatformsProperty)),
                Weaknesses = string.IsNullOrWhiteSpace(options.WeaknessSource)
                    ? null
                    : string.Join(",", GetReferences(technique)
                        .Where(r => GetString(r, "source_name") == options.WeaknessSource)
                        .Select(r => GetString(r, "external_id"))
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct()),
                ReferenceLinks = Links(technique, options.LinkSources)
            };

        /// <summary>The technique's references to other catalogues, as a JSON array of links; null when it has none.</summary>
        private static string Links(JsonElement technique, List<TechniqueLinkSource> sources)
        {
            if (sources == null || sources.Count == 0)
                return null;

            var links = new List<ViewModels.TechniqueLink>();
            foreach (var reference in GetReferences(technique))
            {
                var url = TechniqueFrameworkDefaults.WebAddress(GetString(reference, "url"));
                var source = sources.FirstOrDefault(s => s.Source == GetString(reference, "source_name"));
                if (source == null || string.IsNullOrWhiteSpace(url))
                    continue;

                var text = GetString(reference, "description");
                var id = GetString(reference, "external_id");
                if (!string.IsNullOrWhiteSpace(source.IdLabelPrefix) && !string.IsNullOrWhiteSpace(id))
                    text = $"{source.IdLabelPrefix}{id} {text}";
                var link = new ViewModels.TechniqueLink { Source = source.Label ?? source.Source, Label = text?.Trim(), Url = url.Trim() };
                if (!links.Any(l => l.Source == link.Source && l.Label == link.Label && l.Url == link.Url))
                    links.Add(link);
            }

            return links.Count == 0 ? null : JsonSerializer.Serialize(links, JsonSerializerOptions.Web);
        }

        private static string MostCommon(IEnumerable<string> values) =>
            values
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .GroupBy(v => v)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();

        private static bool IsRetired(JsonElement item) =>
            (item.TryGetProperty("revoked", out var revoked) && revoked.ValueKind == JsonValueKind.True)
            || (item.TryGetProperty("x_mitre_deprecated", out var deprecated) && deprecated.ValueKind == JsonValueKind.True);

        private static (string ExternalId, string Url) GetReference(JsonElement item, string source, string prefix)
        {
            foreach (var reference in GetReferences(item))
            {
                var id = GetString(reference, "external_id");
                if (GetString(reference, "source_name") == source && id != null
                    && (string.IsNullOrEmpty(prefix) || id.StartsWith(prefix, StringComparison.Ordinal)))
                    return (id, TechniqueFrameworkDefaults.WebAddress(GetString(reference, "url")));
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
