Blueprint.Api has an automated test suite in the `Blueprint.Api.Tests` project, at the repository root beside the four application projects. This document covers how the suite is built, how to run it, and the conventions to follow when adding to it.

# Testing

The suite is built on xUnit v3 and NSubstitute, and runs against a real PostgreSQL instance started in a container. It is on the Crucible API test standard in `agent-docs/api-testing/` of the workspace (shared by every Crucible API): the harness files under `Blueprint.Api.Tests/Support/Shared/` are copied from there and are not edited here, and the test-project `Directory.Build.props`, `xunit.runner.json`, `coverlet.runsettings` and `.github/workflows/build-and-test.yml` are the standard's copies too. These are not isolated unit tests. A typical test sends an HTTP request to the application hosted in process, through the real `Startup`, routing, model binding, authorization, claims transformer, AutoMapper profiles, MediatR event handlers and a real database with the real migrations, then asserts on the response, on what changed in the database and on what was broadcast. Collaborators that leave the process are substituted, and so is one in-process service: `BlueprintAppFactory` substitutes `IXApiService`, so the xAPI routes (`XApiLiveEndpointTests`) and the checkbox statements (`DataValueXApiTests`, which shares `DataValueTestsBase` with `DataValueEndpointTests`) are asserted over `XApiEnabledFactory`, which runs the real `XApiService` and lets a test read the queued row (`XApiQueuedStatements`). Only `GET xapi/statements` stays on the substitute, because the real service builds its own `HttpClient`.

# Running the tests

```bash
dotnet test
```

Docker must be running. The suite starts and disposes its own PostgreSQL container through [Testcontainers](https://testcontainers.com/), so there is nothing to install and no local database to keep in sync. Nothing else is needed: no Keycloak, no Player, Gallery, CITE or Steamfitter, no network access and no `appsettings.json` edit. The container starts when the first test asks for a database, so the tests that take none still run without Docker.

A single class or a single test can be run with a filter:

```bash
dotnet test --filter "FullyQualifiedName~.MoveEndpointTests"
dotnet test --filter "FullyQualifiedName~.MoveEndpointTests.GetByMsel_ForEveryMselRole_Is200"
```

`~` is a substring match on the whole name, namespace included, so `~TeamEndpointTests` also selects `CardTeamEndpointTests`; the leading dot (`~.TeamEndpointTests`) keeps one class name from matching another that ends with it. Filters OR with `|`.

# Coverage

```bash
dotnet test --collect:"XPlat Code Coverage"
scripts/coverage.sh                      # the same run, plus an HTML report and a ranked list of untested lines
```

`coverlet.runsettings` sits beside the test project and is applied automatically, because `Blueprint.Api.Tests.csproj` names it in `RunSettingsFilePath`. It is the standard's file: the collector is disabled unless a run asks for it, and it excludes the migrations assembly, generated code and auto-implemented properties. `Cite.Api.Client` and `Gallery.Api.Client`, the checked-in NSwag clients, compile into `Blueprint.Api` and are reported with it.

`scripts/coverage.sh` and `.github/workflows/coverage.yml` (manual dispatch) are this repository's own extras; neither is a gate. Coverage is a local diagnostic rather than a maintained statistic, so this document carries no figure that can drift from the suite.

# Build settings

The root `Directory.Build.props` sets `TreatWarningsAsErrors` for every project, and `Blueprint.Api.Tests/Directory.Build.props` (the standard's) imports it and sets the same for the test project. That is what makes the xUnit analyzers that ship with `xunit.v3` load-bearing:

- xUnit1004 - A skipped test. Raised to warning in the root `.editorconfig`, and therefore an error.
- xUnit1026 - A `[Theory]` parameter the test does not use.
- xUnit1051 - An awaited call that has a `CancellationToken` overload and was not given one. Pass `Ct`.
- xUnit2029, xUnit2031 - `Assert.Empty`/`Assert.Single` with a predicate. Materialize the sequence first.
- CS4007 - A collection expression that binds to `ReadOnlySpan<T>` across an `await`. Hoist the awaited call into a local.

Restore-time warnings stay warnings, through `WarningsNotAsErrors`: NU1901 to NU1904 are the NuGet audit (AutoMapper 13 and MediatR 12 are pinned deliberately, and the rest is transitive), NU1701 reports TinCan's .NET Framework assets, and NU1510 names three `PackageReference`s of `Blueprint.Api` the framework already provides.

Package versions live in `Directory.Packages.props` (central package management); the test packages are pinned by the standard (`agent-docs/api-testing/test-packages.props`) and `sync.sh` checks them. `Microsoft.AspNetCore.SignalR.Client` is the one test package beyond the standard's list: `MainHubConnectionTests` dials the hub the way blueprint.ui does.

# How the harness works

The application is hosted in process over `TestServer`, through `BlueprintAppFactory`, a `WebApplicationFactory<Program>`. Two base classes cover almost every test.

## DatabaseTestBase

For tests that need a database but not the application:

- `Db` - A `BlueprintContext` over a database no other test can see.
- `NewContext()` - Another context over the same database, for re-reading through a cold change tracker after a save. The caller owns it.
- `ReadBack(rb => rb.Teams.SingleAsync(...))` - the shared `DatabaseTestBase` helper (promoted from this repo): runs one query on a fresh `NewContext()` and disposes it, for a cold re-read inside an assertion.
- `Seed(params object[])` - Adds entities through `Db` and saves.
- `Ct` - The cancellation token of the running test.
- `WaitUntil(condition, what)` - Polls for an effect that arrives on a thread the test does not own.

These come from the shared `DatabaseTestBase<BlueprintContext>`. `BlueprintContextFactory` builds the context the way production does: both interceptors (`SanitizerInterceptor` and the entity-event interceptor), `MultipleCollectionIncludeWarning` configured to throw, and the HTML sanitizer.

## ApiTestBase

Extends `DatabaseTestBase` for tests that drive the application over HTTP:

- `Root` and `RootClient` - An actor holding every system permission, seeded before every test, and a client acting as them.
- `Actor()` - Starts describing another actor: `await Actor().WithSystemPermissions(SystemPermission.EditMsels).OnMsel(msel, MselRole.Owner).SeedAsync()`.
- `Client(actor)` - A client acting as a seeded actor. `Client()` carries no identity, so a request to an `/api/` route is a 401.
- `Hub` - The `HubRecorder<MainHub>` of this test's host. `MainHubBroadcasts` reads it by group.
- `Factory` - The class's host, with the substituted sibling clients (`Factory.Cite`, `.Gallery`, `.PlayerApi`, `.Steamfitter`, `.XApi`) and `OutboundHttp`.
- `ReadAsync<T>`, `AssertStatus`, `AssertProblem` - From the shared base, with the host's own JSON options.

Both fixtures arrive by constructor injection: `DatabaseFixture` from `[assembly: AssemblyFixture(...)]` in `AssemblyFixtures.cs`, and `BlueprintAppFactory` from `IClassFixture<BlueprintAppFactory>` on each hosted class.

## The factory is per class

`BlueprintAppFactory` is a class fixture, the standard's per-class variant, because hosted tests both arrange and assert on the substituted sibling API clients (`Factory.Cite.Received()` and friends). A run-wide substitute that tests assert on loses calls under load, since NSubstitute keeps its assertion state per thread and `TestServer` serves requests on the test thread pool. One host per class keeps those substitutes in the class's own scope. `ResetForTest()` clears them, and renews the hub recorder, before every test. The cost is about a second of host startup per class.

## What is real, and what is not

| Real | Substituted or removed |
| --- | --- |
| `Startup`, the MVC pipeline, routing, model binding, both global filters | The sibling API clients: CITE, Gallery, Player, Steamfitter, and `IXApiService` (except in `XApiEnabledFactory`) |
| Authorization, `AuthorizationClaimsTransformer`, `UserClaimsService` | `IHostedService`: the four background workers are removed |
| PostgreSQL, EF Core, the real migrations, both interceptors | `IHubContext<MainHub>`, replaced by the shared `HubRecorder<MainHub>` (which records a `Clients.Groups(...)` send for each group) |
| AutoMapper's 38 profiles, MediatR and the 24 event handlers | Outbound HTTP, over the shared `StubHttpClientFactory` (`Factory.OutboundHttp`) |
| The three singleton queues | Token validation, replaced by the shared `TestAuthHandler` |

- **Token validation.** The shared `TestAuthHandler` mints the identity a validated token would have produced, from `X-Test-User`. Blueprint registers it under the scheme name `Bearer` rather than the standard's `Test`, because `MainHub` carries `[Authorize(AuthenticationSchemes = "Bearer")]`: under any other name every hub request is unauthenticated. The factory removes the application's `IConfigureOptions<AuthenticationOptions>` first, since `AddJwtBearer` already claims the name. `TestConfiguration` switches on two of the shared handler's opt-ins: `TestAuthentication:UserFromBearer`, which reads the user from an `Authorization: Bearer <user id>` header when `X-Test-User` is absent, so `Startup`'s `?bearer=` query-string promotion is observable (`MiddlewareTests`), and `TestAuthentication:Issuer`, the `iss` claim a Keycloak token carries and `XApiService` reads. The `email` claim the invitation endpoints read comes from the shared `X-Test-Email` header (`ClientWithEmail`).
- **Permissions come from rows.** `TestActor` seeds a user, a system role with exactly the permissions named, and the MSEL, unit, team and group rows the requirement helpers read, so the real claims transformer and the real `Msel*Requirement` helpers decide every request.
- **The database.** A request resolves the database of the test that sent it, by the `X-Test-Session` header through the shared `TestDatabaseScope`. A context resolved outside a request (a hub invocation, `Program.Main`'s initialization) uses the host's own session.
- **Configuration.** The content root is `Blueprint.Api/`, so its `appsettings.json` is loaded; `TestConfiguration` overrides only the keys that break or weaken a test run (claims caching, and the shared handler's `UserFromBearer` and `Issuer` opt-ins), and the factory supplies the database through `TestDatabaseScope`. The environment is `Development`, so a 500 carries its message.

## The host database (step 1B)

`Program.CreateWebHostBuilder` matches neither convention `HostFactoryResolver` looks for, so `WebApplicationFactory` runs `Program.Main` on a background thread, and `Main` runs `.InitializeDatabase()` with no production switch to skip it. The factory therefore gives the host a throwaway database cloned from the template (`HostDatabaseName`), the standard's step 1B. It is never the template, since a connection held against the template breaks every later clone. The host's own context is built from the host session's services, so the seeding `Main` does in the background raises no broadcast into a test's recorder.

## Isolation

Migrations are applied once, to a template database, by the shared `PostgresTestDatabase<BlueprintContext>` that `DatabaseFixture` wraps (migrations assembly `Blueprint.Api.Migrations.PostgreSQL`). Each test gets its own database created from that template, a file-level copy. It is real isolation rather than a rolled-back transaction, which matters here: `EntityEventInterceptor` publishes on `SavedChanges` only when there is no ambient transaction, so a test wrapped in one would silence every notification the application sends.

The host is shared by every test in a class, and the tests of a class run in order. Everything registered as a singleton (the queues, `HubCache`, `CompetencyFrameworkImportProgressService`) is shared within the class, so key what a test arranges and asserts on to ids it owns.

# Adding a test

1. Put the file where the code under test lives: a controller's endpoints in `Controllers/`, a service in `Services/`, an event handler in `Infrastructure/EventHandlers/`.
2. Derive from `ApiTestBase` (with `IClassFixture<BlueprintAppFactory>`) if the test sends a request, or `DatabaseTestBase` if it only needs a database. Some tests need neither.
3. Seed with the `TestData` object mothers and add a mother there if one is missing. Seed a caller with `Actor()`.
4. Name the method as a sentence, and pass `Ct` to anything awaited.
5. Assert on the database through `NewContext()`, not on the seeded objects, and on broadcasts through `Hub` keyed on a group the test owns.
6. For an endpoint, test authorization first: allowed at the minimum permission, and denied with a near miss (a close but wrong permission, or the right role on another MSEL through `OnNewMsel`), never an actor holding nothing. Where the gate is a data row rather than a permission (the invitation endpoints), the denied test names the row on a line of its own: `// Data-row gate: <the row>.`

# Layout

The test project mirrors `Blueprint.Api`:

```
Blueprint.Api.Tests/
  Controllers/       one file per controller, over HTTP; RouteAuthorizationTests holds the per-route 401/403 table
  Services/          services and background workers driven directly or through their routes
  Infrastructure/    Authorization, EventHandlers, Extensions, Filters, Identity, JsonConverters, Mappings
  Hubs/              MainHub by direct invocation (HubHarness) and over a real HubConnection
  CompositionTests.cs, MiddlewareTests.cs   Startup's composition and pipeline
  Support/           the harness: Blueprint's own files, the self-tests and the extras
    Shared/          the standard's shared files, copied by sync.sh and never edited here
```

The shared files of the standard (namespace `Crucible.Api.Testing`) are described in `agent-docs/api-testing/README.md`. Blueprint's own files, built from the standard's templates:

- `DatabaseFixture`, `BlueprintContextFactory`, `DatabaseTestBase`, `ApiTestBase`, `TestConfiguration` - As described above.
- `BlueprintAppFactory` - The per-class host (step 1B), with the substituted sibling clients, `Hub` and `OutboundHttp`.
- `TestActor` - `WithSystemPermissions`, `WithAllSystemPermissions`, `WithRole`, `OnMsel(msel, role)`, `OnTeam(team)`, and the near-miss steps `OnNewMsel(role)` (a role on a MSEL someone else created), `OnNewTeam()`, `InUnit(unit)`, `InUnitOf(msel)` (a unit on the MSEL with no role) and `InGroup(group)`.
- `TestData` - Object mothers, and the seeded roles (`TestData.Roles`).
- `ClaimsPrincipalBuilder`, `AuthorizationHarness` - For the tests of the authorization stack itself.
- `TestMapper` - The real AutoMapper configuration without the host. `MappingConfigurationTests` checks it against the host's.

Blueprint's extras in `Support/`:

- `MainHubBroadcasts` - Reads the recorder by group and method, and names the always-joined groups.
- `SiblingApiHandler` - The transport for the `Integration*Extensions` classes, which build their own clients from `IHttpClientFactory`: the shared `StubHttpMessageHandler` with `RefusesUnmatched()`, whose route rules (`Answers`, `AnswersOnce`, `AnswersJson`, `Throws`) and `Sent` record it forwards, behind a `DelegatingHandler` that adds the concurrency controls only these tests need (`Yields`, `Holds`, `HoldsUntil`, `MaxInFlight`). `AsFactory()` hands out the shared `StubHttpClientFactory` over it.
- `IntegrationServiceHarness`, `QueueWorkerHarness` - Construct a background worker with its dependencies and drive it through its real queue.
- `XApiEnabledFactory` - The one factory subclass, for the xAPI options `Startup` binds at construction.
- `CompositionFactory` - Snapshots the service collection before the harness substitutes into it.
- `MselGraph`, `Tokens`, `Workbooks`, `Frameworks` - A whole exercise's rows, a real bearer token, an Open XML workbook and competency framework documents.

`DatabaseHarnessTests`, `HttpHarnessTests` and `TestActorTests` are tests of the harness itself: that two tests inserting the same `system_roles.name` do not see each other and a second insert within one test is refused, that the real migrations and naming are in force, that a request with no identity is a 401 and an actor without the permission a 403, that a request reaches the database of the test that sent it, and that each actor step produces the rows it promises. When the harness breaks, these fail instead of a hundred unrelated tests.

# Conventions

The conventions are the standard's (`agent-docs/api-testing/CONVENTIONS.md`): sentence names, no Arrange/Act/Assert comments, a real assertion over a substitute's call count, `NewContext()` disposed by its caller, and authorization tested first with near-miss denials.

Defects found while writing a test are characterized, not fixed. The test asserts the current behaviour and passes; it fails once the behaviour is corrected. The defect is not described in the code: the test carries at most a one-line `/// <summary>` of the current behaviour, and the description, the production `file:line`, the expected behaviour, the fix and the test to flip are an entry in `agent-docs/api-test-bugs/blueprint.api.md` of the workspace, outside this repository (CONVENTIONS.md section 3). `verify.sh` checks both halves.

# Common mistakes

- `Db` is not the context a request used. Re-read through `NewContext()`; `Db`'s change tracker only confirms what the test set.
- EF's change-tracker fix-up: rows seeded through `Db` stay tracked, so a filtered re-read through `Db` sees them fixed into navigations whatever the filter says. Where a test depends on a row not being tracked, seed through a `NewContext()` (`EventHandlerTests.SeedCold`).
- `ViewModels.Base` makes `DateCreated` and `CreatedBy` non-nullable, so a body record declaring them nullable sends nulls and the request is a 400 that never reaches the controller. A body that must distinguish "absent" from "null" is a `record` varied with `with`.
- Every `int` crosses the wire as a JSON string (`JsonIntegerConverter`), and `Location` headers are lowercased (`RouteOptions.LowercaseUrls`).
- Await a `MultipartFormDataContent` inside its `using`: `TestServer` reads the body inside `SendAsync`.
- A test-side query with two collection `Include`s throws, as in production. Add `.AsSplitQuery()`.
- `Arg.Any<CancellationToken>()` cannot tell a forwarded token from a dropped one. Use `Arg.Is<CancellationToken>(x => x.CanBeCanceled)`.
- A setting `Startup` reads while building needs a factory subclass (`XApiEnabledFactory`), not an arrangement.
- `CurrentHttpContext` is a process-wide static set by `app.UseHttpContext()`; keep everything that reads it in one class.
- A broadcast assertion names the group the test owns (a seeded MSEL's id) rather than asking whether anything was sent: the host serves a whole class.

# Continuous integration

`.github/workflows/build-and-test.yml` restores, builds and runs the suite for pull requests and for pushes to `main`. It is the standard's workflow with this repository's test project filled in, and `sync.sh` keeps it identical; it replaced the repository's earlier `test.yml`. The job greps the provider banner (`[Blueprint.Api.Tests] database provider: PostgreSQL`) out of the log, so a harness change cannot silently stop exercising the production database provider. There is no `services: postgres:` block, because Testcontainers starts the container itself. The TRX of the run is uploaded as `test-results`, also when the job fails. Coverage is not collected in CI.

# Follow-ups

- Contrast requests: several tests pair an allowed and a denied request in one method; splitting them into one request per test would make each failure name its case.
- Mixed casing in test method names: older files use `PascalCase_WithUnderscores` and the near-miss denials use `snake_case_sentences` (`Create_is_forbidden_for_a_viewer_of_the_msel`); pick one when the files are next touched.
