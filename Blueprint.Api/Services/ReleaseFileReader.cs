// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Exceptions;

namespace Blueprint.Api.Services
{
    /// <summary>
    /// Reads an uploaded release: finds its file format, the framework whose match conditions it meets, and reads it
    /// as that framework's definition says.
    /// </summary>
    public static class ReleaseFileReader
    {
        public const string NoFrameworkMessage = "No technique framework matches this file. Add or import a framework definition first.";

        public static async Task<TechniqueReleaseEntity> ReadAsync(Stream stream, string version, IReadOnlyCollection<TechniqueFrameworkEntity> frameworks,
            Guid? frameworkId, CancellationToken ct)
        {
            using var document = await ReadJsonAsync(stream, ct);
            return Read(document.RootElement, version, frameworks, frameworkId);
        }

        public static TechniqueReleaseEntity Read(JsonElement root, string version, IReadOnlyCollection<TechniqueFrameworkEntity> frameworks, Guid? frameworkId = null)
        {
            var format = FormatOf(root);
            if (format == null)
            {
                if (SparqlMappingReader.LooksLikeSparqlResults(root))
                    throw new InvalidTechniqueBundleException(
                        "The file is a mappings file, not a release. Import the release first, then import the mappings onto it.");
                throw new InvalidTechniqueBundleException("The file is neither a STIX bundle nor a JSON-LD ontology.");
            }

            var framework = Choose(root, format.Value, frameworks, frameworkId);
            return framework.Format == TechniqueFileFormat.StixBundle
                ? StixBundleReader.Read(root, version, framework)
                : JsonLdOntologyReader.Read(root, version, framework);
        }

        public static TechniqueFileFormat? FormatOf(JsonElement root) =>
            StixBundleReader.CanRead(root) ? TechniqueFileFormat.StixBundle
            : JsonLdOntologyReader.CanRead(root) ? TechniqueFileFormat.JsonLdOntology
            : null;

        /// <summary>
        /// The framework the file is a release of: the one asked for, which must be of the file's format, or the only
        /// one whose conditions it meets.
        /// </summary>
        private static TechniqueFrameworkEntity Choose(JsonElement root, TechniqueFileFormat format, IReadOnlyCollection<TechniqueFrameworkEntity> frameworks, Guid? frameworkId)
        {
            if (frameworkId.HasValue)
            {
                var chosen = frameworks.FirstOrDefault(f => f.Id == frameworkId.Value)
                    ?? throw new EntityNotFoundException<ViewModels.TechniqueFramework>();
                if (chosen.Format != format)
                    throw new InvalidTechniqueBundleException($"The file is a {Describe(format)}, but {chosen.Name} releases are a {Describe(chosen.Format)}.");
                return chosen;
            }

            var matches = Matching(root, format, frameworks);
            return matches.Count switch
            {
                0 => throw new InvalidTechniqueBundleException(NoFrameworkMessage),
                1 => matches[0],
                _ => throw new InvalidTechniqueBundleException(
                    $"The file matches more than one technique framework: {string.Join(", ", matches.Select(f => f.Name).Order())}. Choose the framework to import it as.")
            };
        }

        /// <summary>The frameworks of the file's format whose every match condition the file meets.</summary>
        public static List<TechniqueFrameworkEntity> Matching(JsonElement root, TechniqueFileFormat format, IEnumerable<TechniqueFrameworkEntity> frameworks)
        {
            var candidates = frameworks.Where(f => f.Format == format && !(f.Settings?.Match ?? new TechniqueFrameworkMatch()).IsEmpty).ToList();
            if (candidates.Count == 0)
                return candidates;

            if (format == TechniqueFileFormat.StixBundle)
            {
                // each definition names the types and properties its conditions are read from
                return candidates.Where(f => Meets(f.Settings.Match, StixBundleReader.Summarize(root, f.Settings.Stix))).ToList();
            }

            var ontology = JsonLdOntologyReader.Summarize(root);
            return candidates.Where(f => Meets(f.Settings.Match, ontology)).ToList();
        }

        private static bool Meets(TechniqueFrameworkMatch match, StixBundleReader.Summary summary) =>
            !Given(match.Property) && !Given(match.OntologyTitleContains)
            && (!Given(match.StixDomain) || summary.Domain == match.StixDomain)
            && (!Given(match.ReferenceSource) || summary.ReferenceSource == match.ReferenceSource)
            && (!Given(match.IdPrefix) || summary.ReferenceIds.Any(id => id.StartsWith(match.IdPrefix, StringComparison.Ordinal)))
            && (!Given(match.CollectionName) || summary.CollectionName == match.CollectionName);

        private static bool Meets(TechniqueFrameworkMatch match, JsonLdOntologyReader.Summary summary) =>
            !Given(match.StixDomain) && !Given(match.ReferenceSource) && !Given(match.IdPrefix) && !Given(match.CollectionName)
            && (!Given(match.Property) || summary.Properties.Contains(match.Property))
            && (!Given(match.OntologyTitleContains) || (summary.Title ?? "").Contains(match.OntologyTitleContains, StringComparison.OrdinalIgnoreCase));

        private static bool Given(string value) => !string.IsNullOrWhiteSpace(value);

        private static string Describe(TechniqueFileFormat format) =>
            format == TechniqueFileFormat.StixBundle ? "STIX bundle" : "JSON-LD ontology";

        /// <summary>The file as JSON, or a 400 naming what is wrong with it.</summary>
        public static async Task<JsonDocument> ReadJsonAsync(Stream stream, CancellationToken ct)
        {
            try
            {
                return await JsonDocument.ParseAsync(stream, default, ct);
            }
            catch (JsonException ex)
            {
                throw new InvalidTechniqueBundleException($"The file is not valid JSON: {ex.Message}");
            }
        }
    }
}
