// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Blueprint.Api.Data.Enumerations;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    public class GroupController : BaseController
    {
        private readonly IGroupService _groupService;
        private readonly IBlueprintAuthorizationService _authorizationService;

        public GroupController(IGroupService groupService, IBlueprintAuthorizationService authorizationService)
        {
            _groupService = groupService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets a specific Group by id
        /// </summary>
        /// <remarks>
        /// Returns the Group with the id specified
        /// </remarks>
        /// <param name="id">The id of the Group</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("groups/{id}")]
        [ProducesResponseType(typeof(Group), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getGroup")]
        public async Task<IActionResult> Get([FromRoute] Guid id, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ViewGroups], ct))
                throw new ForbiddenException();

            var group = await _groupService.GetAsync(id, ct);

            if (group == null)
                throw new EntityNotFoundException<Group>();

            return Ok(group);
        }

        /// <summary>
        /// Gets all Groups
        /// </summary>
        /// <remarks>
        /// Returns a list of all Groups
        /// </remarks>
        /// <returns></returns>
        [HttpGet("groups")]
        [ProducesResponseType(typeof(IEnumerable<Group>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getGroups")]
        public async Task<IActionResult> GetAll(CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ViewGroups], ct))
                throw new ForbiddenException();

            var list = await _groupService.GetAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Creates a new Group
        /// </summary>
        /// <remarks>
        /// Creates a new Group with the attributes specified
        /// </remarks>
        /// <param name="group">The data to create the Group with</param>
        /// <param name="ct"></param>
        [HttpPost("groups")]
        [ProducesResponseType(typeof(Group), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createGroup")]
        public async Task<IActionResult> Create([FromBody] Group group, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ManageGroups], ct))
                throw new ForbiddenException();

            var createdGroup = await _groupService.CreateAsync(group, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdGroup.Id }, createdGroup);
        }

        /// <summary>
        /// Updates a Group
        /// </summary>
        /// <remarks>
        /// Updates a Group with the attributes specified
        /// </remarks>
        /// <param name="id">The Id of the Group to update</param>
        /// <param name="group">The updated Group values</param>
        /// <param name="ct"></param>
        [HttpPut("groups/{id}")]
        [ProducesResponseType(typeof(Group), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateGroup")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] Group group, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ManageGroups], ct))
                throw new ForbiddenException();

            var updatedGroup = await _groupService.UpdateAsync(id, group, ct);
            return Ok(updatedGroup);
        }

        /// <summary>
        /// Deletes a Group
        /// </summary>
        /// <remarks>
        /// Deletes a Group with the specified id
        /// </remarks>
        /// <param name="id">The id of the Group to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("groups/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteGroup")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ManageGroups], ct))
                throw new ForbiddenException();

            await _groupService.DeleteAsync(id, ct);
            return NoContent();
        }

        /// <summary>
        /// Gets a specific GroupMembership by id
        /// </summary>
        /// <remarks>
        /// Returns the GroupMembership with the id specified
        /// </remarks>
        /// <param name="id">The id of the GroupMembership</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("groups/memberships/{id}")]
        [ProducesResponseType(typeof(GroupMembership), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getGroupMembership")]
        public async Task<IActionResult> GetGroupMembership([FromRoute] Guid id, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ViewGroups], ct))
                throw new ForbiddenException();

            var groupMembership = await _groupService.GetMembershipAsync(id, ct);

            if (groupMembership == null)
                throw new EntityNotFoundException<GroupMembership>();

            return Ok(groupMembership);
        }

        /// <summary>
        /// Gets all GroupMemberships for a Group
        /// </summary>
        /// <remarks>
        /// Returns a list of all GroupMemberships for the specified Group
        /// </remarks>
        /// <param name="groupId">The id of the Group</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("groups/{groupId}/memberships")]
        [ProducesResponseType(typeof(IEnumerable<GroupMembership>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getGroupMemberships")]
        public async Task<IActionResult> GetMemberships([FromRoute] Guid groupId, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ViewGroups], ct))
                throw new ForbiddenException();

            var list = await _groupService.GetMembershipsForGroupAsync(groupId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Creates a new GroupMembership
        /// </summary>
        /// <remarks>
        /// Creates a new GroupMembership with the attributes specified
        /// </remarks>
        /// <param name="groupId">The id of the Group</param>
        /// <param name="groupMembership">The data to create the GroupMembership with</param>
        /// <param name="ct"></param>
        [HttpPost("groups/{groupId}/memberships")]
        [ProducesResponseType(typeof(GroupMembership), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createGroupMembership")]
        public async Task<IActionResult> CreateMembership([FromRoute] Guid groupId, [FromBody] GroupMembership groupMembership, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ManageGroups], ct))
                throw new ForbiddenException();

            groupMembership.GroupId = groupId;
            var createdGroupMembership = await _groupService.CreateMembershipAsync(groupMembership, ct);
            return CreatedAtAction(nameof(this.GetGroupMembership), new { id = createdGroupMembership.Id }, createdGroupMembership);
        }

        /// <summary>
        /// Deletes a GroupMembership
        /// </summary>
        /// <remarks>
        /// Deletes a GroupMembership with the specified id
        /// </remarks>
        /// <param name="id">The id of the GroupMembership to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("groups/memberships/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteGroupMembership")]
        public async Task<IActionResult> DeleteMembership([FromRoute] Guid id, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ManageGroups], ct))
                throw new ForbiddenException();

            await _groupService.DeleteMembershipAsync(id, ct);
            return NoContent();
        }
    }
}
