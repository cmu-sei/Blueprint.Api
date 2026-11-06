// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Tests.Support;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// <c>MselRunService</c> / <c>MselRunController</c> - the two reads over a MSEL's runs. Both resolve
/// <c>ViewMsels</c> and fall back to the roles that can open the Assessor View: <c>Owner</c> (and the
/// MSEL's creator), <c>Editor</c>, <c>Approver</c> and <c>Evaluator</c>.
/// </summary>
/// <remarks>
/// 401 and the system-permission 403 live in <see cref="RouteAuthorizationTests"/>; this file covers the MSEL
/// role tier, the happy path and the not-found path.
/// </remarks>
public class MselRunEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET msels/{mselId}/runs
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByMsel_ReturnsOnlyThatMselsRunsNewestFirst()
    {
        var msel = await SeedMsel();
        var other = await SeedMsel();
        // saved one at a time, because a save stamps DateCreated, which orders the list
        var older = TestData.MselRun(msel.Id, MselRunStatus.Removed);
        await Seed(older);
        var newer = TestData.MselRun(msel.Id, MselRunStatus.Active);
        await Seed(newer, TestData.MselRun(other.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"api/msels/{msel.Id}/runs", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<List<ViewModels.MselRun>>(JsonOptions, Ct);
        Assert.Equal([newer.Id, older.Id], list.Select(x => x.Id).ToList());
    }

    [Fact]
    public async Task GetByMsel_ForAMselNeverPushed_IsAnEmptyList()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).GetAsync($"api/msels/{msel.Id}/runs", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadFromJsonAsync<List<ViewModels.MselRun>>(JsonOptions, Ct));
    }

    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"api/msels/{Guid.NewGuid()}/runs", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(MselRole.Owner)]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.Evaluator)]
    public async Task GetByMsel_ByAnAssessorRole_Is200(MselRole role)
    {
        var msel = await SeedMsel();
        await Seed(TestData.MselRun(msel.Id));
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Client(actor).GetAsync($"api/msels/{msel.Id}/runs", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetByMsel_ByTheMselsCreator_Is200()
    {
        var actor = await Actor().SeedAsync();
        var msel = TestData.Msel(createdBy: actor.Id);
        await Seed(msel);

        var response = await Client(actor).GetAsync($"api/msels/{msel.Id}/runs", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(MselRole.Viewer)]
    [InlineData(MselRole.MoveEditor)]
    public async Task GetByMsel_ByARoleOutsideTheAssessorView_Is403(MselRole role)
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Client(actor).GetAsync($"api/msels/{msel.Id}/runs", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(MselRole.Owner)]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.Evaluator)]
    public async Task GetByMsel_ByAnAssessorRoleOnAnotherMsel_Is403(MselRole role)
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnNewMsel(role).SeedAsync();

        var response = await Client(actor).GetAsync($"api/msels/{msel.Id}/runs", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetByMsel_ByAParticipantOnTheMselsTeam_Is403()
    {
        var msel = await SeedMsel();
        var team = TestData.Team(msel.Id);
        await Seed(team);
        var actor = await Actor().OnTeam(team).SeedAsync();

        var response = await Client(actor).GetAsync($"api/msels/{msel.Id}/runs", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetByMsel_ByTheCreatorOfAnotherMsel_Is403()
    {
        var actor = await Actor().SeedAsync();
        await Seed(TestData.Msel(createdBy: actor.Id));
        var msel = await SeedMsel();

        var response = await Client(actor).GetAsync($"api/msels/{msel.Id}/runs", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // GET runs/{id}
    // ---------------------------------------------------------------------------------------------

    /// <summary>The run's ids and times, and none of the large stored documents.</summary>
    [Fact]
    public async Task Get_ReturnsTheRunWithoutItsStoredDocuments()
    {
        var msel = await SeedMsel();
        var run = TestData.MselRun(msel.Id, MselRunStatus.Removed);
        run.Name = "Cohort A";
        run.CasterWorkspaceId = Guid.NewGuid();
        run.CasterRunId = Guid.NewGuid();
        run.CiteScoringModelId = Guid.NewGuid();
        run.MselSnapshotJson = """{"snapshot":true}""";
        run.RecordJson = """{"statements":[]}""";
        run.CiteSubmissionsJson = "[]";
        run.Statistics = """{"cite":3}""";
        await Seed(run);
        var actor = await Actor().OnMsel(msel, MselRole.Evaluator).SeedAsync();

        var response = await Client(actor).GetAsync($"api/runs/{run.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        var read = JsonSerializer.Deserialize<ViewModels.MselRun>(body, JsonOptions);
        Assert.Equal(run.Id, read.Id);
        Assert.Equal(msel.Id, read.MselId);
        Assert.Equal("Cohort A", read.Name);
        Assert.Equal(MselRunStatus.Removed, read.Status);
        Assert.Equal(run.PlayerViewId, read.PlayerViewId);
        Assert.Equal(run.CiteEvaluationId, read.CiteEvaluationId);
        Assert.Equal(run.CiteScoringModelId, read.CiteScoringModelId);
        Assert.Equal(run.GalleryCollectionId, read.GalleryCollectionId);
        Assert.Equal(run.GalleryExhibitId, read.GalleryExhibitId);
        Assert.Equal(run.SteamfitterScenarioId, read.SteamfitterScenarioId);
        Assert.Equal(run.CasterWorkspaceId, read.CasterWorkspaceId);
        Assert.Equal(run.CasterRunId, read.CasterRunId);
        Assert.NotNull(read.ActualStartTime);
        Assert.NotNull(read.ActualEndTime);
        Assert.Equal("""{"cite":3}""", read.Statistics);
        Assert.Contains("\"status\":\"Removed\"", body);
        Assert.DoesNotContain("snapshot", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recordJson", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("citeSubmissions", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_ForARunThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"api/runs/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(MselRole.Owner)]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.Evaluator)]
    public async Task Get_ByAnAssessorRole_Is200(MselRole role)
    {
        var (msel, run) = await SeedRun();
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Client(actor).GetAsync($"api/runs/{run.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_ByTheMselsCreator_Is200()
    {
        var actor = await Actor().SeedAsync();
        var msel = TestData.Msel(createdBy: actor.Id);
        var run = TestData.MselRun(msel.Id);
        await Seed(msel);
        await Seed(run);

        var response = await Client(actor).GetAsync($"api/runs/{run.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(MselRole.Viewer)]
    [InlineData(MselRole.MoveEditor)]
    public async Task Get_ByARoleOutsideTheAssessorView_Is403(MselRole role)
    {
        var (msel, run) = await SeedRun();
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Client(actor).GetAsync($"api/runs/{run.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(MselRole.Owner)]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.Evaluator)]
    public async Task Get_ByAnAssessorRoleOnAnotherMsel_Is403(MselRole role)
    {
        var (_, run) = await SeedRun();
        var actor = await Actor().OnNewMsel(role).SeedAsync();

        var response = await Client(actor).GetAsync($"api/runs/{run.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_ByAParticipantOnTheMselsTeam_Is403()
    {
        var (msel, run) = await SeedRun();
        var team = TestData.Team(msel.Id);
        await Seed(team);
        var actor = await Actor().OnTeam(team).SeedAsync();

        var response = await Client(actor).GetAsync($"api/runs/{run.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_ByTheCreatorOfAnotherMsel_Is403()
    {
        var actor = await Actor().SeedAsync();
        await Seed(TestData.Msel(createdBy: actor.Id));
        var (_, run) = await SeedRun();

        var response = await Client(actor).GetAsync($"api/runs/{run.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private async Task<MselEntity> SeedMsel()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        return msel;
    }

    private async Task<(MselEntity Msel, MselRunEntity Run)> SeedRun()
    {
        var msel = await SeedMsel();
        var run = TestData.MselRun(msel.Id);
        await Seed(run);

        return (msel, run);
    }
}
