// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
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
    public async Task Ask_ByAnEditor_ProposesLabelledChangesAndSavesNothing()
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
        var editor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var result = await Read<MselAssistResult>(await Ask(editor, msel.Id, "Add a CERT team"));

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
        Assert.Contains("there is already a team CERT", refused.Label);
        Assert.True(Assert.Single(result.Results, r => r.Applied).Applied);
        Assert.Equal(1, await ReadBack(db => db.Teams.CountAsync(t => t.MselId == msel.Id && t.ShortName == "CERT", Ct)));
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
