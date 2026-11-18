// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>Teams' mappings.</summary>
[Mapper]
[UseStaticMapper(typeof(UserMapper))]
[UseStaticMapper(typeof(CardTeamMapper))]
[UseStaticMapper(typeof(PlayerApplicationTeamMapper))]
[UseStaticMapper(typeof(InvitationMapper))]
[UseStaticMapper(typeof(UserTeamRoleMapper))]
public static partial class TeamMapper
{
    /// <remarks>
    /// Every list holds whatever the context has loaded, and is empty otherwise; <see cref="Team.Users"/>
    /// holds the user of each loaded membership.
    /// </remarks>
    [MapProperty(nameof(TeamEntity.TeamUsers), nameof(Team.Users), Use = nameof(MapUsers))]
    public static partial Team ToTeam(this TeamEntity source);

    /// <remarks>A create without an MSEL id leaves the entity's empty (MapperDefaults).</remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial TeamEntity ToEntity(this TeamCreate source);

    /// <remarks>The edit request has no MSEL or id, so it cannot move a team.</remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial void ApplyTo(this TeamEdit source, TeamEntity target);

    // A membership whose user is not loaded is a null user, as AutoMapper left it.
    private static User[] MapUsers(ICollection<TeamUserEntity> teamUsers) =>
        teamUsers == null ? [] : teamUsers.Select(x => x.User == null ? null : x.User.ToUser()).ToArray();

    private static CardTeam[] MapCardTeams(ICollection<CardTeamEntity> source) =>
        source == null ? [] : source.Select(x => x.ToCardTeam()).ToArray();

    private static PlayerApplicationTeam[] MapPlayerApplicationTeams(ICollection<PlayerApplicationTeamEntity> source) =>
        source == null ? [] : source.Select(x => x.ToPlayerApplicationTeam()).ToArray();

    private static Invitation[] MapInvitations(ICollection<InvitationEntity> source) =>
        source == null ? [] : source.Select(x => x.ToInvitation()).ToArray();

    private static ICollection<UserTeamRole> MapUserTeamRoles(ICollection<UserTeamRoleEntity> source) =>
        source == null ? [] : source.Select(x => x.ToUserTeamRole()).ToList();
}
