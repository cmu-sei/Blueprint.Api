// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.ViewModels;
using System.IO;

namespace Blueprint.Api.Services
{
    public interface IPlayerApplicationTeamService
    {
        Task<IEnumerable<ViewModels.PlayerApplicationTeam>> GetAsync(CancellationToken ct);
        Task<ViewModels.PlayerApplicationTeam> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct);
        Task<IEnumerable<ViewModels.PlayerApplicationTeam>> GetByPlayerApplicationAsync(Guid playerApplicationId, bool hasSystemPermission, CancellationToken ct);
        Task<IEnumerable<ViewModels.PlayerApplicationTeam>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.PlayerApplicationTeam> CreateAsync(ViewModels.PlayerApplicationTeam playerApplicationTeam, CancellationToken ct);
        Task<ViewModels.PlayerApplicationTeam> UpdateAsync(Guid id, ViewModels.PlayerApplicationTeam playerApplicationTeam, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
        Task<bool> DeleteByIdsAsync(Guid playerApplicationId, Guid teamId, CancellationToken ct);
    }

    public class PlayerApplicationTeamService : IPlayerApplicationTeamService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public PlayerApplicationTeamService(BlueprintContext context, IPrincipal team, IMapper mapper)
        {
            _context = context;
            _user = team as ClaimsPrincipal;
            _mapper = mapper;
        }

        public async Task<IEnumerable<ViewModels.PlayerApplicationTeam>> GetAsync(CancellationToken ct)
        {
            var items = await _context.PlayerApplicationTeams
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<PlayerApplicationTeam>>(items);
        }

        public async Task<ViewModels.PlayerApplicationTeam> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct)
        {
            var item = await _context.PlayerApplicationTeams
                .Include(ct => ct.PlayerApplication)
                .SingleOrDefaultAsync(o => o.Id == id, ct);
            if (!hasSystemPermission && !(await MselViewRequirement.IsMet(_user.GetId(), item.PlayerApplication.MselId, _context)))
            {
                var mselCheck = await _context.Msels.FindAsync(item.PlayerApplication.MselId);
                if (!mselCheck.IsTemplate)
                    throw new ForbiddenException();
            }

            return _mapper.Map<PlayerApplicationTeam>(item);
        }

        public async Task<IEnumerable<ViewModels.PlayerApplicationTeam>> GetByPlayerApplicationAsync(Guid playerApplicationId, bool hasSystemPermission, CancellationToken ct)
        {
            var playerApplication = await _context.PlayerApplications.FirstOrDefaultAsync(c => c.Id == playerApplicationId, ct);
            if (!hasSystemPermission && !(await MselViewRequirement.IsMet(_user.GetId(), playerApplication.MselId, _context)))
            {
                var mselCheck = await _context.Msels.FindAsync(playerApplication.MselId);
                if (!mselCheck.IsTemplate)
                    throw new ForbiddenException();
            }
            var items = await _context.PlayerApplicationTeams
                .Where(et => et.PlayerApplicationId == playerApplicationId)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<PlayerApplicationTeam>>(items);
        }

        public async Task<IEnumerable<ViewModels.PlayerApplicationTeam>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            if (!hasSystemPermission && !(await MselViewRequirement.IsMet(_user.GetId(), mselId, _context)))
            {
                var mselCheck = await _context.Msels.FindAsync(mselId);
                if (!mselCheck.IsTemplate)
                    throw new ForbiddenException();
            }
            var playerApplicationIds = await _context.PlayerApplications
                .Where(c => c.MselId == mselId)
                .Select(c => c.Id)
                .ToListAsync(ct);
            var items = await _context.PlayerApplicationTeams
                .Where(et => playerApplicationIds.Contains(et.PlayerApplicationId))
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<PlayerApplicationTeam>>(items);
        }

        public async Task<ViewModels.PlayerApplicationTeam> CreateAsync(ViewModels.PlayerApplicationTeam playerApplicationTeam, CancellationToken ct)
        {
            // make sure that the display order is valid
            var itemList = await _context.PlayerApplicationTeams.Where(m => m.TeamId == playerApplicationTeam.TeamId).OrderBy(m => m.DisplayOrder).ToListAsync();
            if (playerApplicationTeam.DisplayOrder > itemList.Count + 1 || playerApplicationTeam.DisplayOrder < 1)
            {
                playerApplicationTeam.DisplayOrder = itemList.Count + 1;
            }
            // create the new item
            var playerApplicationTeamEntity = _mapper.Map<PlayerApplicationTeamEntity>(playerApplicationTeam);
            playerApplicationTeamEntity.Id = playerApplicationTeamEntity.Id != Guid.Empty ? playerApplicationTeamEntity.Id : Guid.NewGuid();
            // save the new item
            _context.PlayerApplicationTeams.Add(playerApplicationTeamEntity);
            await _context.SaveChangesAsync(ct);
            await UpdateOrdering(playerApplicationTeamEntity, itemList, ct);

            return await GetAsync(playerApplicationTeamEntity.Id, true, ct);
        }

        public async Task<ViewModels.PlayerApplicationTeam> UpdateAsync(Guid id, ViewModels.PlayerApplicationTeam playerApplicationTeam, CancellationToken ct)
        {
            // make sure that the display order is valid
            var itemList = await _context.PlayerApplicationTeams.Where(m => m.TeamId == playerApplicationTeam.TeamId && m.Id != playerApplicationTeam.Id).OrderBy(m => m.DisplayOrder).ToListAsync();
            if (playerApplicationTeam.DisplayOrder > itemList.Count + 1 || playerApplicationTeam.DisplayOrder < 1)
                throw new InvalidDataException("The requested display order is not valid");

            var playerApplicationTeamToUpdate = await _context.PlayerApplicationTeams.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (playerApplicationTeamToUpdate == null)
                throw new EntityNotFoundException<PlayerApplicationTeam>();

            _mapper.Map(playerApplicationTeam, playerApplicationTeamToUpdate);

            _context.PlayerApplicationTeams.Update(playerApplicationTeamToUpdate);
            await _context.SaveChangesAsync(ct);

            playerApplicationTeam = await GetAsync(playerApplicationTeamToUpdate.Id, true, ct);
            await UpdateOrdering(playerApplicationTeamToUpdate, itemList, ct);

            return playerApplicationTeam;
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            var playerApplicationTeamToDelete = await _context.PlayerApplicationTeams.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (playerApplicationTeamToDelete == null)
                throw new EntityNotFoundException<PlayerApplicationTeam>();

            _context.PlayerApplicationTeams.Remove(playerApplicationTeamToDelete);
            await _context.SaveChangesAsync(ct);
            // make sure that the remaining display order is valid
            var itemList = await _context.PlayerApplicationTeams.Where(m => m.TeamId == playerApplicationTeamToDelete.TeamId).OrderBy(m => m.DisplayOrder).ToListAsync();
            playerApplicationTeamToDelete.DisplayOrder = itemList.Count + 1;
            await UpdateOrdering(playerApplicationTeamToDelete, itemList, ct);

            return true;
        }

        public async Task<bool> DeleteByIdsAsync(Guid playerApplicationId, Guid teamId, CancellationToken ct)
        {
            var playerApplicationTeamToDelete = await _context.PlayerApplicationTeams.SingleOrDefaultAsync(v => (v.TeamId == teamId) && (v.PlayerApplicationId == playerApplicationId), ct);

            if (playerApplicationTeamToDelete == null)
                throw new EntityNotFoundException<PlayerApplicationTeam>();

            _context.PlayerApplicationTeams.Remove(playerApplicationTeamToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

        private async Task UpdateOrdering(PlayerApplicationTeamEntity newItem, List<PlayerApplicationTeamEntity> itemList, CancellationToken ct)
        {
            // update pre-existing items that come before the new item, if necessary
            for (int index = 0; index < newItem.DisplayOrder - 1; index++)
            {
                var item = itemList[index];
                if (item.DisplayOrder != index + 1)
                {
                    item.DisplayOrder = index + 1;
                }
            }
            // update pre-existing items that come after the new item, if necessary
            for (int index = newItem.DisplayOrder - 1; index < itemList.Count; index++)
            {
                var item = itemList[index];
                if (item.DisplayOrder != index + 2)
                {
                    item.DisplayOrder = index + 2;
                }
            }
            await _context.SaveChangesAsync(ct);

        }

    }
}
