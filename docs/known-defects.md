# Known defects

Defects the test programme on `task/api-tests` characterized rather than fixed. Each line names the
test that pins the current behaviour; that test turns red when the defect is fixed, which is the
signal to delete the line.

Units covered before this file existed recorded their findings in their commit messages instead;
`git log task/api-tests` is the fuller list, and Phase 5's `docs/Testing.md` will consolidate both.

## Group

| Defect | Pinned by |
| --- | --- |
| A duplicate group name is a 500 from the unique index rather than a 409; nothing checks the name first. | `GroupEndpointTests.Create_WithADuplicateName_Is500` |
| A membership naming a user who does not exist is a 500 from the foreign key rather than a 404. | `GroupEndpointTests.CreateMembership_ForAUserThatIsNotThere_Is500` |
| Group membership grants nothing: no requirement helper reads `GroupMemberships`, no route is group-scoped, and `UserClaimsService`'s `groupIds` is dead code — so nine routes maintain a table with no consumer. | `GroupEndpointTests.AGroupMembership_GrantsItsMemberNothing` |

## Invitation

| Defect | Pinned by |
| --- | --- |
| `GetAsync` reports a missing invitation as `EntityNotFoundException<MselEntity>`, indistinguishable from the MSEL being gone. | `InvitationEndpointTests.Get_ForAnIdThatIsNotThere_Is404` |
| `UpdateAsync` decides permission from the request body's `MselId` before the lookup, and the profile maps `MselId` both ways — so an owner of any MSEL may edit and steal every other MSEL's invitations. Eighth instance of this shape; `DeleteAsync` fourteen lines below is the model to copy. | `InvitationEndpointTests.Update_DecidesFromTheRequestBodyAndStealsTheRow` |
| No write path calls `ServiceUtilities.SetMselModifiedAsync`, so inviting people to an exercise leaves its `DateModified` untouched. Seventh service in the tier with that gap. | `InvitationEndpointTests.Create_DoesNotMarkTheMselModified` |

## LMT

| Defect | Pinned by |
| --- | --- |
| The document is not valid JSON-LD: the three keywords are named `context`, `type` and `id` with no `@` prefix (an anonymous type cannot declare one), so a processor reads plain JSON with no context, no type and no node identity — and the competency list the route exists to publish resolves to nothing. | `LmtEndpointTests.Get_AnswersTheJsonLdKeywordsWithoutTheirAtPrefix` |
| Nothing gates the `[AllowAnonymous]` route on the MSEL being a template or published, so a live exercise's name, description and competency list go to anybody who guesses a Guid — and a Guid is the only secret protecting it. | `LmtEndpointTests.Get_ForAPendingMselThatIsNotATemplate_PublishesItAnyway` |
| The document's own `id` is built from `ClientSettings:BlueprintApiUrl`, which ships without the `/api` the route lives under, so the node identity is a 404. The fallback used when the setting is empty does include it. | `LmtEndpointTests.Get_AnswersAnIdThatDoesNotIncludeTheApiPathBase` |

## Catalog

| Defect | Pinned by |
| --- | --- |
| `GET catalogs/{id}` is **204** for an unknown id: `GetAsync` is `SingleOrDefaultAsync`, `CatalogController.cs:100-104` does not null-check what it answers, and `HttpNoContentOutputFormatter` turns `Ok(null)` into a 204. The route declares only 200, so a generated client has no case for it, and every other single-row read in the API answers 404 or 500. | `CatalogEndpointTests.Get_ForAnIdThatIsNotThere_Is204` |
| `CopyAsync` does not null-check the catalog it loaded (`CatalogService.cs:172-186`) and hands it to `privateCatalogCopyAsync`, which dereferences it — so an unknown id is a 500 where every other route in the controller answers 404. | `CatalogEndpointTests.Copy_ForAnIdThatIsNotThere_Is500` |
| `GET my-catalogs` resolves no permission at all (`CatalogController.cs:60-66`). Defensible, the answer being scoped to the caller, but it is the only route in the controller with no authorization of any kind. | `CatalogEndpointTests.GetMine_WithNoPermissions_ReturnsTheCallersOwnPublicAndUnitCatalogs` |

## CatalogInject

| Defect | Pinned by |
| --- | --- |
| `GetAsync` puts the **CatalogInject's** id to `CatalogViewRequirement.IsMet` as if it were a catalog id (`CatalogInjectService.cs:70`), so the requirement is false for every caller — a unit member of the catalog may list its injects and cannot read one row of that same list. `GetByCatalogAsync` passes the right id and is the model to copy. Fourth instance of a primary key compared against the wrong id on this branch. | `CatalogInjectEndpointTests.Get_ForAMemberOfAUnitTheCatalogIsAssignedTo_Is403_ThoughTheyMayListTheSameRow` |
| `CreateAsync` validates neither parent, so an unknown catalog is a 500 from the foreign key rather than a 404. `CatalogUnitService.CreateAsync` checks both and answers two clean 404s. | `CatalogInjectEndpointTests.Create_ForACatalogThatIsNotThere_Is500` |

## CatalogUnit

| Defect | Pinned by |
| --- | --- |
| `GET catalogs/{id}/catalogunits` requires `ManageCatalogs` where the inject list one controller over requires `ViewCatalogs` and accepts a unit member besides — so the caller who can see a catalog's contents cannot see who it is shared with, and the single read is more permissive than the list it belongs to. | `CatalogUnitEndpointTests.GetByCatalog_WithManageCatalogs_ReturnsOnlyThatCatalogsUnits` |
| A missing row is reported as `EntityNotFoundException<CatalogEntity>` (`CatalogUnitService.cs:68`), indistinguishable from the catalog being gone; the service's other four methods name `CatalogUnit`. | `CatalogUnitEndpointTests.Get_ForAnIdThatIsNotThere_Is404_EvenForAStranger` |
| A duplicate `(catalog, unit)` pair is refused by an `ArgumentException`, which is not an `IApiException`, so the answer is 500 where the case deserves a 409. | `CatalogUnitEndpointTests.Create_ForAPairThatIsAlreadyThere_Is500` |
| `CatalogUnitEntity` is not a `BaseEntity` and `ViewModels.CatalogUnit` does not derive from `Base`, so nothing records who shared a catalog with a unit or when. | `CatalogUnitEndpointTests.Get_ForAMemberOfTheUnit_Is200` |

## CiteAction

| Defect | Pinned by |
| --- | --- |
| `UpdateAsync` takes its permission decision from the request body's `MselId` (`CiteActionService.cs:126-137`) before the lookup, and `CiteActionProfile` maps it both ways — so an editor of any MSEL may edit and steal every other MSEL's actions. Ninth instance of this shape; `DeleteAsync` thirty lines below is the model to copy. | `CiteActionEndpointTests.Update_DecidesFromTheRequestBodyAndStealsTheRow` |
| `GetAsync` is `SingleAsync`, so an unknown id is a 500, and the null check below it is dead code whose `EntityNotFoundException` names `DataValueEntity` — the fifth copy of that line, after `OrganizationService`, `MoveService`, `CardService` and `InjectService`. Worth one sweep rather than five fixes. | `CiteActionEndpointTests.Get_ForAnIdThatIsNotThere_Is500` |
| The template fall-through in `GetByMselAsync` dereferences an unguarded `FindAsync` that also takes no `CancellationToken` (`CiteActionService.cs:67-68`), so whether a MSEL exists depends on who asks: 500 for an ordinary caller, an empty list for a `ViewMsels` holder. | `CiteActionEndpointTests.GetByMsel_ForAMselThatIsNotThere_Is500_Or200ForAViewMselsHolder` |
| Nothing validates the MSEL the body names, so an unknown one is a 500 from the foreign key after `EditMsels` has already satisfied the permission check. | `CiteActionEndpointTests.Create_ForAMselThatIsNotThere_Is500` |
| No write path calls `ServiceUtilities.SetMselModifiedAsync`, so adding an action to an exercise leaves its `DateModified` untouched. Eighth service in the tier with that gap. | `CiteActionEndpointTests.Create_DoesNotMarkTheMselModified` |
| `GET citeActions/templates` asks the caller for nothing at all — no permission and no MSEL. | `CiteActionEndpointTests.Templates_WithNoPermissions_ReturnsOnlyTheTemplates` |

## CiteDuty

A line-for-line twin of `CiteActionService`, so every defect above is present here too; the rows below
name the duty test that pins each one rather than repeating the reasoning.

| Defect | Pinned by |
| --- | --- |
| `UpdateAsync` decides from the request body's `MselId` and steals the row. Tenth instance of the shape. | `CiteDutyEndpointTests.Update_DecidesFromTheRequestBodyAndStealsTheRow` |
| `GetAsync` is `SingleAsync` plus a dead null check naming `DataValueEntity` (`CiteDutyService.cs:88`) — the sixth copy of that line. | `CiteDutyEndpointTests.Get_ForAnIdThatIsNotThere_Is500` |
| `GetByMselAsync`'s template fall-through dereferences an unguarded, token-less `FindAsync`. | `CiteDutyEndpointTests.GetByMsel_ForAMselThatIsNotThere_Is500_Or200ForAViewMselsHolder` |
| Nothing validates the MSEL the body names. | `CiteDutyEndpointTests.Create_ForAMselThatIsNotThere_Is500` |
| `GET citeDuties/templates` asks the caller for nothing at all. | `CiteDutyEndpointTests.Templates_WithNoPermissions_ReturnsOnlyTheTemplates` |

## PlayerApplication

The push route is not covered here — `PlayerServiceTests` (`ff5bdad`) characterized it, including the
display order that is always 2.

| Defect | Pinned by |
| --- | --- |
| `GetAsync` is `SingleAsync` plus a dead null check naming `DataValueEntity` — the seventh copy of that line — so an unknown id is a 500. | `PlayerApplicationEndpointTests.Get_ForAnIdThatIsNotThere_Is500` |
| The single read asks `MselUserRequirement` where the list asks `MselViewRequirement`, so a unit member holding no role may read a row they cannot list. | `PlayerApplicationEndpointTests.Get_ForAUnitMemberWithNoRole_Is200_ThoughTheyMayNotListTheSameRow` |
| `UpdateAsync` decides from the request body's `MselId` and the profile maps it back, so an owner of any MSEL may edit and steal every other MSEL's applications. Eleventh instance of the shape. | `PlayerApplicationEndpointTests.Update_DecidesFromTheRequestBodyAndStealsTheRow` |
| `DeleteAsync` dereferences `.MselId` (`:131`) before its null check (`:134`), so the status for an unknown id depends on the caller's permission. | `PlayerApplicationEndpointTests.Delete_ForAnIdThatIsNotThere_Is404WithEditMselsAnd500ForAnOwner` |
| `GetByMselAsync`'s template fall-through dereferences an unguarded, token-less `FindAsync`. | `PlayerApplicationEndpointTests.GetByMsel_ForAMselThatIsNotThere_Is500_Or200ForAViewMselsHolder` |
| Nothing validates the MSEL the body names, so an unknown one is a 500 from the foreign key. | `PlayerApplicationEndpointTests.Create_ForAMselThatIsNotThere_Is500` |

## PlayerApplicationTeam

| Defect | Pinned by |
| --- | --- |
| `DELETE teams/{teamId}/playerApplications/{playerApplicationId}` deletes nothing: `PlayerApplicationTeamController.cs:192` passes the two ids to `DeleteByIdsAsync` transposed, so the route is a 404 for every well-formed request and a 204 only when the caller swaps them. Second instance after `CardTeamController.cs:192`. | `PlayerApplicationTeamEndpointTests.DeleteByIds_Is404ForAWellFormedRequest_AndDeletesTheRowWhenTheIdsAreSwapped` |
| `GET teamplayerApplications` has no filter at all, so one `ViewMsels` holder reads every row in the installation. | `PlayerApplicationTeamEndpointTests.GetAll_WithViewMsels_ReturnsEveryRowInTheInstallation` |
| No write path consults a MSEL role or takes a permission argument, so the MSEL's own owner may not decide which team sees an application and any `EditMsels` holder may. | `PlayerApplicationTeamEndpointTests.NoWriteConsultsAMselRole_SoAnOwnerIsRefusedAndAStrangerIsNot` |
| `UpdateAsync` refuses an out-of-range display order with an `InvalidDataException`, which is a 500 — and a body that omits `displayOrder` binds to 0 and always trips it, so a PUT is unusable by any client that does not echo the row back. `CreateAsync` silently clamps instead. | `PlayerApplicationTeamEndpointTests.Update_WithoutADisplayOrder_Is500`, `…Create_WithEditMsels_Is201_AndClampsTheDisplayOrderIntoRange` |
| The three scoped reads dereference an unchecked lookup, so the status for an id that is not there depends on the caller's permission. | `PlayerApplicationTeamEndpointTests.Get_ForAnIdThatIsNotThere_Is404WithViewMselsAnd500ForEverybodyElse`, `…GetByPlayerApplication_ForAnIdThatIsNotThere_Is500_Or200ForAViewMselsHolder` |
| The update route is spelled `playerApplicationteams/{id}` while the create's `Location` header points at `teamplayerApplications/{id}`, so a client following the header to update what it just created gets a 405. | `PlayerApplicationTeamEndpointTests.Create_WithEditMsels_Is201_AndClampsTheDisplayOrderIntoRange` |

## MselPage

| Defect | Pinned by |
| --- | --- |
| The `AllCanView` branch (`MselPageService.cs:70-74`) ignores `hasSystemPermission`, so marking a page "all can view" makes it unreadable by a `ViewMsels` holder who is not on the MSEL — the opposite of what the flag says. | `MselPageEndpointTests.Get_ForAnAllCanViewPage_Is403ForAViewMselsHolderWhoIsNotOnTheMsel` |
| `CreateAsync` and `UpdateAsync` return through `GetAsync`, so creating or editing an `allCanView` page as an `EditMsels` holder is a 403 with the row already saved and already broadcast, and the caller has no id for it. | `MselPageEndpointTests.Create_OfAnAllCanViewPage_WithEditMsels_Is403_WithTheRowAlreadySaved` |
| Both reads report a missing page as `EntityNotFoundException<MselEntity>`, indistinguishable from the MSEL being gone; the service's own update and delete name it correctly. | `MselPageEndpointTests.Get_ForAnIdThatIsNotThere_Is404_ThatNamesTheMselRatherThanThePage` |
| `GetByMselAsync` demands `MselOwnerRequirement` where the single read demands `MselViewRequirement`, so an ordinary viewer cannot discover that a MSEL has pages and may read every one of them by id. | `MselPageEndpointTests.GetByMsel_ForAViewerOfTheMsel_Is403_ThoughTheyMayReadTheSamePageById` |
| `UpdateAsync` decides from the request body's `MselId` and steals the row. Twelfth instance of the shape. | `MselPageEndpointTests.Update_DecidesFromTheRequestBodyAndStealsTheRow` |

## MselCompetency

Largely a positive control: both writes validate both parents with correctly-named 404s, there is no
update method, and both deletes check existence first.

| Defect | Pinned by |
| --- | --- |
| A pair that is already there is refused by a bare `ArgumentException`, so the answer is a 500 where the case deserves a 409. | `MselCompetencyEndpointTests.Create_ForAPairThatIsAlreadyThere_Is500` |
| Both reads resolve a competency's relationships against the MSEL's own pool alone and drop the rest silently, so the same competency answers a different relationship list per MSEL and "these are related" is indistinguishable from "one of them is not on this MSEL". | `MselCompetencyEndpointTests.Get_AnswersOnlyTheRelationshipsWhoseOtherEndIsOnTheSameMsel` |

## TeamCompetency

| Defect | Pinned by |
| --- | --- |
| A pair that is already there is refused by a bare `ArgumentException`, so the answer is a 500 where the case deserves a 409 — and `(TeamId, CompetencyId)` is uniquely indexed, so the guard is a courtesy rather than the protection. | `TeamCompetencyEndpointTests.Create_ForAPairThatIsAlreadyThere_Is500` |
| A missing row is reported as `EntityNotFoundException<TeamCompetency>` — the view model — where the same service's other routes name `TeamEntity`, `MselEntity` and `CompetencyEntity`. | `TeamCompetencyEndpointTests.Get_ForAnIdThatIsNotThere_Is404_ThatNamesTheViewModel` |
| The team-level read resolves no `RelatedIdNumbers` where the MSEL-level read does, so a client showing a team's competencies cannot draw the relationships it draws one level up and nothing in the surface says so. | `TeamCompetencyEndpointTests.Get_ForAViewerOfTheMsel_Is200` |

## ProficiencyLevel

| Defect | Pinned by |
| --- | --- |
| The service checks no permission at all, so `ProficiencyLevelController`'s five `ForbiddenException` throws are the whole guard. | `RouteAuthorizationTests.EveryRoute_WithNoPermissions_Is403` |
| `GetByScaleAsync` validates no scale, so an unknown one is an empty 200 — indistinguishable from a scale that exists and has no levels yet. | `ProficiencyLevelEndpointTests.GetByScale_ForAScaleThatIsNotThere_IsAnEmpty200` |
| `CreateAsync` validates no foreign key, so a body naming an unknown scale is a 500 where the caller deserves a 404. | `ProficiencyLevelEndpointTests.Create_ForAScaleThatIsNotThere_Is500` |
| All three routes taking an id report a missing level as `EntityNotFoundException<ProficiencyLevel>` — the view model. | `ProficiencyLevelEndpointTests.Update_ForAnIdThatIsNotThere_Is404_ThatNamesTheViewModel` |
| The profile maps `ProficiencyScaleId` and nothing compares the body's to the stored row's, so a PUT moves a level from one scale to another with nothing recorded either side. | `ProficiencyLevelEndpointTests.Update_MovesTheLevelToWhicheverScaleTheBodyNames` |

## ProficiencyScale

| Defect | Pinned by |
| --- | --- |
| The service checks no permission at all, so `ProficiencyScaleController`'s seven `ForbiddenException` throws are the whole guard. | `RouteAuthorizationTests.EveryRoute_WithNoPermissions_Is403` |
| A duplicate `Name` is a 500 from the unique index rather than a 409; nothing checks the name first. | `ProficiencyScaleEndpointTests.Create_WithANameThatIsAlreadyTaken_Is500` |
| Deleting a scale cascades to every level on it with no dependent count and no 409, so an assessment scale in use is removed by one request. | `ProficiencyScaleEndpointTests.Delete_WithManageCompetencyFrameworks_Is204_TakingTheLevelsWithIt` |
| The download and upload pair build their own `JsonSerializerOptions`, so the file is PascalCase with raw integers behind `ReferenceHandler.Preserve` where every response is camelCase with `int` as a JSON string — and a file this installation exported is a 500 when uploaded back, the unique `Name` index refusing it. | `ProficiencyScaleEndpointTests.DownloadJson_WithManageCompetencyFrameworks_IsAFileNamingOnlyTheRequestedScales`, `…UploadJson_OfAFileThisInstallationExported_Is500FromTheNameIndex` |
| `UploadJsonAsync` mints fresh ids for the scale and every level and ignores any id in the file, so an import cannot round-trip and nothing links the two copies. | `ProficiencyScaleEndpointTests.UploadJson_OfARenamedExport_CreatesASecondScaleWithFreshIds` |
| The PUT answers an empty `proficiencyLevels` where the GET answers them, the update never loading the collection it returns. | `ProficiencyScaleEndpointTests.Update_WithManageCompetencyFrameworks_Is200_AnsweringNoLevelsAndKeepingThem` |
| The download requires `ManageCompetencyFrameworks` though it writes nothing, where its two GET siblings want `ViewCompetencyFrameworks`. | `ProficiencyScaleEndpointTests.DownloadJson_WithManageCompetencyFrameworks_IsAFileNamingOnlyTheRequestedScales` |
| An id that is not there is an empty export rather than a 404, so a client cannot tell an empty scale from a scale that is gone. | `ProficiencyScaleEndpointTests.DownloadJson_ForAnIdThatIsNotThere_IsAnEmptyExport` |

## MainHub

| Defect | Pinned by |
| --- | --- |
| `Greet` and `GetPresence` check nothing beyond the caller being signed in — no permission, no MSEL, no role, and `Greet` does not even parse its argument — so any authenticated caller may read who is working on any exercise and announce themselves to them. The presence half of the hub is unauthorized end to end. | `MainHubTests.Greet_ForAMselTheCallerCannotReach_BroadcastsAnyway`, `…GetPresence_ForAMselTheCallerCannotReach_ListsItsOccupantsAnyway`, `MainHubConnectionTests.GetPresence_OverARealConnection_AnswersAStrangerTheOccupantsOfAnyMsel` |
| `SelectMsel` ignores an unauthorized MSEL, or two MSELs, in silence: no group, no cache entry, no error and no answer, the method returning `Task`. So a client believes it is subscribed, receives nothing, and cannot tell that from an exercise nobody is editing. Every other authorization decision in the API answers 403. | `MainHubTests.SelectMsel_ForAMselTheCallerCannotReach_JoinsNothing_AndSaysNothing`, `…SelectMsel_WithTwoIds_JoinsNeither` |
| Which MSELs a connection is joined to is gated on `ManageUsers` — a user-administration permission — and `ViewMsels` is never consulted. A `ManageUsers` holder with no MSEL permission receives every edit to every exercise; a `ViewMsels` holder is told nothing beyond the MSELs they belong to. | `MainHubTests.Join_WithManageUsers_AddsEveryMselInTheInstallation` |
| "All templates" shares a clause with "MSELs I created", so every connected user is joined to the group of every template in the installation and receives every edit made to one, with no way to opt out. | `MainHubTests.Join_AddsAGroupPerReachableMsel_PerUnit_AndOneForTheCallerThemselves` |
| `Join` adds a group per unit and `Leave` removes only the MSEL groups and the caller's own, so `Leave` is a partial undo and a client calling it still receives everything addressed to its units. | `MainHubTests.Leave_RemovesTheMselGroupsAndTheCallersOwn_ButNotTheUnitGroups` |
| `Leave` builds its departure payload from the caller's current claims where `OnDisconnectedAsync` builds it from the cached connection — two sources for one event, so a renamed user departs under two different names depending on how they left. | `MainHubTests.Leave_ForATrackedConnection_BroadcastsTheClaimsIdentity_AndForgetsTheConnection`, `…OnDisconnected_ForATrackedConnection_BroadcastsTheCachedIdentity` |
| Three of the six constructor dependencies — `ITeamService`, `IMselService` and `DatabaseOptions` — are assigned and never read. | `MainHubTests` passes null for all three; every test in the file is the assertion |
| Both cancellation tokens are useless: `_ct` comes from a `CancellationTokenSource` nothing cancels, `GetAdminIdList` builds a second with `new CancellationToken()`, and not one of the six database queries is passed a token — so a client that disconnects mid-call leaves its query running. | `MainHubTests` class remarks |
| The `sub` claim is read with `Claims.First(...)`, so a principal without one is an `InvalidOperationException` out of the hub rather than a refused connection. | `MainHubTests.Join_ForACallerWithNoSubClaim_Throws` |
| Presence is kept in a host-wide `HubCache` with no cross-instance state, so a scaled-out deployment shows each replica only its own users and blueprint.ui's "who else is here" is wrong by however many replicas are running. | `MainHubConnectionTests.TheHubCache_IsOneInstanceForTheWholeApplication` |
