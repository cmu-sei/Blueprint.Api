// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using Microsoft.EntityFrameworkCore;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using System;
using System.Data;
using System.Collections.Generic;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Data.Extensions;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blueprint.Api.Data
{
    public class BlueprintContext : DbContext
    {
        public List<Entry> Entries { get; set; } = new List<Entry>();
        // Needed for EventInterceptor
        public IServiceProvider ServiceProvider;

        public BlueprintContext(DbContextOptions<BlueprintContext> options) : base(options) { }

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
        public DbSet<SteamfitterTaskEntity> SteamfitterTasks { get; set; }
        public DbSet<OrganizationEntity> Organizations { get; set; }
        public DbSet<CardEntity> Cards { get; set; }
        public DbSet<CardTeamEntity> CardTeams { get; set; }
        public DbSet<CatalogEntity> Catalogs { get; set; }
        public DbSet<CatalogInjectEntity> CatalogInjects { get; set; }
        public DbSet<CatalogUnitEntity> CatalogUnits { get; set; }
        public DbSet<CiteActionEntity> CiteActions { get; set; }
        public DbSet<CiteRoleEntity> CiteRoles { get; set; }
        public DbSet<InjectTypeEntity> InjectTypes { get; set; }
        public DbSet<PlayerApplicationEntity> PlayerApplications { get; set; }
        public DbSet<PlayerApplicationTeamEntity> PlayerApplicationTeams { get; set; }
        public DbSet<MselPageEntity> MselPages { get; set; }
        public DbSet<InjectEntity> Injects { get; set; }
        public DbSet<InvitationEntity> Invitations { get; set; }
        public DbSet<UnitEntity> Units { get; set; }
        public DbSet<UnitUserEntity> UnitUsers { get; set; }
        public DbSet<MselTeamEntity> MselTeams { get; set; }
        public DbSet<MselUnitEntity> MselUnits { get; set; }
        public DbSet<UserTeamRoleEntity> UserTeamRoles { get; set; }
        public DbSet<SystemRoleEntity> SystemRoles { get; set; }
        public DbSet<GroupEntity> Groups { get; set; }
        public DbSet<GroupMembershipEntity> GroupMemberships { get; set; }

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

        public override async Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            SaveEntries();
            return await base.SaveChangesAsync(ct);
        }

        /// <summary>
        /// keep track of changes across multiple savechanges in a transaction, without duplicates
        /// </summary>
        private void SaveEntries()
        {
            // Handle audit fields for added entries
            var addedEntries = ChangeTracker.Entries().Where(x => x.State == EntityState.Added);
            foreach (var entry in addedEntries)
            {
                try
                {
                    ((BaseEntity)entry.Entity).DateCreated = DateTime.UtcNow;
                    ((BaseEntity)entry.Entity).DateModified = null;
                    ((BaseEntity)entry.Entity).ModifiedBy = null;
                }
                catch
                { }
            }

            // Handle audit fields for modified entries
            var modifiedEntries = ChangeTracker.Entries().Where(x => x.State == EntityState.Modified);
            foreach (var entry in modifiedEntries)
            {
                try
                {
                    ((BaseEntity)entry.Entity).DateModified = DateTime.UtcNow;
                    ((BaseEntity)entry.Entity).CreatedBy = (Guid)entry.OriginalValues["CreatedBy"];
                    ((BaseEntity)entry.Entity).DateCreated = DateTime.SpecifyKind((DateTime)entry.OriginalValues["DateCreated"], DateTimeKind.Utc);
                }
                catch
                { }
            }

            // Track changes for event notifications
            foreach (var entry in ChangeTracker.Entries())
            {
                // find value of id property
                var id = entry.Properties
                    .FirstOrDefault(x =>
                        x.Metadata.ValueGenerated == Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAdd)?.CurrentValue;

                // find matching existing entry, if any
                var e = Entries.FirstOrDefault(x => x.Properties.FirstOrDefault(y =>
                    y.Metadata.ValueGenerated == Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAdd)?.CurrentValue == id);

                if (e != null)
                {
                    // if entry already exists, mark which properties were previously modified,
                    // remove old entry and add new one, to avoid duplicates
                    var modifiedProperties = e.Properties
                        .Where(x => x.IsModified)
                        .Select(x => x.Metadata.Name)
                        .ToArray();

                    var newEntry = new Entry(entry);

                    foreach (var property in newEntry.Properties)
                    {
                        if (modifiedProperties.Contains(property.Metadata.Name))
                        {
                            property.IsModified = true;
                        }
                    }

                    Entries.Remove(e);
                    Entries.Add(newEntry);
                }
                else
                {
                    Entries.Add(new Entry(entry));
                }
            }
        }
    }
}
