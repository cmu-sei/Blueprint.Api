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
