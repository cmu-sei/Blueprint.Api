// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using AutoFixture;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;

namespace Blueprint.Api.Tests.Shared.Fixtures;

/// <summary>
/// AutoFixture customization that registers factories for all Blueprint entity types,
/// avoiding circular reference issues from EF navigation properties.
/// </summary>
public class BlueprintCustomization : ICustomization
{
    public void Customize(IFixture fixture)
    {
        // Prevent infinite recursion from navigation properties
        fixture.Behaviors.OfType<ThrowingRecursionBehavior>()
            .ToList()
            .ForEach(b => fixture.Behaviors.Remove(b));
        fixture.Behaviors.Add(new OmitOnRecursionBehavior());

        // MselEntity - the central aggregate
        fixture.Customize<MselEntity>(c => c
            .Without(x => x.Moves)
            .Without(x => x.DataFields)
            .Without(x => x.ScenarioEvents)
            .Without(x => x.Teams)
            .Without(x => x.MselUnits)
            .Without(x => x.Organizations)
            .Without(x => x.UserMselRoles)
            .Without(x => x.Cards)
            .Without(x => x.CiteDuties)
            .Without(x => x.CiteActions)
            .Without(x => x.PlayerApplications)
            .Without(x => x.Pages)
            .Without(x => x.Invitations)
            .With(x => x.Id, () => Guid.NewGuid())
            .With(x => x.Status, MselItemStatus.Pending)
            .With(x => x.PlayerIntegrationType, IntegrationType.Deploy)
            .With(x => x.GalleryIntegrationType, IntegrationType.Deploy)
            .With(x => x.CiteIntegrationType, IntegrationType.Deploy)
            .With(x => x.SteamfitterIntegrationType, IntegrationType.Deploy)
            .With(x => x.DateCreated, () => DateTime.UtcNow)
            .With(x => x.StartTime, () => DateTime.UtcNow));

        // ScenarioEventEntity
        fixture.Customize<ScenarioEventEntity>(c => c
            .Without(x => x.Msel)
            .Without(x => x.DataValues)
            .Without(x => x.Inject)
            .Without(x => x.SteamfitterTask)
            .With(x => x.Id, () => Guid.NewGuid())
            .With(x => x.MselId, () => Guid.NewGuid())
            .With(x => x.ScenarioEventType, EventType.Inject)
            .With(x => x.DateCreated, () => DateTime.UtcNow));

        // InjectEntity
        fixture.Customize<InjectEntity>(c => c
            .Without(x => x.InjectType)
            .Without(x => x.RequiresInject)
            .Without(x => x.DataValues)
            .Without(x => x.CatalogInjects)
            .With(x => x.Id, () => Guid.NewGuid())
            .With(x => x.InjectTypeId, () => Guid.NewGuid())
            .With(x => x.DateCreated, () => DateTime.UtcNow));

        // InjectTypeEntity
        fixture.Customize<InjectTypeEntity>(c => c
            .Without(x => x.DataFields)
            .With(x => x.Id, () => Guid.NewGuid())
            .With(x => x.DateCreated, () => DateTime.UtcNow));

        // CardEntity
        fixture.Customize<CardEntity>(c => c
            .Without(x => x.Msel)
            .Without(x => x.CardTeams)
            .With(x => x.Id, () => Guid.NewGuid())
            .With(x => x.MselId, () => Guid.NewGuid())
            .With(x => x.DateCreated, () => DateTime.UtcNow));

        // TeamEntity
        fixture.Customize<TeamEntity>(c => c
            .Without(x => x.Msel)
            .Without(x => x.TeamUsers)
            .Without(x => x.CardTeams)
            .Without(x => x.PlayerApplicationTeams)
            .Without(x => x.Invitations)
            .Without(x => x.UserTeamRoles)
            .Without(x => x.CiteActions)
            .Without(x => x.CiteDuties)
            .With(x => x.Id, () => Guid.NewGuid())
            .With(x => x.MselId, () => Guid.NewGuid())
            .With(x => x.DateCreated, () => DateTime.UtcNow));

        // UserEntity
        fixture.Customize<UserEntity>(c => c
            .Without(x => x.Role)
            .Without(x => x.TeamUsers)
            .Without(x => x.UnitUsers)
            .Without(x => x.GroupMemberships)
            .With(x => x.Id, () => Guid.NewGuid())
            .With(x => x.DateCreated, () => DateTime.UtcNow));

        // UnitEntity
        fixture.Customize<UnitEntity>(c => c
            .Without(x => x.UnitUsers)
            .Without(x => x.MselUnits)
            .Without(x => x.CatalogUnits)
            .With(x => x.Id, () => Guid.NewGuid())
            .With(x => x.DateCreated, () => DateTime.UtcNow));

        // MoveEntity
        fixture.Customize<MoveEntity>(c => c
            .Without(x => x.Msel)
            .With(x => x.Id, () => Guid.NewGuid())
            .With(x => x.MselId, () => Guid.NewGuid())
            .With(x => x.DateCreated, () => DateTime.UtcNow));

        // DataFieldEntity
        fixture.Customize<DataFieldEntity>(c => c
            .Without(x => x.Msel)
            .Without(x => x.InjectType)
            .Without(x => x.DataOptions)
            .With(x => x.Id, () => Guid.NewGuid())
            .With(x => x.DataType, DataFieldType.String)
            .With(x => x.DateCreated, () => DateTime.UtcNow));

        // DataValueEntity
        fixture.Customize<DataValueEntity>(c => c
            .Without(x => x.ScenarioEvent)
            .Without(x => x.Inject)
            .Without(x => x.DataField)
            .With(x => x.Id, () => Guid.NewGuid())
            .With(x => x.ScenarioEventId, () => Guid.NewGuid())
            .Without(x => x.InjectId)
            .With(x => x.DataFieldId, () => Guid.NewGuid())
            .With(x => x.DateCreated, () => DateTime.UtcNow));

        // DataOptionEntity
        fixture.Customize<DataOptionEntity>(c => c
            .Without(x => x.DataField)
            .With(x => x.Id, () => Guid.NewGuid())
            .With(x => x.DataFieldId, () => Guid.NewGuid())
            .With(x => x.DateCreated, () => DateTime.UtcNow));

        // MselPageEntity (does not inherit BaseEntity)
        fixture.Customize<MselPageEntity>(c => c
            .Without(x => x.Msel)
            .With(x => x.Id, () => Guid.NewGuid())
            .With(x => x.MselId, () => Guid.NewGuid()));

        // MselUnitEntity (does not inherit BaseEntity)
        fixture.Customize<MselUnitEntity>(c => c
            .Without(x => x.Unit)
            .Without(x => x.Msel)
            .With(x => x.Id, () => Guid.NewGuid())
            .With(x => x.UnitId, () => Guid.NewGuid())
            .With(x => x.MselId, () => Guid.NewGuid()));

        // TeamUserEntity (does not inherit BaseEntity)
        fixture.Customize<TeamUserEntity>(c => c
            .Without(x => x.User)
            .Without(x => x.Team)
            .With(x => x.Id, () => Guid.NewGuid())
            .With(x => x.UserId, () => Guid.NewGuid())
            .With(x => x.TeamId, () => Guid.NewGuid()));

        // PermissionEntity
        fixture.Customize<PermissionEntity>(c => c
            .Without(x => x.UserPermissions)
            .With(x => x.Id, () => Guid.NewGuid())
            .With(x => x.DateCreated, () => DateTime.UtcNow));

        // UserPermissionEntity (does not inherit BaseEntity)
        fixture.Customize<UserPermissionEntity>(c => c
            .Without(x => x.User)
            .Without(x => x.Permission)
            .With(x => x.Id, () => Guid.NewGuid())
            .With(x => x.UserId, () => Guid.NewGuid())
            .With(x => x.PermissionId, () => Guid.NewGuid()));

        // CatalogEntity
        fixture.Customize<CatalogEntity>(c => c
            .Without(x => x.InjectType)
            .Without(x => x.Parent)
            .Without(x => x.CatalogUnits)
            .Without(x => x.CatalogInjects)
            .With(x => x.Id, () => Guid.NewGuid())
            .With(x => x.InjectTypeId, () => Guid.NewGuid())
            .With(x => x.DateCreated, () => DateTime.UtcNow));

        // CatalogInjectEntity (does not inherit BaseEntity)
        fixture.Customize<CatalogInjectEntity>(c => c
            .Without(x => x.Inject)
            .Without(x => x.Catalog)
            .With(x => x.Id, () => Guid.NewGuid())
            .With(x => x.InjectId, () => Guid.NewGuid())
            .With(x => x.CatalogId, () => Guid.NewGuid()));

        // CatalogUnitEntity (does not inherit BaseEntity)
        fixture.Customize<CatalogUnitEntity>(c => c
            .Without(x => x.Unit)
            .Without(x => x.Catalog)
            .With(x => x.Id, () => Guid.NewGuid())
            .With(x => x.UnitId, () => Guid.NewGuid())
            .With(x => x.CatalogId, () => Guid.NewGuid()));

        // SystemRoleEntity (does not inherit BaseEntity)
        fixture.Customize<SystemRoleEntity>(c => c
            .With(x => x.Id, () => Guid.NewGuid())
            .With(x => x.Permissions, () => new List<SystemPermission>()));

        // GroupEntity (does not inherit BaseEntity)
        fixture.Customize<GroupEntity>(c => c
            .Without(x => x.Memberships)
            .With(x => x.Id, () => Guid.NewGuid()));

        // InvitationEntity (does not inherit BaseEntity)
        fixture.Customize<InvitationEntity>(c => c
            .Without(x => x.Msel)
            .Without(x => x.Team)
            .With(x => x.Id, () => Guid.NewGuid())
            .With(x => x.MselId, () => Guid.NewGuid())
            .With(x => x.TeamId, () => Guid.NewGuid()));
    }
}
