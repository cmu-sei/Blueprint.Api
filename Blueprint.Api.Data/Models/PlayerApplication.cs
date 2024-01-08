// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    public class PlayerApplicationEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public Guid MselId { get; set; }
        public virtual MselEntity Msel { get; set; }
        public string Name { get; set; }
        public string Url { get; set; }
        public string Icon { get; set; }
        public bool? Embeddable { get; set; }
        public bool? LoadInBackground { get; set; }
        public virtual ICollection<PlayerApplicationTeamEntity> PlayerApplicationTeams { get; set; } = new HashSet<PlayerApplicationTeamEntity>();
    }

    public class PlayerApplicationEntityConfiguration : IEntityTypeConfiguration<PlayerApplicationEntity>
    {
        public void Configure(EntityTypeBuilder<PlayerApplicationEntity> builder)
        {
            builder
                .HasOne(d => d.Msel)
                .WithMany(d => d.PlayerApplications)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }

}

