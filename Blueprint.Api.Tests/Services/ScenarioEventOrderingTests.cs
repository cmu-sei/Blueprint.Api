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

namespace Blueprint.Api.Tests.Services;

/// <summary><c>ScenarioEventService.ReorderScenarioEvents</c> - what creating, moving and deleting a row
/// does to the <c>GroupOrder</c> of every other row that shares its time.</summary>
public class ScenarioEventOrderingTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // Creating
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_IntoAnEmptyGroup_IsPositionZero()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Create(Client(actor), msel.Id, "N", groupOrder: 0);

        Assert.Equal(["N=0"], await Orders(msel.Id));
    }

    /// <summary>A new row asking for position zero is appended to the end of its group.</summary>
    [Fact]
    public async Task Create_WithAGroupOrderOfZero_IsAppendedToTheEnd()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Create(Client(actor), msel.Id, "N", groupOrder: 0);

        Assert.Equal(["A=0", "B=1", "C=2", "N=3"], await Orders(msel.Id));
    }

    /// <summary>Create without a date created is inserted at the head instead.</summary>
    [Fact]
    public async Task Create_WithoutADateCreated_IsInsertedAtTheHeadInstead()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var before = DateTime.UtcNow;

        await CreateWithoutADate(Client(actor), msel.Id, "N", groupOrder: 0);

        Assert.Equal(["N=0", "A=1", "B=2", "C=3"], await Orders(msel.Id));

        var stored = await Stored(msel.Id, "N");

        Assert.InRange(stored.DateCreated, before, DateTime.UtcNow);
    }

    [Fact]
    public async Task Create_WithANonZeroGroupOrder_TakesThatPositionAndShiftsTheRestDown()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Create(Client(actor), msel.Id, "N", groupOrder: 1);

        Assert.Equal(["A=0", "N=1", "B=2", "C=3"], await Orders(msel.Id));
    }

    /// <summary>Create with a position past the end is given position zero instead.</summary>
    [Fact]
    public async Task Create_WithAPositionPastTheEnd_IsGivenPositionZeroInstead()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Create(Client(actor), msel.Id, "N", groupOrder: 5);

        Assert.Equal(["A=0", "N=0", "B=1"], await Orders(msel.Id));
    }

    /// <summary>A new row asking for a free position inside a gap is put after the last row.</summary>
    [Fact]
    public async Task Create_WithAPositionInsideAGap_TakesTheNumberAfterTheLastRow()
    {
        var msel = await SeedMsel();
        await SeedAt(msel, 0, 0, "A");
        await SeedAt(msel, 0, 3, "B");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Create(Client(actor), msel.Id, "N", groupOrder: 1);

        Assert.Equal(["A=0", "B=3", "N=4"], await Orders(msel.Id));
    }

    /// <summary>
    /// A shift renumbers the rows it moves consecutively, so it also closes any gaps between them.
    /// </summary>
    [Fact]
    public async Task Create_ShiftingTheRestClosesTheGapsBetweenThem()
    {
        var msel = await SeedMsel();
        await SeedAt(msel, 0, 0, "A");
        await SeedAt(msel, 0, 5, "B");
        await SeedAt(msel, 0, 9, "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await CreateWithoutADate(Client(actor), msel.Id, "N", groupOrder: 0);

        Assert.Equal(["N=0", "A=1", "B=2", "C=3"], await Orders(msel.Id));
    }

    [Fact]
    public async Task Create_RenumbersOnlyItsOwnTimeSlot()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B");
        await SeedGroup(msel, 60, "X", "Y");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Create(Client(actor), msel.Id, "N", deltaSeconds: 0, groupOrder: 1);

        Assert.Equal(
            ["A=0:0", "N=0:1", "B=0:2", "X=60:0", "Y=60:1"],
            await Timeline(msel.Id));
    }

    [Fact]
    public async Task Create_RenumbersOnlyItsOwnMsel()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B");
        var other = await SeedMsel();
        await SeedGroup(other, 0, "A", "B");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Create(Client(actor), msel.Id, "N", groupOrder: 1);

        Assert.Equal(["A=0", "N=1", "B=2"], await Orders(msel.Id));
        Assert.Equal(["A=0", "B=1"], await Orders(other.Id));
    }

    /// <summary>The create answers the new row followed by every row it renumbered, with their new
    /// positions.</summary>
    [Fact]
    public async Task Create_ReturnsTheNewRowFollowedByEveryRowItRenumbered()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var returned = await Create(Client(actor), msel.Id, "N", groupOrder: 1);

        Assert.Equal(
            ["N=1", "B=2", "C=3"],
            returned.Select(x => $"{x.Information}={x.GroupOrder}"));
    }

    [Fact]
    public async Task Create_WhenNothingIsRenumbered_ReturnsOnlyTheNewRow()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var returned = await Create(Client(actor), msel.Id, "N", groupOrder: 0);

        Assert.Equal("N=2", $"{Assert.Single(returned).Information}={returned[0].GroupOrder}");
        Assert.Empty(Hub.Of(MainHubMethods.ScenarioEventUpdated));
    }

    /// <summary>
    /// Every row the renumbering touches is broadcast as an update, and the ones it left alone are not.
    /// </summary>
    /// <remarks>
    /// The renumbering saves inside the request's transaction, so these arrive with the create - a client
    /// redrawing the group from the <c>ScenarioEventCreated</c> message alone would have the other rows'
    /// positions wrong.
    /// </remarks>
    [Fact]
    public async Task Create_BroadcastsAnUpdateForEveryRowItRenumbers()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Create(Client(actor), msel.Id, "N", groupOrder: 1);

        Assert.Equal(["B=2", "C=3"], Broadcast(MainHubMethods.ScenarioEventUpdated, msel));
        Assert.Equal(["N=1"], Broadcast(MainHubMethods.ScenarioEventCreated, msel));
    }

    // ---------------------------------------------------------------------------------------------
    // Moving
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_MovingARowUp_ShiftsTheRowsItPassesDown()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel.Id, "C", toGroupOrder: 1);

        Assert.Equal(["A=0", "C=1", "B=2"], await Orders(msel.Id));
    }

    [Fact]
    public async Task Update_MovingARowToTheHead_ShiftsEverythingDown()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel.Id, "C", toGroupOrder: 0);

        Assert.Equal(["C=0", "A=1", "B=2"], await Orders(msel.Id));
    }

    /// <summary>Update moving a row down lands it one position short of the one it asked for.</summary>
    [Fact]
    public async Task Update_MovingARowDown_LandsItOnePositionShortOfTheOneItAskedFor()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel.Id, "A", toGroupOrder: 2);

        Assert.Equal(["B=1", "A=2", "C=3"], await Orders(msel.Id));
    }

    /// <summary>Moving a row past the end of its group sends it to position zero.</summary>
    [Fact]
    public async Task Update_MovingARowPastTheEnd_SendsItToPositionZero()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel.Id, "B", toGroupOrder: 5);

        Assert.Equal(["A=0", "B=0", "C=2"], await Orders(msel.Id));
    }

    /// <summary>Moving a row to a free position inside a gap sends it to the end.</summary>
    [Fact]
    public async Task Update_ToAPositionInsideAGap_TakesTheNumberAfterTheLastRow()
    {
        var msel = await SeedMsel();
        await SeedAt(msel, 0, 0, "A");
        await SeedAt(msel, 0, 3, "B");
        await SeedAt(msel, 0, 9, "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel.Id, "C", toGroupOrder: 1);

        Assert.Equal(["A=0", "B=3", "C=4"], await Orders(msel.Id));
    }

    [Fact]
    public async Task Update_TheRenumberingClosesGapsBetweenTheRowsItShifts()
    {
        var msel = await SeedMsel();
        await SeedAt(msel, 0, 0, "A");
        await SeedAt(msel, 0, 4, "B");
        await SeedAt(msel, 0, 8, "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel.Id, "C", toGroupOrder: 0);

        Assert.Equal(["C=0", "A=1", "B=2"], await Orders(msel.Id));
    }

    /// <summary>A row moved to another time slot is given position zero there.</summary>
    [Fact]
    public async Task Update_ChangingTheTimeSlot_PutsTheRowAtPositionZeroOfTheNewGroup()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B");
        await SeedGroup(msel, 60, "X");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel.Id, "B", toDeltaSeconds: 60);

        Assert.Equal(["A=0:0", "B=60:0", "X=60:0"], await Timeline(msel.Id));
    }

    /// <summary>A row moved to another time slot keeps its position when the new group has a row holding it,
    /// which is shifted.</summary>
    [Fact]
    public async Task Update_ChangingTheTimeSlot_LandsOnAnOccupiedPositionAndShiftsIt()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B");
        await SeedGroup(msel, 60, "X", "Y");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel.Id, "B", toDeltaSeconds: 60);

        Assert.Equal(["A=0:0", "X=60:0", "B=60:1", "Y=60:2"], await Timeline(msel.Id));
    }

    /// <summary>Update changing the time slot leaves a gap behind.</summary>
    [Fact]
    public async Task Update_ChangingTheTimeSlot_LeavesAGapBehind()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel.Id, "B", toDeltaSeconds: 60);

        Assert.Equal(["A=0:0", "C=0:2", "B=60:0"], await Timeline(msel.Id));
    }

    /// <summary>A PUT that changes neither time nor position renumbers nothing.</summary>
    [Fact]
    public async Task Update_ThatChangesNeitherTimeNorPosition_RenumbersNothing()
    {
        var msel = await SeedMsel();
        await SeedAt(msel, 0, 0, "A");
        await SeedAt(msel, 0, 0, "B");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var row = await Stored(msel.Id, "A");
        var response = await Client(actor).PutAsJsonAsync(
            $"/api/scenarioEvents/{row.Id}",
            BodyFor(row) with { Information = "A" , IntegrationTarget = "renamed" },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["A=0", "B=0"], await Orders(msel.Id));
    }

    [Fact]
    public async Task Update_RenumbersOnlyItsOwnMsel()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C");
        var other = await SeedMsel();
        await SeedGroup(other, 0, "A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel.Id, "C", toGroupOrder: 0);

        Assert.Equal(["C=0", "A=1", "B=2"], await Orders(msel.Id));
        Assert.Equal(["A=0", "B=1", "C=2"], await Orders(other.Id));
    }

    [Fact]
    public async Task Update_ReturnsTheRowFollowedByEveryRowItRenumbered()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var returned = await Move(Client(actor), msel.Id, "C", toGroupOrder: 1);

        Assert.Equal(
            ["C=1", "B=2"],
            returned.Select(x => $"{x.Information}={x.GroupOrder}"));
    }

    [Fact]
    public async Task Update_BroadcastsAnUpdateForEveryRowItRenumbers()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel.Id, "C", toGroupOrder: 0);

        Assert.Equal(
            ["A=1", "B=2", "C=0"],
            Broadcast(MainHubMethods.ScenarioEventUpdated, msel).Order(StringComparer.Ordinal));
    }

    /// <summary>Update a shifted row is stamped modified with no modifier.</summary>
    [Fact]
    public async Task Update_AShiftedRowIsStampedModifiedWithNoModifier()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel.Id, "B", toGroupOrder: 0);

        var shifted = await Stored(msel.Id, "A");

        Assert.NotNull(shifted.DateModified);
        Assert.Null(shifted.ModifiedBy);

        var moved = await Stored(msel.Id, "B");

        Assert.Equal(actor.Id, moved.ModifiedBy);
    }

    // ---------------------------------------------------------------------------------------------
    // Deleting
    // ---------------------------------------------------------------------------------------------

    /// <summary>Delete leaves a gap where the row was.</summary>
    [Fact]
    public async Task Delete_LeavesAGapWhereTheRowWas()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var row = await Stored(msel.Id, "B");
        var response = await Client(actor).DeleteAsync($"/api/scenarioEvents/{row.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["A=0", "C=2"], await Orders(msel.Id));
    }

    /// <summary>A batch delete leaves every emptied position empty.</summary>
    [Fact]
    public async Task BatchDelete_LeavesGapsWhereTheRowsWere()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C", "D");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/scenarioEvents/batchDelete",
            new[] { (await Stored(msel.Id, "A")).Id, (await Stored(msel.Id, "C")).Id },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["B=1", "D=3"], await Orders(msel.Id));
    }

    /// <summary>After a delete, creating a row at the visible middle puts it at the end.</summary>
    [Fact]
    public async Task Delete_ThenCreatingAtTheVisibleMiddle_LandsAtTheEnd()
    {
        var msel = await SeedMsel();
        await SeedGroup(msel, 0, "A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var client = Client(actor);

        var row = await Stored(msel.Id, "B");
        await client.DeleteAsync($"/api/scenarioEvents/{row.Id}", Ct);

        await Create(client, msel.Id, "N", groupOrder: 1);

        Assert.Equal(["A=0", "C=2", "N=3"], await Orders(msel.Id));
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The wire shape of a scenario event, trimmed to the fields this file varies. <c>DateCreated</c> and
    /// <c>CreatedBy</c> are non-nullable on <c>ViewModels.Base</c>, so they are sent as values: a null is a
    /// 400 that never reaches the controller.
    /// </summary>
    private sealed record EventBody
    {
        public Guid Id { get; init; }
        public Guid MselId { get; init; }
        public int DeltaSeconds { get; init; }
        public int GroupOrder { get; init; }
        public string Information { get; init; }
        public string IntegrationTarget { get; init; }
        public EventType ScenarioEventType { get; init; }

        public Guid CreatedBy { get; init; }
        public DateTime DateCreated { get; init; }
        public Guid? ModifiedBy { get; init; }
        public DateTime? DateModified { get; init; }
    }

    private static EventBody BodyFor(ScenarioEventEntity scenarioEvent) => new()
    {
        Id = scenarioEvent.Id,
        MselId = scenarioEvent.MselId,
        DeltaSeconds = scenarioEvent.DeltaSeconds,
        GroupOrder = scenarioEvent.GroupOrder,
        Information = scenarioEvent.Information,
        IntegrationTarget = scenarioEvent.IntegrationTarget,
        ScenarioEventType = scenarioEvent.ScenarioEventType,
        DateCreated = scenarioEvent.DateCreated
    };

    private async Task<MselEntity> SeedMsel()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        return msel;
    }

    /// <summary>
    /// One row per name at <paramref name="deltaSeconds"/>, numbered from zero in the order given.
    /// </summary>
    private async Task SeedGroup(MselEntity msel, int deltaSeconds, params string[] names)
    {
        for (var i = 0; i < names.Length; i++)
        {
            await SeedAt(msel, deltaSeconds, i, names[i]);
        }
    }

    /// <summary>
    /// One row at an exact position, for the groups that need a gap in them.
    /// </summary>
    private async Task SeedAt(MselEntity msel, int deltaSeconds, int groupOrder, string name)
    {
        var scenarioEvent = TestData.ScenarioEvent(msel.Id, deltaSeconds, groupOrder);
        scenarioEvent.Information = name;
        await Seed(scenarioEvent);
    }

    /// <summary>
    /// The rows of one MSEL as <c>name=groupOrder</c>, in the order the grid draws them. The tie-break on
    /// name is the test's, not the API's: the list route sorts on <c>DeltaSeconds</c> and
    /// <c>GroupOrder</c> alone, so two rows sharing both have no defined order and an assertion cannot
    /// depend on one.
    /// </summary>
    private async Task<string[]> Orders(Guid mselId)
    {
        var rows = await Rows(mselId);

        return rows.Select(x => $"{x.Information}={x.GroupOrder}").ToArray();
    }

    /// <summary>
    /// The same set as <see cref="Orders"/> as <c>name=deltaSeconds:groupOrder</c>, for the tests that
    /// span more than one time slot.
    /// </summary>
    private async Task<string[]> Timeline(Guid mselId)
    {
        var rows = await Rows(mselId);

        return rows.Select(x => $"{x.Information}={x.DeltaSeconds}:{x.GroupOrder}").ToArray();
    }

    private async Task<List<ScenarioEventEntity>> Rows(Guid mselId)
    {
        await using var context = NewContext();

        return await context.ScenarioEvents
            .AsNoTracking()
            .Where(x => x.MselId == mselId)
            .OrderBy(x => x.DeltaSeconds)
            .ThenBy(x => x.GroupOrder)
            .ThenBy(x => x.Information)
            .ToListAsync(Ct);
    }

    private async Task<ScenarioEventEntity> Stored(Guid mselId, string name)
    {
        await using var context = NewContext();

        return await context.ScenarioEvents
            .AsNoTracking()
            .SingleAsync(x => x.MselId == mselId && x.Information == name, Ct);
    }

    /// <summary>
    /// The rows carried by one broadcast method, as <c>name=groupOrder</c>.
    /// </summary>
    private string[] Broadcast(string method, MselEntity msel) =>
        Hub.ToGroup(msel.Id)
            .Where(x => x.Method == method)
            .Select(x => (ScenarioEvent)x.Payload)
            .Select(x => $"{x.Information}={x.GroupOrder}")
            .ToArray();

    /// <summary>
    /// Creates a row, as a client sending the whole view model does: with a <c>dateCreated</c> of now,
    /// which the server overwrites and which decides where a row asking for position zero lands. See
    /// <see cref="Create_WithoutADateCreated_IsInsertedAtTheHeadInstead"/>.
    /// </summary>
    private Task<List<ScenarioEvent>> Create(
        HttpClient client,
        Guid mselId,
        string name,
        int deltaSeconds = 0,
        int groupOrder = 0) =>
        Post(client, mselId, name, deltaSeconds, groupOrder, DateTime.UtcNow);

    /// <summary>
    /// The same request with no <c>dateCreated</c> in the body.
    /// </summary>
    private Task<List<ScenarioEvent>> CreateWithoutADate(
        HttpClient client,
        Guid mselId,
        string name,
        int deltaSeconds = 0,
        int groupOrder = 0) =>
        Post(client, mselId, name, deltaSeconds, groupOrder, default);

    private async Task<List<ScenarioEvent>> Post(
        HttpClient client,
        Guid mselId,
        string name,
        int deltaSeconds,
        int groupOrder,
        DateTime dateCreated)
    {
        var response = await client.PostAsJsonAsync(
            "/api/scenarioEvents",
            new EventBody
            {
                MselId = mselId,
                DeltaSeconds = deltaSeconds,
                GroupOrder = groupOrder,
                Information = name,
                ScenarioEventType = EventType.Inject,
                DateCreated = dateCreated
            },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<List<ScenarioEvent>>(response);
    }

    private async Task<List<ScenarioEvent>> Move(
        HttpClient client,
        Guid mselId,
        string name,
        int? toDeltaSeconds = null,
        int? toGroupOrder = null)
    {
        var row = await Stored(mselId, name);
        var body = BodyFor(row) with
        {
            DeltaSeconds = toDeltaSeconds ?? row.DeltaSeconds,
            GroupOrder = toGroupOrder ?? row.GroupOrder
        };

        var response = await client.PutAsJsonAsync($"/api/scenarioEvents/{row.Id}", body, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<List<ScenarioEvent>>(response);
    }

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }
}
