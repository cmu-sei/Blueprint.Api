# Blueprint.Api.Tests.Integration

Integration tests for Blueprint API using WebApplicationFactory and Testcontainers PostgreSQL.

## Purpose

Tests the full HTTP request pipeline against a real PostgreSQL database, verifying controller endpoints, middleware, authentication, authorization, and database integration end-to-end.

## Files

### Fixtures/BlueprintTestContext.cs

WebApplicationFactory-based test fixture that:

- Spins up a real PostgreSQL container via Testcontainers
- Configures Blueprint API in "Test" environment
- Replaces production DbContext with test container connection string
- Replaces authentication with `TestAuthenticationHandler` (bypasses OIDC)
- Replaces authorization with `TestAuthorizationService` (allows all operations)
- Registers `HtmlSanitizer` for SanitizerInterceptor
- Implements `IAsyncLifetime` for xUnit fixture lifecycle management

Key methods:

- **GetDbContext()** - Returns scoped DbContext for direct database verification
- **ValidateDbStateAsync()** - Executes validation action against scoped DbContext
- **InitializeAsync()** - Starts PostgreSQL container before test class
- **DisposeAsync()** - Stops and removes container after test class

Example configuration:

```csharp
services.AddEventPublishingDbContextFactory<BlueprintContext>((sp, optionsBuilder) =>
{
    optionsBuilder
        .AddInterceptors(sp.GetRequiredService<SanitizerInterceptor>())
        .UseNpgsql(connectionString);
});

services.ReplaceService<IAuthorizationService, TestAuthorizationService>();
```

### Tests/Controllers/HealthCheckTests.cs

Health endpoint integration tests:

- Verifies `/health` or similar endpoints return 200 OK
- Ensures API starts correctly with test configuration

### Tests/Controllers/UserControllerTests.cs

User API endpoint integration tests:

- **GetUsers_ReturnsOk** - GET /api/users returns 200
- **CreateUser_WithValidData_ReturnsCreatedUser** - POST /api/users with User ViewModel
- **GetUser_ById_WithNonExistentId_ReturnsNotFound** - GET /api/users/{id} with invalid ID returns 404 or 403

Example test:

```csharp
[Fact]
public async Task GetUsers_ReturnsOk()
{
    // Arrange
    var client = _testContext.CreateClient();

    // Act
    var response = await client.GetAsync("/api/users");

    // Assert
    response.StatusCode.ShouldBe(HttpStatusCode.OK);
}
```

## How to Run

```bash
cd /mnt/data/crucible/blueprint/blueprint.api

# Run all integration tests
dotnet test Blueprint.Api.Tests.Integration

# Run specific test class
dotnet test Blueprint.Api.Tests.Integration --filter FullyQualifiedName~UserControllerTests

# Run with detailed output
dotnet test Blueprint.Api.Tests.Integration --logger "console;verbosity=detailed"
```

**Prerequisites:**

- Docker must be running (Testcontainers requires Docker daemon)
- Sufficient permissions to pull `postgres:latest` image
- Network access to Docker Hub

## Key Patterns

### IClassFixture Setup

```csharp
public class UserControllerTests : IClassFixture<BlueprintTestContext>
{
    private readonly BlueprintTestContext _testContext;

    public UserControllerTests(BlueprintTestContext testContext)
    {
        _testContext = testContext;
    }
}
```

xUnit creates one BlueprintTestContext instance per test class, starting/stopping the PostgreSQL container once for all tests in the class.

### HTTP Client

```csharp
var client = _testContext.CreateClient();
var response = await client.GetAsync("/api/users");
response.StatusCode.ShouldBe(HttpStatusCode.OK);
```

`CreateClient()` returns an HttpClient configured to call the in-memory test server.

### JSON Serialization

```csharp
var newUser = new User { Id = Guid.NewGuid(), Name = "Test User" };
var response = await client.PostAsJsonAsync("/api/users", newUser);
```

Uses System.Net.Http.Json extensions for automatic JSON serialization.

### Database Validation

```csharp
await _testContext.ValidateDbStateAsync(async dbContext =>
{
    var user = await dbContext.Users.FindAsync(userId);
    user.ShouldNotBeNull();
    user.Name.ShouldBe("Expected Name");
});
```

Validates database state after API operations.

### Test Authentication

Tests use `TestAuthenticationHandler` from Crucible.Common.Testing, which:

- Bypasses OIDC token validation
- Provides minimal ClaimsPrincipal with configurable claims
- Allows testing authorization logic without external identity provider

### Testcontainers Lifecycle

```csharp
public async Task InitializeAsync()
{
    _container = new PostgreSqlBuilder()
        .WithHostname("localhost")
        .WithUsername("blueprint_test")
        .WithPassword("blueprint_test")
        .WithImage("postgres:latest")
        .WithAutoRemove(true)
        .WithCleanUp(true)
        .Build();

    await _container.StartAsync();
}

public new async Task DisposeAsync()
{
    if (_container is not null)
        await _container.DisposeAsync();
}
```

Container starts before first test, stops after last test in class.

## Dependencies

- **Microsoft.AspNetCore.Mvc.Testing** 10.0.1 - WebApplicationFactory
- **Testcontainers.PostgreSql** 4.0.0 - PostgreSQL container management
- **Npgsql.EntityFrameworkCore.PostgreSQL** 10.0.0 - PostgreSQL provider
- **FakeItEasy** 8.3.0 - Mocking (minimal usage in integration tests)
- **Shouldly** 4.2.1 - Assertions
- **xUnit** 2.9.3 - Test framework with IAsyncLifetime support
- **AutoFixture** 4.18.1 + AutoFakeItEasy, Xunit2 - Test data generation
- **Microsoft.NET.Test.Sdk** 18.0.1 - Test execution
- **coverlet.collector** 6.0.2 - Code coverage

## Project References

- **Blueprint.Api** - Main API project (Startup, Program, controllers)
- **Blueprint.Api.Data** - Data layer (DbContext, migrations)
- **Blueprint.Api.Tests.Shared** - Shared fixtures
- **Crucible.Common.Testing** - TestAuthenticationHandler, TestAuthorizationService, extension methods

## Troubleshooting

### Docker Not Running

```
Error: Cannot connect to Docker daemon
```

**Solution:** Start Docker Desktop or Docker daemon before running tests.

### Port Conflicts

Testcontainers automatically assigns random host ports, avoiding conflicts. If issues persist, check for stale containers:

```bash
docker ps -a | grep blueprint
docker rm -f <container-id>
```

### PostgreSQL Image Pull

```
Error: Unable to pull image postgres:latest
```

**Solution:** Ensure network connectivity and Docker Hub access. Alternatively, specify a locally cached image version in BlueprintTestContext.

---

Copyright 2026 Carnegie Mellon University. All Rights Reserved.
Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
