// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>Six of the eight <c>scenarioEvents</c> routes: the rows of an MSEL's timeline, each one an
/// event the exercise delivers at a point in time. The two bulk routes - <c>fromInjects</c> and <c>copy</c>
/// - and the <c>GroupOrder</c> reordering contract have files of their own.</summary>
public class ScenarioEventEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET msels/{mselId}/scenarioEvents
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByMsel_ReturnsEveryEventOnTheMsel()
    {
        var msel = await SeedMsel();
        await SeedEvent(msel, information: "first");
        await SeedEvent(msel, deltaSeconds: 60, information: "second");
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var events = await GetEvents(Client(actor), msel.Id);

        Assert.Equal(
            ["first", "second"],
            events.Select(x => x.Information).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task GetByMsel_DoesNotReturnAnotherMselsEvents()
    {
        var msel = await SeedMsel();
        var mine = await SeedEvent(msel);
        await SeedEvent(await SeedMsel());
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var events = await GetEvents(Client(actor), msel.Id);

        Assert.Equal(mine.Id, Assert.Single(events).Id);
    }

    /// <summary>
    /// The timeline comes back in the order the grid draws it: by time, then by position within the
    /// events sharing a time.
    /// </summary>
    [Fact]
    public async Task GetByMsel_OrdersByDeltaSecondsThenGroupOrder()
    {
        var msel = await SeedMsel();
        await SeedEvent(msel, deltaSeconds: 100, groupOrder: 1, information: "c");
        await SeedEvent(msel, deltaSeconds: 0, groupOrder: 2, information: "b");
        await SeedEvent(msel, deltaSeconds: 0, groupOrder: 1, information: "a");
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var events = await GetEvents(Client(actor), msel.Id);

        Assert.Equal(["a", "b", "c"], events.Select(x => x.Information));
    }

    /// <summary>Get by MSEL omits the cells that the by id route includes.</summary>
    [Fact]
    public async Task GetByMsel_OmitsTheCellsThatTheByIdRouteIncludes()
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, MselRole.Viewer).SeedAsync();

        var events = await GetEvents(Client(actor), row.Msel.Id);

        Assert.Empty(Assert.Single(events).DataValues);

        var byId = await GetEvent(Client(actor), row.Event.Id);

        Assert.Equal(row.Value.Id, Assert.Single(byId.DataValues).Id);
    }

    /// <summary>Get by MSEL the roles that may read the timeline.</summary>
    [Theory]
    [InlineData(MselRole.Owner, HttpStatusCode.OK)]
    [InlineData(MselRole.Editor, HttpStatusCode.OK)]
    [InlineData(MselRole.Approver, HttpStatusCode.OK)]
    [InlineData(MselRole.MoveEditor, HttpStatusCode.OK)]
    [InlineData(MselRole.Viewer, HttpStatusCode.OK)]
    [InlineData(MselRole.Evaluator, HttpStatusCode.Forbidden)]
    public async Task GetByMsel_TheRolesThatMayReadTheTimeline(MselRole role, HttpStatusCode expected)
    {
        var msel = await SeedMsel();
        await SeedEvent(msel);
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/scenarioEvents", Ct);

        Assert.Equal(expected, response.StatusCode);
    }

    /// <summary>
    /// A member of one of the MSEL's teams reads the timeline holding no role at all, which is what makes
    /// it visible to the people playing the exercise.
    /// </summary>
    [Fact]
    public async Task GetByMsel_ForAMemberOfOneOfTheMselsTeams_Is200()
    {
        var msel = await SeedMsel();
        var scenarioEvent = await SeedEvent(msel);
        var team = TestData.Team(msel.Id);
        await Seed(team);
        var actor = await Actor().OnTeam(team).SeedAsync();

        var events = await GetEvents(Client(actor), msel.Id);

        Assert.Equal(scenarioEvent.Id, Assert.Single(events).Id);
    }

    /// <summary>
    /// Membership of a unit the MSEL is assigned to is not enough on its own, unlike on the cell list.
    /// </summary>
    [Fact]
    public async Task GetByMsel_ForAUnitMemberWithNoRole_Is403()
    {
        var msel = await SeedMsel();
        await SeedEvent(msel);
        var actor = await Actor().InUnitOf(msel).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/scenarioEvents", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetByMsel_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/scenarioEvents", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetByMsel_WithViewMsels_Is200()
    {
        var msel = await SeedMsel();
        var scenarioEvent = await SeedEvent(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var events = await GetEvents(Client(actor), msel.Id);

        Assert.Equal(scenarioEvent.Id, Assert.Single(events).Id);
    }

    /// <summary><c>CreateMsels</c> reads a template's timeline and no other MSEL's.</summary>
    [Fact]
    public async Task GetByMsel_ForATemplate_WithCreateMselsOnly_Is200_WhileAnOrdinaryMselIs403()
    {
        var template = await SeedMsel(isTemplate: true);
        await SeedEvent(template);
        var ordinary = await SeedMsel();
        await SeedEvent(ordinary);
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var events = await GetEvents(Client(actor), template.Id);

        Assert.Single(events);

        var response = await Client(actor).GetAsync($"/api/msels/{ordinary.Id}/scenarioEvents", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetByMsel_ForATemplate_is_forbidden_for_a_caller_holding_only_EditMsels()
    {
        var template = await SeedMsel(isTemplate: true);
        await SeedEvent(template);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{template.Id}/scenarioEvents", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>An unknown MSEL is a 403: this route's requirement guards its lookup.</summary>
    [Fact]
    public async Task GetByMsel_ForAnUnknownMsel_Is403()
    {
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{Guid.NewGuid()}/scenarioEvents", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetByMsel_ForAnUnknownMsel_WithViewMsels_IsAnEmptyList()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var events = await GetEvents(Client(actor), Guid.NewGuid());

        Assert.Empty(events);
    }

    // ---------------------------------------------------------------------------------------------
    // GET scenarioEvents/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ReturnsTheRowAndItsCells()
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, MselRole.Viewer).SeedAsync();

        var scenarioEvent = await GetEvent(Client(actor), row.Event.Id);

        Assert.Equal(row.Msel.Id, scenarioEvent.MselId);
        Assert.Equal(row.Event.Information, scenarioEvent.Information);
        Assert.Equal(EventType.Inject, scenarioEvent.ScenarioEventType);

        var cell = Assert.Single(scenarioEvent.DataValues);

        Assert.Equal(row.Value.Id, cell.Id);
        Assert.Equal("before", cell.Value);
    }

    /// <summary>Get for an unknown id is answered with a 500, a ViewMsels holder's included.</summary>
    [Fact]
    public async Task Get_ForAnUnknownId_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/scenarioEvents/{Guid.NewGuid()}", Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Sequence contains no elements.", failure.Title);
        Assert.Contains("ScenarioEventService.GetAsync", failure.Detail);
    }

    [Fact]
    public async Task Get_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var row = await SeedRow();
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/scenarioEvents/{row.Event.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_ForAMemberOfOneOfTheMselsTeams_Is200()
    {
        var row = await SeedRow();
        var team = TestData.Team(row.Msel.Id);
        await Seed(team);
        var actor = await Actor().OnTeam(team).SeedAsync();

        var scenarioEvent = await GetEvent(Client(actor), row.Event.Id);

        Assert.Equal(row.Event.Id, scenarioEvent.Id);
    }

    [Fact]
    public async Task Get_WithViewMsels_Is200()
    {
        var row = await SeedRow();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var scenarioEvent = await GetEvent(Client(actor), row.Event.Id);

        Assert.Equal(row.Event.Id, scenarioEvent.Id);
    }

    [Fact]
    public async Task Get_on_a_template_with_CreateMsels_is_200()
    {
        var msel = await SeedMsel(isTemplate: true);
        var scenarioEvent = await SeedEvent(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var answered = await GetEvent(Client(actor), scenarioEvent.Id);

        Assert.Equal(scenarioEvent.Id, answered.Id);
    }

    [Fact]
    public async Task Get_on_a_template_is_forbidden_for_a_caller_holding_only_EditMsels()
    {
        var msel = await SeedMsel(isTemplate: true);
        var scenarioEvent = await SeedEvent(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/scenarioEvents/{scenarioEvent.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_on_a_msel_that_is_not_a_template_is_forbidden_for_a_caller_holding_only_CreateMsels()
    {
        var row = await SeedRow();
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/scenarioEvents/{row.Event.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_ForTheMselsCreator_Is200()
    {
        var creator = await Actor().SeedAsync();
        var row = await SeedRow(createdBy: creator.Id);

        var scenarioEvent = await GetEvent(Client(creator), row.Event.Id);

        Assert.Equal(row.Event.Id, scenarioEvent.Id);
    }

    // ---------------------------------------------------------------------------------------------
    // POST scenarioEvents
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_StoresTheRow()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(
            Client(actor),
            Body(msel.Id, deltaSeconds: 300, information: "new row") with
            {
                IsHidden = true,
                IntegrationTarget = "gallery"
            });

        var created = Assert.Single(await Read<List<ScenarioEvent>>(response));
        var stored = await Stored(created.Id);

        Assert.Equal(msel.Id, stored.MselId);
        Assert.Equal("new row", stored.Information);
        Assert.Equal(300, stored.DeltaSeconds);
        Assert.True(stored.IsHidden);
        Assert.Equal("gallery", stored.IntegrationTarget);
        Assert.Equal(EventType.Inject, stored.ScenarioEventType);
    }

    /// <summary>The create answers 200 with every row it renumbered, and no <c>Location</c>.</summary>
    [Fact]
    public async Task Create_Is200WithAListAndNoLocationHeader()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Single(await Read<List<ScenarioEvent>>(response));
    }

    /// <summary>
    /// Adding a row is owner-only: the roles that may change a row are not the roles that may add one.
    /// </summary>
    [Theory]
    [InlineData(MselRole.Owner, HttpStatusCode.OK)]
    [InlineData(MselRole.Editor, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Approver, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.MoveEditor, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Viewer, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Evaluator, HttpStatusCode.Forbidden)]
    public async Task Create_TheRolesThatMayAddARow(MselRole role, HttpStatusCode expected)
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id));

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(expected == HttpStatusCode.OK ? 1 : 0, await CountOn(msel.Id));
    }

    [Fact]
    public async Task Create_ForTheMselsCreator_Is200()
    {
        var creator = await Actor().SeedAsync();
        var msel = await SeedMsel(createdBy: creator.Id);

        var response = await Post(Client(creator), Body(msel.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithEditMsels_Is200()
    {
        var msel = await SeedMsel();
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Create for an unknown MSEL is answered with a 500.</summary>
    [Fact]
    public async Task Create_ForAnUnknownMsel_Is500()
    {
        var actor = await Actor().SeedAsync();

        var response = await Post(Client(actor), Body(Guid.NewGuid()));

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Object reference not set to an instance of an object.", failure.Title);
        Assert.Contains("MselOwnerRequirement.IsMet", failure.Detail);
    }

    /// <summary>
    /// A new row arrives with one cell per data field on the MSEL, whether the caller filled it in or not,
    /// so the grid is rectangular from the moment the row exists.
    /// </summary>
    [Fact]
    public async Task Create_AddsACellForEveryDataFieldOnTheMsel()
    {
        var msel = await SeedMsel();
        var first = TestData.DataField(mselId: msel.Id, displayOrder: 1);
        var second = TestData.DataField(mselId: msel.Id, displayOrder: 2);
        var third = TestData.DataField(mselId: msel.Id, displayOrder: 3);
        await Seed(first, second, third);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(
            Client(actor),
            Body(msel.Id) with
            {
                DataValues = [new ValueBody { DataFieldId = second.Id, Value = "typed" }]
            });
        var created = Assert.Single(await Read<List<ScenarioEvent>>(response));
        var cells = await CellsOn(created.Id);
        var byField = cells.ToDictionary(x => x.DataFieldId, x => x.Value);

        Assert.Equal(3, byField.Count);
        Assert.Null(byField[first.Id]);
        Assert.Equal("typed", byField[second.Id]);
        Assert.Null(byField[third.Id]);
        Assert.All(cells, x => Assert.Equal(actor.Id, x.CreatedBy));
    }

    /// <summary>A cell naming another MSEL's data field contributes nothing to the created row.</summary>
    [Fact]
    public async Task Create_IgnoresACellForAnotherMselsDataField()
    {
        var msel = await SeedMsel();
        var mine = TestData.DataField(mselId: msel.Id);
        var elsewhere = TestData.DataField(mselId: (await SeedMsel()).Id);
        await Seed(mine, elsewhere);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(
            Client(actor),
            Body(msel.Id) with
            {
                DataValues =
                [
                    new ValueBody { DataFieldId = mine.Id, Value = "kept" },
                    new ValueBody { DataFieldId = elsewhere.Id, Value = "dropped" }
                ]
            });
        var created = Assert.Single(await Read<List<ScenarioEvent>>(response));
        var cell = Assert.Single(await CellsOn(created.Id));

        Assert.Equal(mine.Id, cell.DataFieldId);
        Assert.Equal("kept", cell.Value);
    }

    [Fact]
    public async Task Create_StampsTheAuditFieldsOnTheServer()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var before = DateTime.UtcNow;

        var response = await Post(
            Client(actor),
            Body(msel.Id) with
            {
                CreatedBy = Guid.NewGuid(),
                DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ModifiedBy = Guid.NewGuid(),
                DateModified = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });
        var stored = await Stored(Assert.Single(await Read<List<ScenarioEvent>>(response)).Id);

        Assert.Equal(actor.Id, stored.CreatedBy);
        Assert.InRange(stored.DateCreated, before, DateTime.UtcNow);
        Assert.Null(stored.ModifiedBy);
        Assert.Null(stored.DateModified);
    }

    [Fact]
    public async Task Create_MarksTheMselModified()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var before = DateTime.UtcNow;

        await Post(Client(actor), Body(msel.Id));

        var stored = await StoredMsel(msel.Id);

        Assert.Equal(actor.Id, stored.ModifiedBy);
        Assert.NotNull(stored.DateModified);
        Assert.InRange(stored.DateModified.Value, before, DateTime.UtcNow);
    }

    /// <summary>
    /// The client may choose the row's id, which is how a grid that has already drawn a row can address
    /// its cells before the server has answered.
    /// </summary>
    [Fact]
    public async Task Create_WithAnIdInTheBody_UsesIt()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var id = Guid.NewGuid();

        var response = await Post(Client(actor), Body(msel.Id, id: id));
        var created = Assert.Single(await Read<List<ScenarioEvent>>(response));

        Assert.Equal(id, created.Id);
        Assert.NotNull(await Stored(id));
    }

    /// <summary>Create with the id of an existing row is answered with a 500.</summary>
    [Fact]
    public async Task Create_WithTheIdOfAnExistingRow_Is500()
    {
        var msel = await SeedMsel();
        var existing = await SeedEvent(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id, id: existing.Id, deltaSeconds: 60));

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("An error occurred while saving the entity changes. See the inner exception for details.", failure.Title);
        Assert.Contains("ScenarioEventService.CreateAsync", failure.Detail);
    }

    [Fact]
    public async Task Create_BroadcastsToTheMselAndTheAdminGroup()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id, information: "broadcast"));
        var created = Assert.Single(await Read<List<ScenarioEvent>>(response));

        Assert.Equal(
            [msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.ScenarioEventCreated, msel.Id));

        var sent = (ScenarioEvent)Hub.Of(MainHubMethods.ScenarioEventCreated, msel.Id)[0].Payload;

        Assert.Equal(created.Id, sent.Id);
        Assert.Equal("broadcast", sent.Information);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT scenarioEvents/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_StoresTheRowAndStampsTheAuditFields()
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, MselRole.Editor).SeedAsync();
        var before = DateTime.UtcNow;

        var response = await Put(
            Client(actor),
            row.Event.Id,
            BodyFor(row.Event) with
            {
                Information = "after",
                IsHidden = true,
                ModifiedBy = Guid.NewGuid(),
                DateModified = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await Stored(row.Event.Id);

        Assert.Equal("after", stored.Information);
        Assert.True(stored.IsHidden);
        Assert.Equal(actor.Id, stored.ModifiedBy);
        Assert.NotNull(stored.DateModified);
        Assert.InRange(stored.DateModified.Value, before, DateTime.UtcNow);
        Assert.Equal(row.Event.CreatedBy, stored.CreatedBy);
    }

    /// <summary>
    /// Changing a row is owner-or-approver-or-editor, so the two roles that cannot add or remove a row can
    /// rewrite every one that exists.
    /// </summary>
    [Theory]
    [InlineData(MselRole.Owner, HttpStatusCode.OK)]
    [InlineData(MselRole.Approver, HttpStatusCode.OK)]
    [InlineData(MselRole.Editor, HttpStatusCode.OK)]
    [InlineData(MselRole.MoveEditor, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Viewer, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Evaluator, HttpStatusCode.Forbidden)]
    public async Task Update_TheRolesThatMayChangeARow(MselRole role, HttpStatusCode expected)
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, role).SeedAsync();

        var response = await Put(
            Client(actor),
            row.Event.Id,
            BodyFor(row.Event) with { Information = "after" });

        Assert.Equal(expected, response.StatusCode);

        if (expected == HttpStatusCode.Forbidden)
        {
            Assert.Equal("No update permissions", (await ReadError(response)).Title);
        }

        Assert.Equal(
            expected == HttpStatusCode.OK ? "after" : row.Event.Information,
            (await Stored(row.Event.Id)).Information);
    }

    [Fact]
    public async Task Update_WithEditMsels_Is200()
    {
        var row = await SeedRow();
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Put(
            Client(actor),
            row.Event.Id,
            BodyFor(row.Event) with { Information = "after" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Update_ForAnUnknownId_Is404()
    {
        var msel = await SeedMsel();
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Put(Client(actor), Guid.NewGuid(), Body(msel.Id, id: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Update chooses its permission branch from the request body and moves the row.</summary>
    [Fact]
    public async Task Update_ChoosesItsPermissionBranchFromTheRequestBodyAndMovesTheRow()
    {
        var row = await SeedRow();
        var mine = await SeedMsel();
        var actor = await Actor().OnMsel(mine, MselRole.Owner).SeedAsync();

        var response = await Put(
            Client(actor),
            row.Event.Id,
            BodyFor(row.Event) with { MselId = mine.Id, Information = "taken" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await Stored(row.Event.Id);

        Assert.Equal(mine.Id, stored.MselId);
        Assert.Equal("taken", stored.Information);
        Assert.Equal(row.Field.Id, Assert.Single(await CellsOn(row.Event.Id)).DataFieldId);
        Assert.NotNull((await StoredMsel(mine.Id)).DateModified);
        Assert.Null((await StoredMsel(row.Msel.Id)).DateModified);
    }

    /// <summary>Update with a different id in the body is answered with a 500.</summary>
    [Fact]
    public async Task Update_WithADifferentIdInTheBody_Is500()
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, MselRole.Editor).SeedAsync();

        var response = await Put(
            Client(actor),
            row.Event.Id,
            BodyFor(row.Event) with { Id = Guid.NewGuid() });

        Assert.Equal("The property 'ScenarioEventEntity.Id' is part of a key and so cannot be modified or marked as modified. To change the principal of an existing entity with an identifying foreign key, first delete the dependent and invoke 'SaveChanges', and then associate the dependent with the new principal.", (await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
    }

    [Fact]
    public async Task Update_StoresTheValuesOfTheCellsOnTheRow()
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, MselRole.Editor).SeedAsync();

        var response = await Put(
            Client(actor),
            row.Event.Id,
            BodyFor(row.Event) with { DataValues = [Cell(row.Value) with { Value = "after" }] });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var cell = Assert.Single(await CellsOn(row.Event.Id));

        Assert.Equal("after", cell.Value);
        Assert.Equal(actor.Id, cell.ModifiedBy);
    }

    /// <summary>Update a cell that does not name its row is ignored.</summary>
    [Fact]
    public async Task Update_ACellThatDoesNotNameItsRow_IsIgnored()
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, MselRole.Editor).SeedAsync();

        var response = await Put(
            Client(actor),
            row.Event.Id,
            BodyFor(row.Event) with
            {
                DataValues = [Cell(row.Value) with { ScenarioEventId = null, Value = "after" }]
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("before", Assert.Single(await CellsOn(row.Event.Id)).Value);
    }

    /// <summary>
    /// A PUT fills in a cell the row never had, which is how a data field added after the row was created
    /// gets a value.
    /// </summary>
    [Fact]
    public async Task Update_AddsACellTheRowDidNotHave()
    {
        var row = await SeedRow();
        var added = TestData.DataField(mselId: row.Msel.Id, displayOrder: 2);
        await Seed(added);
        var actor = await Actor().OnMsel(row.Msel, MselRole.Editor).SeedAsync();

        var response = await Put(
            Client(actor),
            row.Event.Id,
            BodyFor(row.Event) with
            {
                DataValues =
                [
                    new ValueBody
                    {
                        DataFieldId = added.Id,
                        ScenarioEventId = row.Event.Id,
                        Value = "typed"
                    }
                ]
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var cells = await CellsOn(row.Event.Id);

        Assert.Equal(2, cells.Count);

        var cell = cells.Single(x => x.DataFieldId == added.Id);

        Assert.Equal("typed", cell.Value);
        Assert.Equal(actor.Id, cell.CreatedBy);
        Assert.Equal("FFFFFF,0,normal,0", cell.CellMetadata);
    }

    /// <summary>Update overwrites the metadata of every cell on the row.</summary>
    [Fact]
    public async Task Update_OverwritesTheMetadataOfEveryCellOnTheRow()
    {
        var row = await SeedRow();
        row.Value.CellMetadata = "FF0000,0.7,bold,0";
        await Db.SaveChangesAsync(Ct);
        var actor = await Actor().OnMsel(row.Msel, MselRole.Editor).SeedAsync();

        var response = await Put(
            Client(actor),
            row.Event.Id,
            BodyFor(row.Event) with { Information = "after" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var cell = Assert.Single(await CellsOn(row.Event.Id));

        Assert.Equal("FFFFFF,0,normal,0", cell.CellMetadata);
        Assert.Equal("before", cell.Value);
    }

    /// <summary>Update translates row metadata into cell metadata.</summary>
    [Theory]
    [InlineData(null, "FFFFFF,0,normal,0")]
    [InlineData("", "FFFFFF,0,normal,0")]
    [InlineData("20,255,255,255", "FFFFFF,0.7,normal,0")]
    [InlineData("20,255,0,0", "FF00,0.7,normal,0")]
    [InlineData("20,0,0,0", "000,0.7,normal,0")]
    [InlineData("20,1,2,3", "123,0.7,normal,0")]
    [InlineData("20,10,11,12", "ABC,0.7,normal,0")]
    [InlineData("20,255,255", "FFFFFF,0.7,normal,0")]
    [InlineData("nonsense", "FFFFFF,0.7,normal,0")]
    public async Task Update_TranslatesRowMetadataIntoCellMetadata(string rowMetadata, string expected)
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, MselRole.Editor).SeedAsync();

        var response = await Put(
            Client(actor),
            row.Event.Id,
            BodyFor(row.Event) with { RowMetadata = rowMetadata });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, Assert.Single(await CellsOn(row.Event.Id)).CellMetadata);
    }

    /// <summary>Update with non numeric row metadata is answered with a 500.</summary>
    [Fact]
    public async Task Update_WithNonNumericRowMetadata_Is500()
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, MselRole.Editor).SeedAsync();

        var response = await Put(
            Client(actor),
            row.Event.Id,
            BodyFor(row.Event) with { RowMetadata = "20,red,green,blue" });

        Assert.Equal("The input string 'red' was not in a correct format.", (await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
    }

    /// <summary>Update with two cells for one data field is answered with a 500.</summary>
    [Fact]
    public async Task Update_WithTwoCellsForOneDataField_Is500()
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, MselRole.Editor).SeedAsync();

        var response = await Put(
            Client(actor),
            row.Event.Id,
            BodyFor(row.Event) with
            {
                DataValues =
                [
                    Cell(row.Value) with { Value = "one" },
                    Cell(row.Value) with { Id = Guid.NewGuid(), Value = "two" }
                ]
            });

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Sequence contains more than one matching element", failure.Title);
        Assert.Contains("ScenarioEventService.UpdateAsync", failure.Detail);
    }

    /// <summary>
    /// The update broadcast names the properties that changed, camel-cased for the client.
    /// </summary>
    [Fact]
    public async Task Update_BroadcastsToTheMselAndTheAdminGroup()
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, MselRole.Editor).SeedAsync();

        await Put(Client(actor), row.Event.Id, BodyFor(row.Event) with { Information = "after" });

        Assert.Equal(
            [row.Msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.ScenarioEventUpdated, row.Msel.Id));

        var send = Hub.Of(MainHubMethods.ScenarioEventUpdated, row.Msel.Id)[0];
        var sent = (ScenarioEvent)send.Payload;

        Assert.Equal(row.Event.Id, sent.Id);
        Assert.Equal("after", sent.Information);
        Assert.Contains("information", (string[])send.Arguments[1]);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE scenarioEvents/{id}
    // ---------------------------------------------------------------------------------------------

    /// <summary>Delete removes the row and answers 200 with true.</summary>
    [Fact]
    public async Task Delete_RemovesTheRowAndAnswers200WithTrue()
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/scenarioEvents/{row.Event.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await Read<bool>(response));
        Assert.Null(await Stored(row.Event.Id));
    }

    /// <summary>Delete removes the cells without broadcasting it.</summary>
    [Fact]
    public async Task Delete_RemovesTheCellsWithoutBroadcastingIt()
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, MselRole.Owner).SeedAsync();

        await Client(actor).DeleteAsync($"/api/scenarioEvents/{row.Event.Id}", Ct);

        Assert.Empty(await CellsOn(row.Event.Id));
        Assert.Empty(Hub.Of(MainHubMethods.DataValueDeleted, row.Msel.Id, row.Event.Id));
    }

    [Fact]
    public async Task Delete_ForAnUnknownId_Is404()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/scenarioEvents/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Removing a row is owner-only, like adding one - so an editor may empty every cell on a row and not
    /// remove it.
    /// </summary>
    [Theory]
    [InlineData(MselRole.Owner, HttpStatusCode.OK)]
    [InlineData(MselRole.Editor, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Approver, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.MoveEditor, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Viewer, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Evaluator, HttpStatusCode.Forbidden)]
    public async Task Delete_TheRolesThatMayRemoveARow(MselRole role, HttpStatusCode expected)
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, role).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/scenarioEvents/{row.Event.Id}", Ct);

        Assert.Equal(expected, response.StatusCode);

        if (expected == HttpStatusCode.OK)
        {
            Assert.Null(await Stored(row.Event.Id));
        }
        else
        {
            Assert.NotNull(await Stored(row.Event.Id));
        }
    }

    [Fact]
    public async Task Delete_WithEditMsels_Is200()
    {
        var row = await SeedRow();
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/scenarioEvents/{row.Event.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Delete_MarksTheMselModified()
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, MselRole.Owner).SeedAsync();
        var before = DateTime.UtcNow;

        await Client(actor).DeleteAsync($"/api/scenarioEvents/{row.Event.Id}", Ct);

        var stored = await StoredMsel(row.Msel.Id);

        Assert.Equal(actor.Id, stored.ModifiedBy);
        Assert.NotNull(stored.DateModified);
        Assert.InRange(stored.DateModified.Value, before, DateTime.UtcNow);
    }

    [Fact]
    public async Task Delete_BroadcastsTheIdToTheMselAndTheAdminGroup()
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, MselRole.Owner).SeedAsync();

        await Client(actor).DeleteAsync($"/api/scenarioEvents/{row.Event.Id}", Ct);

        Assert.Equal(
            [row.Msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.ScenarioEventDeleted, row.Msel.Id));
        Assert.Equal(row.Event.Id, Hub.Of(MainHubMethods.ScenarioEventDeleted, row.Msel.Id)[0].Payload);
    }

    // ---------------------------------------------------------------------------------------------
    // POST scenarioEvents/batchDelete
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task BatchDelete_RemovesEveryRow()
    {
        var msel = await SeedMsel();
        var first = await SeedEvent(msel);
        var second = await SeedEvent(msel, deltaSeconds: 60);
        var kept = await SeedEvent(msel, deltaSeconds: 120);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await BatchDelete(Client(actor), first.Id, second.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await Read<bool>(response));
        Assert.Equal(kept.Id, (await Stored(kept.Id)).Id);
        Assert.Null(await Stored(first.Id));
        Assert.Null(await Stored(second.Id));
    }

    /// <summary>Batch delete for rows on two MSELs is answered with a 500 and removes nothing.</summary>
    [Fact]
    public async Task BatchDelete_ForRowsOnTwoMsels_Is500_AndRemovesNothing()
    {
        var msel = await SeedMsel();
        var mine = await SeedEvent(msel);
        var elsewhere = await SeedEvent(await SeedMsel());
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await BatchDelete(Client(actor), mine.Id, elsewhere.Id);

        Assert.Equal("Scenario events can only be from one MSEL for batch delete!", (await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
        Assert.NotNull(await Stored(mine.Id));
        Assert.NotNull(await Stored(elsewhere.Id));
    }

    /// <summary>
    /// One unknown id refuses the whole batch, which is what makes the route safe to retry.
    /// </summary>
    [Fact]
    public async Task BatchDelete_WithAnUnknownId_Is404_AndRemovesNothing()
    {
        var msel = await SeedMsel();
        var scenarioEvent = await SeedEvent(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await BatchDelete(Client(actor), scenarioEvent.Id, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotNull(await Stored(scenarioEvent.Id));
    }

    /// <summary>Batch delete with an empty list is answered with a 404.</summary>
    [Fact]
    public async Task BatchDelete_WithAnEmptyList_Is404()
    {
        var actor = await Actor().SeedAsync();

        var response = await BatchDelete(Client(actor));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task BatchDelete_ForAnEditor_Is403()
    {
        var msel = await SeedMsel();
        var scenarioEvent = await SeedEvent(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var response = await BatchDelete(Client(actor), scenarioEvent.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(await Stored(scenarioEvent.Id));
    }

    [Fact]
    public async Task BatchDelete_BroadcastsOneDeletionPerRow()
    {
        var msel = await SeedMsel();
        var first = await SeedEvent(msel);
        var second = await SeedEvent(msel, deltaSeconds: 60);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        await BatchDelete(Client(actor), first.Id, second.Id);

        Assert.Equal(
            [first.Id, second.Id],
            Hub.ToGroup(msel.Id)
                .Where(x => x.Method == MainHubMethods.ScenarioEventDeleted)
                .Select(x => (Guid)x.Payload));
        Assert.Equal(
            [msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.ScenarioEventDeleted, msel.Id));
    }

    // ---------------------------------------------------------------------------------------------
    // Authentication
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "msels/00000000-0000-0000-0000-000000000001/scenarioEvents")]
    [InlineData("GET", "scenarioEvents/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "scenarioEvents")]
    [InlineData("POST", "scenarioEvents/fromInjects")]
    [InlineData("POST", "msels/00000000-0000-0000-0000-000000000001/scenarioEvents/copy")]
    [InlineData("PUT", "scenarioEvents/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "scenarioEvents/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "scenarioEvents/batchDelete")]
    public async Task EveryRouteRefusesAnUnauthenticatedRequest(string method, string route)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/{route}")
        {
            Content = JsonContent.Create(new { })
        };

        var response = await Client().SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// One row of an MSEL's timeline, with one data field and the one cell where they meet.
    /// </summary>
    private sealed record Row(
        MselEntity Msel,
        ScenarioEventEntity Event,
        DataFieldEntity Field,
        DataValueEntity Value);

    /// <summary>
    /// The wire shape of a scenario event. A record rather than an anonymous type so a test can vary one
    /// property of a stored row with a <c>with</c> expression. <c>DateCreated</c> and <c>CreatedBy</c> are
    /// non-nullable on <c>ViewModels.Base</c>, so they are sent as values: a null is a 400 that never
    /// reaches the controller.
    /// </summary>
    private sealed record EventBody
    {
        public Guid Id { get; init; }
        public Guid MselId { get; init; }
        public int DeltaSeconds { get; init; }
        public int GroupOrder { get; init; }
        public bool IsHidden { get; init; }
        public string RowMetadata { get; init; }
        public EventType ScenarioEventType { get; init; }
        public Guid? InjectId { get; init; }
        public string IntegrationTarget { get; init; }
        public string Information { get; init; }
        public ValueBody[] DataValues { get; init; } = [];

        public Guid CreatedBy { get; init; }
        public DateTime DateCreated { get; init; }
        public Guid? ModifiedBy { get; init; }
        public DateTime? DateModified { get; init; }
    }

    /// <summary>
    /// The wire shape of one cell, as it arrives nested inside a row.
    /// </summary>
    private sealed record ValueBody
    {
        public Guid Id { get; init; }
        public string Value { get; init; }
        public Guid? ScenarioEventId { get; init; }
        public Guid? InjectId { get; init; }
        public Guid DataFieldId { get; init; }
        public string CellMetadata { get; init; }

        public Guid CreatedBy { get; init; }
        public DateTime DateCreated { get; init; }
        public Guid? ModifiedBy { get; init; }
        public DateTime? DateModified { get; init; }
    }

    private static EventBody Body(
        Guid mselId,
        int deltaSeconds = 0,
        string information = "posted by the test",
        Guid? id = null) =>
        new()
        {
            Id = id ?? Guid.Empty,
            MselId = mselId,
            DeltaSeconds = deltaSeconds,
            ScenarioEventType = EventType.Inject,
            Information = information
        };

    private static EventBody BodyFor(ScenarioEventEntity scenarioEvent) => new()
    {
        Id = scenarioEvent.Id,
        MselId = scenarioEvent.MselId,
        DeltaSeconds = scenarioEvent.DeltaSeconds,
        GroupOrder = scenarioEvent.GroupOrder,
        IsHidden = scenarioEvent.IsHidden,
        RowMetadata = scenarioEvent.RowMetadata,
        ScenarioEventType = scenarioEvent.ScenarioEventType,
        InjectId = scenarioEvent.InjectId,
        IntegrationTarget = scenarioEvent.IntegrationTarget,
        Information = scenarioEvent.Information
    };

    private static ValueBody Cell(DataValueEntity dataValue) => new()
    {
        Id = dataValue.Id,
        Value = dataValue.Value,
        ScenarioEventId = dataValue.ScenarioEventId,
        InjectId = dataValue.InjectId,
        DataFieldId = dataValue.DataFieldId,
        CellMetadata = dataValue.CellMetadata
    };

    private async Task<MselEntity> SeedMsel(Guid? createdBy = null, bool isTemplate = false)
    {
        var msel = TestData.Msel(createdBy: createdBy, isTemplate: isTemplate);
        await Seed(msel);

        return msel;
    }

    private async Task<ScenarioEventEntity> SeedEvent(
        MselEntity msel,
        int deltaSeconds = 0,
        int groupOrder = 1,
        string information = null)
    {
        var scenarioEvent = TestData.ScenarioEvent(msel.Id, deltaSeconds, groupOrder);

        if (information is not null)
        {
            scenarioEvent.Information = information;
        }

        await Seed(scenarioEvent);

        return scenarioEvent;
    }

    private async Task<Row> SeedRow(Guid? createdBy = null)
    {
        var msel = await SeedMsel(createdBy);
        var field = TestData.DataField(mselId: msel.Id);
        var scenarioEvent = TestData.ScenarioEvent(msel.Id);
        await Seed(field, scenarioEvent);

        var dataValue = TestData.DataValue(field.Id, scenarioEvent.Id, "before");
        await Seed(dataValue);

        return new Row(msel, scenarioEvent, field, dataValue);
    }

    private Task<HttpResponseMessage> Post(HttpClient client, EventBody body) =>
        client.PostAsJsonAsync("/api/scenarioEvents", body, Ct);

    private Task<HttpResponseMessage> Put(HttpClient client, Guid id, EventBody body) =>
        client.PutAsJsonAsync($"/api/scenarioEvents/{id}", body, Ct);

    private Task<HttpResponseMessage> BatchDelete(HttpClient client, params Guid[] ids) =>
        client.PostAsJsonAsync("/api/scenarioEvents/batchDelete", ids, Ct);

    private async Task<List<ScenarioEvent>> GetEvents(HttpClient client, Guid mselId)
    {
        var response = await client.GetAsync($"/api/msels/{mselId}/scenarioEvents", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<List<ScenarioEvent>>(response);
    }

    private async Task<ScenarioEvent> GetEvent(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/scenarioEvents/{id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<ScenarioEvent>(response);
    }

    private async Task<ScenarioEventEntity> Stored(Guid id)
    {
        await using var context = NewContext();

        return await context.ScenarioEvents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, Ct);
    }

    private async Task<MselEntity> StoredMsel(Guid id)
    {
        await using var context = NewContext();

        return await context.Msels.AsNoTracking().SingleAsync(x => x.Id == id, Ct);
    }

    private async Task<List<DataValueEntity>> CellsOn(Guid scenarioEventId)
    {
        await using var context = NewContext();

        return await context.DataValues
            .AsNoTracking()
            .Where(x => x.ScenarioEventId == scenarioEventId)
            .ToListAsync(Ct);
    }

    private async Task<int> CountOn(Guid mselId)
    {
        await using var context = NewContext();

        return await context.ScenarioEvents.CountAsync(x => x.MselId == mselId, Ct);
    }

    private async Task<ApiError> ReadError(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions, Ct);

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }
}
