// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.ViewModels;
using Blueprint.Api.Infrastructure.Mappings;

namespace Blueprint.Api.Services
{
    public interface IGroupService
    {
        Task<IEnumerable<Group>> GetAsync(CancellationToken ct);
        Task<Group> GetAsync(Guid id, CancellationToken ct);
        Task<Group> CreateAsync(Group group, CancellationToken ct);
        Task<Group> UpdateAsync(Guid id, Group group, CancellationToken ct);
        Task DeleteAsync(Guid id, CancellationToken ct);
        Task<GroupMembership> GetMembershipAsync(Guid id, CancellationToken ct);
        Task<IEnumerable<GroupMembership>> GetMembershipsForGroupAsync(Guid groupId, CancellationToken ct);
        Task<GroupMembership> CreateMembershipAsync(GroupMembership groupMembership, CancellationToken ct);
        Task DeleteMembershipAsync(Guid id, CancellationToken ct);
    }

    public class GroupService : IGroupService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;

        public GroupService(
            BlueprintContext context,
            IPrincipal user)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
        }

        public async Task<IEnumerable<Group>> GetAsync(CancellationToken ct)
        {
            var items = await _context.Groups.ToListAsync(ct);

            return items.Select(x => x.ToGroup()).ToList();
        }

        public async Task<Group> GetAsync(Guid id, CancellationToken ct)
        {
            var item = await _context.Groups.SingleOrDefaultAsync(o => o.Id == id, ct);

            return item.ToGroup();
        }

        public async Task<Group> CreateAsync(Group group, CancellationToken ct)
        {
            var groupEntity = group.ToEntity();
            _context.Groups.Add(groupEntity);
            await _context.SaveChangesAsync(ct);
            group = await GetAsync(groupEntity.Id, ct);

            return group;
        }

        public async Task<Group> UpdateAsync(Guid id, Group group, CancellationToken ct)
        {
            var groupToUpdate = await _context.Groups.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (groupToUpdate == null)
                throw new EntityNotFoundException<Group>();

            group.ApplyTo(groupToUpdate);
            await _context.SaveChangesAsync(ct);
            var updatedGroup = groupToUpdate.ToGroup();

            return updatedGroup;
        }

        public async Task DeleteAsync(Guid id, CancellationToken ct)
        {
            var groupToDelete = await _context.Groups.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (groupToDelete == null)
                throw new EntityNotFoundException<Group>();

            _context.Groups.Remove(groupToDelete);
            await _context.SaveChangesAsync(ct);
        }

        public async Task<IEnumerable<GroupMembership>> GetMembershipsForGroupAsync(Guid groupId, CancellationToken ct)
        {
            var items = await _context.GroupMemberships.Where(m => m.GroupId == groupId).ToListAsync(ct);

            return items.Select(x => x.ToGroupMembership()).ToList();
        }

        public async Task<GroupMembership> GetMembershipAsync(Guid id, CancellationToken ct)
        {
            var item = await _context.GroupMemberships.SingleOrDefaultAsync(o => o.Id == id, ct);

            return item.ToGroupMembership();
        }

        public async Task<GroupMembership> CreateMembershipAsync(GroupMembership groupMembership, CancellationToken ct)
        {
            var groupMembershipEntity = groupMembership.ToEntity();
            _context.GroupMemberships.Add(groupMembershipEntity);
            await _context.SaveChangesAsync(ct);
            groupMembership = await GetMembershipAsync(groupMembershipEntity.Id, ct);

            return groupMembership;
        }

        public async Task DeleteMembershipAsync(Guid id, CancellationToken ct)
        {
            var groupMembershipToDelete = await _context.GroupMemberships.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (groupMembershipToDelete == null)
                throw new EntityNotFoundException<GroupMembership>();

            _context.GroupMemberships.Remove(groupMembershipToDelete);
            await _context.SaveChangesAsync(ct);
        }
    }
}
