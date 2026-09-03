// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Infrastructure;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Sdk;

namespace Blueprint.Api.Tests;

/// <summary>
/// The four endpoints that start and stop a MSEL's deployment: push, pull, cancel and archive.
/// </summary>
/// <remarks>
/// <para>
/// None of these endpoints does the integration work. Each one writes a status, hands the MSEL to the
/// singleton <see cref="IIntegrationQueue"/> and answers immediately; <c>IntegrationService</c> - a hosted
/// service the harness removes, because it would dial the identity provider and then Player, Gallery, CITE
/// and Steamfitter - drains the queue on a background thread. So what these tests assert is the request
/// path: the authorization, the status transition, and the one item that went on the queue. The queue is
/// left real for exactly this reason, and with no worker running nothing takes items off it but the tests.
/// </para>
/// <para>
/// The queue is a host-wide singleton, so it is shared by every test in this class. It is drained before
/// each test, which keeps a failure local: a test that leaves an item behind would otherwise fail the next
/// one to look.
/// </para>
/// <para>
/// Cancel is the exception to all of the above, and its tests are deliberately thin.
/// <c>IntegrationService.CancelPush</c> finds no in-flight push to cancel - nothing started one, because
/// nothing is draining the queue - so it falls through to <c>PerformCancelCleanupAsync</c>, which is
/// <em>fire and forget</em>: an un-awaited task that outlives the request, broadcasts
/// <c>IntegrationStatusUpdated</c>, calls the identity provider for a token, and swallows every failure.
/// Nothing here asserts on what it did or did not do, and in particular nothing asserts the
/// <em>absence</em> of an <c>IntegrationStatusUpdated</c> broadcast, because one can arrive during a later
/// test in this class. Assertions on <see cref="HubRecorder"/> are all scoped to a method name that
/// cleanup never sends.
/// </para>
/// </remarks>
public class MselServiceIntegrationTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    /// <summary>
    /// The roles that are not ownership. Every endpoint in this class falls back to
    /// <c>MselOwnerRequirement</c>, so all five are refused.
    /// </summary>
    public static TheoryData<MselRole> RolesThatAreNotOwnership =>
    [
        MselRole.Editor,
        MselRole.Approver,
        MselRole.MoveEditor,
        MselRole.Viewer,
        MselRole.Evaluator
    ];

    private IIntegrationQueue Queue => Factory.Services.GetRequiredService<IIntegrationQueue>();

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        // The queue lives on the host and the host serves the whole class.
        while (TryTake(TimeSpan.FromMilliseconds(25), out _))
        {
        }
    }

    // ---------------------------------------------------------------------------------------------
    // POST msels/{id}/integrations - push
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Push_AsAnOwner_MarksThePushAndEnqueuesIt()
    {
        var msel = BlueprintAppFactory.Msel(status: MselItemStatus.Approved);
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var before = DateTime.UtcNow;
        var response = await Client(actor).PostAsync($"/api/msels/{msel.Id}/integrations", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var returned = await Read<Msel>(response);
        Assert.Equal(MselItemStatus.Pushing, returned.Status);
        Assert.Equal("Pushing Integrations", returned.IntegrationStatus);

        var stored = await NewContext().Msels.SingleAsync(x => x.Id == msel.Id, Ct);
        Assert.Equal(MselItemStatus.Pushing, stored.Status);
        Assert.Equal("Pushing Integrations", stored.IntegrationStatus);
        Assert.NotNull(stored.DateModified);
        Assert.InRange(stored.DateModified.Value, before, DateTime.UtcNow);

        var queued = Enqueued(msel.Id);
        Assert.True(queued.IsPush);
        Assert.Equal(MselItemStatus.Deployed, queued.FinalStatus);
        Assert.Null(queued.PlayerViewId);
    }

    [Fact]
    public async Task Push_WithManageMselsPermission_Is200()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Client(actor).PostAsync($"/api/msels/{msel.Id}/integrations", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(msel.Id, Enqueued(msel.Id).MselId);
    }

    /// <summary>
    /// The creator needs no role at all: <c>MselOwnerRequirement</c> short-circuits on
    /// <c>msel.CreatedBy</c>.
    /// </summary>
    [Fact]
    public async Task Push_AsTheCreator_Is200()
    {
        var creator = Guid.NewGuid();
        var msel = BlueprintAppFactory.Msel(createdBy: creator);
        await Seed(msel);
        await Actor().WithId(creator).SeedAsync();

        var response = await Client(creator).PostAsync($"/api/msels/{msel.Id}/integrations", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(msel.Id, Enqueued(msel.Id).MselId);
    }

    /// <summary>
    /// Deploying a MSEL is an owner's job, not an editor's.
    /// </summary>
    /// <remarks>
    /// Worth reading against the endpoint's own documentation, which says "Accessible only to a
    /// ContentDeveloper or MSEL owner" - true, and narrower than a reader of the role list would guess.
    /// <see cref="MselRole.Approver"/> and <see cref="MselRole.MoveEditor"/> are refused too, so nobody
    /// short of an owner can deploy the MSEL they have spent the exercise building.
    /// </remarks>
    [Theory]
    [MemberData(nameof(RolesThatAreNotOwnership))]
    public async Task Push_AsAnythingButAnOwner_Is403(MselRole role)
    {
        var msel = BlueprintAppFactory.Msel(status: MselItemStatus.Approved);
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Client(actor).PostAsync($"/api/msels/{msel.Id}/integrations", null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            MselItemStatus.Approved,
            (await NewContext().Msels.SingleAsync(x => x.Id == msel.Id, Ct)).Status);
        AssertNothingEnqueued();
    }

    [Fact]
    public async Task Push_ForAnUnknownMsel_WithManageMselsPermission_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();
        var unknown = Guid.NewGuid();

        var response = await Client(actor).PostAsync($"/api/msels/{unknown}/integrations", null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            $"MSEL {unknown} was not found when attempting to push integrations.",
            (await Error(response)).Title);
    }

    /// <summary>
    /// Characterizes a defect. An unknown MSEL is a 500 rather than a 404 for a caller without the
    /// system permission, because <c>MselOwnerRequirement.IsMet</c> dereferences the MSEL it just failed
    /// to find. Fixing that null guard turns this red, and the endpoint answers 403 - which is the right
    /// answer, since a caller with no permission should not learn whether the MSEL exists.
    /// </summary>
    [Fact]
    public async Task Push_ForAnUnknownMsel_WithNoPermission_Is500()
    {
        var actor = await Actor().SeedAsync();

        var response = await Client(actor)
            .PostAsync($"/api/msels/{Guid.NewGuid()}/integrations", null, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// Characterizes a defect. Pushing a MSEL that is already deployed is a client mistake - the UI
    /// offers the button - but it is reported as a 500 with a stack trace, because
    /// <c>InvalidOperationException</c> is not an <c>IApiException</c> and <c>JsonExceptionFilter</c>
    /// maps everything else to 500. Fixing it, by throwing a 409 or 400 exception instead, turns this
    /// red.
    /// </summary>
    [Fact]
    public async Task Push_ForAnAlreadyDeployedMsel_Is500()
    {
        var msel = BlueprintAppFactory.Msel(status: MselItemStatus.Deployed);
        msel.PlayerViewId = Guid.NewGuid();
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Client(actor).PostAsync($"/api/msels/{msel.Id}/integrations", null, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal($"MSEL {msel.Id} is already deployed.", (await Error(response)).Title);
        Assert.Equal(
            MselItemStatus.Deployed,
            (await NewContext().Msels.SingleAsync(x => x.Id == msel.Id, Ct)).Status);
        AssertNothingEnqueued();
    }

    /// <summary>
    /// A user on two CITE teams stops the push, and the message names the user and both teams.
    /// </summary>
    /// <remarks>
    /// This is the one piece of validation the push does, and it exists because CITE gives a user one
    /// team per evaluation. The status is a 500 for the same reason as the test above - an
    /// <c>InvalidOperationException</c> - so what a UI shows the user is a stack trace next to a message
    /// that was written to be read. Both halves are asserted here: fixing the status code turns this
    /// red, and the message is worth keeping either way.
    /// </remarks>
    [Fact]
    public async Task Push_WhenAUserIsOnTwoCiteTeams_Is500AndNamesThem()
    {
        var msel = BlueprintAppFactory.Msel(status: MselItemStatus.Approved);
        await Seed(msel);

        var first = CiteTeam(msel.Id, "alpha");
        var second = CiteTeam(msel.Id, "bravo");
        await Seed(first, second);

        var doubleBooked = await Actor()
            .WithName("Twice Booked")
            .OnTeam(first)
            .OnTeam(second)
            .SeedAsync();

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Client(actor).PostAsync($"/api/msels/{msel.Id}/integrations", null, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var title = (await Error(response)).Title;
        Assert.StartsWith("Users can only be on one team.", title);
        Assert.Contains(doubleBooked.Name, title);
        Assert.Contains("alpha", title);
        Assert.Contains("bravo", title);

        Assert.Equal(
            MselItemStatus.Approved,
            (await NewContext().Msels.SingleAsync(x => x.Id == msel.Id, Ct)).Status);
        AssertNothingEnqueued();
    }

    /// <summary>
    /// The duplicate check looks only at teams that are mapped to a CITE team type, so a user on two
    /// teams of any other kind does not stop the push.
    /// </summary>
    /// <remarks>
    /// Deliberate rather than a defect - the restriction comes from CITE - but it is worth pinning,
    /// because the message the caller would otherwise see says "Users can only be on one team", which is
    /// not what the rule is.
    /// </remarks>
    [Fact]
    public async Task Push_WhenAUserIsOnTwoTeamsThatAreNotCiteTeams_Is200()
    {
        var msel = BlueprintAppFactory.Msel(status: MselItemStatus.Approved);
        await Seed(msel);

        var first = BlueprintAppFactory.Team(msel.Id);
        var second = BlueprintAppFactory.Team(msel.Id);
        await Seed(first, second);
        await Actor().OnTeam(first).OnTeam(second).SeedAsync();

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Client(actor).PostAsync($"/api/msels/{msel.Id}/integrations", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(msel.Id, Enqueued(msel.Id).MselId);
    }

    /// <summary>
    /// The status change reaches the MSEL's own group and the admin group, naming the two properties that
    /// changed - which is what lets a client update a deployment banner without re-reading the MSEL.
    /// </summary>
    [Fact]
    public async Task Push_BroadcastsMselUpdatedNamingTheChangedProperties()
    {
        var msel = BlueprintAppFactory.Msel(status: MselItemStatus.Approved);
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Client(actor).PostAsync($"/api/msels/{msel.Id}/integrations", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(msel.Id, Enqueued(msel.Id).MselId);

        Assert.Equal(
            [msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.MselUpdated));

        var send = Hub.Of(MainHubMethods.MselUpdated)[0];
        Assert.Equal(MselItemStatus.Pushing, Assert.IsType<Msel>(send.Payload).Status);

        var changed = Assert.IsType<string[]>(send.Args[1]);
        Assert.Contains("status", changed);
        Assert.Contains("integrationStatus", changed);
    }

    /// <summary>
    /// Characterizes a defect. Nothing records who deployed the MSEL: <c>BlueprintContext.SaveEntries</c>
    /// stamps <c>DateModified</c> on a modified entity but never <c>ModifiedBy</c>, and the service does
    /// not set it either. Setting it - in <c>SaveEntries</c> or in the service - turns this red.
    /// </summary>
    /// <remarks>
    /// The consequence reaches further than an audit gap. <c>MselHandler.HandleCreateOrUpdate</c> reads
    /// <c>ModifiedBy ?? CreatedBy</c> to decide whose MSEL roles to include in the broadcast payload, so
    /// with <c>ModifiedBy</c> null every notification about this MSEL carries the <em>creator's</em>
    /// roles, whoever caused it.
    /// </remarks>
    [Fact]
    public async Task Push_DoesNotRecordWhoPushed()
    {
        var msel = BlueprintAppFactory.Msel(status: MselItemStatus.Approved);
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Client(actor).PostAsync($"/api/msels/{msel.Id}/integrations", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(msel.Id, Enqueued(msel.Id).MselId);

        var stored = await NewContext().Msels.SingleAsync(x => x.Id == msel.Id, Ct);
        Assert.NotNull(stored.DateModified);
        Assert.Null(stored.ModifiedBy);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE msels/{id}/integrations - pull
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Pull_AsAnOwner_MarksThePullAndEnqueuesItToApproved()
    {
        var msel = BlueprintAppFactory.Msel(status: MselItemStatus.Deployed);
        msel.PlayerViewId = Guid.NewGuid();
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/msels/{msel.Id}/integrations", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var returned = await Read<Msel>(response);
        Assert.Equal(MselItemStatus.Pulling, returned.Status);
        Assert.Equal("Pulling Integrations", returned.IntegrationStatus);

        var queued = Enqueued(msel.Id);
        Assert.False(queued.IsPush);
        Assert.Equal(MselItemStatus.Approved, queued.FinalStatus);
    }

    /// <summary>
    /// The endpoint does not touch the integration ids. Removing the Player view is the worker's job, and
    /// until it runs the MSEL still points at everything it was deployed to - which is what makes the
    /// pull recoverable if the worker never gets there.
    /// </summary>
    [Fact]
    public async Task Pull_LeavesTheIntegrationIdsAlone()
    {
        var playerViewId = Guid.NewGuid();
        var msel = BlueprintAppFactory.Msel(status: MselItemStatus.Deployed);
        msel.PlayerViewId = playerViewId;
        msel.GalleryCollectionId = Guid.NewGuid();
        msel.CiteEvaluationId = Guid.NewGuid();
        msel.SteamfitterScenarioId = Guid.NewGuid();
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/msels/{msel.Id}/integrations", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(msel.Id, Enqueued(msel.Id).MselId);

        var stored = await NewContext().Msels.SingleAsync(x => x.Id == msel.Id, Ct);
        Assert.Equal(playerViewId, stored.PlayerViewId);
        Assert.NotNull(stored.GalleryCollectionId);
        Assert.NotNull(stored.CiteEvaluationId);
        Assert.NotNull(stored.SteamfitterScenarioId);
    }

    /// <summary>
    /// A MSEL that was never deployed can still be pulled: the endpoint checks nothing about the current
    /// status, so it enqueues a pull for a MSEL there is nothing to pull from, and leaves it saying
    /// "Pulling Integrations" until the worker gets to it.
    /// </summary>
    [Fact]
    public async Task Pull_ForAMselThatWasNeverDeployed_Is200()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/msels/{msel.Id}/integrations", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(MselItemStatus.Pulling, (await Read<Msel>(response)).Status);
        Assert.Equal(msel.Id, Enqueued(msel.Id).MselId);
    }

    [Fact]
    public async Task Pull_AsAnEditor_Is403()
    {
        var msel = BlueprintAppFactory.Msel(status: MselItemStatus.Deployed);
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/msels/{msel.Id}/integrations", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            MselItemStatus.Deployed,
            (await NewContext().Msels.SingleAsync(x => x.Id == msel.Id, Ct)).Status);
    }

    [Fact]
    public async Task Pull_ForAnUnknownMsel_WithManageMselsPermission_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();
        var unknown = Guid.NewGuid();

        var response = await Client(actor).DeleteAsync($"/api/msels/{unknown}/integrations", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal($"MSEL {unknown} was not found.", (await Error(response)).Title);
    }

    /// <summary>
    /// Characterizes a defect in the declared contract rather than in the behaviour. Pull answers 200 with
    /// a MSEL, and its attribute says
    /// <c>[ProducesResponseType(typeof(Msel), (int)HttpStatusCode.NoContent)]</c> - a 204 that carries a
    /// body, which is not a thing. Archive says the same, and push declares 201. Those attributes are what
    /// generates <c>blueprint.ui</c>'s checked-in client and <c>Blueprint.Api.Client</c>, so a generated
    /// method can be typed to return nothing from a call that returns the MSEL.
    /// </summary>
    /// <remarks>
    /// This test pins the half that clients already depend on, and so says which way the mismatch has to
    /// be resolved: correct the attribute, and leave the 200 alone. Reconciling it the other way - making
    /// the endpoint answer 204 - turns this red, and would silently drop the MSEL from the response every
    /// existing caller reads.
    /// </remarks>
    [Fact]
    public async Task Pull_Answers200_NotThe204ItAdvertises()
    {
        var msel = BlueprintAppFactory.Msel(status: MselItemStatus.Deployed);
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/msels/{msel.Id}/integrations", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(msel.Id, (await Read<Msel>(response)).Id);
        Assert.Equal(msel.Id, Enqueued(msel.Id).MselId);
    }

    // ---------------------------------------------------------------------------------------------
    // POST msels/{id}/integrations/cancel
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Cancel answers 200 and enqueues nothing. What it starts instead is the un-awaited cleanup
    /// described on this class, which is why nothing further is asserted here.
    /// </summary>
    [Fact]
    public async Task Cancel_AsAnOwner_Is200AndEnqueuesNothing()
    {
        var msel = BlueprintAppFactory.Msel(status: MselItemStatus.Pushing);
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor)
            .PostAsync($"/api/msels/{msel.Id}/integrations/cancel", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNothingEnqueued();
    }

    [Fact]
    public async Task Cancel_WithManageMselsPermission_Is200()
    {
        var msel = BlueprintAppFactory.Msel(status: MselItemStatus.Pushing);
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Client(actor)
            .PostAsync($"/api/msels/{msel.Id}/integrations/cancel", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Cancel_AsAnEditor_Is403()
    {
        var msel = BlueprintAppFactory.Msel(status: MselItemStatus.Pushing);
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var response = await Client(actor)
            .PostAsync($"/api/msels/{msel.Id}/integrations/cancel", null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// Characterizes a defect. Cancelling a MSEL that does not exist answers 200:
    /// <c>CancelIntegrationsAsync</c> checks the caller's ownership and then calls
    /// <c>IntegrationService.CancelPush</c>, and neither looks the MSEL up. Adding the lookup turns this
    /// red, and the endpoint answers 404 - which is what a client polling a deleted MSEL needs to be told.
    /// </summary>
    /// <remarks>
    /// The cleanup task the 200 kicks off then finds nothing to clean up and logs that it could not find
    /// the MSEL. Nothing observable to a caller comes of it, which is the point: a request that did
    /// nothing reported success.
    /// </remarks>
    [Fact]
    public async Task Cancel_ForAnUnknownMsel_WithManageMselsPermission_Is200()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Client(actor)
            .PostAsync($"/api/msels/{Guid.NewGuid()}/integrations/cancel", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNothingEnqueued();
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE msels/{id}/archive
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Archive is a pull with a different final status, and then a second write.
    /// </summary>
    /// <remarks>
    /// Note the order, which is worth knowing before trusting the status: <c>ArchiveAsync</c> delegates to
    /// <c>PullIntegrationsAsync</c>, which sets <c>Pulling</c> and enqueues the work, and then overwrites
    /// the status with <c>Archived</c> and saves again. So the MSEL reads as archived from the moment the
    /// request returns, while its integrations still exist and <c>IntegrationStatus</c> still says
    /// "Pulling Integrations" - both asserted here.
    /// </remarks>
    [Fact]
    public async Task Archive_AsAnOwner_MarksItArchivedAndEnqueuesAPullToArchived()
    {
        var msel = BlueprintAppFactory.Msel(status: MselItemStatus.Deployed);
        msel.PlayerViewId = Guid.NewGuid();
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/msels/{msel.Id}/archive", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(MselItemStatus.Archived, (await Read<Msel>(response)).Status);

        var stored = await NewContext().Msels.SingleAsync(x => x.Id == msel.Id, Ct);
        Assert.Equal(MselItemStatus.Archived, stored.Status);
        Assert.Equal("Pulling Integrations", stored.IntegrationStatus);
        Assert.NotNull(stored.PlayerViewId);

        var queued = Enqueued(msel.Id);
        Assert.False(queued.IsPush);
        Assert.Equal(MselItemStatus.Archived, queued.FinalStatus);
    }

    /// <summary>
    /// Two saves in one request, so two notifications per group, and a client that watches them sees the
    /// MSEL pass through <c>Pulling</c> on its way to <c>Archived</c>.
    /// </summary>
    [Fact]
    public async Task Archive_BroadcastsBothWrites()
    {
        var msel = BlueprintAppFactory.Msel(status: MselItemStatus.Deployed);
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/msels/{msel.Id}/archive", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(msel.Id, Enqueued(msel.Id).MselId);

        var statuses = Hub.Of(MainHubMethods.MselUpdated)
            .Where(x => x.Group == msel.Id.ToString())
            .Select(x => Assert.IsType<Msel>(x.Payload).Status);

        Assert.Equal([MselItemStatus.Pulling, MselItemStatus.Archived], statuses);
    }

    /// <summary>
    /// The permission check is inherited: <c>ArchiveAsync</c> has none of its own and relies on the one
    /// inside <c>PullIntegrationsAsync</c>, as its comment says.
    /// </summary>
    [Fact]
    public async Task Archive_AsAnEditor_Is403()
    {
        var msel = BlueprintAppFactory.Msel(status: MselItemStatus.Deployed);
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/msels/{msel.Id}/archive", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            MselItemStatus.Deployed,
            (await NewContext().Msels.SingleAsync(x => x.Id == msel.Id, Ct)).Status);
    }

    /// <summary>
    /// The 404 also comes from the inherited call, and it is the only thing standing between an unknown
    /// id and a null reference: <c>ArchiveAsync</c>'s own <c>FindAsync</c> is not null-checked before its
    /// status is set.
    /// </summary>
    [Fact]
    public async Task Archive_ForAnUnknownMsel_WithManageMselsPermission_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();
        var unknown = Guid.NewGuid();

        var response = await Client(actor).DeleteAsync($"/api/msels/{unknown}/archive", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal($"MSEL {unknown} was not found.", (await Error(response)).Title);
    }

    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("POST", "msels/00000000-0000-0000-0000-000000000001/integrations")]
    [InlineData("DELETE", "msels/00000000-0000-0000-0000-000000000001/integrations")]
    [InlineData("POST", "msels/00000000-0000-0000-0000-000000000001/integrations/cancel")]
    [InlineData("GET", "msels/00000000-0000-0000-0000-000000000001/integrations/names")]
    public async Task EveryRoute_Unauthenticated_Is401(string method, string route)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/{route}");

        var response = await AnonymousClient.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A team the duplicate-user check looks at. Only a team mapped to a CITE team type counts.
    /// </summary>
    private static TeamEntity CiteTeam(Guid mselId, string shortName)
    {
        var team = BlueprintAppFactory.Team(mselId);
        team.ShortName = shortName;
        team.CiteTeamTypeId = Guid.NewGuid();

        return team;
    }

    /// <summary>
    /// The queue item for <paramref name="mselId"/>, failing the test if the queue empties first.
    /// </summary>
    /// <remarks>
    /// Items for other MSELs are discarded rather than put back: the queue is drained before each test,
    /// so one being there at all means an earlier test in this class left it, and taking it keeps the
    /// mess from spreading further.
    /// </remarks>
    private IntegrationInformation Enqueued(Guid mselId)
    {
        while (TryTake(TimeSpan.FromSeconds(5), out var taken))
        {
            if (taken.MselId == mselId)
            {
                return taken;
            }
        }

        throw new XunitException(
            $"Nothing was enqueued for MSEL {mselId}. The endpoint answered without handing the MSEL " +
            "to IIntegrationQueue, so no integration work would ever happen.");
    }

    private void AssertNothingEnqueued() => Assert.False(
        TryTake(TimeSpan.FromMilliseconds(150), out var taken),
        $"MSEL {taken?.MselId} was enqueued for integration work, and nothing should have been.");

    /// <summary>
    /// Takes one item, or gives up after <paramref name="patience"/>.
    /// </summary>
    /// <remarks>
    /// <c>IIntegrationQueue.Take</c> wraps a <c>BlockingCollection</c>, so it blocks until something
    /// arrives and the only way out of an empty queue is a cancelled token. The patience is therefore
    /// paid in full whenever the answer is "nothing" - which is why the negative assertion above uses a
    /// short one, and why the wait for an item a request has already made can afford a long one.
    /// </remarks>
    private bool TryTake(TimeSpan patience, out IntegrationInformation taken)
    {
        using var timeout = new CancellationTokenSource(patience);

        try
        {
            taken = Queue.Take(timeout.Token);

            return true;
        }
        catch (OperationCanceledException)
        {
            taken = null;

            return false;
        }
    }

    private async Task<ApiError> Error(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions, Ct);

    private async Task<T> Read<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
}
