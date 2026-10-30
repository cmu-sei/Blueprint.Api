// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
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
/// What creating a drafted MSEL sets up so the planner can run it at once: the Info page's metadata, every Gallery
/// column, briefings as HTML, the planner's units and a seat on exercise control, and the Player applications each
/// team needs.
/// </summary>
public class MselDraftSetupEndpointTests(DatabaseFixture fixture, AiEnabledFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<AiEnabledFactory>
{
    private readonly Cite.Api.Client.ICiteApiClient _cite = factory.Cite;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _cite.ClearSubstitute();
        _cite.GetTeamTypesAsync(Arg.Any<CancellationToken>()).Returns(new List<Cite.Api.Client.TeamType>());
        _cite.GetScoringModelsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>()).Returns(new List<Cite.Api.Client.ScoringModel>());
    }

    /// <summary>
    /// The Info page gets the draft's metadata, only from the page's choices, with the format among the keywords;
    /// a plain-text briefing becomes a paragraph and an HTML one keeps its structure; every Gallery parameter has a column.
    /// </summary>
    [Fact]
    public async Task Import_FillsTheInfoPageAndEveryGalleryColumn()
    {
        var (template, _) = await SeedTemplate(usePlayer: false);
        var planner = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var (mselId, _) = await Import(planner, template);

        await using var db = NewContext();
        var msel = await db.Msels.SingleAsync(m => m.Id == mselId, Ct);
        Assert.Equal("Intermediate", msel.EducationalLevel);
        Assert.Equal("Instruction", msel.EducationalUse);
        Assert.Null(msel.CourseMode);
        Assert.Equal("Incident response", msel.Subject);
        Assert.Equal("ransomware, tabletop exercise", msel.Keywords);
        var situations = await db.Moves.Where(m => m.MselId == mselId).OrderBy(m => m.MoveNumber).Select(m => m.SituationDescription).ToListAsync(Ct);
        // move 0 is the pre-exercise; play starts at move 1
        Assert.Contains("<h4>Pre-exercise</h4>", situations[0]);
        Assert.Equal("<p>The islands lose internet &amp; phone.</p>", situations[1]);
        Assert.Contains("<h4>Press pressure</h4>", situations[2]);
        Assert.DoesNotContain("style", situations[2]);
        var parameters = await db.DataFields.Where(f => f.MselId == mselId && f.GalleryArticleParameter != null).Select(f => f.GalleryArticleParameter).ToListAsync(Ct);
        Assert.Equal(Enum.GetNames<GalleryArticleParameter>().Order(), parameters.Order());
    }

    /// <summary>The planner's units become contributors and the planner joins EXCON; the report says both.</summary>
    [Fact]
    public async Task Import_SharesTheMselWithThePlannersUnitsAndPutsThemOnExcon()
    {
        var (template, _) = await SeedTemplate(usePlayer: false);
        var unit = TestData.Unit(name: "Cyber Range Team");
        unit.ShortName = "CRT";
        await Seed(unit);
        var planner = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).InUnit(unit).SeedAsync();

        var (mselId, result) = await Import(planner, template);

        await using var db = NewContext();
        Assert.Equal([unit.Id], await db.MselUnits.Where(mu => mu.MselId == mselId).Select(mu => mu.UnitId).ToListAsync(Ct));
        var seat = await db.TeamUsers.Include(tu => tu.Team).SingleAsync(tu => tu.UserId == planner.Id && tu.Team.MselId == mselId, Ct);
        Assert.Equal("EXCON", seat.Team.ShortName);
        Assert.Contains(result.Changes, c => c.Message == "Added your unit CRT to the contributors.");
        Assert.Contains(result.Changes, c => c.Message == "Put you on EXCON, the exercise control team.");
    }

    /// <summary>
    /// Every team gets CITE, the Gallery wall and archive, the pages and the live MSEL view; exercise control also gets
    /// Steamfitter; the template's applications reach the drafted teams of the same kind, and none is added twice.
    /// </summary>
    [Fact]
    public async Task Import_GivesEveryTeamItsPlayerApplications()
    {
        var (template, _) = await SeedTemplate(usePlayer: true);
        var planner = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var (mselId, _) = await Import(planner, template);

        await using var db = NewContext();
        var teams = await db.Teams.Where(t => t.MselId == mselId).ToDictionaryAsync(t => t.Id, t => t.ShortName, Ct);
        var apps = await db.PlayerApplications.Include(a => a.PlayerApplicationTeams).Where(a => a.MselId == mselId)
            .ToDictionaryAsync(a => a.Name, a => a.PlayerApplicationTeams.Select(pt => teams[pt.TeamId]).Order().ToArray(), Ct);
        string[] everyone = ["BLUE", "EXCON", "SOC"];
        Assert.Equal(everyone, apps["CITE Dashboard"]);
        Assert.Equal(everyone, apps["Gallery Wall"]);
        Assert.Equal(everyone, apps["Read-In"]);
        Assert.Equal(everyone, apps["Exercise Timeline"]);
        Assert.Equal(everyone, apps["Team Profiles"]);
        Assert.Equal(["EXCON"], apps["Steamfitter Tasks"]);
        Assert.Equal(["EXCON"], apps["Steamfitter Scenarios"]);
        // the template's archive application is Gallery Archive already, and reaches the drafted team SOC
        Assert.False(apps.ContainsKey("Gallery Archive"));
        Assert.Equal(everyone, apps["Gallery"]);
        // the template's EXCON-only application stays with exercise control
        Assert.Equal(["EXCON"], apps["Range Admin"]);
    }

    private async Task<(Guid MselId, MselImportResult Result)> Import(TestActor planner, MselEntity template)
    {
        var draft = JsonSerializer.Deserialize<MselDraft>(Draft(), JsonOptions);
        draft.Design = new MselDraftDesign { Format = "Discussion", CiteAssessment = "None" };
        var response = await Client(planner).PostAsJsonAsync("/api/msels/ai-draft/import", new MselDraftImport { TemplateMselId = template.Id, Draft = draft }, JsonOptions, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        var result = await response.Content.ReadFromJsonAsync<MselImportResult>(JsonOptions, Ct);
        return (result.Msel.Id, result);
    }

    // a template with every integration, teams BLUE and EXCON, a Title column, and with Player a Gallery archive
    // application for both teams and a Range Admin application for EXCON
    private async Task<(MselEntity, TeamEntity)> SeedTemplate(bool usePlayer)
    {
        var template = TestData.Msel(isTemplate: true);
        template.UsePlayer = usePlayer;
        template.UseGallery = true;
        template.UseCite = true;
        template.UseSteamfitter = true;
        var blue = TestData.Team(template.Id);
        blue.ShortName = "BLUE";
        var excon = TestData.Team(template.Id);
        excon.ShortName = "EXCON";
        var entities = new List<object> { template, blue, excon, TestData.DataField(template.Id, name: "Title", displayOrder: 1) };
        if (usePlayer)
        {
            var gallery = TestData.PlayerApplication(template.Id, name: "Gallery", url: "{galleryUrl}?section=archive&exhibit={galleryExhibitId}{theme}");
            var admin = TestData.PlayerApplication(template.Id, name: "Range Admin", url: "https://range.example/admin");
            entities.AddRange([gallery, admin,
                TestData.PlayerApplicationTeam(gallery.Id, blue.Id), TestData.PlayerApplicationTeam(gallery.Id, excon.Id),
                TestData.PlayerApplicationTeam(admin.Id, excon.Id)]);
        }
        await Seed(entities.ToArray());
        return (template, excon);
    }

    private static string Draft() =>
        JsonSerializer.Serialize(new
        {
            name = "Deep Bite",
            description = "Cables are cut.",
            durationMinutes = 120,
            metadata = new { educationalLevel = "intermediate", educationalUse = "Instruction", courseMode = "Hybrid", subject = "Incident response", keywords = "ransomware" },
            moves = new object[]
            {
                new { moveNumber = 0, description = "Start", situationDescription = "The islands lose internet & phone.", startMinutes = 0 },
                new { moveNumber = 1, description = "Press", situationDescription = "<h4>Press pressure</h4><p style=\"color:red\">Reporters call.</p>", startMinutes = 60 }
            },
            teams = new[] { new { name = "Security operations", shortName = "SOC", description = "Watches the network." } },
            organizations = Array.Empty<object>(),
            injects = new object[] { new { minutes = 5, eventType = "Inject", values = new Dictionary<string, string> { ["Title"] = "Outage" } } },
            readIn = "<p>Read this.</p>"
        });
}
