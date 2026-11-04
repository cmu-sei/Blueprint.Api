// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Options;
using Microsoft.AspNetCore.Http;

namespace Blueprint.Api.Services
{
    /// <summary>
    /// A Caster workspace's machines and networks, as GET api/workspaces/{id}/topology answers: the nodes,
    /// which network each NIC is on, and where someone put each node in Caster's topology view.
    /// </summary>
    public class CasterTopology
    {
        public Guid WorkspaceId { get; set; }

        /// <summary>Plan while the latest plan is still to be applied, State after.</summary>
        public string Source { get; set; }

        public List<CasterTopologyNode> Nodes { get; set; } = new();
        public List<CasterTopologyEdge> Edges { get; set; } = new();

        /// <summary>By node id. A node nobody has moved or given a role is absent.</summary>
        public Dictionary<string, CasterTopologyNodeLayout> Layout { get; set; } = new();
    }

    public class CasterTopologyNode
    {
        public string Id { get; set; }

        /// <summary>Machine or Network</summary>
        public string Kind { get; set; }

        public string Type { get; set; }

        /// <summary>The infrastructure name: the VM name, the vnet alias, the port group name.</summary>
        public string Label { get; set; }

        /// <summary>create, update, replace, delete, read or no-op. Null when read from the state.</summary>
        public string Action { get; set; }

        public bool External { get; set; }
        public bool NotCreated { get; set; }
        public Dictionary<string, string> Properties { get; set; } = new();

        /// <summary>Null for a network, and for a machine with no Player record.</summary>
        public CasterTopologyConsole Console { get; set; }

        public bool IsMachine => string.Equals(Kind, "Machine", StringComparison.OrdinalIgnoreCase);
        public bool IsNetwork => string.Equals(Kind, "Network", StringComparison.OrdinalIgnoreCase);

        /// <summary>The machine's name in Player, which its console URL is addressed by. Null without one.</summary>
        public string PlayerName => string.IsNullOrWhiteSpace(Console?.Name) ? null : Console.Name.Trim();
    }

    public class CasterTopologyConsole
    {
        public List<string> TeamIds { get; set; } = new();
        public bool TeamsUnknown { get; set; }
        public string VmId { get; set; }

        /// <summary>The machine's name in Player, as its console shows it.</summary>
        public string Name { get; set; }
    }

    public class CasterTopologyEdge
    {
        public string From { get; set; }
        public string To { get; set; }
        public int Nic { get; set; }
    }

    /// <summary>
    /// A node's place in Caster's view, in Cytoscape model coordinates: pixels at zoom 1, the node's centre,
    /// y down, from an origin the view chose. The force and tree layouts keep separate positions.
    /// </summary>
    public class CasterTopologyNodeLayout
    {
        public double? X { get; set; }
        public double? Y { get; set; }
        public double? TreeX { get; set; }
        public double? TreeY { get; set; }

        /// <summary>Server, Workstation, DomainController, Firewall, Router, WebServer, Database, MailServer, Linux, Windows or Attacker.</summary>
        public string Role { get; set; }
    }

    public interface ICasterTopologyClient
    {
        /// <summary>The workspace's topology, read with the signed-in user's own token.</summary>
        Task<CasterTopology> GetTopologyAsync(Guid workspaceId, CancellationToken ct);
    }

    /// <summary>
    /// Reads GET api/workspaces/{id}/topology, which the Caster client package does not have, with the
    /// caller's own token as the other Caster calls a planner makes are.
    /// </summary>
    public class CasterTopologyClient : ICasterTopologyClient
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ClientOptions _clientOptions;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public CasterTopologyClient(IHttpClientFactory httpClientFactory, ClientOptions clientOptions, IHttpContextAccessor httpContextAccessor)
        {
            _httpClientFactory = httpClientFactory;
            _clientOptions = clientOptions;
            _httpContextAccessor = httpContextAccessor;
        }

        public async Task<CasterTopology> GetTopologyAsync(Guid workspaceId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_clientOptions.CasterApiUrl))
                throw new ConflictException("No Caster API URL is configured.");
            var httpClient = _httpClientFactory.CreateClient();
            httpClient.BaseAddress = new Uri(_clientOptions.CasterApiUrl);
            string authHeader = _httpContextAccessor.HttpContext?.Request.Headers["Authorization"];
            if (!string.IsNullOrEmpty(authHeader))
                httpClient.DefaultRequestHeaders.Add("Authorization", authHeader);
            try
            {
                return await CasterTopologyReader.ReadAsync(httpClient, workspaceId, ct);
            }
            catch (HttpRequestException ex) when (
                ex.StatusCode == HttpStatusCode.Unauthorized ||
                ex.StatusCode == HttpStatusCode.Forbidden ||
                ex.StatusCode == HttpStatusCode.NotFound)
            {
                throw new ForbiddenException($"Caster workspace {workspaceId} does not exist or you cannot view it in Caster.");
            }
        }
    }

    public static class CasterTopologyReader
    {
        private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

        /// <summary>GET api/workspaces/{id}/topology over a client that already carries its token.</summary>
        public static async Task<CasterTopology> ReadAsync(HttpClient casterHttpClient, Guid workspaceId, CancellationToken ct)
        {
            using var response = await casterHttpClient.GetAsync($"api/workspaces/{workspaceId}/topology", ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Caster answered {(int)response.StatusCode} for workspace {workspaceId}'s topology.", null, response.StatusCode);
            return Parse(body);
        }

        public static CasterTopology Parse(string json)
        {
            var topology = JsonSerializer.Deserialize<CasterTopology>(json, _json) ?? new CasterTopology();
            topology.Nodes ??= new();
            topology.Edges ??= new();
            topology.Layout ??= new();
            topology.Nodes.RemoveAll(n => n == null || string.IsNullOrEmpty(n.Id));
            topology.Edges.RemoveAll(e => e == null);
            return topology;
        }
    }
}
