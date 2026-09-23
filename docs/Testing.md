Blueprint.Api's tests live in one project, `Blueprint.Api.Tests`, at the repository root beside the
four application projects. This file is what a contributor needs to run them, to add one, and to read
what they found.

# The suite

3760 test cases in 94 classes and 115 files. A full run takes about a minute and a half. xUnit runs
one class's tests in order and several classes at once, so wall-clock time is not the sum of the parts.

Three kinds, and the base class a test derives from is what says which:

| Kind | Base class | Classes | Cases | What it needs |
| --- | --- | --- | --- | --- |
| Hosted | `ApiTestBase` | 60 | 2870 | A host and a database |
| Database | `DatabaseTestBase` | 23 | 601 | A database |
| Unit | none | 11 | 289 | Nothing |

- Hosted tests drive the real `Startup` over HTTP: routing, model binding, authorization, the claims
  transformer, AutoMapper, MediatR and EF. Anything whose subject is a status code belongs here.
- Database tests construct a service or a helper directly over a real PostgreSQL database. Anything
  whose subject is what a row looks like afterwards belongs here.
- Unit tests take no dependency the harness has to build - the converters, the filters, the four
  `Integration*Extensions` classes, the claims extensions, the AutoMapper configuration.

The stack, all versions centrally managed in `Directory.Packages.props`:

- `xunit.v3` 3.2.2 with `xunit.runner.visualstudio` 3.1.5 and `Microsoft.NET.Test.Sdk` 18.8.1. The
  test project is an executable (`OutputType=Exe`, `GenerateProgramFile=false`) because xunit v3 test
  projects host themselves.
- `Microsoft.AspNetCore.Mvc.Testing` 10.0.1 - hosts `Startup` in process.
- `Microsoft.AspNetCore.SignalR.Client` 10.0.1 - dials `MainHub` the way blueprint.ui does.
- `Testcontainers.PostgreSql` 4.13.0 - starts the database. There is no in-memory or SQLite fallback;
  see *Why PostgreSQL only* below.
- `NSubstitute` 6.0.0 - the four sibling API clients and a handful of options monitors.
- `coverlet.collector` 10.0.1 - loaded only when a run asks for coverage.

# Running the tests

From the repository root:

```sh
dotnet test
```

**Docker must be running.** The suite starts one `postgres:16-alpine` container per run. Without a
Docker daemon every database test fails immediately - in under a tenth of a second for the whole
suite, not one timeout per test - with the message `DatabaseFixture` throws, which names the missing
daemon and says there is deliberately no fallback. Unit tests still pass, because the container is
started lazily on first use.

**Nothing else is needed.** No Keycloak, no Player, Gallery, CITE, Steamfitter or Moodle, no network
access, no `appsettings.json` edit, no migration run, no seed file. The four sibling API clients are
substituted, authentication is a real handler over a test scheme, and the database is created and
dropped by the fixture.

Filtering:

```sh
dotnet test --filter "FullyQualifiedName~Tests.MoveEndpointTests"
dotnet test --filter "FullyQualifiedName~MselService"
dotnet test --filter "FullyQualifiedName~Tests.RouteAuthorizationTests|FullyQualifiedName~Tests.CompositionTests"
dotnet test --filter "FullyQualifiedName~Get_ForAnIdThatIsNotThere"
```

- `~` is a substring match on the **whole** name, namespace included. `~TeamEndpointTests` also
  selects `CardTeamEndpointTests`; prefix the namespace - `~Tests.TeamEndpointTests` - whenever one
  class name is a suffix of another.
- Filters OR with `|`.
- `dotnet test --list-tests` prints every case, theory arguments included.

# Build settings

`Directory.Build.props` sets `TreatWarningsAsErrors` for every project in the repository, and that is
what makes the analyzers load-bearing rather than advisory. The rules that bite while writing tests:

- `CS0162` - unreachable code. So `if (false && cond)` is **not** available as a mutation; neuter a
  `throw` with `_ = 0;` instead, or invert a call rather than a condition.
- `CS1717` - assignment to the same variable. So `x.MselId = x.MselId` is not a no-op mutation either.
- `CS4007` - a collection expression that binds to `ReadOnlySpan<T>` cannot cross an `await`. Hoist the
  awaited call into a local before asserting on it.
- `CS0168`, `CS0219` - an unused caught exception or local, which is easy to leave behind when editing
  an interpolated message.
- `xUnit1004` - a skipped test, raised to warning in `.editorconfig` and therefore an error. A
  conditional `SkipWhen`/`SkipUnless` is unaffected.
- `xUnit1026` - an unused theory parameter.
- `xUnit1051` - an awaited call that has a `CancellationToken` overload and was not given one. Pass
  `Ct`. The rule fires inside test methods only, so a call that must use a token-less overload can be
  named in a private helper, which says "deliberately" once instead of at every site.
- `xUnit2029`, `xUnit2031` - `Assert.Empty`/`Assert.Single` with a predicate. Materialize the sequence
  first: `Assert.Single(xs.Where(...).ToList())` satisfies the rule where `Assert.Single(xs.Where(...))`
  does not.

`.editorconfig` legislates analyzer severities and nothing else. Code style is deliberately absent:
the API is written with block-scoped namespaces and the test project with file-scoped ones, and
encoding either would turn every file into a diff without making anything more correct.

## Why some warnings stay warnings

`WarningsNotAsErrors` lists `NU1901`-`NU1904`, `NU1510` and `NU1701`. None of them can be acted on
from a project file, so promoting them would block every build rather than fix anything:

- `NU1901`-`NU1904` are the NuGet audit. `AutoMapper 13` and `MediatR 12` are pinned deliberately -
  version 15+ and 13+ require a commercial licence - and the rest is transitive.
- `NU1701` is `TinCan 1.3.0`, which ships .NET Framework assemblies only. It is the xAPI statement
  library and there is no `net10.0` build to move to.
- `NU1510` names three `PackageReference`s the framework already provides. Removing them changes what
  the application restores, so it is its own commit.

This is a backlog rather than a permanent exemption. Check it when bumping a dependency:

```sh
dotnet list package --vulnerable --include-transitive
dotnet list package --outdated
```

# How the harness works

Everything below is in `Blueprint.Api.Tests/Infrastructure/`.

## A database per test

`DatabaseFixture` is an assembly fixture, so one container serves the whole run:

1. Start `postgres:16-alpine`.
2. Migrate a template database, `blueprint_template`, once, with
   `MigrationsAssembly("Blueprint.Api.Migrations.PostgreSQL")` - the migrations are in a third
   assembly, so EF's default of "the context's own assembly" finds nothing.
3. Call `NpgsqlConnection.ClearAllPools()`. This is load-bearing: a pooled connection to the template
   blocks every later clone.
4. Per test, `CREATE DATABASE blueprint_test_N TEMPLATE blueprint_template` - a file copy, not a
   migration - and `DROP DATABASE ... WITH (FORCE)` afterwards.

The container is built and started inside a `Lazy<Task>`, and the `PostgreSqlBuilder.Build()` call is
inside the fixture's `try` rather than in a field initializer. Both are deliberate: `Build()` resolves
the Docker endpoint, so a builder in a field initializer throws from the *constructor* and defeats the
one-clear-error design. Keep container construction lazy.

### Why PostgreSQL only

- `BlueprintContext.OnModelCreating` applies `AddPostgresUUIDGeneration()` and `UsePostgresCasing()`
  only `if (Database.IsNpgsql())`, so another provider runs a different model.
- There is no SQLite migrations project.
- Nearly every write wraps an explicit `BeginTransactionAsync`, which the in-memory provider does not
  support.

A fallback that quietly replaced the provider would report a green run that never touched what
production uses.

### Why not one transaction per test, rolled back

`EntityEventInterceptor` publishes entity events on `SavedChanges` only when
`Database.CurrentTransaction` is null, and otherwise on `TransactionCommitted`. A test wrapped in a
transaction that is rolled back would therefore silently stop every notification the application
sends - which is a large part of what these tests assert.

## How a request finds its database

`TestDatabaseScope` keys sessions by a header, `X-Test-Session`:

- `ApiTestBase.InitializeAsync` registers the test's session and stamps the header on every client it
  hands out.
- `BlueprintAppFactory` replaces the `BlueprintContext` registration with one that resolves the
  session from the current `HttpContext`.
- Both failure branches throw with a full remediation sentence, so a request that arrives with no
  header or an unknown one fails loudly and names itself. That is why a header was chosen over an
  `AsyncLocal`.
- The session is released **before** the database is dropped, so a request that outlives its test
  fails naming the header rather than tripping over a missing database.

The host gets its own throwaway session, cloned from the already-migrated template. `Program.Main`
runs `.InitializeDatabase()` on the built host, outside `Startup`, so it must have somewhere to point
that is not the template - a connection held against the template breaks every later clone.

## What is real and what is not

| Real | Substituted or removed |
| --- | --- |
| `Startup`, the whole MVC pipeline, routing, model binding | The four sibling API clients - `Factory.Cite`, `.Gallery`, `.PlayerApi`, `.Steamfitter` |
| Authorization, the claims transformer, `UserClaimsService` | `IHostedService` - all four background workers are removed |
| PostgreSQL, EF Core, the migrations, both interceptors | `IHubContext<MainHub>`, replaced by `HubRecorder` |
| AutoMapper's 38 profiles, MediatR and the 24 event handlers | `IXApiService`, except in `XApiEnabledFactory` |
| The three singleton queues, so a request-path test can assert work was enqueued | Outbound HTTP, where a test installs `TestHttpHandler` |

- **`BlueprintAppFactory` is a class fixture**, not an assembly fixture: `IClassFixture<BlueprintAppFactory>`.
  Four substituted clients that tests both arrange and assert `Received()` against cannot be shared
  across classes, because NSubstitute keeps assertion state per thread. The cost is about a second of
  host startup per class.
- **The test authentication scheme is named `Bearer`**, which is `JwtBearerDefaults.AuthenticationScheme`.
  `MainHub` carries `[Authorize(AuthenticationSchemes = "Bearer")]`, so a scheme named anything else
  leaves every hub test unauthenticated. Because `AddJwtBearer` already claims that name,
  `BlueprintAppFactory` does `RemoveAll<IConfigureOptions<AuthenticationOptions>>()` first; adding a
  second scheme with the same name throws.
- `TestAuthHandler` reads `X-Test-User` for the `sub` claim, falls back to `Authorization: Bearer <user id>`
  so that `Startup`'s `?bearer=` promotion is observable, and returns `AuthenticateResult.NoResult()`
  when a request presents neither - which is what keeps 401 testable. It also mints an `iss` claim,
  without which `XApiService` cannot produce a statement at all.
- Configuration comes from the application's own `appsettings.json`; `WebApplicationFactory` sets the
  content root to `Blueprint.Api/`. The factory overrides only the connection string, the provider, and
  `ClaimsTransformation:EnableCaching=false` - `UserClaimsService` caches by user id in a host-wide
  singleton, which would leak claims between tests.
- The environment is `Development`, so a 500 carries the real message and stack. `JsonExceptionFilter`
  still answers controller exceptions either way, being an exception filter rather than middleware.

## The helpers

| File | What it is for |
| --- | --- |
| `TestActor.cs` | `Actor().WithSystemPermissions(...).OnMsel(msel, MselRole.Owner).SeedAsync()`. Blueprint's authorization is a function of seeded rows, so this seeds the `Msel`/`Team`/`TeamUser`/`Unit`/`UnitUser`/`UserMselRole` graph an actor needs. Mutually exclusive calls throw with an explanation. |
| `MselGraph.cs` | A whole exercise's worth of rows in one call, for tests whose subject is not the seeding. |
| `HubRecorder.cs` | Records broadcasts by group. Hand-written rather than a substitute: NSubstitute's assertion state is per thread and `TestServer` serves requests on the test pool. It is the only seam that can see what an event *handler* broadcast. |
| `HubHarness.cs` | Invokes `MainHub` directly. The only seam that can see a group *name*. |
| `TestHttpHandler.cs` | A rule-list `HttpMessageHandler` with `Answers`/`AnswersJson`/`AnswersOnce`/`Throws`/`Yields`/`Holds`/`HoldsUntil`, a recorded `Sent` list, and `AsFactory()`. The `Integration*Extensions` classes build their own clients from `IHttpClientFactory`, so the substituted clients do not reach them and this does. |
| `RecordingLogger.cs` | Where the only evidence is a log line - and blueprint has silent `catch { }` blocks in several places - this is how a test sees it. |
| `TestMapper.cs` | A static `IMapper` from the real profile scan, so unit tests exercise the 38 profiles too. |
| `Tokens.cs`, `Workbooks.cs`, `Frameworks.cs` | A real bearer token, an Open-XML workbook, a competency framework document. |
| `IntegrationServiceHarness.cs`, `QueueWorkerHarness.cs` | Construct a background worker with its six dependencies and drive it through its real queue. A fire-and-forget worker gives no completion signal, so these poll - `WaitFor(predicate)` with a named `TimeoutException`. |
| `CompositionFactory.cs` | Snapshots the service collection *before* the harness substitutes into it, so a test can ask what a deployment registers as against what the suite runs. The registration order is load-bearing. |
| `XApiEnabledFactory.cs` | The one `BlueprintAppFactory` subclass. The pattern for any setting `Startup` binds at construction. |
| `DatabaseHarnessTests.cs` | The harness's own tests, beside the harness. When it breaks, these fail instead of a hundred unrelated tests. |

# Things a new blueprint test has to respect

Each of these cost a debugging session at least once.

- **The factory is a class fixture, so substitutes persist across a class's tests.** Reset what you
  arrange in `InitializeAsync` - `Factory.Cite.ClearSubstitute()` and friends.
- **A setting `Startup` reads while building needs a factory subclass, not an arrangement.** Subclass
  rather than add a flag: a flag gives a fixture whose behaviour depends on which test ran first.
  `ConfigureTestServices` callbacks run in registration order, so register before
  `base.ConfigureWebHost` to snapshot and after it to override.
- **The test auth scheme is named `Bearer` because `MainHub` demands it.** Do not rename it.
- **`CurrentHttpContext` is a process-wide static**, set by `app.UseHttpContext()` and won by whichever
  host started last. Keep everything that reads `CurrentHttpContext.Current`, `.AppBaseUrl` or
  `.Authorization` in one class so it cannot race.
- **`CompetencyFrameworkImportProgressService` is a singleton by design**, shared by every test in a
  host. Key on an id nobody else uses. `HubCache` is the same shape.
- **EF's change-tracker fix-up is the central hazard.** `Seed` writes through `Db` and leaves the rows
  tracked, so a *filtered* re-read through the same context sees them fixed into navigations whatever
  the filter says - and an un-`Include`d collection can be non-empty. Where a test depends on a row
  **not** being tracked, seed through a throwaway `NewContext()`; `EventHandlerTests.SeedCold` is the
  example. Predict from what the request already touched, not from what the query says.
- **`ViewModels.Base` makes `DateCreated` and `CreatedBy` non-nullable.** A test body record declaring
  them `DateTime?`/`Guid?` sends nulls, and the request is a 400 that never reaches the controller.
- **A body that must distinguish "absent" from "null" cannot be a typed field or an optional
  parameter.** Shape it as a `record` and vary it with `with`, or pass `object x = null` and let the
  serializer omit it. Three separate tests were asserting nothing before this was understood.
- **Never compare a `DateTime` against the seeded object's own field.** Postgres stores microseconds
  where `DateTime` holds ticks. Read the value back through a fresh context, or use
  `AssertStampedBetween`.
- **Audit fields are server-stamped.** `BlueprintContext.SaveEntries` sets `DateCreated` on insert and
  restores `CreatedBy`/`DateCreated` from `OriginalValues` on update, so they are immune to
  request-body spoofing - and the seam for any test about them is the context, not the service.
- **`Location` headers are lowercased and absolute.** `RouteOptions.LowercaseUrls = true`, so assert
  `EndsWith("/api/datavalues/{id}")` rather than the route template's casing.
- **Every `int` crosses the wire as a JSON string**, and `Ok(null)` is a **204** rather than an empty
  200.
- **Await a `MultipartFormDataContent` inside its `using`.** `TestServer` reads the body inside
  `SendAsync`, so returning the task unawaited disposes the content first and the test fails with
  `ObjectDisposedException` instead of its own assertion.
- **A test-side query with two collection `Include`s throws.** The context is configured to throw on
  `MultipleCollectionIncludeWarning`, matching production. Add `.AsSplitQuery()`.
- **A concurrency assertion must be a set, not a sequence**, and `Arg.Any<CancellationToken>()` cannot
  tell a forwarded token from a dropped one - `CancellationToken.None` satisfies it. Use
  `Arg.Is<CancellationToken>(x => x.CanBeCanceled)`.
- **Two names collide inside the test assembly.** `EventType` and `Exception` are declared by the
  generated Player and Gallery clients; spell out `Data.Enumerations.EventType` and alias
  `System.Exception`.

# Adding a test

- **One file per service or area**, flat at the project root; the harness goes in `Infrastructure/`.
  Group by the question being asked rather than one class per production class where several classes
  answer the same question in the same way.
- **Name the method as a sentence**: `Create_WithoutAName_Is400`, `Get_ForAnIdThatIsNotThere_Is404`,
  `Update_DecidesFromTheRequestBodyAndStealsTheRow`. The name is the specification; a reader should not
  need the body.
- **Pass `Ct` to everything awaited.** It is `TestContext.Current.CancellationToken`, and `xUnit1051`
  enforces it.
- **No `// Arrange` / `// Act` / `// Assert` comments.** A test whose three phases are not obvious from
  its shape is too long. Use `// ---` banners to separate route sections in a long file instead.
- **Say why, not what, in a comment.** `<remarks>` carries the reason a test asserts something
  surprising; the assertion itself carries the what.
- **401 and 403 go in `RouteAuthorizationTests.cs`**, one row per route in a
  `TheoryData<method, route, permission, body>` table driving three theories: refused anonymously,
  refused holding nothing, allowed holding the named permission. The third keeps the second honest - a
  guard that refuses everybody passes the first two. Add a row per route as new routes appear; do not
  write per-service 401/403 tests.
- **Per endpoint otherwise, two hand-written cases**: the happy path at the *minimum* permission, and
  not-found for every route taking an id. Anything beyond that is for the services that hold exercise
  data.
- **Break the production code and watch the test fail.** This is the only check that a test tests
  anything:

  ```sh
  # edit the guard, the filter, the comparison the test is about
  dotnet test --filter "FullyQualifiedName~Tests.MoveEndpointTests"   # expect red, and expect the names you predicted
  git checkout -- Blueprint.Api/
  dotnet build                                                        # not optional - see below
  ```

  **Rebuild after restoring.** `git checkout` restores the source, not the assembly, and a following
  `--no-build` run executes the mutated DLL still sitting in the test output. That has produced a
  16-failure run against a clean `git diff` once already.

  A few things that look like mutations and are not: `if (false)` (`CS0162`), a self-assignment
  (`CS1717`), and neutering a loop's increment, which is an infinite loop rather than a failing test.
  If an edit hangs the run, check `git diff --stat HEAD -- Blueprint.Api/` before doing anything else -
  a killed run never reached its `git checkout`.

- **Capture failing test names.** `tail -2` on a test run reports a count and loses the names, which
  makes a stale binary indistinguishable from a regression. Keep the `Failed` lines.

# Characterize, don't fix

**A test asserts what the code does today, not what it should do.** When a test turns up a defect:

1. Write the test so it passes against the current behaviour.
2. Say in a `<remarks>` that the behaviour is wrong, why, and - where it is short - what the fix is.
   Start the sentence `BUG:`.
3. Add a line to `docs/known-defects.md` naming the test.
4. Leave the production code alone.

The test then turns red when somebody fixes the defect, and that red is the signal to delete the test
or invert it and to delete the row. A defect nobody has written down is found again by the next person;
a defect fixed in the same commit as the test that found it is a change nobody reviewed.

A worked example, from `CiteActionEndpointTests`:

```csharp
/// <remarks>
/// BUG: <c>GetAsync</c> is <c>SingleAsync</c>, so an unknown id is a 500 rather than a 404, and the
/// null check below it is dead code whose <c>EntityNotFoundException</c> names <c>DataValueEntity</c>
/// - the <b>fifth</b> copy of that line, after <c>OrganizationService</c>, <c>MoveService</c>,
/// <c>CardService</c> and <c>InjectService</c>. Worth one sweep rather than five fixes.
/// </remarks>
[Fact]
public async Task Get_ForAnIdThatIsNotThere_Is500()
{
    var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

    var response = await Client(actor).GetAsync($"api/citeActions/{Guid.NewGuid()}", Ct);

    Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
}
```

The test is three lines and asserts the wrong answer deliberately. What makes it worth having is the
remark: it names the line, counts the copies, and says the fix is a sweep rather than five edits.

## What it found

- **`docs/known-defects.md`** is the catalogue, by service. Each row names the test that pins it.
- **`docs/fix-list.md`** is the same material ranked, for a follow-up branch. Nothing on this branch
  is a production fix, so that list is the deliverable.
- Units covered before `known-defects.md` existed recorded their findings in their commit messages;
  `git log` on the test branch is the fuller account, and `docs/fix-list.md` consolidates both.

Two things the programme is confident about and that shape the list:

- **The same defect recurs.** Four shapes account for more than thirty rows between them, and each is
  one sweep rather than a fix per service. They are at the top of `docs/fix-list.md`.
- **A working example is worth recording too.** Where two services spell the same call site and one is
  right, the working one gets a test of its own and is named as the model to copy. Two same-typed ids
  either side of a call site is a defect no compiler and no type test can find, so the positive
  controls are the cheapest protection there is.

# Continuous integration

`.github/workflows/test.yml` runs on every push and every pull request, with no branch filter, and is
the first workflow in this repository that ever compiled the solution or ran a test.

- Concurrency is keyed on the branch name, taking `github.event.pull_request.head.ref` first and
  falling back to `github.ref_name`, because no single ref context identifies a branch across both
  triggers. Either alone runs the suite twice at once for a same-repo pull request.
- The SDK comes from `global.json`; never pin a second copy in the workflow.
- NuGet is cached on `Directory.Packages.props` plus every `*.csproj`.
- `dotnet restore` and `dotnet build` cover the **whole solution**, so `Blueprint.Api.Client` compiles
  under `TreatWarningsAsErrors` on a push. Nothing else builds it outside the manual client release.
- The `.trx` is uploaded on failure too, gated on the test step's `outcome` rather than `always()` -
  `always()` also fires when the step never ran, which turns a restore failure into a confusing
  missing-artifact error.
- `ubuntu-latest` has a Docker daemon, so the container starts with no extra service definition.

# Coverage

Opt-in, and never a gate:

```sh
scripts/coverage.sh                              # the whole suite, HTML report plus a ranked list
scripts/coverage.sh --filter MselService         # further arguments go to dotnet test
TOP=50 scripts/coverage.sh                       # a longer ranking; the default is 25
```

`.github/workflows/coverage.yml` is the same thing on `workflow_dispatch`, appending the summary to the
run's own page.

**There is deliberately no threshold.** A percentage attached to a merge button changes what people
write: the figure moves fastest when a test drags an untested file through without asserting anything
about it, which is the opposite of this repository's convention. The number the script leads with is
not a percentage either - it is *untested lines per class, most first*, because 17 uncovered lines in a
94% `Startup` matter less than the 200 in a class at 40%.

What `coverlet.runsettings` excludes, and why:

- `Cite.Api.Client` and `Gallery.Api.Client` - about 40,000 lines of checked-in NSwag output, excluded
  by namespace and by file. Nothing in them was written here.
- `Blueprint.Api.Migrations.PostgreSQL` - generated, enormous, and executed once by the fixture
  migrating the template, so it would report as well covered while proving nothing.
- Auto-implemented property accessors (`SkipAutoProps`), because 49 view models and 43 entities of
  auto-props are what make a raw number move whenever a model grows a field.
- The test assembly itself, whose coverage would be near total by construction.

Included: `Blueprint.Api` **and** `Blueprint.Api.Data`. The data assembly holds the entities, the
context and the audit-field logic, which is the half of the risk the endpoint tests exercise hardest.

## What is not covered yet

The programme covered every controller and service, the hub, the event handlers, the interceptor, the
filters, the converters and the startup composition. What it did not reach:

- **`XApiService`'s outbound half.** `GetStatementsAsync` builds its own `HttpClient`, so it cannot be
  stubbed; the tests assert the queued row and the four routes instead. Fixing that defect is what
  makes the rest testable.
- **The Open-XML export beyond a round trip.** The xlsx import/export is covered as a round trip and by
  its parsers, not cell-by-cell against a reference workbook.
- **Anything that needs two hosts at once.** `CurrentHttpContext` is a process-wide static, so the
  shadowed `IHttpContextAccessor` is characterized rather than exercised concurrently.
- **Scale-out behaviour.** `HubCache` is per host by construction; a test can pin that it is one
  instance per application but not what two replicas do.
- **The provider `switch` in `Startup`.** A host built with a misspelled `Database:Provider` behaves
  like a correct one under the harness, because the harness replaces the context registration. The
  defect is recorded in `CompositionTests`' remarks rather than tested.
- **The three `Program.Main` paths.** `HostFactoryResolver` never matches `Program.CreateWebHostBuilder`,
  so `InitializeDatabase` is exercised directly by `DatabaseInitializationTests` rather than through a
  started host.
