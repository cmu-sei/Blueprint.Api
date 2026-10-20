// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Blueprint.Api.Tests.Support;

/// <summary>
/// Hand-made ATT&amp;CK STIX bundles: the shape of enterprise-attack.json, cut down to two tactics, a technique
/// with a sub-technique, a revoked technique and a deprecated one.
/// </summary>
public static class AttackBundles
{
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
