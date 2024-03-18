// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    public class MselTeamEntity
    {
        public MselTeamEntity() { }

        public MselTeamEntity(Guid teamId, Guid mselId)
        {
            TeamId = teamId;
            MselId = mselId;
        }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }

        public Guid TeamId { get; set; }
        public TeamEntity Team { get; set; }

        public Guid MselId { get; set; }
        public MselEntity Msel { get; set; }
        public Guid? CiteTeamTypeId { get; set; }
        public string Email { get; set; }
    }

    public class MselTeamConfiguration : IEntityTypeConfiguration<MselTeamEntity>
    {
        public void Configure(EntityTypeBuilder<MselTeamEntity> builder)
        {
            builder.HasIndex(x => new { x.TeamId, x.MselId }).IsUnique();
        }
    }
}

