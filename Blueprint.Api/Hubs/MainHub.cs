// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Services;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Options;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

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
        private readonly IAuthorizationService _authorizationService;
        public const string ADMIN_DATA_GROUP = "AdminDataGroup";

        public MainHub(
            ITeamService teamService,
            IMselService mselService,
            BlueprintContext context,
            DatabaseOptions options,
            IAuthorizationService authorizationService
        )
        {
            _teamService = teamService;
            _mselService = mselService;
            _context = context;
            _options = options;
            CancellationTokenSource source = new CancellationTokenSource();
            _ct = source.Token;
            _authorizationService = authorizationService;
        }

        public async Task Join()
        {
            var userId = Context.User.Identities.First().Claims.First(c => c.Type == "sub")?.Value;
            var idList = await GetMselIdList(userId);
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
        }

        public async Task SelectMsel(Guid[] args)
        {
            // leave all other MSELs
            var userId = Context.User.Identities.First().Claims.First(c => c.Type == "sub")?.Value;
            var idList = await GetMselIdList(userId);
            foreach (var id in idList)
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, id.ToString());
            }
            // join the selected MSEL
            if (args.Count() == 1)
            {
                var mselId = args[0].ToString();
                if (idList.Contains(mselId))
                {
                    await Groups.AddToGroupAsync(Context.ConnectionId, mselId);
                }
            }
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

        private async Task<List<string>> GetMselIdList(string userId)
        {
            var userGuid = Guid.Parse(userId);
            var idList = new List<string>();
            if ((await _authorizationService.AuthorizeAsync(Context.User, null, new FullRightsRequirement())).Succeeded)
            {
                idList = await _context.Msels
                    .Where(m => m.Status != Data.Enumerations.ItemStatus.Archived)
                    .Select(m => m.Id.ToString())
                    .ToListAsync();
            }
            else
            {
                var teamIdList = await _context.TeamUsers
                    .Where(tu => tu.UserId == userGuid)
                    .Select(tu => tu.TeamId)
                    .ToListAsync();
                // get my teams' msels
                var teamMselIds = _context.Teams
                    .Where(t => teamIdList.Contains(t.Id) && t.Msel.Status != Data.Enumerations.ItemStatus.Archived)
                    .Select(t => t.Msel.Id.ToString());
                var teamMselIdList = await teamMselIds
                    .Where(id => id != null)
                    .ToListAsync();
                // get msels I created and all templates
                var myMselIdList = await _context.Msels
                    .Where(m => (m.CreatedBy == userGuid || m.IsTemplate) && m.Status != Data.Enumerations.ItemStatus.Archived)
                    .Select(m => m.Id.ToString())
                    .ToListAsync();
                // combine lists
                var mselIdList = teamMselIdList.Union(myMselIdList);
                idList.AddRange(mselIdList);
            }

            return idList;
        }

        private async Task<List<string>> GetAdminIdList()
        {
            var idList = new List<string>();
            var userId = Context.User.Identities.First().Claims.First(c => c.Type == "sub")?.Value;
            idList.Add(userId);
            // content developer or system admin
            if ((await _authorizationService.AuthorizeAsync(Context.User, null, new ContentDeveloperRequirement())).Succeeded)
            {
                idList.Add(ADMIN_DATA_GROUP);
            }

            return idList;
        }

    }

    public static class MainHubMethods
    {
        public const string CardCreated = "CardCreated";
        public const string CardUpdated = "CardUpdated";
        public const string CardDeleted = "CardDeleted";
        public const string CardTeamCreated = "CardTeamCreated";
        public const string CardTeamUpdated = "CardTeamUpdated";
        public const string CardTeamDeleted = "CardTeamDeleted";
        public const string CiteActionCreated = "CiteActionCreated";
        public const string CiteActionUpdated = "CiteActionUpdated";
        public const string CiteActionDeleted = "CiteActionDeleted";
        public const string CiteRoleCreated = "CiteRoleCreated";
        public const string CiteRoleUpdated = "CiteRoleUpdated";
        public const string CiteRoleDeleted = "CiteRoleDeleted";
        public const string DataFieldCreated = "DataFieldCreated";
        public const string DataFieldUpdated = "DataFieldUpdated";
        public const string DataFieldDeleted = "DataFieldDeleted";
        public const string DataValueCreated = "DataValueCreated";
        public const string DataValueUpdated = "DataValueUpdated";
        public const string DataValueDeleted = "DataValueDeleted";
        public const string MselCreated = "MselCreated";
        public const string MselUpdated = "MselUpdated";
        public const string MselDeleted = "MselDeleted";
        public const string MselPushStatusChange = "MselPushStatusChange";
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
        public const string UserCreated = "UserCreated";
        public const string UserUpdated = "UserUpdated";
        public const string UserDeleted = "UserDeleted";
        public const string UserMselRoleCreated = "UserMselRoleCreated";
        public const string UserMselRoleUpdated = "UserMselRoleUpdated";
        public const string UserMselRoleDeleted = "UserMselRoleDeleted";
    }
}
