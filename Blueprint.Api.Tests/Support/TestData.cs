// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;

namespace Blueprint.Api.Tests.Support;

/// <summary>Object mothers, and the ids of the rows the migrations seed.</summary>
/// <remarks>
/// Every mother sets an explicit id, so a test can compose references before saving, and a
/// <c>CreatedBy</c> of a fresh guid rather than <see cref="Guid.Empty"/>: <c>MselViewRequirement</c>,
/// <c>MselOwnerRequirement</c> and <c>CatalogViewRequirement</c> short-circuit on <c>CreatedBy == userId</c>,
/// so a mother never makes the caller a creator unless the test passes the caller's id.
/// </remarks>
public static class TestData
{
    /// <summary>Ids of the system roles the migrations seed (<c>SystemRoleConfiguration.HasData</c>).</summary>
    public static class Roles
    {
        public static readonly Guid Administrator = SystemRoleDefaults.AdministratorRoleId;

        public static readonly Guid ContentDeveloper = SystemRoleDefaults.ContentDeveloperRoleId;

        public static readonly Guid Observer = SystemRoleDefaults.ObserverRoleId;
    }

    /// <summary>
    /// An MSEL created by somebody else, so an actor's role on it is what decides what they may do.
    /// </summary>
    /// <remarks>
    /// <c>CreatedBy</c> defaults to a fresh guid rather than to <see cref="Guid.Empty"/> on purpose:
    /// <c>MselViewRequirement</c> and <c>MselOwnerRequirement</c> both short-circuit on
    /// <c>msel.CreatedBy == userId</c>, and an unset <c>CreatedBy</c> would grant both to any caller
    /// whose id also happened to be unset.
    /// </remarks>
    public static MselEntity Msel(
        Guid? createdBy = null,
        bool isTemplate = false,
        MselItemStatus status = MselItemStatus.Pending)
    {
        var id = Guid.NewGuid();

        return new MselEntity
        {
            Id = id,
            Name = $"msel-{id}",
            Description = "Seeded by TestData.Msel",
            Status = status,
            IsTemplate = isTemplate,
            CreatedBy = createdBy ?? Guid.NewGuid()
        };
    }

    /// <summary>
    /// An organization. With no <paramref name="mselId"/> it is a template, which is the only kind
    /// <c>GET organizations/templates</c> returns.
    /// </summary>
    public static OrganizationEntity Organization(
        Guid? mselId = null,
        Guid? createdBy = null,
        string name = null)
    {
        var id = Guid.NewGuid();

        return new OrganizationEntity
        {
            Id = id,
            Name = name ?? $"org-{id}",
            ShortName = "org",
            Description = "Seeded by TestData.Organization",
            Summary = "Seeded",
            Email = $"{id}@organization.test",
            IsTemplate = mselId is null,
            MselId = mselId,
            CreatedBy = createdBy ?? Guid.NewGuid()
        };
    }

    /// <summary>
    /// A team on <paramref name="mselId"/>. <c>TeamEntity.MselId</c> is a required foreign key, so a team
    /// always belongs to one.
    /// </summary>
    public static TeamEntity Team(Guid mselId, Guid? createdBy = null)
    {
        var id = Guid.NewGuid();

        return new TeamEntity
        {
            Id = id,
            Name = $"team-{id}",
            ShortName = "team",
            MselId = mselId,
            CreatedBy = createdBy ?? Guid.NewGuid()
        };
    }

    /// <summary>
    /// The join row that puts a user on a team - the membership every <c>MselViewRequirement</c> and
    /// <c>MselUserRequirement</c> check looks for once the unit-and-role path has failed.
    /// </summary>
    /// <remarks>
    /// <c>TeamUserEntity</c> is not a <c>BaseEntity</c>: it has no audit columns.
    /// <c>(UserId, TeamId)</c> is uniquely indexed, so a second row for one pair is a 500. Nothing requires
    /// the user to be in a unit the team's MSEL is assigned to.
    /// </remarks>
    public static TeamUserEntity TeamUser(Guid userId, Guid teamId) =>
        new(userId, teamId) { Id = Guid.NewGuid() };

    /// <summary>
    /// A role held by one user on one team - what CITE's team memberships are built from when a MSEL is
    /// pushed.
    /// </summary>
    /// <remarks>
    /// <paramref name="role"/> is a <b>free-text string</b>, not the <c>MselRole</c> enum: nothing validates
    /// it, and its only consumer is <c>IntegrationCiteExtensions.cs:133</c>, which matches it by name against
    /// CITE's own role names and silently creates a membership with no role when the name is unknown.
    /// <c>MselService.cs:404</c> writes the literal <c>"Submitter"</c>, which is why that is the default here.
    /// <c>(TeamId, UserId, Role)</c> is uniquely indexed, so one user may hold several roles on one team - and
    /// CITE takes whichever the database returns first.
    /// </remarks>
    public static UserTeamRoleEntity UserTeamRole(
        Guid userId, Guid teamId, string role = "Submitter", Guid? createdBy = null) =>
        new(userId, teamId, role) { Id = Guid.NewGuid(), CreatedBy = createdBy ?? Guid.NewGuid() };

    /// <summary>
    /// A unit: the reusable group of people that gets assigned to a MSEL, as against a team, which belongs
    /// to one MSEL and cannot be reused.
    /// </summary>
    /// <remarks>
    /// <c>UnitConfiguration</c> declares nothing but a unique index on the primary key, so neither
    /// <c>Name</c> nor <c>ShortName</c> is unique - two units may be called the same thing, and nothing in
    /// the API disambiguates them. Only <c>Description</c> carries <c>[SanitizeHtml]</c>.
    /// <para />
    /// A unit has no MSEL of its own: it reaches one through a <see cref="MselUnit"/> row, which is the join
    /// every <c>Msel*Requirement</c> walks. <c>MselUnitConfiguration</c> cascades from here, so deleting a
    /// unit drops it from every MSEL it was assigned to.
    /// </remarks>
    public static UnitEntity Unit(Guid? createdBy = null, string name = null)
    {
        var id = Guid.NewGuid();

        return new UnitEntity
        {
            Id = id,
            Name = name ?? $"unit-{id}",
            ShortName = "unit",
            Description = "Seeded by TestData.Unit",
            CreatedBy = createdBy ?? Guid.NewGuid()
        };
    }

    /// <summary>
    /// The join row that puts a user in a unit - the half of every <c>Msel*Requirement</c> conjunction that
    /// has to match before the role query is ever reached.
    /// </summary>
    /// <remarks>
    /// <c>UnitUserEntity</c> is not a <c>BaseEntity</c>: it has no audit columns.
    /// <c>(UserId, UnitId)</c> is uniquely indexed, so a second row for one pair is a 500.
    /// </remarks>
    public static UnitUserEntity UnitUser(Guid userId, Guid unitId) =>
        new(userId, unitId) { Id = Guid.NewGuid() };

    /// <summary>
    /// The join row that assigns a unit to a MSEL.
    /// </summary>
    /// <remarks>
    /// Note the argument order: <c>MselUnitEntity(Guid unitId, Guid mselId)</c> takes the unit first while
    /// <c>ViewModels.MselUnit(Guid mselId, Guid unitId)</c> takes the MSEL first - two same-typed ids in
    /// opposite orders, which no compiler can check. This helper follows the entity's order.
    /// <para />
    /// <c>(UnitId, MselId)</c> is uniquely indexed and the row cascades from <b>both</b> sides, so deleting
    /// either the unit or the MSEL takes the assignment with it. Unlike its two siblings this pair is
    /// consistent: neither the entity nor the view model carries audit fields.
    /// </remarks>
    public static MselUnitEntity MselUnit(Guid unitId, Guid mselId) =>
        new(unitId, mselId) { Id = Guid.NewGuid() };

    /// <summary>
    /// One Gallery card: the topic heading that a MSEL's articles are filed under.
    /// </summary>
    /// <remarks>
    /// <c>MselId</c> is nullable and is the whole of the permission story, because <c>CardService</c> has two
    /// permission branches and picks between them on whether it is set: a card with a MSEL is the MSEL
    /// owner's or editor's, and a card without one is <c>ManageGalleryCards</c>'. <c>IsTemplate</c> is a
    /// separate flag that nothing keeps in step with it, so all four combinations are reachable - and a card
    /// with no MSEL and no template flag is reachable by no list route at all
    /// (<c>CardEndpointTests.Update_ThatOmitsTheMselId_WithManageGalleryCardsOnly_DetachesTheCard</c>).
    /// Pass <paramref name="mselId"/> null and <paramref name="isTemplate"/> true for a template.
    /// <para />
    /// Nothing is unique here: two cards may share a MSEL, a <c>Move</c> and an <c>Inject</c>.
    /// </remarks>
    public static CardEntity Card(
        Guid? mselId,
        int move = 0,
        int inject = 0,
        bool isTemplate = false,
        string description = null,
        Guid? galleryId = null,
        Guid? createdBy = null)
    {
        var id = Guid.NewGuid();

        return new CardEntity
        {
            Id = id,
            MselId = mselId,
            Name = $"card-{id}",
            Description = description ?? $"description of card {id}",
            Move = move,
            Inject = inject,
            GalleryId = galleryId,
            IsTemplate = isTemplate,
            CreatedBy = createdBy ?? Guid.NewGuid()
        };
    }

    /// <summary>
    /// The join row that puts a card in front of one team, and says what that team may do with it.
    /// </summary>
    /// <remarks>
    /// <c>CardTeamEntity</c> is <b>not</b> a <c>BaseEntity</c>, so a card team row carries no audit fields at
    /// all - there is no record of who showed a card to a team or when. <c>(TeamId, CardId)</c> is uniquely
    /// indexed, so a second row for one pair is a 500. Nothing requires the team to belong to the card's MSEL.
    /// </remarks>
    public static CardTeamEntity CardTeam(
        Guid cardId, Guid teamId, bool isShownOnWall = false, bool canPostArticles = false) =>
        new(cardId, teamId) { Id = Guid.NewGuid(), IsShownOnWall = isShownOnWall, CanPostArticles = canPostArticles };

    /// <summary>
    /// One move of a MSEL's timeline: the coarse division that scenario events are grouped into.
    /// </summary>
    /// <remarks>
    /// <c>DeltaSeconds</c> is what everything orders and groups by; <c>MoveNumber</c> is what CITE, Gallery
    /// and Steamfitter are told. The two are independent and may disagree - a move 2 starting before move 1
    /// is accepted - but they are not both unconstrained: <c>MoveEntityConfiguration</c> declares
    /// <c>(MselId, MoveNumber)</c> unique, so one MSEL cannot hold two move zeroes and a second one is a
    /// 500 (see <c>MoveEndpointTests.Create_WithAMoveNumberTheMselAlreadyUses_Is500</c>). Seed distinct
    /// numbers per MSEL.
    /// </remarks>
    public static MoveEntity Move(
        Guid mselId,
        int moveNumber = 0,
        int deltaSeconds = 0,
        DateTime? situationTime = null,
        Guid? createdBy = null)
    {
        var id = Guid.NewGuid();

        return new MoveEntity
        {
            Id = id,
            MselId = mselId,
            MoveNumber = moveNumber,
            DeltaSeconds = deltaSeconds,
            Description = $"move-{moveNumber}",
            SituationDescription = $"situation for move {moveNumber}",
            SituationTime = situationTime ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedBy = createdBy ?? Guid.NewGuid()
        };
    }

    /// <summary>
    /// One of the applications a MSEL asks Player to create in its view.
    /// </summary>
    /// <remarks>
    /// <c>Url</c> defaults to something absolute and <c>http</c>, because that is what
    /// <c>IntegrationPlayerExtensions.CreateApplicationsAsync</c> requires to send a url at all - anything
    /// else it silently sends as null. Pass a url holding <c>{playerViewId}</c> and friends to exercise the
    /// placeholder substitution.
    /// </remarks>
    public static PlayerApplicationEntity PlayerApplication(
        Guid mselId,
        string name = null,
        string url = "http://application.example/",
        string icon = null,
        Guid? createdBy = null)
    {
        var id = Guid.NewGuid();

        return new PlayerApplicationEntity
        {
            Id = id,
            MselId = mselId,
            Name = name ?? $"application-{id}",
            Url = url,
            Icon = icon,
            Embeddable = true,
            LoadInBackground = false,
            CreatedBy = createdBy ?? Guid.NewGuid()
        };
    }

    /// <summary>
    /// The join row that puts an application on one team's dashboard, and carries the order it appears in.
    /// </summary>
    public static PlayerApplicationTeamEntity PlayerApplicationTeam(
        Guid playerApplicationId, Guid teamId, int displayOrder = 0) =>
        new(playerApplicationId, teamId) { DisplayOrder = displayOrder };

    /// <summary>
    /// An inject type. Every catalog needs one - <c>CatalogEntity.InjectTypeId</c> is a non-nullable
    /// foreign key - so seed this before <see cref="Catalog"/>.
    /// </summary>
    public static InjectTypeEntity InjectType(Guid? createdBy = null)
    {
        var id = Guid.NewGuid();

        return new InjectTypeEntity
        {
            Id = id,
            Name = $"injectType-{id}",
            Description = "Seeded by TestData.InjectType",
            CreatedBy = createdBy ?? Guid.NewGuid()
        };
    }

    /// <summary>
    /// A data field on an MSEL, or - given an <paramref name="injectTypeId"/> - on an inject type. Never
    /// both: the table carries a check constraint saying one of the two columns must be null. With
    /// neither it is a template, which is the only kind <c>GET dataFields/templates</c> is meant to
    /// return.
    /// </summary>
    /// <remarks>
    /// <paramref name="isTemplate"/> is separate from the two ids rather than derived from them because
    /// nothing in the API keeps them consistent: <c>DataFieldService.CreateAsync</c> maps
    /// <c>IsTemplate</c> straight off the request body, so a field can be both scoped to an MSEL and
    /// flagged a template. Leave it unset to get the honest default.
    /// </remarks>
    public static DataFieldEntity DataField(
        Guid? mselId = null,
        Guid? injectTypeId = null,
        DataFieldType dataType = DataFieldType.String,
        int displayOrder = 1,
        string name = null,
        bool? isTemplate = null,
        Guid? createdBy = null)
    {
        var id = Guid.NewGuid();

        return new DataFieldEntity
        {
            Id = id,
            Name = name ?? $"dataField-{id}",
            Description = "Seeded by TestData.DataField",
            MselId = mselId,
            InjectTypeId = injectTypeId,
            DataType = dataType,
            DisplayOrder = displayOrder,
            IsTemplate = isTemplate ?? (mselId is null && injectTypeId is null),
            CreatedBy = createdBy ?? Guid.NewGuid()
        };
    }

    /// <summary>
    /// One choice in a data field's dropdown. <c>DataOptionEntity.DataFieldId</c> is a required foreign
    /// key, so an option always belongs to a field.
    /// </summary>
    public static DataOptionEntity DataOption(
        Guid dataFieldId,
        string optionName = null,
        string optionValue = null,
        int displayOrder = 1,
        Guid? createdBy = null)
    {
        var id = Guid.NewGuid();

        return new DataOptionEntity
        {
            Id = id,
            DataFieldId = dataFieldId,
            OptionName = optionName ?? $"option-{id}",
            OptionValue = optionValue ?? optionName ?? $"value-{id}",
            OptionDescription = "Seeded by TestData.DataOption",
            DisplayOrder = displayOrder,
            CreatedBy = createdBy ?? Guid.NewGuid()
        };
    }

    /// <summary>
    /// A scenario event - one row of an MSEL's timeline. <c>ScenarioEventEntity.MselId</c> is a
    /// non-nullable foreign key, so an event always belongs to one.
    /// </summary>
    /// <remarks>
    /// <c>EventType</c> is spelled out because <c>Player.Api.Client</c> declares one too, and this file
    /// has both namespaces in scope.
    /// </remarks>
    public static ScenarioEventEntity ScenarioEvent(
        Guid mselId,
        int deltaSeconds = 0,
        int groupOrder = 1,
        Data.Enumerations.EventType scenarioEventType = Data.Enumerations.EventType.Inject,
        Guid? createdBy = null)
    {
        var id = Guid.NewGuid();

        return new ScenarioEventEntity
        {
            Id = id,
            MselId = mselId,
            DeltaSeconds = deltaSeconds,
            GroupOrder = groupOrder,
            ScenarioEventType = scenarioEventType,
            Information = $"scenarioEvent-{id}",
            CreatedBy = createdBy ?? Guid.NewGuid()
        };
    }

    /// <summary>
    /// The Steamfitter work one scenario event triggers. One per event: the foreign key lives on this
    /// side and is uniquely constrained by the one-to-one relationship.
    /// </summary>
    /// <remarks>
    /// <c>ScenarioEventEntity.SteamfitterTaskId</c> is <em>not</em> the foreign key - the relationship is
    /// configured with <c>HasForeignKey&lt;SteamfitterTaskEntity&gt;(t => t.ScenarioEventId)</c> - so that
    /// column is a denormalized copy that nothing keeps current. Set it yourself when a test needs the
    /// branches that read it.
    /// </remarks>
    public static SteamfitterTaskEntity SteamfitterTask(
        Guid scenarioEventId,
        string name = null,
        Guid? createdBy = null,
        Dictionary<string, string> actionParameters = null)
    {
        var id = Guid.NewGuid();

        return new SteamfitterTaskEntity
        {
            Id = id,
            ScenarioEventId = scenarioEventId,
            Name = name ?? $"steamfitterTask-{id}",
            Description = "Seeded by TestData.SteamfitterTask",
            TaskType = SteamfitterIntegrationType.Notification,
            Action = SteamfitterTaskAction.http_post,
            TriggerCondition = SteamfitterTaskTrigger.Time,
            VmMask = "vm-mask",
            ApiUrl = "https://steamfitter.example/api",
            ExpectedOutput = "expected",
            ExpirationSeconds = 60,
            DelaySeconds = 5,
            IntervalSeconds = 10,
            Iterations = 2,
            UserExecutable = true,
            Repeatable = false,
            // Left null by default, because that is how a row arrives from the database unless something
            // set it - and it is the shape that makes IntegrationSteamfitterExtensions throw for the two
            // task types that write into it.
            ActionParameters = actionParameters,
            CreatedBy = createdBy ?? Guid.NewGuid()
        };
    }

    /// <summary>
    /// One cell: the value of <paramref name="dataFieldId"/> on <paramref name="scenarioEventId"/>.
    /// </summary>
    /// <remarks>
    /// The table requires exactly one of a scenario event and an inject - a check constraint, not a
    /// convention - and is uniquely indexed on the three ids together, so two values for one field on one
    /// event is a constraint violation rather than a last-one-wins.
    /// </remarks>
    public static DataValueEntity DataValue(
        Guid dataFieldId,
        Guid scenarioEventId,
        string value = null,
        Guid? createdBy = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            DataFieldId = dataFieldId,
            ScenarioEventId = scenarioEventId,
            Value = value,
            CreatedBy = createdBy ?? Guid.NewGuid()
        };

    /// <summary>
    /// An inject of <paramref name="injectTypeId"/> - the reusable half of the data model, where a
    /// scenario event is the MSEL-specific half.
    /// </summary>
    public static InjectEntity Inject(Guid injectTypeId, Guid? createdBy = null)
    {
        var id = Guid.NewGuid();

        return new InjectEntity
        {
            Id = id,
            Name = $"inject-{id}",
            Description = "Seeded by TestData.Inject",
            InjectTypeId = injectTypeId,
            CreatedBy = createdBy ?? Guid.NewGuid()
        };
    }

    /// <summary>
    /// The other kind of cell: the value of <paramref name="dataFieldId"/> on
    /// <paramref name="injectId"/>. Separate from <see cref="DataValue"/> rather than an optional
    /// parameter on it because the check constraint makes the two mutually exclusive - a row with both
    /// ids, or neither, is rejected by the database rather than by the API.
    /// </summary>
    public static DataValueEntity DataValueOnInject(
        Guid dataFieldId,
        Guid injectId,
        string value = null,
        Guid? createdBy = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            DataFieldId = dataFieldId,
            InjectId = injectId,
            Value = value,
            CreatedBy = createdBy ?? Guid.NewGuid()
        };

    /// <summary>
    /// A private catalog by default, so an actor's units are what decide whether they can see it.
    /// <c>CatalogViewRequirement</c> grants any caller a public one.
    /// </summary>
    public static CatalogEntity Catalog(
        Guid injectTypeId,
        Guid? createdBy = null,
        bool isPublic = false)
    {
        var id = Guid.NewGuid();

        return new CatalogEntity
        {
            Id = id,
            Name = $"catalog-{id}",
            Description = "Seeded by TestData.Catalog",
            InjectTypeId = injectTypeId,
            IsPublic = isPublic,
            CreatedBy = createdBy ?? Guid.NewGuid()
        };
    }

    /// <summary>
    /// The join row that puts an inject in a catalog. Every route that reads a catalog's injects reads this
    /// table, and <c>InjectService.CreateAsync</c> is the only thing in the API that writes it.
    /// </summary>
    /// <remarks>
    /// <c>CatalogInjectEntity</c> is <b>not</b> a <c>BaseEntity</c>, so nothing records who added an inject to
    /// a catalog or when. <c>(InjectId, CatalogId)</c> is uniquely indexed and both foreign keys cascade.
    /// Neither <c>IsNew</c> nor <c>DisplayOrder</c> appears on <c>ViewModels.Injectm</c>, so neither reaches
    /// the wire on any inject route - the create path leaves both at their defaults and no inject route can
    /// show or change them.
    /// </remarks>
    public static CatalogInjectEntity CatalogInject(
        Guid catalogId, Guid injectId, bool isNew = false, int displayOrder = 0) =>
        new(catalogId, injectId) { Id = Guid.NewGuid(), IsNew = isNew, DisplayOrder = displayOrder };

    /// <summary>
    /// The join row that makes a private catalog visible to a unit's members - the only thing
    /// <c>CatalogViewRequirement</c> reads beyond the catalog's own <c>IsPublic</c> and <c>CreatedBy</c>.
    /// </summary>
    /// <remarks>
    /// <c>Id</c> is settable on purpose. It is the row's own primary key and nothing in the API should care
    /// what it is - but <c>InjectService.GetAsync</c> compares it against a <em>user</em> id, so a test of
    /// that comparison has to be able to choose it. See
    /// <c>InjectEndpointTests.Get_ForACatalogUnitRowWhosePrimaryKeyEqualsTheCallersId_ReadsAnyInject</c>.
    /// </remarks>
    public static CatalogUnitEntity CatalogUnit(Guid unitId, Guid catalogId, Guid? id = null) =>
        new(unitId, catalogId) { Id = id ?? Guid.NewGuid() };

    /// <summary>
    /// A competency framework. <paramref name="idNumber"/> defaults to a fresh value rather than to null
    /// because the column is uniquely indexed, and two frameworks sharing an ID number is the one thing
    /// the create and import paths are supposed to refuse.
    /// </summary>
    public static CompetencyFrameworkEntity CompetencyFramework(
        Guid? createdBy = null,
        string idNumber = null,
        string source = null,
        string version = null)
    {
        var id = Guid.NewGuid();

        return new CompetencyFrameworkEntity
        {
            Id = id,
            Name = $"framework-{id}",
            IdNumber = idNumber ?? $"FW-{id}",
            Description = "Seeded by TestData.CompetencyFramework",
            Source = source ?? "SEEDED",
            Version = version ?? "1.0",
            CreatedBy = createdBy ?? Guid.NewGuid()
        };
    }

    /// <summary>
    /// A competency in <paramref name="frameworkId"/>. <c>IdNumber</c> is what every relationship in this
    /// area is expressed in - the service resolves related competencies by ID number, never by id - so a
    /// test that cares about relationships should name it.
    /// </summary>
    public static CompetencyEntity Competency(
        Guid frameworkId,
        string idNumber = null,
        Guid? createdBy = null,
        Guid? parentId = null)
    {
        var id = Guid.NewGuid();

        return new CompetencyEntity
        {
            Id = id,
            CompetencyFrameworkId = frameworkId,
            IdNumber = idNumber ?? $"C-{id}",
            ShortName = $"competency-{id}",
            Description = "Seeded by TestData.Competency",
            ParentId = parentId,
            Path = $"/{id}",
            CreatedBy = createdBy ?? Guid.NewGuid()
        };
    }

    /// <summary>
    /// A directed relationship from <paramref name="competencyId"/> to
    /// <paramref name="relatedCompetencyId"/>. Both ends are navigations on <c>CompetencyEntity</c> -
    /// <c>Relationships</c> for the outbound side and <c>InverseRelationships</c> for the inbound one - and
    /// <c>MselCompetencyService</c> reads both, so one row is visible from either competency.
    /// </summary>
    public static CompetencyRelationshipEntity CompetencyRelationship(
        Guid competencyId,
        Guid relatedCompetencyId,
        Guid? createdBy = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            CompetencyId = competencyId,
            RelatedCompetencyId = relatedCompetencyId,
            CreatedBy = createdBy ?? Guid.NewGuid()
        };

    /// <summary>
    /// A user who is nobody in particular: no system role, no team, no unit. Use
    /// <see cref="TestActorBuilder"/> for a user whose permissions or MSEL roles matter - this helper is for
    /// the <i>subject</i> of a user route rather than its caller.
    /// </summary>
    /// <remarks>
    /// <c>UserConfiguration</c> declares nothing but a unique index on the primary key, so <c>Name</c> is not
    /// unique and two users may be called the same thing. <c>RoleId</c> is an optional foreign key to
    /// <c>SystemRoleEntity</c> with no explicit <c>OnDelete</c>, which is why deleting a role that users
    /// reference behaves differently from deleting an unused one
    /// (<c>SystemRoleEndpointTests.Delete_ForARoleAUserHolds_Is500</c>).
    /// <para />
    /// <c>UserEntity</c> is a <c>BaseEntity</c> and <c>ViewModels.User</c> derives from <c>Base</c>, so unlike
    /// <see cref="TeamUser"/> and <see cref="UnitUser"/> the audit fields here are real columns.
    /// </remarks>
    public static UserEntity User(Guid? createdBy = null, string name = null, Guid? roleId = null)
    {
        var id = Guid.NewGuid();

        return new UserEntity
        {
            Id = id,
            Name = name ?? $"user-{id}",
            RoleId = roleId,
            CreatedBy = createdBy ?? Guid.NewGuid()
        };
    }

    /// <summary>
    /// A role held by one user on one MSEL directly - the row <c>MselUnitService</c> grants on an assignment
    /// and <c>UnitUserService</c> orphans on a removal.
    /// </summary>
    /// <remarks>
    /// <b>A row here grants nothing on its own.</b> Every <c>Msel*Requirement</c> reaches the role query only
    /// once the unit query has already found the user, so a <c>UserMselRoleEntity</c> without a matching
    /// <c>UnitUserEntity</c> and <c>MselUnitEntity</c> is a no-op, which is why
    /// <c>TestActorBuilder.OnMsel</c> writes all three. Use this helper for the row itself as a route's
    /// subject; use the builder when the caller needs the role to work.
    /// <para />
    /// <c>(MselId, UserId, Role)</c> is uniquely indexed - the <i>role</i> is part of the key - so one user may
    /// hold several roles on one MSEL, and a second row for the same three values is a 500. The row cascades
    /// from the MSEL only; deleting the <i>user</i> is what the FK refuses.
    /// </remarks>
    public static UserMselRoleEntity UserMselRole(
        Guid userId, Guid mselId, MselRole role = MselRole.Viewer, Guid? createdBy = null) =>
        new(userId, mselId, role) { Id = Guid.NewGuid(), CreatedBy = createdBy ?? Guid.NewGuid() };

    /// <summary>
    /// A system role - the row that decides a user's 28 <c>SystemPermission</c>s. The three the migrations
    /// seed (<c>SystemRoleDefaults</c>) are already in every test database; this is for a fourth.
    /// </summary>
    /// <remarks>
    /// <c>SystemRoleEntity</c> is <b>not</b> a <c>BaseEntity</c>, so nothing records who created a role or
    /// when - which is the audit trail for granting somebody every permission in the installation.
    /// <c>Name</c> is uniquely indexed, hence the generated default.
    /// <para />
    /// <paramref name="immutable"/> is stored and read by nothing: <c>Immutable</c> has no reader anywhere in
    /// production, so a <c>ManageRoles</c> holder may rename, re-permission or delete the seeded Administrator
    /// role (<c>SystemRoleEndpointTests.Update_MayRewriteTheSeededAdministratorRole</c>).
    /// <c>AllPermissions</c> has exactly one reader, <c>UserClaimsService.cs:209</c>, where it expands to every
    /// value of the enum.
    /// </remarks>
    public static SystemRoleEntity SystemRole(
        string name = null,
        bool allPermissions = false,
        bool immutable = false,
        params Data.Enumerations.SystemPermission[] permissions)
    {
        var id = Guid.NewGuid();

        return new SystemRoleEntity
        {
            Id = id,
            Name = name ?? $"role-{id}",
            Description = "Seeded by TestData.SystemRole",
            AllPermissions = allPermissions,
            Immutable = immutable,
            Permissions = [.. permissions]
        };
    }

    /// <summary>
    /// A group - a named set of users, global rather than MSEL-scoped. <c>Name</c> is uniquely indexed.
    /// </summary>
    /// <remarks>
    /// No authorization path in the API reads group membership.
    /// </remarks>
    public static GroupEntity Group(string name = null)
    {
        var id = Guid.NewGuid();

        return new GroupEntity
        {
            Id = id,
            Name = name ?? $"group-{id}",
            Description = "Seeded by TestData.Group"
        };
    }

    /// <summary>
    /// A user's membership of a group. <c>(GroupId, UserId)</c> is uniquely indexed; no audit columns.
    /// </summary>
    public static GroupMembershipEntity GroupMembership(Guid groupId, Guid userId) =>
        new(groupId, userId) { Id = Guid.NewGuid() };

    /// <summary>
    /// An invitation - the row that lets somebody join a MSEL's team by link rather than by assignment.
    /// </summary>
    /// <remarks>
    /// No audit columns on either side, and <c>MaxUsersAllowed</c>/<c>UserCount</c> are <c>int</c>, so they
    /// cross the wire as JSON strings.
    /// </remarks>
    public static InvitationEntity Invitation(
        Guid mselId,
        Guid teamId,
        string emailDomain = null,
        int maxUsersAllowed = 10) =>
        new()
        {
            Id = Guid.NewGuid(),
            MselId = mselId,
            TeamId = teamId,
            EmailDomain = emailDomain,
            ExpirationDateTime = DateTime.UtcNow.AddDays(7),
            MaxUsersAllowed = maxUsersAllowed,
            UserCount = 0,
            IsTeamLeader = false,
            WasDeactivated = false
        };

    /// <summary>
    /// A competency assigned to a MSEL - what <c>GET lmt/resource/{mselId}</c> publishes as
    /// <c>assesses</c>. <c>(MselId, CompetencyId)</c> is uniquely indexed.
    /// </summary>
    public static MselCompetencyEntity MselCompetency(Guid mselId, Guid competencyId) =>
        new(mselId, competencyId) { Id = Guid.NewGuid() };

    /// <summary>
    /// A CITE action - what a team is told to do at one point in an exercise, pushed to cite.api. With no
    /// <paramref name="mselId"/> it is a template, which is the only kind <c>GET citeActions/templates</c>
    /// returns and the only kind the upload route can produce.
    /// </summary>
    /// <remarks>
    /// Both foreign keys cascade (from the MSEL and from the team), and neither is indexed, so nothing
    /// stops two identical actions. <c>MoveNumber</c>, <c>InjectNumber</c> and <c>ActionNumber</c> are
    /// <c>int</c>, so they cross the wire as JSON strings.
    /// </remarks>
    public static CiteActionEntity CiteAction(
        Guid? mselId = null,
        Guid? teamId = null,
        int moveNumber = 0,
        int injectNumber = 0,
        int actionNumber = 0,
        string description = null)
    {
        var id = Guid.NewGuid();

        return new CiteActionEntity
        {
            Id = id,
            MselId = mselId,
            TeamId = teamId,
            MoveNumber = moveNumber,
            InjectNumber = injectNumber,
            ActionNumber = actionNumber,
            Description = description ?? $"action-{id}",
            IsTemplate = mselId is null
        };
    }

    /// <summary>
    /// A CITE duty - the named role a team holds in an exercise, pushed to cite.api. Same shape as
    /// <see cref="CiteAction"/>, which its service and controller are a line-for-line copy of.
    /// </summary>
    /// <remarks>
    /// Both foreign keys cascade and neither is indexed, as for <see cref="CiteAction"/>.
    /// </remarks>
    public static CiteDutyEntity CiteDuty(Guid? mselId = null, Guid? teamId = null, string name = null)
    {
        var id = Guid.NewGuid();

        return new CiteDutyEntity
        {
            Id = id,
            MselId = mselId,
            TeamId = teamId,
            Name = name ?? $"duty-{id}",
            IsTemplate = mselId is null
        };
    }

    /// <summary>
    /// A page of an exercise's playbook - free HTML attached to a MSEL.
    /// </summary>
    /// <remarks>
    /// <paramref name="allCanView"/> is not the widening it reads as: <c>MselPageService.GetAsync</c>
    /// answers an <c>AllCanView</c> page only to a member of one of the MSEL's teams and ignores
    /// <c>hasSystemPermission</c> in that branch, so setting it <i>narrows</i> who may read the page
    /// (<c>MselPageEndpointTests.Get_OfAnAllCanViewPage_RefusesAViewMselsHolder</c>). Seed it false for a
    /// row a <c>ViewMsels</c> holder has to reach.
    /// <para />
    /// <c>MselPageEntity</c> is not a <c>BaseEntity</c> and <c>ViewModels.MselPage</c> does not derive from
    /// <c>Base</c>, so nothing records who wrote a page or when. <c>Content</c> carries
    /// <c>[SanitizeHtml]</c>; the row cascades from the MSEL.
    /// </remarks>
    public static MselPageEntity MselPage(
        Guid mselId,
        string name = null,
        string content = null,
        bool allCanView = false,
        bool includeInPlaybook = false)
    {
        var id = Guid.NewGuid();

        return new MselPageEntity
        {
            Id = id,
            MselId = mselId,
            Name = name ?? $"page-{id}",
            Content = content ?? "<p>Seeded by TestData.MselPage</p>",
            AllCanView = allCanView,
            IncludeInPlaybook = includeInPlaybook
        };
    }

    /// <summary>
    /// A competency assigned to a team - the team-level twin of <see cref="MselCompetency"/>.
    /// <c>(TeamId, CompetencyId)</c> is uniquely indexed, so a duplicate pair is a 500.
    /// </summary>
    /// <remarks>
    /// Not a <c>BaseEntity</c>, and <c>ViewModels.TeamCompetency</c> does not derive from <c>Base</c>, so
    /// nothing records who assigned a competency to a team. The row cascades from both the team and the
    /// competency.
    /// </remarks>
    public static TeamCompetencyEntity TeamCompetency(Guid teamId, Guid competencyId) =>
        new(teamId, competencyId) { Id = Guid.NewGuid() };

    /// <summary>
    /// A proficiency scale - the named set of levels a competency assessment is scored against. Reference
    /// data, global rather than MSEL-scoped, and <c>Name</c> is uniquely indexed.
    /// </summary>
    public static ProficiencyScaleEntity ProficiencyScale(Guid? createdBy = null, string name = null)
    {
        var id = Guid.NewGuid();

        return new ProficiencyScaleEntity
        {
            Id = id,
            Name = name ?? $"scale-{id}",
            Description = "Seeded by TestData.ProficiencyScale",
            CreatedBy = createdBy ?? Guid.NewGuid()
        };
    }

    /// <summary>
    /// One level of <paramref name="proficiencyScaleId"/>. <c>Value</c> and <c>DisplayOrder</c> are
    /// <c>int</c>, so they cross the wire as JSON strings; neither is constrained or uniquely indexed.
    /// </summary>
    public static ProficiencyLevelEntity ProficiencyLevel(
        Guid proficiencyScaleId,
        string name = null,
        int value = 1,
        int displayOrder = 1,
        Guid? createdBy = null)
    {
        var id = Guid.NewGuid();

        return new ProficiencyLevelEntity
        {
            Id = id,
            ProficiencyScaleId = proficiencyScaleId,
            Name = name ?? $"level-{id}",
            Value = value,
            Description = "Seeded by TestData.ProficiencyLevel",
            DisplayOrder = displayOrder,
            CreatedBy = createdBy ?? Guid.NewGuid()
        };
    }
}
