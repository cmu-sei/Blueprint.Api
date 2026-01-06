// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Blueprint.Api.Data.Enumerations;

namespace Blueprint.Api.Data.Models
{
    public class SystemRoleEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public bool AllPermissions { get; set; }
        public bool Immutable { get; set; }

        public List<SystemPermission> Permissions { get; set; }
    }

    public static class SystemRoleDefaults
    {
        public static Guid AdministratorRoleId = new Guid("f35e8fff-f996-4cba-b303-3ba515ad8d2f");
        public static Guid ContentDeveloperRoleId = new Guid("d80b73c3-95d7-4468-8650-c62bbd082507");
        public static Guid ObserverRoleId = new Guid("1da3027e-725d-4753-9455-a836ed9bdb1e");
    }

    public class SystemRoleConfiguration : IEntityTypeConfiguration<SystemRoleEntity>
    {
        public void Configure(EntityTypeBuilder<SystemRoleEntity> builder)
        {
            builder.HasIndex(x => x.Name).IsUnique();

            builder.HasData(
                new SystemRoleEntity
                {
                    Id = SystemRoleDefaults.AdministratorRoleId,
                    Name = "Administrator",
                    AllPermissions = true,
                    Immutable = true,
                    Permissions = new List<SystemPermission>(),
                    Description = "Can perform all actions"
                },
                new SystemRoleEntity
                {
                    Id = SystemRoleDefaults.ContentDeveloperRoleId,
                    Name = "Content Developer",
                    AllPermissions = false,
                    Immutable = false,
                    Permissions = new List<SystemPermission>
                    {
                        SystemPermission.CreateMsels,
                        SystemPermission.ViewMsels,
                        SystemPermission.EditMsels,
                        SystemPermission.ManageMsels
                    },
                    Description = "Can create and manage their own MSELs."
                },
                new SystemRoleEntity
                {
                    Id = SystemRoleDefaults.ObserverRoleId,
                    Name = "Observer",
                    AllPermissions = false,
                    Immutable = false,
                    Permissions = Enum.GetValues<SystemPermission>()
                        .Where(x => x.ToString().StartsWith("View"))
                        .ToList(),
                    Description = "Can view all MSELs, but cannot make any changes."
                }
            );
        }
    }
}
