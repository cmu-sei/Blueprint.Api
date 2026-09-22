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
