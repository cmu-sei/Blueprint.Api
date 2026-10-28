// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

public class MissingParentEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    [Theory]
    [InlineData("api/teams", "TeamService.CreateAsync")]
    [InlineData("api/scenarioEvents", "ScenarioEventService.CreateAsync")]
    [InlineData("api/cards", "CardService.CreateAsync")]
    [InlineData("api/organizations", "OrganizationService.CreateAsync")]
    public async Task Create_with_EditMsels_answers_a_missing_Msel_with_a_server_error_and_saves_nothing(string route, string service)
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var id = Guid.NewGuid();
        var body = new { id, mselId = Guid.NewGuid(), name = "missing parent", isTemplate = false, scenarioEventType = EventType.Inject };

        var response = await Client(actor).PostAsJsonAsync(route, body, Ct);

        var failure = await AssertJsonError<ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("An error occurred while saving the entity changes. See the inner exception for details.", failure.Title);
        Assert.Contains(service, failure.Detail);
        Assert.False(await ReadBack(db => db.Teams.AnyAsync(x => x.Id == id, Ct)));
        Assert.False(await ReadBack(db => db.ScenarioEvents.AnyAsync(x => x.Id == id, Ct)));
        Assert.False(await ReadBack(db => db.Cards.AnyAsync(x => x.Id == id, Ct)));
        Assert.False(await ReadBack(db => db.Organizations.AnyAsync(x => x.Id == id, Ct)));
    }
}
