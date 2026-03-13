# Blueprint.Api.Tests.Unit

Unit tests for Blueprint API services and infrastructure using in-memory Entity Framework Core and FakeItEasy mocking.

## Purpose

Tests individual service methods and infrastructure components in isolation with mocked dependencies and in-memory database contexts.

## Files

### MappingConfigurationTests.cs

Validates AutoMapper configuration for Blueprint API:

- **AutoMapperConfiguration_IsValid** - Verifies mapper can be created from all registered profiles in Blueprint.Api assembly
- **AllProfiles_AreRegistered** - Ensures all Profile classes are discovered and registered (expects 20+ profiles)

Uses `cfg.AddMaps(typeof(Startup).Assembly)` to auto-discover profiles.

### Services/InjectServiceTests.cs

Unit tests for `InjectService` CRUD operations:

- Create, read, update, delete operations for Inject entities
- InjectType associations
- DataValue handling
- Catalog integration

### Services/MselServiceTests.cs

Unit tests for `MselService` CRUD operations:

- MSEL creation, retrieval, update, deletion
- Integration queue interactions (Player, Gallery, CITE, Steamfitter)
- Team and Unit associations
- ScenarioEvent management
- Permission validation

Example test structure:

```csharp
[Fact]
public async Task GetAsync_WithValidId_ReturnsMappedMsel()
{
    // Arrange
    using var context = TestDbContextFactory.Create<BlueprintContext>();
    var mselEntity = _fixture.Create<MselEntity>();
    var expectedMsel = new Blueprint.Api.ViewModels.Msel { /* ... */ };

    context.Msels.Add(mselEntity);
    await context.SaveChangesAsync();

    A.CallTo(() => _fakeMapper.Map<Blueprint.Api.ViewModels.Msel>(A<MselEntity>._))
        .Returns(expectedMsel);

    var service = new MselService(context, /* mocked dependencies */, _fakeMapper, /* ... */);

    // Act
    var result = await service.GetAsync(mselEntity.Id, true, CancellationToken.None);

    // Assert
    result.ShouldNotBeNull();
    result.Id.ShouldBe(mselEntity.Id);
}
```

### Services/UserServiceTests.cs

Unit tests for `UserService` operations:

- User CRUD operations
- Permission management
- Team membership
- Group assignments

## How to Run

```bash
cd /mnt/data/crucible/blueprint/blueprint.api

# Run all unit tests
dotnet test Blueprint.Api.Tests.Unit

# Run specific test class
dotnet test Blueprint.Api.Tests.Unit --filter FullyQualifiedName~MselServiceTests

# Run with coverage
dotnet test Blueprint.Api.Tests.Unit /p:CollectCoverage=true /p:CoverletOutputFormat=opencover
```

## Key Patterns

### In-Memory Database

```csharp
using var context = TestDbContextFactory.Create<BlueprintContext>();
var entity = _fixture.Create<MselEntity>();
context.Msels.Add(entity);
await context.SaveChangesAsync();
```

`TestDbContextFactory` (from Crucible.Common.Testing) creates an in-memory EF Core context with SQLite provider.

### FakeItEasy Mocking

```csharp
var fakeMapper = A.Fake<AutoMapper.IMapper>();
A.CallTo(() => fakeMapper.Map<Msel>(A<MselEntity>._)).Returns(expectedViewModel);
```

All external dependencies (ILogger, IMapper, integration clients) are mocked with FakeItEasy.

### AutoFixture

```csharp
_fixture = new Fixture()
    .Customize(new AutoFakeItEasyCustomization())
    .Customize(new BlueprintCustomization());

var mselEntity = _fixture.Create<MselEntity>();
```

Combines AutoFakeItEasy (auto-mocking) with BlueprintCustomization (entity generation).

### Shouldly Assertions

```csharp
result.ShouldNotBeNull();
result.Id.ShouldBe(expectedId);
result.Name.ShouldBe("Expected Name");
collection.Count.ShouldBeGreaterThan(0);
```

Fluent assertion library with readable error messages.

### ClaimsPrincipal Setup

```csharp
var claims = new List<Claim>
{
    new("sub", Guid.NewGuid().ToString()),
    new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())
};
var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
```

Services requiring user context receive a minimal ClaimsPrincipal.

## Dependencies

- **FakeItEasy** 8.3.0 - Mocking framework
- **MockQueryable.FakeItEasy** 7.0.3 - Queryable mock support
- **Microsoft.EntityFrameworkCore.InMemory** 10.0.1 - In-memory database
- **Shouldly** 4.2.1 - Assertion library
- **xUnit** 2.9.3 - Test framework
- **AutoFixture** 4.18.1 + AutoFakeItEasy, Xunit2 - Test data generation
- **Microsoft.NET.Test.Sdk** 18.0.1 - Test execution
- **coverlet.collector** 6.0.2 - Code coverage

## Project References

- **Blueprint.Api** - Main API project (services, controllers)
- **Blueprint.Api.Data** - Data layer (DbContext, entities)
- **Blueprint.Api.Tests.Shared** - Shared fixtures
- **Crucible.Common.Testing** - TestDbContextFactory and utilities

---

Copyright 2026 Carnegie Mellon University. All Rights Reserved.
Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
