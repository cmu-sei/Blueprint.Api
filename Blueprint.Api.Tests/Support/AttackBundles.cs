// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Blueprint.Api.Tests.Support;

/// <summary>
/// Hand-made ATT&amp;CK STIX bundles: the shape of enterprise-attack.json, cut down to two tactics, a technique
/// with a sub-technique, a revoked technique and a deprecated one; and the shape of stix-atlas.json, cut down
/// the same way.
/// </summary>
public static class AttackBundles
{
    /// <summary>
    /// The ATLAS bundle keeps the ATT&amp;CK data model but is its own catalogue: "mitre-atlas" references and
    /// kill chain, AML.* ids, atlas.mitre.org URLs. Its first tactic and its first technique also cite the
    /// ATT&amp;CK objects they are adapted from, as the real bundle does, so a test can show those are ignored.
    /// </summary>
    public static string Atlas(string version = "2026.10") => $$"""
        {
          "type": "bundle",
          "id": "bundle--00000000-0000-4000-8000-000000000101",
          "objects": [
            {
              "type": "x-mitre-collection",
              "spec_version": "2.1",
              "id": "x-mitre-collection--00000000-0000-4000-8000-000000000102",
              "name": "ATLAS",
              "description": "Test ATLAS collection",
              "x_mitre_version": "{{version}}",
              "modified": "2026-10-28T00:00:00Z"
            },
            {
              "type": "x-mitre-matrix",
              "id": "x-mitre-matrix--00000000-0000-4000-8000-000000000103",
              "name": "ATLAS",
              "tactic_refs": [
                "x-mitre-tactic--00000000-0000-4000-8000-000000000110",
                "x-mitre-tactic--00000000-0000-4000-8000-000000000111"
              ]
            },
            {
              "type": "x-mitre-tactic",
              "id": "x-mitre-tactic--00000000-0000-4000-8000-000000000110",
              "name": "Reconnaissance",
              "x_mitre_shortname": "reconnaissance",
              "x_mitre_domains": [ "ATLAS" ],
              "external_references": [
                { "source_name": "mitre-atlas", "external_id": "AML.TA0002", "url": "https://atlas.mitre.org/tactics/AML.TA0002" },
                { "source_name": "mitre-attack", "external_id": "TA0043", "url": "https://attack.mitre.org/tactics/TA0043" }
              ]
            },
            {
              "type": "x-mitre-tactic",
              "id": "x-mitre-tactic--00000000-0000-4000-8000-000000000111",
              "name": "AI Model Access",
              "x_mitre_shortname": "ai-model-access",
              "x_mitre_domains": [ "ATLAS" ],
              "external_references": [
                { "source_name": "mitre-atlas", "external_id": "AML.TA0000", "url": "https://atlas.mitre.org/tactics/AML.TA0000" }
              ]
            },
            {
              "type": "attack-pattern",
              "id": "attack-pattern--00000000-0000-4000-8000-000000000120",
              "name": "Search Open Technical Databases",
              "description": "Adversaries may search public sources for details of the target's AI systems.",
              "x_mitre_domains": [ "ATLAS" ],
              "x_mitre_platforms": [ "Agentic AI", "Generative AI" ],
              "kill_chain_phases": [ { "kill_chain_name": "mitre-atlas", "phase_name": "reconnaissance" } ],
              "external_references": [
                { "source_name": "mitre-atlas", "external_id": "AML.T0000", "url": "https://atlas.mitre.org/techniques/AML.T0000" },
                { "source_name": "mitre-attack", "external_id": "T1596", "url": "https://attack.mitre.org/techniques/T1596/" }
              ]
            },
            {
              "type": "attack-pattern",
              "id": "attack-pattern--00000000-0000-4000-8000-000000000121",
              "name": "Journals and Conference Proceedings",
              "description": "Adversaries may read published research on the target's AI systems.",
              "x_mitre_is_subtechnique": true,
              "x_mitre_domains": [ "ATLAS" ],
              "x_mitre_platforms": [ "Generative AI" ],
              "kill_chain_phases": [ { "kill_chain_name": "mitre-atlas", "phase_name": "reconnaissance" } ],
              "external_references": [
                { "source_name": "mitre-atlas", "external_id": "AML.T0000.000", "url": "https://atlas.mitre.org/techniques/AML.T0000.000" }
              ]
            },
            {
              "type": "attack-pattern",
              "id": "attack-pattern--00000000-0000-4000-8000-000000000122",
              "name": "AI Model Inference API Access",
              "description": "Adversaries may use a model's public inference API.",
              "x_mitre_domains": [ "ATLAS" ],
              "x_mitre_platforms": [ "Predictive AI", "Enterprise" ],
              "kill_chain_phases": [ { "kill_chain_name": "mitre-atlas", "phase_name": "ai-model-access" } ],
              "external_references": [
                { "source_name": "mitre-atlas", "external_id": "AML.T0040", "url": "https://atlas.mitre.org/techniques/AML.T0040" }
              ]
            },
            {
              "type": "relationship",
              "id": "relationship--00000000-0000-4000-8000-000000000130",
              "relationship_type": "subtechnique-of",
              "source_ref": "attack-pattern--00000000-0000-4000-8000-000000000121",
              "target_ref": "attack-pattern--00000000-0000-4000-8000-000000000120"
            },
            {
              "type": "course-of-action",
              "id": "course-of-action--00000000-0000-4000-8000-000000000140",
              "name": "Limit Public Release of Information",
              "external_references": [ { "source_name": "mitre-atlas", "external_id": "AML.M0000" } ]
            }
          ]
        }
        """;

    /// <summary>Tactics whose references name no source: nothing in the bundle can be identified.</summary>
    public static string WithoutASource() => """
        {
          "type": "bundle",
          "id": "bundle--00000000-0000-4000-8000-000000000201",
          "objects": [
            {
              "type": "x-mitre-tactic",
              "id": "x-mitre-tactic--00000000-0000-4000-8000-000000000210",
              "name": "Execution",
              "x_mitre_shortname": "execution",
              "external_references": [ { "external_id": "TA0002" } ]
            },
            {
              "type": "attack-pattern",
              "id": "attack-pattern--00000000-0000-4000-8000-000000000220",
              "name": "Command and Scripting Interpreter",
              "external_references": [ { "external_id": "T1059" } ]
            }
          ]
        }
        """;

    /// <summary>
    /// The matrix lists Persistence before Execution, the reverse of the bundle's object order, so a test can
    /// tell matrix order from file order. T1003 is gone in <paramref name="version"/> "2.0".
    /// </summary>
    public static string Enterprise(string version = "1.0", bool includeVersion = true) => $$"""
        {
          "type": "bundle",
          "id": "bundle--00000000-0000-4000-8000-000000000001",
          "objects": [
            {
              "type": "x-mitre-collection",
              "id": "x-mitre-collection--00000000-0000-4000-8000-000000000002",
              "name": "Enterprise ATT&CK",
              "description": "Test enterprise collection",
              {{(includeVersion ? $"\"x_mitre_version\": \"{version}\"," : "")}}
              "modified": "2025-04-15T00:00:00.000Z"
            },
            {
              "type": "x-mitre-matrix",
              "id": "x-mitre-matrix--00000000-0000-4000-8000-000000000003",
              "name": "Enterprise ATT&CK",
              "tactic_refs": [
                "x-mitre-tactic--00000000-0000-4000-8000-000000000011",
                "x-mitre-tactic--00000000-0000-4000-8000-000000000010"
              ]
            },
            {
              "type": "x-mitre-tactic",
              "id": "x-mitre-tactic--00000000-0000-4000-8000-000000000010",
              "name": "Execution",
              "x_mitre_shortname": "execution",
              "external_references": [
                { "source_name": "mitre-attack", "external_id": "TA0002", "url": "https://attack.mitre.org/tactics/TA0002" }
              ]
            },
            {
              "type": "x-mitre-tactic",
              "id": "x-mitre-tactic--00000000-0000-4000-8000-000000000011",
              "name": "Persistence",
              "x_mitre_shortname": "persistence",
              "external_references": [
                { "source_name": "mitre-attack", "external_id": "TA0003", "url": "https://attack.mitre.org/tactics/TA0003" }
              ]
            },
            {
              "type": "attack-pattern",
              "id": "attack-pattern--00000000-0000-4000-8000-000000000020",
              "name": "Command and Scripting Interpreter",
              "description": "Adversaries may abuse command and script interpreters.",
              "x_mitre_domains": [ "enterprise-attack" ],
              "x_mitre_platforms": [ "Linux", "Windows" ],
              "kill_chain_phases": [ { "kill_chain_name": "mitre-attack", "phase_name": "execution" } ],
              "external_references": [
                { "source_name": "mitre-attack", "external_id": "T1059", "url": "https://attack.mitre.org/techniques/T1059" },
                { "source_name": "capec", "external_id": "CAPEC-1" }
              ]
            },
            {
              "type": "attack-pattern",
              "id": "attack-pattern--00000000-0000-4000-8000-000000000021",
              "name": "PowerShell",
              "x_mitre_is_subtechnique": true,
              "x_mitre_domains": [ "enterprise-attack" ],
              "x_mitre_platforms": [ "Windows" ],
              "kill_chain_phases": [ { "kill_chain_name": "mitre-attack", "phase_name": "execution" } ],
              "external_references": [
                { "source_name": "mitre-attack", "external_id": "T1059.001", "url": "https://attack.mitre.org/techniques/T1059/001" }
              ]
            },
            {
              "type": "attack-pattern",
              "id": "attack-pattern--00000000-0000-4000-8000-000000000022",
              "name": "Scheduled Task/Job",
              "x_mitre_domains": [ "enterprise-attack" ],
              "kill_chain_phases": [
                { "kill_chain_name": "mitre-attack", "phase_name": "execution" },
                { "kill_chain_name": "mitre-attack", "phase_name": "persistence" }
              ],
              "external_references": [
                { "source_name": "mitre-attack", "external_id": "T1053", "url": "https://attack.mitre.org/techniques/T1053" }
              ]
            },
            {{(version == "2.0" ? "" : """
            {
              "type": "attack-pattern",
              "id": "attack-pattern--00000000-0000-4000-8000-000000000023",
              "name": "OS Credential Dumping",
              "x_mitre_domains": [ "enterprise-attack" ],
              "kill_chain_phases": [ { "kill_chain_name": "mitre-attack", "phase_name": "credential-access" } ],
              "external_references": [
                { "source_name": "mitre-attack", "external_id": "T1003", "url": "https://attack.mitre.org/techniques/T1003" }
              ]
            },
            """)}}
            {
              "type": "attack-pattern",
              "id": "attack-pattern--00000000-0000-4000-8000-000000000024",
              "name": "Revoked Technique",
              "revoked": true,
              "external_references": [ { "source_name": "mitre-attack", "external_id": "T1900" } ]
            },
            {
              "type": "attack-pattern",
              "id": "attack-pattern--00000000-0000-4000-8000-000000000025",
              "name": "Deprecated Technique",
              "x_mitre_deprecated": true,
              "external_references": [ { "source_name": "mitre-attack", "external_id": "T1901" } ]
            },
            {
              "type": "relationship",
              "id": "relationship--00000000-0000-4000-8000-000000000030",
              "relationship_type": "subtechnique-of",
              "source_ref": "attack-pattern--00000000-0000-4000-8000-000000000021",
              "target_ref": "attack-pattern--00000000-0000-4000-8000-000000000020"
            }
          ]
        }
        """;
}
