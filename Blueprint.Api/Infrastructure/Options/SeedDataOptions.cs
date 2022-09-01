// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using Blueprint.Api.Data.Models;
using System.Collections.Generic;

namespace Blueprint.Api.Infrastructure.Options
{
    public class SeedDataOptions
    {
        public List<PermissionEntity> Permissions { get; set; }
        public List<UserEntity> Users { get; set; }
        public List<UserPermissionEntity> UserPermissions { get; set; }
        public List<TeamEntity> Teams { get; set; }
        public List<TeamUserEntity> TeamUsers { get; set; }
        public List<MselEntity> Msels { get; set; }
        public List<MselTeamEntity> MselTeams { get; set; }
        public List<MoveEntity> Moves { get; set; }
        public List<OrganizationEntity> Organizations { get; set; }
        public List<ScenarioEventEntity> ScenarioEvents { get; set; }
        public List<DataFieldEntity> DataFields { get; set; }
        public List<UserMselRoleEntity> UserMselRoles { get; set; }
    }
}

