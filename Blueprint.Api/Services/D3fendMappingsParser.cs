// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Infrastructure.Exceptions;

namespace Blueprint.Api.Services
{
    /// <summary>
    /// Reads d3fend-full-mappings.json (SPARQL results JSON), the "countered by" mappings between offensive and
    /// defensive techniques, into one row per (offensive id, defensive class).
    /// </summary>
    /// <remarks>
    /// The file is SPARQL results JSON: a <c>head.vars</c> list and a <c>results.bindings</c> array of rows, each
    /// row a map of variable name to <c>{"type", "value"}</c>. It is about 60 MB, with each pair repeated once per
    /// artifact path, so it is read a buffer at a time through a <see cref="Utf8JsonReader"/> and never held whole.
    /// A pair's weight is its number of distinct artifact paths and its rationale is the most common path.
    /// </remarks>
    public static class D3fendMappingsParser
    {
        private const int InitialBufferSize = 256 * 1024;

        /// <summary>One aggregated "countered by" pair.</summary>
        public sealed class Pair
        {
            public string OffensiveId { get; init; }
            /// <summary>The class's local name, such as "CredentialHardening".</summary>
            public string DefensiveClass { get; init; }
            public string DefensiveLabel { get; init; }
            public string Tactic { get; init; }
            public string Rationale { get; init; }
            public int Weight { get; init; }
        }

        public sealed class Result
        {
            public int RowsRead { get; init; }
            public List<Pair> Pairs { get; init; } = new List<Pair>();
        }

        /// <summary>True when the JSON is SPARQL results (a <c>head</c> and a <c>results</c>).</summary>
        public static bool LooksLikeSparqlResults(JsonElement root) =>
            root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("head", out var head) && head.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Object;

        public static async Task<Result> ParseAsync(Stream stream, CancellationToken ct)
        {
            var cursor = new Cursor();
            var state = new JsonReaderState(new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var buffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
            var filled = 0;
            try
            {
                while (true)
                {
                    if (filled == buffer.Length)
                    {
                        // a token is longer than the buffer: make room for it
                        var larger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                        Buffer.BlockCopy(buffer, 0, larger, 0, filled);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = larger;
                    }

                    var read = await stream.ReadAsync(buffer.AsMemory(filled), ct);
                    var isFinal = read == 0;
                    filled += read;

                    var consumed = Consume(buffer.AsSpan(0, filled), isFinal, ref state, cursor);
                    if (isFinal)
                        break;

                    if (consumed > 0)
                    {
                        Buffer.BlockCopy(buffer, consumed, buffer, 0, filled - consumed);
                        filled -= consumed;
                    }
                }
            }
            catch (JsonException ex)
            {
                throw new InvalidTechniqueBundleException($"The file is not valid JSON: {ex.Message}");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return cursor.Finish();
        }

        /// <summary>
        /// Reads as many whole tokens as the buffer holds and says how many bytes they took. A row is only taken when
        /// all of it is there; otherwise the reader goes back in front of it for the next call.
        /// </summary>
        private static int Consume(ReadOnlySpan<byte> data, bool isFinal, ref JsonReaderState state, Cursor cursor)
        {
            var reader = new Utf8JsonReader(data, isFinal, state);
            while (true)
            {
                var before = reader;
                if (!reader.Read())
                    break;

                if (cursor.InBindings)
                {
                    if (reader.TokenType == JsonTokenType.EndArray)
                    {
                        cursor.InBindings = false;
                        continue;
                    }
                    if (reader.TokenType != JsonTokenType.StartObject)
                        throw new InvalidTechniqueBundleException("The file is not the D3FEND mappings: results.bindings holds something other than result rows.");

                    var start = (int)reader.TokenStartIndex;
                    if (!reader.TrySkip())
                    {
                        reader = before;
                        break;
                    }
                    cursor.AddRow(data.Slice(start, (int)reader.BytesConsumed - start));
                    continue;
                }

                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName when reader.CurrentDepth == 1:
                        cursor.TopLevel = reader.GetString();
                        cursor.Second = null;
                        break;
                    case JsonTokenType.PropertyName when reader.CurrentDepth == 2:
                        cursor.Second = reader.GetString();
                        break;
                    case JsonTokenType.StartArray when reader.CurrentDepth == 2 && cursor.TopLevel == "results" && cursor.Second == "bindings":
                        cursor.InBindings = true;
                        cursor.SawBindings = true;
                        break;
                    case JsonTokenType.StartArray when reader.CurrentDepth == 2 && cursor.TopLevel == "head" && cursor.Second == "vars":
                        var start = (int)reader.TokenStartIndex;
                        if (!reader.TrySkip())
                        {
                            reader = before;
                            goto done;
                        }
                        cursor.SetVars(data.Slice(start, (int)reader.BytesConsumed - start));
                        break;
                }
            }

        done:
            state = reader.CurrentState;
            return (int)reader.BytesConsumed;
        }

        /// <summary>The parse state across buffers, and the pairs aggregated so far.</summary>
        private sealed class Cursor
        {
            private static readonly string[] RequiredVars = ["off_tech_id", "def_tech"];

            public string TopLevel;
            public string Second;
            public bool InBindings;
            public bool SawBindings;
            private List<string> _vars;
            private int _rowsRead;
            private readonly Dictionary<(string Off, string Def), Accumulator> _pairs = new();

            public void SetVars(ReadOnlySpan<byte> json)
            {
                using var document = JsonDocument.Parse(json.ToArray());
                _vars = document.RootElement.EnumerateArray()
                    .Where(v => v.ValueKind == JsonValueKind.String)
                    .Select(v => v.GetString())
                    .ToList();
            }

            public void AddRow(ReadOnlySpan<byte> json)
            {
                _rowsRead++;
                var row = Row.Read(json);
                if (string.IsNullOrEmpty(row.OffensiveId) || string.IsNullOrEmpty(row.DefensiveClass))
                    return;

                var key = (row.OffensiveId, row.DefensiveClass);
                if (!_pairs.TryGetValue(key, out var pair))
                {
                    pair = new Accumulator { Order = _pairs.Count, DefensiveLabel = row.DefensiveLabel };
                    _pairs[key] = pair;
                }
                pair.DefensiveLabel ??= row.DefensiveLabel;
                if (row.Tactic != null)
                    pair.Tactics[row.Tactic] = pair.Tactics.GetValueOrDefault(row.Tactic) + 1;
                var path = new ArtifactPath(row.DefensiveRelation, row.DefensiveArtifact, row.OffensiveRelation, row.OffensiveArtifact);
                pair.Paths[path] = pair.Paths.GetValueOrDefault(path) + 1;
            }

            public Result Finish()
            {
                if (!SawBindings)
                    throw new InvalidTechniqueBundleException(
                        "The file is not the D3FEND mappings. Upload d3fend-full-mappings.json for the release, from https://d3fend.mitre.org/ontologies/d3fend/<version>/d3fend-full-mappings.json.");
                if (_vars != null && RequiredVars.Any(v => !_vars.Contains(v)))
                    throw new InvalidTechniqueBundleException(
                        "The file is SPARQL results, but not the D3FEND mappings: its variables lack off_tech_id and def_tech.");
                if (_rowsRead == 0)
                    throw new InvalidTechniqueBundleException("The mappings file has no result rows.");
                if (_pairs.Count == 0)
                    throw new InvalidTechniqueBundleException("No row in the file names an offensive technique id and a defensive technique.");

                var pairs = _pairs
                    .OrderBy(p => p.Value.Order)
                    .Select(p => new Pair
                    {
                        OffensiveId = p.Key.Off,
                        DefensiveClass = p.Key.Def,
                        DefensiveLabel = p.Value.DefensiveLabel,
                        Tactic = MostCommon(p.Value.Tactics),
                        Rationale = Rationale(p.Key.Off, MostCommon(p.Value.Paths)),
                        Weight = p.Value.Paths.Count
                    })
                    .ToList();
                return new Result { RowsRead = _rowsRead, Pairs = pairs };
            }

            /// <summary>The key seen most often; the first seen on a tie.</summary>
            private static T MostCommon<T>(Dictionary<T, int> counts)
            {
                var best = default(T);
                var bestCount = 0;
                foreach (var (key, count) in counts)
                {
                    if (count > bestCount)
                    {
                        best = key;
                        bestCount = count;
                    }
                }
                return best;
            }

            /// <summary>The path as the file words it, or as much of it as the row gave.</summary>
            private static string Rationale(string offensiveId, ArtifactPath path)
            {
                var defensive = Join(Humanize(path.DefensiveRelation), path.DefensiveArtifact);
                var offensive = Join(Humanize(path.OffensiveRelation), path.OffensiveArtifact);
                if (offensive != null)
                    offensive = $"{offensiveId} {offensive}";
                return defensive != null && offensive != null ? $"{defensive}; {offensive}" : defensive ?? offensive;
            }

            private static string Join(string a, string b) =>
                string.IsNullOrEmpty(a) ? (string.IsNullOrEmpty(b) ? null : b) : string.IsNullOrEmpty(b) ? a : $"{a} {b}";

            /// <summary>A hyphenated relation reads as words.</summary>
            private static string Humanize(string relation) => relation?.Replace('-', ' ');
        }

        private sealed class Accumulator
        {
            public int Order;
            public string DefensiveLabel;
            public readonly Dictionary<string, int> Tactics = new();
            public readonly Dictionary<ArtifactPath, int> Paths = new();
        }

        private readonly record struct ArtifactPath(string DefensiveRelation, string DefensiveArtifact, string OffensiveRelation, string OffensiveArtifact);

        /// <summary>The variables of one result row the mapping needs.</summary>
        private struct Row
        {
            public string OffensiveId;
            public string DefensiveClass;
            public string DefensiveLabel;
            public string Tactic;
            public string DefensiveRelation;
            public string DefensiveArtifact;
            public string OffensiveRelation;
            public string OffensiveArtifact;

            /// <summary>Reads a row, taking each variable's value.</summary>
            public static Row Read(ReadOnlySpan<byte> json)
            {
                var row = new Row();
                var reader = new Utf8JsonReader(json, isFinalBlock: true, state: default);
                reader.Read(); // StartObject
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    // the variable's name, then its binding
                    var name = reader.TokenType == JsonTokenType.PropertyName ? reader.GetString() : null;
                    reader.Read();
                    var value = BindingValue(ref reader);
                    switch (name)
                    {
                        case "off_tech_id": row.OffensiveId = value?.Trim(); break;
                        case "def_tech": row.DefensiveClass = LocalName(value); break;
                        case "def_tech_label": row.DefensiveLabel = value; break;
                        case "def_tactic_label": row.Tactic = value; break;
                        case "def_artifact_rel_label": row.DefensiveRelation = value; break;
                        case "def_artifact_label": row.DefensiveArtifact = value; break;
                        case "off_artifact_rel_label": row.OffensiveRelation = value; break;
                        case "off_artifact_label": row.OffensiveArtifact = value; break;
                    }
                }
                return row;
            }

            /// <summary>The <c>value</c> of a binding; a bare string is taken as is, anything else skipped.</summary>
            private static string BindingValue(ref Utf8JsonReader reader)
            {
                if (reader.TokenType == JsonTokenType.String)
                    return reader.GetString();
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.TrySkip();
                    return null;
                }

                string value = null;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var isValue = reader.ValueTextEquals("value"u8);
                    reader.Read();
                    if (isValue && reader.TokenType == JsonTokenType.String)
                        value = reader.GetString();
                    else
                        reader.TrySkip();
                }
                return value;
            }

            /// <summary>The class's local name from a full IRI or a prefixed one.</summary>
            private static string LocalName(string iri)
            {
                if (string.IsNullOrEmpty(iri))
                    return null;
                var cut = Math.Max(iri.LastIndexOf('#'), Math.Max(iri.LastIndexOf('/'), iri.LastIndexOf(':')));
                var name = cut >= 0 ? iri[(cut + 1)..] : iri;
                return name.Length == 0 ? null : name;
            }
        }
    }
}
