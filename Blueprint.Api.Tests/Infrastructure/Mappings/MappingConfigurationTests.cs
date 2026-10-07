// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Reflection;
using AutoMapper;
using AutoMapper.Internal;
using AutoMapper.QueryableExtensions;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Blueprint.Api.Tests.Infrastructure.Mappings;

/// <summary>The AutoMapper configuration as a whole: all 38 profiles, the global null-source rule
/// <c>Startup</c> installs over them, and the handful of maps whose behaviour the services depend
/// on.</summary>
public class MappingConfigurationTests(BlueprintAppFactory factory) : IClassFixture<BlueprintAppFactory>
{
    /// <summary>Every map with a destination member AutoMapper cannot fill, as it stands today.</summary>
    private static readonly string[] MapsWithUnmappedMembers =
    [
        "Card -> CardEntity: CardTeams",
        "CardTeam -> CardTeamEntity: Team, Card",
        "Catalog -> CatalogEntity: InjectType, CatalogUnits, CatalogInjects",
        "CatalogEntity -> Catalog: Units",
        "CatalogInject -> CatalogInjectEntity: Catalog",
        "CatalogUnit -> CatalogUnitEntity: Catalog",
        "Competency -> CompetencyEntity: CompetencyFramework, Parent",
        "CompetencyFramework -> CompetencyFrameworkEntity: DefaultProficiencyScale",
        "DataField -> DataFieldEntity: InjectType",
        "DataValue -> DataValueEntity: ScenarioEvent, Inject",
        "GroupMembership -> GroupMembershipEntity: Group, User",
        "Injectm -> InjectEntity: InjectType, RequiresInject, CatalogInjects",
        "Invitation -> InvitationEntity: Msel, Team",
        "Msel -> MselEntity: CiteScoringModelName",
        "MselCompetency -> MselCompetencyEntity: Msel",
        "MselEntity -> Msel: GalleryArticleParameters, GallerySourceTypes",
        "MselPage -> MselPageEntity: Msel",
        "MselUnit -> MselUnitEntity: Msel",
        "PlayerApplication -> PlayerApplicationEntity: Msel, PlayerApplicationTeams",
        "PlayerApplicationTeam -> PlayerApplicationTeamEntity: Team, PlayerApplication",
        "ProficiencyLevel -> ProficiencyLevelEntity: ProficiencyScale",
        "ScenarioEvent -> ScenarioEventEntity: Msel, Inject",
        "Team -> TeamEntity: Msel, CiteTeamTypeName, CiteActions, CiteDuties, TeamCompetencies",
        "TeamCompetency -> TeamCompetencyEntity: Team",
        "TeamUser -> TeamUserEntity: User, Team",
        "TeamUserEntity -> TeamUser: DateCreated, DateModified, CreatedBy, ModifiedBy",
        "Unit -> UnitEntity: CatalogUnits",
        "UnitUserEntity -> UnitUser: DateCreated, DateModified, CreatedBy, ModifiedBy",
        "UserEntity -> User: Permissions",
        "UserMselRole -> UserMselRoleEntity: Msel, User",
        "UserTeamRole -> UserTeamRoleEntity: Team, User"
    ];

    /// <summary><c>AssertConfigurationIsValid</c> throws: 31 of the 78 maps have a destination member with no
    /// source.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Configuration_IsNotValid(bool hosted) =>
        Assert.Throws<AutoMapperConfigurationException>(
            Mapper(hosted).ConfigurationProvider.AssertConfigurationIsValid);

    /// <summary>
    /// The approved list, so a profile added with an unmapped member fails here - which is what
    /// <c>AssertConfigurationIsValid</c> would have done for the whole configuration had it ever passed.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Configuration_TheMapsWithUnmappedMembers_AreTheKnownList(bool hosted) =>
        Assert.Equal(
            MapsWithUnmappedMembers.AsEnumerable(),
            TypeMaps(hosted)
                .Select(x => (Map: x, Unmapped: x.GetUnmappedPropertyNames()))
                .Where(x => x.Unmapped.Length > 0)
                .OrderBy(x => x.Map.SourceType.Name)
                .ThenBy(x => x.Map.DestinationType.Name)
                .Select(x =>
                    $"{x.Map.SourceType.Name} -> {x.Map.DestinationType.Name}: " +
                    string.Join(", ", x.Unmapped)));

    /// <summary>
    /// Every profile in <c>Blueprint.Api</c> contributes at least one map, so a profile the scan misses -
    /// one moved to another assembly, or one whose registration is dropped - fails here rather than as a
    /// missing-map exception in whichever endpoint test happened to need it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Configuration_IncludesEveryProfileInTheApiAssembly(bool hosted)
    {
        var declared = typeof(Startup).Assembly
            .GetTypes()
            .Where(x => x.IsSubclassOf(typeof(Profile)) && !x.IsAbstract)
            .Select(x => x.FullName)
            .ToArray();

        var scanned = TypeMaps(hosted).Select(x => x.Profile?.Name).ToHashSet();

        Assert.NotEmpty(declared);
        Assert.All(declared, x => Assert.Contains(x, scanned));
    }

    /// <summary>
    /// The two mappers know the same maps. This is the assertion <see cref="TestMapper"/>'s remarks
    /// promise: a profile added to the application is picked up by the copy too, because both come from
    /// the same assembly scan.
    /// </summary>
    [Fact]
    public void Configuration_TheHostedMapperAndTestMapper_KnowTheSameMaps() =>
        Assert.Equal(Signatures(hosted: true), Signatures(hosted: false));

    /// <summary>Exactly one property across all maps is the <c>T?</c> to <c>T</c> shape the null-source rule
    /// selects.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Configuration_ExactlyOneProperty_IsTheShapeTheNullSourceRuleSelects(bool hosted) =>
        Assert.Equal(
            ["Team -> TeamEntity.MselId"],
            [
                .. from map in TypeMaps(hosted)
                   from property in map.PropertyMaps
                   let source = map.SourceType.GetProperty(
                       property.DestinationName, BindingFlags.Public | BindingFlags.Instance)
                   let destination = property.DestinationMember as PropertyInfo
                   where source is not null && destination is not null
                   where Nullable.GetUnderlyingType(source.PropertyType) == destination.PropertyType
                   orderby map.SourceType.Name, property.DestinationName
                   select $"{map.SourceType.Name} -> {map.DestinationType.Name}.{property.DestinationName}"
            ]);

    /// <summary>
    /// The one property the rule protects, as behaviour. <c>TeamEntity.MselId</c> is a required foreign
    /// key and <c>Team.MselId</c> is nullable, so a <c>PUT</c> whose body omits it must not move the team
    /// off its MSEL - it could not be saved if it did.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Map_ATeamWithNoMselId_LeavesTheEntitysMselIdAlone(bool hosted)
    {
        var mselId = Guid.NewGuid();
        var entity = new TeamEntity { Id = Guid.NewGuid(), Name = "Blue", MselId = mselId };

        Mapper(hosted).Map(new Team { Id = entity.Id, Name = "Red", MselId = null }, entity);

        Assert.Equal(mselId, entity.MselId);
        Assert.Equal("Red", entity.Name);
    }

    /// <summary>
    /// And a body that does name an MSEL moves the team, so the rule is about nulls and not about the
    /// member.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Map_ATeamWithAnMselId_MovesTheEntityToIt(bool hosted)
    {
        var entity = new TeamEntity { Id = Guid.NewGuid(), MselId = Guid.NewGuid() };
        var mselId = Guid.NewGuid();

        Mapper(hosted).Map(new Team { Id = entity.Id, MselId = mselId }, entity);

        Assert.Equal(mselId, entity.MselId);
    }

    /// <summary>A body with no <c>MselId</c> detaches the organization from its MSEL.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Map_AnOrganizationWithNoMselId_DetachesItFromItsMsel(bool hosted)
    {
        var entity = new OrganizationEntity { Id = Guid.NewGuid(), MselId = Guid.NewGuid() };

        Mapper(hosted).Map(new Organization { Id = entity.Id, MselId = null }, entity);

        Assert.Null(entity.MselId);
    }

    /// <summary>
    /// An MSEL's units come from its join rows rather than from a navigation property of that name -
    /// <c>MselEntity</c> has <c>MselUnits</c> and <c>Msel</c> has <c>Units</c>, so this is the one
    /// <c>ForMember</c> the read path depends on.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Map_MselEntityToMsel_TakesUnitsFromTheJoinRows(bool hosted)
    {
        var unit = new UnitEntity { Id = Guid.NewGuid(), Name = "Alpha", ShortName = "A" };
        var entity = TestData.Msel();
        entity.MselUnits.Add(new MselUnitEntity { UnitId = unit.Id, MselId = entity.Id, Unit = unit });

        var msel = Mapper(hosted).Map<Msel>(entity);

        Assert.Equal([unit.Id], msel.Units.Select(x => x.Id));
        Assert.Equal("Alpha", msel.Units.Single().Name);
    }

    /// <summary>
    /// <c>Msel.Pages</c> is declared <c>ExplicitExpansion</c>, and a projection honours that: the pages
    /// are left out unless the caller names them. Blueprint's MSEL list would otherwise carry every
    /// page's HTML body.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Project_MselEntityToMsel_LeavesPagesOut(bool hosted)
    {
        var entity = WithAPage();

        var msel = Query(entity).ProjectTo<Msel>(Mapper(hosted).ConfigurationProvider).Single();

        Assert.Empty(msel.Pages);
    }

    /// <summary>
    /// And they arrive when the projection names them, which is what makes the line above a
    /// configuration choice rather than a broken map.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Project_MselEntityToMsel_IncludesPagesWhenNamed(bool hosted)
    {
        var entity = WithAPage();

        var msel = Query(entity)
            .ProjectTo<Msel>(Mapper(hosted).ConfigurationProvider, null, x => x.Pages)
            .Single();

        Assert.Equal("Brief", msel.Pages.Single().Name);
    }

    /// <summary>An in-memory <c>Map</c> fills <c>Pages</c>; <c>ExplicitExpansion</c> governs only
    /// <c>ProjectTo</c>.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Map_MselEntityToMsel_IncludesPagesRegardless(bool hosted)
    {
        var msel = Mapper(hosted).Map<Msel>(WithAPage());

        Assert.Equal("Brief", msel.Pages.Single().Name);
    }

    /// <summary>
    /// The two Gallery lists are view-model-only, so the mapper leaves them empty rather than null and
    /// <c>MselService</c> fills them from <c>Enum.GetNames</c>. This is why
    /// <c>MselEntity -> Msel</c> is on <see cref="MapsWithUnmappedMembers"/>.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Map_MselEntityToMsel_LeavesTheGalleryListsEmpty(bool hosted)
    {
        var msel = Mapper(hosted).Map<Msel>(TestData.Msel());

        Assert.Empty(msel.GalleryArticleParameters);
        Assert.Empty(msel.GallerySourceTypes);
    }

    /// <summary>
    /// <c>DateCreated</c> is ignored on the write map, so a client cannot even propose one. Note this is
    /// belt and braces rather than the protection itself: <c>BlueprintContext.SaveEntries</c> restores
    /// every audit field from <c>OriginalValues</c> on save regardless. The other three are mapped and
    /// then overwritten there.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Map_MselToMselEntity_IgnoresDateCreated(bool hosted)
    {
        var created = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entity = new MselEntity { Id = Guid.NewGuid(), DateCreated = created };

        Mapper(hosted).Map(new Msel { Id = entity.Id, DateCreated = new DateTime(1999, 1, 1) }, entity);

        Assert.Equal(created, entity.DateCreated);
    }

    /// <summary>
    /// The entity-to-entity map exists for the MSEL copy path and ignores <c>Id</c>, so the destination
    /// keeps the identity it was created with while taking every other value from the original. A map
    /// that copied the id would make a clone a no-op update of its source.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Map_MselEntityToMselEntity_KeepsTheDestinationsId(bool hosted)
    {
        var source = TestData.Msel();
        var destination = new MselEntity { Id = Guid.NewGuid() };

        Mapper(hosted).Map(source, destination);

        Assert.NotEqual(source.Id, destination.Id);
        Assert.Equal(source.Name, destination.Name);
    }

    /// <summary>Map team user entity to team user leaves the audit fields unset.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Map_TeamUserEntityToTeamUser_LeavesTheAuditFieldsUnset(bool hosted)
    {
        var entity = new TeamUserEntity(Guid.NewGuid(), Guid.NewGuid()) { Id = Guid.NewGuid() };

        var teamUser = Mapper(hosted).Map<TeamUser>(entity);

        Assert.Equal(entity.UserId, teamUser.UserId);
        Assert.Equal(default, teamUser.DateCreated);
        Assert.Equal(Guid.Empty, teamUser.CreatedBy);
        Assert.Null(teamUser.DateModified);
        Assert.Null(teamUser.ModifiedBy);
    }

    /// <summary>Map user entity to user leaves permissions null.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Map_UserEntityToUser_LeavesPermissionsNull(bool hosted)
    {
        var entity = new UserEntity { Id = Guid.NewGuid(), Name = "Ada" };

        var user = Mapper(hosted).Map<User>(entity);

        Assert.Equal("Ada", user.Name);
        Assert.Null(user.Permissions);
    }

    /// <summary>
    /// The application's mapper, or <see cref="TestMapper"/>'s copy of its configuration.
    /// </summary>
    private IMapper Mapper(bool hosted) =>
        hosted ? factory.Services.GetRequiredService<IMapper>() : TestMapper.Mapper;

    /// <summary>An MSEL carrying one page, for the three tests about expansion.</summary>
    private static MselEntity WithAPage()
    {
        var entity = TestData.Msel();
        entity.Pages.Add(new MselPageEntity { Id = Guid.NewGuid(), MselId = entity.Id, Name = "Brief" });

        return entity;
    }

    /// <summary>
    /// A one-element queryable, so a projection can be tested without a database. What blueprint
    /// projects over in production is an EF query, and the difference is the provider rather than the
    /// configuration under test here.
    /// </summary>
    private static IQueryable<MselEntity> Query(MselEntity entity) => new[] { entity }.AsQueryable();

    private TypeMap[] TypeMaps(bool hosted) =>
        [.. Mapper(hosted).ConfigurationProvider.Internal().GetAllTypeMaps()];

    private string[] Signatures(bool hosted) =>
        [.. TypeMaps(hosted)
            .Select(x => $"{x.SourceType.FullName} -> {x.DestinationType.FullName}")
            .Order()];
}
