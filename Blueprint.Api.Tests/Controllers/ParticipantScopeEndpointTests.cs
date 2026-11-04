// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Tests.Support;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// What a MSEL participant - a member of one of its teams holding no role on the Contributors tab - may read
/// across the routes that are not about events and cells (those are in the scenario event and data value
/// tests). Blueprint shows a participant the Exercise View, so the API keeps back what that view does not
/// show: a collection route answers an empty list, because the build page asks for it whoever opens the
/// MSEL, and a route for one row answers 403. Each case has its near miss: the same team member holding
/// Viewer, who is a contributor and reads it all.
/// </summary>
public class ParticipantScopeEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // Data Fields
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task DataFields_ByMsel_ForAParticipant_AreOnlyThoseOnTheExerciseView()
    {
        var (msel, team) = await SeedMselAndTeam();
        var shown = TestData.DataField(mselId: msel.Id);
        shown.OnExerciseView = true;
        var keptOff = TestData.DataField(mselId: msel.Id, displayOrder: 2);
        await Seed(shown, keptOff);
        var (participant, contributor) = await SeedParticipantAndContributor(msel, team);

        var sent = await Read<List<ViewModels.DataField>>(
            await Client(participant).GetAsync($"/api/msels/{msel.Id}/dataFields", Ct));

        Assert.Equal(shown.Id, Assert.Single(sent).Id);
        Assert.Equal(2, (await Read<List<ViewModels.DataField>>(
            await Client(contributor).GetAsync($"/api/msels/{msel.Id}/dataFields", Ct))).Count);
    }

    [Fact]
    public async Task DataField_ById_KeptOffTheExerciseView_IsForbiddenToAParticipant_AndReadByAContributor()
    {
        var (msel, team) = await SeedMselAndTeam();
        var keptOff = TestData.DataField(mselId: msel.Id);
        await Seed(keptOff);
        var (participant, contributor) = await SeedParticipantAndContributor(msel, team);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client(participant).GetAsync($"/api/dataFields/{keptOff.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await Client(contributor).GetAsync($"/api/dataFields/{keptOff.Id}", Ct)).StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // CITE actions and duties
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CiteActions_ForAParticipant_AreAnEmptyList_AndOneByIdIs403()
    {
        var (msel, team) = await SeedMselAndTeam();
        var action = TestData.CiteAction(msel.Id, team.Id);
        await Seed(action);
        var (participant, contributor) = await SeedParticipantAndContributor(msel, team);

        Assert.Empty(await Read<List<ViewModels.CiteAction>>(
            await Client(participant).GetAsync($"/api/msels/{msel.Id}/citeActions", Ct)));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client(participant).GetAsync($"/api/citeActions/{action.Id}", Ct)).StatusCode);

        Assert.Single(await Read<List<ViewModels.CiteAction>>(
            await Client(contributor).GetAsync($"/api/msels/{msel.Id}/citeActions", Ct)));
        Assert.Equal(HttpStatusCode.OK,
            (await Client(contributor).GetAsync($"/api/citeActions/{action.Id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task CiteDuties_ForAParticipant_AreAnEmptyList_AndOneByIdIs403()
    {
        var (msel, team) = await SeedMselAndTeam();
        var duty = TestData.CiteDuty(msel.Id, team.Id);
        await Seed(duty);
        var (participant, contributor) = await SeedParticipantAndContributor(msel, team);

        Assert.Empty(await Read<List<ViewModels.CiteDuty>>(
            await Client(participant).GetAsync($"/api/msels/{msel.Id}/citeDuties", Ct)));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client(participant).GetAsync($"/api/citeDuties/{duty.Id}", Ct)).StatusCode);

        Assert.Single(await Read<List<ViewModels.CiteDuty>>(
            await Client(contributor).GetAsync($"/api/msels/{msel.Id}/citeDuties", Ct)));
        Assert.Equal(HttpStatusCode.OK,
            (await Client(contributor).GetAsync($"/api/citeDuties/{duty.Id}", Ct)).StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Card teams
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CardTeams_ForAParticipant_AreAnEmptyList_ByMselAndByCard_AndOneByIdIs403()
    {
        var (msel, team) = await SeedMselAndTeam();
        var card = TestData.Card(msel.Id);
        await Seed(card);
        var cardTeam = TestData.CardTeam(card.Id, team.Id);
        await Seed(cardTeam);
        var (participant, contributor) = await SeedParticipantAndContributor(msel, team);

        Assert.Empty(await Read<List<ViewModels.CardTeam>>(
            await Client(participant).GetAsync($"/api/msels/{msel.Id}/teamcards", Ct)));
        Assert.Empty(await Read<List<ViewModels.CardTeam>>(
            await Client(participant).GetAsync($"/api/cards/{card.Id}/teamcards", Ct)));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client(participant).GetAsync($"/api/teamcards/{cardTeam.Id}", Ct)).StatusCode);

        Assert.Single(await Read<List<ViewModels.CardTeam>>(
            await Client(contributor).GetAsync($"/api/msels/{msel.Id}/teamcards", Ct)));
        Assert.Single(await Read<List<ViewModels.CardTeam>>(
            await Client(contributor).GetAsync($"/api/cards/{card.Id}/teamcards", Ct)));
        Assert.Equal(HttpStatusCode.OK,
            (await Client(contributor).GetAsync($"/api/teamcards/{cardTeam.Id}", Ct)).StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // The whole MSEL: the xlsx export and the copy
    // ---------------------------------------------------------------------------------------------

    /// <summary>The export holds every event and cell, hidden or not, so a participant may not download it.</summary>
    [Fact]
    public async Task Xlsx_IsForbiddenToAParticipant_AndDownloadedByAContributor()
    {
        var (msel, team) = await SeedMselAndTeam();
        var (participant, contributor) = await SeedParticipantAndContributor(msel, team);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client(participant).GetAsync($"/api/msels/{msel.Id}/xlsx", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await Client(contributor).GetAsync($"/api/msels/{msel.Id}/xlsx", Ct)).StatusCode);
    }

    /// <summary>
    /// A copy hands over the whole MSEL, so a participant holding CreateMsels, which the copy route asks
    /// for, still may not copy it.
    /// </summary>
    [Fact]
    public async Task Copy_IsForbiddenToAParticipantHoldingCreateMsels()
    {
        var (msel, team) = await SeedMselAndTeam();
        var participant = await Actor().OnTeam(team).WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(participant).PostAsync($"/api/msels/{msel.Id}/copy", content: null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<(MselEntity Msel, TeamEntity Team)> SeedMselAndTeam()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var team = TestData.Team(msel.Id);
        await Seed(team);

        return (msel, team);
    }

    /// <summary>Two members of <paramref name="team"/>: one holding no role, and one holding Viewer.</summary>
    private async Task<(TestActor Participant, TestActor Contributor)> SeedParticipantAndContributor(
        MselEntity msel, TeamEntity team)
    {
        var participant = await Actor().OnTeam(team).SeedAsync();
        var contributor = await Actor().OnTeam(team).OnMsel(msel, MselRole.Viewer).SeedAsync();

        return (participant, contributor);
    }

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        return System.Text.Json.JsonSerializer.Deserialize<T>(body, JsonOptions);
    }
}
