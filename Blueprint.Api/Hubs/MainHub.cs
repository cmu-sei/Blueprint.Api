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
            var idList = await GetIdList();
            foreach (var id in idList)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, id.ToString());
            }
        }

        public async Task Leave()
        {
            var idList = await GetIdList();
            foreach (var id in idList)
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, id.ToString());
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

        private async Task<List<string>> GetIdList()
        {
            var idList = new List<string>();
            var userId = Context.User.Identities.First().Claims.First(c => c.Type == "sub")?.Value;
            idList.Add(userId);
            // user's teams
            var teamList = await  _context.TeamUsers
                .Where(tu => tu.UserId == Guid.Parse(userId))
                .Include(tu => tu.Team)
                .Select(tu => tu.Team)
                .ToListAsync();
            var teamIdList = teamList.Select(t => t.Id.ToString()).ToList();
            idList.AddRange(teamIdList);
            // user's msels
            var mselIdList = await _context.MselTeams
                .Where(m => teamIdList.Contains(m.TeamId.ToString()))
                .Select(m => m.MselId.ToString())
                .ToListAsync();
            idList.AddRange(mselIdList);

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
        public const string MselCreated = "MselCreated";
        public const string MselUpdated = "MselUpdated";
        public const string MselDeleted = "MselDeleted";
        public const string OrganizationCreated = "OrganizationCreated";
        public const string OrganizationUpdated = "OrganizationUpdated";
        public const string OrganizationDeleted = "OrganizationDeleted";
        public const string TeamCreated = "TeamCreated";
        public const string TeamUpdated = "TeamUpdated";
        public const string TeamDeleted = "TeamDeleted";
        public const string TeamUserCreated = "TeamUserCreated";
        public const string TeamUserUpdated = "TeamUserUpdated";
        public const string TeamUserDeleted = "TeamUserDeleted";
        public const string UserCreated = "UserCreated";
        public const string UserUpdated = "UserUpdated";
        public const string UserDeleted = "UserDeleted";
    }
}
