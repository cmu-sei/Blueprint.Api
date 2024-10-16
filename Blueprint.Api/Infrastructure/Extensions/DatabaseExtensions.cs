// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;

namespace Blueprint.Api.Infrastructure.Extensions
{
    public static class DatabaseExtensions
    {
        public static IHost InitializeDatabase(this IHost webHost)
        {
            using (var scope = webHost.Services.CreateScope())
            {
                var services = scope.ServiceProvider;

                try
                {
                    var databaseOptions = services.GetService<DatabaseOptions>();
                    var ctx = services.GetRequiredService<BlueprintContext>();

                    if (ctx != null)
                    {
                        if (databaseOptions.DevModeRecreate)
                            ctx.Database.EnsureDeleted();

                        // Do not run migrations on Sqlite, only devModeRecreate allowed
                        if (!ctx.Database.IsSqlite())
                        {
                            ctx.Database.Migrate();
                        }

                        if (databaseOptions.DevModeRecreate)
                        {
                            ctx.Database.EnsureCreated();
                        }

                        IHostEnvironment env = services.GetService<IHostEnvironment>();
                        string seedFile = Path.Combine(
                            env.ContentRootPath,
                            databaseOptions.SeedFile
                        );
                        if (File.Exists(seedFile)) {
                            SeedDataOptions seedDataOptions = JsonSerializer.Deserialize<SeedDataOptions>(File.ReadAllText(seedFile));
                            ProcessSeedDataOptions(seedDataOptions, ctx);
                        }
                    }

                }
                catch (Exception ex)
                {
                    var message = ex.Message;
                    if (ex.InnerException != null)
                    {
                        message = message + ex.InnerException.Message;
                    }
                    var logger = services.GetRequiredService<ILogger<Program>>();
                    logger.LogError(message);
                }
            }

            return webHost;
        }

        private static void ProcessSeedDataOptions(SeedDataOptions options, BlueprintContext context)
        {
            // PERMISSIONS
            if (options.Permissions != null && options.Permissions.Any())
            {
                var dbPermissions = context.Permissions.ToList();

                foreach (PermissionEntity permission in options.Permissions)
                {
                    if (!dbPermissions.Where(x => x.Key == permission.Key && x.Value == permission.Value).Any())
                    {
                        context.Permissions.Add(permission);
                    }
                }
                context.SaveChanges();
            }
            // USERS
            if (options.Users != null && options.Users.Any())
            {
                var dbUsers = context.Users.ToList();

                foreach (UserEntity user in options.Users)
                {
                    if (!dbUsers.Where(x => x.Id == user.Id).Any())
                    {
                        context.Users.Add(user);
                    }
                }
                context.SaveChanges();
            }
            // USERPERMISSIONS
            if (options.UserPermissions != null && options.UserPermissions.Any())
            {
                var dbUserPermissions = context.UserPermissions.ToList();

                foreach (UserPermissionEntity userPermission in options.UserPermissions)
                {
                    if (!dbUserPermissions.Where(x => x.UserId == userPermission.UserId && x.PermissionId == userPermission.PermissionId).Any())
                    {
                        context.UserPermissions.Add(userPermission);
                    }
                }
                context.SaveChanges();
            }
            // TEAMS
            if (options.Teams != null && options.Teams.Any())
            {
                var dbTeams = context.Teams.ToList();

                foreach (TeamEntity team in options.Teams)
                {
                    if (!dbTeams.Where(x => x.Id == team.Id).Any())
                    {
                        context.Teams.Add(team);
                    }
                }
                context.SaveChanges();
            }
            // TEAMUSERS
            if (options.TeamUsers != null && options.TeamUsers.Any())
            {
                var dbTeamUsers = context.TeamUsers.ToList();

                foreach (TeamUserEntity teamUser in options.TeamUsers)
                {
                    if (!dbTeamUsers.Where(x => x.UserId == teamUser.UserId && x.TeamId == teamUser.TeamId).Any())
                    {
                        context.TeamUsers.Add(teamUser);
                    }
                }
                context.SaveChanges();
            }
            // MSELS
            if (options.Msels != null && options.Msels.Any())
            {
                var dbMsels = context.Msels.ToList();

                foreach (MselEntity msel in options.Msels)
                {
                    if (!dbMsels.Where(x => x.Id == msel.Id).Any())
                    {
                        context.Msels.Add(msel);
                    }
                }
                context.SaveChanges();
            }
            // MOVES
            if (options.Moves != null && options.Moves.Any())
            {
                var dbMoves = context.Moves.ToList();

                foreach (MoveEntity move in options.Moves)
                {
                    if (!dbMoves.Where(x => x.Id == move.Id || (x.MselId == move.MselId && x.MoveNumber == move.MoveNumber)).Any())
                    {
                        context.Moves.Add(move);
                    }
                }
                context.SaveChanges();
            }
            // DATAFIELDS
            if (options.DataFields != null && options.DataFields.Any())
            {
                var dbDataFields = context.DataFields.ToList();

                foreach (DataFieldEntity dataField in options.DataFields)
                {
                    if (!dbDataFields.Where(x => x.Id == dataField.Id).Any())
                    {
                        context.DataFields.Add(dataField);
                    }
                }
                context.SaveChanges();
            }
            // DATAOPTIONS
            if (options.DataOptions != null && options.DataOptions.Any())
            {
                var dbDataOptions = context.DataOptions.ToList();

                foreach (DataOptionEntity dataOption in options.DataOptions)
                {
                    if (!dbDataOptions.Where(x => x.Id == dataOption.Id).Any())
                    {
                        context.DataOptions.Add(dataOption);
                    }
                }
                context.SaveChanges();
            }
            // ORGANIZATIONS
            if (options.Organizations != null && options.Organizations.Any())
            {
                var dbOrganizations = context.Organizations.ToList();

                foreach (OrganizationEntity organization in options.Organizations)
                {
                    if (!dbOrganizations.Where(x => x.Id == organization.Id || (x.MselId == organization.MselId && x.Name == organization.Name)).Any())
                    {
                        context.Organizations.Add(organization);
                    }
                }
                context.SaveChanges();
            }
            // USERMSELROLES
            if (options.UserMselRoles != null && options.UserMselRoles.Any())
            {
                var dbUserMselRoles = context.UserMselRoles.ToList();

                foreach (UserMselRoleEntity userMselRole in options.UserMselRoles)
                {
                    if (!dbUserMselRoles.Where(x => x.Id == userMselRole.Id || (x.MselId == userMselRole.MselId && x.UserId == userMselRole.UserId && x.Role == userMselRole.Role)).Any())
                    {
                        context.UserMselRoles.Add(userMselRole);
                    }
                }
                context.SaveChanges();
            }
        }

        private static string DbProvider(IConfiguration config)
        {
            return config.GetValue<string>("Database:Provider", "Sqlite").Trim();
        }

        public static DbContextOptionsBuilder UseConfiguredDatabase(
            this DbContextOptionsBuilder builder,
            IConfiguration config
        )
        {
            string dbProvider = DbProvider(config);
            var migrationsAssembly = String.Format("{0}.Migrations.{1}", typeof(Startup).GetTypeInfo().Assembly.GetName().Name, dbProvider);
            var connectionString = config.GetConnectionString(dbProvider);

            switch (dbProvider)
            {
                case "Sqlite":
                    builder.UseSqlite(connectionString, options => options.MigrationsAssembly(migrationsAssembly));
                    break;

                case "PostgreSQL":
                    builder.UseNpgsql(connectionString, options => options.MigrationsAssembly(migrationsAssembly));
                    builder.ConfigureWarnings(w => w.Throw(RelationalEventId.MultipleCollectionIncludeWarning));
                    break;

            }
            return builder;
        }
    }
}
