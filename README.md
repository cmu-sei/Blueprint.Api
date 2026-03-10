# Blueprint.Api

This project provides a RESTful API for Blueprint, the Crucible scenario design and MSEL (Master Scenario Events List) management application.

By default, blueprint.api is available at localhost:4724, with the swagger page at localhost:4724/api/index.html.

# Database Migrations

When the data model is changed, a new database migration must be created.  From the Blueprint.Api directory, run this command to create the new migration:

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
- Operations run sequentially across APIs (Gallery → CITE → Steamfitter → Player)
- Within each API, operations run in parallel up to the configured limit
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

## MSEL-Specific Roles

Users can be assigned roles for individual MSELs:

* **Owner** - Full control over the MSEL (includes all permissions below)
* **Editor** - Can modify MSEL content and events
* **Approver** - Can approve MSEL events
* **MoveEditor** - Can advance the MSEL timeline/moves
* **Viewer** - Read-only access to the MSEL
* **Evaluator** - Can evaluate MSEL execution

## Team Roles

Roles for team participation during MSEL execution:

* **Observer** - View team activities
* **Inviter** - Invite users to the team
* **Incrementer** - Advance team progress
* **Modifier** - Modify team settings
* **Submitter** - Submit team responses

