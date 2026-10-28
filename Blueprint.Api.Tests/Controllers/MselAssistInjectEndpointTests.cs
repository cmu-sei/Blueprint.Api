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
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// The MSEL assistant on moves and injects, and on who may ask for what: each MSEL role and system permission is offered
/// only the changes its services let it make, and applying makes them through those services.
/// </summary>
public class MselAssistInjectEndpointTests(DatabaseFixture fixture, AiEnabledFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<AiEnabledFactory>
{
    private static readonly DateTime Start = new(2026, 11, 2, 13, 0, 0, DateTimeKind.Utc);
    private readonly IAiModelClient _model = factory.Model;
    private string _prompt;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _model.ClearSubstitute();
    }

    // one change of each kind; each role is offered the ones its services allow
    private static readonly object[] OneOfEach =
    [
        new { op = "team.add", shortName = "CERT" },
        new { op = "organization.add", shortName = "VENDOR" },
        new { op = "card.add", name = "Patch Status" },
        new { op = "competency.add", idNumber = "CS-511" },
        new { op = "move.add", move = 3, minutes = 120, description = "Disclosure" },
        new { op = "inject.add", minutes = 30, values = new Dictionary<string, string> { ["Title"] = "Vendor advisory" } },
        new { op = "inject.update", target = "E1", values = new Dictionary<string, string> { ["Title"] = "Phishing wave grows" } }
    ];

    public static TheoryData<MselRole, string[]> RoleOffers => new()
    {
        { MselRole.Owner, ["team.add", "organization.add", "card.add", "competency.add", "move.add", "inject.add", "inject.update"] },
        { MselRole.Editor, ["organization.add", "card.add", "inject.update"] },
        { MselRole.Approver, ["inject.update"] },
        { MselRole.MoveEditor, ["move.add"] }
    };

    /// <summary>Each MSEL role is offered the changes it may make, and told why the others are left out.</summary>
    [Theory]
    [MemberData(nameof(RoleOffers))]
    public async Task Ask_OffersEachRoleTheChangesItMayMake(MselRole role, string[] offered)
    {
        var (msel, _) = await SeedMsel();
        await SeedWorkRole();
        Answer(new { reply = "Here you go.", changes = OneOfEach });
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var result = await Read<MselAssistResult>(await Ask(actor, msel.Id));

        Assert.Equal(offered, result.Changes.Select(c => c.Op));
        Assert.Equal(OneOfEach.Length - offered.Length, result.Warnings.Count(w => w.Contains(": left out, only the MSEL's")));
    }

    /// <summary>Applying as each role makes exactly the offered changes; the services agree with the offer.</summary>
    [Theory]
    [MemberData(nameof(RoleOffers))]
    public async Task Apply_MakesEachRolesChangesThroughItsServices(MselRole role, string[] offered)
    {
        var (msel, _) = await SeedMsel();
        await SeedWorkRole();
        Answer(new { reply = "Here you go.", changes = OneOfEach });
        var actor = await Actor().OnMsel(msel, role).SeedAsync();
        var proposal = await Read<MselAssistResult>(await Ask(actor, msel.Id));

        var result = await Read<MselAssistApplyResult>(await Apply(actor, msel.Id, proposal.Changes.ToArray()));

        Assert.Equal(offered.Length, result.Results.Count);
        Assert.All(result.Results, r => Assert.True(r.Applied, $"{role}: {r.Label}: {r.Error}"));
    }

    /// <summary>A planner offered nothing, by role or permission, may not ask at all, and the model is not asked.</summary>
    [Theory]
    [InlineData(MselRole.Viewer)]
    [InlineData(MselRole.Evaluator)]
    public async Task Ask_ByARoleThatMayChangeNothing_IsForbidden(MselRole role)
    {
        var (msel, _) = await SeedMsel();
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Ask(actor, msel.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_model.ReceivedCalls());
    }

    /// <summary>EditMsels without ManageMsels changes everything but competencies, on a MSEL it has no role on.</summary>
    [Fact]
    public async Task Ask_WithEditMselsOnly_OffersAllButCompetencies()
    {
        var (msel, _) = await SeedMsel();
        await SeedWorkRole();
        Answer(new { reply = "Here you go.", changes = OneOfEach });
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var result = await Read<MselAssistResult>(await Ask(actor, msel.Id));

        Assert.Equal(["team.add", "organization.add", "card.add", "move.add", "inject.add", "inject.update"], result.Changes.Select(c => c.Op));
        Assert.Contains(result.Warnings, w => w.Contains("only the MSEL's owner can change its competencies"));
    }

    /// <summary>The model is told what the planner may change, the columns it may write, each inject by reference, and each move on both clocks.</summary>
    [Fact]
    public async Task Ask_ShowsTheModelTheColumnsAndInjectsByReference()
    {
        var (msel, _) = await SeedMsel();
        Answer(new { reply = "Nothing to do.", changes = Array.Empty<object>() });
        var approver = await Actor().OnMsel(msel, MselRole.Approver).SeedAsync();

        await Ask(approver, msel.Id);

        Assert.Contains("The planner may change: existing injects.", _prompt);
        Assert.Contains("- Priority: Plain text. One of: High, Low", _prompt);
        Assert.Contains("- Details: HTML using p, ul, ol, li, strong and em only", _prompt);
        Assert.DoesNotContain("- Delivery Time:", _prompt);
        Assert.Contains("- E1 minute 10 Inject: Title: Phishing wave | Details: Users report a fake payroll email. | Priority: High", _prompt);
        Assert.Contains("- E2 minute 75 Information:", _prompt);
        Assert.Contains("- Move 2 at minute 60, scenario time 2026-01-01T00:00:00Z: Escalation", _prompt);
    }

    /// <summary>A new inject gets the model's values, a Pending status and a delivery time from the MSEL's start.</summary>
    [Fact]
    public async Task Apply_AddsAnInjectWithTheServersOwnValues()
    {
        var (msel, fields) = await SeedMsel();
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var result = await Read<MselAssistApplyResult>(await Apply(owner, msel.Id, new MselChange
        {
            Op = "inject.add",
            Minutes = 90,
            EventType = "information",
            Values = new Dictionary<string, string> { ["title"] = "Vendor advisory", ["Details"] = "<p>Patch <b>now</b></p><script>x()</script>", ["Priority"] = "low", ["To Org"] = "blue, RED" }
        }));

        Assert.True(Assert.Single(result.Results).Applied, result.Results[0].Error);
        Assert.Equal("Add information at 1:30: Vendor advisory", result.Results[0].Label);
        await using var db = NewContext();
        var added = await db.ScenarioEvents.Include(e => e.DataValues).SingleAsync(e => e.MselId == msel.Id && e.DeltaSeconds == 90 * 60, Ct);
        Assert.Equal(EventType.Information, added.ScenarioEventType);
        string Value(string name) => added.DataValues.Single(v => v.DataFieldId == fields[name].Id).Value;
        Assert.Equal("Vendor advisory", Value("Title"));
        Assert.DoesNotContain("script", Value("Details"));
        Assert.Equal("Low", Value("Priority"));
        Assert.Equal("BLUE", Value("To Org"));
        Assert.Equal(MselItemStatus.Pending.ToString(), Value("Status"));
        Assert.Equal("2026-11-02T14:30:00.000Z", Value("Delivery Time"));
    }

    /// <summary>An edit by reference changes only the columns it names and keeps the rest of the inject.</summary>
    [Fact]
    public async Task Apply_EditsAnInjectByReferenceKeepingItsOtherValues()
    {
        var (msel, fields) = await SeedMsel();
        var editor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();
        Answer(new { reply = "Escalating E1.", changes = new object[] { new { op = "inject.update", target = "e1", minutes = 20, values = new Dictionary<string, string> { ["Priority"] = "Low", ["Title"] = "Phishing wave" } } } });
        var proposal = await Read<MselAssistResult>(await Ask(editor, msel.Id));

        var change = Assert.Single(proposal.Changes);
        Assert.Equal("Edit E1 (Phishing wave): Priority, time 0:20", change.Label);
        var result = await Read<MselAssistApplyResult>(await Apply(editor, msel.Id, change));

        Assert.True(Assert.Single(result.Results).Applied, result.Results[0].Error);
        await using var db = NewContext();
        var inject = await db.ScenarioEvents.Include(e => e.DataValues).SingleAsync(e => e.Id == change.ScenarioEventId, Ct);
        Assert.Equal(20 * 60, inject.DeltaSeconds);
        string Value(string name) => inject.DataValues.Single(v => v.DataFieldId == fields[name].Id).Value;
        Assert.Equal("Low", Value("Priority"));
        Assert.Equal("Phishing wave", Value("Title"));
        Assert.Equal("<p>Users report a fake payroll email.</p>", Value("Details"));
        Assert.Equal("2026-11-02T13:20:00.000Z", Value("Delivery Time"));
    }

    /// <summary>The edit lands on the inject it was proposed for, even when another inject comes before it by then.</summary>
    [Fact]
    public async Task Apply_EditsTheProposedInjectAfterOthersAreAdded()
    {
        var (msel, fields) = await SeedMsel();
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        Answer(new { reply = "Retitling E1.", changes = new object[] { new { op = "inject.update", target = "E1", values = new Dictionary<string, string> { ["Title"] = "Second wave" } } } });
        var change = Assert.Single((await Read<MselAssistResult>(await Ask(owner, msel.Id))).Changes);
        var earlier = TestData.ScenarioEvent(msel.Id, deltaSeconds: 60);
        await Seed(earlier);

        await Read<MselAssistApplyResult>(await Apply(owner, msel.Id, change));

        Assert.Equal("Second wave", await ReadBack(db => db.DataValues.Where(v => v.ScenarioEventId == change.ScenarioEventId && v.DataFieldId == fields["Title"].Id).Select(v => v.Value).SingleAsync(Ct)));
        Assert.False(await ReadBack(db => db.DataValues.AnyAsync(v => v.ScenarioEventId == earlier.Id && v.Value == "Second wave", Ct)));
    }

    /// <summary>Values for columns the MSEL lacks or the server fills, options it does not offer, and edits that change nothing are left out.</summary>
    [Fact]
    public async Task Ask_LeavesOutValuesAndEditsItCannotUse()
    {
        var (msel, _) = await SeedMsel();
        Answer(new
        {
            reply = "Done.",
            changes = new object[]
            {
                new { op = "inject.add", minutes = 5, values = new Dictionary<string, string> { ["Title"] = "Outage", ["Mood"] = "tense", ["Delivery Time"] = "2020-01-01", ["Priority"] = "Urgent" } },
                new { op = "inject.update", target = "E1", values = new Dictionary<string, string> { ["Priority"] = "High" } },
                new { op = "inject.update", target = "E9", values = new Dictionary<string, string> { ["Title"] = "x" } },
                new { op = "inject.add", values = new Dictionary<string, string> { ["Title"] = "No time" } }
            }
        });
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var result = await Read<MselAssistResult>(await Ask(owner, msel.Id));

        var added = Assert.Single(result.Changes);
        Assert.Equal(new Dictionary<string, string> { ["Title"] = "Outage" }, added.Values);
        Assert.Contains(result.Warnings, w => w.Contains("Mood, which is not a column"));
        Assert.Contains(result.Warnings, w => w.Contains("Delivery Time, which is not a column"));
        Assert.Contains(result.Warnings, w => w.Contains("\"Urgent\" is not an option for Priority"));
        Assert.Contains(result.Warnings, w => w.Contains("Edit E1 (Phishing wave): left out, it changes nothing."));
        Assert.Contains(result.Warnings, w => w.Contains("there is no inject E9"));
        Assert.Contains(result.Warnings, w => w.Contains("a new inject needs a time"));
    }

    /// <summary>Moves are added and edited through the move service, keeping what the edit does not name, and the scenario clock with them.</summary>
    [Fact]
    public async Task Apply_AddsAndEditsMoves()
    {
        var (msel, _) = await SeedMsel();
        var moveEditor = await Actor().OnMsel(msel, MselRole.MoveEditor).SeedAsync();

        var result = await Read<MselAssistApplyResult>(await Apply(moveEditor, msel.Id,
            new MselChange { Op = "move.add", Move = 3, Minutes = 150, Description = "Disclosure", Situation = "The press has the story." },
            new MselChange { Op = "move.update", Target = "2", Situation = "Attackers pivot to the cloud.", SituationTime = "2026-03-05T09:00:00Z" },
            new MselChange { Op = "move.add", Move = 1, Description = "Duplicate" }));

        Assert.Equal([true, true, false], result.Results.Select(r => r.Applied));
        Assert.Equal("No longer possible: there is already a move 1.", result.Results[2].Error);
        await using var db = NewContext();
        var added = await db.Moves.SingleAsync(m => m.MselId == msel.Id && m.MoveNumber == 3, Ct);
        Assert.Equal(150 * 60, added.DeltaSeconds);
        Assert.Equal("The press has the story.", added.SituationDescription);
        var edited = await db.Moves.SingleAsync(m => m.MselId == msel.Id && m.MoveNumber == 2, Ct);
        Assert.Equal("Attackers pivot to the cloud.", edited.SituationDescription);
        Assert.Equal(new DateTime(2026, 3, 5, 9, 0, 0, DateTimeKind.Utc), edited.SituationTime?.ToUniversalTime());
        Assert.Null(added.SituationTime);
        Assert.Equal("Escalation", edited.Description);
        Assert.Equal(60 * 60, edited.DeltaSeconds);
    }

    private void Answer(object answer) =>
        _model.InvokeToolAsync(default, default, default, default, default, default)
            .ReturnsForAnyArgs(callInfo =>
            {
                _prompt = callInfo.ArgAt<string>(1);
                return JsonSerializer.SerializeToElement(answer);
            });

    private Task<HttpResponseMessage> Ask(TestActor actor, Guid mselId) =>
        Client(actor).PostAsJsonAsync($"/api/msels/{mselId}/ai-assist", new MselAssistRequest { Prompt = "Make the changes" }, Ct);

    private Task<HttpResponseMessage> Apply(TestActor actor, Guid mselId, params MselChange[] changes) =>
        Client(actor).PostAsJsonAsync($"/api/msels/{mselId}/ai-assist/apply", new MselAssistApply { Changes = changes.ToList() }, Ct);

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
    }

    // a MSEL starting at 13:00 with team BLUE, organization NEWS, moves 1 and 2, the usual columns, and two injects
    private async Task<(MselEntity, Dictionary<string, DataFieldEntity> Fields)> SeedMsel()
    {
        var msel = TestData.Msel();
        msel.StartTime = Start;
        var blue = TestData.Team(msel.Id);
        blue.ShortName = "BLUE";
        var news = TestData.Organization(msel.Id, name: "Daily News");
        news.ShortName = "NEWS";
        var move1 = TestData.Move(msel.Id, 1, 0);
        move1.Description = "Initial access";
        var move2 = TestData.Move(msel.Id, 2, 60 * 60);
        move2.Description = "Escalation";
        var fields = new Dictionary<string, DataFieldEntity>
        {
            ["Title"] = TestData.DataField(msel.Id, dataType: DataFieldType.String, displayOrder: 1, name: "Title"),
            ["Details"] = TestData.DataField(msel.Id, dataType: DataFieldType.Html, displayOrder: 2, name: "Details"),
            ["Priority"] = TestData.DataField(msel.Id, dataType: DataFieldType.String, displayOrder: 3, name: "Priority"),
            ["To Org"] = TestData.DataField(msel.Id, dataType: DataFieldType.TeamsMultiple, displayOrder: 4, name: "To Org"),
            ["Status"] = TestData.DataField(msel.Id, dataType: DataFieldType.Status, displayOrder: 5, name: "Status"),
            ["Delivery Time"] = TestData.DataField(msel.Id, dataType: DataFieldType.DateTime, displayOrder: 6, name: "Delivery Time")
        };
        foreach (var field in fields.Values)
            field.Description = null;
        fields["Priority"].IsChosenFromList = true;
        var high = TestData.DataOption(fields["Priority"].Id, "High", "High", 1);
        var low = TestData.DataOption(fields["Priority"].Id, "Low", "Low", 2);
        var first = TestData.ScenarioEvent(msel.Id, deltaSeconds: 10 * 60);
        var second = TestData.ScenarioEvent(msel.Id, deltaSeconds: 75 * 60, scenarioEventType: EventType.Information);
        await Seed([msel, blue, news, move1, move2, .. fields.Values, high, low, first, second,
            TestData.DataValue(fields["Title"].Id, first.Id, "Phishing wave"),
            TestData.DataValue(fields["Details"].Id, first.Id, "<p>Users report a fake payroll email.</p>"),
            TestData.DataValue(fields["Priority"].Id, first.Id, "High"),
            TestData.DataValue(fields["Delivery Time"].Id, first.Id, "2026-11-02T13:10:00.000Z"),
            TestData.DataValue(fields["Title"].Id, second.Id, "Status update")]);
        return (msel, fields);
    }

    private async Task SeedWorkRole()
    {
        var framework = TestData.CompetencyFramework();
        var category = TestData.Competency(framework.Id, "CS");
        var role = TestData.Competency(framework.Id, "CS-511", parentId: category.Id);
        role.ShortName = "Cyber Defense Analyst";
        await Seed([framework, category, role]);
    }
}
