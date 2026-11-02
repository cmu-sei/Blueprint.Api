// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Exceptions;

namespace Blueprint.Api.Services
{
    /// <summary>
    /// Reads an uploaded technique framework release, whichever of the two file formats it is in: a STIX 2.x
    /// bundle (ATT&amp;CK, ATLAS) goes to <see cref="AttackStixParser"/>, the D3FEND JSON-LD ontology to
    /// <see cref="D3fendOntologyParser"/>.
    /// </summary>
    public static class ReleaseFileParser
    {
        public static async Task<AttackReleaseEntity> ParseAsync(Stream stream, string version, CancellationToken ct)
        {
            using var document = await ReadAsync(stream, ct);
            var root = document.RootElement;
            if (D3fendOntologyParser.CanParse(root))
                return D3fendOntologyParser.Parse(root, version);
            if (AttackStixParser.CanParse(root))
                return AttackStixParser.Parse(root, version);
            if (D3fendMappingsParser.LooksLikeSparqlResults(root))
                throw new InvalidAttackBundleException(
                    "The file is the D3FEND mappings, not a release. Import the D3FEND ontology (d3fend.json) first, then import the mappings onto that release.");

            throw new InvalidAttackBundleException(
                "The file is neither a STIX bundle nor the D3FEND ontology. Upload a release bundle such as enterprise-attack.json or stix-atlas.json, or d3fend.json.");
        }

        /// <summary>The file as JSON, or a 400 naming what is wrong with it.</summary>
        public static async Task<JsonDocument> ReadAsync(Stream stream, CancellationToken ct)
        {
            try
            {
                return await JsonDocument.ParseAsync(stream, default, ct);
            }
            catch (JsonException ex)
            {
                throw new InvalidAttackBundleException($"The file is not valid JSON: {ex.Message}");
            }
        }
    }
}
