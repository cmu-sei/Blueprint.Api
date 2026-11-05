// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// What the mappings between entities and view models preserve, over HTTP: how a missing list or dictionary is
/// stored and answered, what a write stores from the objects nested in its body, which full-body updates
/// write the body's id, and which related rows a read carries.
/// </summary>
public class MappedResponseTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---- Missing lists and dictionaries ---------------------------------------------------------

    /// <summary>A system role stored without a permission list is answered with an empty one.</summary>
    [Fact]
    public async Task A_system_role_stored_without_permissions_is_read_with_an_empty_list()
    {
        var role = TestData.SystemRole(name: "No permissions");
        role.Permissions = null;
        await Seed(role);

        var one = await ReadAsync<ViewModels.SystemRole>(await RootClient.GetAsync($"api/system-roles/{role.Id}", Ct), JsonOptions);
        var all = await ReadAsync<List<ViewModels.SystemRole>>(await RootClient.GetAsync("api/system-roles", Ct), JsonOptions);

        Assert.NotNull(one.Permissions);
        Assert.Empty(one.Permissions);
        Assert.Empty(Assert.Single(all, x => x.Id == role.Id).Permissions);
    }

    /// <summary>A system role created or updated with a null permission list stores and answers an empty one.</summary>
    [Fact]
    public async Task A_system_role_written_with_null_permissions_stores_an_empty_list()
    {
        var created = await ReadAsync<ViewModels.SystemRole>(await RootClient.PostAsJsonAsync("api/system-roles",
            new { id = Guid.NewGuid(), name = "Created without permissions", permissions = (string[])null }, Ct), JsonOptions);
        var role = TestData.SystemRole(name: "Updated without permissions", permissions: [SystemPermission.ViewUsers]);
        await Seed(role);

        var updated = await ReadAsync<ViewModels.SystemRole>(await RootClient.PutAsJsonAsync($"api/system-roles/{role.Id}",
            new { id = role.Id, name = "Renamed", allPermissions = true, permissions = (string[])null }, Ct), JsonOptions);

        Assert.NotNull(created.Permissions);
        Assert.Empty(created.Permissions);
        Assert.Equal(("Renamed", true), (updated.Name, updated.AllPermissions));
        Assert.NotNull(updated.Permissions);
        Assert.Empty(updated.Permissions);
        await using var context = NewContext();
        Assert.Empty((await context.SystemRoles.SingleAsync(x => x.Id == created.Id, Ct)).Permissions);
        var stored = await context.SystemRoles.SingleAsync(x => x.Id == role.Id, Ct);
        Assert.Equal(("Renamed", true), (stored.Name, stored.AllPermissions));
        Assert.NotNull(stored.Permissions);
        Assert.Empty(stored.Permissions);
    }

    /// <summary>A system role's permissions are stored and answered as the body sends them.</summary>
    [Fact]
    public async Task A_system_role_written_with_permissions_stores_them()
    {
        var role = TestData.SystemRole(name: "With permissions", permissions: [SystemPermission.ViewUsers]);
        await Seed(role);

        var updated = await ReadAsync<ViewModels.SystemRole>(await RootClient.PutAsJsonAsync($"api/system-roles/{role.Id}",
            new { id = role.Id, name = role.Name, permissions = new[] { "ViewMsels", "ManageUnits" } }, Ct), JsonOptions);

        Assert.Equal([SystemPermission.ViewMsels, SystemPermission.ManageUnits], updated.Permissions);
        var stored = await ReadBack(db => db.SystemRoles.SingleAsync(x => x.Id == role.Id, Ct));
        Assert.Equal([SystemPermission.ViewMsels, SystemPermission.ManageUnits], stored.Permissions);
    }

    /// <summary>
    /// A Steamfitter task stored without action parameters is answered with an empty dictionary, on the
    /// event and on the MSEL's event list.
    /// </summary>
    [Fact]
    public async Task A_steamfitter_task_stored_without_action_parameters_is_read_with_an_empty_dictionary()
    {
        var (msel, scenarioEvent) = await SeedEventWithTask(actionParameters: null);

        var one = await ReadAsync<ViewModels.ScenarioEvent>(await RootClient.GetAsync($"api/scenarioEvents/{scenarioEvent.Id}", Ct), JsonOptions);
        var all = await ReadAsync<List<ViewModels.ScenarioEvent>>(await RootClient.GetAsync($"api/msels/{msel.Id}/scenarioEvents", Ct), JsonOptions);

        Assert.NotNull(one.SteamfitterTask.ActionParameters);
        Assert.Empty(one.SteamfitterTask.ActionParameters);
        Assert.Empty(Assert.Single(all).SteamfitterTask.ActionParameters);
    }

    /// <summary>A Steamfitter task's action parameters are answered as stored.</summary>
    [Fact]
    public async Task A_steamfitter_tasks_action_parameters_are_read_as_stored()
    {
        var (_, scenarioEvent) = await SeedEventWithTask(new Dictionary<string, string> { ["url"] = "https://example.test" });

        var one = await ReadAsync<ViewModels.ScenarioEvent>(await RootClient.GetAsync($"api/scenarioEvents/{scenarioEvent.Id}", Ct), JsonOptions);

        Assert.Equal(new Dictionary<string, string> { ["url"] = "https://example.test" }, one.SteamfitterTask.ActionParameters);
        Assert.Null(one.SteamfitterTask.ScenarioEvent);
    }

    /// <summary>A scenario event created with a task whose action parameters are null stores an empty dictionary.</summary>
    [Fact]
    public async Task A_scenario_event_created_with_null_action_parameters_stores_an_empty_dictionary()
    {
        var msel = TestData.Msel(createdBy: Root.Id);
        await Seed(msel);

        var created = await ReadAsync<List<ViewModels.ScenarioEvent>>(await RootClient.PostAsJsonAsync("api/scenarioEvents",
            new
            {
                mselId = msel.Id,
                scenarioEventType = "Inject",
                integrationTarget = "Steamfitter",
                steamfitterTask = new { name = "Task", taskType = "Notification", actionParameters = (Dictionary<string, string>)null },
            }, Ct), JsonOptions);

        var answered = Assert.Single(created);
        Assert.Empty(answered.SteamfitterTask.ActionParameters);
        var stored = await ReadBack(db => db.SteamfitterTasks.SingleAsync(x => x.ScenarioEventId == answered.Id, Ct));
        Assert.Equal("Task", stored.Name);
        Assert.NotNull(stored.ActionParameters);
        Assert.Empty(stored.ActionParameters);
    }

    /// <summary>
    /// An edit whose task has null action parameters empties the stored ones; an edit that sends some
    /// replaces them.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_scenario_event_edit_writes_the_tasks_action_parameters(bool send)
    {
        var (msel, scenarioEvent) = await SeedEventWithTask(new Dictionary<string, string> { ["old"] = "value" });
        var parameters = send ? new Dictionary<string, string> { ["new"] = "value" } : null;

        var response = await RootClient.PutAsJsonAsync($"api/scenarioEvents/{scenarioEvent.Id}",
            new
            {
                id = scenarioEvent.Id,
                mselId = msel.Id,
                deltaSeconds = scenarioEvent.DeltaSeconds,
                groupOrder = scenarioEvent.GroupOrder,
                scenarioEventType = "Inject",
                integrationTarget = "Steamfitter",
                steamfitterTask = new { name = "Edited", taskType = "Notification", actionParameters = parameters },
            }, Ct);

        var answered = Assert.Single(await ReadAsync<List<ViewModels.ScenarioEvent>>(response, JsonOptions));
        var expected = parameters ?? [];
        Assert.Equal(expected, answered.SteamfitterTask.ActionParameters);
        var stored = await ReadBack(db => db.SteamfitterTasks.SingleAsync(x => x.ScenarioEventId == scenarioEvent.Id, Ct));
        Assert.Equal("Edited", stored.Name);
        Assert.Equal(expected, stored.ActionParameters);
    }

    /// <summary>A proficiency scale created or updated with a null level list is answered with an empty one.</summary>
    [Fact]
    public async Task A_proficiency_scale_written_with_null_levels_is_answered_with_an_empty_list()
    {
        var created = await ReadAsync<ViewModels.ProficiencyScale>(await RootClient.PostAsJsonAsync("api/proficiencyScales",
            new { name = "Created without levels", proficiencyLevels = (object[])null }, Ct), JsonOptions);
        var scale = TestData.ProficiencyScale(name: "Updated without levels");
        await Seed(scale);
        var level = TestData.ProficiencyLevel(scale.Id, "Kept");
        await Seed(level);

        var updated = await ReadAsync<ViewModels.ProficiencyScale>(await RootClient.PutAsJsonAsync($"api/proficiencyScales/{scale.Id}",
            new { id = scale.Id, name = "Renamed", proficiencyLevels = (object[])null }, Ct), JsonOptions);

        Assert.NotNull(created.ProficiencyLevels);
        Assert.Empty(created.ProficiencyLevels);
        Assert.Equal("Renamed", updated.Name);
        Assert.NotNull(updated.ProficiencyLevels);
        Assert.Empty(updated.ProficiencyLevels);
        Assert.Equal("Kept", (await ReadBack(db => db.ProficiencyLevels.SingleAsync(x => x.Id == level.Id, Ct))).Name);
    }

    /// <summary>A competency created with a null child list is answered with an empty one.</summary>
    [Fact]
    public async Task A_competency_created_with_null_children_is_answered_with_an_empty_list()
    {
        var framework = TestData.CompetencyFramework();
        await Seed(framework);

        var response = await RootClient.PostAsJsonAsync($"api/competencyframeworks/{framework.Id}/competencies",
            new { idNumber = "C-1", shortName = "One", children = (object[])null, relatedIdNumbers = (string[])null }, Ct);

        var created = await ReadAsync<ViewModels.Competency>(response, JsonOptions);
        Assert.Equal(("C-1", "One", framework.Id), (created.IdNumber, created.ShortName, created.CompetencyFrameworkId));
        Assert.NotNull(created.Children);
        Assert.Empty(created.Children);
    }

    // ---- Objects nested in a request body -------------------------------------------------------

    /// <summary>A competency created with nested children stores them as competencies under it.</summary>
    [Fact]
    public async Task A_competency_created_with_nested_children_stores_them()
    {
        var framework = TestData.CompetencyFramework();
        await Seed(framework);
        var childId = Guid.NewGuid();

        var response = await RootClient.PostAsJsonAsync($"api/competencyframeworks/{framework.Id}/competencies",
            new
            {
                idNumber = "P-1",
                shortName = "Parent",
                children = new[] { new { id = childId, competencyFrameworkId = framework.Id, idNumber = "K-1", shortName = "Child" } },
            }, Ct);

        var created = await ReadAsync<ViewModels.Competency>(response, JsonOptions);
        var child = Assert.Single(created.Children);
        Assert.Equal((childId, "K-1", "Child", created.Id), (child.Id, child.IdNumber, child.ShortName, child.ParentId));
        var stored = await ReadBack(db => db.Competencies.SingleAsync(x => x.Id == childId, Ct));
        Assert.Equal((created.Id, framework.Id, "Child"), (stored.ParentId, stored.CompetencyFrameworkId, stored.ShortName));
    }

    /// <summary>
    /// A unit membership created with a nested user and unit inserts both, and the membership is put on them
    /// rather than on the ids the body names.
    /// </summary>
    [Fact]
    public async Task A_unit_user_created_with_a_nested_user_and_unit_stores_them()
    {
        var user = TestData.User(name: "Named");
        var unit = TestData.Unit(name: "Named unit");
        await Seed(user, unit);
        var nestedUserId = Guid.NewGuid();
        var nestedUnitId = Guid.NewGuid();

        var response = await RootClient.PostAsJsonAsync("api/unitusers",
            new
            {
                userId = user.Id,
                unitId = unit.Id,
                user = new { id = nestedUserId, name = "Nested user" },
                unit = new { id = nestedUnitId, name = "Nested unit", shortName = "NU" },
            }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadAsync<ViewModels.UnitUser>(response, JsonOptions);
        Assert.Equal((nestedUserId, nestedUnitId), (created.UserId, created.UnitId));
        await using var context = NewContext();
        Assert.Equal("Nested user", (await context.Users.SingleAsync(x => x.Id == nestedUserId, Ct)).Name);
        var nestedUnit = await context.Units.SingleAsync(x => x.Id == nestedUnitId, Ct);
        Assert.Equal(("Nested unit", "NU"), (nestedUnit.Name, nestedUnit.ShortName));
        var stored = await context.UnitUsers.SingleAsync(x => x.Id == created.Id, Ct);
        Assert.Equal((nestedUserId, nestedUnitId), (stored.UserId, stored.UnitId));
    }

    /// <summary>A catalog inject created with a nested inject inserts the inject with its data values.</summary>
    [Fact]
    public async Task A_catalog_inject_created_with_a_nested_inject_stores_it_with_its_data_values()
    {
        var injectType = TestData.InjectType();
        await Seed(injectType);
        var dataField = TestData.DataField(injectTypeId: injectType.Id);
        var catalog = TestData.Catalog(injectType.Id);
        await Seed(dataField, catalog);
        var injectId = Guid.NewGuid();
        var dataValueId = Guid.NewGuid();

        var response = await RootClient.PostAsJsonAsync("api/cataloginjects",
            new
            {
                catalogId = catalog.Id,
                injectId,
                inject = new
                {
                    id = injectId,
                    name = "Nested inject",
                    injectTypeId = injectType.Id,
                    dataValues = new[] { new { id = dataValueId, dataFieldId = dataField.Id, injectId, value = "nested value" } },
                },
            }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadAsync<ViewModels.CatalogInject>(response, JsonOptions);
        Assert.Equal((catalog.Id, injectId), (created.CatalogId, created.InjectId));
        Assert.Equal("Nested inject", created.Inject.Name);
        Assert.Equal("nested value", Assert.Single(created.Inject.DataValues).Value);
        await using var context = NewContext();
        Assert.Equal("Nested inject", (await context.Injects.SingleAsync(x => x.Id == injectId, Ct)).Name);
        var value = await context.DataValues.SingleAsync(x => x.Id == dataValueId, Ct);
        Assert.Equal((injectId, dataField.Id, "nested value"), (value.InjectId, value.DataFieldId, value.Value));
    }

    /// <summary>A catalog unit created with a nested unit of another id inserts that unit too.</summary>
    [Fact]
    public async Task A_catalog_unit_created_with_a_nested_unit_stores_it()
    {
        var injectType = TestData.InjectType();
        await Seed(injectType);
        var catalog = TestData.Catalog(injectType.Id);
        var unit = TestData.Unit(name: "Named unit");
        await Seed(catalog, unit);
        var nestedUnitId = Guid.NewGuid();

        var response = await RootClient.PostAsJsonAsync("api/catalogunits",
            new { catalogId = catalog.Id, unitId = unit.Id, unit = new { id = nestedUnitId, name = "Nested unit", shortName = "NU" } }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadAsync<ViewModels.CatalogUnit>(response, JsonOptions);
        Assert.Equal(nestedUnitId, created.UnitId);
        Assert.Equal("Nested unit", created.Unit.Name);
        Assert.Equal("Nested unit", (await ReadBack(db => db.Units.SingleAsync(x => x.Id == nestedUnitId, Ct))).Name);
    }

    /// <summary>A catalog unit update with a nested unit renames the stored unit with that id, as AutoMapper did.</summary>
    [Fact]
    public async Task A_catalog_unit_update_with_a_nested_unit_overwrites_the_unit()
    {
        var injectType = TestData.InjectType();
        await Seed(injectType);
        var catalog = TestData.Catalog(injectType.Id);
        var unit = TestData.Unit(name: "Stored unit");
        await Seed(catalog, unit);
        var catalogUnit = TestData.CatalogUnit(unit.Id, catalog.Id);
        await Seed(catalogUnit);

        var response = await RootClient.PutAsJsonAsync($"api/catalogunits/{catalogUnit.Id}",
            new { id = catalogUnit.Id, catalogId = catalog.Id, unitId = unit.Id, unit = new { id = unit.Id, name = "Renamed" } }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Renamed", (await ReadBack(db => db.Units.SingleAsync(x => x.Id == unit.Id, Ct))).Name);
    }

    /// <summary>An MSEL competency and a team competency created with a nested competency insert it.</summary>
    [Fact]
    public async Task Competency_links_created_with_a_nested_competency_store_it()
    {
        var framework = TestData.CompetencyFramework();
        var msel = TestData.Msel(createdBy: Root.Id);
        await Seed(framework, msel);
        var competency = TestData.Competency(framework.Id, "LINKED");
        var team = TestData.Team(msel.Id);
        await Seed(competency, team);
        var forMsel = Guid.NewGuid();
        var forTeam = Guid.NewGuid();

        var mselResponse = await RootClient.PostAsJsonAsync("api/mselcompetencies",
            new { mselId = msel.Id, competencyId = competency.Id, competency = new { id = forMsel, competencyFrameworkId = framework.Id, idNumber = "M-1", shortName = "For MSEL" } }, Ct);
        var teamResponse = await RootClient.PostAsJsonAsync("api/teamcompetencies",
            new { teamId = team.Id, competencyId = competency.Id, competency = new { id = forTeam, competencyFrameworkId = framework.Id, idNumber = "T-1", shortName = "For team" } }, Ct);

        Assert.Equal(HttpStatusCode.Created, mselResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, teamResponse.StatusCode);
        await using var context = NewContext();
        Assert.Equal("For MSEL", (await context.Competencies.SingleAsync(x => x.Id == forMsel, Ct)).ShortName);
        Assert.Equal("For team", (await context.Competencies.SingleAsync(x => x.Id == forTeam, Ct)).ShortName);
        Assert.Equal(forMsel, (await context.MselCompetencies.SingleAsync(x => x.MselId == msel.Id, Ct)).CompetencyId);
        Assert.Equal(forTeam, (await context.TeamCompetencies.SingleAsync(x => x.TeamId == team.Id, Ct)).CompetencyId);
    }

    /// <summary>A catalog created with a nested parent inserts the parent and is put under it.</summary>
    [Fact]
    public async Task A_catalog_created_with_a_nested_parent_stores_the_parent()
    {
        var injectType = TestData.InjectType();
        await Seed(injectType);
        var parentId = Guid.NewGuid();

        var response = await RootClient.PostAsJsonAsync("api/catalogs",
            new { name = "Child", injectTypeId = injectType.Id, parent = new { id = parentId, name = "Nested parent", injectTypeId = injectType.Id } }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadAsync<ViewModels.Catalog>(response, JsonOptions);
        Assert.Equal(parentId, created.ParentId);
        Assert.Equal("Nested parent", created.Parent.Name);
        await using var context = NewContext();
        Assert.Equal("Nested parent", (await context.Catalogs.SingleAsync(x => x.Id == parentId, Ct)).Name);
        Assert.Equal(parentId, (await context.Catalogs.SingleAsync(x => x.Id == created.Id, Ct)).ParentId);
    }

    /// <summary>A catalog update with a nested parent overwrites the stored parent's fields from the body.</summary>
    [Fact]
    public async Task A_catalog_update_with_a_nested_parent_overwrites_the_parent()
    {
        var injectType = TestData.InjectType();
        await Seed(injectType);
        var parent = TestData.Catalog(injectType.Id);
        var child = TestData.Catalog(injectType.Id);
        await Seed(parent, child);

        var response = await RootClient.PutAsJsonAsync($"api/catalogs/{child.Id}",
            new
            {
                id = child.Id,
                name = "Renamed child",
                injectTypeId = injectType.Id,
                parentId = parent.Id,
                parent = new { id = parent.Id, name = "Renamed parent", description = "From the body", injectTypeId = injectType.Id },
            }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var context = NewContext();
        var storedParent = await context.Catalogs.SingleAsync(x => x.Id == parent.Id, Ct);
        Assert.Equal(("Renamed parent", "From the body"), (storedParent.Name, storedParent.Description));
        var storedChild = await context.Catalogs.SingleAsync(x => x.Id == child.Id, Ct);
        Assert.Equal(("Renamed child", (Guid?)parent.Id), (storedChild.Name, storedChild.ParentId));
    }

    /// <summary>
    /// A catalog that is its own parent has itself as its loaded parent, so an update with a nested parent of
    /// that id writes the nested parent onto the stored catalog rather than adding a second one: it saves.
    /// </summary>
    [Fact]
    public async Task A_self_parented_catalog_update_with_a_nested_parent_writes_it_onto_the_catalog()
    {
        var injectType = TestData.InjectType();
        await Seed(injectType);
        var catalog = TestData.Catalog(injectType.Id);
        await Seed(catalog);
        await using (var db = NewContext())
        {
            (await db.Catalogs.SingleAsync(x => x.Id == catalog.Id, Ct)).ParentId = catalog.Id;
            await db.SaveChangesAsync(Ct);
        }

        var response = await RootClient.PutAsJsonAsync($"api/catalogs/{catalog.Id}",
            new
            {
                id = catalog.Id,
                name = "Outer",
                description = "Outer",
                injectTypeId = injectType.Id,
                parentId = catalog.Id,
                listDataFields = "outer",
                parent = new { id = catalog.Id, name = "Inner", description = "Inner", injectTypeId = injectType.Id, parentId = catalog.Id, listDataFields = "inner" },
            }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await ReadBack(db => db.Catalogs.SingleAsync(x => x.Id == catalog.Id, Ct));
        Assert.Equal(("Inner", "Inner", "outer", (Guid?)catalog.Id), (stored.Name, stored.Description, stored.ListDataFields, stored.ParentId));
        Assert.Equal(Root.Id, stored.ModifiedBy);
        Assert.Equal(1, await ReadBack(db => db.Catalogs.CountAsync(x => x.Id == catalog.Id, Ct)));
    }

    /// <summary>
    /// A proficiency scale update writes the levels in its body onto the stored ones with those ids, and
    /// answers them; the scale's other levels are left alone.
    /// </summary>
    [Fact]
    public async Task A_proficiency_scale_update_writes_the_levels_in_its_body()
    {
        var scale = TestData.ProficiencyScale(name: "Scale");
        await Seed(scale);
        var edited = TestData.ProficiencyLevel(scale.Id, "Edited", value: 1);
        var untouched = TestData.ProficiencyLevel(scale.Id, "Untouched", value: 2);
        await Seed(edited, untouched);

        var updated = await ReadAsync<ViewModels.ProficiencyScale>(await RootClient.PutAsJsonAsync($"api/proficiencyScales/{scale.Id}",
            new
            {
                id = scale.Id,
                name = "Scale",
                proficiencyLevels = new[] { new { id = edited.Id, proficiencyScaleId = scale.Id, name = "Renamed level", value = 5, displayOrder = 3 } },
            }, Ct), JsonOptions);

        var answered = Assert.Single(updated.ProficiencyLevels);
        Assert.Equal((edited.Id, "Renamed level", 5, 3), (answered.Id, answered.Name, answered.Value, answered.DisplayOrder));
        await using var context = NewContext();
        var storedEdited = await context.ProficiencyLevels.SingleAsync(x => x.Id == edited.Id, Ct);
        Assert.Equal(("Renamed level", 5, 3), (storedEdited.Name, storedEdited.Value, storedEdited.DisplayOrder));
        Assert.Equal("Untouched", (await context.ProficiencyLevels.SingleAsync(x => x.Id == untouched.Id, Ct)).Name);
    }

    // ---- Full-body updates ----------------------------------------------------------------------

    /// <summary>
    /// The full-body updates write the body's id onto the stored row, so a body whose id is not the route's
    /// tries to change the key and fails the save; the row is left as it was.
    /// </summary>
    [Theory]
    [InlineData("catalogs")]
    [InlineData("groups")]
    [InlineData("system-roles")]
    [InlineData("proficiencyScales")]
    [InlineData("catalogunits")]
    [InlineData("injects")]
    public async Task A_full_body_update_with_another_id_is_a_server_error(string route)
    {
        var injectType = TestData.InjectType();
        await Seed(injectType);
        var catalog = TestData.Catalog(injectType.Id);
        var unit = TestData.Unit();
        var group = TestData.Group("Group");
        var role = TestData.SystemRole(name: "Role");
        var scale = TestData.ProficiencyScale(name: "Scale");
        var inject = TestData.Inject(injectType.Id);
        await Seed(catalog, unit, group, role, scale, inject);
        var catalogUnit = TestData.CatalogUnit(unit.Id, catalog.Id);
        await Seed(catalogUnit);
        var other = Guid.NewGuid();
        (Guid Id, object Body) target = route switch
        {
            "catalogs" => (catalog.Id, new { id = other, name = "Renamed", injectTypeId = injectType.Id }),
            "groups" => (group.Id, new { id = other, name = "Renamed" }),
            "system-roles" => (role.Id, new { id = other, name = "Renamed" }),
            "proficiencyScales" => (scale.Id, new { id = other, name = "Renamed" }),
            "catalogunits" => (catalogUnit.Id, new { id = other, catalogId = catalog.Id, unitId = unit.Id }),
            _ => (inject.Id, new { id = other, name = "Renamed", injectTypeId = injectType.Id }),
        };

        var response = await RootClient.PutAsJsonAsync($"api/{route}/{target.Id}", target.Body, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await using var context = NewContext();
        Assert.Equal(0, route switch
        {
            "catalogs" => await context.Catalogs.CountAsync(x => x.Id == other || x.Name == "Renamed", Ct),
            "groups" => await context.Groups.CountAsync(x => x.Id == other || x.Name == "Renamed", Ct),
            "system-roles" => await context.SystemRoles.CountAsync(x => x.Id == other || x.Name == "Renamed", Ct),
            "proficiencyScales" => await context.ProficiencyScales.CountAsync(x => x.Id == other || x.Name == "Renamed", Ct),
            "catalogunits" => await context.CatalogUnits.CountAsync(x => x.Id == other, Ct),
            _ => await context.Injects.CountAsync(x => x.Id == other || x.Name == "Renamed", Ct),
        });
    }

    /// <summary>
    /// Every update writes the nulls in its body: a nullable member the body sends as null (or leaves out) is
    /// cleared on the stored row, for each kind of update. The HTML fields SanitizerInterceptor cleans are
    /// cleared to an empty string.
    /// </summary>
    [Theory]
    [InlineData("cards")]
    [InlineData("citeActions")]
    [InlineData("citeDuties")]
    [InlineData("moves")]
    [InlineData("invitations")]
    [InlineData("playerApplications")]
    [InlineData("teams")]
    [InlineData("organizations")]
    [InlineData("dataFields")]
    [InlineData("dataOptions")]
    [InlineData("dataValues")]
    [InlineData("mselpages")]
    [InlineData("injects")]
    [InlineData("users")]
    [InlineData("catalogs")]
    [InlineData("units")]
    [InlineData("groups")]
    [InlineData("proficiencyLevels")]
    [InlineData("proficiencyScales")]
    [InlineData("system-roles")]
    [InlineData("injectTypes")]
    [InlineData("scenarioEvents")]
    [InlineData("steamfitterTask")]
    public async Task An_update_with_null_values_clears_them(string kind)
    {
        var msel = TestData.Msel(createdBy: Root.Id);
        var injectType = TestData.InjectType();
        injectType.Description = "Stored";
        await Seed(msel, injectType);
        var team = TestData.Team(msel.Id);
        await Seed(team);
        var now = DateTime.UtcNow;

        async Task<(string Url, object Body, Func<Data.BlueprintContext, Task<object[]>> Read)> Arrange()
        {
            switch (kind)
            {
                case "cards":
                {
                    var row = TestData.Card(msel.Id, description: "Stored", galleryId: Guid.NewGuid());
                    await Seed(row);
                    return ($"cards/{row.Id}", new { name = "Card", description = (string)null, galleryId = (Guid?)null },
                        async db => { var x = await db.Cards.SingleAsync(y => y.Id == row.Id, Ct); return [x.Description, x.GalleryId]; });
                }
                case "citeActions":
                {
                    var row = TestData.CiteAction(mselId: msel.Id, teamId: team.Id, description: "Stored");
                    await Seed(row);
                    return ($"citeActions/{row.Id}", new { teamId = (Guid?)null, description = (string)null },
                        async db => { var x = await db.CiteActions.SingleAsync(y => y.Id == row.Id, Ct); return [x.TeamId, x.Description]; });
                }
                case "citeDuties":
                {
                    var row = TestData.CiteDuty(mselId: msel.Id, teamId: team.Id, name: "Stored");
                    await Seed(row);
                    return ($"citeDuties/{row.Id}", new { teamId = (Guid?)null, name = (string)null },
                        async db => { var x = await db.CiteDuties.SingleAsync(y => y.Id == row.Id, Ct); return [x.TeamId, x.Name]; });
                }
                case "moves":
                {
                    var row = TestData.Move(msel.Id, situationTime: now);
                    row.Description = "Stored";
                    row.MoveStartTime = now;
                    row.SituationDescription = "Stored";
                    await Seed(row);
                    return ($"moves/{row.Id}", new { moveNumber = row.MoveNumber },
                        async db => { var x = await db.Moves.SingleAsync(y => y.Id == row.Id, Ct); return [x.Description, x.MoveStartTime, x.SituationTime, x.SituationDescription]; });
                }
                case "invitations":
                {
                    var row = TestData.Invitation(msel.Id, team.Id, emailDomain: "stored.test");
                    row.ExpirationDateTime = now.AddDays(1);
                    row.CiteTeamRole = "Stored";
                    await Seed(row);
                    return ($"invitations/{row.Id}", new { teamId = team.Id, maxUsersAllowed = 10 },
                        async db => { var x = await db.Invitations.SingleAsync(y => y.Id == row.Id, Ct); return [x.EmailDomain, x.ExpirationDateTime, x.CiteTeamRole]; });
                }
                case "playerApplications":
                {
                    var row = TestData.PlayerApplication(msel.Id, icon: "Stored");
                    row.Embeddable = true;
                    row.LoadInBackground = true;
                    await Seed(row);
                    return ($"playerApplications/{row.Id}", new { name = "Application" },
                        async db => { var x = await db.PlayerApplications.SingleAsync(y => y.Id == row.Id, Ct); return [x.Url, x.Icon, x.Embeddable, x.LoadInBackground]; });
                }
                case "teams":
                {
                    team.Description = "Stored";
                    team.CiteTeamTypeId = Guid.NewGuid();
                    team.Email = "stored@example.test";
                    await using (var db = NewContext())
                    {
                        db.Teams.Update(team);
                        await db.SaveChangesAsync(Ct);
                    }
                    return ($"teams/{team.Id}", new { name = "Team", shortName = "T" },
                        async db => { var x = await db.Teams.SingleAsync(y => y.Id == team.Id, Ct); return [x.Description, x.CiteTeamTypeId, x.Email]; });
                }
                case "organizations":
                {
                    var row = TestData.Organization(mselId: msel.Id);
                    row.ShortName = "Stored";
                    row.Description = "Stored";
                    row.Summary = "Stored";
                    row.Email = "stored@example.test";
                    await Seed(row);
                    return ($"organizations/{row.Id}", new { name = "Organization" },
                        async db => { var x = await db.Organizations.SingleAsync(y => y.Id == row.Id, Ct); return [x.ShortName, x.Description, x.Summary, x.Email]; });
                }
                case "dataFields":
                {
                    var row = TestData.DataField(mselId: msel.Id);
                    row.Description = "Stored";
                    row.CellMetadata = "Stored";
                    row.ColumnMetadata = "Stored";
                    row.GalleryArticleParameter = "Stored";
                    await Seed(row);
                    return ($"dataFields/{row.Id}", new { name = "Field", dataType = "String" },
                        async db => { var x = await db.DataFields.SingleAsync(y => y.Id == row.Id, Ct); return [x.Description, x.CellMetadata, x.ColumnMetadata, x.GalleryArticleParameter]; });
                }
                case "dataOptions":
                {
                    var field = TestData.DataField(mselId: msel.Id);
                    await Seed(field);
                    var row = TestData.DataOption(field.Id, "Option", "Stored");
                    row.OptionDescription = "Stored";
                    await Seed(row);
                    return ($"dataOptions/{row.Id}", new { optionName = "Option" },
                        async db => { var x = await db.DataOptions.SingleAsync(y => y.Id == row.Id, Ct); return [x.OptionValue, x.OptionDescription]; });
                }
                case "dataValues":
                {
                    var field = TestData.DataField(mselId: msel.Id);
                    var scenarioEvent = TestData.ScenarioEvent(msel.Id);
                    await Seed(field, scenarioEvent);
                    var row = TestData.DataValue(field.Id, scenarioEvent.Id, "Stored");
                    row.CellMetadata = "Stored";
                    await Seed(row);
                    return ($"dataValues/{row.Id}", new { },
                        async db => { var x = await db.DataValues.SingleAsync(y => y.Id == row.Id, Ct); return [x.Value, x.CellMetadata]; });
                }
                case "mselpages":
                {
                    var row = TestData.MselPage(msel.Id, content: "Stored");
                    await Seed(row);
                    return ($"mselpages/{row.Id}", new { name = "Page" },
                        async db => { var x = await db.MselPages.SingleAsync(y => y.Id == row.Id, Ct); return [x.Content]; });
                }
                case "injects":
                {
                    var required = TestData.Inject(injectType.Id);
                    await Seed(required);
                    var row = TestData.Inject(injectType.Id);
                    row.RequiresInjectId = required.Id;
                    await Seed(row);
                    return ($"injects/{row.Id}", new { id = row.Id, name = "Inject", injectTypeId = injectType.Id },
                        async db => { var x = await db.Injects.SingleAsync(y => y.Id == row.Id, Ct); return [x.Description, x.RequiresInjectId]; });
                }
                case "users":
                {
                    var row = TestData.User(name: "Stored", roleId: TestData.Roles.Observer);
                    await Seed(row);
                    return ($"users/{row.Id}", new { id = row.Id, name = "User" },
                        async db => { var x = await db.Users.SingleAsync(y => y.Id == row.Id, Ct); return [x.RoleId]; });
                }
                case "catalogs":
                {
                    var parent = TestData.Catalog(injectType.Id);
                    await Seed(parent);
                    var row = TestData.Catalog(injectType.Id);
                    row.Description = "Stored";
                    row.ParentId = parent.Id;
                    row.ListDataFields = "Stored";
                    await Seed(row);
                    return ($"catalogs/{row.Id}", new { id = row.Id, name = "Catalog", injectTypeId = injectType.Id },
                        async db => { var x = await db.Catalogs.SingleAsync(y => y.Id == row.Id, Ct); return [x.Description, x.ParentId, x.ListDataFields]; });
                }
                case "units":
                {
                    var row = TestData.Unit(name: "Stored");
                    row.Description = "Stored";
                    await Seed(row);
                    return ($"units/{row.Id}", new { id = row.Id, name = "Unit", shortName = "U" },
                        async db => { var x = await db.Units.SingleAsync(y => y.Id == row.Id, Ct); return [x.Description]; });
                }
                case "groups":
                {
                    var row = TestData.Group("Stored");
                    row.Description = "Stored";
                    await Seed(row);
                    return ($"groups/{row.Id}", new { id = row.Id, name = "Group" },
                        async db => { var x = await db.Groups.SingleAsync(y => y.Id == row.Id, Ct); return [x.Description]; });
                }
                case "proficiencyLevels":
                {
                    var scale = TestData.ProficiencyScale(name: "Scale");
                    await Seed(scale);
                    var row = TestData.ProficiencyLevel(scale.Id, "Level");
                    row.Description = "Stored";
                    await Seed(row);
                    return ($"proficiencyLevels/{row.Id}", new { name = "Level", value = 1 },
                        async db => { var x = await db.ProficiencyLevels.SingleAsync(y => y.Id == row.Id, Ct); return [x.Description]; });
                }
                case "proficiencyScales":
                {
                    var row = TestData.ProficiencyScale(name: "Scale");
                    row.Description = "Stored";
                    await Seed(row);
                    return ($"proficiencyScales/{row.Id}", new { id = row.Id, name = "Scale" },
                        async db => { var x = await db.ProficiencyScales.SingleAsync(y => y.Id == row.Id, Ct); return [x.Description]; });
                }
                case "system-roles":
                {
                    var row = TestData.SystemRole(name: "Role");
                    row.Description = "Stored";
                    await Seed(row);
                    return ($"system-roles/{row.Id}", new { id = row.Id, name = "Role" },
                        async db => { var x = await db.SystemRoles.SingleAsync(y => y.Id == row.Id, Ct); return [x.Description]; });
                }
                case "injectTypes":
                    return ($"injectTypes/{injectType.Id}", new { name = "Type" },
                        async db => { var x = await db.InjectTypes.SingleAsync(y => y.Id == injectType.Id, Ct); return [x.Description]; });
                case "scenarioEvents":
                {
                    var row = TestData.ScenarioEvent(msel.Id);
                    row.RowMetadata = "Stored";
                    row.IntegrationTarget = "Stored";
                    await Seed(row);
                    return ($"scenarioEvents/{row.Id}", new { id = row.Id, mselId = msel.Id, deltaSeconds = row.DeltaSeconds, groupOrder = row.GroupOrder, scenarioEventType = "Inject" },
                        async db => { var x = await db.ScenarioEvents.SingleAsync(y => y.Id == row.Id, Ct); return [x.Information, x.RowMetadata, x.IntegrationTarget]; });
                }
                default:
                {
                    var (_, row) = await SeedEventWithTask(new Dictionary<string, string> { ["key"] = "value" });
                    return ($"scenarioEvents/{row.Id}",
                        new
                        {
                            id = row.Id,
                            mselId = msel.Id,
                            deltaSeconds = row.DeltaSeconds,
                            groupOrder = row.GroupOrder,
                            scenarioEventType = "Inject",
                            integrationTarget = "Steamfitter",
                            steamfitterTask = new { name = "Task", taskType = "Notification", actionParameters = new Dictionary<string, string>() },
                        },
                        async db => { var x = await db.SteamfitterTasks.SingleAsync(y => y.ScenarioEventId == row.Id, Ct); return [x.Description, x.VmMask, x.ApiUrl, x.ExpectedOutput]; });
                }
            }
        }

        var (url, body, read) = await Arrange();

        var response = await RootClient.PutAsJsonAsync($"api/{url}", body, Ct);

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        await using var context = NewContext();
        // SanitizerInterceptor stores a cleared HTML field as an empty string.
        Assert.All(await read(context), x => Assert.True(x is null or "", $"not cleared: {x}"));
    }

    // ---- Related rows on a read -----------------------------------------------------------------

    /// <summary>
    /// A user's catalogs carry each one's parent when the parent is among them, because the list is read into
    /// one context.
    /// </summary>
    [Fact]
    public async Task A_users_catalogs_carry_a_parent_that_is_listed_too()
    {
        var injectType = TestData.InjectType();
        await Seed(injectType);
        var parent = TestData.Catalog(injectType.Id, createdBy: Root.Id);
        await Seed(parent);
        var child = TestData.Catalog(injectType.Id, createdBy: Root.Id);
        child.ParentId = parent.Id;
        await Seed(child);

        var catalogs = await ReadAsync<List<ViewModels.Catalog>>(await RootClient.GetAsync($"api/users/{Root.Id}/catalogs", Ct), JsonOptions);

        var readChild = Assert.Single(catalogs, x => x.Id == child.Id);
        Assert.Equal((parent.Id, parent.Name), (readChild.Parent.Id, readChild.Parent.Name));
        Assert.Null(readChild.Parent.Parent);
        Assert.Null(Assert.Single(catalogs, x => x.Id == parent.Id).Parent);
    }

    /// <summary>
    /// A competency updated to be its own parent is answered with itself as its only child, which the
    /// response's cycle handling writes as null.
    /// </summary>
    [Fact]
    public async Task A_competency_that_is_its_own_parent_is_answered_with_a_null_child()
    {
        var framework = TestData.CompetencyFramework();
        await Seed(framework);
        var competency = TestData.Competency(framework.Id, "SELF");
        await Seed(competency);

        var response = await RootClient.PutAsJsonAsync($"api/competencies/{competency.Id}",
            new { idNumber = "SELF", shortName = "Self", parentId = competency.Id }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(competency.Id, json.RootElement.GetProperty("parentId").GetGuid());
        var child = Assert.Single(json.RootElement.GetProperty("children").EnumerateArray());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, child.ValueKind);
    }

    /// <summary>
    /// A catalog that is its own parent is read with itself as its parent, which the response's cycle
    /// handling writes as null; in a parent cycle each catalog is read with the other as its parent, down to
    /// where the cycle closes.
    /// </summary>
    [Fact]
    public async Task Catalogs_in_a_parent_cycle_are_read_with_the_cycle_cut()
    {
        var injectType = TestData.InjectType();
        await Seed(injectType);
        var self = TestData.Catalog(injectType.Id, createdBy: Root.Id);
        var first = TestData.Catalog(injectType.Id, createdBy: Root.Id);
        var second = TestData.Catalog(injectType.Id, createdBy: Root.Id);
        await Seed(self, first, second);
        await using (var db = NewContext())
        {
            (await db.Catalogs.SingleAsync(x => x.Id == self.Id, Ct)).ParentId = self.Id;
            (await db.Catalogs.SingleAsync(x => x.Id == first.Id, Ct)).ParentId = second.Id;
            (await db.Catalogs.SingleAsync(x => x.Id == second.Id, Ct)).ParentId = first.Id;
            await db.SaveChangesAsync(Ct);
        }

        var one = await RootClient.GetAsync($"api/catalogs/{self.Id}", Ct);
        var all = await RootClient.GetAsync($"api/users/{Root.Id}/catalogs", Ct);

        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        using var oneJson = System.Text.Json.JsonDocument.Parse(await one.Content.ReadAsStringAsync(Ct));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, oneJson.RootElement.GetProperty("parent").ValueKind);
        using var allJson = System.Text.Json.JsonDocument.Parse(await all.Content.ReadAsStringAsync(Ct));
        var rows = allJson.RootElement.EnumerateArray().ToDictionary(x => x.GetProperty("id").GetGuid());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, rows[self.Id].GetProperty("parent").ValueKind);
        var firstParent = rows[first.Id].GetProperty("parent");
        Assert.Equal(second.Id, firstParent.GetProperty("id").GetGuid());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, firstParent.GetProperty("parent").ValueKind);
        var secondParent = rows[second.Id].GetProperty("parent");
        Assert.Equal(first.Id, secondParent.GetProperty("id").GetGuid());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, secondParent.GetProperty("parent").ValueKind);
    }

    /// <summary>A CITE action is read with its team.</summary>
    [Fact]
    public async Task A_cite_action_is_read_with_its_team()
    {
        var msel = TestData.Msel(createdBy: Root.Id);
        await Seed(msel);
        var team = TestData.Team(msel.Id);
        await Seed(team);
        var action = TestData.CiteAction(mselId: msel.Id, teamId: team.Id);
        await Seed(action);

        var read = await ReadAsync<ViewModels.CiteAction>(await RootClient.GetAsync($"api/citeActions/{action.Id}", Ct), JsonOptions);

        Assert.Equal((team.Id, team.Name), (read.Team.Id, read.Team.Name));
    }

    private async Task<(MselEntity Msel, ScenarioEventEntity ScenarioEvent)> SeedEventWithTask(Dictionary<string, string> actionParameters)
    {
        var msel = TestData.Msel(createdBy: Root.Id);
        await Seed(msel);
        var scenarioEvent = TestData.ScenarioEvent(msel.Id);
        scenarioEvent.IntegrationTarget = "Steamfitter";
        await Seed(scenarioEvent);
        var task = TestData.SteamfitterTask(scenarioEvent.Id, actionParameters: actionParameters);
        await Seed(task);

        return (msel, scenarioEvent);
    }
}
