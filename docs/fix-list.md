The defects the test programme on `task/api-tests` characterized, ranked for a follow-up branch.
Nothing here is fixed on that branch, by design - see the characterize-don't-fix rule in
`docs/Testing.md`.

# How to read this

- `docs/known-defects.md` is the same material by service, and names the test that pins each row. This
  file is the order to fix them in. Where a row here has no test, it was found in a unit written before
  `known-defects.md` existed and lives in that commit's message; `git log task/api-tests` is the fuller
  account.
- **Start with the sweeps.** Nine shapes account for more than a third of the catalogue. Each is one
  change applied in several places, and each has at least one place in the codebase that already does
  it right - the model to copy is named.
- Then the tiers, in order: P1 is security and data loss, P2 is exercise correctness, P3 is the
  contract clients are generated from, P4 is hygiene.
- **Every fix turns a test red.** That is the signal it worked. Delete the test or invert it, and delete
  the row from `known-defects.md`.
- Sizing: a sweep is a day each at most, P1 is about a fortnight, P2 is the bulk of the work and wants
  splitting by feature, P3 and P4 are opportunistic.

# The sweeps

Do these first. They are mechanical, they are the majority of the catalogue by row count, and several
P1 and P2 entries below stop existing once they are done.

## S1 - `UpdateAsync` decides from the request body's parent id

**Twelve instances.** The method reads the parent id out of the *request body*, puts that to a
requirement helper, and then lets the profile map the same id onto the stored row. So a caller with a
role on any one MSEL may edit, and move to themselves, every equivalent row of every other MSEL - and
the MSEL the row left is told nothing, neither a `DateModified` nor a broadcast.

- `OrganizationService`, `ScenarioEventService`, `DataOptionService`, `MoveService`, `CardService`,
  `TeamService`, `MselUnitService`, `InvitationService`, `CiteActionService`, `CiteDutyService`,
  `MselPageService`, `PlayerApplicationService`.
- **The fix, in each:** read the stored row first, check existence, decide from what is stored, and
  `Ignore` the parent id in the profile. `MoveService.DeleteAsync` eleven lines below its `UpdateAsync`
  is the model; `TeamUserService` is a whole service written this way.
- `TeamService` already has the guard against the steal (`TeamService.cs:173`) and runs it *after* the
  permission check, so it answers 500 where it means 403. Order matters as much as the guard.
- Fixing this also fixes "an unknown id is a 403 for a stranger and a 404 for a permission holder",
  which appears throughout the catalogue as a separate row.

## S2 - `GetAsync` is `SingleAsync` plus a dead null check

**Six copies.** An unknown id is an `InvalidOperationException` and therefore a 500; the null check
below it can never run, and its `EntityNotFoundException` names `DataValueEntity` whatever service it
is in.

- `OrganizationService`, `MoveService`, `CardService`, `InjectService`, `CiteActionService`,
  `CiteDutyService`.
- **The fix:** `SingleOrDefaultAsync`, then the existing null check, with the entity type corrected.
  `SystemRoleService`'s two single-row methods are the model.
- Do **not** answer `Ok(null)`: `HttpNoContentOutputFormatter` turns that into a **204**, which is what
  `CatalogController` does today and what no generated client has a case for.

## S3 - a write path that never marks its MSEL modified

**Eleven services.** `ServiceUtilities.SetMselModifiedAsync` is what tells blueprint.ui an exercise
changed. These services never call it, so editing an exercise through them leaves its `DateModified`
untouched and any client polling it sees nothing.

- `CardService`, `CardTeamService`, `TeamService`, `TeamUserService`, `UnitService`, `UnitUserService`,
  `MselUnitService`, `InvitationService`, `CiteActionService`, `CiteDutyService`, `InjectService` (its
  create calls `SetCatalogModifiedAsync`, its update and delete call nothing).
- **The fix:** call it inside the existing transaction. `UserMselRoleService` does this on both writes
  and is the model.
- Note the date argument is dead code - `BlueprintContext.SaveEntries` overwrites it. Delete the
  parameter while you are here.

## S4 - a duplicate row is a 500 where a 409 is owed

A unique index refuses the insert and `DbUpdateException` reaches `JsonExceptionFilter` as a 500, so a
client cannot tell "already there" from "the server is broken". Where the service does check, it throws
`ArgumentException`, which is not an `IApiException`, and the answer is still a 500.

- `GroupService` (name), `CardTeamService` (`(CardId, TeamId)`), `CatalogUnitService`
  (`(CatalogId, UnitId)`), `UserMselRoleService` (`(MselId, UserId, Role)`), `SystemRoleService`
  (name), `InjectTypeService` (name), `MoveService` (`(MselId, MoveNumber)`), `TeamCompetencyService`,
  `ProficiencyScaleService`.
- **The fix:** a `ConflictException : IApiException` mapping to 409, thrown after an existence check.
  There is no 409 anywhere in the API today except `CompetencyFrameworkService`'s duplicate ID number,
  which is the model.

## S5 - a primary key compared against a user id

Four instances of the same typo, none of them a compile error because both sides are `Guid`.

- `InjectService` - `CatalogUnits.Where(m => m.Id == userId)`, so `GET injects/{id}` is 403 for the
  catalog's creator, its unit members and strangers alike, **and 200 for a caller whose user id happens
  to equal some join row's primary key**, who then reads every inject in the installation.
- `CatalogInjectService.cs:70` - the join row's own id put to `CatalogViewRequirement` as a catalog id,
  so a unit member may list a catalog's injects and not read one of them.
- `UnitService.cs:104-108` and `:126-129` - the route's *unit* id tested against `_user.GetId()` under
  the messages "You cannot change your own Id" and "You cannot delete your own account".
- **The fix:** pass the right id. `CatalogInjectService.GetByCatalogAsync`, eleven lines from the first
  defect, is the model. `UserService` has no such guard at all, so the unit copies are not a mis-paste
  from a working original - delete them and decide separately whether a self-delete guard is wanted.

## S6 - transposed ids at a call site

Two instances, both `(teamId, cardId)` passed to `(cardId, teamId)`.

- `CardTeamController.cs:192` - `DELETE teams/{teamId}/cards/{cardId}` matches no row and is a 404 for
  every well-formed request; it answers 204 only when the caller passes the ids the wrong way round.
- `PlayerApplicationTeamController.cs:192` - the same line.
- **The fix:** swap the arguments. `TeamUserController.cs:152` and the equivalent in
  `UnitUserController` are the positive controls, and both have tests of their own for exactly this
  reason.
- **Worth sweeping the estate**, not just this repository: two same-typed ids either side of a call
  site is a defect no compiler and no type test can find. Naming the arguments at the call site is the
  cheap prophylactic.

## S7 - an unguarded `FindAsync` in a template fall-through

A list route checks whether the MSEL is a template by `FindAsync`-ing it and reading a property, with
no null check and no `CancellationToken`. The dereference sits to the right of `!hasSystemPermission`,
so **whether a MSEL exists depends on who asks**: 500 for an ordinary caller, an empty list for a
permission holder.

- `CiteActionService.cs:67-68`, `CiteDutyService`, `TeamService.GetByMselAsync`,
  `UserTeamRoleService.GetAsync`, `MselPageService`, `MselCompetencyService`, and
  `UserMselRoleService.GetAsync` (where the same shape makes an unknown id a 404 for one class of
  caller and a 500 for another).
- **The fix:** null-check and pass the token. `UserMselRoleService.DeleteAsync` checks existence before
  the permission check and is a clean 404 for everybody; it is the model.

## S8 - `EntityNotFoundException<WrongType>`

A 404 that names the wrong entity, so a client cannot tell which thing is missing.

- `InvitationService` and both `MselUnitService` single-row reads report `MselEntity`;
  `CatalogUnitService.cs:68` reports `CatalogEntity`; every service in S2 reports `DataValueEntity`;
  `ProficiencyLevelService` and `ProficiencyScaleService` name the **view model** rather than the
  entity, so the message reads `ProficiencyLevel Entity not found`.
- **The fix:** name the entity the route is about. Mechanical, and worth doing in the same commit as S2.

## S9 - a handler broadcasting to a group nobody is in

`GetGroups` builds a group name from the row's `MselId`. When that is null it is the empty string, and
when it is an unset `Guid` it is all zeros, so the notification reaches nobody and no client learns the
row changed.

- Five handler families broadcast to the empty-string group (`OrganizationHandler`,
  `DataFieldHandler`, `CardHandler`, `CardTeamHandler`, `MselPageHandler`) and two to the all-zeros
  group.
- **The fix:** address `ADMIN_DATA_GROUP` for a row with no MSEL, which is where template rows belong
  anyway. `UserHandler` is a partial model, though it never addresses `MainHub.USER_GROUP`, which
  exists for exactly its case.
- Three handlers are missing entirely - there is no `DataOptionHandler`, no `MselUnitHandler` and no
  `UserTeamRoleHandler` - so renaming a drop-down entry, assigning a unit to an exercise and granting
  somebody a role on one are all invisible to connected clients.

# P1 - security and data loss

| # | Fix | Where |
| --- | --- | --- |
| P1-1 | **Authorize the four xAPI routes.** None requires a permission and none puts any MSEL id - route, query string or body - to `MselViewRequirement`, so any authenticated caller may assert any competency about any MSEL and read every statement the LRS holds for one, including exercises they cannot otherwise see. | `XApiController` |
| P1-2 | **`GET users` hands the whole user directory to anybody on any team.** The permission and a single `TeamUser` row are alternatives, so `ViewUsers` gates nothing a team membership does not already open - and the "just yourself" branch applies only to a caller on no team, which includes a MSEL's own owner. | `UserService.cs:56-70` |
| P1-3 | **`GET units` has no authorization of any kind**, though its own remarks say "Only accessible to a SuperUser". | `UnitController.cs:42-49` |
| P1-4 | **`GET cards/templates` has no authorization of any kind.** | `CardController` |
| P1-5 | **Three list routes have no filter at all**, so one permission holder reads every row in the installation: `GET teamcards` (`ViewMsels`), `GET unitusers` (`ViewUnits`), and `GET injectTypes/{id}/injects`, which is scoped by the type alone and so lists private catalogs' injects. | `CardTeamService`, `UnitUserService`, `InjectService` |
| P1-6 | **`GET teams/{id}` is not MSEL-scoped.** The controller requires `ViewMsels` outright and the service checks nothing, so one `ViewMsels` holder reads every team in the installation with its member list - while the MSEL's own owner is answered 403 by the `Location` header their create just handed them. | `TeamService`, `TeamController` |
| P1-7 | **`MainHub.Greet` and `GetPresence` check nothing at all**, so any signed-in caller reads and joins any exercise's presence list. | `MainHub` |
| P1-8 | **`SystemRoleEntity.Immutable` is read nowhere**, so a `ManageRoles` holder may rename the Administrator role, clear its `AllPermissions` flag or delete it. The caller doing it is normally the holder of that very role, so the request succeeds and their next one is a 403 - a lock-out with no undo. | `SystemRoleService` |
| P1-9 | **A `ManageUsers` holder may give themselves every permission in the installation** by naming `SystemRoleDefaults.AdministratorRoleId` in a PUT to their own row. | `UserService.UpdateAsync` |
| P1-10 | **Nothing invalidates an escalated or deleted user's claims.** `UserService` injects `IUserClaimsService` at `:41`, assigns it at `:49` and never reads it. | `UserService` |
| P1-11 | **A revoked role survives a re-login.** The jti cache invalidation is guarded by `UseGroupsFromIdP \|\| UseRolesFromIdP`, both false in the shipped configuration, so a claims cache entry outlives the change until it expires. This is why the test harness disables claims caching. | `UserClaimsService` |
| P1-12 | **A string claim at the head of a configured claim path grants all 28 permissions.** With `RolesClaimPath = "realm_access.roles"`, a plain string claim named `realm_access` whose value is `"Administrator"` is accepted and the rest of the path ignored. | `UserClaimsService` |
| P1-13 | **The LMT route is `[AllowAnonymous]` and gated on nothing** - not on the MSEL being a template, not on it being published - so a live exercise's name, description and competency list go to anybody who guesses a Guid. | `LmtController` |
| P1-14 | **`ValidateDiscoveryDocument` is absent from `appsettings.json`**, so blueprint accepts a discovery document from any realm and posts the service account's credentials to whatever `https` `token_endpoint` it names. | `ApiClientsExtensions`, `appsettings.json` |
| P1-15 | **`DELETE injectTypes/{id}` silently destroys every catalog built on the type**, every inject of it, every data value and every join row - 204, one broadcast, no dependent count and no 409. | `InjectTypeService.DeleteAsync` |
| P1-16 | **Editing a scenario event deletes its Steamfitter task.** `UpdateAsync` maps a body that does not mention the task onto a row loaded with it included, orphaning it into a cascade delete. A copy loses it twice over: the destination task is a discarded view model and the guard reads a denormalized column nothing keeps current. | `ScenarioEventService` |
| P1-17 | **A PUT may move a data value into another MSEL's cell**, only the stored field's MSEL being checked before the mapper writes the body's ids - and the `data_values` unique index forbids nothing, because the always-null `inject_id` sits inside it. | `DataValueService`, `Data/Models/DataValue.cs` |
| P1-18 | **`GET cards/templates` and `POST cards/json/download` export a live MSEL's cards** to anybody holding `ManageGalleryCards`; the download filters on the requested ids and nothing else. | `CardService` |
| P1-19 | **An imported card template keeps its `GalleryId`**, so it already points at a card in whatever Gallery the file came from - which is how the all-zeros `CardId` defect in `IntegrationGalleryExtensions` is reached with no Gallery failure at all. | `CardService.UploadJsonAsync` |
| P1-20 | **`DELETE users/{id}` has no self-delete guard**, so a `ManageUsers` holder may delete their own account. (The guard that exists is in `UnitService`, applied to unit ids - see S5.) | `UserService.DeleteAsync` |
| P1-21 | **`DatabaseExtensions.InitializeDatabase` wraps its whole body in one logging `catch`**, so an application whose migration or seed failed starts and serves requests, and the log line does not say what was misconfigured. | `DatabaseExtensions` |

# P2 - exercise correctness

The push, the pull and the timeline. These are what make an exercise wrong rather than unavailable, so
they are the ones a participant sees.

## The push to sibling applications

| # | Fix | Where |
| --- | --- | --- |
| P2-1 | **A MSEL with scenario events and no moves is an `IndexOutOfRangeException`.** Line 701 computes a correctly-guarded `moveNumber` local and line 703 indexes `moves[m]` directly, so `moves[0]` on an empty array - and this is reached from `CreateArticlesAsync`, so the Gallery half of a push fails naming nothing. The dead local is the fix, two lines above. Reachable by an ordinary DELETE of a MSEL's last move. | `ScenarioEventService.GetMovesAndInjects` |
| P2-2 | **Its private twin stores the move's *index* where it stores the `MoveNumber`**, so one MSEL yields `MoveNumber` to Gallery and a zero-based index to Steamfitter and CITE: a MSEL numbered 1, 2, 3 has its task names and its move-change URLs built from 0, 1, 2. | `IntegrationService.GetMovesAndGroups` |
| P2-3 | **Move names are padded `"00" + n` then `Substring(length - 2)`**, so move 100 is named what move 0 is named - and the name is the only thing ordering tasks in Steamfitter's UI. | `IntegrationSteamfitterExtensions` |
| P2-4 | **A Steamfitter notification body is built by string concatenation**, so a quotation mark in the text produces malformed JSON that fails during the exercise rather than at push time. | `IntegrationSteamfitterExtensions` |
| P2-5 | **`CreateScenarioTasksAsync` writes resolved URLs back onto the tracked entity it was given**, so they reach the database the moment anything saves that context after the push loop. | `IntegrationSteamfitterExtensions` |
| P2-6 | **`GetArticleValue` uses `SingleOrDefault` twice**, so two fields mapped to one Gallery parameter, or two values for one field, abort the push on its first article - after the collection, exhibit, teams and cards have already been created. | `IntegrationGalleryExtensions` |
| P2-7 | **Every parse in the article builder fails quietly to a default**, so a missing `DatePosted` publishes the article dated year one and a misspelled `Status` publishes it unused; and an article whose `ToOrg` is empty is created and shown to no team at all. | `IntegrationGalleryExtensions` |
| P2-8 | **`CardId = (Guid)card.GalleryId` is not the guard it looks like.** Gallery's `Card.Id` is a non-nullable `Guid`, so a response without an id gives all zeros and every team-card for that card points at nothing. | `IntegrationGalleryExtensions` |
| P2-9 | **`CreateEvaluationAsync` reads `newEvaluation.Moves.Single()`** to delete the default move CITE creates, so an answer with no moves or two throws after the evaluation exists, with nothing to undo it and the MSEL keeping the id - a retry then creates a second evaluation. | `IntegrationCiteExtensions` |
| P2-10 | **`MoveEntity.SituationTime` is `DateTime?` and is cast unguarded to `DateTimeOffset` in two methods**, so one move with an empty situation time aborts a push already part-way through. | `IntegrationCiteExtensions` |
| P2-11 | **A team with no `CiteTeamTypeId` is skipped silently**, with its users, its duties and its actions - so a MSEL whose teams all lack one pushes an empty evaluation and reports success. | `IntegrationCiteExtensions` |
| P2-12 | **`CreateApplicationsAsync` ignores its `batchSize`**, because `Select(async ...)` starts every task as the list is materialized - so `PlayerMaxConcurrentRequests` is decoration and a large MSEL opens one concurrent request per application. **Five of the six call sites in the layer are correct**; this is the only wrong one, so copy a sibling rather than redesigning. | `IntegrationPlayerExtensions` |
| P2-13 | **A push that fails leaves every integration id written**, with nothing to trigger the cleanup that could use them; a cancel that fails leaves the MSEL saying `Pulling` while still pointing at four things in four other applications, the token fetch that is likeliest to fail sitting outside the four per-pull `try` blocks. | `IntegrationService` |
| P2-14 | **The error a user sees names the call site, not the step that failed**, each process method tracking a detailed `currentProcessStep` in a local that the outer handler overwrites with its own coarser value - and on the pull path that value has a DI scope stringified into it (`"Getting Auth Token with scope " + scope`), so the user-visible text contains `...ServiceProviderEngineScope`. Deleting `+ scope` is a one-word fix. | `IntegrationService` |
| P2-15 | **Queueing work for a MSEL that no longer exists produces three failures and one misleading message**: `ProcessTheMsel` never null-checks its `SingleOrDefaultAsync`, the inner handler dereferences the same null, and the outer handler's `ExecuteUpdateAsync` matches no row. | `IntegrationService` |
| P2-16 | **A MSEL that uses nothing "deploys"** - no ids, nothing contacted, status `Deployed`. `CanMselBePushed` looks like it was meant to prevent this; it returns `true` behind a `// TODO: build this out!!!` and is called from nowhere. | `IntegrationService` |
| P2-17 | **Player is deleted twice** in the cancel path (`:175-176`) with no comment, where the copy in `PullIntegrations` at least asks `// TODO: Player requires two deletes?`. | `IntegrationService` |
| P2-18 | **`CancelPush` returns before doing anything**, so the endpoint answers 200 with four DELETEs still to come and no way for the caller to learn the outcome - and cancelling a MSEL that does not exist broadcasts that one is being cancelled anyway. | `IntegrationService` |

## The queue workers

| # | Fix | Where |
| --- | --- | --- |
| P2-19 | **All three joins share one `try`**, so the first failure ends the join and a Gallery outage leaves the user on Player's team, off the other two, with no retry, no record and no cleanup. `IntegrationService.PullIntegrations`' per-application `try` is the shape to copy. | `JoinService` |
| P2-20 | **Both workers hold an `IHubContext<MainHub>` they never send through**, so a user who accepted an invitation cannot learn whether they joined, and blueprint's UI shows an application as pushed whether Player took it or not. | `JoinService`, `AddApplicationService` |
| P2-21 | **`AddApplicationService` blames the one step that cannot fail**: `currentProcessStep` is set to `"Player - get API client"` before a constructor that cannot throw and is never set again, and its log line is the bare constant `"Adding Application"` - so a failed add names neither the step, the application, the team nor the view. | `AddApplicationService` |
| P2-22 | **Both `ProcessThe*` methods cast their queue item outside the `try`, in an `async void` on a foreground thread**, so a null item takes the host down rather than being logged. (Deliberately untested - a test for it would kill the run.) | `JoinService`, `AddApplicationService` |
| P2-23 | **Nothing caches a token**, so every queue item pays three round trips (discovery, JWKS, token) and thirty acceptances cost ninety requests to Keycloak, while the configured `TokenExpirationBufferSeconds: 900` is read nowhere. A join with all three flags false costs three requests and does nothing. | `ApiClientsExtensions` |
| P2-24 | **`RequestTokenAsync` never checks `IsError`**, so a wrong service-account password becomes `FormatException: The format of value ' ' is invalid.` raised inside whichever integration step ran next and logged as *that step* failing. Both failure paths throw a bare `System.Exception`. | `ApiClientsExtensions` |

## The timeline

| # | Fix | Where |
| --- | --- | --- |
| P2-25 | **Move the reorder after the save.** The same helper is called before `SaveChanges` in `CreateAsync` and after it in `CreateScenarioEventsFromInjectsAsync`, so its append heuristic reads a server-stamped date in one caller and an unstamped request-body date in the other - and a create whose body omits `dateCreated` is inserted at the **head** of its group rather than appended. | `ScenarioEventService.ReorderScenarioEvents` |
| P2-26 | **A position nobody holds is a rejected position, not a free one.** A create or move naming a position past the end of the group is silently given position **0**, colliding with the row already there - and there is no unique index on `(MselId, DeltaSeconds, GroupOrder)` to stop it, so the two rows' relative order is then whatever the database returns. | `ScenarioEventService.ReorderScenarioEvents` |
| P2-27 | **No delete renumbers**, so the timeline accumulates gaps no later request can fill: delete a row, create one at the visibly-middle position, and it lands at the end. The same is true of move numbers after a move is deleted. | `ScenarioEventService`, `MoveService` |
| P2-28 | **Any PUT rewrites every cell's `CellMetadata` from the row's `RowMetadata`**, so the per-cell formatting `PUT dataValues/{id}` exists to set cannot survive an edit - and the translation formats each colour component `"X"` without padding, so red is `FF00`. | `ScenarioEventService` |
| P2-29 | **`AddDataFields` is ignored**, so the flag the UI sends to protect an MSEL's schema does nothing: both bulk routes reconcile data fields by name and type and create what is missing, editing the MSEL's column layout as a side effect without asking permission to. | `ScenarioEventService` |
| P2-30 | **The checkbox cascade requires `Evaluator` *and* `Approver`-or-`Editor`**, the inverse of the comment above it, and `"1"` is recorded to the LRS as unchecked. A data field with no MSEL makes create and delete a 500 through `MselOwnerRequirement`, which makes every value on an inject undeletable. `ManageDataFields` grants nothing on any mutating route. | `DataValueService` |
| P2-31 | **A moved or deleted move takes its scenario events' grouping with it silently** - deleting a middle move regroups its events into the preceding one, and the route answers 204. | `MoveService.DeleteAsync` |
| P2-32 | **`UpdateAsync`'s data-value loop is dead code whose only reachable effect is a 500.** The map, `Update` and save above it already wrote the body's values, so the loop's `else if` cannot fire and its `if` branch dereferences null - which means **adding a data field to an inject type makes every later update of an existing inject a 500**, as does any body that does not echo every cell back. | `InjectService.UpdateAsync` |
| P2-33 | **A PUT lets the client write a data value's `CreatedBy` and `DateCreated`** - the exception to the server-stamping rule, because `Update` attaches the nested collection as Modified and `SaveEntries` restores from the client's `OriginalValues`. The same map lets a PUT point a value at another type's field and retype the inject itself. | `InjectService.UpdateAsync` |

## Roles, teams and units

| # | Fix | Where |
| --- | --- | --- |
| P2-34 | **`MselRole.Evaluator` is missing from `MselViewRequirement`'s role list**, so the person running a live exercise cannot read the timeline they are evaluating although they can tick its checkboxes - and an evaluator assigned through a unit satisfies `EvaluatorRequirement` while being unable to view the MSEL. | `MselViewRequirement` |
| P2-35 | **Three of the eight requirement helpers ignore the creator**, so the person who created an MSEL cannot edit its moves until somebody assigns them a unit and a role; callers paper over it with `\|\|` against a `SystemPermission`. Reconcile the eight - they disagree in six ways, tabulated in the Phase 2 notes. | `Msel*Requirement` |
| P2-36 | **A `UserMselRoleEntity` without a `UnitUserEntity` is a no-op** in every helper: the role query is never reached unless the unit query already found the user. This is the mistake an administrator makes adding somebody to a MSEL by hand, and it fails silently as a 403. It is reachable through three different APIs. | `Msel*Requirement` |
| P2-37 | **Assigning a unit to a MSEL grants `Viewer` to the members it has at that moment and nothing to anybody who joins later**, so the two requests are order-dependent and nothing says so. Removing the assignment drops the membership half and leaves the role rows, so a MSEL accumulates roles naming people with no path to it - and re-assigning the unit skips them as already having a role. | `MselUnitService` |
| P2-38 | **`SetIntegrationRolesAsync` writes a role that is not a `MselRole`.** `MselRole` starts at `Owner = 10`, so the back-filled row's unset `Role` stores `(MselRole)0` - a number no name maps to, refused by every helper's list, crossing the wire as a bare `0` where every other enum is a name. It back-fills only for the MSEL's own `CreatedBy`, and a partial body silently clears the integration roles it does not mention. | `UserService` |
| P2-39 | **`POST users` without an id in the body creates the user and answers 500.** `CreateAsync` returns `GetAsync(user.Id, ...)` - the body's id, where the column has a database default - so the read finds nothing and `CreatedAtAction` dereferences null. The row is already saved and already broadcast as created. `UserMselRoleService` and `SystemRoleService` return the entity's id and are the model. | `UserService.CreateAsync` |
| P2-40 | **`UnitUserService.CreateAsync` validates nothing under a comment saying it does** - two `FindAsync` locals that are never read and take no token - so an unknown user or unit is a 500 from a foreign key. `MselUnitService.CreateAsync` checks both parents and answers two clean 404s. | `UnitUserService` |
| P2-41 | **Nothing checks that a user is on a team before granting them a role there**, and `(TeamId, UserId, Role)` is indexed rather than `(TeamId, UserId)`, so one user may hold several roles on one team - and CITE takes whichever the database returns first. `UserTeamRoleEntity.Role` is free text whose only consumer matches it by name against CITE's role names, so a typo is a user pushed to CITE with no role at all. | `UserTeamRoleService` |
| P2-42 | **Every route of `UnitController` answers an empty `users` list**, neither `GetAsync` overload including `UnitUsers` while `UnitProfile` maps `Users` from them - so the one thing a unit's own routes cannot tell you about a unit is who is in it. Four more routes answer a dropped `Include` the same way (`InjectTypeService`'s two reads, `GET my-teams`, `GET users/{userId}/teams`, `GET unitusers`). | `UnitService`, `InjectTypeService`, `TeamService`, `UnitUserService` |

## xAPI

| # | Fix | Where |
| --- | --- | --- |
| P2-43 | **An assertion does not say who was assessed.** `CompetencyAssertion` has no participant field, the actor is the caller, and the team lists the caller as its only member - so an instructor rating six participants writes six statements about themselves and the LRS cannot attribute any of them. The confidence extension is the constant `1.0`, so every one also claims total certainty. | `XApiService` |
| P2-44 | **The two hand-built paths name the same MSEL two different ways** (`msel/{id}` against `msels/{id}`), so one checklist tick can produce two statements naming two activities for the same exercise - and the checkbox path writes `Verb = "completed"` whatever the statement says, so a box being *cleared* is recorded as a completion. | `XApiService` |
| P2-45 | **`GetStatementsAsync` builds its own `HttpClient`** instead of taking the `IHttpClientFactory` every other outbound call uses, so it cannot be stubbed - and it swallows the wrong failures: an LRS that is down is a 500 the user sees, where an LRS answering 401 or 500 is logged and becomes an empty list. Fixing this is what makes the rest of the service testable. | `XApiService.cs:843` |
| P2-46 | **An unconfigured service reports success having queued nothing**, and three of the four routes discard the `bool` they are given, so "queued", "xAPI is off" and "that MSEL does not exist" are one answer. `IsConfigured()` wants the flag and the username while the background service reads the username alone. | `XApiService`, `XApiController` |
| P2-47 | **A caller on two of a MSEL's teams is attributed to whichever row the database returns first**; a checkbox tick against another MSEL's event is filed under the wrong registration; a group number with no move is numbered under move zero; and "grouped under nothing" and "never asked" are the same statement on the wire. | `XApiService` |
| P2-48 | **An empty `UiUrl` records statements for a caller on no team and throws for a caller on one**, `new Uri("/msel/...")` being `file:///msel/...` on Linux where `new Uri(UiUrl)` with nothing appended throws. `ApiUrl` and `UiUrl` take opposite trailing-slash conventions, nothing documents or validates either, and `appsettings.json` ships both empty. | `XApiService` |

## Player and CITE on the request path

| # | Fix | Where |
| --- | --- | --- |
| P2-49 | **The display order is always 2, and it answers the wrong question anyway.** `PushApplication` reads `msel.PlayerApplications.Count + 1` on a MSEL loaded with no `Include`, and change-tracker fix-up supplies exactly the one row this request inserted - while the position it computes belongs to `PlayerApplicationTeamEntity.DisplayOrder`, a column nothing reads. | `PlayerService` |
| P2-50 | **A push that cannot be built leaves the row stored and already broadcast as created**, `CreateAsync` saving before `PushApplication` runs - so an undeployed MSEL, an unparsable url and a caller on two of the MSEL's teams each answer 500 with an application blueprint lists and Player has never heard of. | `PlayerService` |
| P2-51 | **No `PlayerApplicationTeam` row records the push**, so blueprint's answer to "who can see this" and Player's disagree from the moment of the push, and `POST playerApplicationTeams` cannot correct it. A MSEL author on none of its teams pushes to the all-zeros team id and is told 201. | `PlayerService` |
| P2-52 | **All four reference-data reads turn every failure into 200 and `[]` with nothing logged**, on routes no permission gates; `CiteService.GetScoringModelsAsync` alone drops the request's `CancellationToken`. | `CiteService`, `PlayerService` |

## Elsewhere

| # | Fix | Where |
| --- | --- | --- |
| P2-53 | **An `allCanView` MSEL page is unreadable by a `ViewMsels` holder off the MSEL**, and creating one as an `EditMsels` holder is a 403 with the row saved and broadcast - both writes returning through `GetAsync`. | `MselPageService` |
| P2-54 | **The card import/export pair is consistent with itself and with nothing else**, both routes building their own `JsonSerializerOptions` - so a file built from what `GET cards/templates` answered is a 500 naming `System.Int32`. The inject-type pair has the same problem through the missing `JsonStringEnumConverter`. | `CardService`, `InjectTypeService` |
| P2-55 | **The LMT document is not valid JSON-LD**: the three keywords are named `context`, `type` and `id` with no `@` prefix, so a processor reads plain JSON with no context, no type and no node identity - and the document's own `id` is built from a setting that ships without the `/api` the route lives under, so the identity is a 404. | `LmtService` |
| P2-56 | **`[SanitizeHtml]` is on six properties, none of them the strings pushed to Gallery**, and it is on `MoveEntity.SituationDescription` but not `Description`. `CardEntity` carries none on either string and Gallery renders the description. | `Data/Models/*` |
| P2-57 | **`GET injects/{id}` is 403 for every caller without `ViewMsels`** - see S5 - and `GetByInjectTypeAsync` is scoped by nothing but the type, so it lists private catalogs' injects and injects in no catalog. | `InjectService` |
| P2-58 | **No list route has an `OrderBy`**, so the order of a move list, a team list or a card list is whatever the database returns. Blueprint's UI presents several of them as ordered. | throughout |

# P3 - the contract clients are generated from

`blueprint.ui` checks in a swagger-generated client that nothing regenerates against a running API, so
every row here compiles on both sides and is `undefined` at runtime. **`docs/known-defects.md`'s
*Contract surface* section is the full list**; these are the ones worth fixing first.

| # | Fix |
| --- | --- |
| P3-1 | **Declare the status codes the routes actually answer.** Ten routes declare 201 and answer 200 with no `Location`, two declare 204 and answer 200 with the JSON literal `true`, and `deleteDataField` declares `[ProducesResponseType(typeof(Guid), 204)]`, a pair no response can satisfy. Decide per route whether the declaration or the answer is wrong; several of the answers are right and the attribute is the bug. |
| P3-2 | **`updateMselUnit` declares `typeof(Unit)` and returns a `MselUnit`** - the one outright wrong type in the surface, so the generated client's signature is wrong rather than its status handling. |
| P3-3 | **Three `Location` headers point somewhere useless**: `createCardTeam`'s and `createPlayerApplicationTeam`'s name a route with no PUT (405), `createTeam`'s names a route the creator is 403'd by, and `createUser`'s is handed back by the create that answers 500. |
| P3-4 | **`ViewModels.User.Permissions` is declared and never populated**; `ViewModels.TeamUser` and `ViewModels.UnitUser` derive from `Base` while their entities have no audit columns, so those routes promise `createdBy` and `dateCreated` and answer zeros. Either populate them or stop declaring them. |
| P3-5 | **Every `int` crosses the wire as a JSON string** while the document declares `type: integer`, `JsonIntegerConverter` reaching `ProblemDetails.Status` and `ApiError.Status` too. Reading accepts both forms, so the fix is one-sided and safe: stop writing strings. |
| P3-6 | **Neither error dialect appears in the document.** MVC's `ValidationProblemDetails`/`application/problem+json` answers a binding failure and blueprint's `ApiError`/`application/json` answers anything thrown, and the media type is the only reliable discriminator because the statuses overlap. Declare both, or unify on one. |
| P3-7 | **Every download file is PascalCase with raw integers and numeric enums behind `$id`/`$values`**, against camelCase responses. Use the MVC options, and the import and export stop disagreeing. |
| P3-8 | **Four routes return the sibling APIs' generated DTOs straight through blueprint's own surface**, so regenerating `Cite.Api.Client` or `Player.Api.Client` changes blueprint's contract with no blueprint file changing. Project them onto blueprint view models. |
| P3-9 | **Regenerate `Blueprint.Api.Client`** once the surface is settled; it is generated from the same document. Never hand-edit the output - fix a bad signature on the API side. |

# P4 - hygiene

Cheap, and each one removes a place a future reader has to work out what was intended.

| # | Fix |
| --- | --- |
| P4-1 | **`IInjectTypeService` is registered twice** (`Startup.cs:228` and `:231`). |
| P4-2 | **`ValidateModelStateFilter` is dead code.** `BaseController`'s `[ApiController]` means MVC's `ModelStateInvalidFilter` (order −2000) answers first, so no 400 in blueprint has ever had the `ApiError` shape. Delete the filter or remove `[ApiController]`, but not both. |
| P4-3 | **`Startup` binds a section named `"SignalROptions"` while `appsettings.json` ships `"SignalR"`**, so the configured values are never read. |
| P4-4 | **Remove the silent `catch { }` blocks or log in them**: two in `BlueprintContext.SaveEntries`, `UserClaimsService.ValidateUser`, `ClaimsPrincipalExtensions.GetId`, and the one around the whole of `DatabaseExtensions.InitializeDatabase` (P1-21). |
| P4-5 | **`AddPlayerApiClient` returns null off-request**, and the Gallery and Steamfitter registrations dereference `HttpContext.Request` unguarded. |
| P4-6 | **Delete the dead code.** `ServiceUtilities.SetMselModifiedAsync`'s date argument; `UserClaimsService`'s `groupIds`; `XApiOptions.EmailDomain` (both readers are overwritten four lines later); `CreateAsync`'s own config gate and its `parentData`/`otherData` blocks in `XApiService`, unreachable from all six callers; `ApiClientsExtensions`' `ClientSecret` ternary and its two-argument `GetHttpClient` overload; `AddUserToTeamAsync`'s `if (user != null)`, whose exception the surrounding `catch` absorbs; `IntegrationSteamfitterExtensions`' shared `ActionParameters`; and roughly a dozen `CreatedBy`/`ModifiedBy` assignments in controllers that their services overwrite. |
| P4-7 | **Stop passing arguments nobody reads**: seven of `IntegrationCiteExtensions`' eight methods and two of `IntegrationPlayerExtensions`' take a `BlueprintContext` they never touch; `galleryApiUrl` is passed and never read; `AddApplicationService` resolves a context it never reads, so an application can be sent to a view the MSEL no longer points at. |
| P4-8 | **`SystemRoleService` injects an `IPrincipal`, casts it and never reads it**, so `ManageRoles` is effectively every permission, and nothing validates a permission list - a role may hold the same permission twice, or a number the enum does not define. `GetSystemPermissions` likewise accepts numeric values outside the enum and drops values differing only in case. |
| P4-9 | **`JsonDoubleConverter` is configuration with no subject** - no property in the API is a `double`, `float` or `decimal` - and its three latent defects (NaN written as `0.0`, unguarded infinity throwing mid-response, culture-sensitive parsing) argue for deleting it rather than fixing it. |
| P4-10 | **Give the health probes different questions.** One check answers both (`AddNpgSql(..., tags: ["ready", "live"])`), so a database outage makes Kubernetes restart the pod rather than take it out of the load balancer - and `MapHealthChecks` is called with no `HealthCheckOptions`, so a 24-byte body gets a gzip stream several times a minute. |
| P4-11 | **`PUT cardteams/{id}` is spelled `cardteams` where its seven siblings are `teamcards`**, and `PlayerApiUrl` is the only sibling url shipped without a trailing slash, which silently drops a path prefix. |
| P4-12 | **`SubUserIdProvider` does not fall back to the SOAP name identifier** while `GetId` does, so a principal carrying only that claim has an id everywhere except SignalR - and `GetId` on a null principal throws, which is the root cause of two `UserClaimsService` throws. |
| P4-13 | **`addNewClaims` dedupes by claim `Type` only**, so a second `AddUserClaims` call on one identity adds none of the new permissions; and legacy `UserPermissions` rows become claims typed `SystemAdmin`/`ContentDeveloper`/… with value `"true"`, which no policy reads. |
| P4-14 | **The provider `switch` in `Startup` accepts a misspelled `Database:Provider`** and the application starts with no context registered. Fail at startup. |
| P4-15 | **`MoveEditorRequirement` is the only one of the eight helpers without the `Msel` prefix**, and it takes a non-nullable `Guid` where its three siblings take `Guid?`. Rename and align. |
| P4-16 | **`HostFactoryResolver` never matches `Program.CreateWebHostBuilder`**, which is why `InitializeDatabase` runs outside `Startup` and why the test harness has to give the host its own database. Returning `IWebHostBuilder`, or moving to the minimal-hosting shape, removes a class of harness workaround. |

# Not on this list

- **Coverage gaps** are in `docs/Testing.md` under *What is not covered yet*. They are work to do, not
  defects to fix.
- **Two mutation-verification gaps** are carried rather than fixed:
  `InjectTypeEndpointTests.Download_ForAnIdThatIsNotThere_IsAnEmptyExport` seeds nothing and so no
  mutation can kill it, and the empty-list assertions in the units written before `977f578` want the
  same treatment its three got - seed a row that must not appear.
- **Deliberate pins.** Several tests assert behaviour that is correct and easy to break, and are here
  so nobody "fixes" them: the five correct `batchSize` call sites, `TeamUserService`'s whole shape,
  `MselUnitService.CreateAsync`, `UserMselRoleService`'s `SetMselModifiedAsync` calls,
  `SystemRoleService`'s `SingleOrDefaultAsync` reads, the two matching `DeleteByIdsAsync` call sites,
  and the server-stamped audit fields.
