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
using Blueprint.Api.Tests.Infrastructure;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// The five <c>dataValues</c> routes: the contents of the cells of an MSEL's scenario-event grid.
/// </summary>
/// <remarks>
/// <para>
/// A data value is one cell - the value of one data field on one scenario event - so this is the endpoint
/// an exercise author's every keystroke goes through, and the one an evaluator running a live exercise
/// uses to tick boxes off. That split is why the update path has a permission cascade of its own instead
/// of the owner-or-editor test the other four use: what a caller may change depends on the
/// <em>type</em> of the field they are changing.
/// </para>
/// <para>
/// The cascade (<c>DataValueService.cs:117-136</c>) reads as four rules and behaves as three, because the
/// checkbox rule is a filter rather than a grant. An evaluator satisfying it still falls through to the
/// approver and editor tests below, so the comment "Evaluators can update checkboxes" is false in both
/// directions: an evaluator alone cannot change a checkbox, and an approver or editor who is not also an
/// evaluator cannot either. Changing a checkbox needs <c>Evaluator</c> <em>and</em> one of
/// <c>Approver</c>/<c>Editor</c> - a combination nothing in the UI asks for.
/// <see cref="Update_ThePermissionCascade"/> is the whole matrix on one screen.
/// </para>
/// <para>
/// The two read routes disagree about who may see the same data. <c>GET msels/{id}/datavalues</c> uses
/// <c>MselUserRequirement</c>, where membership of a unit assigned to the MSEL is enough on its own;
/// <c>GET dataValues/{id}</c> uses <c>MselViewRequirement</c>, which needs a role as well. So a unit
/// member with no role reads every cell of the MSEL as a list and none of them individually
/// (<see cref="GetByMsel_ForAUnitMemberWithNoRole_Is200_WhileTheSameValueByIdIs403"/>).
/// </para>
/// <para>
/// Four more characterizations. The list route filters on the scenario event and never on the data field,
/// so a value wired to another MSEL's field is returned as one of this MSEL's cells - and
/// <c>UpdateAsync</c> checks only the <em>stored</em> field's MSEL before mapping the request body over
/// the row, so an editor of one MSEL can move a cell into another
/// (<see cref="Update_MovesTheValueIntoAnotherMselsCell"/>). The unique index over
/// <c>(ScenarioEventId, InjectId, DataFieldId)</c> looks like it forbids two values for one cell and does
/// not, because one of the three columns is always null and Postgres counts nulls as distinct
/// (<see cref="Create_ASecondValueForTheSameCell_IsAccepted"/>). And every path that resolves the MSEL
/// from the data field reaches <c>MselOwnerRequirement</c> with a null id for a field belonging to an
/// inject type, which throws rather than refusing - a 500 where a 403 was meant
/// (<see cref="Create_ForADataFieldOnAnInjectType_Is500"/>).
/// </para>
/// <para>
/// Two things are noted here and not tested because nothing observable follows from them:
/// <c>hasDataFieldPermission</c> is passed to all four mutating methods and read by none - the tests that
/// pin it are the <c>WithManageDataFieldsOnly</c> ones, which show the permission buying nothing - and
/// the data-field lookups at <c>DataValueService.cs:112</c> and <c>:179</c> are missing their
/// <c>CancellationToken</c>.
/// </para>
/// </remarks>
public class DataValueEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET msels/{mselId}/datavalues
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByMsel_ReturnsEveryValueOnTheMsel()
    {
        var cell = await SeedCell();
        var second = BlueprintAppFactory.DataField(mselId: cell.Msel.Id, displayOrder: 2);
        await Seed(second);
        await Seed(BlueprintAppFactory.DataValue(second.Id, cell.Event.Id, "also"));
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

    /// <summary>
    /// A value whose scenario event is on this MSEL and whose data field is on another is returned as one
    /// of this MSEL's cells.
    /// </summary>
    /// <remarks>
    /// Characterization. <c>GetByMselAsync</c> collects the MSEL's scenario events and then every value
    /// pointing at one of them, without ever asking which MSEL the value's data field belongs to - and
    /// nothing else does either, because no foreign key ties the two sides of a cell to the same MSEL.
    /// The consequence is the update path's, not this one's: see
    /// <see cref="Update_MovesTheValueIntoAnotherMselsCell"/>, which is how such a row gets written.
    /// Turns red when the query filters on the data field as well.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_ReturnsAValueWhoseDataFieldBelongsToAnotherMsel()
    {
        var cell = await SeedCell();
        var elsewhere = await SeedCell();
        var stray = BlueprintAppFactory.DataValue(elsewhere.Field.Id, cell.Event.Id, "stray");
        await Seed(stray);
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Viewer).SeedAsync();

        var values = await GetValues(Client(actor), cell.Msel.Id);

        Assert.Equal(["before", "stray"], values.Select(x => x.Value).Order(StringComparer.Ordinal));
        Assert.Contains(elsewhere.Field.Id, values.Select(x => x.DataFieldId));
    }

    /// <summary>
    /// The two read routes disagree: membership of a unit assigned to the MSEL reads the whole grid and
    /// none of its cells.
    /// </summary>
    /// <remarks>
    /// Characterization, and the reason both halves are asserted in one test: the contradiction is the
    /// finding, and two tests could each be read as describing correct behaviour. <c>GetByMselAsync</c>
    /// uses <c>MselUserRequirement</c>, which grants a unit member with no role; <c>GetAsync</c> uses
    /// <c>MselViewRequirement</c>, which requires a role as well. Turns red when the two are reconciled,
    /// whichever way round.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_ForAUnitMemberWithNoRole_Is200_WhileTheSameValueByIdIs403()
    {
        var cell = await SeedCell();
        var actor = await Actor().SeedAsync();
        await Db.AddUnitMembershipAsync(actor.Id, cell.Msel.Id, Ct);

        var values = await GetValues(Client(actor), cell.Msel.Id);

        Assert.Equal(cell.Value.Id, Assert.Single(values).Id);

        var byId = await Client(actor).GetAsync($"/api/dataValues/{cell.Value.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, byId.StatusCode);
    }

    [Fact]
    public async Task GetByMsel_WithNoRelationshipToTheMsel_Is403()
    {
        var cell = await SeedCell();
        var actor = await Actor().SeedAsync();

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

    /// <summary>
    /// An unknown MSEL is a 500 for a caller who has to be checked against it.
    /// </summary>
    /// <remarks>
    /// Characterization. <c>MselUserRequirement.IsMet</c> reads <c>.CreatedBy</c> off the result of a
    /// <c>FirstOrDefaultAsync</c>, so an id matching nothing is a null reference rather than a refusal.
    /// The pair below shows it is the permission check and not the query: the same request from a caller
    /// holding <c>ViewMsels</c> skips the helper and answers an empty list. Turns red when the helper
    /// guards its lookup.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_ForAnUnknownMsel_Is500()
    {
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{Guid.NewGuid()}/datavalues", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
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
        var team = BlueprintAppFactory.Team(cell.Msel.Id);
        await Seed(team);
        var actor = await Actor().OnTeam(team).SeedAsync();

        var value = await GetValue(Client(actor), cell.Value.Id);

        Assert.Equal(cell.Value.Id, value.Id);
    }

    [Fact]
    public async Task Get_WithNoRelationshipToTheMsel_Is403()
    {
        var cell = await SeedCell();
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync($"/api/dataValues/{cell.Value.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// Nobody can read a value on an inject without <c>ViewMsels</c>, however much of the installation
    /// they own.
    /// </summary>
    /// <remarks>
    /// Characterization. The permission check runs <c>MselViewRequirement</c> against the data field's
    /// <c>MselId</c>, which for an inject type's field is null - and the requirement answers false for an
    /// MSEL it cannot find, so there is no combination of catalog, unit or role that satisfies it. The
    /// pair below shows the data is readable, so this is the check and not the query. Turns red when the
    /// inject side gets a permission check of its own.
    /// </remarks>
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
        var field = BlueprintAppFactory.DataField(mselId: cell.Msel.Id, displayOrder: 2);
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

    /// <summary>
    /// The <c>Location</c> header is lowercase where the route template is camel-case, because
    /// <c>Startup</c> sets <c>RouteOptions.LowercaseUrls</c>. It resolves either way - routing is
    /// case-insensitive - and it is asserted as the API actually writes it rather than as the attribute
    /// spells it, so that a change to the setting is a failure here rather than a surprise in a client
    /// that compares the header to a URL it built itself.
    /// </summary>
    [Fact]
    public async Task Create_ReturnsALocationHeaderThatResolves()
    {
        var cell = await SeedCell();
        var field = BlueprintAppFactory.DataField(mselId: cell.Msel.Id, displayOrder: 2);
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
        var field = BlueprintAppFactory.DataField(mselId: cell.Msel.Id, displayOrder: 2);
        await Seed(field);
        var actor = await Actor().OnMsel(cell.Msel, role).SeedAsync();

        var response = await Post(Client(actor), Body(field.Id, cell.Event.Id));

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithEditMsels_Is201()
    {
        var cell = await SeedCell();
        var field = BlueprintAppFactory.DataField(mselId: cell.Msel.Id, displayOrder: 2);
        await Seed(field);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(Client(actor), Body(field.Id, cell.Event.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    /// <c>ManageDataFields</c> buys nothing here, although the controller resolves it and passes it down.
    /// </summary>
    /// <remarks>
    /// Characterization of dead code rather than of a defect: <c>hasDataFieldPermission</c> is a parameter
    /// of all four mutating methods and is read by none of them. Turns red if the permission is ever
    /// wired up - at which point this test says where to look.
    /// </remarks>
    [Fact]
    public async Task Create_WithManageDataFieldsOnly_Is403()
    {
        var cell = await SeedCell();
        var field = BlueprintAppFactory.DataField(mselId: cell.Msel.Id, displayOrder: 2);
        await Seed(field);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Post(Client(actor), Body(field.Id, cell.Event.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// A data field that does not exist is a 500, not a 404 or a 403.
    /// </summary>
    /// <remarks>
    /// Characterization. The MSEL is resolved from the data field, an unknown field gives a null id, and
    /// <c>MselOwnerRequirement.IsMet</c> dereferences the MSEL it then fails to find. Turns red when the
    /// requirement guards its lookup or the service checks the field exists first.
    /// </remarks>
    [Fact]
    public async Task Create_ForAnUnknownDataField_Is500()
    {
        var cell = await SeedCell();
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(Guid.NewGuid(), cell.Event.Id));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// The same 500 for a data field that does exist and belongs to an inject type, because that field has
    /// no MSEL either.
    /// </summary>
    /// <remarks>
    /// Characterization, and the more serious half of <see cref="Create_ForAnUnknownDataField_Is500"/>:
    /// this is a legitimate request against real rows, and the only way to make it work is to hold
    /// <c>EditMsels</c> - see <see cref="Create_AValueOnAnInject_WithEditMsels_Is201"/>, which proves the
    /// write itself is fine. Turns red when the inject side gets a permission check of its own.
    /// </remarks>
    [Fact]
    public async Task Create_ForADataFieldOnAnInjectType_Is500()
    {
        var cell = await SeedInjectCell();
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();
        var forbidden = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();
        var inject = BlueprintAppFactory.Inject(cell.InjectType.Id);
        await Seed(inject);

        // Proves the row is writable at all, so the 500 below is the permission check.
        var allowed = await Post(Client(actor), Body(cell.Field.Id, injectId: inject.Id));

        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);

        var response = await Post(Client(forbidden), Body(cell.Field.Id, injectId: inject.Id));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Create_AValueOnAnInject_WithEditMsels_Is201()
    {
        var cell = await SeedInjectCell();
        var second = BlueprintAppFactory.DataField(injectTypeId: cell.InjectType.Id, displayOrder: 2);
        await Seed(second);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(Client(actor), Body(second.Id, injectId: cell.Inject.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var stored = await Stored((await Read<DataValue>(response)).Id);

        Assert.Equal(cell.Inject.Id, stored.InjectId);
        Assert.Null(stored.ScenarioEventId);
    }

    /// <summary>
    /// Two values for one cell are accepted, although the table carries a unique index that reads as
    /// though they are not.
    /// </summary>
    /// <remarks>
    /// Characterization, and a defect in the schema rather than in the service. The 2023 migration indexed
    /// <c>(scenario_event_id, data_field_id)</c> uniquely; the 2024 migration that added injects replaced
    /// it with <c>(scenario_event_id, inject_id, data_field_id)</c>, and because exactly one of the first
    /// two columns is always null - the check constraint says so - and Postgres counts nulls as distinct,
    /// the index no longer refuses anything. Nothing else in the API enforces one value per cell either,
    /// and a duplicate is invisible until something reads the cell with <c>Single</c>. Turns red when the
    /// index is made partial, split in two, or declared <c>NULLS NOT DISTINCT</c>.
    /// </remarks>
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

    /// <summary>
    /// A body naming neither a scenario event nor an inject, or both, is a 500: the check constraint
    /// refuses it and nothing checks it first.
    /// </summary>
    /// <remarks>
    /// Characterization. Every other invalid body on this endpoint is a 400 from
    /// <c>ValidateModelStateFilter</c>; this one reaches the database, so the caller gets a server error
    /// for a request the API could have refused. Turns red when the rule is expressed in the view model.
    /// </remarks>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Create_WithoutExactlyOneOfAScenarioEventAndAnInject_Is500(
        bool withScenarioEvent,
        bool withInject)
    {
        var cell = await SeedCell();
        var injectType = BlueprintAppFactory.InjectType();
        await Seed(injectType);
        var inject = BlueprintAppFactory.Inject(injectType.Id);
        await Seed(inject);
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), new ValueBody
        {
            DataFieldId = cell.Field.Id,
            ScenarioEventId = withScenarioEvent ? cell.Event.Id : null,
            InjectId = withInject ? inject.Id : null,
            Value = "neither"
        });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Create_SetsCreatedByToTheCallerAndIgnoresTheBody()
    {
        var cell = await SeedCell();
        var field = BlueprintAppFactory.DataField(mselId: cell.Msel.Id, displayOrder: 2);
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
        var field = BlueprintAppFactory.DataField(mselId: cell.Msel.Id, displayOrder: 2);
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
        var field = BlueprintAppFactory.DataField(mselId: cell.Msel.Id, displayOrder: 2);
        await Seed(field);
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(field.Id, cell.Event.Id, "broadcast"));
        var created = await Read<DataValue>(response);

        Assert.Equal(
            [cell.Msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.DataValueCreated));

        var sent = (DataValue)Hub.Of(MainHubMethods.DataValueCreated)[0].Payload;

        Assert.Equal(created.Id, sent.Id);
        Assert.Equal("broadcast", sent.Value);
    }

    /// <summary>
    /// A value on an inject is broadcast to a group named by the all-zeros guid, which no client joins.
    /// </summary>
    /// <remarks>
    /// Characterization, the same shape of defect as <c>OrganizationHandler</c>'s empty-string group.
    /// <c>DataValueHandler.GetGroups</c> resolves the MSEL by looking the value's scenario event up in
    /// <c>ScenarioEvents</c>; for an inject there is no event, <c>SingleOrDefault</c> answers
    /// <c>Guid.Empty</c>, and that is what the group is named after. The admin group still gets it, so
    /// nothing is lost - but a client editing injects has nothing to subscribe to. Turns red when the
    /// handler skips the MSEL group for a value that has no scenario event.
    /// </remarks>
    [Fact]
    public async Task Create_AValueOnAnInject_BroadcastsToAGroupNamedByTheEmptyGuid()
    {
        var cell = await SeedInjectCell();
        var second = BlueprintAppFactory.DataField(injectTypeId: cell.InjectType.Id, displayOrder: 2);
        await Seed(second);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        await Post(Client(actor), Body(second.Id, injectId: cell.Inject.Id));

        Assert.Equal(
            [Guid.Empty.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.DataValueCreated));
    }

    // ---------------------------------------------------------------------------------------------
    // PUT dataValues/{id} - the permission cascade
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The whole of <c>DataValueService.cs:117-136</c>, as the roles a caller holds against the type of
    /// the field they are changing. Every case is a caller with no system permission, so the seeded rows
    /// are the only thing deciding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read down the <c>Checkbox</c> rows for the finding. The rule that names evaluators does not grant
    /// them anything - it only refuses everybody else - so an evaluator alone is refused by the editor
    /// test two lines below it, and a checkbox needs <c>Evaluator</c> together with <c>Approver</c> or
    /// <c>Editor</c>. Meanwhile an approver or editor who is not an evaluator, who may change every other
    /// field on the row, cannot tick the box.
    /// </para>
    /// <para>
    /// The message is asserted alongside the status because it is the only evidence of which branch
    /// answered: <c>JsonExceptionFilter</c> puts the exception's message in <c>title</c> for anything that
    /// is not a 500, and the four refusals here carry four different ones. Turns red when the cascade is
    /// rewritten, which is the point - the table is what a rewrite has to be checked against.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// An editor of one MSEL moves a cell into another MSEL, and the API answers 200.
    /// </summary>
    /// <remarks>
    /// Characterization, and the most serious finding on this endpoint. The permission check runs against
    /// the MSEL of the <em>stored</em> data field; the body is then mapped over the row wholesale, so
    /// <c>DataFieldId</c> and <c>ScenarioEventId</c> land wherever the caller pointed them - at a MSEL
    /// they have no relationship to. What they have written is a cell of that MSEL's grid, returned by its
    /// list route (see <see cref="GetByMsel_ReturnsAValueWhoseDataFieldBelongsToAnotherMsel"/>) and
    /// editable from then on by its owner and not by this caller. Turns red when the new data field's MSEL
    /// is checked as well as the old one's.
    /// </remarks>
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

    /// <summary>
    /// A body carrying an id other than the route's is a 500.
    /// </summary>
    /// <remarks>
    /// Characterization. The route id finds the row and the body is then mapped over it including its key,
    /// so EF is asked to change a primary key and refuses. The same shape as
    /// <c>MselEndpointTests.Update_WithAnotherMselsIdInTheBody_Is500</c>: harmless in practice, because
    /// the client generates the body from what it read, but a 500 for a request that should be a 400.
    /// Turns red when the service forces the route id onto the body.
    /// </remarks>
    [Fact]
    public async Task Update_WithNoIdInTheBody_Is500()
    {
        var cell = await SeedCell();
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Editor).SeedAsync();

        var response = await Put(
            Client(actor),
            cell.Value.Id,
            BodyFor(cell.Value) with { Id = Guid.Empty, Value = "after" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Update_BroadcastsToTheMselAndTheAdminGroup()
    {
        var cell = await SeedCell();
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Editor).SeedAsync();

        await Put(Client(actor), cell.Value.Id, BodyFor(cell.Value) with { Value = "after" });

        Assert.Equal(
            [cell.Msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.DataValueUpdated));

        var sent = (DataValue)Hub.Of(MainHubMethods.DataValueUpdated)[0].Payload;

        Assert.Equal(cell.Value.Id, sent.Id);
        Assert.Equal("after", sent.Value);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT dataValues/{id} - the xAPI statement
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_TickingACheckbox_RecordsAnXApiStatement()
    {
        var cell = await SeedCell(DataFieldType.Checkbox, value: "false");
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Owner).SeedAsync();

        await Put(Client(actor), cell.Value.Id, BodyFor(cell.Value) with { Value = "true" });

        await Factory.XApi.Received(1).RecordCheckboxChangeAsync(
            cell.Msel.Id,
            cell.Event.Id,
            cell.Field.Id,
            cell.Field.Name,
            true,
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Anything other than the exact string <c>"true"</c> is recorded as unchecked, whatever the grid
    /// makes of it.
    /// </summary>
    /// <remarks>
    /// Characterization. <c>isChecked</c> is <c>Value?.ToLower() == "true"</c>, so a client that stores a
    /// checkbox as <c>"1"</c> - which nothing here forbids, the column is free text - has every tick
    /// recorded in the learning-record store as an untick. The statement is still sent, which is what
    /// makes this worse than sending nothing: the record is wrong rather than absent. Turns red when the
    /// value is parsed rather than compared.
    /// </remarks>
    [Fact]
    public async Task Update_ACheckboxSetToOne_IsRecordedAsUnchecked()
    {
        var cell = await SeedCell(DataFieldType.Checkbox, value: "0");
        var actor = await Actor().OnMsel(cell.Msel, MselRole.Owner).SeedAsync();

        await Put(Client(actor), cell.Value.Id, BodyFor(cell.Value) with { Value = "1" });

        await Factory.XApi.Received(1).RecordCheckboxChangeAsync(
            cell.Msel.Id,
            cell.Event.Id,
            cell.Field.Id,
            cell.Field.Name,
            false,
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A PUT that leaves the value alone records nothing, so the store holds changes rather than saves.
    /// </summary>
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

    /// <summary>
    /// A checkbox on an inject records nothing, because a statement needs both an MSEL and a scenario
    /// event to place the activity in and an inject has neither.
    /// </summary>
    /// <remarks>
    /// Not a defect on its own - there is no MSEL to attribute the statement to - but worth pinning:
    /// injects are the reusable half of the data model, so as more of an exercise moves into them, more of
    /// it stops being recorded. Both of the two guards fail here, not one.
    /// </remarks>
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

    /// <summary>
    /// Deleting a value on an inject is a 500 for anyone without <c>EditMsels</c>, for the same reason
    /// creating one is.
    /// </summary>
    /// <remarks>
    /// Characterization. The MSEL is resolved from the data field, an inject type's field has none, and
    /// <c>MselOwnerRequirement</c> dereferences the MSEL it cannot find. Turns red when the requirement
    /// guards its lookup.
    /// </remarks>
    [Fact]
    public async Task Delete_AValueOnAnInject_Is500()
    {
        var cell = await SeedInjectCell();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/dataValues/{cell.Value.Id}", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
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
            Hub.Recipients(MainHubMethods.DataValueDeleted));
        Assert.Equal(cell.Value.Id, Hub.Of(MainHubMethods.DataValueDeleted)[0].Payload);
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

        var response = await AnonymousClient.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// One cell of an MSEL's grid, and everything it hangs off.
    /// </summary>
    private sealed record Cell(
        MselEntity Msel,
        ScenarioEventEntity Event,
        DataFieldEntity Field,
        DataValueEntity Value);

    /// <summary>
    /// The other kind of cell: a value on an inject, whose data field belongs to an inject type and so to
    /// no MSEL at all.
    /// </summary>
    private sealed record InjectCell(
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

    private static ValueBody Body(
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

    private static ValueBody BodyFor(DataValueEntity dataValue) => new()
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
    private async Task<Cell> SeedCell(
        DataFieldType dataType = DataFieldType.String,
        string value = "before",
        Guid? createdBy = null)
    {
        var msel = BlueprintAppFactory.Msel(createdBy: createdBy);
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id, dataType: dataType);
        var scenarioEvent = BlueprintAppFactory.ScenarioEvent(msel.Id);
        await Seed(field, scenarioEvent);

        var dataValue = BlueprintAppFactory.DataValue(field.Id, scenarioEvent.Id, value);
        await Seed(dataValue);

        return new Cell(msel, scenarioEvent, field, dataValue);
    }

    private async Task<InjectCell> SeedInjectCell(
        DataFieldType dataType = DataFieldType.String,
        string value = "before")
    {
        var injectType = BlueprintAppFactory.InjectType();
        await Seed(injectType);

        var field = BlueprintAppFactory.DataField(injectTypeId: injectType.Id, dataType: dataType);
        var inject = BlueprintAppFactory.Inject(injectType.Id);
        await Seed(field, inject);

        var dataValue = BlueprintAppFactory.DataValueOnInject(field.Id, inject.Id, value);
        await Seed(dataValue);

        return new InjectCell(injectType, inject, field, dataValue);
    }

    private Task<HttpResponseMessage> Post(HttpClient client, ValueBody body) =>
        client.PostAsJsonAsync("/api/dataValues", body, Ct);

    private Task<HttpResponseMessage> Put(HttpClient client, Guid id, ValueBody body) =>
        client.PutAsJsonAsync($"/api/dataValues/{id}", body, Ct);

    private async Task<List<DataValue>> GetValues(HttpClient client, Guid mselId)
    {
        var response = await client.GetAsync($"/api/msels/{mselId}/datavalues", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<List<DataValue>>(response);
    }

    private async Task<DataValue> GetValue(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/dataValues/{id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<DataValue>(response);
    }

    private async Task<DataValueEntity> Stored(Guid id)
    {
        await using var context = NewContext();

        return await context.DataValues.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, Ct);
    }

    private Task AssertRecordedNothing() =>
        Factory.XApi.DidNotReceiveWithAnyArgs()
            .RecordCheckboxChangeAsync(default, default, default, default, default, default);

    private async Task<ApiError> ReadError(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions, Ct);

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }
}
