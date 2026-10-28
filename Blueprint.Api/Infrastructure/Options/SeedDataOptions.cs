// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using Blueprint.Api.Data.Models;
using System.Collections.Generic;

namespace Blueprint.Api.Infrastructure.Options
{
    public class SeedDataOptions
    {
        public List<SystemRoleEntity> Roles { get; set; }
        public List<GroupEntity> Groups { get; set; }
        public List<UserEntity> Users { get; set; }
        public List<TeamEntity> Teams { get; set; }
        public List<TeamUserEntity> TeamUsers { get; set; }
        public List<MselEntity> Msels { get; set; }
        public List<MoveEntity> Moves { get; set; }
        public List<OrganizationEntity> Organizations { get; set; }
        public List<ScenarioEventEntity> ScenarioEvents { get; set; }
        public List<DataFieldEntity> DataFields { get; set; }
        public List<DataOptionEntity> DataOptions { get; set; }
        public List<UserMselRoleEntity> UserMselRoles { get; set; }
        public List<InjectTypeEntity> InjectTypes { get; set; }
        public List<CardEntity> Cards { get; set; }
        public List<CardTeamEntity> CardTeams { get; set; }
        public List<CiteDutyEntity> CiteDuties { get; set; }
        public List<CiteActionEntity> CiteActions { get; set; }
        public List<PlayerApplicationEntity> PlayerApplications { get; set; }
        public List<PlayerApplicationTeamEntity> PlayerApplicationTeams { get; set; }
    }
}

