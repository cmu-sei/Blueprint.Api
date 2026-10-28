// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// The integration IDs, the integration status and the pipeline statuses belong to the push and pull
/// pipeline. <c>POST msels</c> and <c>PUT msels/{id}</c> cannot set them.
/// </summary>
/// <remarks>
/// The pull and cancel jobs delete the Player view, Gallery collection, CITE evaluation and Steamfitter
/// scenario a MSEL names, using Blueprint's service account. A MSEL that could be pointed at another
/// exercise's objects would let its owner delete them, so the pull tests here run the real worker and
/// assert that nothing reached the sibling APIs. A 200 alone would prove nothing, because the worker
/// swallows remote failures.
/// </remarks>
public class MselIntegrationOwnershipTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    private static readonly Guid ForeignViewId = Guid.NewGuid();
    private static readonly Guid ForeignCollectionId = Guid.NewGuid();
    private static readonly Guid ForeignExhibitId = Guid.NewGuid();
    private static readonly Guid ForeignEvaluationId = Guid.NewGuid();
    private static readonly Guid ForeignScenarioId = Guid.NewGuid();

    [Fact]
    public async Task Create_WithIntegrationIdsInTheBody_StoresNone()
    {
        var creator = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var id = Guid.NewGuid();

        var response = await Client(creator).PostAsJsonAsync("/api/msels", ForeignBody(id, MselItemStatus.Pending), JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        AssertNoIntegrations(await Stored(id));
    }

    [Fact]
    public async Task Create_WithIntegrationIdsInTheBody_ThenPull_ReachesNoSiblingApi()
    {
        var creator = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var id = Guid.NewGuid();
        await Client(creator).PostAsJsonAsync("/api/msels", ForeignBody(id, MselItemStatus.Pending), JsonOptions, Ct);
        var harness = Harness();

        await harness.PullAsync(id);
        await harness.WaitForFinalStatus(id, MselItemStatus.Approved, Ct);

        Assert.Empty(SiblingPaths(harness));
    }

    [Theory]
    [InlineData(MselItemStatus.Pushing)]
    [InlineData(MselItemStatus.Pulling)]
    [InlineData(MselItemStatus.Deployed)]
    public async Task Create_WithAPipelineStatus_Is409AndCreatesNothing(MselItemStatus status)
    {
        var creator = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var id = Guid.NewGuid();

        var response = await Client(creator).PostAsJsonAsync("/api/msels", ForeignBody(id, status), JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(await ReadBack(db => db.Msels.AnyAsync(x => x.Id == id, Ct)));
    }

    [Fact]
    public async Task Update_AsAnOwner_WithIntegrationIdsInTheBody_LeavesTheStoredIdsAlone()
    {
        var msel = TestData.Msel(status: MselItemStatus.Approved);
        await Seed(msel);
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(owner).PutAsJsonAsync(
            $"/api/msels/{msel.Id}", ForeignBody(msel.Id, MselItemStatus.Approved, msel.Name), JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoIntegrations(await Stored(msel.Id));
    }

    [Fact]
    public async Task Update_AsAnOwner_WithIntegrationIdsInTheBody_ThenPull_ReachesNoSiblingApi()
    {
        var msel = TestData.Msel(status: MselItemStatus.Approved);
        await Seed(msel);
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        await Client(owner).PutAsJsonAsync(
            $"/api/msels/{msel.Id}", ForeignBody(msel.Id, MselItemStatus.Approved, msel.Name), JsonOptions, Ct);
        var harness = Harness();

        await harness.PullAsync(msel.Id);
        await harness.WaitForFinalStatus(msel.Id, MselItemStatus.Approved, Ct);

        Assert.Empty(SiblingPaths(harness));
    }

    /// <summary>
    /// A deployed MSEL keeps the IDs its own push wrote, whatever the body says.
    /// </summary>
    [Fact]
    public async Task Update_OfADeployedMsel_WithOtherIntegrationIdsInTheBody_KeepsItsOwn()
    {
        var msel = TestData.Msel(status: MselItemStatus.Deployed);
        var ownViewId = Guid.NewGuid();
        var ownEvaluationId = Guid.NewGuid();
        msel.PlayerViewId = ownViewId;
        msel.CiteEvaluationId = ownEvaluationId;
        msel.IntegrationStatus = null;
        // the body's integration settings, which cannot change while deployed
        msel.UsePlayer = msel.UseGallery = msel.UseCite = msel.UseSteamfitter = true;
        await Seed(msel);
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(owner).PutAsJsonAsync(
            $"/api/msels/{msel.Id}", ForeignBody(msel.Id, MselItemStatus.Deployed, "Renamed"), JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await Stored(msel.Id);
        Assert.Equal("Renamed", stored.Name);
        Assert.Equal(ownViewId, stored.PlayerViewId);
        Assert.Equal(ownEvaluationId, stored.CiteEvaluationId);
        Assert.Null(stored.GalleryCollectionId);
        Assert.Null(stored.SteamfitterScenarioId);
        Assert.Null(stored.IntegrationStatus);
    }

    [Theory]
    [InlineData(MselItemStatus.Approved, MselItemStatus.Deployed)]
    [InlineData(MselItemStatus.Pending, MselItemStatus.Pushing)]
    [InlineData(MselItemStatus.Complete, MselItemStatus.Pulling)]
    [InlineData(MselItemStatus.Deployed, MselItemStatus.Approved)]
    [InlineData(MselItemStatus.Deployed, MselItemStatus.Archived)]
    [InlineData(MselItemStatus.Pushing, MselItemStatus.Pending)]
    [InlineData(MselItemStatus.Pulling, MselItemStatus.Deployed)]
    public async Task Update_IntoOrOutOfAPipelineStatus_Is409AndLeavesTheMselAlone(
        MselItemStatus stored, MselItemStatus requested)
    {
        var msel = TestData.Msel(status: stored);
        await Seed(msel);
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(owner).PutAsJsonAsync(
            $"/api/msels/{msel.Id}", new { name = "Renamed", status = requested }, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var row = await Stored(msel.Id);
        Assert.Equal(stored, row.Status);
        Assert.Equal(msel.Name, row.Name);
    }

    [Theory]
    [InlineData(MselItemStatus.Pending, MselItemStatus.Entered)]
    [InlineData(MselItemStatus.Entered, MselItemStatus.Approved)]
    [InlineData(MselItemStatus.Approved, MselItemStatus.Complete)]
    [InlineData(MselItemStatus.Complete, MselItemStatus.Archived)]
    [InlineData(MselItemStatus.Archived, MselItemStatus.Pending)]
    public async Task Update_BetweenWorkflowStatuses_Is200(MselItemStatus stored, MselItemStatus requested)
    {
        var msel = TestData.Msel(status: stored);
        await Seed(msel);
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(owner).PutAsJsonAsync(
            $"/api/msels/{msel.Id}", new { name = msel.Name, status = requested }, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(requested, (await Stored(msel.Id)).Status);
    }

    /// <summary>
    /// The UI sends the stored status back on every save, so a deployed MSEL can still be renamed.
    /// </summary>
    [Theory]
    [InlineData(MselItemStatus.Pushing)]
    [InlineData(MselItemStatus.Pulling)]
    [InlineData(MselItemStatus.Deployed)]
    public async Task Update_KeepingAPipelineStatus_Is200(MselItemStatus status)
    {
        var msel = TestData.Msel(status: status);
        await Seed(msel);
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(owner).PutAsJsonAsync(
            $"/api/msels/{msel.Id}", new { name = "Renamed", status }, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = await Stored(msel.Id);
        Assert.Equal("Renamed", row.Name);
        Assert.Equal(status, row.Status);
    }

    /// <summary>
    /// What a push built follows the settings it read, and only a pull and a new push rebuild it. So the
    /// settings stay as they are while anything the push made is still there: a failed push leaves the
    /// MSEL Pushing, and a failed pull leaves the ids it could not remove.
    /// </summary>
    [Theory]
    [InlineData(MselItemStatus.Pushing, false)]
    [InlineData(MselItemStatus.Pulling, false)]
    [InlineData(MselItemStatus.Deployed, false)]
    [InlineData(MselItemStatus.Approved, true)]
    public async Task Update_ChangingTheCasterDirectory_WhileIntegrated_Is409AndLeavesTheMselAlone(
        MselItemStatus status, bool leftoverWorkspace)
    {
        var msel = IntegratedCasterMsel(status, leftoverWorkspace);
        await Seed(msel);
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(owner).PutAsJsonAsync(
            $"/api/msels/{msel.Id}", CasterBody(msel, "Renamed", casterDirectoryId: Guid.NewGuid()), JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var row = await Stored(msel.Id);
        Assert.Equal(msel.CasterDirectoryId, row.CasterDirectoryId);
        Assert.Equal(msel.Name, row.Name);
    }

    [Fact]
    public async Task Update_TurningOffAnIntegration_WhilePushing_Is409()
    {
        var msel = IntegratedCasterMsel(MselItemStatus.Pushing, leftoverWorkspace: false);
        await Seed(msel);
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(owner).PutAsJsonAsync(
            $"/api/msels/{msel.Id}", CasterBody(msel, msel.Name, useCaster: false), JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True((await Stored(msel.Id)).UseCaster);
    }

    /// <summary>The UI sends the stored settings back on every save, so an integrated MSEL can still be renamed.</summary>
    [Fact]
    public async Task Update_KeepingTheIntegrationSettings_WhilePushing_Is200()
    {
        var msel = IntegratedCasterMsel(MselItemStatus.Pushing, leftoverWorkspace: false);
        await Seed(msel);
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(owner).PutAsJsonAsync(
            $"/api/msels/{msel.Id}", CasterBody(msel, "Renamed"), JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Renamed", (await Stored(msel.Id)).Name);
    }

    [Fact]
    public async Task Update_ChangingTheIntegrationSettings_OfAMselWithNoIntegrations_Is200()
    {
        var msel = TestData.Msel(status: MselItemStatus.Approved);
        await Seed(msel);
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(owner).PutAsJsonAsync(
            $"/api/msels/{msel.Id}", new { name = msel.Name, status = msel.Status, useGallery = true }, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await Stored(msel.Id)).UseGallery);
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A Player and Caster MSEL as a push leaves it: with its workspace, or for <paramref name="leftoverWorkspace"/>
    /// back to Approved with the workspace a failed pull could not remove.
    /// </summary>
    private static MselEntity IntegratedCasterMsel(MselItemStatus status, bool leftoverWorkspace)
    {
        var msel = TestData.Msel(status: status);
        msel.UsePlayer = true;
        msel.UseCaster = true;
        msel.CasterDirectoryId = Guid.NewGuid();
        msel.CasterWorkspaceId = Guid.NewGuid();
        msel.PlayerViewId = leftoverWorkspace ? null : Guid.NewGuid();
        return msel;
    }

    private static object CasterBody(MselEntity msel, string name, bool useCaster = true, Guid? casterDirectoryId = null) => new
    {
        name,
        status = msel.Status,
        usePlayer = msel.UsePlayer,
        useCaster,
        casterDirectoryId = casterDirectoryId ?? msel.CasterDirectoryId
    };

    private static object ForeignBody(Guid id, MselItemStatus status, string name = "Bound") => new
    {
        id,
        name,
        status,
        integrationStatus = "Pushing Integrations",
        usePlayer = true,
        playerViewId = ForeignViewId,
        useGallery = true,
        galleryCollectionId = ForeignCollectionId,
        galleryExhibitId = ForeignExhibitId,
        useCite = true,
        citeEvaluationId = ForeignEvaluationId,
        useSteamfitter = true,
        steamfitterScenarioId = ForeignScenarioId
    };

    private async Task<MselEntity> Stored(Guid id) =>
        await ReadBack(db => db.Msels.AsNoTracking().SingleAsync(x => x.Id == id, Ct));

    private static void AssertNoIntegrations(MselEntity msel)
    {
        Assert.Null(msel.PlayerViewId);
        Assert.Null(msel.GalleryCollectionId);
        Assert.Null(msel.GalleryExhibitId);
        Assert.Null(msel.CiteEvaluationId);
        Assert.Null(msel.SteamfitterScenarioId);
        Assert.Null(msel.IntegrationStatus);
    }

    private static string[] SiblingPaths(IntegrationServiceHarness harness) =>
        harness.Handler.Paths.Where(x => !x.StartsWith("realms/")).ToArray();

    private IntegrationServiceHarness Harness() =>
        new(Session, new SiblingApiHandler()
            .AnswersJson(IntegrationServiceHarness.DiscoveryPath, """
                {
                  "issuer": "http://localhost:8080/realms/crucible",
                  "authorization_endpoint": "http://localhost:8080/realms/crucible/protocol/openid-connect/auth",
                  "token_endpoint": "http://localhost:8080/realms/crucible/protocol/openid-connect/token",
                  "jwks_uri": "http://localhost:8080/realms/crucible/protocol/openid-connect/certs",
                  "response_types_supported": ["code"],
                  "subject_types_supported": ["public"],
                  "id_token_signing_alg_values_supported": ["RS256"]
                }
                """)
            .AnswersJson(IntegrationServiceHarness.JwksPath, """{"keys":[]}""")
            .AnswersJson(IntegrationServiceHarness.TokenPath,
                """{"access_token":"abc123","token_type":"Bearer","expires_in":300}""")
            .Answers("steamfitter/api/scenarios/*", HttpStatusCode.NoContent)
            .Answers("cite/api/evaluations/*", HttpStatusCode.NoContent)
            .Answers("gallery/api/collections/*", HttpStatusCode.NoContent)
            .Answers("player/api/views/*", HttpStatusCode.NoContent));
}
