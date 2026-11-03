// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ClearExtensions;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// The scenario's indicator set in an AI build: written once with the injects, read from the range's Caster workspace where
/// there is one, given to each article and row, filled into the IP Address and Network columns, and enforced on what is written.
/// </summary>
public class MselBuildIndicatorEndpointTests(DatabaseFixture fixture, AiEnabledFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<AiEnabledFactory>
{
    private readonly IAiModelClient _model = factory.Model;
    private readonly List<(string System, string Prompt, string Tool, JsonObject Schema)> _calls = new();
    private static readonly Guid WorkspaceId = Guid.Parse("2b1f4c3e-8a5d-4e2f-9c7b-1d3e5f7a9b0c");

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _model.ClearSubstitute();
        factory.CasterTopology.ClearSubstitute();
        factory.Images.ClearSubstitute();
        factory.Cite.ClearSubstitute();
        factory.Cite.GetTeamTypesAsync(Arg.Any<CancellationToken>()).Returns(new List<Cite.Api.Client.TeamType>());
        _model.InvokeToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<JsonObject>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var system = call.ArgAt<string>(0);
                var tool = call.ArgAt<string>(2);
                _calls.Add((system, call.ArgAt<string>(1), tool, call.ArgAt<JsonObject>(4)));
                var answer = tool switch
                {
                    // a stray C2 address and domain, beside the set's own
                    "submit_article" => """{"html":"<p>Beacon from ws01 to 198.51.100.200 and evil-cdn.ru; also 203.0.113.10 and update-check.example.</p>"}""",
                    "submit_inject_text" => """{"html":"<p>Block 8.8.4.4 at the firewall; mail from it-support@update-check.example.</p>"}""",
                    _ when system.Contains("first of two steps") => Design(),
                    _ => Injects()
                };
                return JsonDocument.Parse(answer).RootElement.Clone();
            });
    }

    /// <summary>
    /// The injects step's schema, with the indicator set, each inject's indicators, techniques and layout, and IP Address and
    /// Network columns, stays within what Bedrock's strict mode takes, as the design's does.
    /// </summary>
    [Fact]
    public async Task InjectsSchema_StaysWithinWhatBedrockStrictModeTakes()
    {
        var (mselId, planner) = await CreateFromDesign(useCite: true);

        await Read<MselBuildStatus>(await Client(planner).PostAsync($"/api/msels/{mselId}/ai-draft/injects", null, Ct));

        var schema = BedrockAiModelClient.StrictSchema(_calls.Single(c => c.System.Contains("second of two steps")).Schema);
        var properties = schema["properties"].AsObject();
        Assert.Contains("indicators", properties.Select(p => p.Key));
        Assert.Equal(["threatActor", "assets", "adversary", "domains"], properties["indicators"]["properties"].AsObject().Select(p => p.Key));
        var inject = properties["injects"]["items"]["properties"].AsObject();
        Assert.Equal(["Standard", "CtiEvent"], inject["layout"]["enum"].AsArray().Select(e => e.GetValue<string>()));
        var free = MselDraftSchemaSizeTests.FreeTextFields(schema);
        Assert.True(free <= MselDraftSchemaSizeTests.FreeTextFieldsBedrockAccepts, $"The injects schema has {free} free-text fields:\n{schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true })}");
    }

    /// <summary>
    /// With a Caster workspace, the injects are written to the range's machines: the prompt lists them, the set's hosts are
    /// them with Caster's addresses, a host the model made up is left out, and the attacker's machine is an adversary address.
    /// </summary>
    [Fact]
    public async Task Injects_UseTheCasterWorkspacesMachines()
    {
        var (mselId, planner) = await CreateFromDesign(useCaster: true);
        factory.CasterTopology.GetRangeAsync(WorkspaceId, Arg.Any<CancellationToken>()).Returns(CasterRange.Parse(JsonDocument.Parse(Topology(withAddresses: true)).RootElement));

        var status = await Read<MselBuildStatus>(await Client(planner).PostAsync($"/api/msels/{mselId}/ai-draft/injects", null, Ct));

        var prompt = _calls.Single(c => c.System.Contains("second of two steps")).Prompt;
        Assert.Contains("The range's machines, from its Caster workspace.", prompt);
        Assert.Contains("- dc01 (DomainController): 10.5.0.10 on 10.5.0.0/24 (corp), gateway 10.5.0.1", prompt);
        Assert.Contains("The adversary's addresses may also come from the range's outside network: 100.64.10.0/24.", prompt);
        var set = (await State(mselId)).Draft.Indicators;
        Assert.Equal("Caster", set.AssetSource);
        Assert.Equal([("dc01", "10.5.0.10"), ("ws01", "10.5.0.11")], set.Assets.Select(a => (a.HostName, a.IpAddress)));
        Assert.Contains(set.Adversary, a => a.Value == "100.64.10.5" && a.Purpose == "Adversary host kali");
        Assert.Contains(status.Warnings, w => w.Contains("left out 1 the model made up: mail01"));
    }

    /// <summary>Caster before it sends addresses: the range's machine names are kept, and their addresses are made up.</summary>
    [Fact]
    public async Task Injects_KeepTheMachineNames_WhenCasterGivesNoAddresses()
    {
        var (mselId, planner) = await CreateFromDesign(useCaster: true);
        factory.CasterTopology.GetRangeAsync(WorkspaceId, Arg.Any<CancellationToken>()).Returns(CasterRange.Parse(JsonDocument.Parse(Topology(withAddresses: false)).RootElement));

        var status = await Read<MselBuildStatus>(await Client(planner).PostAsync($"/api/msels/{mselId}/ai-draft/injects", null, Ct));

        var set = (await State(mselId)).Draft.Indicators;
        Assert.Equal(["dc01", "ws01"], set.Assets.Select(a => a.HostName));
        Assert.All(set.Assets, a => Assert.True(MselIndicatorRules.IsPrivate(System.Net.IPAddress.Parse(a.IpAddress))));
        Assert.Contains("Caster gave no addresses for the workspace's 2 machines, so their addresses are made up; their names are the range's.", status.Warnings);
    }

    /// <summary>A Caster that cannot be read does not stop the build: the hosts are made up, and the planner is told.</summary>
    [Fact]
    public async Task Injects_AreWritten_WhenCasterFails()
    {
        var (mselId, planner) = await CreateFromDesign(useCaster: true);
        factory.CasterTopology.GetRangeAsync(WorkspaceId, Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("Caster answered 503 for the workspace's topology."));

        var status = await Read<MselBuildStatus>(await Client(planner).PostAsync($"/api/msels/{mselId}/ai-draft/injects", null, Ct));

        Assert.Equal("Articles", status.Stage);
        Assert.Contains("Could not read the Caster workspace's machines (Caster answered 503 for the workspace's topology.), so the scenario's hosts are made up.", status.Warnings);
        var set = (await State(mselId)).Draft.Indicators;
        Assert.Null(set.AssetSource);
        Assert.Equal(["dc01", "ws01", "mail01"], set.Assets.Select(a => a.HostName));
    }

    /// <summary>A MSEL without Caster never asks Caster.</summary>
    [Fact]
    public async Task Injects_DoNotAskCaster_WithoutCaster()
    {
        var (mselId, planner) = await CreateFromDesign();

        await Read<MselBuildStatus>(await Client(planner).PostAsync($"/api/msels/{mselId}/ai-draft/injects", null, Ct));

        await factory.CasterTopology.DidNotReceiveWithAnyArgs().GetRangeAsync(default, Ct);
        Assert.DoesNotContain("The range's machines", _calls.Single(c => c.System.Contains("second of two steps")).Prompt);
    }

    /// <summary>The IP Address and Network columns are filled from the set: the model's value where the set has it, else the row's own indicator.</summary>
    [Fact]
    public async Task Injects_FillTheIpAddressAndNetworkColumns()
    {
        var (mselId, planner) = await CreateFromDesign();

        var status = await Read<MselBuildStatus>(await Client(planner).PostAsync($"/api/msels/{mselId}/ai-draft/injects", null, Ct));

        await using var db = NewContext();
        var fields = await db.DataFields.Where(f => f.MselId == mselId).ToListAsync(Ct);
        var values = await db.DataValues.Where(v => v.ScenarioEvent.MselId == mselId).Include(v => v.ScenarioEvent).ToListAsync(Ct);
        string Cell(string title, string column)
        {
            var titleField = fields.Single(f => f.Name == "Title");
            var eventId = values.Single(v => v.DataFieldId == titleField.Id && v.Value == title).ScenarioEventId;
            return values.SingleOrDefault(v => v.ScenarioEventId == eventId && v.DataFieldId == fields.Single(f => f.Name == column).Id)?.Value;
        }
        // the model wrote 8.8.8.8, which the set does not have; the row names ws01
        Assert.Equal(("10.5.0.11", "10.5.0.0/24"), (Cell("Block the C2", "IP Address"), Cell("Block the C2", "Network")));
        Assert.Equal(("203.0.113.10", null), (Cell("Beacon seen", "IP Address"), Cell("Beacon seen", "Network")));
        Assert.Contains("Set 1 IP Address or Network value that the scenario's indicator set does not have from the set.", status.Warnings);
    }

    /// <summary>
    /// Two articles about the same intrusion are given the same values from the set, and told to use only those; the CTI event
    /// gets its layout, tags, attributes and the other article as a related event; a stray address or domain each wrote is
    /// replaced with the set's, and the planner is told.
    /// </summary>
    [Fact]
    public async Task Articles_ReuseTheSetAndAreChecked()
    {
        var (mselId, planner) = await CreateFromDesign(marking: "Tlp", markingText: "AMBER");
        var status = await Read<MselBuildStatus>(await Client(planner).PostAsync($"/api/msels/{mselId}/ai-draft/injects", null, Ct));
        var set = (await State(mselId)).Draft.Indicators;
        var file = set.Adversary.Single(a => a.Type == "sha256");
        var articles = status.Articles.ToDictionary(a => a.Name, a => a.Id);

        var report = await Read<ArticleDraftResult>(await Client(planner).PostAsync($"/api/msels/{mselId}/ai-draft/articles/{articles["Beacon seen"]}", null, Ct));
        var cti = await Read<ArticleDraftResult>(await Client(planner).PostAsync($"/api/msels/{mselId}/ai-draft/articles/{articles["GRAY SPARROW event"]}", null, Ct));

        var prompts = _calls.Where(c => c.Tool == "submit_article").Select(c => c.Prompt).ToList();
        Assert.Equal(2, prompts.Count);
        Assert.All(prompts, p =>
        {
            Assert.Contains("Name only these values: no other IP address, network, domain, file hash or email address.", p);
            Assert.Contains("- IP address (C2 server): 203.0.113.10", p);
            Assert.Contains("- Domain (C2 domain): update-check.example", p);
            Assert.Contains("Organization domains: HPWR harborpower.example", p);
        });
        Assert.DoesNotContain("CTI event (MISP style), with these values", prompts[0]);
        var ctiPrompt = prompts[1];
        Assert.Contains("Lay this Intel article out as a CTI event (MISP style), with these values:", ctiPrompt);
        Assert.Contains("Tags: tlp:amber; misp-galaxy:threat-actor=\"GRAY SPARROW\"; misp-galaxy:mitre-attack-pattern=\"Exfiltration Over C2 Channel - T1041\"; misp-galaxy:mitre-attack-pattern=\"Web Protocols - T1071.001\"", ctiPrompt);
        Assert.Contains("- Network activity | ip-dst | 203.0.113.10 | Yes | C2 server", ctiPrompt);
        Assert.Contains($"- Payload delivery | sha256 | {file.Value} | Yes | loader.dll", ctiPrompt);
        Assert.Contains("Beacon seen", ctiPrompt[ctiPrompt.IndexOf("Related events", StringComparison.Ordinal)..]);
        Assert.Contains("CTI event (MISP style), when the request says so", _calls.First(c => c.Tool == "submit_article").System);

        foreach (var written in new[] { report, cti })
        {
            Assert.DoesNotContain("198.51.100.200", written.Html);
            Assert.DoesNotContain("evil-cdn.ru", written.Html);
            Assert.Contains("ws01", written.Html);
            Assert.StartsWith("Replaced 2 values not in the scenario's indicator set in \"", Assert.Single(written.Warnings));
        }
        Assert.Contains("TLP:AMBER", cti.Html);
        await using var db = NewContext();
        var saved = await db.DataValues.Where(v => v.ScenarioEventId == articles["Beacon seen"] && v.Value.Contains("Beacon from")).SingleAsync(Ct);
        Assert.Contains("203.0.113.10", saved.Value);
        Assert.DoesNotContain("198.51.100.200", saved.Value);
    }

    /// <summary>A row's long text is given its indicators and checked the same way; the planner is told what was replaced.</summary>
    [Fact]
    public async Task Rows_AreGivenTheirIndicatorsAndChecked()
    {
        var (mselId, planner) = await CreateFromDesign();
        var status = await Read<MselBuildStatus>(await Client(planner).PostAsync($"/api/msels/{mselId}/ai-draft/injects", null, Ct));
        var row = status.Injects.Single(i => i.Name == "Block the C2");

        var written = await Read<MselBuildContent>(await Client(planner).PostAsync($"/api/msels/{mselId}/ai-draft/rows/{row.Id}", null, Ct));

        var prompt = _calls.Single(c => c.Tool == "submit_inject_text").Prompt;
        Assert.Contains("- Host ws01 (Workstation): 10.5.0.11 on 10.5.0.0/24", prompt);
        Assert.Contains("- IP address (C2 server): 203.0.113.10", prompt);
        Assert.DoesNotContain("8.8.4.4", written.Html);
        Assert.Contains("it-support@update-check.example", written.Html);
        Assert.Equal("Replaced 1 value not in the scenario's indicator set in \"Block the C2\": 8.8.4.4 with 203.0.113.10.", Assert.Single(written.Warnings));
    }

    private static string Topology(bool withAddresses) => $$"""
        {
          "workspaceId": "{{WorkspaceId}}",
          "source": "Plan",
          "nodes": [
            { "id": "module.host[\"dc\"].proxmox_virtual_environment_vm.vm", "kind": "Machine", "label": "dc01"{{(withAddresses ? ", \"properties\": { \"ip_addresses\": \"10.5.0.10\", \"gateway\": \"10.5.0.1\", \"ip_addresses_by_network\": \"proxmox_sdn_vnet.corp=10.5.0.10\" }" : "")}} },
            { "id": "module.host[\"ws\"].proxmox_virtual_environment_vm.vm", "kind": "Machine", "label": "ws01"{{(withAddresses ? ", \"properties\": { \"ip_addresses\": \"10.5.0.11\" }" : "")}} },
            { "id": "proxmox_virtual_environment_vm.kali", "kind": "Machine", "label": "kali"{{(withAddresses ? ", \"properties\": { \"ip_addresses\": \"100.64.10.5\" }" : "")}} },
            { "id": "proxmox_sdn_vnet.corp", "kind": "Network", "label": "corp"{{(withAddresses ? ", \"properties\": { \"cidr\": \"10.5.0.0/24\" }" : "")}} },
            { "id": "external:vmbr9", "kind": "Network", "label": "internet", "external": true{{(withAddresses ? ", \"properties\": { \"cidr\": \"100.64.10.0/24\" }" : "")}} }
          ],
          "edges": [
            { "from": "module.host[\"dc\"].proxmox_virtual_environment_vm.vm", "to": "proxmox_sdn_vnet.corp", "nic": 0 },
            { "from": "module.host[\"ws\"].proxmox_virtual_environment_vm.vm", "to": "proxmox_sdn_vnet.corp", "nic": 0 },
            { "from": "proxmox_virtual_environment_vm.kali", "to": "external:vmbr9", "nic": 0 }
          ],
          "layout": { "module.host[\"dc\"].proxmox_virtual_environment_vm.vm": { "role": "DomainController" } }
        }
        """;

    private async Task<MselBuildState> State(Guid mselId)
    {
        await using var db = NewContext();
        return MselBuildState.Deserialize((await db.Msels.SingleAsync(m => m.Id == mselId, Ct)).AiDraft);
    }

    private async Task<(Guid MselId, TestActor Planner)> CreateFromDesign(bool useCite = false, bool useCaster = false, string marking = null, string markingText = null)
    {
        var template = TestData.Msel(isTemplate: true);
        template.UseGallery = true;
        template.UseCite = useCite;
        var excon = TestData.Team(template.Id);
        excon.ShortName = "EXCON";
        await Seed(template, excon,
            TestData.DataField(template.Id, name: "Title", displayOrder: 1),
            TestData.DataField(template.Id, name: "IP Address", dataType: DataFieldType.IpAddress, displayOrder: 2),
            TestData.DataField(template.Id, name: "Network", dataType: DataFieldType.Network, displayOrder: 3));
        var planner = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var design = await Read<MselDraftResult>(await Client(planner).PostAsJsonAsync("/api/msels/ai-draft/design", new MselDraftRequest
        {
            TemplateMselId = template.Id,
            Prompt = "Ransomware at the utility",
            Design = new MselDraftDesign { Marking = marking, MarkingText = markingText }
        }, JsonOptions, Ct));
        var created = await Read<MselImportResult>(await Client(planner).PostAsJsonAsync("/api/msels/ai-draft/create",
            new MselDesignBuild { TemplateMselId = template.Id, Prompt = "Ransomware at the utility", Draft = design.Draft }, JsonOptions, Ct));
        if (useCaster)
        {
            // the MSEL's range: a workspace it is linked to
            await using var db = NewContext();
            var msel = await db.Msels.SingleAsync(m => m.Id == created.Msel.Id, Ct);
            msel.UseCaster = true;
            msel.CasterWorkspaceId = WorkspaceId;
            await db.SaveChangesAsync(Ct);
        }
        _calls.Clear();
        return (created.Msel.Id, planner);
    }

    private static string Design() =>
        JsonSerializer.Serialize(new
        {
            name = "Low Tide",
            description = "Ransomware reaches a power utility.",
            durationMinutes = 120,
            scenario = new { problem = "A loader beacons out.", premise = "A phish landed.", environment = "Island grid", escalationCeiling = "No outage" },
            objectives = new[] { new { id = "O1", task = "Contain the host", condition = "Alerts", standard = "In 30 minutes" } },
            moves = new[] { new { moveNumber = 0, description = "Detection", startMinutes = 0, situationTime = "2026-03-04T08:00:00Z", purpose = "Triage", plannedInjects = 4 } },
            teams = new[] { new { name = "Security operations", shortName = "SOC", description = "Watches the network." } },
            organizations = new[] { new { name = "Harbor Power", shortName = "HPWR", description = "The utility", sector = "Energy", size = "Medium", industry = "Electric utility", country = "Islandia" } },
            questions = Array.Empty<object>()
        });

    private static string Injects() =>
        JsonSerializer.Serialize(new
        {
            moves = new object[] { new { moveNumber = 1, situationDescription = "<p>A host beacons.</p>", expectedDecisions = new[] { "Isolate it" } } },
            indicators = new
            {
                threatActor = "GRAY SPARROW",
                assets = new object[]
                {
                    new { hostName = "dc01", role = "DomainController", ipAddress = "10.5.0.10", network = "10.5.0.0/24" },
                    new { hostName = "ws01", role = "Workstation", ipAddress = "10.5.0.11", network = "10.5.0.0/24" },
                    new { hostName = "mail01", role = "MailServer", ipAddress = "10.5.0.25", network = "10.5.0.0/24" }
                },
                domains = new object[] { new { organization = "HPWR", domain = "harborpower.example" } },
                adversary = new object[]
                {
                    new { type = "ip", value = "203.0.113.10", purpose = "C2 server" },
                    new { type = "domain", value = "update-check.example", purpose = "C2 domain" },
                    new { type = "sha256", value = "loader.dll", purpose = "" },
                    new { type = "email", value = "it-support@update-check.example", purpose = "Phishing sender" }
                }
            },
            injects = new object[]
            {
                new { minutes = 5, eventType = "Inject", values = new Dictionary<string, string> { ["Title"] = "Beacon seen", ["SourceType"] = "Intel", ["IP Address"] = "203.0.113.10" },
                    indicators = new[] { "ws01", "203.0.113.10", "update-check.example" }, techniques = new[] { "T1071.001 Web Protocols" }, layout = "Standard" },
                new { minutes = 20, eventType = "Inject", values = new Dictionary<string, string> { ["Title"] = "GRAY SPARROW event", ["SourceType"] = "Intel" },
                    indicators = new[] { "203.0.113.10", "update-check.example", "loader.dll" }, techniques = new[] { "T1041 Exfiltration Over C2 Channel" }, layout = "CtiEvent" },
                new { minutes = 30, eventType = "Facilitation", values = new Dictionary<string, string> { ["Title"] = "Block the C2", ["IP Address"] = "8.8.8.8" },
                    indicators = new[] { "ws01", "203.0.113.10" }, techniques = Array.Empty<string>(), layout = "Standard" }
            },
            readIn = "<p>Read this.</p>"
        });

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
    }
}
