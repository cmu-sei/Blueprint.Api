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
                        IConfiguration configuration = services.GetService<IConfiguration>();
                        string seedFile = Path.Combine(
                            env.ContentRootPath,
                            databaseOptions.SeedFile
                        );

                        SeedDataOptions seedDataOptions = null;

                        // Try to load from seed file first
                        if (File.Exists(seedFile))
                        {
                            seedDataOptions = JsonSerializer.Deserialize<SeedDataOptions>(File.ReadAllText(seedFile));
                        }
                        // Fall back to SeedData section in appsettings.json
                        else
                        {
                            seedDataOptions = new SeedDataOptions();
                            configuration.GetSection("SeedData").Bind(seedDataOptions);
                        }

                        if (seedDataOptions != null)
                        {
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
            if (options.Roles?.Any() == true)
            {
                var dbRoles = context.SystemRoles.ToHashSet();

                foreach (SystemRoleEntity role in options.Roles)
                {
                    if (!dbRoles.Any(x => x.Name == role.Name))
                    {
                        context.SystemRoles.Add(role);
                    }
                }
                context.SaveChanges();
            }
            // USERS
            if (options.Users?.Any() == true)
            {
                var dbUserIds = context.Users.Select(x => x.Id).ToHashSet();

                foreach (UserEntity user in options.Users)
                {
                    if (!dbUserIds.Contains(user.Id))
                    {
                        if (user.Role?.Id == Guid.Empty && !string.IsNullOrEmpty(user.Role.Name))
                        {
                            var role = context.SystemRoles.FirstOrDefault(x => x.Name == user.Role.Name);
                            if (role != null)
                            {
                                user.RoleId = role.Id;
                                user.Role = role;
                            }
                            else
                            {
                                user.RoleId = null;
                                user.Role = null;
                            }
                        }

                        context.Users.Add(user);
                    }
                }

                context.SaveChanges();
            }
            // GROUPS
            if (options.Groups?.Any() == true)
            {
                var dbGroup = context.Groups.ToHashSet();

                foreach (var group in options.Groups)
                {
                    if (!dbGroup.Any(x => x.Name == group.Name))
                    {
                        context.Groups.Add(group);
                    }
                }

                context.SaveChanges();
            }
            // MSELS
            // before TEAMS, because every team belongs to a MSEL
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
            // INJECTTYPES
            // before DATAFIELDS, because a data field can belong to an inject type
            if (options.InjectTypes != null && options.InjectTypes.Any())
            {
                var dbInjectTypes = context.InjectTypes.ToList();

                foreach (InjectTypeEntity injectType in options.InjectTypes)
                {
                    if (!dbInjectTypes.Where(x => x.Id == injectType.Id || x.Name == injectType.Name).Any())
                    {
                        context.InjectTypes.Add(injectType);
                    }
                }
                context.SaveChanges();
            }
            // DATAFIELDS
            if (options.DataFields != null && options.DataFields.Any())
            {
                var dbDataFields = context.DataFields.ToList();
                var dbInjectTypeIds = context.InjectTypes.Select(x => x.Id).ToHashSet();

                foreach (DataFieldEntity dataField in options.DataFields)
                {
                    // a seeded inject type skipped because its name was taken leaves its fields without a parent
                    if (dataField.InjectTypeId != null && !dbInjectTypeIds.Contains((Guid)dataField.InjectTypeId))
                    {
                        continue;
                    }

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
            // CARDS
            if (options.Cards != null && options.Cards.Any())
            {
                var dbCards = context.Cards.ToList();

                foreach (CardEntity card in options.Cards)
                {
                    if (!dbCards.Where(x => x.Id == card.Id).Any())
                    {
                        context.Cards.Add(card);
                    }
                }
                context.SaveChanges();
            }
            // CARDTEAMS
            if (options.CardTeams != null && options.CardTeams.Any())
            {
                var dbCardTeams = context.CardTeams.ToList();

                foreach (CardTeamEntity cardTeam in options.CardTeams)
                {
                    if (!dbCardTeams.Where(x => x.Id == cardTeam.Id || (x.CardId == cardTeam.CardId && x.TeamId == cardTeam.TeamId)).Any())
                    {
                        context.CardTeams.Add(cardTeam);
                    }
                }
                context.SaveChanges();
            }
            // CITEDUTIES
            if (options.CiteDuties != null && options.CiteDuties.Any())
            {
                var dbCiteDuties = context.CiteDuties.ToList();

                foreach (CiteDutyEntity citeDuty in options.CiteDuties)
                {
                    if (!dbCiteDuties.Where(x => x.Id == citeDuty.Id).Any())
                    {
                        context.CiteDuties.Add(citeDuty);
                    }
                }
                context.SaveChanges();
            }
            // CITEACTIONS
            if (options.CiteActions != null && options.CiteActions.Any())
            {
                var dbCiteActions = context.CiteActions.ToList();

                foreach (CiteActionEntity citeAction in options.CiteActions)
                {
                    if (!dbCiteActions.Where(x => x.Id == citeAction.Id).Any())
                    {
                        context.CiteActions.Add(citeAction);
                    }
                }
                context.SaveChanges();
            }
            // PLAYERAPPLICATIONS
            if (options.PlayerApplications != null && options.PlayerApplications.Any())
            {
                var dbPlayerApplications = context.PlayerApplications.ToList();

                foreach (PlayerApplicationEntity playerApplication in options.PlayerApplications)
                {
                    if (!dbPlayerApplications.Where(x => x.Id == playerApplication.Id).Any())
                    {
                        context.PlayerApplications.Add(playerApplication);
                    }
                }
                context.SaveChanges();
            }
            // PLAYERAPPLICATIONTEAMS
            if (options.PlayerApplicationTeams != null && options.PlayerApplicationTeams.Any())
            {
                var dbPlayerApplicationTeams = context.PlayerApplicationTeams.ToList();

                foreach (PlayerApplicationTeamEntity playerApplicationTeam in options.PlayerApplicationTeams)
                {
                    if (!dbPlayerApplicationTeams.Where(x => x.Id == playerApplicationTeam.Id || (x.PlayerApplicationId == playerApplicationTeam.PlayerApplicationId && x.TeamId == playerApplicationTeam.TeamId)).Any())
                    {
                        context.PlayerApplicationTeams.Add(playerApplicationTeam);
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
