// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Services;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Options;

namespace Blueprint.Api.Hubs
{
    [Authorize(AuthenticationSchemes = "Bearer")]
    public class MainHub : Hub
    {
        private readonly ITeamService _teamService;
        private readonly IMselService _mselService;
        private readonly BlueprintContext _context;
        private readonly DatabaseOptions _options;
        private readonly CancellationToken _ct;
        private readonly IBlueprintAuthorizationService _blueprintAuthorizationService;
        private readonly IPresenceTracker _presence;
        private readonly ILogger<MainHub> _logger;
        public const string ADMIN_DATA_GROUP = "AdminDataGroup";
        public const string GROUP_GROUP = "AdminGroupGroup";
        public const string ROLE_GROUP = "AdminRoleGroup";
        public const string USER_GROUP = "AdminUserGroup";

        public MainHub(
            ITeamService teamService,
            IMselService mselService,
            BlueprintContext context,
            DatabaseOptions options,
            IBlueprintAuthorizationService blueprintAuthorizationService,
            IPresenceTracker presence,
            ILogger<MainHub> logger
        )
        {
            _teamService = teamService;
            _mselService = mselService;
            _context = context;
            _options = options;
            CancellationTokenSource source = new CancellationTokenSource();
            _ct = source.Token;
            _blueprintAuthorizationService = blueprintAuthorizationService;
            _presence = presence;
            _logger = logger;
        }

        public async Task Join()
        {
            var userId = Context.User.Identities.First().Claims.First(c => c.Type == "sub")?.Value;
            var idList = await GetMselIdList(userId);
            idList.AddRange(await GetUnitIdList(userId));
            idList.Add(userId);
            foreach (var id in idList)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, id.ToString());
            }
        }

        public async Task Leave()
        {
            var userId = Context.User.Identities.First().Claims.First(c => c.Type == "sub")?.Value;
            var idList = await GetMselIdList(userId);
            idList.Add(userId);
            foreach (var id in idList)
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, id.ToString());
            }

            await LeavePresence();
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            // Forget the connection first, so that a send that fails can't leave it listed.
            await LeavePresence(swallowFailures: true);
            await base.OnDisconnectedAsync(exception);
        }

        /// <summary>
        /// Makes the connection the caller's only one on a MSEL, or on none when <paramref name="args"/> is
        /// empty, and tells the MSEL's group who is there when that changed it.
        /// </summary>
        /// <returns>
        /// Everyone present on the MSEL now, the caller included, as of a version; null when no MSEL was
        /// joined. An update can reach the caller before this answer does, so the caller keeps whichever has
        /// the higher version.
        /// </returns>
        public async Task<MselPresence> SelectMsel(Guid[] args)
        {
            // leave all other MSELs
            var userId = Context.User.Identities.First().Claims.First(c => c.Type == "sub")?.Value;
            var idList = await GetMselIdList(userId);
            var selected = args.Count() == 1 && idList.Contains(args[0].ToString()) ? args[0] : (Guid?)null;

            // Selecting the MSEL the connection is already on changes nothing for the others.
            await LeavePresence(swallowFailures: true, except: selected);

            foreach (var id in idList)
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, id.ToString());
            }
            // join the selected MSEL
            if (selected is not null)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, selected.Value.ToString());

                var change = _presence.Join(selected.Value, Context.ConnectionId, GetPresentUser(userId));

                // The connection can have closed while this was waiting. Its disconnect has then already
                // looked for what it had open and may have found nothing, so a join that registered it now
                // must not leave it registered.
                if (Context.ConnectionAborted.IsCancellationRequested)
                {
                    await LeavePresence(swallowFailures: true);
                    return null;
                }

                if (change.Changed)
                {
                    await SendPresence(change.State);
                }

                return ToWire(change.State);
            }

            return null;
        }

        public async Task JoinAdmin()
        {
            var idList = await GetAdminIdList();
            foreach (var id in idList)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, id.ToString());
            }
        }

        public async Task LeaveAdmin()
        {
            var idList = await GetAdminIdList();
            foreach (var id in idList)
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, id.ToString());
            }
        }

        private PresentUser GetPresentUser(string userId)
        {
            var name = Context.User.Identities.First().Claims.FirstOrDefault(c => c.Type == "name")?.Value;
            return new PresentUser(Guid.Parse(userId), string.IsNullOrWhiteSpace(name) ? "Unknown" : name);
        }

        /// <summary>
        /// Takes the connection out of the presence of every MSEL it was in, except <paramref name="except"/>,
        /// telling those that changed.
        /// </summary>
        private async Task LeavePresence(bool swallowFailures = false, Guid? except = null)
        {
            var changes = except is null
                ? _presence.LeaveAll(Context.ConnectionId)
                : [.. _presence.ScopesOf(Context.ConnectionId)
                    .Where(x => x != except)
                    .Select(x => _presence.Leave(x, Context.ConnectionId))
                    .Where(x => x is not null)];

            foreach (var change in changes.Where(x => x.Changed))
            {
                if (!swallowFailures)
                {
                    await SendPresence(change.State);
                    continue;
                }

                // A send that fails only costs the others one update, and must not stop the ones after it.
                try
                {
                    await SendPresence(change.State);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not send presence for MSEL {MselId}", change.State.ScopeId);
                }
            }
        }

        private Task SendPresence(PresenceState state) =>
            Clients.Group(state.ScopeId.ToString()).SendAsync(MainHubMethods.PresenceChanged, ToWire(state));

        // The wire names the scope for what it is here.
        private static MselPresence ToWire(PresenceState state) =>
            new(state.ScopeId, state.Version, state.Users);

        private async Task<List<string>> GetMselIdList(string userId)
        {
            var userGuid = Guid.Parse(userId);
            var idList = new List<string>();
            // every MSEL's live data goes to those who may view every MSEL
            if (await _blueprintAuthorizationService.AuthorizeAsync([SystemPermission.ViewMsels], _ct))
            {
                idList = await _context.Msels
                    .Where(m => m.Status != Data.Enumerations.MselItemStatus.Archived)
                    .Select(m => m.Id.ToString())
                    .ToListAsync();
            }
            else
            {
                var unitIdList = await _context.UnitUsers
                    .Where(tu => tu.UserId == userGuid)
                    .Select(tu => tu.UnitId)
                    .ToListAsync();
                // get my unit's msels, where I hold a role. A MSEL's live data carries every event and cell,
                // hidden or not, so it goes to contributors only: a unit member with no role is at most a
                // participant, shown only the Exercise View
                var unitMselIds = _context.MselUnits
                    .Where(mu => unitIdList.Contains(mu.UnitId) && mu.Msel.Status != Data.Enumerations.MselItemStatus.Archived)
                    .Where(mu => _context.UserMselRoles.Any(umr => umr.UserId == userGuid && umr.MselId == mu.MselId))
                    .Select(mu => mu.MselId.ToString());
                var unitMselIdList = await unitMselIds
                    .Where(id => id != null)
                    .ToListAsync();
                // get msels I created and all templates
                var myMselIdList = await _context.Msels
                    .Where(m => (m.CreatedBy == userGuid || m.IsTemplate) && m.Status != Data.Enumerations.MselItemStatus.Archived)
                    .Select(m => m.Id.ToString())
                    .ToListAsync();
                // combine lists
                var mselIdList = unitMselIdList.Union(myMselIdList);
                idList.AddRange(mselIdList);
            }

            return idList;
        }

        private async Task<List<string>> GetUnitIdList(string userId)
        {
            var userGuid = Guid.Parse(userId);
            var unitIdList = await _context.UnitUsers
                .Where(tu => tu.UserId == userGuid)
                .Select(tu => tu.UnitId.ToString())
                .ToListAsync();

            return unitIdList;
        }

        private async Task<List<string>> GetAdminIdList()
        {
            var ct = new CancellationToken();
            var idList = new List<string>();
            var userId = Context.User.Identities.First().Claims.First(c => c.Type == "sub")?.Value;
            idList.Add(userId);
            // content developer or system admin
            if (await _blueprintAuthorizationService.AuthorizeAsync([SystemPermission.EditMsels], _ct))
            {
                idList.Add(ADMIN_DATA_GROUP);
            }
            // Groups permission
            if (await _blueprintAuthorizationService.AuthorizeAsync([SystemPermission.ViewGroups], ct))
            {
                idList.Add(GROUP_GROUP);
            }
            // Roles permission
            if (await _blueprintAuthorizationService.AuthorizeAsync([SystemPermission.ViewRoles], ct))
            {
                idList.Add(ROLE_GROUP);
            }
            // Users permission
            if (await _blueprintAuthorizationService.AuthorizeAsync([SystemPermission.ViewUsers], ct))
            {
                idList.Add(USER_GROUP);
            }

            return idList;
        }

    }

    /// <summary>Everyone present on a MSEL as of a version, as the hub sends it.</summary>
    public record MselPresence(Guid MselId, long Version, IReadOnlyList<PresentUser> Users);

    public static class MainHubMethods
    {
        public const string CardCreated = "CardCreated";
        public const string CardUpdated = "CardUpdated";
        public const string CardDeleted = "CardDeleted";
        public const string CardTeamCreated = "CardTeamCreated";
        public const string CardTeamUpdated = "CardTeamUpdated";
        public const string CardTeamDeleted = "CardTeamDeleted";
        public const string CatalogCreated = "CatalogCreated";
        public const string CatalogUpdated = "CatalogUpdated";
        public const string CatalogDeleted = "CatalogDeleted";
        public const string CiteActionCreated = "CiteActionCreated";
        public const string CiteActionUpdated = "CiteActionUpdated";
        public const string CiteActionDeleted = "CiteActionDeleted";
        public const string CiteDutyCreated = "CiteDutyCreated";
        public const string CiteDutyUpdated = "CiteDutyUpdated";
        public const string CiteDutyDeleted = "CiteDutyDeleted";
        public const string DataFieldCreated = "DataFieldCreated";
        public const string DataFieldUpdated = "DataFieldUpdated";
        public const string DataFieldDeleted = "DataFieldDeleted";
        public const string DataValueCreated = "DataValueCreated";
        public const string DataValueUpdated = "DataValueUpdated";
        public const string DataValueDeleted = "DataValueDeleted";
        public const string InjectCreated = "InjectCreated";
        public const string InjectUpdated = "InjectUpdated";
        public const string InjectDeleted = "InjectDeleted";
        public const string InjectTypeCreated = "InjectTypeCreated";
        public const string InjectTypeUpdated = "InjectTypeUpdated";
        public const string InjectTypeDeleted = "InjectTypeDeleted";
        public const string MselCreated = "MselCreated";
        public const string MselUpdated = "MselUpdated";
        public const string MselDeleted = "MselDeleted";
        public const string IntegrationStatusUpdated = "IntegrationStatusUpdated";
        public const string CasterIdsUpdated = "CasterIdsUpdated";

        public const string MoveCreated = "MoveCreated";
        public const string MoveUpdated = "MoveUpdated";
        public const string MoveDeleted = "MoveDeleted";
        public const string MselTeamCreated = "MselTeamCreated";
        public const string MselTeamUpdated = "MselTeamUpdated";
        public const string MselTeamDeleted = "MselTeamDeleted";
        public const string OrganizationCreated = "OrganizationCreated";
        public const string OrganizationUpdated = "OrganizationUpdated";
        public const string OrganizationDeleted = "OrganizationDeleted";
        public const string PlayerApplicationCreated = "PlayerApplicationCreated";
        public const string PlayerApplicationUpdated = "PlayerApplicationUpdated";
        public const string PlayerApplicationDeleted = "PlayerApplicationDeleted";
        public const string PlayerApplicationTeamCreated = "PlayerApplicationTeamCreated";
        public const string PlayerApplicationTeamUpdated = "PlayerApplicationTeamUpdated";
        public const string PlayerApplicationTeamDeleted = "PlayerApplicationTeamDeleted";
        public const string ScenarioEventCreated = "ScenarioEventCreated";
        public const string ScenarioEventUpdated = "ScenarioEventUpdated";
        public const string ScenarioEventDeleted = "ScenarioEventDeleted";
        public const string TeamCreated = "TeamCreated";
        public const string TeamUpdated = "TeamUpdated";
        public const string TeamDeleted = "TeamDeleted";
        public const string TeamUserCreated = "TeamUserCreated";
        public const string TeamUserUpdated = "TeamUserUpdated";
        public const string TeamUserDeleted = "TeamUserDeleted";
        public const string UnitCreated = "UnitCreated";
        public const string UnitUpdated = "UnitUpdated";
        public const string UnitDeleted = "UnitDeleted";
        public const string UnitUserCreated = "UnitUserCreated";
        public const string UnitUserUpdated = "UnitUserUpdated";
        public const string UnitUserDeleted = "UnitUserDeleted";
        public const string UserCreated = "UserCreated";
        public const string UserUpdated = "UserUpdated";
        public const string UserDeleted = "UserDeleted";
        public const string UserMselRoleCreated = "UserMselRoleCreated";
        public const string UserMselRoleUpdated = "UserMselRoleUpdated";
        public const string UserMselRoleDeleted = "UserMselRoleDeleted";
        public const string GroupCreated = "GroupCreated";
        public const string GroupUpdated = "GroupUpdated";
        public const string GroupDeleted = "GroupDeleted";
        public const string GroupMembershipCreated = "GroupMembershipCreated";
        public const string GroupMembershipUpdated = "GroupMembershipUpdated";
        public const string GroupMembershipDeleted = "GroupMembershipDeleted";
        public const string SystemRoleCreated = "SystemRoleCreated";
        public const string SystemRoleUpdated = "SystemRoleUpdated";
        public const string SystemRoleDeleted = "SystemRoleDeleted";
        public const string PresenceChanged = "PresenceChanged";
    }
}
