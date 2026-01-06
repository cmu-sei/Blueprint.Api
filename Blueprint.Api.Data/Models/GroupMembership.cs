// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    public class GroupMembershipEntity
    {
        public GroupMembershipEntity() { }

        public GroupMembershipEntity(Guid groupId, Guid userId)
        {
            GroupId = groupId;
            UserId = userId;
        }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }

        public Guid GroupId { get; set; }
        public GroupEntity Group { get; set; }

        public Guid UserId { get; set; }
        public UserEntity User { get; set; }
    }

    public class GroupMembershipConfiguration : IEntityTypeConfiguration<GroupMembershipEntity>
    {
        public void Configure(EntityTypeBuilder<GroupMembershipEntity> builder)
        {
            builder.HasIndex(x => new { x.GroupId, x.UserId }).IsUnique();

            builder
                .HasOne(u => u.Group)
                .WithMany(p => p.Memberships)
                .HasForeignKey(x => x.GroupId);
            builder
                .HasOne(u => u.User)
                .WithMany(p => p.GroupMemberships)
                .HasForeignKey(x => x.UserId);
        }
    }
}
