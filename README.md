# Blueprint.Api

This project provides a RESTful API for Blueprint, the Crucible scenario design and MSEL (Master Scenario Events List) management application.

Run locally (`dotnet run --project Blueprint.Api`), blueprint.api is available at localhost:4724, with the swagger page at localhost:4724/api/index.html. The Docker image listens on port 8080.

# Testing

`Blueprint.Api.Tests` holds the test suite.  Run it from the repository root:

    dotnet test

**Docker must be running.** The suite uses Testcontainers PostgreSQL and gives every test its own
database, cloned from a migrated template.  Nothing else is needed — no Keycloak, no Player, Gallery,
CITE or Caster, and no network.

To run one class or one test:

    dotnet test --filter-class "*.OrganizationEndpointTests"
    dotnet test --filter-method "*.MselPageEndpointTests.Create_ForAnOwnerOfTheMsel_Is201"

The filters match the whole name, namespace included, with `*` as a wildcard, so start with `*.` when a
class name is a suffix of another (`*.TeamEndpointTests`, not `*TeamEndpointTests`; the classes sit in
sub-namespaces such as `Blueprint.Api.Tests.Controllers`, so `*Tests.X` does not match). The suite runs on
Microsoft.Testing.Platform (`global.json`), so these are xUnit's options rather than VSTest's `--filter`.

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

When pushing MSELs to external systems (CITE, Gallery, Player), Blueprint uses parallel processing to improve performance. The maximum concurrent requests can be configured in `appsettings.json` under the `ClientSettings` section:

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
- Operations run sequentially across APIs (Player view and teams → Gallery → CITE → Player applications)
- Gallery cards and articles, CITE moves, duties and actions, and Player applications run in parallel up to the configured limit
- Approximate peak connection usage: `MaxConcurrentRequests × NumberOfTeams` (for team-related operations)

# Automation Tasks

Blueprint runs a MSEL's automation tasks itself; it no longer pushes them to Steamfitter. Authors add tasks to a
scenario event on its Automation tab and test them against one of their own Player views. Once the MSEL is
deployed, a Facilitator runs them on the Facilitator View, or they run at their event's time or after another task.
Settings are in the `Automation` section of `appsettings.json`.

**Secret keys.** Passwords, private keys and http headers are stored encrypted with AES-GCM. Without a key, a
task with a secret cannot be saved. Give each key an id and 32 random bytes, base64 encoded:

    Automation__SecretKeys__k1=$(openssl rand -base64 32)

New secrets use `Automation:ActiveSecretKeyId`, or the first key id in order when that is unset. To rotate, add a
new key, make it active, and keep the old one for as long as secrets written with it are stored.

**Outbound requests.** An http task may reach only the hosts in `Automation:Http:AllowedHosts`, or the Crucible
APIs Blueprint is configured with when that is empty. Blueprint's service account token is sent only to
`Automation:Http:TokenHosts`, which also defaults to the Crucible APIs. Link-local addresses, including cloud
metadata endpoints, are always refused, and loopback addresses are refused unless
`Automation:Http:AllowLoopbackAddresses` is true, which a development stack on one machine needs. SSH tasks may
reach only `Automation:Ssh:AllowedHosts`, and file uploads read only from `Automation:Files:LocalDirectory`.

**VM actions** go through the Player VM API at `ClientSettings:PlayerVmApiUrl`, as Blueprint's service account,
which needs the `player-vm` scope.

**More than one instance.** Instances share the work safely through leases in the database, but SignalR updates
reach only the clients of the instance that made them. Set `Automation:WorkerEnabled` to true on one replica only.

**Scoring.** A task has points it earns when its newest run in the deployment succeeds, as a Steamfitter task did. The
most the MSEL can earn adds up, for each chain of tasks, the best way each run can go: a task's points, the tasks it
triggers on completion, and the better of those on success and on failure, unless those on expiration offer more. The
Facilitator View and the Tasks page show the score, and each exercise run's xAPI `executed` statement carries the
points it earned; that statement needs `XApiOptions:IssuerUrl`, since a run ends with no request to name its user by.

**Participant tasks.** A task marked "Participants can run it" is offered on the MSEL's Tasks page,
`/msel/{id}/tasks`, to the members of its teams. Add it as a Player application,
`{blueprintUrl}/msel/{blueprintMselId}/tasks`, as Steamfitter's task page was.

**Upgrading from Steamfitter.** Pull deployed MSELs before upgrading, or their Steamfitter scenarios are left behind.
The upgrade converts each scenario event's Steamfitter task to an automation task, and so does importing an older
MSEL file. Within a group, each converted task runs after the one before it, and the first is run by hand or by
starting the group, as the push did. A task participants could run in Steamfitter can still be run on the Tasks page.
Http headers in an imported file are dropped and have to be entered again.

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
* **Facilitator** - Runs a deployed MSEL on the Facilitator View: starts moves and groups, runs, reruns and cancels automation tasks, and pauses automation. Editors and Approvers can open the Facilitator View read-only.

## Team Roles

A team role is the name of one of CITE's team roles. Blueprint reads the list from CITE (`GET api/teamroles`), and when a MSEL is pushed to CITE each team member gets the CITE team role of that name; a member whose role name CITE does not have is added to the CITE team without a role.

