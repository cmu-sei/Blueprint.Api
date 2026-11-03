// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary>
/// The scenario's indicator set: made safe and complete once, read from the range's Caster topology where there is one, shared
/// out to each inject, and enforced on every document written.
/// </summary>
public class MselIndicatorsTests
{
    private static readonly Guid DraftId = Guid.Parse("6f9b3a52-0c1d-4f4e-9a4b-2d7f0e1c5a11");

    private static MselDraft Draft() => new()
    {
        DraftId = DraftId,
        Name = "Deep Bite",
        Organizations = [new MselDraftOrganization { ShortName = "HPWR", Name = "Harbor Power" }, new MselDraftOrganization { ShortName = "NEWS", Name = "Island Herald" }]
    };

    /// <summary>
    /// Adversary addresses outside the documentation ranges, hosts outside the private ones, real domains and senders are
    /// replaced; the hashes are always Blueprint's own; every organization gets a reserved domain.
    /// </summary>
    [Fact]
    public void Clean_ReplacesEverythingUnsafe()
    {
        var warnings = new List<string>();
        static MselIndicatorSet Input() => new MselIndicatorSet
        {
            ThreatActor = "  SALT   TYPHOON-ish ",
            Assets =
            [
                new MselIndicatorAsset { HostName = "dc01", Role = "DomainController", IpAddress = "8.8.8.8", Network = "8.8.8.0/24" },
                new MselIndicatorAsset { HostName = "ws 114", Role = "Laptop", IpAddress = "10.20.1.114", Network = "10.20.1.0/24" }
            ],
            Domains = [new MselIndicatorDomain { Organization = "HPWR", Domain = "harborpower.com" }],
            Adversary =
            [
                new MselIndicator { Type = "ip", Value = "185.220.101.4", Purpose = "C2 server" },
                new MselIndicator { Type = "IP", Value = "203.0.113.66", Purpose = "Staging" },
                new MselIndicator { Type = "domain", Value = "evil-update.ru", Purpose = "C2 domain" },
                new MselIndicator { Type = "domain", Value = "cdn-sync.example" },
                new MselIndicator { Type = "email", Value = "Billing@PayPal.com", Purpose = "Phishing sender" },
                new MselIndicator { Type = "sha256", Value = "invoice.docm" },
                new MselIndicator { Type = "sha256", Value = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", Purpose = "Loader" },
                new MselIndicator { Type = "user-agent", Value = "Mozilla/5.0 <script>", Purpose = "Beacon" },
                new MselIndicator { Type = "registry", Value = "HKLM\\Run" }
            ]
        };
        var set = MselIndicatorRules.Clean(Input(), Draft(), null, warnings);

        Assert.Equal("SALT TYPHOON-ish", set.ThreatActor);
        Assert.Null(set.AssetSource);
        Assert.Equal([("dc01", "DomainController", "10.10.10.10", "10.10.10.0/24"), ("ws-114", "Other", "10.20.1.114", "10.20.1.0/24")],
            set.Assets.Select(a => (a.HostName, a.Role, a.IpAddress, a.Network)));
        Assert.Equal([("HPWR", "harborpower.example"), ("NEWS", "news.example")], set.Domains.Select(d => (d.Organization, d.Domain)));
        Assert.Equal(["203.0.113.10", "203.0.113.66"], set.Adversary.Where(a => a.Type == "ip").Select(a => a.Value));
        Assert.Equal(["evil-update.example", "cdn-sync.example"], set.Adversary.Where(a => a.Type == "domain").Select(a => a.Value));
        Assert.Equal("billing@paypal.example", set.Adversary.Single(a => a.Type == "email").Value);
        Assert.Equal("Mozilla/5.0 script", set.Adversary.Single(a => a.Type == "user-agent").Value);
        Assert.DoesNotContain(set.Adversary, a => a.Type == "registry");
        var files = set.Adversary.Where(a => a.Type == "sha256").ToList();
        Assert.Equal(2, files.Count);
        Assert.All(files, f =>
        {
            Assert.Matches("^[0-9a-f]{64}$", f.Value);
            Assert.Matches("^[0-9a-f]{32}$", f.Md5);
            Assert.Matches("^[0-9a-f]{40}$", f.Sha1);
        });
        // the model's hash is never kept, since it could be a real file's; a file's name is kept in its purpose
        Assert.DoesNotContain(files, f => f.Value.StartsWith("e3b0c442"));
        Assert.NotEqual(files[0].Value, files[1].Value);
        Assert.Equal(("invoice.docm", null), (files[0].Name, files[0].Purpose));
        Assert.Equal((null, "Loader"), (files[1].Name, files[1].Purpose));
        Assert.Contains(warnings, w => w.StartsWith("Replaced 5 indicators outside the safe ranges") && w.Contains("185.220.101.4 with 203.0.113.10"));
        // the same draft makes the same hashes every time
        Assert.Equal(files.Select(f => f.Value), MselIndicatorRules.Clean(Input(), Draft(), null, new()).Adversary.Where(a => a.Type == "sha256").Select(a => a.Value));
    }

    /// <summary>An empty set still has one indicator of each kind a document names, so a stray value always has one to become.</summary>
    [Fact]
    public void Clean_FillsInAnEmptySet()
    {
        var set = MselIndicatorRules.Clean(null, Draft(), null, new());

        Assert.Equal(["domain", "ip", "sha256", "email"], set.Adversary.Select(a => a.Type));
        Assert.All(set.Adversary.Where(a => a.Type == "ip"), a => Assert.True(MselIndicatorRules.IsDocumentation(IPAddress.Parse(a.Value))));
        Assert.Equal("notice@update-check.example", set.Adversary.Single(a => a.Type == "email").Value);
        Assert.Empty(set.Assets);
    }

    private const string Topology = """
        {
          "nodes": [
            { "id": "proxmox_virtual_environment_vm.dc", "kind": "Machine", "label": "dc01",
              "properties": { "ip_addresses": "10.5.1.10, 10.5.0.10", "ip_addresses_by_network": "proxmox_sdn_vnet.corp=10.5.0.10;proxmox_sdn_vnet.mgmt=10.5.1.10", "gateway": "10.5.0.1" } },
            { "id": "proxmox_virtual_environment_vm.ws", "kind": "Machine", "label": "ws01" },
            { "id": "proxmox_virtual_environment_vm.kali", "kind": "Machine", "label": "kali", "properties": { "ip_addresses": "100.64.10.5" } },
            { "id": "proxmox_sdn_vnet.corp", "kind": "Network", "label": "corp", "properties": { "cidr": "10.5.0.0/24, fd00:5::/64" } },
            { "id": "proxmox_sdn_vnet.mgmt", "kind": "Network", "label": "mgmt", "properties": { "cidr": "10.5.1.0/24" } },
            { "id": "external:vmbr1", "kind": "Network", "label": "internet", "external": true, "properties": { "cidr": "100.64.10.0/24" } }
          ],
          "edges": [
            { "from": "proxmox_virtual_environment_vm.dc", "to": "proxmox_sdn_vnet.mgmt", "nic": 1 },
            { "from": "proxmox_virtual_environment_vm.dc", "to": "proxmox_sdn_vnet.corp", "nic": 0 },
            { "from": "proxmox_virtual_environment_vm.ws", "to": "proxmox_sdn_vnet.corp", "nic": 0 },
            { "from": "proxmox_virtual_environment_vm.kali", "to": "external:vmbr1", "nic": 0 }
          ],
          "layout": { "proxmox_virtual_environment_vm.dc": { "role": "DomainController" } }
        }
        """;

    /// <summary>
    /// The range's machines are the hosts, on the networks Caster says, with Caster's addresses; one without an address gets
    /// one on its network; the machine on the internet network is the adversary's, and that network's range is the adversary's too.
    /// </summary>
    [Fact]
    public void Clean_TakesTheHostsFromTheCasterTopology()
    {
        var range = CasterRange.Parse(JsonDocument.Parse(Topology).RootElement);
        Assert.Equal(["dc01", "ws01"], range.Hosts.Select(h => h.Name));
        Assert.Equal(["10.5.0.10", "10.5.1.10"], range.Hosts[0].IpAddresses);
        Assert.Equal(("10.5.0.0/24", "10.5.0.1", "DomainController"), (range.Hosts[0].Network, range.Hosts[0].Gateway, range.Hosts[0].Role));
        Assert.Equal("kali", range.Attackers.Single().Name);
        Assert.Equal(["100.64.10.0/24"], range.ExternalNetworks);

        var warnings = new List<string>();
        var set = MselIndicatorRules.Clean(new MselIndicatorSet
        {
            Assets =
            [
                new MselIndicatorAsset { HostName = "DC01", Role = "Server", IpAddress = "10.99.0.1" },
                new MselIndicatorAsset { HostName = "ws01", Role = "Workstation", IpAddress = "10.5.0.77" },
                new MselIndicatorAsset { HostName = "mail01", Role = "MailServer", IpAddress = "10.5.0.25" }
            ],
            Adversary = [new MselIndicator { Type = "ip", Value = "100.64.10.99", Purpose = "Scanner" }]
        }, Draft(), range, warnings);

        Assert.Equal("Caster", set.AssetSource);
        Assert.Equal([("dc01", "Server", "10.5.0.10", "10.5.0.0/24", "10.5.0.1"), ("ws01", "Workstation", "10.5.0.77", "10.5.0.0/24", null)],
            set.Assets.Select(a => (a.HostName, a.Role, a.IpAddress, a.Network, a.Gateway)));
        Assert.Equal([("100.64.10.99", "Scanner"), ("100.64.10.5", "Adversary host kali")], set.Adversary.Where(a => a.Type == "ip").Select(a => (a.Value, a.Purpose)));
        Assert.Contains("Kept the Caster workspace's 2 machines as the scenario's hosts, and left out 1 the model made up: mail01.", warnings);
    }

    /// <summary>
    /// Caster before it sends addresses: the machines still give their names, and their addresses are the model's private
    /// ones, else made up; the attacker is still known by its network's name.
    /// </summary>
    [Fact]
    public void Clean_KeepsTheRangesNames_WithoutCastersAddresses()
    {
        var bare = System.Text.RegularExpressions.Regex.Replace(Topology, @",\s*""properties"": \{[^}]*\}", "");
        var range = CasterRange.Parse(JsonDocument.Parse(bare).RootElement);
        Assert.All(range.Hosts, h => Assert.Empty(h.IpAddresses));
        Assert.All(range.Hosts, h => Assert.Null(h.Network));
        Assert.Empty(range.ExternalNetworks);
        Assert.Equal("kali", range.Attackers.Single().Name);

        var set = MselIndicatorRules.Clean(new MselIndicatorSet
        {
            Assets = [new MselIndicatorAsset { HostName = "dc01", IpAddress = "8.8.8.8" }, new MselIndicatorAsset { HostName = "ws01", IpAddress = "192.168.4.20", Network = "192.168.4.0/24" }]
        }, Draft(), range, new());

        Assert.Equal([("dc01", "10.10.10.10", "10.10.10.0/24"), ("ws01", "192.168.4.20", "192.168.4.0/24")], set.Assets.Select(a => (a.HostName, a.IpAddress, a.Network)));
        Assert.Equal("Caster", set.AssetSource);
    }

    /// <summary>A topology Caster writes with numbers for its enums is read the same; an attacker can be marked in the view's layout.</summary>
    [Fact]
    public void Parse_ReadsKindsAndRolesByNumber()
    {
        var range = CasterRange.Parse(JsonDocument.Parse("""
            { "nodes": [ { "id": "a", "kind": 0, "label": "red-box" }, { "id": "b", "kind": 0, "label": "web01" }, { "id": "n", "kind": 1, "label": "dmz" } ],
              "edges": [ { "from": "a", "to": "n" }, { "from": "b", "to": "n" } ],
              "layout": { "a": { "role": 10 }, "b": { "role": 5 } } }
            """).RootElement);

        Assert.Equal("red-box", range.Attackers.Single().Name);
        Assert.Equal(("web01", "WebServer"), (range.Hosts.Single().Name, range.Hosts.Single().Role));
        Assert.Empty(CasterRange.Parse(JsonDocument.Parse("[]").RootElement).Hosts);
    }

    private static MselIndicatorSet Set() => new()
    {
        ThreatActor = "GRAY SPARROW",
        Assets = [new MselIndicatorAsset { HostName = "dc01", Role = "DomainController", IpAddress = "10.5.0.10", Network = "10.5.0.0/24", Gateway = "10.5.0.1" }],
        Domains = [new MselIndicatorDomain { Organization = "HPWR", Domain = "harborpower.example" }],
        Adversary =
        [
            new MselIndicator { Type = "ip", Value = "203.0.113.10", Purpose = "C2 server" },
            new MselIndicator { Type = "domain", Value = "update-check.example", Purpose = "C2 domain" },
            new MselIndicator { Type = "email", Value = "it-support@update-check.example", Purpose = "Phishing sender" },
            new MselIndicator { Type = "sha256", Value = new string('a', 63) + "1", Md5 = new string('b', 31) + "2", Sha1 = new string('c', 39) + "3", Purpose = "loader.dll" }
        ]
    };

    /// <summary>
    /// Every address, network, domain, hash and sender the set does not have becomes one it has, of the same kind: a private
    /// address a host's, a public one the adversary's; defanged values stay defanged; data: URIs and the SVG namespace are left alone.
    /// </summary>
    [Fact]
    public void Enforce_ReplacesStrayValuesWithTheSets()
    {
        var replaced = new List<IndicatorCheck.Replacement>();
        var html = "<p>Beacon from 10.5.0.10 (gateway 10.5.0.1) to 198.51.100.77 and 8.8.8.8:443 via evil.ru and hxxp://bad[.]com/x and http://203.0.113.10/p.</p>" +
            "<p>Mail from a.b@phish.net; reply to j.doe@harborpower.example or it-support@update-check.example. Host 10.9.9.9 on 10.9.9.0/24.</p>" +
            "<p>SHA-256 e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855, MD5 D41D8CD98F00B204E9800998ECF8427E, 2001:4860:4860::8888.</p>" +
            "<img src=\"data:image/png;base64,MTk4LjUxLjEwMC41IGV2aWwucnU=198.51.100.5\"><svg xmlns=\"http://www.w3.org/2000/svg\"><text>portal.harborpower.example</text></svg>";

        var result = IndicatorCheck.Enforce(html, Set(), ["press@islandherald.example"], replaced);

        Assert.Contains("Beacon from 10.5.0.10 (gateway 10.5.0.1) to 203.0.113.10 and 203.0.113.10:443 via update-check.example and hxxp://update-check[.]example/x and http://203.0.113.10/p.", result);
        Assert.Contains("Mail from it-support@update-check.example; reply to j.doe@harborpower.example or it-support@update-check.example. Host 10.5.0.10 on 10.5.0.0/24.", result);
        Assert.Contains($"SHA-256 {new string('a', 63)}1, MD5 {new string('b', 31)}2, 203.0.113.10.", result);
        Assert.Contains("data:image/png;base64,MTk4LjUxLjEwMC41IGV2aWwucnU=198.51.100.5\"", result);
        Assert.Contains("http://www.w3.org/2000/svg", result);
        Assert.Contains("portal.harborpower.example", result);
        Assert.Equal(10, replaced.Count);
        Assert.StartsWith("Replaced 10 values not in the scenario's indicator set in \"Alert\": a.b@phish.net with it-support@update-check.example; " +
            "198.51.100.77 with 203.0.113.10; 8.8.8.8 with 203.0.113.10; 10.9.9.9 with 10.5.0.10; 10.9.9.0/24 with 10.5.0.0/24;", IndicatorCheck.Warning("\"Alert\"", replaced));
    }

    /// <summary>What is not an indicator is left as written: versions, builds, times, phone numbers, netmasks, MACs, file names, capitalised names.</summary>
    [Fact]
    public void Enforce_LeavesOrdinaryTextAlone()
    {
        const string html = "<p>Version 10.0.19045.3803, build 22.04.1, v1.2.3.4, section 1.2.3.4, at 14:30:00 call 555-0142. ASP.NET and Node.js read report.pdf, " +
            "e.g. README.md and setup.sh. Mask 255.255.255.0, loopback 127.0.0.1, dc01.harbor.local, MAC 00:1a:2b:3c:4d:5e, colour #A1B2C3, T1566.001, " +
            "case 12345678901234567890123456789012, id 550e8400-e29b-41d4-a716-446655440000.</p>";
        var replaced = new List<IndicatorCheck.Replacement>();

        Assert.Equal(html, IndicatorCheck.Enforce(html, Set(), [], replaced));
        Assert.Empty(replaced);
        Assert.Null(IndicatorCheck.Warning("the row", replaced));
    }

    /// <summary>
    /// Each inject keeps only the set's values, its techniques as ID and name, the CTI event layout on at most five Intel
    /// articles, and IP Address and Network columns from the set: the model's where the set has it, else the row's own indicator.
    /// </summary>
    [Fact]
    public void CleanInjects_SharesTheSetOut()
    {
        var template = new MselEntity { UseGallery = true };
        template.DataFields.Add(new DataFieldEntity { Name = "Title", DataType = DataFieldType.String, GalleryArticleParameter = nameof(GalleryArticleParameter.Name), DisplayOrder = 1 });
        template.DataFields.Add(new DataFieldEntity { Name = "Source Type", DataType = DataFieldType.SourceType, GalleryArticleParameter = nameof(GalleryArticleParameter.SourceType), DisplayOrder = 2 });
        template.DataFields.Add(new DataFieldEntity { Name = "IP Address", DataType = DataFieldType.IpAddress, DisplayOrder = 3 });
        template.DataFields.Add(new DataFieldEntity { Name = "Network", DataType = DataFieldType.Network, DisplayOrder = 4 });
        var draft = Draft();
        draft.Indicators = Set();
        draft.Injects =
        [
            new MselDraftInject { Values = new() { ["Title"] = "Beacon", ["Source Type"] = "Intel", ["IP Address"] = "8.8.8.8" }, Layout = "ctievent",
                Indicators = ["DC01", "203.0.113.10", "nonexistent.example"], Techniques = ["t1566.001 - Spearphishing Attachment", "not a technique", "T1566.001 again"] },
            new MselDraftInject { Values = new() { ["Title"] = "C2", ["Source Type"] = "Intel", ["IP Address"] = "203.0.113.10", ["Network"] = "203.0.113.0/24" }, Layout = "CtiEvent" },
            new MselDraftInject { Values = new() { ["Title"] = "Story", ["Source Type"] = "News" }, Layout = "CtiEvent", Indicators = ["10.5.0.10"] }
        ];
        draft.Injects.AddRange(Enumerable.Range(0, 5).Select(i => new MselDraftInject { Values = new() { ["Title"] = $"Intel {i}", ["Source Type"] = "Intel" }, Layout = "CtiEvent" }));
        var warnings = new List<string>();

        MselIndicatorRules.CleanInjects(draft, template, warnings);

        var first = draft.Injects[0];
        Assert.Equal(["dc01", "203.0.113.10"], first.Indicators);
        Assert.Equal(["T1566.001 Spearphishing Attachment"], first.Techniques);
        Assert.Equal(("10.5.0.10", "10.5.0.0/24"), (first.Values["IP Address"], first.Values["Network"]));
        Assert.Equal("203.0.113.10", draft.Injects[1].Values["IP Address"]);
        Assert.False(draft.Injects[1].Values.ContainsKey("Network"));
        Assert.Equal(("10.5.0.10", "10.5.0.0/24"), (draft.Injects[2].Values["IP Address"], draft.Injects[2].Values["Network"]));
        Assert.Null(draft.Injects[2].Layout);
        Assert.Equal(5, draft.Injects.Count(i => i.Layout == MselIndicatorRules.CtiEvent));
        Assert.Contains("Took 1 value off injects that the scenario's indicator set does not have: nonexistent.example.", warnings);
        Assert.Contains("Kept the CTI event layout on the first 5 of 7 Intel articles; the others are intelligence reports.", warnings);
        Assert.Contains("Set 2 IP Address or Network values that the scenario's indicator set does not have from the set.", warnings);
    }

    /// <summary>
    /// A CTI event's request: the TLP tag from the MSEL's marking, the actor, the techniques of the inject and of those sharing
    /// its indicators, an attribute row per indicator with its MISP category and type, and the related events.
    /// </summary>
    [Fact]
    public void CtiEventRequest_ListsTagsAttributesAndRelatedEvents()
    {
        var draft = Draft();
        draft.Design = new MselDraftDesign { Marking = "Tlp", MarkingText = "amber" };
        draft.Indicators = Set();
        draft.Injects =
        [
            new MselDraftInject { Values = new() { ["Title"] = "Phish wave", ["Date"] = "2026-03-04" }, Indicators = ["it-support@update-check.example", "dc01"], Techniques = ["T1566.001 Spearphishing Attachment"] },
            new MselDraftInject { Values = new() { ["Title"] = "Event", ["Date"] = "2026-03-05" }, Indicators = ["203.0.113.10", "dc01", new string('a', 63) + "1"], Techniques = ["T1071.001 Web Protocols"], Layout = "CtiEvent" },
            new MselDraftInject { Values = new() { ["Title"] = "Unrelated" }, Indicators = ["harborpower.example"] }
        ];

        var request = MselIndicatorRules.CtiEventRequest(draft, 1, "2026-03-05T10:00:00Z", i => i.Values["Title"], i => i.Values.GetValueOrDefault("Date"));

        Assert.Contains("Event ID: 1002", request);
        Assert.Contains("Tags: tlp:amber; misp-galaxy:threat-actor=\"GRAY SPARROW\"; misp-galaxy:mitre-attack-pattern=\"Web Protocols - T1071.001\"; misp-galaxy:mitre-attack-pattern=\"Spearphishing Attachment - T1566.001\"", request);
        Assert.Contains("- Network activity | ip-dst | 10.5.0.10 | No | Affected host dc01 (DomainController)", request);
        Assert.Contains("- Network activity | ip-dst | 203.0.113.10 | Yes | C2 server", request);
        Assert.Contains($"- Payload delivery | sha256 | {new string('a', 63)}1 | Yes | loader.dll", request);
        Assert.Contains($"- Payload delivery | md5 | {new string('b', 31)}2 | Yes | loader.dll", request);
        Assert.Contains("- Event 1001, 2026-03-04: Phish wave", request);
        Assert.DoesNotContain("Unrelated", request);
        Assert.DoesNotContain("update-check.example |", request);

        // a marking that is not TLP gives no TLP tag
        draft.Design = new MselDraftDesign { Marking = "Exercise" };
        Assert.DoesNotContain("tlp:", MselIndicatorRules.CtiEventRequest(draft, 1, null, i => i.Values["Title"], i => null));
    }
}
