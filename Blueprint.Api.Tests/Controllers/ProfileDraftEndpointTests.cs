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
/// <c>POST msels/ai-draft/profile</c> - a team's or organization's profile, for a MSEL by the roles that may edit it,
/// or for a draft by a planner who may create MSELs. Nothing is saved; the logo comes back as an image.
/// </summary>
public class ProfileDraftEndpointTests(DatabaseFixture fixture, AiEnabledFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<AiEnabledFactory>
{
    private readonly IAiModelClient _model = factory.Model;
    private string _prompt;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _model.ClearSubstitute();
        _model.InvokeToolAsync(default, default, default, default, default, default)
            .ReturnsForAnyArgs(callInfo =>
            {
                _prompt = callInfo.ArgAt<string>(1);
                return JsonSerializer.SerializeToElement(new
                {
                    html = "<table width=\"680\"><tr><td><svg viewBox=\"0 0 96 96\" aria-label=\"CERT logo\"><circle cx=\"48\" cy=\"48\" r=\"40\"/></svg></td>" +
                           "<td><h2>National CERT</h2><font face=\"Arial\" size=\"2\">Coordinates the response.</font></td></tr></table>",
                    notes = new[] { "Assumed the CERT has six staff." }
                });
            });
    }

    /// <summary>For a MSEL's team the model sees the scenario and the other teams and organizations; the logo becomes an image.</summary>
    [Fact]
    public async Task Draft_ForAMselsTeam_WritesTheProfileFromTheScenario()
    {
        var msel = await SeedMsel();
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var result = await Read<ProfileDraftResult>(await Post(owner, new ProfileDraftRequest
        {
            MselId = msel.Id, Kind = "team", ShortName = "CERT", Name = "National CERT", Description = "<p>Runs the national response.</p>", Instruction = "Add a duty officer"
        }));

        Assert.Contains("<img src=\"data:image/svg+xml;base64,", result.Html);
        Assert.Contains("alt=\"CERT logo\"", result.Html);
        Assert.Empty(GalleryArticleHtml.Problems(result.Html));
        Assert.Contains("Assumed the CERT has six staff.", result.Notes);
        Assert.Contains("Exercise: Deep Bite", _prompt);
        Assert.Contains("Scenario: Undersea cables are cut.", _prompt);
        Assert.Contains("- BLUE: Blue team,", _prompt);
        Assert.Contains("- NEWS: Harbour Herald, regional paper", _prompt);
        Assert.Contains("Write the profile of this participant team:", _prompt);
        Assert.Contains("What the planner has written so far: Runs the national response.", _prompt);
        Assert.Contains("The planner's instruction: Add a duty officer", _prompt);
    }

    /// <summary>Editors may have an organization's profile written but not a team's, as with editing them.</summary>
    [Theory]
    [InlineData(MselRole.Editor, "organization", HttpStatusCode.OK)]
    [InlineData(MselRole.Editor, "team", HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Viewer, "organization", HttpStatusCode.Forbidden)]
    public async Task Draft_ForAMsel_IsAllowedAsEditingTheTeamOrOrganizationIs(MselRole role, string kind, HttpStatusCode expected)
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Post(actor, new ProfileDraftRequest { MselId = msel.Id, Kind = kind, ShortName = "X", Name = "X" });

        Assert.Equal(expected, response.StatusCode);
    }

    /// <summary>For a draft, the scenario comes from the draft, and the planner needs CreateMsels.</summary>
    [Fact]
    public async Task Draft_ForADraft_UsesTheDraftsScenario()
    {
        var planner = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var result = await Read<ProfileDraftResult>(await Post(planner, new ProfileDraftRequest
        {
            Kind = "organization", ShortName = "NEWS", Name = "Harbour Herald", Summary = "Regional paper",
            Draft = new MselDraft { Name = "Port Storm", Description = "A storm closes the port.", Organizations = [new MselDraftOrganization { ShortName = "PORT", Name = "Port Authority", Description = "Runs the port" }] }
        }));

        Assert.NotEmpty(result.Html);
        Assert.Contains("Exercise: Port Storm", _prompt);
        Assert.Contains("- PORT: Port Authority, Runs the port", _prompt);
        Assert.Contains("Write the profile of this organization:", _prompt);
        Assert.Contains("Summary: Regional paper", _prompt);
    }

    [Fact]
    public async Task Draft_ForADraft_WithoutCreateMsels_IsForbidden()
    {
        var actor = await Actor().SeedAsync();

        var response = await Post(actor, new ProfileDraftRequest { Kind = "team", ShortName = "X", Draft = new MselDraft { Name = "D" } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_model.ReceivedCalls());
    }

    [Theory]
    [InlineData("unit", "X")]
    [InlineData("team", null)]
    public async Task Draft_WithoutAKindOrName_IsABadRequest(string kind, string shortName)
    {
        var planner = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Post(planner, new ProfileDraftRequest { Kind = kind, ShortName = shortName, Draft = new MselDraft { Name = "D" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>An organization whose description became a profile keeps a one-line summary when the draft is imported.</summary>
    [Fact]
    public async Task Import_KeepsAnOrganizationsSummaryToOneLine()
    {
        var template = TestData.Msel(isTemplate: true);
        await Seed(template, TestData.DataField(template.Id, name: "Title"));
        var planner = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var draft = new MselDraft
        {
            Name = "Port Storm",
            DurationMinutes = 60,
            Moves = [new MselDraftMove { MoveNumber = 0, Description = "Start" }],
            Organizations =
            [
                new MselDraftOrganization { ShortName = "NEWS", Name = "Harbour Herald", Description = "<table><tr><td><h2>Harbour Herald</h2></td></tr></table>", Summary = "Regional paper" },
                new MselDraftOrganization { ShortName = "PORT", Name = "Port Authority", Description = "<p>Runs the <b>port</b>.</p>" }
            ],
            Injects = [new MselDraftInject { Minutes = 5, Values = new() { ["Title"] = "Storm" } }]
        };

        var response = await Client(planner).PostAsJsonAsync("/api/msels/ai-draft/import", new MselDraftImport { TemplateMselId = template.Id, Draft = draft }, JsonOptions, Ct);

        var mselId = (await Read<MselImportResult>(response)).Msel.Id;
        var organizations = await ReadBack(db => db.Organizations.Where(o => o.MselId == mselId).ToListAsync(Ct));
        Assert.Equal("Regional paper", organizations.Single(o => o.ShortName == "NEWS").Summary);
        Assert.Equal("Runs the port.", organizations.Single(o => o.ShortName == "PORT").Summary);
    }

    private Task<HttpResponseMessage> Post(TestActor actor, ProfileDraftRequest request) =>
        Client(actor).PostAsJsonAsync("/api/msels/ai-draft/profile", request, JsonOptions, Ct);

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
    }

    private async Task<MselEntity> SeedMsel()
    {
        var msel = TestData.Msel();
        msel.Name = "Deep Bite";
        msel.Description = "<p>Undersea cables are cut.</p>";
        var blue = TestData.Team(msel.Id);
        blue.ShortName = "BLUE";
        blue.Name = "Blue team";
        var news = TestData.Organization(msel.Id, name: "Harbour Herald");
        news.ShortName = "NEWS";
        news.Summary = "regional paper";
        await Seed(msel, blue, news);
        return msel;
    }
}
