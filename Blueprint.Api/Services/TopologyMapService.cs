// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Player.Api.Client;

namespace Blueprint.Api.Services
{
    public interface ITopologyMapService
    {
        /// <summary>Draws the MSEL's map from its Caster workspace's topology. Nothing is sent to Player.</summary>
        Task<ViewModels.TopologyMapPreview> PreviewAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);

        /// <summary>Draws the map and creates or replaces it in Player, adding the Network Map application if missing.</summary>
        Task<ViewModels.TopologyMapResult> UpdateAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
    }

    /// <summary>
    /// Update map and its preview. Caster is read with the signed-in user's own token, as their other
    /// Caster calls are; Player is written as the service account, as the push writes it.
    /// </summary>
    public class TopologyMapService : ITopologyMapService
    {
        private readonly BlueprintContext _context;
        private readonly ICasterTopologyClient _casterTopologyClient;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ClientOptions _clientOptions;
        private readonly ResourceOwnerAuthorizationOptions _serviceAccount;
        private readonly ClaimsPrincipal _user;

        public TopologyMapService(
            BlueprintContext context,
            ICasterTopologyClient casterTopologyClient,
            IHttpClientFactory httpClientFactory,
            ClientOptions clientOptions,
            ResourceOwnerAuthorizationOptions serviceAccount,
            IPrincipal user)
        {
            _context = context;
            _casterTopologyClient = casterTopologyClient;
            _httpClientFactory = httpClientFactory;
            _clientOptions = clientOptions;
            _serviceAccount = serviceAccount;
            _user = user as ClaimsPrincipal;
        }

        public async Task<ViewModels.TopologyMapPreview> PreviewAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            var msel = await AuthorizeAsync(mselId, hasSystemPermission, ct);
            var topology = await _casterTopologyClient.GetTopologyAsync((Guid)msel.CasterWorkspaceId, ct);
            return TopologyMapPublisher.Preview(msel, topology, TopologyMapPublisher.Settings(msel));
        }

        public async Task<ViewModels.TopologyMapResult> UpdateAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            var msel = await AuthorizeAsync(mselId, hasSystemPermission, ct);
            if (msel.PlayerViewId == null)
                throw new ConflictException("The MSEL has no Player view yet. Push it to Player first.");
            if (string.IsNullOrWhiteSpace(_clientOptions.VmApiUrl))
                throw new ConflictException("No Player VM API URL is configured (ClientSettings:VmApiUrl), so Blueprint cannot write the map.");

            var topology = await _casterTopologyClient.GetTopologyAsync((Guid)msel.CasterWorkspaceId, ct);
            using var tokenClient = _httpClientFactory.CreateClient();
            var token = await ApiClientsExtensions.RequestTokenAsync(_serviceAccount, tokenClient);
            if (token.IsError)
                throw new Exception($"Could not get a token for Player: {token.Error}");
            var vmHttpClient = ApiClientsExtensions.GetHttpClient(_httpClientFactory, _clientOptions.VmApiUrl, token);
            var playerApiClient = IntegrationPlayerExtensions.GetPlayerApiClient(_httpClientFactory, _clientOptions.PlayerApiUrl, token);

            var result = await TopologyMapPublisher.PublishAsync(msel.Id, topology, TopologyMapPublisher.Settings(msel), vmHttpClient, playerApiClient, _context, _clientOptions, _user?.GetId(), ct);
            if (result == null)
                throw new ConflictException("The Caster workspace has no machines yet, so there is nothing to map. Apply the workspace, or check its topology in Caster.");
            return result;
        }

        private async Task<MselEntity> AuthorizeAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            var msel = await _context.Msels.AsNoTracking().SingleOrDefaultAsync(m => m.Id == mselId, ct)
                ?? throw new EntityNotFoundException<MselEntity>("MSEL not found: " + mselId);
            if (!hasSystemPermission && !await MselOwnerRequirement.IsMet(_user.GetId(), mselId, _context))
                throw new ForbiddenException();
            if (!msel.UsePlayer || !msel.UseCaster)
                throw new ConflictException("The network map is for a MSEL that uses both Player and Caster.");
            if (msel.CasterWorkspaceId == null)
                throw new ConflictException("The MSEL has no Caster workspace yet. The push creates it.");
            return msel;
        }
    }

    /// <summary>
    /// Turns a Caster topology into a Player VM map for a MSEL's view: the drawing as the map's image, a
    /// click point per machine with a Player name, and a "Network Map" application for the teams. Shared by
    /// Update map and the push.
    /// </summary>
    public static class TopologyMapPublisher
    {
        public const string ApplicationName = "Network Map";

        /// <summary>The Blueprint application's URL. Player replaces {theme} itself.</summary>
        public const string ApplicationUrl = "{vmUrl}/views/{playerViewId}/map{theme}";

        private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

        /// <summary>
        /// The map's id in Player, the same every time for one view, so an update finds the map it made
        /// before and leaves any other map in the view alone.
        /// </summary>
        public static Guid MapId(Guid playerViewId)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes("blueprint-topology-map:" + playerViewId.ToString("D")));
            var bytes = hash[..16];
            bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);
            bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
            return new Guid(bytes);
        }

        /// <summary>The MSEL's own map settings. None are stored yet, so the defaults.</summary>
        public static TopologyMapSettings Settings(MselEntity msel) => new();

        public static ViewModels.TopologyMapPreview Preview(MselEntity msel, CasterTopology topology, TopologyMapSettings settings)
        {
            var drawing = TopologyMapRenderer.Render(topology, settings);
            return new ViewModels.TopologyMapPreview
            {
                MselId = msel.Id,
                CasterWorkspaceId = msel.CasterWorkspaceId ?? topology.WorkspaceId,
                Source = topology.Source,
                Svg = drawing.Svg,
                ImageUrl = drawing.ImageUrl,
                ClickPoints = drawing.ClickPoints,
                MachineCount = drawing.MachineCount,
                NetworkCount = drawing.NetworkCount,
                MachinesWithoutConsole = drawing.MachinesWithoutConsole
            };
        }

        /// <summary>Whether a Blueprint or Player application is the view's Network Map.</summary>
        public static bool IsNetworkMapApplication(string name, string url) =>
            string.Equals(name?.Trim(), ApplicationName, StringComparison.OrdinalIgnoreCase) ||
            (url != null && _mapUrl.IsMatch(url));

        private static readonly Regex _mapUrl = new(@"/views/(\{playerViewId\}|[0-9a-fA-F-]{36})/map(\{theme\}|[/?#]|$)", RegexOptions.IgnoreCase);

        /// <summary>
        /// Creates or replaces the view's map and makes sure its teams can open it. Null, with nothing
        /// sent, when the topology has no machines.
        /// </summary>
        public static async Task<ViewModels.TopologyMapResult> PublishAsync(
            Guid mselId,
            CasterTopology topology,
            TopologyMapSettings settings,
            HttpClient vmHttpClient,
            PlayerApiClient playerApiClient,
            BlueprintContext context,
            ClientOptions clientOptions,
            Guid? userId,
            CancellationToken ct)
        {
            var msel = await context.Msels.AsNoTracking()
                .Include(m => m.Teams)
                .SingleAsync(m => m.Id == mselId, ct);
            var viewId = (Guid)msel.PlayerViewId;
            var preview = Preview(msel, topology, settings);
            if (preview.MachineCount == 0)
                return null;

            // the teams that can open a machine on the map, and the view's Admin team; every team when the
            // topology names none, as before apply
            var viewTeams = (await playerApiClient.GetViewTeamsAsync(viewId, ct)).ToList();
            var consoleTeams = topology.Nodes
                .Where(n => n.IsMachine && n.Console?.TeamIds != null)
                .SelectMany(n => n.Console.TeamIds)
                .Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty)
                .ToHashSet();
            var teamIds = viewTeams
                .Where(t => consoleTeams.Contains(t.Id) || IntegrationPlayerExtensions.IsAdminTeam(t.Name))
                .Select(t => t.Id)
                .ToList();
            if (!teamIds.Any(consoleTeams.Contains))
                teamIds = viewTeams.Select(t => t.Id).ToList();

            var mapId = MapId(viewId);
            var form = new
            {
                id = mapId,
                name = string.IsNullOrWhiteSpace(settings.Title) ? ApplicationName : settings.Title.Trim(),
                imageUrl = preview.ImageUrl,
                teamIds,
                coordinates = preview.ClickPoints.Select(p => new
                {
                    xPosition = p.XPosition,
                    yPosition = p.YPosition,
                    radius = p.Radius,
                    label = p.Label ?? "",
                    urls = p.Urls
                }).ToList()
            };

            bool created;
            using (var existing = await vmHttpClient.GetAsync($"api/views/maps/{mapId}", ct))
            {
                if (existing.StatusCode == HttpStatusCode.NotFound)
                {
                    await SendAsync(vmHttpClient, HttpMethod.Post, $"api/views/{viewId}/map", form, ct);
                    created = true;
                }
                else
                {
                    await EnsureSuccessAsync(existing, "read the map", ct);
                    await SendAsync(vmHttpClient, HttpMethod.Put, $"api/views/maps/{mapId}", form, ct);
                    created = false;
                }
            }

            var result = new ViewModels.TopologyMapResult
            {
                Map = preview,
                PlayerMapId = mapId,
                Created = created,
                TeamIds = teamIds
            };
            await EnsureBlueprintApplicationAsync(msel, teamIds, context, userId, ct);
            await EnsurePlayerApplicationAsync(viewId, teamIds, playerApiClient, clientOptions, result, ct);
            return result;
        }

        // The MSEL's own row, so a later push makes the application with the rest
        private static async Task EnsureBlueprintApplicationAsync(MselEntity msel, List<Guid> teamIds, BlueprintContext context, Guid? userId, CancellationToken ct)
        {
            var applications = await context.PlayerApplications
                .Include(a => a.PlayerApplicationTeams)
                .Where(a => a.MselId == msel.Id)
                .ToListAsync(ct);
            var application = applications.FirstOrDefault(a => IsNetworkMapApplication(a.Name, a.Url));
            if (application == null)
            {
                application = new PlayerApplicationEntity
                {
                    Id = Guid.NewGuid(),
                    MselId = msel.Id,
                    Name = ApplicationName,
                    Url = ApplicationUrl,
                    Embeddable = true,
                    LoadInBackground = false,
                    CreatedBy = userId ?? msel.CreatedBy
                };
                context.PlayerApplications.Add(application);
            }

            var mselTeamIds = msel.Teams.Select(t => t.Id).ToHashSet();
            foreach (var teamId in teamIds.Where(mselTeamIds.Contains))
            {
                if (application.PlayerApplicationTeams.Any(t => t.TeamId == teamId))
                    continue;
                var order = await context.PlayerApplicationTeams.CountAsync(t => t.TeamId == teamId, ct) + 1;
                context.PlayerApplicationTeams.Add(new PlayerApplicationTeamEntity(application.Id, teamId) { Id = Guid.NewGuid(), DisplayOrder = order });
            }
            await context.SaveChangesAsync(ct);
        }

        // The view's own application, for a view already pushed: one per view, on every map team
        private static async Task EnsurePlayerApplicationAsync(Guid viewId, List<Guid> teamIds, PlayerApiClient playerApiClient, ClientOptions clientOptions, ViewModels.TopologyMapResult result, CancellationToken ct)
        {
            var applications = await playerApiClient.GetViewApplicationsAsync(viewId, ct);
            var application = applications.FirstOrDefault(a => IsNetworkMapApplication(a.Name, a.Url?.OriginalString));
            if (application == null)
            {
                var url = ApplicationUrl
                    .Replace("{vmUrl}", (clientOptions.VmUiUrl ?? "").TrimEnd('/'))
                    .Replace("{playerViewId}", viewId.ToString());
                application = await playerApiClient.CreateApplicationAsync(viewId, new Application
                {
                    Name = ApplicationName,
                    ViewId = viewId,
                    Url = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null,
                    Embeddable = true,
                    LoadInBackground = false
                }, ct);
                result.ApplicationAdded = true;
            }

            foreach (var teamId in teamIds)
            {
                var instances = await playerApiClient.GetTeamApplicationInstancesAsync(teamId, ct);
                if (instances.Any(i => i.ApplicationId == application.Id))
                    continue;
                await playerApiClient.CreateApplicationInstanceAsync(teamId, new ApplicationInstanceForm
                {
                    TeamId = teamId,
                    ApplicationId = (Guid)application.Id,
                    DisplayOrder = instances.Count + 1
                }, ct);
                result.ApplicationTeamIds.Add(teamId);
            }
        }

        private static async Task SendAsync(HttpClient client, HttpMethod method, string path, object body, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, options: _json) };
            using var response = await client.SendAsync(request, ct);
            await EnsureSuccessAsync(response, method == HttpMethod.Post ? "create the map" : "update the map", ct);
        }

        private static async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken ct)
        {
            if (response.IsSuccessStatusCode)
                return;
            var body = await response.Content.ReadAsStringAsync(ct);
            if (body.Length > 300)
                body = body[..300];
            throw new HttpRequestException($"Player's VM API refused to {what}: {(int)response.StatusCode} {body}".Trim(), null, response.StatusCode);
        }
    }
}
