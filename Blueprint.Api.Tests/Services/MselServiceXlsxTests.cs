// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary><c>GET api/msels/{id}/xlsx</c>, <c>POST api/msels/xlsx</c> and <c>PUT api/msels/{id}/xlsx</c> -
/// the spreadsheet a MSEL is actually authored in.</summary>
public class MselServiceXlsxTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // Download
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Download_ReturnsTheMselAsAFileNamedAfterIt()
    {
        var msel = await SeedSheet(["Target"]);
        var actor = await Author();

        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/xlsx", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"{msel.Name}.xlsx", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
    }

    [Fact]
    public async Task Download_OfAMselAlreadyNamedDotXlsx_DoesNotAppendASecondSuffix()
    {
        var msel = await SeedSheet(["Target"], m => m.Name = "Exercise.XLSX");
        var actor = await Author();

        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/xlsx", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Exercise.XLSX", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
    }

    [Fact]
    public async Task Download_OfAnUnknownMsel_Is404()
    {
        var actor = await Author();

        var response = await Client(actor).GetAsync($"/api/msels/{Guid.NewGuid()}/xlsx", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Download_Anonymously_Is401()
    {
        var msel = await SeedSheet(["Target"]);

        var response = await Client().GetAsync($"/api/msels/{msel.Id}/xlsx", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Download with no permission and no role on the MSEL is answered with a 200.</summary>
    [Fact]
    public async Task Download_WithNoPermissionAndNoRoleOnTheMsel_Is200()
    {
        var msel = await SeedSheet(["Target"]);
        await SeedEvent(msel, 0, "the exercise's secret");
        var stranger = await Actor().SeedAsync();

        var sheet = Read(await Download(Client(stranger), msel.Id));

        Assert.Equal("the exercise's secret", sheet.Text(0, "Target"));
    }

    [Fact]
    public async Task Download_WritesTheDataFieldsInDisplayOrder()
    {
        var msel = await SeedSheet(["First", "Second", "Third"]);
        await SeedEvent(msel, 0, "a", "b", "c");
        var actor = await Author();

        var sheet = Read(await Download(Client(actor), msel.Id));

        // Time is the system column and sorts ahead of the fields, which take display orders 1..3.
        Assert.Equal(["Time", "First", "Second", "Third"], sheet.Headings);
        Assert.Equal("a", sheet.Text(0, "First"));
        Assert.Equal("b", sheet.Text(0, "Second"));
        Assert.Equal("c", sheet.Text(0, "Third"));
    }

    /// <summary>The Time column is written whether or not the MSEL asks for a schedule column.</summary>
    [Fact]
    public async Task Download_AlwaysWritesTheTimeColumn()
    {
        var msel = await SeedSheet(["Target"], m =>
        {
            m.ShowMoveOnScenarioEventList = false;
            m.ShowGroupOnScenarioEventList = false;
        });
        await SeedEvent(msel, 3600, "a");
        var actor = await Author();

        var sheet = Read(await Download(Client(actor), msel.Id));

        Assert.Equal(["Time", "Target"], sheet.Headings);
        Assert.Equal("+ 01:00:00", sheet.Text(0, "Time"));
    }

    [Fact]
    public async Task Download_WithMoveAndGroupTurnedOn_WritesThemToo()
    {
        var msel = await SeedSheet(["Target"], m =>
        {
            m.ShowMoveOnScenarioEventList = true;
            m.ShowGroupOnScenarioEventList = true;
            m.MoveDisplayOrder = 8;
            m.GroupDisplayOrder = 9;
        });
        await Seed(new MoveEntity
        {
            Id = Guid.NewGuid(),
            MselId = msel.Id,
            MoveNumber = 1,
            DeltaSeconds = 0,
            CreatedBy = msel.CreatedBy
        });
        await SeedEvent(msel, 0, "first");
        await SeedEvent(msel, 60, "second");
        var actor = await Author();

        var sheet = Read(await Download(Client(actor), msel.Id));

        Assert.Equal(["Time", "Target", "Move", "Group"], sheet.Headings);
        Assert.Equal("1", sheet.Text(0, "Move"));
        Assert.Equal("1", sheet.Text(0, "Group"));
        Assert.Equal("1", sheet.Text(1, "Move"));
        // A second group within the same move: the group number counts distinct times, not rows.
        Assert.Equal("2", sheet.Text(1, "Group"));
    }

    /// <summary>
    /// A Data Field of a system column's name wins, and the system column is not written.
    /// </summary>
    /// <remarks>
    /// Not a nicety: <c>DataTable</c> throws on a duplicate column name, so writing both would make the
    /// export fail outright for any MSEL whose author happened to name a field <c>Time</c>. The import
    /// applies the same precedence, so such a MSEL round-trips as plain Data Fields - at the cost of its
    /// schedule, which no longer has a column to travel in.
    /// </remarks>
    [Fact]
    public async Task Download_WhenADataFieldIsNamedTime_DoesNotAlsoWriteTheSystemColumn()
    {
        var msel = await SeedSheet(["Time", "Target"]);
        await SeedEvent(msel, 3600, "09:00 local", "a");
        var actor = await Author();

        var sheet = Read(await Download(Client(actor), msel.Id));

        Assert.Equal(["Time", "Target"], sheet.Headings);
        Assert.Equal("09:00 local", sheet.Text(0, "Time"));
    }

    [Fact]
    public async Task Download_OrdersTheRowsByDeltaSeconds()
    {
        var msel = await SeedSheet(["Target"]);
        await SeedEvent(msel, 7200, "last");
        await SeedEvent(msel, 60, "first");
        await SeedEvent(msel, 3600, "middle");
        var actor = await Author();

        var sheet = Read(await Download(Client(actor), msel.Id));

        Assert.Equal(["first", "middle", "last"], sheet.Rows.Select(r => r["Target"].Text));
    }

    [Theory]
    [InlineData(0, "+ 00:00:00")]
    [InlineData(59, "+ 00:00:59")]
    [InlineData(3661, "+ 01:01:01")]
    [InlineData(86400, "+ 1 00:00:00")]
    [InlineData(90061, "+ 1 01:01:01")]
    [InlineData(-60, "- 00:01:00")]
    [InlineData(-90061, "- 1 01:01:01")]
    public async Task Download_FormatsTheTimeColumnAsADeltaFromTheStart(int deltaSeconds, string expected)
    {
        var msel = await SeedSheet(["Target"]);
        await SeedEvent(msel, deltaSeconds, "a");
        var actor = await Author();

        var sheet = Read(await Download(Client(actor), msel.Id));

        Assert.Equal(expected, sheet.Text(0, "Time"));
    }

    /// <summary>
    /// A Card field holds a card's id; the sheet gets the card's name, because a GUID is no use to an
    /// author editing the file.
    /// </summary>
    [Fact]
    public async Task Download_ResolvesACardValueToTheCardName()
    {
        var msel = await SeedSheet(["Card"]);
        var field = msel.DataFields.Single();
        field.DataType = DataFieldType.Card;
        await Db.SaveChangesAsync(Ct);
        var card = new CardEntity
        {
            Id = Guid.NewGuid(),
            MselId = msel.Id,
            Name = "Press briefing",
            CreatedBy = msel.CreatedBy
        };
        await Seed(card);
        await SeedEvent(msel, 0, card.Id.ToString());
        var actor = await Author();

        var sheet = Read(await Download(Client(actor), msel.Id));

        Assert.Equal("Press briefing", sheet.Text(0, "Card"));
    }

    [Fact]
    public async Task Download_OfAMselWithNoScenarioEvents_WritesOnlyTheHeaderRow()
    {
        var msel = await SeedSheet(["Target"]);
        var actor = await Author();

        var sheet = Read(await Download(Client(actor), msel.Id));

        Assert.Equal(["Time", "Target"], sheet.Headings);
        Assert.Empty(sheet.Rows);
    }

    /// <summary>Download of a data value whose cell metadata has three parts is answered with a 500.</summary>
    [Fact]
    public async Task Download_OfADataValueWhoseCellMetadataHasThreeParts_Is500()
    {
        var msel = await SeedSheet(["Target"]);
        var scenarioEvent = await SeedEvent(msel, 0, "a");
        await using (var db = NewContext())
        {
            var dataValue = await db.DataValues.SingleAsync(dv => dv.ScenarioEventId == scenarioEvent.Id, Ct);
            dataValue.CellMetadata = "FFFFFF,0,bold";
            await db.SaveChangesAsync(Ct);
        }
        var actor = await Author();

        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/xlsx", Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Index was outside the bounds of the array.", failure.Title);
        Assert.Contains("MselService.CreateStylesheet", failure.Detail);
    }

    /// <summary>Download writes a value containing markup as an inline string.</summary>
    [Fact]
    public async Task Download_WritesAValueContainingMarkupAsAnInlineString()
    {
        var msel = await SeedSheet(["Target"]);
        await SeedEvent(msel, 0, "<p>Evacuate the building</p>");
        var actor = await Author();

        var sheet = Read(await Download(Client(actor), msel.Id));

        var cell = sheet.Cell(0, "Target");
        Assert.Equal("InlineString", cell.DataType);
        Assert.Contains("Evacuate the building", cell.Text);
        Assert.DoesNotContain("<p>", cell.Text);
    }

    // ---------------------------------------------------------------------------------------------
    // Upload - POST msels/xlsx, which creates a MSEL
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Upload_CreatesAMselNamedAfterTheFile()
    {
        var actor = await Author();

        var msel = await Upload(Client(actor), Workbook(["Target"], ["a"]), "Winter exercise.xlsx");

        Assert.Equal("Winter exercise", msel.Name);
        Assert.Equal("Uploaded from Winter exercise.xlsx", msel.Description);
        Assert.Equal(MselItemStatus.Pending, msel.Status);
        Assert.False(msel.IsTemplate);
        Assert.Equal(actor.Id, msel.CreatedBy);
    }

    /// <summary>Upload removes every occurrence of the extension from the name.</summary>
    [Fact]
    public async Task Upload_RemovesEveryOccurrenceOfTheExtensionFromTheName()
    {
        var actor = await Author();

        var msel = await Upload(Client(actor), Workbook(["Target"], ["a"]), "march.xlsx.reviewed.xlsx");

        Assert.Equal("march.reviewed", msel.Name);
    }

    [Fact]
    public async Task Upload_CreatesADataFieldPerHeadingInSheetOrder()
    {
        var actor = await Author();

        var msel = await Upload(Client(actor), Workbook(["First", "Second", "Third"], ["a", "b", "c"]));

        await using var db = NewContext();
        var fields = await db.DataFields
            .Where(df => df.MselId == msel.Id)
            .OrderBy(df => df.DisplayOrder)
            .ToListAsync(Ct);
        Assert.Equal(["First", "Second", "Third"], fields.Select(df => df.Name));
        Assert.Equal([1, 2, 3], fields.Select(df => df.DisplayOrder));
        // Every column starts as a string; the data values are what promote a field to another type.
        Assert.All(fields, df => Assert.Equal(DataFieldType.String, df.DataType));
        Assert.All(fields, df => Assert.True(df.OnScenarioEventList && df.OnExerciseView));
    }

    [Fact]
    public async Task Upload_CreatesAScenarioEventPerRowWithItsValues()
    {
        var actor = await Author();

        var msel = await Upload(Client(actor), Workbook(
            ["Target", "Detail"],
            ["first target", "first detail"],
            ["second target", "second detail"]));

        await using var db = NewContext();
        var events = await db.ScenarioEvents
            .Where(se => se.MselId == msel.Id)
            .OrderBy(se => se.DeltaSeconds)
            .ToListAsync(Ct);
        Assert.Equal(2, events.Count);
        Assert.All(events, se => Assert.Equal(EventType.Inject, se.ScenarioEventType));
        Assert.Equal(
            ["first target", "first detail"],
            await ValuesOf(db, events[0].Id));
        Assert.Equal(
            ["second target", "second detail"],
            await ValuesOf(db, events[1].Id));
    }

    [Fact]
    public async Task Upload_DoesNotTurnTheTimeColumnIntoADataField()
    {
        var actor = await Author();

        var msel = await Upload(Client(actor), Workbook(
            ["Time", "Target"],
            ["+ 00:01:00", "a"]));

        await using var db = NewContext();
        var fields = await db.DataFields.Where(df => df.MselId == msel.Id).ToListAsync(Ct);
        var field = Assert.Single(fields);
        Assert.Equal("Target", field.Name);
        // and it keeps display order 1: a system column does not consume one.
        Assert.Equal(1, field.DisplayOrder);
    }

    [Fact]
    public async Task Upload_ReadsTheTimeColumnBackIntoASchedule()
    {
        var actor = await Author();

        var msel = await Upload(Client(actor), Workbook(
            ["Time", "Target"],
            ["+ 00:00:30", "half a minute"],
            ["+ 2 03:04:05", "two days out"],
            ["- 00:10:00", "before the start"]));

        await using var db = NewContext();
        var events = await db.ScenarioEvents
            .Where(se => se.MselId == msel.Id)
            .OrderBy(se => se.DeltaSeconds)
            .ToListAsync(Ct);
        Assert.Equal([-600, 30, 2 * 86400 + 3 * 3600 + 4 * 60 + 5], events.Select(se => se.DeltaSeconds));
        Assert.Equal(["before the start"], await ValuesOf(db, events[0].Id));
        Assert.Equal(["half a minute"], await ValuesOf(db, events[1].Id));
        Assert.Equal(["two days out"], await ValuesOf(db, events[2].Id));
    }

    /// <summary>
    /// With nothing to read a schedule from, the rows are put a minute apart - which at least keeps them
    /// in the order the author wrote them.
    /// </summary>
    [Fact]
    public async Task Upload_WithNoTimeColumn_SchedulesOneMinutePerRow()
    {
        var actor = await Author();

        var msel = await Upload(Client(actor), Workbook(["Target"], ["a"], ["b"], ["c"]));

        await using var db = NewContext();
        var events = await db.ScenarioEvents.Where(se => se.MselId == msel.Id).ToListAsync(Ct);
        Assert.Equal([60, 120, 180], events.Select(se => se.DeltaSeconds).Order());
    }

    /// <summary>A Time value not in the export format is ignored and the row falls back to its position.</summary>
    [Fact]
    public async Task Upload_WithAnUnreadableTimeValue_FallsBackToRowOrder()
    {
        var actor = await Author();

        var msel = await Upload(Client(actor), Workbook(
            ["Time", "Target"],
            ["09:00 on the Tuesday", "a"],
            ["+ 00:05:00", "b"]));

        await using var db = NewContext();
        var events = await db.ScenarioEvents
            .Where(se => se.MselId == msel.Id)
            .OrderBy(se => se.DeltaSeconds)
            .ToListAsync(Ct);
        // The unreadable row keeps its position - row 2 of the sheet, so one minute in - and the row
        // below it still gets the time it asked for.
        Assert.Equal([60, 300], events.Select(se => se.DeltaSeconds));
        Assert.Equal(["a"], await ValuesOf(db, events[0].Id));
        Assert.Equal(["b"], await ValuesOf(db, events[1].Id));
    }

    /// <summary>Upload with a blank heading leaves a gap in the display orders.</summary>
    [Fact]
    public async Task Upload_WithABlankHeading_LeavesAGapInTheDisplayOrders()
    {
        var actor = await Author();

        var msel = await Upload(Client(actor), Workbook(["First", "", "Third"], ["a", "", "c"]));

        await using var db = NewContext();
        var fields = await db.DataFields
            .Where(df => df.MselId == msel.Id)
            .OrderBy(df => df.DisplayOrder)
            .ToListAsync(Ct);
        Assert.Equal(["First", "Third"], fields.Select(df => df.Name));
        Assert.Equal([1, 3], fields.Select(df => df.DisplayOrder));
    }

    [Fact]
    public async Task Upload_BroadcastsTheNewMselToItsOwnGroupAndTheAdminGroup()
    {
        var actor = await Author();

        var msel = await Upload(Client(actor), Workbook(["Target"], ["a"]));

        var recipients = Hub.Recipients(MainHubMethods.MselCreated, msel.Id);
        Assert.Equal(2, recipients.Count);
        Assert.Contains(msel.Id.ToString(), recipients);
        Assert.Contains(MainHub.ADMIN_DATA_GROUP, recipients);
    }

    [Fact]
    public async Task Upload_WithNoFile_Is400()
    {
        var actor = await Author();

        using var content = new MultipartFormDataContent();
        var response = await Client(actor).PostAsync("/api/msels/xlsx", content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Upload of something that is not a spreadsheet is answered with a 500.</summary>
    [Fact]
    public async Task Upload_OfSomethingThatIsNotASpreadsheet_Is500()
    {
        var actor = await Author();

        var response = await Post(Client(actor), Encoding.UTF8.GetBytes("not a spreadsheet"));

        Assert.StartsWith("The Position must be within the length of the Stream: 17 (Parameter 'value')", (await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
    }

    [Fact]
    public async Task Upload_WithEditMselsPermission_Is200()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(Client(actor), Workbook(["Target"], ["a"]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The upload takes installation-wide <c>EditMsels</c> and nothing else - not <c>CreateMsels</c>,
    /// which is what <c>POST msels</c> and <c>POST msels/json</c> ask for, and not a role on any MSEL,
    /// since there is no MSEL yet to hold one.
    /// </summary>
    [Theory]
    [InlineData(SystemPermission.CreateMsels)]
    [InlineData(SystemPermission.ViewMsels)]
    public async Task Upload_WithoutEditMsels_Is403(SystemPermission permission)
    {
        var actor = await Actor().WithSystemPermissions(permission).SeedAsync();

        var response = await Post(Client(actor), Workbook(["Target"], ["a"]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Replace - PUT msels/{id}/xlsx, which re-imports over an existing MSEL
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Replace_ReplacesTheScenarioEventsAndKeepsTheMsel()
    {
        var msel = await SeedSheet(["Target"]);
        var stale = await SeedEvent(msel, 0, "the old plan");
        var actor = await Author();

        var response = await Put(Client(actor), msel.Id, Workbook(["Target"], ["the new plan"], ["and more"]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var returned = await Read<Msel>(response);
        Assert.Equal(msel.Id, returned.Id);
        Assert.Equal(msel.Name, returned.Name);

        await using var db = NewContext();
        Assert.False(await db.ScenarioEvents.AnyAsync(se => se.Id == stale.Id, Ct));
        var events = await db.ScenarioEvents
            .Where(se => se.MselId == msel.Id)
            .OrderBy(se => se.DeltaSeconds)
            .ToListAsync(Ct);
        Assert.Equal(2, events.Count);
        Assert.Equal(["the new plan"], await ValuesOf(db, events[0].Id));
        Assert.Equal(["and more"], await ValuesOf(db, events[1].Id));
    }

    [Fact]
    public async Task Replace_DoesNotCreateNewDataFields()
    {
        var msel = await SeedSheet(["First", "Second"]);
        var actor = await Author();

        var response = await Put(Client(actor), msel.Id, Workbook(["First", "Second"], ["a", "b"]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = NewContext();
        var fields = await db.DataFields.Where(df => df.MselId == msel.Id).ToListAsync(Ct);
        Assert.Equal(
            msel.DataFields.Select(df => df.Id).Order(),
            fields.Select(df => df.Id).Order());
    }

    [Fact]
    public async Task Replace_WithAHeadingThatIsNotADataField_Is500()
    {
        var msel = await SeedSheet(["First"]);
        var actor = await Author();

        var response = await Put(Client(actor), msel.Id, Workbook(["First", "Invented"], ["a", "b"]));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var error = await Read<ApiError>(response);
        Assert.Contains("'Invented' does not exist in the current Data Fields", error.Title);
    }

    // Same case as Replace_WithAHeadingThatIsNotADataField_Is500.
    [Fact]
    public async Task Replace_WithTheHeadingsInADifferentOrder_Is500()
    {
        var msel = await SeedSheet(["First", "Second"]);
        var actor = await Author();

        var response = await Put(Client(actor), msel.Id, Workbook(["Second", "First"], ["b", "a"]));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var error = await Read<ApiError>(response);
        Assert.Contains("is not in the same order as in the current Data Fields", error.Title);
    }

    /// <summary>
    /// A rejected sheet costs the MSEL nothing: the replace deletes the old events before it reads the
    /// new ones, but it does so inside a transaction it never commits.
    /// </summary>
    /// <remarks>
    /// Worth a test of its own because nothing in the method rolls back explicitly - the transaction is
    /// undone only because the request's <c>BlueprintContext</c> is disposed with it still open. So a
    /// later refactor that keeps a context alive across requests, or commits earlier, destroys an
    /// author's scenario on a mistyped column heading with nothing to recover it from.
    /// </remarks>
    // Same case as Replace_WithAHeadingThatIsNotADataField_Is500.
    [Fact]
    public async Task Replace_WhenTheSheetIsRejected_LeavesTheOldScenarioEventsInPlace()
    {
        var msel = await SeedSheet(["First"]);
        await SeedEvent(msel, 0, "keep me");
        await SeedEvent(msel, 60, "and me");
        var actor = await Author();

        var response = await Put(Client(actor), msel.Id, Workbook(["Invented"], ["a"]));

        Assert.Equal("The xlsx file column heading 'Invented' does not exist in the current Data Fields.", (await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
        await using var db = NewContext();
        var events = await db.ScenarioEvents
            .Where(se => se.MselId == msel.Id)
            .OrderBy(se => se.DeltaSeconds)
            .ToListAsync(Ct);
        Assert.Equal(2, events.Count);
        Assert.Equal(["keep me"], await ValuesOf(db, events[0].Id));
    }

    [Fact]
    public async Task Replace_OfAnUnknownMsel_Is404()
    {
        var actor = await Author();

        var response = await Put(Client(actor), Guid.NewGuid(), Workbook(["Target"], ["a"]));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Replace of an unknown MSEL without edit MSELs is answered with a 500.</summary>
    [Fact]
    public async Task Replace_OfAnUnknownMsel_WithoutEditMsels_Is500()
    {
        var actor = await Actor().SeedAsync();

        var response = await Put(Client(actor), Guid.NewGuid(), Workbook(["Target"], ["a"]));

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Object reference not set to an instance of an object.", failure.Title);
        Assert.Contains("MselOwnerRequirement.IsMet", failure.Detail);
    }

    // Same case as Replace_WithAHeadingThatIsNotADataField_Is500.
    [Fact]
    public async Task Replace_WithAMismatchedMselIdInTheForm_Is500()
    {
        var msel = await SeedSheet(["Target"]);
        var other = Guid.NewGuid();
        var actor = await Author();

        var response = await Put(Client(actor), msel.Id, Workbook(["Target"], ["a"]), formMselId: other);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var error = await Read<ApiError>(response);
        Assert.Contains(msel.Id.ToString(), error.Title);
        Assert.Contains(other.ToString(), error.Title);
    }

    [Fact]
    public async Task Replace_WithTheMatchingMselIdInTheForm_Is200()
    {
        var msel = await SeedSheet(["Target"]);
        var actor = await Author();

        var response = await Put(Client(actor), msel.Id, Workbook(["Target"], ["a"]), formMselId: msel.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Replace_AsTheCreator_WithoutEditMsels_Is200()
    {
        var creator = Guid.NewGuid();
        var msel = await SeedSheet(["Target"], m => m.CreatedBy = creator);
        var actor = await Actor().WithId(creator).SeedAsync();

        var response = await Put(Client(actor), msel.Id, Workbook(["Target"], ["a"]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Replace_AsAnOwnerByRole_WithoutEditMsels_Is200()
    {
        var msel = await SeedSheet(["Target"]);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Put(Client(actor), msel.Id, Workbook(["Target"], ["a"]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Replace as anything but an owner without edit MSELs is answered with a 403.</summary>
    [Theory]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.Viewer)]
    public async Task Replace_AsAnythingButAnOwner_WithoutEditMsels_Is403(MselRole role)
    {
        var msel = await SeedSheet(["Target"]);
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Put(Client(actor), msel.Id, Workbook(["Target"], ["a"]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Replace_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var msel = await SeedSheet(["Target"]);
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        var response = await Put(Client(actor), msel.Id, Workbook(["Target"], ["a"]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // The round trip, which is the whole point of the three endpoints
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Download_ThenUpload_KeepsTheColumnsAndTheSchedule()
    {
        var original = await SeedSheet(["Target", "Detail"]);
        await SeedEvent(original, 3661, "first target", "first detail");
        await SeedEvent(original, 90061, "second target", "second detail");
        var actor = await Author();

        var file = await Download(Client(actor), original.Id);
        var imported = await Upload(Client(actor), file, "again.xlsx");

        await using var db = NewContext();
        var fields = await db.DataFields
            .Where(df => df.MselId == imported.Id)
            .OrderBy(df => df.DisplayOrder)
            .ToListAsync(Ct);
        Assert.Equal(["Target", "Detail"], fields.Select(df => df.Name));
        var events = await db.ScenarioEvents
            .Where(se => se.MselId == imported.Id)
            .OrderBy(se => se.DeltaSeconds)
            .ToListAsync(Ct);
        Assert.Equal([3661, 90061], events.Select(se => se.DeltaSeconds));
        Assert.Equal(["first target", "first detail"], await ValuesOf(db, events[0].Id));
        Assert.Equal(["second target", "second detail"], await ValuesOf(db, events[1].Id));
    }

    [Fact]
    public async Task Download_ThenReplace_KeepsTheSchedule()
    {
        var msel = await SeedSheet(["Target"]);
        await SeedEvent(msel, 3661, "first");
        await SeedEvent(msel, 90061, "second");
        var actor = await Author();

        var file = await Download(Client(actor), msel.Id);
        var response = await Put(Client(actor), msel.Id, file);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = NewContext();
        var events = await db.ScenarioEvents
            .Where(se => se.MselId == msel.Id)
            .OrderBy(se => se.DeltaSeconds)
            .ToListAsync(Ct);
        Assert.Equal([3661, 90061], events.Select(se => se.DeltaSeconds));
        Assert.Equal(["first"], await ValuesOf(db, events[0].Id));
        Assert.Equal(["second"], await ValuesOf(db, events[1].Id));
    }

    // ---------------------------------------------------------------------------------------------
    // Seeding
    // ---------------------------------------------------------------------------------------------

    private async Task<TestActor> Author() =>
        await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

    /// <summary>
    /// A MSEL with one Data Field per name, at display orders 1..n.
    /// </summary>
    /// <remarks>
    /// The display orders start at 1 rather than 0 because that is what <c>PUT msels/{id}/xlsx</c>
    /// requires: it walks the header row counting from 1, skipping system columns, and refuses the file
    /// if a heading's field does not carry the number it reached. <c>TimeDisplayOrder</c> is left at 0 so
    /// the exported Time column sorts ahead of the fields, which is where a MSEL created by
    /// <c>POST msels/xlsx</c> also puts it.
    /// </remarks>
    private async Task<MselEntity> SeedSheet(string[] fieldNames, Action<MselEntity> arrange = null)
    {
        var msel = TestData.Msel();
        arrange?.Invoke(msel);

        var displayOrder = 1;
        foreach (var name in fieldNames)
        {
            msel.DataFields.Add(new DataFieldEntity
            {
                Id = Guid.NewGuid(),
                MselId = msel.Id,
                Name = name,
                DataType = DataFieldType.String,
                DisplayOrder = displayOrder++,
                OnScenarioEventList = true,
                OnExerciseView = true,
                CellMetadata = "FFFFFF,0,bold,0",
                CreatedBy = msel.CreatedBy
            });
        }

        await Seed(msel);
        return msel;
    }

    /// <summary>
    /// One scenario event, with <paramref name="values"/> written into the MSEL's Data Fields in display
    /// order.
    /// </summary>
    private async Task<ScenarioEventEntity> SeedEvent(MselEntity msel, int deltaSeconds, params string[] values)
    {
        var fields = msel.DataFields.OrderBy(df => df.DisplayOrder).ToList();
        var scenarioEvent = new ScenarioEventEntity
        {
            Id = Guid.NewGuid(),
            MselId = msel.Id,
            ScenarioEventType = EventType.Inject,
            DeltaSeconds = deltaSeconds,
            CreatedBy = msel.CreatedBy
        };
        await Seed(scenarioEvent);

        for (var i = 0; i < values.Length; i++)
        {
            await Seed(new DataValueEntity
            {
                Id = Guid.NewGuid(),
                ScenarioEventId = scenarioEvent.Id,
                DataFieldId = fields[i].Id,
                Value = values[i],
                CellMetadata = "FFFFFF,0,normal,0",
                CreatedBy = msel.CreatedBy
            });
        }

        return scenarioEvent;
    }

    private static async Task<List<string>> ValuesOf(Data.BlueprintContext db, Guid scenarioEventId) =>
        await db.DataValues
            .Where(dv => dv.ScenarioEventId == scenarioEventId)
            .OrderBy(dv => dv.DataField.DisplayOrder)
            .Select(dv => dv.Value)
            .ToListAsync(Ct);

    // ---------------------------------------------------------------------------------------------
    // Requests
    // ---------------------------------------------------------------------------------------------

    private async Task<byte[]> Download(HttpClient client, Guid mselId)
    {
        var response = await client.GetAsync($"/api/msels/{mselId}/xlsx", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync(Ct);
    }

    private async Task<Msel> Upload(HttpClient client, byte[] file, string fileName = "sheet.xlsx")
    {
        var response = await Post(client, file, fileName);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Read<Msel>(response);
    }

    private async Task<HttpResponseMessage> Post(HttpClient client, byte[] file, string fileName = "sheet.xlsx")
    {
        // The content has to be awaited before it falls out of scope: TestServer reads the body inside
        // SendAsync, so returning the task unawaited disposes it first.
        using var content = Form(file, fileName);
        return await client.PostAsync("/api/msels/xlsx", content, Ct);
    }

    private async Task<HttpResponseMessage> Put(
        HttpClient client, Guid mselId, byte[] file, Guid? formMselId = null, string fileName = "sheet.xlsx")
    {
        using var content = Form(file, fileName);
        if (formMselId.HasValue)
            content.Add(new StringContent(formMselId.Value.ToString()), "MselId");
        return await client.PutAsync($"/api/msels/{mselId}/xlsx", content, Ct);
    }

    private static MultipartFormDataContent Form(byte[] file, string fileName)
    {
        var content = new MultipartFormDataContent();
        var upload = new ByteArrayContent(file);
        upload.Headers.ContentType = new MediaTypeHeaderValue(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        content.Add(upload, "ToUpload", fileName);
        return content;
    }

    private async Task<T> Read<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);

    // ---------------------------------------------------------------------------------------------
    // Spreadsheets
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The smallest workbook the import will read: one sheet, a header row, and one row per set of
    /// values, all written as plain strings at style 0.
    /// </summary>
    /// <remarks>
    /// Hand-built rather than taken from a checked-in fixture file so that a test can say what is in the
    /// sheet on the same screen as its assertion. The stylesheet is not optional padding - the import
    /// walks every cell's style index into <c>CellFormats</c>, then into <c>Fills</c> and <c>Fonts</c> by
    /// the ids it finds there, with no null checks - so a workbook without one fails inside the reader
    /// rather than in the code under test. Pass <c>null</c> or <c>""</c> for a heading to get a column
    /// with no name.
    /// </remarks>
    private static byte[] Workbook(string[] headings, params string[][] rows)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
            stylesPart.Stylesheet = MinimalStylesheet();
            stylesPart.Stylesheet.Save();

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData();
            worksheetPart.Worksheet = new Worksheet(sheetData);

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Sheet1"
            });

            sheetData.AppendChild(TextRow(1, headings));
            for (var i = 0; i < rows.Length; i++)
                sheetData.AppendChild(TextRow((uint)(i + 2), rows[i]));

            workbookPart.Workbook.Save();
        }

        return stream.ToArray();
    }

    private static Row TextRow(uint rowIndex, string[] values)
    {
        var row = new Row { RowIndex = rowIndex };
        for (var i = 0; i < values.Length; i++)
        {
            var cell = new Cell
            {
                CellReference = Reference(i + 1, rowIndex),
                StyleIndex = 0U
            };
            if (!string.IsNullOrEmpty(values[i]))
            {
                cell.DataType = CellValues.String;
                cell.CellValue = new CellValue(values[i]);
            }
            row.AppendChild(cell);
        }

        return row;
    }

    private static Stylesheet MinimalStylesheet() => new(
        new Fonts(new Font(), new Font(new Bold())),
        new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 })),
        new Borders(new Border()),
        new CellFormats(new CellFormat
        {
            NumberFormatId = 0U,
            FontId = 0U,
            FillId = 0U,
            BorderId = 0U,
            FormatId = 0U
        }));

    private static string Reference(int columnIndex, uint rowIndex)
    {
        var columnRef = "";
        while (columnIndex > 0)
        {
            columnIndex--;
            columnRef = (char)('A' + columnIndex % 26) + columnRef;
            columnIndex /= 26;
        }

        return columnRef + rowIndex;
    }

    /// <summary>What an exported sheet says, read back by column heading rather than by position.</summary>
    private sealed record SheetCell(string Text, string DataType);

    private sealed class SheetContents
    {
        public List<string> Headings { get; init; }
        public List<Dictionary<string, SheetCell>> Rows { get; init; }

        public SheetCell Cell(int row, string heading) => Rows[row].GetValueOrDefault(heading);

        public string Text(int row, string heading) => Cell(row, heading)?.Text;
    }

    private static SheetContents Read(byte[] file)
    {
        using var stream = new MemoryStream(file);
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbookPart = document.WorkbookPart;
        var sheet = workbookPart.Workbook.GetFirstChild<Sheets>().GetFirstChild<Sheet>();
        var worksheet = ((WorksheetPart)workbookPart.GetPartById(sheet.Id)).Worksheet;
        var rows = worksheet.Elements<SheetData>().First().Elements<Row>().ToList();

        var headings = new List<string>();
        var headingByColumn = new Dictionary<int, string>();
        foreach (var cell in rows[0].Elements<Cell>())
        {
            var heading = CellOf(cell).Text;
            headings.Add(heading);
            headingByColumn[ColumnOf(cell)] = heading;
        }

        var body = new List<Dictionary<string, SheetCell>>();
        foreach (var row in rows.Skip(1))
        {
            var cells = new Dictionary<string, SheetCell>();
            foreach (var cell in row.Elements<Cell>())
            {
                if (headingByColumn.TryGetValue(ColumnOf(cell), out var heading))
                    cells[heading] = CellOf(cell);
            }

            body.Add(cells);
        }

        return new SheetContents { Headings = headings, Rows = body };
    }

    private static SheetCell CellOf(Cell cell)
    {
        var type = cell.DataType?.Value.ToString();
        if (cell.DataType != null && cell.DataType == CellValues.InlineString)
            return new SheetCell(cell.InlineString == null ? "" : cell.InlineString.InnerText, type);

        return new SheetCell(cell.CellValue == null ? cell.InnerText : cell.CellValue.Text, type);
    }

    private static int ColumnOf(Cell cell)
    {
        var reference = cell.CellReference?.Value ?? "";
        var index = 0;
        foreach (var character in reference)
        {
            if (character < 'A' || character > 'Z')
                break;
            index = index * 26 + (character - 'A' + 1);
        }

        return index;
    }
}
