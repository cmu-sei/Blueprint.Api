// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>MSELs' mappings.</summary>
[Mapper]
[UseStaticMapper(typeof(MoveMapper))]
[UseStaticMapper(typeof(DataFieldMapper))]
[UseStaticMapper(typeof(ScenarioEventMapper))]
[UseStaticMapper(typeof(TeamMapper))]
[UseStaticMapper(typeof(UnitMapper))]
[UseStaticMapper(typeof(UserMselRoleMapper))]
[UseStaticMapper(typeof(OrganizationMapper))]
[UseStaticMapper(typeof(CardMapper))]
[UseStaticMapper(typeof(CiteDutyMapper))]
[UseStaticMapper(typeof(CiteActionMapper))]
[UseStaticMapper(typeof(PlayerApplicationMapper))]
[UseStaticMapper(typeof(MselPageMapper))]
[UseStaticMapper(typeof(InvitationMapper))]
[UseStaticMapper(typeof(MselCompetencyMapper))]
public static partial class MselMapper
{
    /// <remarks>
    /// Every list holds whatever the context has loaded, and is empty otherwise: the pages too, which
    /// AutoMapper's ExplicitExpansion left out of projections only, and an MSEL is never projected.
    /// <see cref="Msel.Units"/> holds the unit of each loaded MSEL unit. The three Gallery lists are left
    /// empty for MselService to fill.
    /// </remarks>
    [MapProperty(nameof(MselEntity.MselUnits), nameof(Msel.Units), Use = nameof(MapUnits))]
    [MapperIgnoreTarget(nameof(Msel.GalleryArticleParameters))]
    [MapperIgnoreTarget(nameof(Msel.GallerySourceTypes))]
    [MapperIgnoreTarget(nameof(Msel.GalleryItemStatuses))]
    public static partial Msel ToMsel(this MselEntity source);

    /// <remarks>The write maps copy exactly the request's fields: everything else on the entity is left as it is.</remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial MselEntity ToEntity(this MselCreate source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial void ApplyTo(this MselEdit source, MselEntity target);

    private static ICollection<Unit> MapUnits(ICollection<MselUnitEntity> mselUnits) =>
        mselUnits == null ? [] : mselUnits.Select(x => x.Unit == null ? null : x.Unit.ToUnit()).ToList();

    private static ICollection<Move> MapMoves(ICollection<MoveEntity> source) =>
        source == null ? [] : source.Select(x => x.ToMove()).ToList();

    private static ICollection<DataField> MapDataFields(ICollection<DataFieldEntity> source) =>
        source == null ? [] : source.Select(x => x.ToDataField()).ToList();

    private static ICollection<ScenarioEvent> MapScenarioEvents(ICollection<ScenarioEventEntity> source) =>
        source == null ? [] : source.Select(x => x.ToScenarioEvent()).ToList();

    private static ICollection<Team> MapTeams(ICollection<TeamEntity> source) =>
        source == null ? [] : source.Select(x => x.ToTeam()).ToList();

    private static ICollection<UserMselRole> MapUserMselRoles(ICollection<UserMselRoleEntity> source) =>
        source == null ? [] : source.Select(x => x.ToUserMselRole()).ToList();

    private static ICollection<Organization> MapOrganizations(ICollection<OrganizationEntity> source) =>
        source == null ? [] : source.Select(x => x.ToOrganization()).ToList();

    private static ICollection<Card> MapCards(ICollection<CardEntity> source) =>
        source == null ? [] : source.Select(x => x.ToCard()).ToList();

    private static ICollection<CiteDuty> MapCiteDuties(ICollection<CiteDutyEntity> source) =>
        source == null ? [] : source.Select(x => x.ToCiteDuty()).ToList();

    private static ICollection<CiteAction> MapCiteActions(ICollection<CiteActionEntity> source) =>
        source == null ? [] : source.Select(x => x.ToCiteAction()).ToList();

    private static ICollection<PlayerApplication> MapPlayerApplications(ICollection<PlayerApplicationEntity> source) =>
        source == null ? [] : source.Select(x => x.ToPlayerApplication()).ToList();

    private static ICollection<MselPage> MapPages(ICollection<MselPageEntity> source) =>
        source == null ? [] : source.Select(x => x.ToMselPage()).ToList();

    private static ICollection<Invitation> MapInvitations(ICollection<InvitationEntity> source) =>
        source == null ? [] : source.Select(x => x.ToInvitation()).ToList();

    private static ICollection<MselCompetency> MapMselCompetencies(ICollection<MselCompetencyEntity> source) =>
        source == null ? [] : source.Select(x => x.ToMselCompetency()).ToList();
}
