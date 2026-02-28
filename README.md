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

## Batch Size Configuration

When pushing MSELs to external systems (CITE, Gallery, Player, Steamfitter), Blueprint uses parallel batching to improve performance. Batch sizes can be configured in `appsettings.json` under the `ClientSettings` section:

```json
{
  "ClientSettings": {
    "CiteBatchSize": 10,      // Parallel batch size for CITE operations (actions, duties, moves)
    "GalleryBatchSize": 10,   // Parallel batch size for Gallery operations (cards, articles)
    "PlayerBatchSize": 5      // Parallel batch size for Player operations (applications)
  }
}
```

**Default values:**
- CiteBatchSize: 10
- GalleryBatchSize: 10
- PlayerBatchSize: 5

## Database Connection Pool Considerations

Higher batch sizes improve MSEL push performance but consume more database connections. Consider your environment:

**Development (local PostgreSQL)**:
- Default batch sizes work well
- PostgreSQL default: 100 max_connections

**AWS RDS PostgreSQL**:
- Connection limits based on instance memory: `LEAST({DBInstanceClassMemory/9531392}, 5000)`
- Common limits:
  - db.t3.micro (1GB): ~87 connections
  - db.t3.medium (4GB): ~338 connections
  - db.m5.large (8GB): ~677 connections

**Tuning Guidelines**:
- **Small RDS instances (< 200 connections)**: Reduce batch sizes to 5
- **Medium RDS instances (200-500 connections)**: Use default batch sizes
- **Large RDS instances (> 500 connections)**: Can increase batch sizes to 15-20
- **Production with connection pooler (RDS Proxy/PgBouncer)**: Can increase batch sizes to 20+

**Connection Usage During MSEL Push**:
- Batch operations run sequentially across APIs (Gallery → CITE → Steamfitter → Player)
- Within each API, operations run in parallel batches
- Approximate peak connection usage: `BatchSize × NumberOfTeams` (for team-related operations)

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

