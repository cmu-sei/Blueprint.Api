// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure.Internal;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Data.Extensions;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;

namespace Blueprint.Api.Data
{
    public class BlueprintContext : DbContext
    {
        private DbContextOptions<BlueprintContext> _options;

        public BlueprintContext(DbContextOptions<BlueprintContext> options) : base(options) {
            _options = options;
        }

        public DbSet<UserEntity> Users { get; set; }
        public DbSet<PermissionEntity> Permissions { get; set; }
        public DbSet<UserPermissionEntity> UserPermissions { get; set; }
        public DbSet<TeamEntity> Teams { get; set; }
        public DbSet<TeamUserEntity> TeamUsers { get; set; }
        public DbSet<MselEntity> Msels { get; set; }
        public DbSet<UserMselRoleEntity> UserMselRoles { get; set; }
        public DbSet<MoveEntity> Moves { get; set; }
        public DbSet<ScenarioEventEntity> ScenarioEvents { get; set; }
        public DbSet<DataFieldEntity> DataFields { get; set; }
        public DbSet<DataOptionEntity> DataOptions { get; set; }
        public DbSet<DataValueEntity> DataValues { get; set; }
        public DbSet<OrganizationEntity> Organizations { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfigurations();

            // Apply PostgreSQL specific options
            if (Database.IsNpgsql())
            {
                modelBuilder.AddPostgresUUIDGeneration();
                modelBuilder.UsePostgresCasing();
            }

        }
    }
}

