// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Blueprint.Api.Tests.Support;

/// <summary>
/// A hand-made D3FEND ontology: the JSON-LD shape of d3fend.json, cut down to two tactics, a top-level technique
/// with a sub-technique under it, a top-level technique that enables both tactics, an analytic technique that
/// carries a d3fend-id but is no countermeasure, and the anonymous restriction classes the real graph is full of.
/// The definitions are invented.
/// </summary>
public static class D3fendOntologies
{
    /// <summary>
    /// Harden is listed before Detect by display-order, the reverse of the graph's object order, so a test can tell
    /// the one from the other. The version is the ontology's <c>owl:versionInfo</c> unless
    /// <paramref name="includeVersion"/> leaves it out.
    /// </summary>
    public static string Ontology(string version = "1.6.0", bool includeVersion = true) => $$"""
        {
          "@context": {
            "d3f": "http://d3fend.mitre.org/ontologies/d3fend.owl#",
            "dcterms": "http://purl.org/dc/terms/",
            "owl": "http://www.w3.org/2002/07/owl#",
            "rdfs": "http://www.w3.org/2000/01/rdf-schema#",
            "xsd": "http://www.w3.org/2001/XMLSchema#"
          },
          "@graph": [
            {
              "@id": "http://d3fend.mitre.org/ontologies/d3fend.owl",
              "@type": "owl:Ontology",
              "d3f:release-date": { "@type": "xsd:dateTime", "@value": "2026-08-31T00:12:00+00:00" },
              "dcterms:description": "Test D3FEND ontology",
              "dcterms:title": "D3FEND - A knowledge graph of cybersecurity countermeasures",
              {{(includeVersion ? $"\"owl:versionInfo\": \"{version}\"," : "")}}
              "rdfs:comment": "Test comment"
            },
            { "@id": "_:b0001", "@type": "owl:Restriction", "owl:onProperty": { "@id": "d3f:enables" }, "owl:someValuesFrom": { "@id": "d3f:DefensiveTactic" } },
            {
              "@id": "d3f:DefensiveTactic",
              "@type": "owl:Class",
              "d3f:definition": "A plan for attaining a particular goal.",
              "rdfs:label": "Defensive Tactic"
            },
            {
              "@id": "d3f:Detect",
              "@type": [ "d3f:DefensiveTactic", "owl:Class", "owl:NamedIndividual" ],
              "d3f:definition": "The detect tactic finds an adversary on the network.",
              "d3f:display-order": 1,
              "rdfs:label": "Detect",
              "rdfs:subClassOf": { "@id": "d3f:DefensiveTactic" }
            },
            {
              "@id": "d3f:Harden",
              "@type": [ "d3f:DefensiveTactic", "owl:Class", "owl:NamedIndividual" ],
              "d3f:definition": "The harden tactic raises the cost of exploitation.",
              "d3f:display-order": 0,
              "rdfs:label": "Harden",
              "rdfs:subClassOf": { "@id": "d3f:DefensiveTactic" }
            },
            {
              "@id": "d3f:DefensiveTechnique",
              "@type": [ "d3f:DefensiveTechnique", "owl:Class", "owl:NamedIndividual" ],
              "d3f:definition": "A method which makes a computer system more difficult to attack.",
              "d3f:display-baseurl": "/technique/",
              "rdfs:label": "Defensive Technique",
              "rdfs:subClassOf": [ { "@id": "_:b0001" }, { "@id": "d3f:CyberTechnique" } ]
            },
            {
              "@id": "d3f:CredentialHardening",
              "@type": [ "d3f:CredentialHardening", "owl:Class", "owl:NamedIndividual" ],
              "d3f:d3fend-id": "D3-CH",
              "d3f:definition": "Credential hardening makes a credential harder to guess or reuse.",
              "d3f:enables": { "@id": "d3f:Harden" },
              "rdfs:label": "Credential Hardening",
              "rdfs:subClassOf": [ { "@id": "_:b0001" }, { "@id": "d3f:DefensiveTechnique" } ]
            },
            {
              "@id": "d3f:CertificateBasedAuthentication",
              "@type": [ "owl:Class", "owl:NamedIndividual" ],
              "d3f:d3fend-id": "D3-CBAN",
              "d3f:definition": "Certificate-based authentication proves a party's identity with a certificate.",
              "rdfs:label": "Certificate-based Authentication",
              "rdfs:subClassOf": { "@id": "d3f:CredentialHardening" }
            },
            {
              "@id": "d3f:FileAnalysis",
              "@type": [ "d3f:FileAnalysis", "owl:Class", "owl:NamedIndividual" ],
              "d3f:d3fend-id": "D3-FA",
              "d3f:definition": "File analysis inspects files for malicious content and hardens how they are handled.",
              "d3f:enables": [ { "@id": "d3f:Detect" }, { "@id": "d3f:Harden" } ],
              "rdfs:label": "File Analysis",
              "rdfs:subClassOf": { "@id": "d3f:DefensiveTechnique" }
            },
            {
              "@id": "d3f:ClusterAnalysis",
              "@type": [ "owl:Class", "owl:NamedIndividual" ],
              "d3f:d3fend-id": "D3A-CA",
              "d3f:definition": "Cluster analysis groups similar observations.",
              "rdfs:label": "Cluster Analysis",
              "rdfs:subClassOf": { "@id": "d3f:AnalyticTechnique" }
            },
            {
              "@id": "d3f:AnalyticTechnique",
              "@type": "owl:Class",
              "rdfs:label": "Analytic Technique",
              "rdfs:subClassOf": { "@id": "d3f:Technique" }
            }
          ]
        }
        """;

    /// <summary>A JSON-LD graph with no defensive tactics or techniques in it: nothing to import.</summary>
    public static string WithoutTechniques() => """
        {
          "@context": { "d3f": "http://d3fend.mitre.org/ontologies/d3fend.owl#" },
          "@graph": [
            { "@id": "http://d3fend.mitre.org/ontologies/d3fend.owl", "@type": "owl:Ontology", "owl:versionInfo": "1.6.0" },
            { "@id": "d3f:Artifact", "@type": "owl:Class", "rdfs:label": "Artifact" }
          ]
        }
        """;
}
