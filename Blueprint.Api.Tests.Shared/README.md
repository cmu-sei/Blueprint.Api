# Blueprint.Api.Tests.Shared

Shared test infrastructure and fixtures for Blueprint API test projects.

## Purpose

Provides reusable AutoFixture customizations for Blueprint entity types, preventing circular reference issues from Entity Framework navigation properties and ensuring consistent test data generation across unit and integration tests.

## Files

### Fixtures/BlueprintCustomization.cs

AutoFixture customization that registers factories for all Blueprint entity types, including:

- **MselEntity** - Central MSEL aggregate with integrations (Player, Gallery, CITE, Steamfitter)
- **ScenarioEventEntity** - Events with Inject, DataValue, and SteamfitterTask relationships
- **InjectEntity** - Injects with InjectType and DataValue associations
- **InjectTypeEntity** - Inject types with DataField definitions
- **CardEntity** - Cards with Team assignments
- **TeamEntity** - Teams with Users, Invitations, and application relationships
- **UserEntity** - Users with Role, Team, Unit, and Group memberships
- **UnitEntity** - Units with User and MSEL associations
- **MoveEntity** - MSEL moves
- **DataFieldEntity** - Data field definitions (String, Integer, etc.)
- **DataValueEntity** - Data values for ScenarioEvents and Injects
- **DataOptionEntity** - Options for DataFields
- **MselPageEntity** - MSEL pages (does not inherit BaseEntity)
- **MselUnitEntity** - MSEL-Unit join table
- **TeamUserEntity** - Team-User join table
- **PermissionEntity** - System permissions
- **UserPermissionEntity** - User-Permission join table
- **CatalogEntity** - Inject catalogs with hierarchy
- **CatalogInjectEntity** - Catalog-Inject join table
- **CatalogUnitEntity** - Catalog-Unit join table
- **SystemRoleEntity** - System roles with permissions
- **GroupEntity** - User groups
- **InvitationEntity** - Team invitations

Each customization removes navigation properties (`Without()`), assigns unique IDs, and sets required enum/DateTime values to prevent AutoFixture recursion errors.

## How to Use

```csharp
using AutoFixture;
using Blueprint.Api.Tests.Shared.Fixtures;

var fixture = new Fixture().Customize(new BlueprintCustomization());
var msel = fixture.Create<MselEntity>();
var scenarioEvent = fixture.Create<ScenarioEventEntity>();
```

## Running Tests

This is a shared library project with no executable tests. It is referenced by:

- `Blueprint.Api.Tests.Unit`
- `Blueprint.Api.Tests.Integration`

Run tests in those projects:

```bash
cd /mnt/data/crucible/blueprint/blueprint.api
dotnet test Blueprint.Api.Tests.Unit
dotnet test Blueprint.Api.Tests.Integration
```

## Dependencies

- **AutoFixture** 4.18.1 - Test data generation
- **AutoFixture.AutoFakeItEasy** 4.18.1 - Integration with FakeItEasy mocking
- **AutoFixture.Xunit2** 4.18.1 - xUnit integration attributes
- **Blueprint.Api** - Main API project (entity definitions)
- **Blueprint.Api.Data** - Data layer (DbContext, entities)
- **Crucible.Common.Testing** - Shared Crucible test utilities

## Key Patterns

- **OmitOnRecursionBehavior** - Prevents infinite loops from EF navigation properties
- **Guid.NewGuid()** - Generates unique IDs for all entities
- **DateTime.UtcNow** - Sets DateCreated/StartTime to current time
- **Explicit Enum Assignment** - Ensures valid enum values (MselItemStatus.Pending, IntegrationType.Deploy, etc.)

---

Copyright 2026 Carnegie Mellon University. All Rights Reserved.
Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
