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
using Blueprint.Api.Tests.Infrastructure;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// The two bulk <c>scenarioEvents</c> routes, and the Steamfitter task that hangs off a row. Both routes
/// fill an MSEL's timeline from somewhere else: <c>fromInjects</c> from the reusable inject library,
/// <c>copy</c> from another MSEL. Both have to reconcile two sets of data fields to do it, and that
/// reconciliation is where they differ from one another and from the single-row routes.
/// </summary>
/// <remarks>
/// <para>
/// The shared shape: match a source field to a destination field by <em>name and data type</em>, create it
/// if there is none, then write one cell per destination field. Neither route asks the caller which fields
/// to use, so calling either one edits the MSEL's column layout as a side effect.
/// </para>
/// <para>
/// Seven characterizations, in rough order of how much they matter.
/// </para>
/// <para>
/// <c>copy</c> silently loses the source row's Steamfitter task, twice over. It builds the destination task
/// as a <c>ViewModels.SteamfitterTask</c> - the view model, not the entity, both being in scope - assigns
/// it to a local and never adds it to the context, so nothing is ever written
/// (<see cref="Copy_LosesTheSourceRowsSteamfitterTask"/>). And the branch that does the building is guarded
/// on <c>SteamfitterTaskId</c>, a denormalized column only <c>UpdateAsync</c> back-fills, so for a row
/// nobody has edited since the task was created the branch does not even run
/// (<see cref="Copy_ForARowWhoseTaskIdWasNeverBackfilled_DoesNotEvenTry"/>). It also assigns a fresh
/// <c>Guid</c> over the <em>source</em> row's <c>SteamfitterTaskId</c> on the way past. <c>MselService</c>
/// copies a whole MSEL correctly one file over, at <c>MselService.cs:561-567</c>.
/// </para>
/// <para>
/// <c>copy</c> reads <c>scenarioEventIdList[0]</c> before checking anything, so an empty list is a 500
/// (<see cref="Copy_WithAnEmptyList_Is500"/>), and it then dereferences the source MSEL it may not have
/// found, so an unknown id is a 500 as well - the <c>if (sourceMsel == null)</c> two lines further down is
/// dead code (<see cref="Copy_ForAnUnknownScenarioEvent_Is500"/>).
/// </para>
/// <para>
/// <c>CreateFromInjectsForm.AddDataFields</c> is declared and never read
/// (<see cref="CreateFromInjects_IgnoresTheAddDataFieldsFlag"/>), so the caller's only means of saying
/// "leave my columns alone" does nothing. An empty inject list adds the Name and Description fields and
/// nothing else (<see cref="CreateFromInjects_WithAnEmptyInjectList_StillAddsTheNameAndDescriptionFields"/>).
/// </para>
/// <para>
/// Neither route sets <c>CreatedBy</c> on everything it creates: <c>fromInjects</c> leaves it empty on the
/// scenario event itself (<see cref="CreateFromInjects_LeavesTheNewRowsCreatedByEmpty"/>) and both leave it
/// empty on any data option they copy (<see cref="CreateFromInjects_LeavesTheNewDataOptionsCreatedByEmpty"/>).
/// <c>CreatedBy</c> is a non-nullable <c>Guid</c>, so "unset" is the all-zeros id rather than null.
/// </para>
/// <para>
/// <c>copy</c> gives every copied row <c>GroupOrder = 0</c> and never calls the reordering helper, so a
/// multi-row copy lands them all on top of each other and on top of whatever the destination already held
/// (<see cref="Copy_PutsEveryCopiedRowAtPositionZero"/>). It also never marks the destination MSEL modified
/// (<see cref="Copy_DoesNotStampTheDestinationMselAsModified"/>), which <c>fromInjects</c> does.
/// </para>
/// <para>
/// One thing that is <em>right</em> here and wrong in <c>CreateAsync</c>: <c>fromInjects</c> calls
/// <c>ReorderScenarioEvents</c> <em>after</em> saving each row, so the reorder reads a server-stamped
/// <c>DateCreated</c> and the documented append actually happens
/// (<see cref="CreateFromInjects_AppendsEachNewRowToTheEndOfItsGroup"/>). Same helper, same argument, one
/// line's difference in placement - see <c>ScenarioEventOrderingTests</c>.
/// </para>
/// </remarks>
public class ScenarioEventFromInjectsTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // POST scenarioEvents/fromInjects
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateFromInjects_ForAnMselOwner_CreatesOneRowPerInject()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var source = await SeedInjects(2);

        var rows = await FromInjects(Client(actor), msel, source);

        Assert.Equal(2, rows.Count);
        Assert.Equal(
            source.Injects.Select(x => x.Id).OrderBy(x => x),
            rows.Select(x => x.InjectId.Value).OrderBy(x => x));
        Assert.All(rows, row =>
        {
            Assert.Equal(msel.Id, row.MselId);
            Assert.Equal(EventType.Inject, row.ScenarioEventType);
            Assert.Equal(0, row.DeltaSeconds);
        });
    }

    [Fact]
    public async Task CreateFromInjects_CopiesTheInjectsNameAndDescriptionIntoCells()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var source = await SeedInjects(1);

        await FromInjects(Client(actor), msel, source);

        var cells = await CellsByFieldName(msel.Id);

        Assert.Equal(source.Injects[0].Name, cells["Name"]);
        Assert.Equal(source.Injects[0].Description, cells["Description"]);
    }

    [Fact]
    public async Task CreateFromInjects_CopiesTheInjectsOwnCells()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var source = await SeedInjects(1, injectFieldValue: "from the inject");

        await FromInjects(Client(actor), msel, source);

        var cells = await CellsByFieldName(msel.Id);

        Assert.Equal("from the inject", cells[source.Field.Name]);
    }

    [Fact]
    public async Task CreateFromInjects_CreatesTheMselsMissingDataFields()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var source = await SeedInjects(1);

        Assert.Empty(await FieldsOn(msel.Id));

        await FromInjects(Client(actor), msel, source);

        var fields = await FieldsOn(msel.Id);

        Assert.Equal(
            ["Description", "Name", source.Field.Name],
            fields.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task CreateFromInjects_NumbersTheNewFieldsAfterTheMselsExistingOnes()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        await Seed(BlueprintAppFactory.DataField(mselId: msel.Id, displayOrder: 7, name: "already here"));
        var source = await SeedInjects(1);

        await FromInjects(Client(actor), msel, source);

        var fields = await FieldsOn(msel.Id);

        Assert.Equal(
            [7, 8, 9, 10],
            fields.Select(x => x.DisplayOrder).OrderBy(x => x));
    }

    [Fact]
    public async Task CreateFromInjects_ReusesAnExistingFieldOfTheSameNameAndType()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var existing = BlueprintAppFactory.DataField(
            mselId: msel.Id,
            dataType: DataFieldType.String,
            name: "Name");
        await Seed(existing);
        var source = await SeedInjects(1);

        await FromInjects(Client(actor), msel, source);

        var fields = await FieldsOn(msel.Id);

        Assert.Single(fields.Where(x => x.Name == "Name").ToList());
        Assert.Equal(existing.Id, fields.Single(x => x.Name == "Name").Id);
    }

    /// <summary>
    /// Matching is on name <em>and</em> data type, so a column called Name that holds something other
    /// than a string does not count as the Name column.
    /// </summary>
    [Fact]
    public async Task CreateFromInjects_CreatesASecondFieldWhenTheNameMatchesButTheTypeDoesNot()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        await Seed(BlueprintAppFactory.DataField(
            mselId: msel.Id,
            dataType: DataFieldType.Integer,
            name: "Name"));
        var source = await SeedInjects(1);

        await FromInjects(Client(actor), msel, source);

        var named = (await FieldsOn(msel.Id)).Where(x => x.Name == "Name").ToList();

        Assert.Equal(2, named.Count);
        Assert.Contains(DataFieldType.Integer, named.Select(x => x.DataType));
        Assert.Contains(DataFieldType.String, named.Select(x => x.DataType));
    }

    [Fact]
    public async Task CreateFromInjects_CopiesTheOptionsOfAChosenFromListField()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var source = await SeedInjects(1, isChosenFromList: true, optionNames: ["red", "green"]);

        await FromInjects(Client(actor), msel, source);

        var field = (await FieldsOn(msel.Id)).Single(x => x.Name == source.Field.Name);

        Assert.Equal(
            ["green", "red"],
            (await OptionsOn(field.Id)).Select(x => x.OptionName).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task CreateFromInjects_DoesNotCopyTheOptionsOfAFreeTextField()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var source = await SeedInjects(1, isChosenFromList: false, optionNames: ["red", "green"]);

        await FromInjects(Client(actor), msel, source);

        var field = (await FieldsOn(msel.Id)).Single(x => x.Name == source.Field.Name);

        Assert.Empty(await OptionsOn(field.Id));
    }

    /// <summary>
    /// An HTML field is created hidden from both the scenario-event list and the exercise view, on the
    /// grounds that a column of markup is unreadable in a grid. Every other type is shown in both.
    /// </summary>
    [Theory]
    [InlineData(DataFieldType.Html, false)]
    [InlineData(DataFieldType.String, true)]
    [InlineData(DataFieldType.Integer, true)]
    public async Task CreateFromInjects_ShowsEveryNewFieldExceptAnHtmlOne(
        DataFieldType dataType,
        bool expected)
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var source = await SeedInjects(1, injectFieldType: dataType);

        await FromInjects(Client(actor), msel, source);

        var field = (await FieldsOn(msel.Id)).Single(x => x.Name == source.Field.Name);

        Assert.Equal(expected, field.OnScenarioEventList);
        Assert.Equal(expected, field.OnExerciseView);
    }

    /// <summary>
    /// The Name and Description columns are always shown, whatever the inject type's own fields do.
    /// </summary>
    [Fact]
    public async Task CreateFromInjects_ShowsTheNameAndDescriptionFields()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var source = await SeedInjects(1, injectFieldType: DataFieldType.Html);

        await FromInjects(Client(actor), msel, source);

        var fields = (await FieldsOn(msel.Id)).Where(x => x.Name is "Name" or "Description").ToList();

        Assert.Equal(2, fields.Count);
        Assert.All(fields, field =>
        {
            Assert.True(field.OnScenarioEventList);
            Assert.True(field.OnExerciseView);
        });
    }

    /// <summary>
    /// The response is the MSEL's whole timeline, not the rows this call added - the same shape
    /// <c>POST scenarioEvents</c> answers with, for the same reason: a create renumbers its siblings.
    /// </summary>
    [Fact]
    public async Task CreateFromInjects_ReturnsEveryRowOfTheMsel()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var existing = BlueprintAppFactory.ScenarioEvent(msel.Id, groupOrder: 0);
        await Seed(existing);
        var source = await SeedInjects(1);

        var rows = await FromInjects(Client(actor), msel, source);

        Assert.Equal(2, rows.Count);
        Assert.Contains(existing.Id, rows.Select(x => x.Id));
    }

    /// <summary>
    /// This caller reorders <em>after</em> saving the row, so the helper reads the server-stamped
    /// <c>DateCreated</c> and each new row is appended to the end of its group. <c>CreateAsync</c> reorders
    /// before saving and therefore inserts at the head - see
    /// <c>ScenarioEventOrderingTests.Create_WithoutADateCreated_IsInsertedAtTheHeadInstead</c>. One line's
    /// difference in placement.
    /// </summary>
    [Fact]
    public async Task CreateFromInjects_AppendsEachNewRowToTheEndOfItsGroup()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        await Seed(BlueprintAppFactory.ScenarioEvent(msel.Id, groupOrder: 0));
        var source = await SeedInjects(2);

        await FromInjects(Client(actor), msel, source);

        var orders = await Orders(msel.Id);

        Assert.Equal([0, 1, 2], orders);
    }

    [Fact]
    public async Task CreateFromInjects_StampsTheMselAsModified()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var source = await SeedInjects(1);
        var before = DateTime.UtcNow;

        await FromInjects(Client(actor), msel, source);

        var stored = await StoredMsel(msel.Id);

        Assert.Equal(actor.Id, stored.ModifiedBy);
        Assert.InRange(stored.DateModified.Value, before, DateTime.UtcNow);
    }

    /// <summary>
    /// The new row is built without a <c>CreatedBy</c>, so it records the all-zeros id rather than the
    /// caller - unlike every cell the same loop creates, and unlike <c>CreateAsync</c>, which sets it.
    /// Turns red when the service sets it.
    /// </summary>
    [Fact]
    public async Task CreateFromInjects_LeavesTheNewRowsCreatedByEmpty()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var source = await SeedInjects(1);

        var rows = await FromInjects(Client(actor), msel, source);

        var stored = await Stored(rows.Single().Id);

        Assert.Equal(Guid.Empty, stored.CreatedBy);
        Assert.Equal(actor.Id, (await CellsOn(stored.Id)).First().CreatedBy);
    }

    /// <summary>
    /// The copied data options are built without a <c>CreatedBy</c> either. Turns red when the service
    /// sets it.
    /// </summary>
    [Fact]
    public async Task CreateFromInjects_LeavesTheNewDataOptionsCreatedByEmpty()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var source = await SeedInjects(1, isChosenFromList: true, optionNames: ["red"]);

        await FromInjects(Client(actor), msel, source);

        var field = (await FieldsOn(msel.Id)).Single(x => x.Name == source.Field.Name);

        Assert.Equal(Guid.Empty, (await OptionsOn(field.Id)).Single().CreatedBy);
    }

    /// <summary>
    /// <c>CreateFromInjectsForm.AddDataFields</c> is declared on the form and read nowhere, so the flag
    /// that exists to say "do not touch my columns" is ignored and the columns are added anyway. Turns red
    /// when the service honours it.
    /// </summary>
    [Fact]
    public async Task CreateFromInjects_IgnoresTheAddDataFieldsFlag()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var source = await SeedInjects(1);

        await FromInjects(Client(actor), msel, source, addDataFields: false);

        Assert.Equal(3, (await FieldsOn(msel.Id)).Count);
    }

    /// <summary>
    /// The fields are created before the inject loop and outside any decision about it, so a call that
    /// creates no rows still rewrites the MSEL's column layout and marks it modified. Turns red when the
    /// service returns early on an empty list.
    /// </summary>
    [Fact]
    public async Task CreateFromInjects_WithAnEmptyInjectList_StillAddsTheNameAndDescriptionFields()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var source = await SeedInjects(0);

        var rows = await FromInjects(Client(actor), msel, source);

        Assert.Empty(rows);
        Assert.Equal(
            ["Description", "Name", source.Field.Name],
            (await FieldsOn(msel.Id)).Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>
    /// The form names an inject type and a list of injects and nothing checks that the injects are of that
    /// type, so a cell whose field belongs to another inject type misses the dictionary and the request is
    /// a 500. Turns red when the service validates the pairing, or skips the unmapped cell.
    /// </summary>
    [Fact]
    public async Task CreateFromInjects_ForAnInjectOfAnotherInjectType_Is500()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var declared = await SeedInjects(0);
        var other = await SeedInjects(1, injectFieldValue: "belongs to another type");

        var response = await Post(
            Client(actor),
            msel.Id,
            declared.InjectType.Id,
            other.Injects.Select(x => x.Id));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task CreateFromInjects_ForAnUnknownInject_Is404AndCreatesNothing()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var source = await SeedInjects(1);

        var response = await Post(
            Client(actor),
            msel.Id,
            source.InjectType.Id,
            [source.Injects[0].Id, Guid.NewGuid()]);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await Rows(msel.Id));
        Assert.Empty(await FieldsOn(msel.Id));
    }

    [Fact]
    public async Task CreateFromInjects_ForAnUnknownInjectType_Is404()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), msel.Id, Guid.NewGuid(), []);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The permission check runs first and <c>MselOwnerRequirement</c> dereferences the MSEL it did not
    /// find, so an unknown MSEL is a 500 for an ordinary caller and the 404 three lines below is
    /// unreachable for them. Turns red when the requirement guards its lookup - the same defect
    /// <c>MselOwnerRequirementTests</c> pins directly.
    /// </summary>
    [Fact]
    public async Task CreateFromInjects_ForAnUnknownMsel_Is500()
    {
        var actor = await Actor().SeedAsync();

        var response = await Post(Client(actor), Guid.NewGuid(), Guid.NewGuid(), []);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// A caller holding <c>EditMsels</c> skips the requirement altogether and so reaches the 404 the
    /// service means to answer with.
    /// </summary>
    [Fact]
    public async Task CreateFromInjects_ForAnUnknownMsel_WithEditMsels_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(Client(actor), Guid.NewGuid(), Guid.NewGuid(), []);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Filling a timeline from the inject library is owner-only: none of the other three MSEL roles will
    /// do, including the two that may edit a row that already exists.
    /// </summary>
    [Theory]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.Evaluator)]
    [InlineData(MselRole.Viewer)]
    public async Task CreateFromInjects_ForARoleOtherThanOwner_Is403(MselRole role)
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, role).SeedAsync();
        var source = await SeedInjects(1);

        var response = await Post(
            Client(actor),
            msel.Id,
            source.InjectType.Id,
            source.Injects.Select(x => x.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateFromInjects_ForTheMselCreator_Succeeds()
    {
        var creator = await Actor().SeedAsync();
        var msel = await SeedMsel(createdBy: creator.Id);
        var source = await SeedInjects(1);

        var rows = await FromInjects(Client(creator), msel, source);

        Assert.Single(rows);
    }

    [Fact]
    public async Task CreateFromInjects_WithEditMsels_Succeeds()
    {
        var msel = await SeedMsel();
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var source = await SeedInjects(1);

        var rows = await FromInjects(Client(actor), msel, source);

        Assert.Single(rows);
    }

    [Fact]
    public async Task CreateFromInjects_BroadcastsACreateForEachNewRow()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var source = await SeedInjects(2);

        await FromInjects(Client(actor), msel, source);

        Assert.Equal(2, Broadcast(MainHubMethods.ScenarioEventCreated, msel).Length);
    }

    // ---------------------------------------------------------------------------------------------
    // POST msels/{mselId}/scenarioEvents/copy
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Copy_ForTheOwnerOfBothMsels_CopiesEachRow()
    {
        var scene = await SeedCopy(rows: 2);

        var rows = await Copy(Client(scene.Actor), scene.Destination, scene.Rows);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(scene.Destination.Id, row.MselId));
        Assert.Empty(rows.Select(x => x.Id).Intersect(scene.Rows.Select(x => x.Id)));
        Assert.Equal(
            scene.Rows.Select(x => x.Information).OrderBy(x => x, StringComparer.Ordinal),
            rows.Select(x => x.Information).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Copy_CarriesTheRowsTimeAndTypeAcross()
    {
        var scene = await SeedCopy(rows: 1, deltaSeconds: 3600);

        var rows = await Copy(Client(scene.Actor), scene.Destination, scene.Rows);

        var row = rows.Single();

        Assert.Equal(3600, row.DeltaSeconds);
        Assert.Equal(scene.Rows[0].ScenarioEventType, row.ScenarioEventType);
        Assert.Equal(scene.Rows[0].InjectId, row.InjectId);
    }

    [Fact]
    public async Task Copy_CreatesTheDestinationsMissingFields()
    {
        var scene = await SeedCopy(rows: 1);

        Assert.Empty(await FieldsOn(scene.Destination.Id));

        await Copy(Client(scene.Actor), scene.Destination, scene.Rows);

        var field = Assert.Single(await FieldsOn(scene.Destination.Id));

        Assert.Equal(scene.Field.Name, field.Name);
        Assert.Equal(scene.Field.DataType, field.DataType);
    }

    /// <summary>
    /// Only fields the copied rows actually hold a value in are created, so a column that is empty on
    /// every row of the selection does not come across at all.
    /// </summary>
    [Fact]
    public async Task Copy_DoesNotCreateAFieldThatIsEmptyOnEveryCopiedRow()
    {
        var scene = await SeedCopy(rows: 1, value: null);

        await Copy(Client(scene.Actor), scene.Destination, scene.Rows);

        Assert.Empty(await FieldsOn(scene.Destination.Id));
    }

    [Fact]
    public async Task Copy_ReusesAnExistingDestinationFieldOfTheSameNameAndType()
    {
        var scene = await SeedCopy(rows: 1);
        var existing = BlueprintAppFactory.DataField(
            mselId: scene.Destination.Id,
            dataType: scene.Field.DataType,
            name: scene.Field.Name);
        await Seed(existing);

        await Copy(Client(scene.Actor), scene.Destination, scene.Rows);

        Assert.Equal(existing.Id, (await FieldsOn(scene.Destination.Id)).Single().Id);
    }

    [Fact]
    public async Task Copy_CopiesTheCellValues()
    {
        var scene = await SeedCopy(rows: 1, value: "carried across");

        await Copy(Client(scene.Actor), scene.Destination, scene.Rows);

        Assert.Equal("carried across", (await CellsByFieldName(scene.Destination.Id))[scene.Field.Name]);
    }

    /// <summary>
    /// A copied row gets a cell for every field of the destination MSEL, not only the fields the copy
    /// brought with it, so the row arrives with a full set of columns and blanks where it has nothing to
    /// say.
    /// </summary>
    [Fact]
    public async Task Copy_GivesTheCopiedRowACellForEveryDestinationField()
    {
        var scene = await SeedCopy(rows: 1);
        await Seed(BlueprintAppFactory.DataField(mselId: scene.Destination.Id, name: "unrelated"));

        var rows = await Copy(Client(scene.Actor), scene.Destination, scene.Rows);

        var cells = await CellsOn(rows.Single().Id);

        Assert.Equal(2, cells.Count);
        Assert.Single(cells.Where(x => x.Value is null).ToList());
    }

    [Fact]
    public async Task Copy_RecordsTheCallerAsTheCreatorOfEachCopiedRow()
    {
        var scene = await SeedCopy(rows: 1);

        var rows = await Copy(Client(scene.Actor), scene.Destination, scene.Rows);

        Assert.Equal(scene.Actor.Id, (await Stored(rows.Single().Id)).CreatedBy);
    }

    /// <summary>
    /// Every copied row is built with <c>GroupOrder = 0</c> and the reordering helper is never called, so
    /// a two-row copy puts both rows at position zero, on top of each other and on top of whatever the
    /// destination already held at that time. Nothing stops it: there is no unique index on
    /// (MSEL, time, position). Turns red when the service reorders, as <c>fromInjects</c> does.
    /// </summary>
    [Fact]
    public async Task Copy_PutsEveryCopiedRowAtPositionZero()
    {
        var scene = await SeedCopy(rows: 2);
        await Seed(BlueprintAppFactory.ScenarioEvent(scene.Destination.Id, groupOrder: 0));

        await Copy(Client(scene.Actor), scene.Destination, scene.Rows);

        var orders = await Orders(scene.Destination.Id);

        Assert.Equal([0, 0, 0], orders);
    }

    /// <summary>
    /// A copy does not mark the destination MSEL modified, although <c>fromInjects</c> - which fills the
    /// same timeline from the other source - does. Turns red when the service stamps it.
    /// </summary>
    [Fact]
    public async Task Copy_DoesNotStampTheDestinationMselAsModified()
    {
        var scene = await SeedCopy(rows: 1);

        await Copy(Client(scene.Actor), scene.Destination, scene.Rows);

        var stored = await StoredMsel(scene.Destination.Id);

        Assert.Null(stored.ModifiedBy);
        Assert.Null(stored.DateModified);
    }

    /// <summary>
    /// The source MSEL is looked up through <c>scenarioEventIdList[0]</c> before anything is validated, so
    /// an empty list is an index-out-of-range and a 500. The route's <c>[Required]</c> catches a null body
    /// and not an empty one. Turns red when the service checks the list.
    /// </summary>
    [Fact]
    public async Task Copy_WithAnEmptyList_Is500()
    {
        var scene = await SeedCopy(rows: 1);

        var response = await PostCopy(Client(scene.Actor), scene.Destination.Id, []);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// The source lookup can return null and the next use of it is a dereference, so an unknown scenario
    /// event id is a 500 - and the <c>if (sourceMsel == null)</c> that follows is dead code, unreachable
    /// by any input. Turns red when the service checks before dereferencing.
    /// </summary>
    [Fact]
    public async Task Copy_ForAnUnknownScenarioEvent_Is500()
    {
        var scene = await SeedCopy(rows: 1);

        var response = await PostCopy(Client(scene.Actor), scene.Destination.Id, [Guid.NewGuid()]);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Copy_ForAnUnknownDestinationMsel_Is404()
    {
        var scene = await SeedCopy(rows: 1);

        var response = await PostCopy(
            Client(scene.Actor),
            Guid.NewGuid(),
            [scene.Rows[0].Id]);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The source MSEL is whichever one the <em>first</em> id belongs to, and the rest of the list is
    /// matched against that MSEL's rows, so a selection spanning two MSELs fails the count check with a
    /// <c>DataException</c> - a 500, and one whose message says nothing about which id was the odd one.
    /// </summary>
    [Fact]
    public async Task Copy_WithRowsFromTwoMsels_Is500()
    {
        var scene = await SeedCopy(rows: 1);
        var elsewhere = await SeedMsel(createdBy: scene.Actor.Id);
        var stray = BlueprintAppFactory.ScenarioEvent(elsewhere.Id);
        await Seed(stray);

        var response = await PostCopy(
            Client(scene.Actor),
            scene.Destination.Id,
            [scene.Rows[0].Id, stray.Id]);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Copy_ForTheOwnerOfOnlyTheDestination_Is403()
    {
        var scene = await SeedCopy(rows: 1, ownsSource: false);

        var response = await PostCopy(
            Client(scene.Actor),
            scene.Destination.Id,
            [scene.Rows[0].Id]);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Copy_ForTheOwnerOfOnlyTheSource_Is403()
    {
        var scene = await SeedCopy(rows: 1, ownsDestination: false);

        var response = await PostCopy(
            Client(scene.Actor),
            scene.Destination.Id,
            [scene.Rows[0].Id]);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Copy_WithEditMsels_SkipsBothOwnerChecks()
    {
        var scene = await SeedCopy(rows: 1, ownsSource: false, ownsDestination: false);
        var admin = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var rows = await Copy(Client(admin), scene.Destination, scene.Rows);

        Assert.Single(rows);
    }

    /// <summary>
    /// Copying a row back into its own MSEL is not refused; it duplicates the row in place, cells and all.
    /// </summary>
    [Fact]
    public async Task Copy_ToTheSameMsel_DuplicatesTheRowInPlace()
    {
        var scene = await SeedCopy(rows: 1);

        var rows = await Copy(Client(scene.Actor), scene.Source, scene.Rows);

        Assert.Equal(2, rows.Count);
        Assert.Equal(
            [scene.Rows[0].Information, scene.Rows[0].Information],
            rows.Select(x => x.Information));
        Assert.Single(await FieldsOn(scene.Source.Id));
    }

    /// <summary>
    /// Each copied cell is read with <c>Single</c>, so a source row holding two values for one field is a
    /// 500. The database permits that pair: the unique index on <c>data_values</c> includes the always-null
    /// <c>inject_id</c>, and a Postgres unique index does not dedupe rows with a null in it. Turns red
    /// when either the query or the index is fixed.
    /// </summary>
    [Fact]
    public async Task Copy_ForARowHoldingTwoValuesForOneField_Is500()
    {
        var scene = await SeedCopy(rows: 1);
        await Seed(BlueprintAppFactory.DataValue(scene.Field.Id, scene.Rows[0].Id, "the second one"));

        var response = await PostCopy(
            Client(scene.Actor),
            scene.Destination.Id,
            [scene.Rows[0].Id]);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Copy_BroadcastsACreateForEachCopiedRow()
    {
        var scene = await SeedCopy(rows: 2);

        await Copy(Client(scene.Actor), scene.Destination, scene.Rows);

        Assert.Equal(2, Broadcast(MainHubMethods.ScenarioEventCreated, scene.Destination).Length);
    }

    // ---------------------------------------------------------------------------------------------
    // The Steamfitter task on a row
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ReturnsTheRowsSteamfitterTask()
    {
        var scene = await SeedCopy(rows: 1, withSteamfitterTask: true);

        var row = await Read<ScenarioEvent>(
            await Client(scene.Actor).GetAsync($"/api/scenarioEvents/{scene.Rows[0].Id}", Ct));

        Assert.NotNull(row.SteamfitterTask);
        Assert.Equal(scene.Task.Name, row.SteamfitterTask.Name);
        Assert.Equal(scene.Task.TaskType, row.SteamfitterTask.TaskType);
        Assert.Equal(scene.Task.Iterations, row.SteamfitterTask.Iterations);
    }

    /// <summary>
    /// A task arriving nested inside a create is written, because the profile maps the navigation property
    /// - the only route that creates one.
    /// </summary>
    [Fact]
    public async Task Create_WithASteamfitterTask_StoresTheTask()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var rows = await Read<List<ScenarioEvent>>(await Client(actor).PostAsJsonAsync(
            "/api/scenarioEvents",
            new
            {
                MselId = msel.Id,
                ScenarioEventType = EventType.Inject,
                Information = "carries a task",
                CreatedBy = Guid.Empty,
                DateCreated = DateTime.UtcNow,
                SteamfitterTask = new
                {
                    Name = "sent with the row",
                    TaskType = SteamfitterIntegrationType.Email,
                    Action = SteamfitterTaskAction.http_post,
                    TriggerCondition = SteamfitterTaskTrigger.Manual,
                    Iterations = 3,
                    CreatedBy = Guid.Empty,
                    DateCreated = DateTime.UtcNow
                }
            },
            Ct));

        var stored = await StoredTask(rows.Single().Id);

        Assert.Equal("sent with the row", stored.Name);
        Assert.Equal(SteamfitterIntegrationType.Email, stored.TaskType);
        Assert.Equal(3, stored.Iterations);
    }

    /// <summary>
    /// <c>ScenarioEventEntity.SteamfitterTaskId</c> is not the foreign key - the relationship is keyed on
    /// the task's own <c>ScenarioEventId</c> - so it is a denormalized copy, and a create does not set it.
    /// The row and its task are linked; the column that says so is null. Turns red when the create sets it.
    /// </summary>
    [Fact]
    public async Task Create_WithASteamfitterTask_LeavesTheRowsTaskIdNull()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var rows = await Read<List<ScenarioEvent>>(await Client(actor).PostAsJsonAsync(
            "/api/scenarioEvents",
            new
            {
                MselId = msel.Id,
                ScenarioEventType = EventType.Inject,
                Information = "carries a task",
                CreatedBy = Guid.Empty,
                DateCreated = DateTime.UtcNow,
                SteamfitterTask = new
                {
                    Name = "sent with the row",
                    CreatedBy = Guid.Empty,
                    DateCreated = DateTime.UtcNow
                }
            },
            Ct));

        Assert.NotNull(await StoredTask(rows.Single().Id));
        Assert.Null((await Stored(rows.Single().Id)).SteamfitterTaskId);
    }

    /// <summary>
    /// An update maps the whole view model onto the row, <c>SteamfitterTask</c> included, and the row was
    /// loaded with that navigation populated - so a body that does not mention the task orphans it, and
    /// the relationship cascades the orphan into a delete. Editing a scenario event's time from the
    /// timeline grid destroys the Steamfitter work attached to it. Turns red when the profile ignores the
    /// navigation on the inbound map, or the service preserves it.
    /// </summary>
    [Fact]
    public async Task Update_WithoutTheTaskInTheBody_DeletesTheRowsSteamfitterTask()
    {
        var scene = await SeedCopy(rows: 1, withSteamfitterTask: true);

        var response = await Update(scene, deltaSeconds: 60);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await StoredTask(scene.Rows[0].Id));
    }

    /// <summary>
    /// The back-fill of <c>SteamfitterTaskId</c> reads the task back from its own foreign key after saving,
    /// so it only ever sees a task the same request just wrote - which makes it reachable when the body
    /// carries the task and dead when it does not, the delete above having removed the only other
    /// candidate. This is the one path that puts a value in that column, and it is what makes
    /// <see cref="Copy_ForARowWhoseTaskIdWasNeverBackfilled_DoesNotEvenTry"/> the ordinary case rather
    /// than an edge one.
    /// </summary>
    [Fact]
    public async Task Update_WithTheTaskInTheBody_BackfillsTheRowsSteamfitterTaskId()
    {
        var scene = await SeedCopy(rows: 1, withSteamfitterTask: true);

        Assert.Null((await Stored(scene.Rows[0].Id)).SteamfitterTaskId);

        var response = await Update(scene, deltaSeconds: 60, steamfitterTask: new
        {
            scene.Task.Id,
            ScenarioEventId = scene.Rows[0].Id,
            Name = "still here",
            scene.Task.TaskType,
            scene.Task.Action,
            scene.Task.TriggerCondition,
            CreatedBy = Guid.Empty,
            DateCreated = DateTime.UtcNow
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await StoredTask(scene.Rows[0].Id);

        Assert.Equal("still here", stored.Name);
        Assert.Equal(stored.Id, (await Stored(scene.Rows[0].Id)).SteamfitterTaskId);
    }

    [Fact]
    public async Task Delete_AlsoDeletesTheRowsSteamfitterTask()
    {
        var scene = await SeedCopy(rows: 1, withSteamfitterTask: true);

        var response = await Client(scene.Actor)
            .DeleteAsync($"/api/scenarioEvents/{scene.Rows[0].Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await StoredTask(scene.Rows[0].Id));
    }

    /// <summary>
    /// The destination task is built as a <c>ViewModels.SteamfitterTask</c>, assigned to a local and never
    /// added to the context, so the copy silently loses it: the copied row arrives with no task at all.
    /// The service also writes a fresh <c>Guid</c> over the <em>source</em> row's <c>SteamfitterTaskId</c>
    /// on the way past, which the source only survives because it was read <c>AsNoTracking</c>.
    /// <c>MselService.cs:561-567</c> does this correctly for a whole-MSEL copy. Turns red when the service
    /// builds an entity and adds it.
    /// </summary>
    [Fact]
    public async Task Copy_LosesTheSourceRowsSteamfitterTask()
    {
        var scene = await SeedCopy(rows: 1, withSteamfitterTask: true, backfillTaskId: true);

        var rows = await Copy(Client(scene.Actor), scene.Destination, scene.Rows);

        var copied = rows.Single();

        Assert.Null(copied.SteamfitterTask);
        Assert.Null(await StoredTask(copied.Id));
        Assert.NotNull(await StoredTask(scene.Rows[0].Id));
    }

    /// <summary>
    /// The branch that would have copied the task is guarded on <c>SteamfitterTaskId</c>, which only an
    /// update back-fills, so for a row nobody has edited since its task was created the guard is false and
    /// the copy does not even attempt it. Two independent reasons for the same lost task; see
    /// <see cref="Copy_LosesTheSourceRowsSteamfitterTask"/> for the other. Turns red when the guard reads
    /// the navigation property instead.
    /// </summary>
    [Fact]
    public async Task Copy_ForARowWhoseTaskIdWasNeverBackfilled_DoesNotEvenTry()
    {
        var scene = await SeedCopy(rows: 1, withSteamfitterTask: true);

        Assert.Null((await Stored(scene.Rows[0].Id)).SteamfitterTaskId);

        var rows = await Copy(Client(scene.Actor), scene.Destination, scene.Rows);

        Assert.Null(await StoredTask(rows.Single().Id));
    }

    // ---------------------------------------------------------------------------------------------
    // Authentication
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/api/scenarioEvents/fromInjects")]
    [InlineData("/api/msels/00000000-0000-0000-0000-000000000001/scenarioEvents/copy")]
    public async Task EveryBulkRoute_WithoutAToken_Is401(string route)
    {
        var response = await AnonymousClient.PostAsJsonAsync(route, new { }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// An inject type, one data field on it, and the injects to draw from - everything
    /// <c>fromInjects</c> reads.
    /// </summary>
    private sealed record InjectSource(
        InjectTypeEntity InjectType,
        DataFieldEntity Field,
        List<InjectEntity> Injects);

    /// <summary>
    /// Two MSELs, a row or two on the first with one field and one cell each, and an actor whose ownership
    /// of each MSEL the test chooses.
    /// </summary>
    private sealed record CopyScene(
        TestActor Actor,
        MselEntity Source,
        MselEntity Destination,
        DataFieldEntity Field,
        List<ScenarioEventEntity> Rows,
        SteamfitterTaskEntity Task);

    private async Task<MselEntity> SeedMsel(Guid? createdBy = null)
    {
        var msel = BlueprintAppFactory.Msel(createdBy: createdBy);
        await Seed(msel);

        return msel;
    }

    /// <summary>
    /// An inject type with one field, and <paramref name="count"/> injects each holding a value in it.
    /// </summary>
    private async Task<InjectSource> SeedInjects(
        int count,
        DataFieldType injectFieldType = DataFieldType.String,
        string injectFieldValue = "seeded",
        bool isChosenFromList = false,
        string[] optionNames = null)
    {
        var injectType = BlueprintAppFactory.InjectType();
        await Seed(injectType);

        var field = BlueprintAppFactory.DataField(
            injectTypeId: injectType.Id,
            dataType: injectFieldType);
        field.IsChosenFromList = isChosenFromList;
        await Seed(field);

        foreach (var optionName in optionNames ?? [])
        {
            await Seed(BlueprintAppFactory.DataOption(field.Id, optionName));
        }

        var injects = new List<InjectEntity>();

        for (var i = 0; i < count; i++)
        {
            var inject = BlueprintAppFactory.Inject(injectType.Id);
            await Seed(inject);
            await Seed(BlueprintAppFactory.DataValueOnInject(field.Id, inject.Id, injectFieldValue));
            injects.Add(inject);
        }

        return new InjectSource(injectType, field, injects);
    }

    /// <summary>
    /// The source and destination of a copy. <paramref name="ownsSource"/> and
    /// <paramref name="ownsDestination"/> are separate because the service demands both and the two halves
    /// of that check are worth failing one at a time.
    /// </summary>
    private async Task<CopyScene> SeedCopy(
        int rows,
        int deltaSeconds = 0,
        string value = "seeded",
        bool ownsSource = true,
        bool ownsDestination = true,
        bool withSteamfitterTask = false,
        bool backfillTaskId = false)
    {
        var actor = await Actor().SeedAsync();
        var source = await SeedMsel(createdBy: ownsSource ? actor.Id : null);
        var destination = await SeedMsel(createdBy: ownsDestination ? actor.Id : null);

        var field = BlueprintAppFactory.DataField(mselId: source.Id);
        await Seed(field);

        var seeded = new List<ScenarioEventEntity>();
        SteamfitterTaskEntity task = null;

        for (var i = 0; i < rows; i++)
        {
            var row = BlueprintAppFactory.ScenarioEvent(source.Id, deltaSeconds, groupOrder: i);
            await Seed(row);
            await Seed(BlueprintAppFactory.DataValue(field.Id, row.Id, value));
            seeded.Add(row);
        }

        if (withSteamfitterTask)
        {
            task = BlueprintAppFactory.SteamfitterTask(seeded[0].Id);
            await Seed(task);

            if (backfillTaskId)
            {
                await using var context = NewContext();
                var stored = await context.ScenarioEvents.SingleAsync(x => x.Id == seeded[0].Id, Ct);
                stored.SteamfitterTaskId = task.Id;
                await context.SaveChangesAsync(Ct);
            }
        }

        Hub.Clear();

        return new CopyScene(actor, source, destination, field, seeded, task);
    }

    private Task<HttpResponseMessage> Post(
        HttpClient client,
        Guid mselId,
        Guid injectTypeId,
        IEnumerable<Guid> injectIds,
        bool addDataFields = true) =>
        client.PostAsJsonAsync(
            "/api/scenarioEvents/fromInjects",
            new
            {
                MselId = mselId,
                InjectTypeId = injectTypeId,
                InjectIdList = injectIds,
                AddDataFields = addDataFields
            },
            Ct);

    private async Task<List<ScenarioEvent>> FromInjects(
        HttpClient client,
        MselEntity msel,
        InjectSource source,
        bool addDataFields = true)
    {
        var response = await Post(
            client,
            msel.Id,
            source.InjectType.Id,
            source.Injects.Select(x => x.Id),
            addDataFields);

        return await Read<List<ScenarioEvent>>(response);
    }

    /// <summary>
    /// A PUT of the scene's first row. <paramref name="steamfitterTask"/> is <c>object</c> rather than a
    /// typed body because the point of these tests is what the service does with the property being
    /// present or absent, and a nullable field cannot express "absent".
    /// </summary>
    private Task<HttpResponseMessage> Update(CopyScene scene, int deltaSeconds, object steamfitterTask = null)
    {
        var row = scene.Rows[0];

        return Client(scene.Actor).PutAsJsonAsync(
            $"/api/scenarioEvents/{row.Id}",
            new
            {
                row.Id,
                row.MselId,
                DeltaSeconds = deltaSeconds,
                row.GroupOrder,
                row.ScenarioEventType,
                Information = "edited",
                CreatedBy = Guid.Empty,
                DateCreated = DateTime.UtcNow,
                SteamfitterTask = steamfitterTask
            },
            Ct);
    }

    private Task<HttpResponseMessage> PostCopy(HttpClient client, Guid mselId, Guid[] ids) =>
        client.PostAsJsonAsync($"/api/msels/{mselId}/scenarioEvents/copy", ids, Ct);

    private async Task<List<ScenarioEvent>> Copy(
        HttpClient client,
        MselEntity destination,
        List<ScenarioEventEntity> rows)
    {
        var response = await PostCopy(client, destination.Id, [.. rows.Select(x => x.Id)]);

        return await Read<List<ScenarioEvent>>(response);
    }

    private async Task<List<ScenarioEventEntity>> Rows(Guid mselId)
    {
        await using var context = NewContext();

        return await context.ScenarioEvents
            .AsNoTracking()
            .Where(x => x.MselId == mselId)
            .OrderBy(x => x.DeltaSeconds)
            .ThenBy(x => x.GroupOrder)
            .ToListAsync(Ct);
    }

    private async Task<int[]> Orders(Guid mselId)
    {
        var rows = await Rows(mselId);

        return [.. rows.Select(x => x.GroupOrder)];
    }

    private async Task<ScenarioEventEntity> Stored(Guid id)
    {
        await using var context = NewContext();

        return await context.ScenarioEvents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, Ct);
    }

    private async Task<SteamfitterTaskEntity> StoredTask(Guid scenarioEventId)
    {
        await using var context = NewContext();

        return await context.SteamfitterTasks
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.ScenarioEventId == scenarioEventId, Ct);
    }

    private async Task<MselEntity> StoredMsel(Guid id)
    {
        await using var context = NewContext();

        return await context.Msels.AsNoTracking().SingleAsync(x => x.Id == id, Ct);
    }

    private async Task<List<DataFieldEntity>> FieldsOn(Guid mselId)
    {
        await using var context = NewContext();

        return await context.DataFields
            .AsNoTracking()
            .Where(x => x.MselId == mselId)
            .OrderBy(x => x.DisplayOrder)
            .ToListAsync(Ct);
    }

    private async Task<List<DataOptionEntity>> OptionsOn(Guid dataFieldId)
    {
        await using var context = NewContext();

        return await context.DataOptions
            .AsNoTracking()
            .Where(x => x.DataFieldId == dataFieldId)
            .ToListAsync(Ct);
    }

    private async Task<List<DataValueEntity>> CellsOn(Guid scenarioEventId)
    {
        await using var context = NewContext();

        return await context.DataValues
            .AsNoTracking()
            .Where(x => x.ScenarioEventId == scenarioEventId)
            .ToListAsync(Ct);
    }

    /// <summary>
    /// Every cell on the MSEL's rows, keyed by the name of the field it belongs to. The tests that use it
    /// seed one row, so one entry per column.
    /// </summary>
    private async Task<Dictionary<string, string>> CellsByFieldName(Guid mselId)
    {
        await using var context = NewContext();

        var pairs = await context.DataValues
            .AsNoTracking()
            .Where(x => x.ScenarioEvent.MselId == mselId)
            .Select(x => new { x.DataField.Name, x.Value })
            .ToListAsync(Ct);

        return pairs.ToDictionary(x => x.Name, x => x.Value);
    }

    private string[] Broadcast(string method, MselEntity msel) =>
        [.. Hub.Of(method).Where(x => x.Group == msel.Id.ToString()).Select(x => x.Method)];

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }
}
