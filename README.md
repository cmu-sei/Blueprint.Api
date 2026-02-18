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

