// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Services;

public class MselCopySiteTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    [Fact]
    public async Task Import_answers_duplicate_team_competencies_with_a_server_error_and_saves_no_copy()
    {
        var framework = TestData.CompetencyFramework();
        var competency = TestData.Competency(framework.Id);
        await Seed(framework, competency);
        var msel = TestData.Msel();
        var team = TestData.Team(msel.Id);
        team.TeamCompetencies.Add(TestData.TeamCompetency(team.Id, competency.Id));
        team.TeamCompetencies.Add(TestData.TeamCompetency(team.Id, competency.Id));
        msel.Teams.Add(team);

        var response = await Import(msel);

        await CopyError(response);
        Assert.Equal(0, await ReadBack(db => db.Msels.CountAsync(Ct)));
        Assert.Equal(0, await ReadBack(db => db.TeamCompetencies.CountAsync(Ct)));
    }

    [Fact]
    public async Task Import_answers_duplicate_card_team_assignments_with_a_server_error_and_saves_no_copy()
    {
        var msel = TestData.Msel();
        var team = TestData.Team(msel.Id);
        var card = TestData.Card(msel.Id);
        card.CardTeams.Add(TestData.CardTeam(card.Id, team.Id));
        card.CardTeams.Add(TestData.CardTeam(card.Id, team.Id));
        msel.Teams.Add(team);
        msel.Cards.Add(card);

        var response = await Import(msel);

        await CopyError(response);
        Assert.Equal(0, await ReadBack(db => db.Msels.CountAsync(Ct)));
        Assert.Equal(0, await ReadBack(db => db.CardTeams.CountAsync(Ct)));
    }

    [Fact]
    public async Task Import_answers_duplicate_Msel_competencies_with_a_server_error_and_saves_no_copy()
    {
        var framework = TestData.CompetencyFramework();
        var competency = TestData.Competency(framework.Id);
        await Seed(framework, competency);
        var msel = TestData.Msel();
        msel.MselCompetencies.Add(TestData.MselCompetency(msel.Id, competency.Id));
        msel.MselCompetencies.Add(TestData.MselCompetency(msel.Id, competency.Id));

        var response = await Import(msel);

        await CopyError(response);
        Assert.Equal(0, await ReadBack(db => db.Msels.CountAsync(Ct)));
        Assert.Equal(0, await ReadBack(db => db.MselCompetencies.CountAsync(Ct)));
    }

    [Fact]
    public async Task Import_answers_duplicate_application_team_assignments_with_a_server_error_and_saves_no_copy()
    {
        var msel = TestData.Msel();
        var team = TestData.Team(msel.Id);
        var application = TestData.PlayerApplication(msel.Id);
        application.PlayerApplicationTeams.Add(TestData.PlayerApplicationTeam(application.Id, team.Id));
        application.PlayerApplicationTeams.Add(TestData.PlayerApplicationTeam(application.Id, team.Id));
        msel.Teams.Add(team);
        msel.PlayerApplications.Add(application);

        var response = await Import(msel);

        await CopyError(response);
        Assert.Equal(0, await ReadBack(db => db.Msels.CountAsync(Ct)));
        Assert.Equal(0, await ReadBack(db => db.PlayerApplicationTeams.CountAsync(Ct)));
    }

    [Fact]
    public async Task Launch_answers_a_submitter_role_without_membership_with_a_server_error_and_saves_no_copy()
    {
        var template = TestData.Msel(isTemplate: true);
        var team = TestData.Team(template.Id);
        await Seed(template, team, TestData.Invitation(template.Id, team.Id));
        var actor = await Actor().SeedAsync();
        await Seed(TestData.UserTeamRole(actor.Id, team.Id));

        var response = await ClientWithEmail(actor, "participant@example.test").PostAsync($"api/msels/{template.Id}/launch", null, Ct);

        await CopyError(response);
        Assert.Equal(1, await ReadBack(db => db.Msels.CountAsync(Ct)));
        Assert.Equal(1, await ReadBack(db => db.UserTeamRoles.CountAsync(Ct)));
        Assert.Equal(0, await ReadBack(db => db.TeamUsers.CountAsync(Ct)));
    }

    private async Task<HttpResponseMessage> Import(MselEntity msel)
    {
        var json = JsonSerializer.SerializeToNode(msel, new JsonSerializerOptions { ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles });
        // The export's collections have no references back to their parent; preserve only their data.
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json"), "ToUpload", "copy.json");
        return await RootClient.PostAsync("api/msels/json", form, Ct);
    }

    private async Task CopyError(HttpResponseMessage response)
    {
        var error = await AssertJsonError<ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("An error occurred while saving the entity changes. See the inner exception for details.", error.Title);
        Assert.Contains("MselService.privateMselCopyAsync", error.Detail);
    }
}
