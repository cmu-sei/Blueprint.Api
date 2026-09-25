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
using Blueprint.Api.Tests.Infrastructure;
using Blueprint.Api.ViewModels;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>GET api/msels/{id}/xlsx</c>, <c>POST api/msels/xlsx</c> and <c>PUT api/msels/{id}/xlsx</c> - the
/// spreadsheet a MSEL is actually authored in.
/// </summary>
/// <remarks>
/// <para>
/// A MSEL's scenario events are a table, and the table is what exercise authors work in: they build the
/// sheet in Excel, upload it to create the MSEL, then download it, edit it and replace. So the three
/// endpoints are one feature, and the property that matters is that they compose - a sheet that came out
/// of the export has to go back in through either import and leave the MSEL where it was. Two tests pin
/// that directly (<see cref="Download_ThenUpload_KeepsTheColumnsAndTheSchedule"/>,
/// <see cref="Download_ThenReplace_KeepsTheSchedule"/>); the rest pin the halves.
/// </para>
/// <para>
/// The sheet carries two kinds of column and the distinction runs through everything here. Most are Data
/// Fields, with a row of Data Values behind them. Three - <c>Time</c>, <c>Move</c> and <c>Group</c> - are
/// <em>system</em> columns, written from the event's own schedule and position with no entity behind
/// them. <c>Time</c> is always written, because it is the only one whose value cannot be recomputed from
/// the sheet, and the import reads it back; <c>Move</c> and <c>Group</c> are opt-in and write-only. A
/// Data Field named <c>Time</c> takes precedence over the system column on both sides - see
/// <see cref="Download_WhenADataFieldIsNamedTime_DoesNotAlsoWriteTheSystemColumn"/>.
/// </para>
/// <para>
/// The two imports differ in more than their target. <c>POST msels/xlsx</c> creates a MSEL and takes the
/// sheet's headings as the Data Fields, whatever they are. <c>PUT msels/{id}/xlsx</c> replaces an
/// existing MSEL's events and insists the headings match the Data Fields it already has, by name
/// <em>and</em> by order, refusing the file otherwise - which is what stops a re-ordered spreadsheet from
/// silently writing every value into the wrong column.
/// </para>
/// <para>
/// Two things are characterized rather than fixed. <c>GET msels/{id}/xlsx</c> has <em>no authorization
/// check at all</em> - see <see cref="Download_WithNoPermissionAndNoRoleOnTheMsel_Is200"/> - so the whole
/// scenario of every MSEL in the installation is readable by any account that can sign in. And the two
/// write endpoints disagree about who may write: the upload demands installation-wide <c>EditMsels</c>,
/// while the replace also accepts the MSEL's owner, so the author of a MSEL can replace its sheet but
/// cannot create one.
/// </para>
/// <para>
/// One note for anyone mutation-checking the replace half. Turning <c>verifyDataFields</c> off reddens
/// ten tests rather than the three that assert the rejection, because the import then creates a
/// <em>second</em> Data Field for a heading the MSEL already has, and the duplicate violates a unique
/// index - so every replace that was meant to succeed fails too. By the same route
/// <see cref="Replace_WhenTheSheetIsRejected_LeavesTheOldScenarioEventsInPlace"/> stays green against
/// that mutation: it still gets its 500, just from the database instead of from the check. That is a
/// coincidence of the mutation, not a hole in the test - the rejection itself is pinned by
/// <see cref="Replace_WithAHeadingThatIsNotADataField_Is500"/> and
/// <see cref="Replace_WithTheHeadingsInADifferentOrder_Is500"/>.
/// </para>
/// </remarks>
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

        var response = await AnonymousClient.GetAsync($"/api/msels/{msel.Id}/xlsx", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Characterizes a missing authorization check: the action asks nothing of the caller beyond being
    /// signed in.
    /// </summary>
    /// <remarks>
    /// Every other MSEL-scoped read in the controller resolves a <c>SystemPermission</c> and passes it
    /// to the service, which falls back to the caller's role on the MSEL. This one calls
    /// <c>DownloadXlsxAsync</c> straight off the route, and the service method takes no permission
    /// argument - so any account that can obtain a token can read the full scenario, every injected
    /// event and every Data Value, of every MSEL in the installation. That is the whole exercise: the
    /// thing the participants are not supposed to see.
    /// <para>
    /// The fix is the shape <c>GET msels/{id}/json</c> uses, and better than it: resolve
    /// <c>ViewMsels</c> in the controller, pass it in, and fall back to
    /// <c>MselViewRequirement.IsMet</c>. This test turns red when that lands, and
    /// <see cref="Download_ReturnsTheMselAsAFileNamedAfterIt"/> stays green because its actor holds
    /// <c>EditMsels</c> - so give the new check a permission an author already has, or update both.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Time is written whether or not the MSEL asks for a schedule column, unlike Move and Group.
    /// </summary>
    /// <remarks>
    /// This is what makes an export/import round trip keep the exercise's timings: nothing else in the
    /// sheet records when an event fires, so an export without it silently reduces every MSEL to one
    /// event per minute on the way back in. See
    /// <see cref="Download_ThenUpload_KeepsTheColumnsAndTheSchedule"/>.
    /// </remarks>
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

    /// <summary>
    /// Characterizes a bounds bug: cell metadata of exactly three parts fails the whole export.
    /// </summary>
    /// <remarks>
    /// <c>CellMetadata</c> is a comma-joined <c>colour,tint,weight,dataFieldType</c>, and the export
    /// reads the fourth part after checking there are at least <em>three</em> - so a three-part value
    /// indexes past the end. There is nothing to stop one: the column is a plain string, written
    /// verbatim by <c>PUT api/datavalues/{id}</c>, and any client that trims a trailing empty part
    /// produces one. The result is a 500 on the export of the whole MSEL, from a single cell, with the
    /// spreadsheet no longer obtainable by any route.
    /// <para>
    /// The fix is <c>&gt;= 4</c> in both places that read part four - <c>CreateStylesheet</c> reaches it
    /// first, so fixing only the cell loop moves the exception rather than removing it. This test
    /// therefore turns red only once <em>both</em> halves are fixed, and stays green against a
    /// half-finished fix; mutation-checking it by guarding one index alone will look like the test does
    /// nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Download_OfADataValueWhoseCellMetadataHasThreeParts_Is500()
    {
        var msel = await SeedSheet(["Target"]);
        var scenarioEvent = await SeedEvent(msel, 0, "a");
        using (var db = NewContext())
        {
            var dataValue = await db.DataValues.SingleAsync(dv => dv.ScenarioEventId == scenarioEvent.Id, Ct);
            dataValue.CellMetadata = "FFFFFF,0,bold";
            await db.SaveChangesAsync(Ct);
        }
        var actor = await Author();

        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/xlsx", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// A value containing markup is written as an inline string with the tags turned into layout, so the
    /// author sees text rather than HTML.
    /// </summary>
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

    /// <summary>
    /// Characterizes the name derivation: it removes every <c>.xlsx</c> in the file name, not the suffix.
    /// </summary>
    /// <remarks>
    /// <c>Name = uploadItem.FileName.Replace(".xlsx", "")</c>, so a file called
    /// <c>march.xlsx.reviewed.xlsx</c> - the shape a copy-and-rename produces - becomes a MSEL called
    /// <c>march.reviewed</c>, losing a piece out of the middle of the name. The fix is to trim the
    /// extension only when it is at the end, as the two download endpoints already do when they add one.
    /// Note the match is case-sensitive too, so a file saved as <c>.XLSX</c> keeps its extension in the
    /// MSEL name.
    /// </remarks>
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

        using var db = NewContext();
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

        using var db = NewContext();
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

        using var db = NewContext();
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

        using var db = NewContext();
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

        using var db = NewContext();
        var events = await db.ScenarioEvents.Where(se => se.MselId == msel.Id).ToListAsync(Ct);
        Assert.Equal([60, 120, 180], events.Select(se => se.DeltaSeconds).Order());
    }

    /// <summary>
    /// A Time value that is not in the format the export writes is refused rather than guessed at, and
    /// the row falls back to its position.
    /// </summary>
    [Fact]
    public async Task Upload_WithAnUnreadableTimeValue_FallsBackToRowOrder()
    {
        var actor = await Author();

        var msel = await Upload(Client(actor), Workbook(
            ["Time", "Target"],
            ["09:00 on the Tuesday", "a"],
            ["+ 00:05:00", "b"]));

        using var db = NewContext();
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

    /// <summary>
    /// Characterizes a gap in the display orders when the sheet has a blank heading.
    /// </summary>
    /// <remarks>
    /// A column with an empty heading creates no Data Field, but it still advances the display-order
    /// counter - unlike a system column, which explicitly does not. So a sheet with a blank column
    /// between two headings produces fields at display orders 1 and 3, and the MSEL carries a gap in its
    /// field ordering forever. It is only cosmetic on the way in, but it is enough to make
    /// <c>PUT msels/{id}/xlsx</c> refuse the same file back: the replace compares each heading's display
    /// order against a counter that skips nothing, so the second field's 3 does not match the expected 2.
    /// The fix is to advance the counter only where a Data Field was created.
    /// </remarks>
    [Fact]
    public async Task Upload_WithABlankHeading_LeavesAGapInTheDisplayOrders()
    {
        var actor = await Author();

        var msel = await Upload(Client(actor), Workbook(["First", "", "Third"], ["a", "", "c"]));

        using var db = NewContext();
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

        var recipients = Hub.Recipients(MainHubMethods.MselCreated);
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

    /// <summary>
    /// Characterizes the response to a file that is not a spreadsheet: a 500, not a 400.
    /// </summary>
    /// <remarks>
    /// The Open XML SDK throws <c>OpenXmlPackageException</c>, which is not an <c>IApiException</c>, so
    /// the exception filter reports a server error for what is squarely a bad request - an author who
    /// picks the wrong file is told the API is broken. Catching it and throwing a
    /// <c>BadRequestException</c> would turn this red.
    /// </remarks>
    [Fact]
    public async Task Upload_OfSomethingThatIsNotASpreadsheet_Is500()
    {
        var actor = await Author();

        var response = await Post(Client(actor), Encoding.UTF8.GetBytes("not a spreadsheet"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
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

        using var db = NewContext();
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
        using var db = NewContext();
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
    [Fact]
    public async Task Replace_WhenTheSheetIsRejected_LeavesTheOldScenarioEventsInPlace()
    {
        var msel = await SeedSheet(["First"]);
        await SeedEvent(msel, 0, "keep me");
        await SeedEvent(msel, 60, "and me");
        var actor = await Author();

        var response = await Put(Client(actor), msel.Id, Workbook(["Invented"], ["a"]));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var db = NewContext();
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

    /// <summary>
    /// Characterizes the order of the two checks: without the system permission the missing MSEL is a
    /// 500 rather than the 403 or 404 either reading of the request would give.
    /// </summary>
    /// <remarks>
    /// <c>MselOwnerRequirement.IsMet</c> dereferences the result of <c>FirstOrDefaultAsync</c>, so it
    /// throws <c>NullReferenceException</c> for a MSEL that does not exist, and the permission check runs
    /// before the existence check. A caller probing for MSEL ids can therefore tell an id that exists
    /// (403) from one that does not (500) without being allowed to touch either. Adding the null guard to
    /// the requirement - which <c>MselViewRequirement</c> already has - turns this red and gives a 403;
    /// see also the two <c>MselOwnerRequirementTests</c> characterizations of the same throw.
    /// </remarks>
    [Fact]
    public async Task Replace_OfAnUnknownMsel_WithoutEditMsels_Is500()
    {
        var actor = await Actor().SeedAsync();

        var response = await Put(Client(actor), Guid.NewGuid(), Workbook(["Target"], ["a"]));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

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

    /// <summary>
    /// Characterizes which MSEL roles may replace the sheet: only <c>Owner</c>, so the MSEL's own editors
    /// cannot.
    /// </summary>
    /// <remarks>
    /// <c>ReplaceAsync</c> falls back to <c>MselOwnerRequirement</c>, and that requirement looks for the
    /// <c>Owner</c> role alone. Editing the spreadsheet is how an author edits a MSEL, so an
    /// <c>Editor</c> can change every scenario event one at a time through the API and the UI but cannot
    /// upload the sheet those events came out of. Whichever way this is settled - accepting
    /// <c>MselEditorRequirement</c> here, or documenting the replace as an owner-only operation - the
    /// two should agree, and this test says which way it went.
    /// </remarks>
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
    public async Task Replace_WithNoPermissionAndNoRole_Is403()
    {
        var msel = await SeedSheet(["Target"]);
        var actor = await Actor().SeedAsync();

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

        using var db = NewContext();
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
        using var db = NewContext();
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
        var msel = BlueprintAppFactory.Msel();
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
