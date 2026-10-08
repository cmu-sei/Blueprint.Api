// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Cite.Api.Client;
using Gallery.Api.Client;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

// Both generated clients declare a SystemPermission of their own, so the name is ambiguous in any file
// that reaches for two of the four APIs at once - as this one does.
using SystemPermission = Blueprint.Api.Data.Enumerations.SystemPermission;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// <c>GET api/msels/{id}/integrations/names</c>, and through it <c>IntegrationNameService</c> - the one
/// place blueprint reads from all four sibling APIs in a single request.
/// </summary>
/// <remarks>
/// <para>
/// This is the first test class to drive the four substituted API clients, which is the whole point of
/// covering this endpoint early: everything else that talks to Player, Gallery, CITE or Steamfitter does it
/// from a background worker, where a test can only see what went on a queue. Here the calls happen on the
/// request thread and their results come back in the response.
/// </para>
/// <para>
/// The service's contract is that a name is best-effort. A MSEL with no association of a kind, an
/// application that cannot be reached, and an association whose target has been deleted all produce an
/// empty string rather than an error, because one unreachable application must not cost the caller the
/// other five names. What is <em>not</em> best-effort is the authorization: the names are read through
/// <c>MselService.GetAsync</c> precisely so that a caller who cannot view the MSEL cannot use this
/// endpoint to learn the name of something in another application.
/// </para>
/// </remarks>
public class IntegrationNamesEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    [Fact]
    public async Task Names_ReadsEachNameFromTheApplicationThatOwnsIt()
    {
        var msel = WithEveryIntegration();
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Factory.PlayerApi.GetViewAsync((Guid)msel.PlayerViewId, Arg.Any<CancellationToken>())
            .Returns(new Player.Api.Client.View { Name = "the view" });
        Factory.Gallery.GetCollectionAsync((Guid)msel.GalleryCollectionId, Arg.Any<CancellationToken>())
            .Returns(new Collection { Name = "the collection" });
        Factory.Gallery.GetExhibitAsync((Guid)msel.GalleryExhibitId, Arg.Any<CancellationToken>())
            .Returns(new Exhibit { Name = "the exhibit" });
        Factory.Cite.GetEvaluationAsync((Guid)msel.CiteEvaluationId, Arg.Any<CancellationToken>())
            .Returns(new Evaluation { Description = "the evaluation" });
        Factory.Cite.GetScoringModelAsync((Guid)msel.CiteScoringModelId, Arg.Any<CancellationToken>())
            .Returns(new ScoringModel { Description = "the scoring model" });
        Factory.Steamfitter
            .GetScenarioAsync((Guid)msel.SteamfitterScenarioId, Arg.Any<CancellationToken>())
            .Returns(new Steamfitter.Api.Client.Scenario { Name = "the scenario" });

        var names = await Names(Client(actor), msel.Id);

        Assert.Equal("the view", names.PlayerViewName);
        Assert.Equal("the collection", names.GalleryCollectionName);
        Assert.Equal("the exhibit", names.GalleryExhibitName);
        // CITE names an evaluation and a scoring model by their description, not by a Name property.
        Assert.Equal("the evaluation", names.CiteEvaluationName);
        Assert.Equal("the scoring model", names.CiteScoringModelName);
        Assert.Equal("the scenario", names.SteamfitterScenarioName);
    }

    [Fact]
    public async Task Names_ForAMselWithNoIntegrations_AreEmptyAndNothingIsAsked()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var names = await Names(Client(actor), msel.Id);

        Assert.Equal("", names.PlayerViewName);
        Assert.Equal("", names.GalleryCollectionName);
        Assert.Equal("", names.GalleryExhibitName);
        Assert.Equal("", names.CiteEvaluationName);
        Assert.Equal("", names.CiteScoringModelName);
        Assert.Equal("", names.SteamfitterScenarioName);

        Assert.Empty(Factory.PlayerApi.ReceivedCalls());
        Assert.Empty(Factory.Gallery.ReceivedCalls());
        Assert.Empty(Factory.Cite.ReceivedCalls());
        Assert.Empty(Factory.Steamfitter.ReceivedCalls());
    }

    /// <summary>
    /// Each kind is asked about on its own. A MSEL with a Gallery collection and no exhibit costs one call,
    /// not two.
    /// </summary>
    [Fact]
    public async Task Names_AsksOnlyAboutTheIntegrationsTheMselHas()
    {
        var collectionId = Guid.NewGuid();
        var msel = TestData.Msel();
        msel.GalleryCollectionId = collectionId;
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Factory.Gallery.GetCollectionAsync(collectionId, Arg.Any<CancellationToken>())
            .Returns(new Collection { Name = "the collection" });

        var names = await Names(Client(actor), msel.Id);

        Assert.Equal("the collection", names.GalleryCollectionName);
        Assert.Equal("", names.GalleryExhibitName);
        await Factory.Gallery.Received(1).GetCollectionAsync(collectionId, Arg.Any<CancellationToken>());
        await Factory.Gallery.DidNotReceiveWithAnyArgs()
            .GetExhibitAsync(default, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// One unreachable application costs its own name and nothing else, which is the reason the lookups are
    /// wrapped one at a time rather than in a single try.
    /// </summary>
    [Fact]
    public async Task Names_WhenAnApplicationCannotBeReached_ReturnsTheOtherNames()
    {
        var msel = WithEveryIntegration();
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Factory.PlayerApi.GetViewAsync((Guid)msel.PlayerViewId, Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Connection refused"));
        Factory.Cite.GetEvaluationAsync((Guid)msel.CiteEvaluationId, Arg.Any<CancellationToken>())
            .Returns(new Evaluation { Description = "the evaluation" });

        var names = await Names(Client(actor), msel.Id);

        Assert.Equal("", names.PlayerViewName);
        Assert.Equal("the evaluation", names.CiteEvaluationName);
    }

    /// <summary>
    /// An association whose target has been deleted out from under the MSEL is a display problem rather
    /// than an error: the client answers with no object, and the name is empty.
    /// </summary>
    [Fact]
    public async Task Names_WhenTheTargetIsGone_IsEmptyRatherThanAnError()
    {
        var msel = TestData.Msel();
        msel.SteamfitterScenarioId = Guid.NewGuid();
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Factory.Steamfitter
            .GetScenarioAsync((Guid)msel.SteamfitterScenarioId, Arg.Any<CancellationToken>())
            .Returns((Steamfitter.Api.Client.Scenario)null);

        var names = await Names(Client(actor), msel.Id);

        Assert.Equal("", names.SteamfitterScenarioName);
    }

    /// <summary>A lookup that throws <c>OperationCanceledException</c> is not caught, and fails the request with a 500.</summary>
    [Fact]
    public async Task Names_WhenALookupIsCancelled_FailsTheRequest()
    {
        var msel = TestData.Msel();
        msel.PlayerViewId = Guid.NewGuid();
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Factory.PlayerApi.GetViewAsync((Guid)msel.PlayerViewId, Arg.Any<CancellationToken>())
            .Throws(new OperationCanceledException());

        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/integrations/names", Ct);

        Assert.Equal("The operation was canceled.", (await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
    }

    // ---------------------------------------------------------------------------------------------
    // Who may read them
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Viewing the MSEL is enough, and nothing more is required: the names are only the display form of
    /// ids the caller can already read off the MSEL itself.
    /// </summary>
    [Fact]
    public async Task Names_AsAViewer_Is200()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/integrations/names", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Team membership is a view grant on its own, so a participant on any team on the MSEL may read them.
    /// </summary>
    [Fact]
    public async Task Names_AsATeamMember_Is200()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var team = TestData.Team(msel.Id);
        await Seed(team);
        var actor = await Actor().OnTeam(team).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/integrations/names", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The 403 is the whole reason this lookup lives in the API rather than in the browser: without it,
    /// a caller could use blueprint's forwarded token to name a Player view they have no business seeing.
    /// </summary>
    [Fact]
    public async Task Names_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel_and_asks_nothing()
    {
        var msel = WithEveryIntegration();
        await Seed(msel);
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/integrations/names", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(Factory.PlayerApi.ReceivedCalls());
        Assert.Empty(Factory.Gallery.ReceivedCalls());
        Assert.Empty(Factory.Cite.ReceivedCalls());
        Assert.Empty(Factory.Steamfitter.ReceivedCalls());
    }

    /// <summary>A template MSEL is readable by anybody, so its integration names are too.</summary>
    [Fact]
    public async Task Names_ForATemplate_WithNoRoleOrPermission_Is200()
    {
        var msel = TestData.Msel(isTemplate: true);
        msel.PlayerViewId = Guid.NewGuid();
        await Seed(msel);
        var actor = await Actor().SeedAsync();

        Factory.PlayerApi.GetViewAsync((Guid)msel.PlayerViewId, Arg.Any<CancellationToken>())
            .Returns(new Player.Api.Client.View { Name = "the template's view" });

        var names = await Names(Client(actor), msel.Id);

        Assert.Equal("the template's view", names.PlayerViewName);
    }

    /// <summary>Names for an unknown MSEL with view MSELs permission is answered with a 500.</summary>
    [Fact]
    public async Task Names_ForAnUnknownMsel_WithViewMselsPermission_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor)
            .GetAsync($"/api/msels/{Guid.NewGuid()}/integrations/names", Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Object reference not set to an instance of an object.", failure.Title);
        Assert.Contains("MselService.GetAsync", failure.Detail);
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A MSEL associated with one of everything, so a test can tell which lookup produced which name.
    /// </summary>
    private static MselEntity WithEveryIntegration()
    {
        var msel = TestData.Msel(status: MselItemStatus.Deployed);
        msel.PlayerViewId = Guid.NewGuid();
        msel.GalleryCollectionId = Guid.NewGuid();
        msel.GalleryExhibitId = Guid.NewGuid();
        msel.CiteEvaluationId = Guid.NewGuid();
        msel.CiteScoringModelId = Guid.NewGuid();
        msel.SteamfitterScenarioId = Guid.NewGuid();

        return msel;
    }

    private async Task<MselIntegrationNames> Names(HttpClient client, Guid mselId)
    {
        var response = await client.GetAsync($"/api/msels/{mselId}/integrations/names", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<MselIntegrationNames>(JsonOptions, Ct);
    }
}
