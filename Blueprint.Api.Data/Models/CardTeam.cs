// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    public class CardTeamEntity
    {
        public CardTeamEntity() { }

        public CardTeamEntity(Guid cardId, Guid teamId)
        {
            TeamId = teamId;
            CardId = cardId;
        }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }

        public Guid TeamId { get; set; }
        public TeamEntity Team { get; set; }

        public Guid CardId { get; set; }
        public CardEntity Card { get; set; }
        public bool IsShownOnWall { get; set; }
        public bool CanPostArticles { get; set; }
    }

    public class CardTeamConfiguration : IEntityTypeConfiguration<CardTeamEntity>
    {
        public void Configure(EntityTypeBuilder<CardTeamEntity> builder)
        {
            builder.HasIndex(x => new { x.TeamId, x.CardId }).IsUnique();

            builder
                .HasOne(u => u.Team)
                .WithMany(p => p.CardTeams)
                .HasForeignKey(x => x.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
            builder
                .HasOne(u => u.Card)
                .WithMany(p => p.CardTeams)
                .HasForeignKey(x => x.CardId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}

