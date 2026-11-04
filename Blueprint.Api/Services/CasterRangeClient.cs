// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Infrastructure.Options;
using Microsoft.AspNetCore.Http;

namespace Blueprint.Api.Services
{
    /// <summary>A Caster workspace's machines and networks, as an AI build names them in its documents</summary>
    public interface ICasterRangeClient
    {
        /// <summary>The workspace's machines and networks, read with the signed-in user's own token</summary>
        Task<CasterRange> GetRangeAsync(Guid workspaceId, CancellationToken ct);
    }

    /// <summary>
    /// Reads GET api/workspaces/{id}/topology, which the Caster client package does not have yet, with the caller's own token,
    /// as the other Caster calls a planner makes are.
    /// </summary>
    public class CasterRangeClient : ICasterRangeClient
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ClientOptions _clientOptions;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public CasterRangeClient(IHttpClientFactory httpClientFactory, ClientOptions clientOptions, IHttpContextAccessor httpContextAccessor)
        {
            _httpClientFactory = httpClientFactory;
            _clientOptions = clientOptions;
            _httpContextAccessor = httpContextAccessor;
        }

        public async Task<CasterRange> GetRangeAsync(Guid workspaceId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_clientOptions.CasterApiUrl))
                throw new InvalidOperationException("No Caster API URL is configured.");
            var httpClient = _httpClientFactory.CreateClient();
            httpClient.BaseAddress = new Uri(_clientOptions.CasterApiUrl);
            string authHeader = _httpContextAccessor.HttpContext?.Request.Headers["Authorization"];
            if (!string.IsNullOrEmpty(authHeader))
                httpClient.DefaultRequestHeaders.Add("Authorization", authHeader);
            using var response = await httpClient.GetAsync($"api/workspaces/{workspaceId}/topology", ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Caster answered {(int)response.StatusCode} for the workspace's topology.", null, response.StatusCode);
            using var document = JsonDocument.Parse(body);
            return CasterRange.Parse(document.RootElement);
        }
    }

    /// <summary>
    /// What an AI build takes from a Caster workspace's topology: the defenders' machines with their addresses and networks,
    /// the adversary's machines, and the address ranges of the networks outside. Caster's machine nodes may carry
    /// Properties["ip_addresses"] (comma separated, in NIC order), ["ip_addresses_by_network"] ("networkNodeId=address;...")
    /// and ["gateway"], and network nodes Properties["cidr"] (comma separated, IPv4 first); without them a machine still
    /// gives its name.
    /// </summary>
    public class CasterRange
    {
        public const string IpAddressesProperty = "ip_addresses";
        public const string IpAddressesByNetworkProperty = "ip_addresses_by_network";
        public const string GatewayProperty = "gateway";
        public const string CidrProperty = "cidr";

        public List<CasterRangeHost> Hosts { get; set; } = new List<CasterRangeHost>();
        /// <summary>Machines the topology marks as the attacker's, or that sit only on a network outside</summary>
        public List<CasterRangeHost> Attackers { get; set; } = new List<CasterRangeHost>();
        /// <summary>The ranges of the networks outside (internet, external, red team), which adversary addresses may come from</summary>
        public List<string> ExternalNetworks { get; set; } = new List<string>();

        // the roles Caster's topology view gives a machine, by name and by number
        private static readonly string[] _roles =
            ["Server", "Workstation", "DomainController", "Firewall", "Router", "WebServer", "Database", "MailServer", "Linux", "Windows", "Attacker"];
        private static readonly Regex _outside = new(@"internet|external|outside|\bwan\b|(?<![a-z])red(?![a-z])|attack|adversary|threat", RegexOptions.IgnoreCase);

        public static CasterRange Parse(JsonElement topology)
        {
            var range = new CasterRange();
            if (topology.ValueKind != JsonValueKind.Object)
                return range;
            var nodes = Array(topology, "nodes").Where(n => n.ValueKind == JsonValueKind.Object).ToList();
            var edges = Array(topology, "edges")
                .Select(e => (From: String(e, "from"), To: String(e, "to"), Nic: e.TryGetProperty("nic", out var nic) && nic.ValueKind == JsonValueKind.Number ? nic.GetInt32() : 0))
                .ToList();
            var roles = new Dictionary<string, string>();
            if (topology.TryGetProperty("layout", out var layout) && layout.ValueKind == JsonValueKind.Object)
                foreach (var entry in layout.EnumerateObject())
                    if (entry.Value.ValueKind == JsonValueKind.Object && entry.Value.TryGetProperty("role", out var role))
                        roles[entry.Name] = role.ValueKind == JsonValueKind.Number && role.TryGetInt32(out var r) && r >= 0 && r < _roles.Length ? _roles[r]
                            : role.ValueKind == JsonValueKind.String ? _roles.FirstOrDefault(x => x.Equals(role.GetString(), StringComparison.OrdinalIgnoreCase)) : null;

            var networks = nodes.Where(n => Kind(n) == "Network").ToDictionary(n => String(n, "id") ?? "", n => n);
            // a network outside is named so (internet, external, red team); a bridge the workspace does not manage, whose id
            // Caster writes as external:<name>, is often the range's own LAN, so it is judged by its name too
            bool IsOutside(JsonElement network) =>
                _outside.IsMatch(String(network, "label") ?? "")
                || _outside.IsMatch(Regex.Replace(String(network, "id") ?? "", "^external:", "", RegexOptions.IgnoreCase));
            foreach (var network in networks.Values.Where(IsOutside))
                if (Cidr(network) is string cidr && !range.ExternalNetworks.Contains(cidr))
                    range.ExternalNetworks.Add(cidr);

            foreach (var machine in nodes.Where(n => Kind(n) == "Machine"))
            {
                var id = String(machine, "id") ?? "";
                var label = String(machine, "label");
                if (string.IsNullOrWhiteSpace(label))
                    continue;
                var attached = edges.Where(e => e.From == id).OrderBy(e => e.Nic)
                    .Select(e => networks.TryGetValue(e.To ?? "", out var n) ? n : default)
                    .Where(n => n.ValueKind == JsonValueKind.Object)
                    .ToList();
                var inside = attached.Where(n => !IsOutside(n)).ToList();
                var network = inside.FirstOrDefault(n => Cidr(n) != null);
                if (network.ValueKind != JsonValueKind.Object)
                    network = inside.FirstOrDefault();
                // the address on the host's network first, where Caster says which address is on which network
                var byNetwork = (Property(machine, IpAddressesByNetworkProperty) ?? "").Split(';')
                    .Select(pair => pair.LastIndexOf('=') is var at && at > 0 ? (Network: pair[..at].Trim(), Address: pair[(at + 1)..].Trim()) : (null, null))
                    .Where(pair => pair.Network != null && IPAddress.TryParse(pair.Address, out _))
                    .ToList();
                var networkId = network.ValueKind == JsonValueKind.Object ? String(network, "id") : null;
                var addresses = byNetwork.Where(pair => pair.Network == networkId).Select(pair => pair.Address)
                    .Concat((Property(machine, IpAddressesProperty) ?? "").Split(',').Select(a => a.Trim()))
                    .Where(a => IPAddress.TryParse(a, out _)).Distinct().ToList();
                var host = new CasterRangeHost
                {
                    Name = label.Trim(),
                    Role = roles.TryGetValue(id, out var role) ? role : null,
                    IpAddresses = addresses,
                    Gateway = Property(machine, GatewayProperty)?.Trim() is string gateway && IPAddress.TryParse(gateway, out _) ? gateway : null,
                    Network = network.ValueKind == JsonValueKind.Object ? Cidr(network) : null,
                    NetworkName = network.ValueKind == JsonValueKind.Object ? String(network, "label") : null
                };
                // the attacker's machine: marked so in the view, or on no network but those outside
                if (host.Role == "Attacker" || (attached.Any() && !inside.Any()))
                {
                    if (host.Network == null)
                        host.Network = attached.Select(Cidr).FirstOrDefault(c => c != null);
                    range.Attackers.Add(host);
                }
                else if (!range.Hosts.Any(h => h.Name.Equals(host.Name, StringComparison.OrdinalIgnoreCase)))
                    range.Hosts.Add(host);
            }
            return range;
        }

        private static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
            element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : Enumerable.Empty<JsonElement>();

        private static string String(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        // Caster writes the kind by name; a number is read as the enum's position
        private static string Kind(JsonElement node) =>
            node.TryGetProperty("kind", out var kind)
                ? kind.ValueKind == JsonValueKind.Number ? (kind.GetInt32() == 0 ? "Machine" : "Network")
                : kind.ValueKind == JsonValueKind.String ? (kind.GetString().Equals("Machine", StringComparison.OrdinalIgnoreCase) ? "Machine" : "Network") : null
                : null;

        private static string Property(JsonElement node, string name)
        {
            if (!node.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
                return null;
            foreach (var property in properties.EnumerateObject().Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.ToString();
            return null;
        }

        // the first of the network's ranges, which Caster writes IPv4 first
        private static string Cidr(JsonElement network) =>
            (Property(network, CidrProperty) ?? "").Split(',').Select(c => c.Trim())
                .Select(c => MselIndicatorRules.TryParseNetwork(c, out var parsed) ? parsed.ToString() : null)
                .FirstOrDefault(c => c != null);
    }

    public class CasterRangeHost
    {
        /// <summary>The machine's label in the topology: its VM name</summary>
        public string Name { get; set; }
        /// <summary>The role given it in Caster's topology view, if any</summary>
        public string Role { get; set; }
        public List<string> IpAddresses { get; set; } = new List<string>();
        /// <summary>The machine's first IPv4 gateway, when Caster knows it</summary>
        public string Gateway { get; set; }
        /// <summary>The CIDR of the network it is on, when Caster knows it</summary>
        public string Network { get; set; }
        public string NetworkName { get; set; }
    }
}
