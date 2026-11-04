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
