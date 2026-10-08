// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>The helpers the data-value test classes share.</summary>
public abstract class DataValueTestsBase(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory)
{

    /// <summary>
    /// One cell of an MSEL's grid, and everything it hangs off.
    /// </summary>
    protected sealed record Cell(
        MselEntity Msel,
        ScenarioEventEntity Event,
        DataFieldEntity Field,
        DataValueEntity Value);

    /// <summary>
    /// The other kind of cell: a value on an inject, whose data field belongs to an inject type and so to
    /// no MSEL at all.
    /// </summary>
    protected sealed record InjectCell(
        InjectTypeEntity InjectType,
        InjectEntity Inject,
        DataFieldEntity Field,
        DataValueEntity Value);

    /// <summary>
    /// The wire shape of a data value. A record rather than an anonymous type so a test can vary one
    /// property of a stored row with a <c>with</c> expression - which is how the request bodies here stay
    /// honest about what a client would send. <c>DateCreated</c> and <c>CreatedBy</c> are non-nullable on
    /// <c>ViewModels.Base</c>, so they are sent as values: a null is a 400 that never reaches the
    /// controller.
    /// </summary>
    protected sealed record ValueBody
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

    protected static ValueBody Body(
        Guid dataFieldId,
        Guid? scenarioEventId = null,
        string value = "entered",
        Guid? injectId = null) =>
        new()
        {
            DataFieldId = dataFieldId,
            ScenarioEventId = scenarioEventId,
            InjectId = injectId,
            Value = value
        };

    protected static ValueBody BodyFor(DataValueEntity dataValue) => new()
    {
        Id = dataValue.Id,
        Value = dataValue.Value,
        ScenarioEventId = dataValue.ScenarioEventId,
        InjectId = dataValue.InjectId,
        DataFieldId = dataValue.DataFieldId,
        CellMetadata = dataValue.CellMetadata
    };

    /// <summary>
    /// An MSEL with one scenario event, one data field of <paramref name="dataType"/> and the one cell
    /// where they meet.
    /// </summary>
    protected async Task<Cell> SeedCell(
        DataFieldType dataType = DataFieldType.String,
        string value = "before",
        Guid? createdBy = null)
    {
        var msel = TestData.Msel(createdBy: createdBy);
        await Seed(msel);

        var field = TestData.DataField(mselId: msel.Id, dataType: dataType);
        var scenarioEvent = TestData.ScenarioEvent(msel.Id);
        await Seed(field, scenarioEvent);

        var dataValue = TestData.DataValue(field.Id, scenarioEvent.Id, value);
        await Seed(dataValue);

        return new Cell(msel, scenarioEvent, field, dataValue);
    }

    protected async Task<InjectCell> SeedInjectCell(
        DataFieldType dataType = DataFieldType.String,
        string value = "before")
    {
        var injectType = TestData.InjectType();
        await Seed(injectType);

        var field = TestData.DataField(injectTypeId: injectType.Id, dataType: dataType);
        var inject = TestData.Inject(injectType.Id);
        await Seed(field, inject);

        var dataValue = TestData.DataValueOnInject(field.Id, inject.Id, value);
        await Seed(dataValue);

        return new InjectCell(injectType, inject, field, dataValue);
    }

    protected Task<HttpResponseMessage> Post(HttpClient client, ValueBody body) =>
        client.PostAsJsonAsync("/api/dataValues", body, Ct);

    protected Task<HttpResponseMessage> Put(HttpClient client, Guid id, ValueBody body) =>
        client.PutAsJsonAsync($"/api/dataValues/{id}", body, Ct);

    protected async Task<List<DataValue>> GetValues(HttpClient client, Guid mselId)
    {
        var response = await client.GetAsync($"/api/msels/{mselId}/datavalues", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<List<DataValue>>(response);
    }

    protected async Task<DataValue> GetValue(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/dataValues/{id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<DataValue>(response);
    }

    protected async Task<DataValueEntity> Stored(Guid id)
    {
        await using var context = NewContext();

        return await context.DataValues.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, Ct);
    }

    protected async Task AssertRecordedNothing() =>
        Assert.Empty(await ReadBack(rb => rb.XApiQueuedStatements.ToListAsync(Ct)));

    /// <summary>The verb IRI of the statement a queued row carries.</summary>
    protected static string VerbOf(XApiQueuedStatementEntity row)
    {
        using var document = System.Text.Json.JsonDocument.Parse(row.StatementJson);

        return document.RootElement.GetProperty("verb").GetProperty("id").GetString();
    }

    protected async Task<ApiError> ReadError(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions, Ct);

    protected async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }
}

/// <summary>The five <c>dataValues</c> routes: the contents of the cells of an MSEL's scenario-event
/// grid.</summary>
public class DataValueEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : DataValueTestsBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET msels/{mselId}/datavalues
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByMsel_ReturnsEveryValueOnTheMsel()
    {
        var cell = await SeedCell();
        var second = TestData.DataField(mselId: cell.Msel.Id, displayOrder: 2);
        await Seed(second);
        await Seed(TestData.DataValue(second.Id, cell.Event.Id, "also"));
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Viewer).SeedAsync();

        var values = await GetValues(Client(actor), cell.Msel.Id);

        Assert.Equal(["also", "before"], values.Select(x => x.Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task GetByMsel_DoesNotReturnAnotherMselsValues()
    {
        var cell = await SeedCell();
        await SeedCell();
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Viewer).SeedAsync();

        var values = await GetValues(Client(actor), cell.Msel.Id);

        Assert.Equal(cell.Value.Id, Assert.Single(values).Id);
    }

    /// <summary>
    /// A value on an inject is not one of any MSEL's cells: it belongs to the reusable half of the data
    /// model and has no scenario event to be found by.
    /// </summary>
    [Fact]
    public async Task GetByMsel_DoesNotReturnAValueOnAnInject()
    {
        var cell = await SeedCell();
        await SeedInjectCell();
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Viewer).SeedAsync();

        var values = await GetValues(Client(actor), cell.Msel.Id);

        Assert.Equal(cell.Value.Id, Assert.Single(values).Id);
    }

    /// <summary>Get by MSEL returns a value whose data field belongs to another MSEL.</summary>
    [Fact]
    public async Task GetByMsel_ReturnsAValueWhoseDataFieldBelongsToAnotherMsel()
    {
        var cell = await SeedCell();
        var elsewhere = await SeedCell();
        var stray = TestData.DataValue(elsewhere.Field.Id, cell.Event.Id, "stray");
        await Seed(stray);
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Viewer).SeedAsync();

        var values = await GetValues(Client(actor), cell.Msel.Id);

        Assert.Equal(["before", "stray"], values.Select(x => x.Value).Order(StringComparer.Ordinal));
        Assert.Contains(elsewhere.Field.Id, values.Select(x => x.DataFieldId));
    }

    /// <summary>Get by MSEL for a unit member with no role is answered with a 200 while the same value by id is answered with a 403.</summary>
    [Fact]
    public async Task GetByMsel_ForAUnitMemberWithNoRole_Is200_WhileTheSameValueByIdIs403()
    {
        var cell = await SeedCell();
        var actor = await Actor().InUnitOf(cell.Msel).SeedAsync();

        var values = await GetValues(Client(actor), cell.Msel.Id);

        Assert.Equal(cell.Value.Id, Assert.Single(values).Id);

        var byId = await Client(actor).GetAsync($"/api/dataValues/{cell.Value.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, byId.StatusCode);
    }

    [Fact]
    public async Task GetByMsel_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var cell = await SeedCell();
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{cell.Msel.Id}/datavalues", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetByMsel_WithViewMsels_Is200()
    {
        var cell = await SeedCell();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var values = await GetValues(Client(actor), cell.Msel.Id);

        Assert.Equal(cell.Value.Id, Assert.Single(values).Id);
    }

    /// <summary>Get by MSEL for an unknown MSEL is answered with a 500.</summary>
    [Fact]
    public async Task GetByMsel_ForAnUnknownMsel_Is500()
    {
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{Guid.NewGuid()}/datavalues", Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Object reference not set to an instance of an object.", failure.Title);
        Assert.Contains("MselUserRequirement.IsMet", failure.Detail);
    }

    [Fact]
    public async Task GetByMsel_ForAnUnknownMsel_WithViewMsels_IsAnEmptyList()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var values = await GetValues(Client(actor), Guid.NewGuid());

        Assert.Empty(values);
    }

    // ---------------------------------------------------------------------------------------------
    // GET dataValues/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ReturnsTheValue()
    {
        var cell = await SeedCell();
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Viewer).SeedAsync();

        var value = await GetValue(Client(actor), cell.Value.Id);

        Assert.Equal("before", value.Value);
        Assert.Equal(cell.Field.Id, value.DataFieldId);
        Assert.Equal(cell.Event.Id, value.ScenarioEventId);
        Assert.Null(value.InjectId);
    }

    [Fact]
    public async Task Get_ForAnUnknownId_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/dataValues/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// A member of one of the MSEL's teams reads a cell without holding any role, which is what makes the
    /// grid visible to the people playing the exercise.
    /// </summary>
    [Fact]
    public async Task Get_ForAMemberOfOneOfTheMselsTeams_Is200()
    {
        var cell = await SeedCell();
        var team = TestData.Team(cell.Msel.Id);
        await Seed(team);
        var actor = await Actor().OnTeam(team).SeedAsync();

        var value = await GetValue(Client(actor), cell.Value.Id);

        Assert.Equal(cell.Value.Id, value.Id);
    }

    [Fact]
    public async Task Get_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var cell = await SeedCell();
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/dataValues/{cell.Value.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Get for a value on an inject is answered with a 403.</summary>
    [Fact]
    public async Task Get_ForAValueOnAnInject_Is403()
    {
        var cell = await SeedInjectCell();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/dataValues/{cell.Value.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_ForAValueOnAnInject_WithViewMsels_Is200()
    {
        var cell = await SeedInjectCell();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var value = await GetValue(Client(actor), cell.Value.Id);

        Assert.Equal(cell.Inject.Id, value.InjectId);
        Assert.Null(value.ScenarioEventId);
    }

    // ---------------------------------------------------------------------------------------------
    // POST dataValues
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_StoresTheValue()
    {
        var cell = await SeedCell();
        var field = TestData.DataField(mselId: cell.Msel.Id, displayOrder: 2);
        await Seed(field);
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(field.Id, cell.Event.Id, "entered"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await Read<DataValue>(response);
        var stored = await Stored(created.Id);

        Assert.Equal("entered", stored.Value);
        Assert.Equal(field.Id, stored.DataFieldId);
        Assert.Equal(cell.Event.Id, stored.ScenarioEventId);
    }

    /// <summary>The <c>Location</c> header is lower-case, as <c>RouteOptions.LowercaseUrls</c> writes it, and
    /// resolves.</summary>
    [Fact]
    public async Task Create_ReturnsALocationHeaderThatResolves()
    {
        var cell = await SeedCell();
        var field = TestData.DataField(mselId: cell.Msel.Id, displayOrder: 2);
        await Seed(field);
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(field.Id, cell.Event.Id));
        var created = await Read<DataValue>(response);

        Assert.NotNull(response.Headers.Location);
        Assert.EndsWith($"/api/datavalues/{created.Id}", response.Headers.Location.ToString());

        var followed = await Client(actor).GetAsync(response.Headers.Location, Ct);

        Assert.Equal(HttpStatusCode.OK, followed.StatusCode);
    }

    /// <summary>
    /// Creating a cell is owner-or-editor, with no allowance for the type of the field - so the roles that
    /// may fill in a new cell are not the roles that may change one, and an approver who can edit every
    /// existing value cannot add one.
    /// </summary>
    [Theory]
    [InlineData(MselRole.Owner, HttpStatusCode.Created)]
    [InlineData(MselRole.Editor, HttpStatusCode.Created)]
    [InlineData(MselRole.Approver, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.MoveEditor, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Viewer, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Evaluator, HttpStatusCode.Forbidden)]
    public async Task Create_TheRolesThatMayAddACell(MselRole role, HttpStatusCode expected)
    {
        var cell = await SeedCell();
        var field = TestData.DataField(mselId: cell.Msel.Id, displayOrder: 2);
        await Seed(field);
        var actor = await Actor().OnMsel(cell.Msel, role).SeedAsync();

        var response = await Post(Client(actor), Body(field.Id, cell.Event.Id));

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithEditMsels_Is201()
    {
        var cell = await SeedCell();
        var field = TestData.DataField(mselId: cell.Msel.Id, displayOrder: 2);
        await Seed(field);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(Client(actor), Body(field.Id, cell.Event.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary><c>ManageDataFields</c> alone cannot create a value.</summary>
    [Fact]
    public async Task Create_WithManageDataFieldsOnly_Is403()
    {
        var cell = await SeedCell();
        var field = TestData.DataField(mselId: cell.Msel.Id, displayOrder: 2);
        await Seed(field);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Post(Client(actor), Body(field.Id, cell.Event.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Create for an unknown data field is answered with a 500.</summary>
    [Fact]
    public async Task Create_ForAnUnknownDataField_Is500()
    {
        var cell = await SeedCell();
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(Guid.NewGuid(), cell.Event.Id));

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Object reference not set to an instance of an object.", failure.Title);
        Assert.Contains("MselOwnerRequirement.IsMet", failure.Detail);
    }

    /// <summary>Create for a data field on an inject type is answered with a 500.</summary>
    [Fact]
    public async Task Create_ForADataFieldOnAnInjectType_Is500()
    {
        var cell = await SeedInjectCell();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();
        var inject = TestData.Inject(cell.InjectType.Id);
        await Seed(inject);

        var response = await Post(Client(actor), Body(cell.Field.Id, injectId: inject.Id));

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Object reference not set to an instance of an object.", failure.Title);
        Assert.Contains("MselOwnerRequirement.IsMet", failure.Detail);
    }

    [Fact]
    public async Task Create_AValueOnAnInject_WithEditMsels_Is201()
    {
        var cell = await SeedInjectCell();
        var second = TestData.DataField(injectTypeId: cell.InjectType.Id, displayOrder: 2);
        await Seed(second);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(Client(actor), Body(second.Id, injectId: cell.Inject.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var stored = await Stored((await Read<DataValue>(response)).Id);

        Assert.Equal(cell.Inject.Id, stored.InjectId);
        Assert.Null(stored.ScenarioEventId);
    }

    /// <summary>Create a second value for the same cell is accepted.</summary>
    [Fact]
    public async Task Create_ASecondValueForTheSameCell_IsAccepted()
    {
        var cell = await SeedCell();
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(cell.Field.Id, cell.Event.Id, "second"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var context = NewContext();
        var stored = await context.DataValues
            .AsNoTracking()
            .Where(x => x.ScenarioEventId == cell.Event.Id && x.DataFieldId == cell.Field.Id)
            .Select(x => x.Value)
            .ToListAsync(Ct);

        Assert.Equal(["before", "second"], stored.Order(StringComparer.Ordinal));
    }

    /// <summary>Create without exactly one of a scenario event and an inject is answered with a 500.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Create_WithoutExactlyOneOfAScenarioEventAndAnInject_Is500(
        bool withScenarioEvent,
        bool withInject)
    {
        var cell = await SeedCell();
        var injectType = TestData.InjectType();
        await Seed(injectType);
        var inject = TestData.Inject(injectType.Id);
        await Seed(inject);
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), new ValueBody
        {
            DataFieldId = cell.Field.Id,
            ScenarioEventId = withScenarioEvent ? cell.Event.Id : null,
            InjectId = withInject ? inject.Id : null,
            Value = "neither"
        });

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("An error occurred while saving the entity changes. See the inner exception for details.", failure.Title);
        Assert.Contains("DataValueService.CreateAsync", failure.Detail);
    }

    [Fact]
    public async Task Create_SetsCreatedByToTheCallerAndIgnoresTheBody()
    {
        var cell = await SeedCell();
        var field = TestData.DataField(mselId: cell.Msel.Id, displayOrder: 2);
        await Seed(field);
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Owner).SeedAsync();
        var before = DateTime.UtcNow;

        var response = await Post(
            Client(actor),
            Body(field.Id, cell.Event.Id) with
            {
                CreatedBy = Guid.NewGuid(),
                DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ModifiedBy = Guid.NewGuid(),
                DateModified = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });

        var stored = await Stored((await Read<DataValue>(response)).Id);

        Assert.Equal(actor.Id, stored.CreatedBy);
        Assert.InRange(stored.DateCreated, before, DateTime.UtcNow);
        Assert.Null(stored.ModifiedBy);
        Assert.Null(stored.DateModified);
    }

    [Fact]
    public async Task Create_MarksTheMselModified()
    {
        var cell = await SeedCell();
        var field = TestData.DataField(mselId: cell.Msel.Id, displayOrder: 2);
        await Seed(field);
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Owner).SeedAsync();
        var before = DateTime.UtcNow;

        await Post(Client(actor), Body(field.Id, cell.Event.Id));

        await using var context = NewContext();
        var msel = await context.Msels.AsNoTracking().SingleAsync(x => x.Id == cell.Msel.Id, Ct);

        Assert.Equal(actor.Id, msel.ModifiedBy);
        Assert.NotNull(msel.DateModified);
        Assert.InRange(msel.DateModified.Value, before, DateTime.UtcNow);
    }

    [Fact]
    public async Task Create_BroadcastsToTheMselAndTheAdminGroup()
    {
        var cell = await SeedCell();
        var field = TestData.DataField(mselId: cell.Msel.Id, displayOrder: 2);
        await Seed(field);
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(field.Id, cell.Event.Id, "broadcast"));
        var created = await Read<DataValue>(response);

        Assert.Equal(
            [cell.Msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.DataValueCreated, cell.Msel.Id));

        var sent = (DataValue)Hub.Of(MainHubMethods.DataValueCreated, cell.Msel.Id)[0].Payload;

        Assert.Equal(created.Id, sent.Id);
        Assert.Equal("broadcast", sent.Value);
    }

    /// <summary>Create a value on an inject broadcasts to a group named by the empty guid.</summary>
    [Fact]
    public async Task Create_AValueOnAnInject_BroadcastsToAGroupNamedByTheEmptyGuid()
    {
        var cell = await SeedInjectCell();
        var second = TestData.DataField(injectTypeId: cell.InjectType.Id, displayOrder: 2);
        await Seed(second);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        await Post(Client(actor), Body(second.Id, injectId: cell.Inject.Id));

        Assert.Equal(
            [Guid.Empty.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.DataValueCreated, Guid.Empty));
    }

    // ---------------------------------------------------------------------------------------------
    // PUT dataValues/{id} - the permission cascade
    // ---------------------------------------------------------------------------------------------

    /// <summary>Update the permission cascade.</summary>
    [Theory]
    [InlineData(DataFieldType.String, MselRole.Owner, null, HttpStatusCode.OK, null)]
    [InlineData(DataFieldType.String, MselRole.Approver, null, HttpStatusCode.OK, null)]
    [InlineData(DataFieldType.String, MselRole.Editor, null, HttpStatusCode.OK, null)]
    [InlineData(DataFieldType.String, MselRole.MoveEditor, null, HttpStatusCode.Forbidden, "Insufficient Permissions")]
    [InlineData(DataFieldType.String, MselRole.Viewer, null, HttpStatusCode.Forbidden, "Insufficient Permissions")]
    [InlineData(DataFieldType.String, MselRole.Evaluator, null, HttpStatusCode.Forbidden, "Insufficient Permissions")]
    [InlineData(DataFieldType.Checkbox, MselRole.Owner, null, HttpStatusCode.OK, null)]
    [InlineData(DataFieldType.Checkbox, MselRole.Approver, null, HttpStatusCode.Forbidden, "Cannot change this value.")]
    [InlineData(DataFieldType.Checkbox, MselRole.Editor, null, HttpStatusCode.Forbidden, "Cannot change this value.")]
    [InlineData(DataFieldType.Checkbox, MselRole.Viewer, null, HttpStatusCode.Forbidden, "Cannot change this value.")]
    [InlineData(DataFieldType.Checkbox, MselRole.Evaluator, null, HttpStatusCode.Forbidden, "Insufficient Permissions")]
    [InlineData(DataFieldType.Checkbox, MselRole.Evaluator, MselRole.Editor, HttpStatusCode.OK, null)]
    [InlineData(DataFieldType.Checkbox, MselRole.Evaluator, MselRole.Approver, HttpStatusCode.OK, null)]
    [InlineData(DataFieldType.Status, MselRole.Owner, null, HttpStatusCode.OK, null)]
    [InlineData(DataFieldType.Status, MselRole.Approver, null, HttpStatusCode.OK, null)]
    [InlineData(DataFieldType.Status, MselRole.Editor, null, HttpStatusCode.Forbidden, "Cannot change the Status.")]
    [InlineData(DataFieldType.Status, MselRole.Viewer, null, HttpStatusCode.Forbidden, "Cannot change the Status.")]
    [InlineData(DataFieldType.Team, MselRole.Owner, null, HttpStatusCode.OK, null)]
    [InlineData(DataFieldType.Team, MselRole.Approver, null, HttpStatusCode.Forbidden, "Cannot change the Assigned Team.")]
    [InlineData(DataFieldType.Team, MselRole.Editor, null, HttpStatusCode.Forbidden, "Cannot change the Assigned Team.")]
    [InlineData(DataFieldType.Team, MselRole.Evaluator, null, HttpStatusCode.Forbidden, "Cannot change the Assigned Team.")]
    public async Task Update_ThePermissionCascade(
        DataFieldType dataType,
        MselRole role,
        MselRole? alsoHolding,
        HttpStatusCode expected,
        string title)
    {
        var cell = await SeedCell(dataType);
        var builder = Actor().OnMsel(cell.Msel, role);

        if (alsoHolding is not null)
        {
            builder = builder.OnMsel(cell.Msel, alsoHolding.Value);
        }

        var actor = await builder.SeedAsync();

        var response = await Put(
            Client(actor),
            cell.Value.Id,
            BodyFor(cell.Value) with { Value = "after" });

        Assert.Equal(expected, response.StatusCode);

        if (title is not null)
        {
            Assert.Equal(title, (await ReadError(response)).Title);
        }

        var stored = await Stored(cell.Value.Id);

        Assert.Equal(expected == HttpStatusCode.OK ? "after" : "before", stored.Value);
    }

    /// <summary>
    /// The MSEL's creator changes anything, holding no role at all - the one short-circuit the cascade's
    /// first test carries.
    /// </summary>
    [Fact]
    public async Task Update_ForTheMselsCreator_ChangesTheAssignedTeam()
    {
        var creator = await Actor().SeedAsync();
        var cell = await SeedCell(DataFieldType.Team, createdBy: creator.Id);

        var response = await Put(
            Client(creator),
            cell.Value.Id,
            BodyFor(cell.Value) with { Value = "after" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Update_WithEditMsels_ChangesTheAssignedTeam()
    {
        var cell = await SeedCell(DataFieldType.Team);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Put(
            Client(actor),
            cell.Value.Id,
            BodyFor(cell.Value) with { Value = "after" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Update_WithManageDataFieldsOnly_Is403()
    {
        var cell = await SeedCell();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Put(
            Client(actor),
            cell.Value.Id,
            BodyFor(cell.Value) with { Value = "after" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Same case as Create_ForADataFieldOnAnInjectType_Is500.
    [Fact]
    public async Task Update_AValueOnAnInject_WithoutEditMsels_Is500()
    {
        var cell = await SeedInjectCell();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Put(
            Client(actor),
            cell.Value.Id,
            BodyFor(cell.Value) with { Value = "after" });

        var error = await AssertJsonError<ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Object reference not set to an instance of an object.", error.Title);
    }

    [Fact]
    public async Task Update_ForAnUnknownId_Is404()
    {
        var cell = await SeedCell();
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Put(
            Client(actor),
            Guid.NewGuid(),
            BodyFor(cell.Value) with { Id = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT dataValues/{id} - what a PUT can change
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_StoresTheValueAndStampsTheAuditFields()
    {
        var cell = await SeedCell();
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Editor).SeedAsync();
        var before = DateTime.UtcNow;

        var response = await Put(
            Client(actor),
            cell.Value.Id,
            BodyFor(cell.Value) with
            {
                Value = "after",
                CellMetadata = "background-color:red",
                ModifiedBy = Guid.NewGuid(),
                DateModified = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await Stored(cell.Value.Id);

        Assert.Equal("after", stored.Value);
        Assert.Equal("background-color:red", stored.CellMetadata);
        Assert.Equal(actor.Id, stored.ModifiedBy);
        Assert.NotNull(stored.DateModified);
        Assert.InRange(stored.DateModified.Value, before, DateTime.UtcNow);
        Assert.Equal(cell.Value.CreatedBy, stored.CreatedBy);
    }

    /// <summary>Update moves the value into another MSELs cell.</summary>
    [Fact]
    public async Task Update_MovesTheValueIntoAnotherMselsCell()
    {
        var cell = await SeedCell();
        var elsewhere = await SeedCell();
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Editor).SeedAsync();

        var response = await Put(
            Client(actor),
            cell.Value.Id,
            BodyFor(cell.Value) with
            {
                DataFieldId = elsewhere.Field.Id,
                ScenarioEventId = elsewhere.Event.Id,
                Value = "moved"
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await Stored(cell.Value.Id);

        Assert.Equal(elsewhere.Field.Id, stored.DataFieldId);
        Assert.Equal(elsewhere.Event.Id, stored.ScenarioEventId);

        var admin = await Actor().WithAllSystemPermissions().SeedAsync();
        var values = await GetValues(Client(admin), elsewhere.Msel.Id);

        Assert.Contains(cell.Value.Id, values.Select(x => x.Id));
    }

    /// <summary>Update with no id in the body is answered with a 500.</summary>
    [Fact]
    public async Task Update_WithNoIdInTheBody_Is500()
    {
        var cell = await SeedCell();
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Editor).SeedAsync();

        var response = await Put(
            Client(actor),
            cell.Value.Id,
            BodyFor(cell.Value) with { Id = Guid.Empty, Value = "after" });

        Assert.Equal("The property 'DataValueEntity.Id' is part of a key and so cannot be modified or marked as modified. To change the principal of an existing entity with an identifying foreign key, first delete the dependent and invoke 'SaveChanges', and then associate the dependent with the new principal.", (await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
    }

    [Fact]
    public async Task Update_BroadcastsToTheMselAndTheAdminGroup()
    {
        var cell = await SeedCell();
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Editor).SeedAsync();

        await Put(Client(actor), cell.Value.Id, BodyFor(cell.Value) with { Value = "after" });

        Assert.Equal(
            [cell.Msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.DataValueUpdated, cell.Msel.Id));

        var sent = (DataValue)Hub.Of(MainHubMethods.DataValueUpdated, cell.Msel.Id)[0].Payload;

        Assert.Equal(cell.Value.Id, sent.Id);
        Assert.Equal("after", sent.Value);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE dataValues/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_RemovesTheValue()
    {
        var cell = await SeedCell();
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/dataValues/{cell.Value.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await Stored(cell.Value.Id));
    }

    [Fact]
    public async Task Delete_ForAnUnknownId_Is404()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/dataValues/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Deleting a cell is owner-or-editor, the same test creating one uses and not the one changing one
    /// uses - so an approver may edit every value on the row and remove none of them.
    /// </summary>
    [Theory]
    [InlineData(MselRole.Owner, HttpStatusCode.NoContent)]
    [InlineData(MselRole.Editor, HttpStatusCode.NoContent)]
    [InlineData(MselRole.Approver, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.MoveEditor, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Viewer, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Evaluator, HttpStatusCode.Forbidden)]
    public async Task Delete_TheRolesThatMayRemoveACell(MselRole role, HttpStatusCode expected)
    {
        var cell = await SeedCell();
        var actor = await Actor().OnMsel(cell.Msel, role).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/dataValues/{cell.Value.Id}", Ct);

        Assert.Equal(expected, response.StatusCode);

        var stored = await Stored(cell.Value.Id);

        if (expected == HttpStatusCode.NoContent)
        {
            Assert.Null(stored);
        }
        else
        {
            Assert.NotNull(stored);
        }
    }

    [Fact]
    public async Task Delete_WithEditMsels_Is204()
    {
        var cell = await SeedCell();
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/dataValues/{cell.Value.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Delete_WithManageDataFieldsOnly_Is403()
    {
        var cell = await SeedCell();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/dataValues/{cell.Value.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Delete a value on an inject is answered with a 500.</summary>
    [Fact]
    public async Task Delete_AValueOnAnInject_Is500()
    {
        var cell = await SeedInjectCell();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/dataValues/{cell.Value.Id}", Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Object reference not set to an instance of an object.", failure.Title);
        Assert.Contains("MselOwnerRequirement.IsMet", failure.Detail);
        Assert.NotNull(await Stored(cell.Value.Id));
    }

    /// <summary>
    /// The delete broadcast carries the id alone, where the create and update broadcasts carry the whole
    /// value.
    /// </summary>
    [Fact]
    public async Task Delete_BroadcastsTheIdToTheMselAndTheAdminGroup()
    {
        var cell = await SeedCell();
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Owner).SeedAsync();

        await Client(actor).DeleteAsync($"/api/dataValues/{cell.Value.Id}", Ct);

        Assert.Equal(
            [cell.Msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.DataValueDeleted, cell.Msel.Id));
        Assert.Equal(cell.Value.Id, Hub.Of(MainHubMethods.DataValueDeleted, cell.Msel.Id)[0].Payload);
    }

    // ---------------------------------------------------------------------------------------------
    // Authentication
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "msels/00000000-0000-0000-0000-000000000001/datavalues")]
    [InlineData("GET", "dataValues/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "dataValues")]
    [InlineData("PUT", "dataValues/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "dataValues/00000000-0000-0000-0000-000000000001")]
    public async Task EveryRouteRefusesAnUnauthenticatedRequest(string method, string route)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/{route}")
        {
            Content = JsonContent.Create(new { })
        };

        var response = await Client().SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

}

/// <summary>The xAPI statement a checkbox change records, over the real <c>XApiService</c>, read back from the queue.</summary>
public class DataValueXApiTests(DatabaseFixture fixture, XApiEnabledFactory factory)
    : DataValueTestsBase(fixture, factory), IClassFixture<XApiEnabledFactory>
{
    // ---------------------------------------------------------------------------------------------
    // PUT dataValues/{id} - the xAPI statement
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_TickingACheckbox_RecordsAnXApiStatement()
    {
        var cell = await SeedCell(DataFieldType.Checkbox, value: "false");
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Owner).SeedAsync();

        await Put(Client(actor), cell.Value.Id, BodyFor(cell.Value) with { Value = "true" });

        var row = Assert.Single(await ReadBack(rb => rb.XApiQueuedStatements.ToListAsync(Ct)));
        Assert.Equal(cell.Msel.Id, row.MselId);
        Assert.Equal(XApiEnabledFactory.ApiUrl + "scenarioevents/" + cell.Event.Id + "/datafields/" + cell.Field.Id, row.ActivityId);
        Assert.Equal("https://w3id.org/xapi/dod-isd/verbs/selected", VerbOf(row));
        Assert.Equal(cell.Field.Name, FieldNameOf(row));
    }

    /// <summary>A checkbox set to one is recorded as unchecked.</summary>
    [Fact]
    public async Task Update_ACheckboxSetToOne_IsRecordedAsUnchecked()
    {
        var cell = await SeedCell(DataFieldType.Checkbox, value: "0");
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Owner).SeedAsync();

        await Put(Client(actor), cell.Value.Id, BodyFor(cell.Value) with { Value = "1" });

        var row = Assert.Single(await ReadBack(rb => rb.XApiQueuedStatements.ToListAsync(Ct)));
        Assert.Equal("https://w3id.org/xapi/dod-isd/verbs/reset", VerbOf(row));
        Assert.Equal(cell.Msel.Id, row.MselId);
        Assert.Equal(XApiEnabledFactory.ApiUrl + "scenarioevents/" + cell.Event.Id + "/datafields/" + cell.Field.Id, row.ActivityId);
        Assert.Equal(cell.Field.Name, FieldNameOf(row));
    }

    /// <summary>A PUT that leaves a checkbox alone records no statement.</summary>
    [Fact]
    public async Task Update_ACheckboxToTheSameValue_RecordsNothing()
    {
        var cell = await SeedCell(DataFieldType.Checkbox, value: "true");
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Owner).SeedAsync();

        var response = await Put(
            Client(actor),
            cell.Value.Id,
            BodyFor(cell.Value) with { CellMetadata = "still true" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertRecordedNothing();
    }

    [Fact]
    public async Task Update_ANonCheckboxField_RecordsNothing()
    {
        var cell = await SeedCell();
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Owner).SeedAsync();

        await Put(Client(actor), cell.Value.Id, BodyFor(cell.Value) with { Value = "after" });

        await AssertRecordedNothing();
    }

    /// <summary>A checkbox on an inject records no statement: there is no MSEL or scenario event to place it
    /// in.</summary>
    [Fact]
    public async Task Update_ACheckboxOnAnInject_RecordsNothing()
    {
        var cell = await SeedInjectCell(DataFieldType.Checkbox, value: "false");
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Put(
            Client(actor),
            cell.Value.Id,
            BodyFor(cell.Value) with { Value = "true" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertRecordedNothing();
    }

    /// <summary>The checkbox's data field name, which the statement carries as its object's name.</summary>
    private static string FieldNameOf(XApiQueuedStatementEntity row)
    {
        using var document = System.Text.Json.JsonDocument.Parse(row.StatementJson);

        return document.RootElement
            .GetProperty("object").GetProperty("definition").GetProperty("name").GetProperty("en-US")
            .GetString();
    }
}
