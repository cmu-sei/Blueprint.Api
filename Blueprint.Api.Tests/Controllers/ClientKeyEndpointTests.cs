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

public class ClientKeyEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    [Theory]
    [InlineData("api/cards", "CardService.CreateAsync")]
    [InlineData("api/teamcards", "CardTeamService.CreateAsync")]
    [InlineData("api/cataloginjects", "CatalogInjectService.CreateAsync")]
    [InlineData("api/cataloginjects/multiple", "CatalogInjectService.CreateMultipleAsync")]
    [InlineData("api/catalogs", "CatalogService.CreateAsync")]
    [InlineData("api/catalogunits", "CatalogUnitService.CreateAsync")]
    [InlineData("api/citeActions", "CiteActionService.CreateAsync")]
    [InlineData("api/citeDuties", "CiteDutyService.CreateAsync")]
    [InlineData("api/dataFields", "DataFieldService.CreateAsync")]
    [InlineData("api/dataOptions", "DataOptionService.CreateAsync")]
    [InlineData("api/dataValues", "DataValueService.CreateAsync")]
    [InlineData("api/groups", "GroupService.CreateAsync")]
    [InlineData("api/groups/{0}/memberships", "GroupService.CreateMembershipAsync")]
    [InlineData("api/catalog/{0}/injects", "InjectService.CreateAsync")]
    [InlineData("api/injectTypes", "InjectTypeService.CreateAsync")]
    [InlineData("api/invitations", "InvitationService.CreateAsync")]
    [InlineData("api/moves", "MoveService.CreateAsync")]
    [InlineData("api/mselcompetencies", "MselCompetencyService.CreateAsync")]
    [InlineData("api/mselpages", "MselPageService.CreateAsync")]
    [InlineData("api/msels", "MselService.CreateAsync")]
    [InlineData("api/mselunits", "MselUnitService.CreateAsync")]
    [InlineData("api/organizations", "OrganizationService.CreateAsync")]
    [InlineData("api/playerApplications", "PlayerApplicationService.CreateAsync")]
    [InlineData("api/teamplayerApplications", "PlayerApplicationTeamService.CreateAsync")]
    [InlineData("api/proficiencyLevels", "ProficiencyLevelService.CreateAsync")]
    [InlineData("api/proficiencyScales", "ProficiencyScaleService.CreateAsync")]
    [InlineData("api/system-roles", "SystemRoleService.CreateAsync")]
    [InlineData("api/teamcompetencies", "TeamCompetencyService.CreateAsync")]
    [InlineData("api/teamusers", "TeamUserService.CreateAsync")]
    [InlineData("api/units", "UnitService.CreateAsync")]
    [InlineData("api/unitusers", "UnitUserService.CreateAsync")]
    [InlineData("api/usermselroles", "UserMselRoleService.CreateAsync")]
    [InlineData("api/users", "UserService.CreateAsync")]
    [InlineData("api/userteamroles", "UserTeamRoleService.CreateAsync")]
    public async Task Create_answers_an_existing_client_key_with_a_server_error_and_keeps_the_original_row(
        string route, string service)
    {
        var request = await ExistingKey(route);
        var before = await Scalars(request.Entity.GetType(), request.Id);

        var response = await RootClient.PostAsJsonAsync(request.Route, request.Body, Ct);

        var error = await AssertJsonError<ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("An error occurred while saving the entity changes. See the inner exception for details.", error.Title);
        Assert.Contains(service, error.Detail);
        Assert.Equivalent(before, await Scalars(request.Entity.GetType(), request.Id), strict: true);
    }

    private async Task<Dictionary<string, object>> Scalars(Type type, Guid id)
    {
        await using var context = NewContext();
        var row = await context.FindAsync(type, [id], Ct);
        Assert.NotNull(row);
        return context.Entry(row).Properties.ToDictionary(p => p.Metadata.Name, p => p.CurrentValue);
    }

    private async Task<(string Route, Guid Id, object Entity, object Body)> ExistingKey(string route)
    {
        var msel = TestData.Msel();
        var team = TestData.Team(msel.Id);
        var otherTeam = TestData.Team(msel.Id);
        var unit = TestData.Unit();
        var otherUnit = TestData.Unit();
        var user = TestData.User();
        var otherUser = TestData.User();
        var type = TestData.InjectType();
        var catalog = TestData.Catalog(type.Id);
        var otherCatalog = TestData.Catalog(type.Id);
        var inject = TestData.Inject(type.Id);
        var card = TestData.Card(msel.Id);
        var application = TestData.PlayerApplication(msel.Id);
        var field = TestData.DataField(msel.Id);
        var scenarioEvent = TestData.ScenarioEvent(msel.Id);
        var framework = TestData.CompetencyFramework();
        var competency = TestData.Competency(framework.Id);
        var otherCompetency = TestData.Competency(framework.Id);
        var scale = TestData.ProficiencyScale();
        var group = TestData.Group();
        await Seed(msel, team, otherTeam, unit, otherUnit, user, otherUser, type, catalog, otherCatalog,
            inject, card, application, field, scenarioEvent, framework, competency, otherCompetency, scale, group);

        (object entity, object body) = route switch
        {
            "api/cards" => Pair(TestData.Card(msel.Id), new { mselId = msel.Id, name = "new card" }),
            "api/teamcards" => Pair(TestData.CardTeam(card.Id, team.Id), new { cardId = card.Id, teamId = otherTeam.Id }),
            "api/cataloginjects" or "api/cataloginjects/multiple" => Pair(TestData.CatalogInject(catalog.Id, inject.Id), new { catalogId = otherCatalog.Id, injectId = inject.Id }),
            "api/catalogs" => Pair(TestData.Catalog(type.Id), new { injectTypeId = type.Id, name = "new catalog" }),
            "api/catalogunits" => Pair(TestData.CatalogUnit(unit.Id, catalog.Id), new { unitId = otherUnit.Id, catalogId = catalog.Id }),
            "api/citeActions" => Pair(TestData.CiteAction(msel.Id, team.Id), new { mselId = msel.Id, teamId = team.Id }),
            "api/citeDuties" => Pair(TestData.CiteDuty(msel.Id, team.Id), new { mselId = msel.Id, teamId = team.Id, name = "new duty" }),
            "api/dataFields" => Pair(TestData.DataField(msel.Id), new { mselId = msel.Id, name = "new field", dataType = DataFieldType.String, displayOrder = 2 }),
            "api/dataOptions" => Pair(TestData.DataOption(field.Id), new { dataFieldId = field.Id, optionName = "new option" }),
            "api/dataValues" => Pair(TestData.DataValue(field.Id, scenarioEvent.Id, "before"), new { dataFieldId = field.Id, scenarioEventId = scenarioEvent.Id, value = "after" }),
            "api/groups" => Pair(TestData.Group(), new { name = "new group" }),
            "api/groups/{0}/memberships" => Pair(TestData.GroupMembership(group.Id, user.Id), new { groupId = group.Id, userId = otherUser.Id }),
            "api/catalog/{0}/injects" => Pair(TestData.Inject(type.Id), new { injectTypeId = type.Id, name = "new inject" }),
            "api/injectTypes" => Pair(TestData.InjectType(), new { name = "new type" }),
            "api/invitations" => Pair(TestData.Invitation(msel.Id, team.Id), new { mselId = msel.Id, teamId = team.Id }),
            "api/moves" => Pair(TestData.Move(msel.Id), new { mselId = msel.Id, moveNumber = 1 }),
            "api/mselcompetencies" => Pair(TestData.MselCompetency(msel.Id, competency.Id), new { mselId = msel.Id, competencyId = otherCompetency.Id }),
            "api/mselpages" => Pair(TestData.MselPage(msel.Id), new { mselId = msel.Id, name = "new page" }),
            "api/msels" => Pair(TestData.Msel(), new { name = "new msel" }),
            "api/mselunits" => Pair(TestData.MselUnit(unit.Id, msel.Id), new { mselId = msel.Id, unitId = otherUnit.Id }),
            "api/organizations" => Pair(TestData.Organization(msel.Id), new { mselId = msel.Id, name = "new organization" }),
            "api/playerApplications" => Pair(TestData.PlayerApplication(msel.Id), new { mselId = msel.Id, name = "new application" }),
            "api/teamplayerApplications" => Pair(TestData.PlayerApplicationTeam(application.Id, team.Id), new { playerApplicationId = application.Id, teamId = otherTeam.Id }),
            "api/proficiencyLevels" => Pair(TestData.ProficiencyLevel(scale.Id), new { proficiencyScaleId = scale.Id, name = "new level" }),
            "api/proficiencyScales" => Pair(TestData.ProficiencyScale(), new { name = "new scale" }),
            "api/system-roles" => Pair(TestData.SystemRole(), new { name = "new role", permissions = Array.Empty<SystemPermission>() }),
            "api/teamcompetencies" => Pair(TestData.TeamCompetency(team.Id, competency.Id), new { teamId = team.Id, competencyId = otherCompetency.Id }),
            "api/teamusers" => Pair(TestData.TeamUser(user.Id, team.Id), new { userId = otherUser.Id, teamId = team.Id }),
            "api/units" => Pair(TestData.Unit(), new { name = "new unit" }),
            "api/unitusers" => Pair(TestData.UnitUser(user.Id, unit.Id), new { userId = otherUser.Id, unitId = unit.Id }),
            "api/usermselroles" => Pair(TestData.UserMselRole(user.Id, msel.Id), new { userId = otherUser.Id, mselId = msel.Id, role = MselRole.Viewer }),
            "api/users" => Pair(TestData.User(), new { name = "new user" }),
            "api/userteamroles" => Pair(TestData.UserTeamRole(user.Id, team.Id), new { userId = otherUser.Id, teamId = team.Id, role = "Submitter" }),
            _ => throw new ArgumentOutOfRangeException(nameof(route))
        };
        await Seed(entity);
        var id = (Guid)Db.Entry(entity).Property("Id").CurrentValue;
        var payload = System.Text.Json.JsonSerializer.SerializeToNode(body).AsObject();
        payload["id"] = id;
        var requestBody = route == "api/cataloginjects/multiple" ? (object)new[] { payload } : payload;
        var resolvedRoute = string.Format(route, route == "api/groups/{0}/memberships" ? group.Id : catalog.Id);
        return (resolvedRoute, id, entity, requestBody);
    }

    private static (object Entity, object Body) Pair(object entity, object body) => (entity, body);
}
