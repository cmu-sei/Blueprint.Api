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
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// <c>POST msels/{id}/ai-assist</c> and <c>msels/{id}/ai-assist/apply</c> - the planner asks for changes in their own
/// words, reviews what the model proposes, and applies what they keep through the usual team, organization, card and
/// competency rules.
/// </summary>
public class MselAssistEndpointTests(DatabaseFixture fixture, AiEnabledFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<AiEnabledFactory>
{
    private readonly IAiModelClient _model = factory.Model;
    private string _prompt;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _model.ClearSubstitute();
    }

    /// <summary>The model sees the MSEL and the work roles it may add; the planner gets labelled changes and nothing is saved.</summary>
    [Fact]
    public async Task Ask_ByTheOwner_ProposesLabelledChangesAndSavesNothing()
    {
        var (msel, _) = await SeedMsel();
        await SeedWorkRole("CS-511", "Cyber Defense Analyst");
        Answer(new
        {
            reply = "Adding a CERT team with the analyst work role.",
            changes = new object[]
            {
                new { op = "team.add", shortName = "CERT", name = "National CERT" },
                new { op = "competency.add", target = "CERT", idNumber = "CS-511" },
                new { op = "team.add", shortName = "BLUE" }
            }
        });
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var result = await Read<MselAssistResult>(await Ask(owner, msel.Id, "Add a CERT team"));

        Assert.Equal("Adding a CERT team with the analyst work role.", result.Reply);
        Assert.Equal(["Add team CERT (National CERT)", "Add CS-511 Cyber Defense Analyst to team CERT"], result.Changes.Select(c => c.Label));
        Assert.Contains(result.Warnings, w => w.Contains("there is already a team BLUE"));
        Assert.Contains("- BLUE (Blue team)", _prompt);
        Assert.Contains("- NEWS (Daily News)", _prompt);
        Assert.Contains("- CS-511 Cyber Defense Analyst", _prompt);
        Assert.Contains("Planner's request:\nAdd a CERT team", _prompt.ReplaceLineEndings("\n"));
        Assert.False(await ReadBack(db => db.Teams.AnyAsync(t => t.MselId == msel.Id && t.ShortName == "CERT", Ct)));
    }

    /// <summary>The earlier turns are sent, so a follow-up can say "do that for the other team too".</summary>
    [Fact]
    public async Task Ask_SendsTheConversationSoFar()
    {
        var (msel, _) = await SeedMsel();
        Answer(new { reply = "Done.", changes = Array.Empty<object>() });
        var editor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        await Ask(editor, msel.Id, "And for EXCON?", new MselAssistMessage { Role = "planner", Text = "Give BLUE a card" }, new MselAssistMessage { Role = "assistant", Text = "Added a BLUE card." });

        Assert.Contains("Planner: Give BLUE a card\nAssistant: Added a BLUE card.", _prompt.ReplaceLineEndings("\n"));
    }

    /// <summary>A question means the request was ambiguous; any changes that came with it are not offered.</summary>
    [Fact]
    public async Task Ask_ThatGetsAQuestion_OffersNoChanges()
    {
        var (msel, _) = await SeedMsel();
        Answer(new { reply = "", question = "Which team should get the card?", changes = new object[] { new { op = "card.add", name = "Wall" } } });
        var editor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var result = await Read<MselAssistResult>(await Ask(editor, msel.Id, "Add a card"));

        Assert.Equal("Which team should get the card?", result.Question);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public async Task Ask_ByAViewer_IsForbiddenWithoutAskingTheModel()
    {
        var (msel, _) = await SeedMsel();
        var viewer = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var response = await Ask(viewer, msel.Id, "Add a team");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_model.ReceivedCalls());
    }

    /// <summary>Every kind of change lands, in order, so a competency can go on a team added in the same apply.</summary>
    [Fact]
    public async Task Apply_ByAnAdministrator_MakesEachChange()
    {
        var (msel, _) = await SeedMsel();
        var role = await SeedWorkRole("CS-511", "Cyber Defense Analyst");
        var admin = await Actor().WithAllSystemPermissions().SeedAsync();

        var result = await Read<MselAssistApplyResult>(await Apply(admin, msel.Id,
            new MselChange { Op = "team.add", ShortName = "CERT", Name = "National CERT", Description = "Coordinates the patch" },
            new MselChange { Op = "competency.add", Target = "CERT", IdNumber = "CS-511" },
            new MselChange { Op = "competency.add", IdNumber = "CS-511" },
            new MselChange { Op = "team.update", Target = "BLUE", Name = "Blue cell" },
            new MselChange { Op = "organization.add", ShortName = "VENDOR", Name = "PHP Vendor", Summary = "Ships the fix" },
            new MselChange { Op = "organization.update", Target = "NEWS", Summary = "Regional outlet" },
            new MselChange { Op = "card.add", Name = "Patch Status", Move = 2 },
            new MselChange { Op = "card.update", Target = "Situation", Description = "What participants know" }));

        Assert.All(result.Results, r => Assert.True(r.Applied, $"{r.Label}: {r.Error}"));
        await using var db = NewContext();
        var cert = await db.Teams.Include(t => t.TeamCompetencies).SingleAsync(t => t.MselId == msel.Id && t.ShortName == "CERT", Ct);
        Assert.Equal("Coordinates the patch", cert.Description);
        Assert.Equal(role.Id, Assert.Single(cert.TeamCompetencies).CompetencyId);
        Assert.True(await db.MselCompetencies.AnyAsync(mc => mc.MselId == msel.Id && mc.CompetencyId == role.Id, Ct));
        var blue = await db.Teams.SingleAsync(t => t.MselId == msel.Id && t.ShortName == "BLUE", Ct);
        Assert.Equal("Blue cell", blue.Name);
        Assert.Equal("blue@team.test", blue.Email);
        Assert.Equal("Ships the fix", (await db.Organizations.SingleAsync(o => o.MselId == msel.Id && o.ShortName == "VENDOR", Ct)).Summary);
        var news = await db.Organizations.SingleAsync(o => o.MselId == msel.Id && o.ShortName == "NEWS", Ct);
        Assert.Equal("Regional outlet", news.Summary);
        Assert.Equal("Daily News", news.Name);
        Assert.Equal(2, (await db.Cards.SingleAsync(c => c.MselId == msel.Id && c.Name == "Patch Status", Ct)).Move);
        Assert.Equal("What participants know", (await db.Cards.SingleAsync(c => c.MselId == msel.Id && c.Name == "Situation", Ct)).Description);
    }

    /// <summary>A change that was possible when proposed but not now is reported, and the rest still apply.</summary>
    [Fact]
    public async Task Apply_ChecksEachChangeAgainstTheMselAsItIsNow()
    {
        var (msel, blue) = await SeedMsel();
        var admin = await Actor().WithAllSystemPermissions().SeedAsync();
        await ReadBack(async db =>
        {
            db.Teams.Add(new TeamEntity { Id = Guid.NewGuid(), MselId = msel.Id, ShortName = "CERT", Name = "Added meanwhile" });
            return await db.SaveChangesAsync(Ct);
        });

        var result = await Read<MselAssistApplyResult>(await Apply(admin, msel.Id,
            new MselChange { Op = "team.add", ShortName = "CERT" },
            new MselChange { Op = "card.add", Name = "Patch Status" }));

        var refused = Assert.Single(result.Results, r => !r.Applied);
        Assert.Equal("Add team CERT (CERT)", refused.Label);
        Assert.Equal("No longer possible: there is already a team CERT.", refused.Error);
        Assert.True(Assert.Single(result.Results, r => r.Applied).Applied);
        Assert.Equal(1, await ReadBack(db => db.Teams.CountAsync(t => t.MselId == msel.Id && t.ShortName == "CERT", Ct)));
    }

    /// <summary>
    /// A column added in a proposal is one the injects after it may write, so "add a Severity column and set it" is one apply;
    /// its value is checked against its choices. An edit renames it and replaces its choices, keeping the values chosen.
    /// </summary>
    [Fact]
    public async Task Apply_AddsAColumnAndFillsItOnTheInjects()
    {
        var (msel, _) = await SeedMsel();
        var title = TestData.DataField(msel.Id, name: "Title", displayOrder: 1);
        var existing = TestData.ScenarioEvent(msel.Id);
        await Seed(title, existing, TestData.DataValue(title.Id, existing.Id, "Cables cut"));
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var result = await Read<MselAssistApplyResult>(await Apply(owner, msel.Id,
            new MselChange { Op = "column.add", Name = "Severity", Description = "How bad it is", Options = ["Low", "Medium", "High"] },
            new MselChange { Op = "inject.update", Target = "E1", Values = new Dictionary<string, string> { ["Severity"] = "High" } },
            new MselChange { Op = "inject.add", Minutes = 30, EventType = "Inject", Values = new Dictionary<string, string> { ["Title"] = "Press call", ["Severity"] = "Low" } }));

        Assert.All(result.Results, r => Assert.True(r.Applied, $"{r.Label}: {r.Error}"));
        Assert.Equal("Add column Severity (list: Low, Medium, High)", result.Results[0].Label);
        await using var db = NewContext();
        var severity = await db.DataFields.Include(f => f.DataOptions).SingleAsync(f => f.MselId == msel.Id && f.Name == "Severity", Ct);
        Assert.True(severity.IsChosenFromList);
        Assert.Equal(["Low", "Medium", "High"], severity.DataOptions.OrderBy(o => o.DisplayOrder).Select(o => o.OptionValue));
        Assert.True(severity.DisplayOrder > title.DisplayOrder);
        var values = await db.DataValues.Where(v => v.DataFieldId == severity.Id && v.Value != null).Select(v => v.Value).ToListAsync(Ct);
        Assert.Equal(["High", "Low"], values.OrderBy(v => v));

        var renamed = await Read<MselAssistApplyResult>(await Apply(owner, msel.Id,
            new MselChange { Op = "column.update", Target = "Severity", Name = "Impact", Options = ["Low", "High", "Critical"] }));
        Assert.True(Assert.Single(renamed.Results).Applied, renamed.Results[0].Error);
        Assert.Equal("Edit column Severity: name \"Impact\", choices Low, High, Critical", renamed.Results[0].Label);
        await using var after = NewContext();
        var impact = await after.DataFields.Include(f => f.DataOptions).SingleAsync(f => f.Id == severity.Id, Ct);
        Assert.Equal("Impact", impact.Name);
        // the options that stay keep their ids
        Assert.Equal(severity.DataOptions.Single(o => o.OptionValue == "High").Id, impact.DataOptions.Single(o => o.OptionValue == "High").Id);
    }

    /// <summary>A column change that cannot be made is left out with the reason: a name taken, a list on a number, a Gallery parameter another column fills.</summary>
    [Fact]
    public async Task Ask_LeavesOutColumnChangesThatCannotBeMade()
    {
        var (msel, _) = await SeedMsel();
        var summary = TestData.DataField(msel.Id, name: "Summary", displayOrder: 1);
        summary.GalleryArticleParameter = "Summary";
        await Seed(summary);
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        Answer(new
        {
            reply = "Columns",
            changes = new object[]
            {
                new { op = "column.add", name = "Summary" },
                new { op = "column.add", name = "Count", dataType = "Integer", options = new[] { "1", "2" } },
                new { op = "column.add", name = "Gist", galleryParameter = "Summary" },
                new { op = "column.add", name = "Analyst Notes", dataType = "Html" }
            }
        });

        var result = await Read<MselAssistResult>(await Ask(owner, msel.Id, "Add columns"));

        Assert.Equal("Add column Analyst Notes (Html)", Assert.Single(result.Changes).Label);
        Assert.Contains("Add column Summary (String): left out, there is already a column Summary.", result.Warnings);
        Assert.Contains("Add column Count (list: 1, 2): left out, only a text column can be chosen from a list.", result.Warnings);
        Assert.Contains("Add column Gist (String): left out, the column Summary already fills the Gallery Summary.", result.Warnings);
        Assert.Contains("Summary, String:", _prompt);
        Assert.Contains("Column types: String, Html,", _prompt);
    }

    /// <summary>
    /// Turning Gallery on adds the article columns, which the injects after it in the same apply write to; turning CITE on gives
    /// the teams CITE team types and authors the preset the model chose.
    /// </summary>
    [Fact]
    public async Task Apply_TurnsOnGalleryAndCiteWithWhatTheyNeed()
    {
        var msel = TestData.Msel();
        var blue = TestData.Team(msel.Id);
        blue.ShortName = "BLUE";
        var excon = TestData.Team(msel.Id);
        excon.ShortName = "EXCON";
        var title = TestData.DataField(msel.Id, name: "Title", displayOrder: 1);
        var existing = TestData.ScenarioEvent(msel.Id);
        await Seed(msel, blue, excon, title, existing, TestData.DataValue(title.Id, existing.Id, "Cables cut"));
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var contributor = new Cite.Api.Client.TeamType { Id = Guid.NewGuid(), Name = "Official Score Contributor" };
        var other = new Cite.Api.Client.TeamType { Id = Guid.NewGuid(), Name = "Other" };
        Factory.Cite.GetTeamTypesAsync(Arg.Any<CancellationToken>()).Returns(new List<Cite.Api.Client.TeamType> { contributor, other });
        Answer(new { reply = "Gallery and CITE", changes = new object[] { new { op = "integration.add", integration = "Gallery" }, new { op = "integration.add", integration = "cite", preset = "Nciss" } } });

        var proposal = await Read<MselAssistResult>(await Ask(owner, msel.Id, "Use Gallery and CITE"));
        Assert.StartsWith("Turn on Gallery, adding the columns ", proposal.Changes[0].Label);
        Assert.Equal("Turn on CITE, giving the teams CITE team types and authoring the CISA NCISS scoring model", proposal.Changes[1].Label);

        var result = await Read<MselAssistApplyResult>(await Apply(owner, msel.Id,
            proposal.Changes[0],
            new MselChange { Op = "inject.update", Target = "E1", Values = new Dictionary<string, string> { ["SourceType"] = "News" } },
            proposal.Changes[1]));

        Assert.All(result.Results, r => Assert.True(r.Applied, $"{r.Label}: {r.Error}"));
        await using var db = NewContext();
        var stored = await db.Msels.Include(m => m.DataFields).Include(m => m.Teams).Include(m => m.ScoringModel).AsSplitQuery().SingleAsync(m => m.Id == msel.Id, Ct);
        Assert.True(stored.UseGallery);
        Assert.True(stored.UseCite);
        Assert.Contains(stored.DataFields, f => f.GalleryArticleParameter == "SourceType");
        var source = stored.DataFields.Single(f => f.GalleryArticleParameter == "SourceType");
        Assert.Equal("News", await db.DataValues.Where(v => v.DataFieldId == source.Id && v.ScenarioEventId == existing.Id).Select(v => v.Value).SingleAsync(Ct));
        Assert.Equal(contributor.Id, stored.Teams.Single(t => t.ShortName == "BLUE").CiteTeamTypeId);
        Assert.Equal(other.Id, stored.Teams.Single(t => t.ShortName == "EXCON").CiteTeamTypeId);
        Assert.Equal("CISA NCISS", stored.ScoringModel.Description);
    }

    /// <summary>An integration already on, or any change to the integrations of a pushed MSEL, is left out with the reason.</summary>
    [Fact]
    public async Task Ask_LeavesOutIntegrationsThatCannotChange()
    {
        var (msel, _) = await SeedMsel();
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        Answer(new { reply = "On", changes = new object[] { new { op = "integration.add", integration = "Gallery" }, new { op = "integration.add", integration = "Steamfitter" } } });

        var result = await Read<MselAssistResult>(await Ask(owner, msel.Id, "Use Gallery and Steamfitter"));
        Assert.Contains("Turn on Gallery: left out, the MSEL already uses Gallery.", result.Warnings);
        Assert.Equal("Turn on Steamfitter (set it up on its tab)", Assert.Single(result.Changes).Label);

        await ReadBack(async db =>
        {
            var stored = await db.Msels.SingleAsync(m => m.Id == msel.Id, Ct);
            stored.GalleryExhibitId = Guid.NewGuid();
            return await db.SaveChangesAsync(Ct);
        });
        result = await Read<MselAssistResult>(await Ask(owner, msel.Id, "Use Steamfitter"));
        Assert.Contains("Turn on Steamfitter: left out, the MSEL has been pushed, and its integrations change only after a pull.", result.Warnings);
        Assert.Contains("(pushed: integrations cannot change until it is pulled)", _prompt);
    }

    /// <summary>
    /// Ask AI adds a page and edits one through the page rules; script is removed. A page too long to have been shown whole is not
    /// edited, as its content would lose the rest; nor is one the edit would empty.
    /// </summary>
    [Fact]
    public async Task Apply_AddsAndEditsPages()
    {
        var (msel, _) = await SeedMsel();
        var readIn = new MselPageEntity { Id = Guid.NewGuid(), MselId = msel.Id, Name = "Read-In", Content = "<p>Old</p>", AllCanView = true, IncludeInPlaybook = true };
        var guide = new MselPageEntity { Id = Guid.NewGuid(), MselId = msel.Id, Name = "Facilitator Guide", Content = "<p>" + new string('x', MselAssistRules.MaxPageShown + 10) + "</p>" };
        await Seed(readIn, guide);
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        Answer(new
        {
            reply = "Pages",
            changes = new object[]
            {
                new { op = "page.add", name = "Rules of Play", content = "<h2>Rules</h2><p>Stay in role.</p><script>x()</script>", allCanView = true },
                new { op = "page.update", target = "read-in", content = "<p>New read-in</p>" },
                new { op = "page.update", target = "Facilitator Guide", content = "<p>Short</p>" },
                new { op = "page.update", target = "Read-In", content = "<p></p>" }
            }
        });

        var proposal = await Read<MselAssistResult>(await Ask(owner, msel.Id, "Add rules of play and update the read-in"));

        Assert.Equal(["Add page Rules of Play (participants can see it)", "Edit page Read-In: content"], proposal.Changes.Select(c => c.Label));
        Assert.Contains("Edit page Facilitator Guide: content: left out, the page is too long to edit here; use Write with AI on the page.", proposal.Warnings);
        Assert.Contains("Edit page Read-In: content: left out, it would empty the page.", proposal.Warnings);
        Assert.Contains("- Read-In (participants and controllers):", _prompt);
        Assert.Contains("[cut short]", _prompt);

        var result = await Read<MselAssistApplyResult>(await Apply(owner, msel.Id, proposal.Changes.ToArray()));
        Assert.All(result.Results, r => Assert.True(r.Applied, $"{r.Label}: {r.Error}"));
        await using var db = NewContext();
        var pages = await db.MselPages.Where(p => p.MselId == msel.Id).ToDictionaryAsync(p => p.Name, Ct);
        Assert.DoesNotContain("<script", pages["Rules of Play"].Content);
        Assert.True(pages["Rules of Play"].AllCanView);
        Assert.Equal("<p>New read-in</p>", pages["Read-In"].Content);
        Assert.True(pages["Read-In"].AllCanView);
    }

    [Fact]
    public async Task Apply_ByAViewer_IsForbiddenAndChangesNothing()
    {
        var (msel, _) = await SeedMsel();
        var viewer = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var response = await Apply(viewer, msel.Id, new MselChange { Op = "team.add", ShortName = "CERT" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await ReadBack(db => db.Teams.AnyAsync(t => t.MselId == msel.Id && t.ShortName == "CERT", Ct)));
    }

    private void Answer(object answer) =>
        _model.InvokeToolAsync(default, default, default, default, default, default)
            .ReturnsForAnyArgs(callInfo =>
            {
                _prompt = callInfo.ArgAt<string>(1);
                return JsonSerializer.SerializeToElement(answer);
            });

    private Task<HttpResponseMessage> Ask(TestActor actor, Guid mselId, string prompt, params MselAssistMessage[] history) =>
        Client(actor).PostAsJsonAsync($"/api/msels/{mselId}/ai-assist", new MselAssistRequest { Prompt = prompt, History = history.ToList() }, Ct);

    /// <summary>
    /// A Gallery article the assistant adds, or a row an edit turns into one, gets a Gallery integration target, so the
    /// push sends it; other targets on the row stay.
    /// </summary>
    [Fact]
    public async Task Apply_SendsTheArticlesItAddsOrMakesToGallery()
    {
        var (msel, _) = await SeedMsel();
        var title = TestData.DataField(msel.Id, name: "Title", displayOrder: 1);
        title.GalleryArticleParameter = "Name";
        var details = TestData.DataField(msel.Id, dataType: DataFieldType.Html, name: "Details", displayOrder: 2);
        details.GalleryArticleParameter = "Description";
        var source = TestData.DataField(msel.Id, dataType: DataFieldType.SourceType, name: "Source Type", displayOrder: 3);
        source.GalleryArticleParameter = "SourceType";
        var existing = TestData.ScenarioEvent(msel.Id);
        existing.IntegrationTarget = "Steamfitter";
        await Seed(title, details, source, existing, TestData.DataValue(title.Id, existing.Id, "Controller check"));
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var result = await Read<MselAssistApplyResult>(await Apply(owner, msel.Id,
            new MselChange { Op = "inject.add", Minutes = 30, EventType = "Inject", Values = new Dictionary<string, string> { ["Title"] = "Cables cut", ["Source Type"] = "News" } },
            new MselChange { Op = "inject.add", Minutes = 40, EventType = "Facilitation", Values = new Dictionary<string, string> { ["Title"] = "Check in" } },
            new MselChange { Op = "inject.update", Target = "E1", Values = new Dictionary<string, string> { ["Source Type"] = "Email" } }));

        Assert.All(result.Results, r => Assert.True(r.Applied, r.Error));
        await using var db = NewContext();
        var targets = await db.ScenarioEvents.Where(e => e.MselId == msel.Id).ToDictionaryAsync(e => e.DeltaSeconds, e => e.IntegrationTarget, Ct);
        Assert.Equal("Gallery", targets[30 * 60]);
        Assert.Null(targets[40 * 60]);
        Assert.Equal("Steamfitter|Gallery", targets[existing.DeltaSeconds]);
    }

    private Task<HttpResponseMessage> Apply(TestActor actor, Guid mselId, params MselChange[] changes) =>
        Client(actor).PostAsJsonAsync($"/api/msels/{mselId}/ai-assist/apply", new MselAssistApply { Changes = changes.ToList() }, Ct);

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
    }

    // a MSEL with team BLUE, organization NEWS and card Situation
    private async Task<(MselEntity, TeamEntity Blue)> SeedMsel()
    {
        var msel = TestData.Msel();
        msel.UseGallery = true;
        var blue = TestData.Team(msel.Id);
        blue.ShortName = "BLUE";
        blue.Name = "Blue team";
        blue.Email = "blue@team.test";
        var news = TestData.Organization(msel.Id, name: "Daily News");
        news.ShortName = "NEWS";
        var card = TestData.Card(msel.Id);
        card.Name = "Situation";
        await Seed(msel, blue, news, card);
        return (msel, blue);
    }

    // a work role, one level under a framework's category
    private async Task<CompetencyEntity> SeedWorkRole(string idNumber, string name)
    {
        var framework = TestData.CompetencyFramework();
        var category = TestData.Competency(framework.Id, "CS");
        var role = TestData.Competency(framework.Id, idNumber, parentId: category.Id);
        role.ShortName = name;
        await Seed([framework, category, role]);
        return role;
    }
}
