// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Blueprint.Api.Tests.Support;

/// <summary>
/// A hand-made MITRE CAPEC STIX bundle: the shape of stix-capec.json, cut down to two Meta patterns (the groups),
/// two Standard patterns (the techniques), three Detailed ones (the sub-techniques, one of them a level lower
/// than the Standard pattern it refines), and a deprecated and an obsolete pattern. Like the real bundle it has
/// no collection and no tactics, carries its version on each pattern, and holds mitigations and relationships a
/// release does not read.
/// </summary>
public static class CapecBundles
{
    private const string Command = "attack-pattern--00000000-0000-4000-8000-000000000248";
    private const string Buffer = "attack-pattern--00000000-0000-4000-8000-000000000123";
    private const string Sql = "attack-pattern--00000000-0000-4000-8000-000000000066";
    private const string Overflow = "attack-pattern--00000000-0000-4000-8000-000000000100";
    private const string ApiCall = "attack-pattern--00000000-0000-4000-8000-000000000008";
    private const string Environment = "attack-pattern--00000000-0000-4000-8000-000000000010";
    private const string Local = "attack-pattern--00000000-0000-4000-8000-000000000009";

    /// <summary>
    /// Command Injection (Meta) holds SQL Injection (Standard); Buffer Manipulation (Meta) holds Overflow Buffers
    /// (Standard), which holds Buffer Overflow in an API Call and via Environment Variables (Detailed), and below
    /// the first of those Buffer Overflow in Local Command-Line Utilities (Detailed). CAPEC-2 is deprecated and
    /// CAPEC-3 obsolete, both under Command Injection.
    /// </summary>
    public static string Capec(string version = "3.9", bool includeVersion = true) => $$"""
        {
          "type": "bundle",
          "id": "bundle--00000000-0000-4000-8000-000000000201",
          "objects": [
            {
              "type": "marking-definition",
              "spec_version": "2.1",
              "id": "marking-definition--00000000-0000-4000-8000-000000000202",
              "created": "2023-01-30T20:40:28.791035Z",
              "definition_type": "statement",
              "definition": { "statement": "CAPEC is sponsored by US-CERT in the office of Cybersecurity and Communications at the U.S. Department of Homeland Security. Copyright © 2007 - 2023, The MITRE Corporation. CAPEC and the CAPEC logo are trademarks of The MITRE Corporation." }
            },
            {
              "type": "identity",
              "spec_version": "2.1",
              "id": "identity--00000000-0000-4000-8000-000000000203",
              "name": "The MITRE Corporation",
              "identity_class": "organization"
            },
            {{Pattern(Command, "CAPEC-248", "Command Injection", "Meta", "Stable", version, includeVersion, null, "Software", "2023-01-10T00:00:00Z")}},
            {{Pattern(Buffer, "CAPEC-123", "Buffer Manipulation", "Meta", "Draft", version, includeVersion, null, "Software", "2023-01-24T00:00:00Z")}},
            {{Pattern(Sql, "CAPEC-66", "SQL Injection", "Standard", "Stable", version, includeVersion, Command, "Software", "2023-01-12T00:00:00Z",
                weaknesses: ["CWE-89", "CWE-1286", "CWE-89"], links: SqlLinks)}},
            {{Pattern(Overflow, "CAPEC-100", "Overflow Buffers", "Standard", "Draft", version, includeVersion, Buffer, "Software,Hardware", "2023-01-11T00:00:00Z")}},
            {{Pattern(ApiCall, "CAPEC-8", "Buffer Overflow in an API Call", "Detailed", "Draft", version, includeVersion, Overflow, "Software", "2023-01-13T00:00:00Z",
                weaknesses: ["CWE-120", "CWE-119"])}},
            {{Pattern(Environment, "CAPEC-10", "Buffer Overflow via Environment Variables", "Detailed", "Draft", version, includeVersion, Overflow, "Software", "2023-01-14T00:00:00Z",
                links: EnvironmentLinks)}},
            {{Pattern(Local, "CAPEC-9", "Buffer Overflow in Local Command-Line Utilities", "Detailed", "Draft", version, includeVersion, ApiCall, "Software", "2023-01-15T00:00:00Z")}},
            {{Pattern("attack-pattern--00000000-0000-4000-8000-000000000002", "CAPEC-2", "Inducing Account Lockout", "Detailed", "Deprecated", version, includeVersion, Sql, "Software", "2023-01-16T00:00:00Z")}},
            {{Pattern("attack-pattern--00000000-0000-4000-8000-000000000003", "CAPEC-3", "Using Leading 'Ghost' Character Sequences", "Detailed", "Obsolete", version, includeVersion, Sql, "Software", "2023-01-17T00:00:00Z")}},
            {
              "type": "course-of-action",
              "spec_version": "2.1",
              "id": "course-of-action--00000000-0000-4000-8000-000000000301",
              "name": "Use prepared statements",
              "description": "A mitigation, which a release does not read."
            },
            {
              "type": "relationship",
              "spec_version": "2.1",
              "id": "relationship--00000000-0000-4000-8000-000000000302",
              "relationship_type": "mitigates",
              "source_ref": "course-of-action--00000000-0000-4000-8000-000000000301",
              "target_ref": "{{Sql}}"
            }
          ]
        }
        """;

    /// <summary>A bundle of attack patterns of some other catalogue: no tactics and no CAPEC ids.</summary>
    public static string WithoutCapecIds() => """
        {
          "type": "bundle",
          "id": "bundle--00000000-0000-4000-8000-000000000401",
          "objects": [
            {
              "type": "attack-pattern",
              "spec_version": "2.1",
              "id": "attack-pattern--00000000-0000-4000-8000-000000000402",
              "name": "A pattern some tool exported",
              "external_references": [ { "source_name": "some-tool", "external_id": "X-1" } ]
            }
          ]
        }
        """;

    private const string SqlLinks =
        """
        { "source_name": "WASC", "external_id": "19", "description": "SQL Injection", "url": "http://projects.webappsec.org/SQL-Injection" },
        { "source_name": "OWASP Attacks", "description": "SQL Injection", "url": "https://owasp.org/www-community/attacks/SQL_Injection" },
        { "source_name": "reference_from_CAPEC", "external_id": "REF-607", "description": "A paper", "url": "https://example.test/paper" }
        """;

    private const string EnvironmentLinks =
        """
        { "source_name": "OWASP Attacks", "description": "Buffer Overflow via Environment Variables", "url": "https://owasp.org/www-community/attacks/Buffer_Overflow_via_Environment_Variables" }
        """;

    private static string Pattern(
        string id, string capecId, string name, string abstraction, string status, string version, bool includeVersion,
        string parent, string domains, string modified, string[] weaknesses = null, string links = null)
    {
        var number = capecId["CAPEC-".Length..];
        var references = new System.Collections.Generic.List<string>
        {
            $$"""{ "source_name": "capec", "external_id": "{{capecId}}", "url": "https://capec.mitre.org/data/definitions/{{number}}.html" }"""
        };
        foreach (var weakness in weaknesses ?? [])
            references.Add($$"""{ "source_name": "cwe", "external_id": "{{weakness}}", "url": "http://cwe.mitre.org/data/definitions/{{weakness["CWE-".Length..]}}.html" }""");
        if (links != null)
            references.Add(links);

        var parents = parent == null ? "" : $"\"x_capec_child_of_refs\": [\"{parent}\"],";
        var versionField = includeVersion ? $"\"x_capec_version\": \"{version}\"," : "";
        var domainList = string.Join(", ", System.Linq.Enumerable.Select(domains.Split(','), d => $"\"{d}\""));
        return $$"""
            {
              "type": "attack-pattern",
              "spec_version": "2.1",
              "id": "{{id}}",
              "created_by_ref": "identity--00000000-0000-4000-8000-000000000203",
              "object_marking_refs": [ "marking-definition--00000000-0000-4000-8000-000000000202" ],
              "created": "2020-01-01T00:00:00Z",
              "modified": "{{modified}}",
              "name": "{{name}}",
              "description": "{{name}} is a pattern.",
              "external_references": [ {{string.Join(", ", references)}} ],
              "x_capec_abstraction": "{{abstraction}}",
              "x_capec_status": "{{status}}",
              "x_capec_domains": [ {{domainList}} ],
              {{parents}}
              {{versionField}}
              "x_capec_likelihood_of_attack": "High"
            }
            """;
    }
}
