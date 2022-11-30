// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Blueprint.Api.Data.Models
{
    public class TeamEntity : BaseEntity
    {
        [Key]
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string ShortName { get; set; }
        public Guid? PlayerTeamId { get; set; }
        public bool IsParticipantTeam { get; set; }
        public ICollection<TeamUserEntity> TeamUsers { get; set; } = new List<TeamUserEntity>();
        public virtual ICollection<MselTeamEntity> MselTeams { get; set; } = new HashSet<MselTeamEntity>();
        public virtual ICollection<CardTeamEntity> CardTeams { get; set; } = new HashSet<CardTeamEntity>();
    }

    public class TeamConfiguration : IEntityTypeConfiguration<TeamEntity>
    {
        public void Configure(EntityTypeBuilder<TeamEntity> builder)
        {
            builder.HasIndex(e => e.Id).IsUnique();
        }
    }
}
