// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Blueprint.Api.Tests.Support;

/// <summary>
/// A hand-made d3fend-full-mappings.json: the SPARQL results JSON shape (<c>head.vars</c> and
/// <c>results.bindings</c>, each row a map of variable to <c>{"type", "value"}</c>) with the variable names the real
/// file uses, cut down to a few offensive techniques against the techniques of <see cref="D3fendOntologies"/>.
/// </summary>
public static class D3fendMappings
{
    private const string D3f = "http://d3fend.mitre.org/ontologies/d3fend.owl#";

    /// <summary>
    /// Nine rows, six pairs. T1003.005 is countered by Credential Hardening along three distinct artifact paths,
    /// one of them listed twice (once from the Token Binding query, once from Credential Hardening's own), by
    /// Certificate-based Authentication along one, and by Network Traffic Filtering, a class the ontology fixture
    /// does not have. Its parent T1003 has a row of its own; T1059 has one and its sub-technique T1059.001 none;
    /// T0800 is an ICS technique. The relations are the file's, the pairs invented.
    /// </summary>
    public static string Standard() => File(
        Row("T1003.005", "Cached Domain Credentials", "T1003", "enterprise", "CredentialHardening", "Credential Hardening", "Harden",
            "hardens", "Credential", "accesses", "Encrypted Credential", query: "Token Binding"),
        Row("T1003.005", "Cached Domain Credentials", "T1003", "enterprise", "CredentialHardening", "Credential Hardening", "Harden",
            "hardens", "Credential", "accesses", "Encrypted Credential"),
        Row("T1003.005", "Cached Domain Credentials", "T1003", "enterprise", "CredentialHardening", "Credential Hardening", "Harden",
            "hardens", "Credential", "accesses", "Credential"),
        Row("T1003.005", "Cached Domain Credentials", "T1003", "enterprise", "CredentialHardening", "Credential Hardening", "Harden",
            "encrypts", "Credential", "accesses", "Encrypted Credential"),
        Row("T1003.005", "Cached Domain Credentials", "T1003", "enterprise", "CertificateBasedAuthentication", "Certificate-based Authentication", "Harden",
            "authenticates", "User Account", "accesses", "Encrypted Credential"),
        Row("T1003.005", "Cached Domain Credentials", "T1003", "enterprise", "NetworkTrafficFiltering", "Network Traffic Filtering", "Isolate",
            "filters", "Network Traffic", "produces", "Network Traffic"),
        Row("T1003", "OS Credential Dumping", null, "enterprise", "CredentialHardening", "Credential Hardening", "Harden",
            "hardens", "Credential", "may-access", "Credential"),
        Row("T1059", "Command and Scripting Interpreter", null, "enterprise", "FileAnalysis", "File Analysis", "Detect",
            "analyzes", "File", "executes", "Executable Script"),
        Row("T0800", "Activate Firmware Update Mode", null, "ics", "FileAnalysis", "File Analysis", "Detect",
            "analyzes", "File", "modifies", "Firmware"));

    /// <summary>SPARQL results whose variables are not the mappings file's.</summary>
    public static string OtherSparqlResults() => """
        {
          "head": { "vars": [ "s", "p", "o" ] },
          "results": { "bindings": [ { "s": { "type": "uri", "value": "http://example.org/a" }, "p": { "type": "uri", "value": "http://example.org/b" }, "o": { "type": "literal", "value": "c" } } ] }
        }
        """;

    /// <summary>The file around the given rows.</summary>
    public static string File(params string[] rows) => $$"""
        {
          "head": {
            "vars": [ "query_def_tech_label", "top_def_tech_label", "def_tactic_label", "def_tactic_rel_label", "def_tech_label", "def_artifact_rel_label", "def_artifact_label", "off_artifact_label", "off_artifact_rel_label", "off_tech_label", "off_tech_id", "framework_root_iri", "off_tech_parent_label", "off_tech_parent_is_toplevel", "off_tactic_rel_label", "off_tactic_label", "def_tactic", "def_tactic_rel", "def_tech", "def_artifact_rel", "def_artifact", "off_artifact", "off_artifact_rel", "off_tech", "off_tech_parent", "off_tactic_rel", "off_tactic" ]
          },
          "results": {
            "bindings": [
        {{string.Join(",\n", rows)}}
            ]
          }
        }
        """;

    /// <summary>
    /// One result row. <paramref name="query"/> is the technique whose query produced the row (a sub-technique
    /// inherits its parent's mappings, so the same pair comes out once per descendant); it defaults to the
    /// defensive technique itself. <paramref name="padding"/> adds a long unrelated value, for rows longer than a
    /// read buffer.
    /// </summary>
    public static string Row(
        string offensiveId, string offensiveLabel, string parentId, string framework,
        string defensiveClass, string defensiveLabel, string tactic,
        string defensiveRelation, string defensiveArtifact, string offensiveRelation, string offensiveArtifact,
        string query = null, string padding = null)
    {
        var bindings = new List<KeyValuePair<string, (string Type, string Value)>>
        {
            Binding("query_def_tech_label", "literal", query ?? defensiveLabel),
            Binding("top_def_tech_label", "literal", defensiveLabel),
            Binding("def_tactic_label", "literal", tactic),
            Binding("def_tactic_rel_label", "literal", "enables"),
            Binding("def_tech_label", "literal", defensiveLabel),
            Binding("def_artifact_rel_label", "literal", defensiveRelation),
            Binding("def_artifact_label", "literal", defensiveArtifact),
            Binding("off_artifact_label", "literal", offensiveArtifact),
            Binding("off_artifact_rel_label", "literal", offensiveRelation),
            Binding("off_tech_label", "literal", offensiveLabel),
            Binding("off_tech_id", "literal", offensiveId),
            Binding("framework_root_iri", "uri", D3f + (framework == "ics" ? "ATTACKICSTechnique" : "ATTACKEnterpriseTechnique")),
            Binding("off_tactic_rel_label", "literal", "enables"),
            Binding("off_tactic_label", "literal", "Credential Access"),
            Binding("def_tactic", "uri", D3f + tactic),
            Binding("def_tactic_rel", "uri", D3f + "enables"),
            Binding("def_tech", "uri", D3f + defensiveClass),
            Binding("def_artifact_rel", "uri", D3f + defensiveRelation),
            Binding("def_artifact", "uri", D3f + defensiveArtifact.Replace(" ", "")),
            Binding("off_artifact", "uri", D3f + offensiveArtifact.Replace(" ", "")),
            Binding("off_artifact_rel", "uri", D3f + offensiveRelation),
            Binding("off_tech", "uri", D3f + offensiveId),
            Binding("off_tactic_rel", "uri", D3f + "enables"),
            Binding("off_tactic", "uri", D3f + "TA0006"),
            Binding("framework_key", "literal", framework),
            Binding("framework_label", "literal", framework == "ics" ? "ICS" : "Enterprise"),
        };
        if (parentId != null)
        {
            bindings.Add(Binding("off_tech_parent_label", "literal", "Parent of " + offensiveId));
            bindings.Add(Binding("off_tech_parent", "uri", D3f + parentId));
        }
        if (padding != null)
            bindings.Add(Binding("off_tech_comment", "literal", padding));

        return "{ " + string.Join(", ", bindings.Select(b =>
            $"{JsonSerializer.Serialize(b.Key)}: {{ \"type\": \"{b.Value.Type}\", \"value\": {JsonSerializer.Serialize(b.Value.Value)} }}")) + " }";
    }

    private static KeyValuePair<string, (string, string)> Binding(string name, string type, string value) => new(name, (type, value));
}
