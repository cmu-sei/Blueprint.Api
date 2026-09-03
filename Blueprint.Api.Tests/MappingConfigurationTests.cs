// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Reflection;
using AutoMapper;
using AutoMapper.Internal;
using AutoMapper.QueryableExtensions;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Tests.Infrastructure;
using Blueprint.Api.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// The AutoMapper configuration as a whole: all 38 profiles, the global null-source rule
/// <c>Startup</c> installs over them, and the handful of maps whose behaviour the services depend on.
/// </summary>
/// <remarks>
/// <para>
/// Every test here runs against <em>both</em> mappers - the one on the hosted application and
/// <see cref="TestMapper"/>'s copy - because the copy is what unit tests elsewhere in this suite use, and
/// it reproduces <c>Startup</c>'s configuration by hand. <c>Blueprint.Api</c>'s own
/// <c>IgnoreNullSourceValues</c> resolver is internal, so the copy cannot reference it and substitutes a
/// two-line reimplementation; these tests are what stop the two drifting apart silently. The
/// <c>hosted</c> parameter says which is under test.
/// </para>
/// <para>
/// This class takes the factory without <see cref="ApiTestBase"/>: an <c>IMapper</c> is immutable
/// configuration, so nothing here needs a database, a request, or an actor.
/// </para>
/// </remarks>
public class MappingConfigurationTests(BlueprintAppFactory factory) : IClassFixture<BlueprintAppFactory>
{
    /// <summary>
    /// Every map with a destination member AutoMapper cannot fill, as it stands today.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the list <see cref="Configuration_IsNotValid"/> is about, written out so that a new
    /// profile joining it fails here rather than nowhere. Most entries are navigation properties on the
    /// entity side of a write map - <c>Msel</c>, <c>Team</c>, <c>Catalog</c> - which the service sets
    /// itself from the route, and mapping them from a client-supplied view model would be worse than
    /// leaving them alone. Four entries are not that, and are findings:
    /// </para>
    /// <list type="bullet">
    /// <item><c>MselEntity -> Msel: GalleryArticleParameters, GallerySourceTypes</c> - view-model-only
    /// lists that <c>MselService</c> fills from <c>Enum.GetNames</c> after mapping.</item>
    /// <item><c>TeamUserEntity -> TeamUser</c> and <c>UnitUserEntity -> UnitUser</c>: <c>DateCreated,
    /// DateModified, CreatedBy, ModifiedBy</c> - both view models derive from <c>ViewModels.Base</c>
    /// while neither entity derives from <c>BaseEntity</c>, so those four fields go out as defaults.
    /// See <see cref="Map_TeamUserEntityToTeamUser_LeavesTheAuditFieldsUnset"/>.</item>
    /// <item><c>UserEntity -> User: Permissions</c> - a legacy array with nothing behind it. See
    /// <see cref="Map_UserEntityToUser_LeavesPermissionsNull"/>.</item>
    /// </list>
    /// </remarks>
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

    /// <summary>
    /// Characterization, and the reason the rest of this class exists.
    /// <c>AssertConfigurationIsValid</c> is the one test AutoMapper ships, and blueprint does not pass
    /// it: 31 of its 78 maps have a destination member with no source.
    /// </summary>
    /// <remarks>
    /// Nothing calls it in production - AutoMapper 13 validates lazily, per map, on first use, and only
    /// for the members it can reach - so this has never been visible. Asserted as a throw rather than
    /// skipped, so that the day the profiles are completed this test says so.
    /// <see cref="MapsWithUnmappedMembers"/> is what holds the line meanwhile.
    /// Turns red when every profile accounts for its destination members.
    /// </remarks>
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

    /// <summary>
    /// <c>Startup</c> installs a rule over every property map whose source is <c>T?</c> and whose
    /// destination is <c>T</c>: a null source leaves the destination alone. Across all 78 maps, exactly
    /// one property is that shape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Worth pinning precisely, because the rule reads as though it made every nullable field on every
    /// <c>PUT</c> a partial update, and it does not - <c>Organization.MselId</c>, <c>Team.CiteTeamTypeId</c>,
    /// <c>Msel.PlayerViewId</c> and the rest are <c>T?</c> on both sides, where the rule does not fire and
    /// a null overwrites. See <see cref="Map_AnOrganizationWithNoMselId_DetachesItFromItsMsel"/>.
    /// </para>
    /// <para>
    /// Measured from the CLR property types rather than from the property map, so this says which
    /// properties the rule's predicate selects and not that the rule is installed - by the time a
    /// configuration is sealed, a map the rule has claimed reports its source type as <c>object</c>.
    /// <see cref="Map_ATeamWithNoMselId_LeavesTheEntitysMselIdAlone"/> is what fails if the rule goes.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Characterization, and the mapper half of <c>OrganizationEndpointTests</c>'s
    /// <c>Update_ChoosesItsPermissionBranchFromTheRequestBody</c>. <c>MselId</c> is <c>Guid?</c> on both
    /// the view model and the entity, so the global rule does not fire and a <c>PUT</c> whose body omits
    /// it detaches the organization from its MSEL - turning an MSEL's organization back into a template.
    /// </summary>
    /// <remarks>
    /// Turns red when the rule is widened to <c>T?</c> → <c>T?</c>, or when <c>OrganizationService</c>
    /// stops taking <c>MselId</c> from the body.
    /// </remarks>
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
        var entity = BlueprintAppFactory.Msel();
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

    /// <summary>
    /// Characterization, and the trap in the three <c>ExplicitExpansion</c> declarations
    /// (<c>Msel.Pages</c>, <c>Team.Users</c>, <c>Unit.Users</c>): the option only governs
    /// <c>ProjectTo</c>. An in-memory <c>Map</c> fills the member whether the caller wanted it or not, so
    /// a service that loads an MSEL with <c>Include(x =&gt; x.Pages)</c> and maps it returns them.
    /// </summary>
    /// <remarks>
    /// Turns red if AutoMapper ever extends the option to <c>Map</c>. Until then, "explicit" describes
    /// one of the two ways blueprint produces a view model.
    /// </remarks>
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
        var msel = Mapper(hosted).Map<Msel>(BlueprintAppFactory.Msel());

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
        var source = BlueprintAppFactory.Msel();
        var destination = new MselEntity { Id = Guid.NewGuid() };

        Mapper(hosted).Map(source, destination);

        Assert.NotEqual(source.Id, destination.Id);
        Assert.Equal(source.Name, destination.Name);
    }

    /// <summary>
    /// Characterization. <c>TeamUser</c> derives from <c>ViewModels.Base</c> and <c>TeamUserEntity</c>
    /// does not derive from <c>BaseEntity</c>, so the four audit fields on every team membership the API
    /// returns are defaults - a <c>dateCreated</c> of <c>0001-01-01</c> and an all-zero <c>createdBy</c>.
    /// <c>UnitUser</c> is the same shape.
    /// </summary>
    /// <remarks>
    /// Turns red when either entity gains <c>BaseEntity</c> - which needs a migration, since neither
    /// table has the columns.
    /// </remarks>
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

    /// <summary>
    /// Characterization. <c>User.Permissions</c> is a legacy array with nothing behind it -
    /// <c>UserEntity</c> has no permissions of its own, they come from its <c>SystemRoleEntity</c> - so
    /// every user the API returns carries a null. The administration UI reads permissions from
    /// <c>GET api/roles</c> instead.
    /// </summary>
    /// <remarks>Turns red when the property is removed, which is the fix.</remarks>
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
        hosted ? factory.Services.GetRequiredService<IMapper>() : TestMapper.Value;

    /// <summary>An MSEL carrying one page, for the three tests about expansion.</summary>
    private static MselEntity WithAPage()
    {
        var entity = BlueprintAppFactory.Msel();
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
