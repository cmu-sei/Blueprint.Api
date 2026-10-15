# Blueprint.Api

This project provides a RESTful API for Blueprint, the Crucible scenario design and MSEL (Master Scenario Events List) management application.

Run locally (`dotnet run --project Blueprint.Api`), blueprint.api is available at localhost:4724, with the swagger page at localhost:4724/api/index.html. The Docker image listens on port 8080.

# Testing

`Blueprint.Api.Tests` holds the test suite.  Run it from the repository root:

    dotnet test

**Docker must be running.** The suite uses Testcontainers PostgreSQL and gives every test its own
database, cloned from a migrated template.  Nothing else is needed — no Keycloak, no Player, Gallery,
CITE or Steamfitter, and no network.

To run one class or one test:

    dotnet test --filter "FullyQualifiedName~.OrganizationEndpointTests"
    dotnet test --filter "FullyQualifiedName~.MselPageEndpointTests.Create_ForAnOwnerOfTheMsel_Is201"

`--filter FullyQualifiedName~X` is a substring match on the whole name, so prefix the namespace when a
class name is a suffix of another (`~.TeamEndpointTests`, not `~TeamEndpointTests`; the classes sit in
sub-namespaces such as `Blueprint.Api.Tests.Controllers`, so `~Tests.X` does not match).

See [docs/Testing.md](docs/Testing.md) for the harness, the conventions a new test has to respect, and coverage.

# Database Migrations

When the data model is changed, a new database migration must be created.  The commands need the `dotnet-ef` tool, which the repository's tool manifest does not include (`dotnet tool install --global dotnet-ef`).  From the Blueprint.Api directory, run this command to create the new migration:

    dotnet ef migrations add <new_migration_name> --project ../Blueprint.Api.Migrations.PostgreSQL/Blueprint.Api.Migrations.PostgreSQL.csproj

Running the app will automatically migrate the database.
To Roll back a migration, first update the database to the previous migration

    dotnet ef database update <previous_migration_name> --project ../Blueprint.Api.Migrations.PostgreSQL/Blueprint.Api.Migrations.PostgreSQL.csproj

Then remove the migration

    dotnet ef migrations remove --project ../Blueprint.Api.Migrations.PostgreSQL/Blueprint.Api.Migrations.PostgreSQL.csproj

# MSEL Push Performance and Database Connections

## Concurrent Request Configuration

When pushing MSELs to external systems (CITE, Gallery, Player, Steamfitter), Blueprint uses parallel processing to improve performance. The maximum concurrent requests can be configured in `appsettings.json` under the `ClientSettings` section:

```json
{
  "ClientSettings": {
    "CiteMaxConcurrentRequests": 5,      // Max parallel requests for CITE operations (actions, duties, moves)
    "GalleryMaxConcurrentRequests": 5,   // Max parallel requests for Gallery operations (cards, articles)
    "PlayerMaxConcurrentRequests": 3     // Max parallel requests for Player operations (applications)
  }
}
```

**Default values (conservative for small database instances):**
- CiteMaxConcurrentRequests: 5
- GalleryMaxConcurrentRequests: 5
- PlayerMaxConcurrentRequests: 3

## Database Connection Pool Considerations

Higher concurrency improves MSEL push performance but consumes more database connections. Each concurrent request holds a database connection. Consider your environment:

**Development (local PostgreSQL)**:
- Default settings work well
- PostgreSQL default: 100 max_connections

**Production PostgreSQL**:
- Connection limits vary by database configuration and available memory
- Managed database services typically limit connections based on instance size
- Common production limits range from ~80 connections (small instances) to 500+ (large instances)

**Tuning Guidelines**:
- **Small instances (< 200 connections)**: Use default settings (5/5/3)
- **Medium instances (200-500 connections)**: Can increase to 10/10/5
- **Large instances (> 500 connections)**: Can increase to 15-20
- **Production with connection pooler (PgBouncer, etc.)**: Can increase to 20+

**Connection Usage During MSEL Push**:
- Operations run sequentially across APIs (Player view and teams → Gallery → CITE → Steamfitter → Player applications)
- Gallery cards and articles, CITE moves, duties and actions, and Player applications run in parallel up to the configured limit
- Approximate peak connection usage: `MaxConcurrentRequests × NumberOfTeams` (for team-related operations)

# Permissions

## System Roles

**Administrator** - Full system access, all permissions enabled

**Content Developer** - Can create and manage MSELs:
* CreateMsels
* ViewMsels
* EditMsels
* ManageMsels

**Observer** - Read-only access to all system resources:
* ViewMsels, ViewUnits, ViewOrganizations, ViewDataFields
* ViewInjectTypes, ViewCatalogs, ViewGalleryCards
* ViewCiteActions, ViewCiteDuties
* ViewUsers, ViewRoles, ViewGroups
* ViewCompetencyFrameworks

## MSEL-Specific Roles

Users can be assigned roles for individual MSELs. A role applies only while the user is also a member of a unit added to the MSEL, and the user who created a MSEL counts as its Owner.

* **Owner** - Full control over the MSEL (includes all permissions below)
* **Editor** - Can modify MSEL content and events
* **Approver** - Can approve MSEL events
* **MoveEditor** - Can add, edit and delete the MSEL's moves
* **Viewer** - Read-only access to the MSEL
* **Evaluator** - Can evaluate MSEL execution

## Team Roles

A team role is the name of one of CITE's team roles. Blueprint reads the list from CITE (`GET api/teamroles`), and when a MSEL is pushed to CITE each team member gets the CITE team role of that name; a member whose role name CITE does not have is added to the CITE team without a role.

