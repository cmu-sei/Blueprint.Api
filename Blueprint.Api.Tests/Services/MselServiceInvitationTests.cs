// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary>The four invitation endpoints: <c>GET api/my-join-msels</c>, <c>GET api/my-launch-msels</c>,
/// <c>POST api/msels/{mselId}/join</c> and <c>POST api/msels/{mselId}/launch</c> - how a participant who
/// has no standing in Blueprint at all gets into an exercise.</summary>
public class MselServiceInvitationTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        // The queues are host-wide singletons, unlike everything else a test touches.
        DrainJoinQueue();
        DrainIntegrationQueue();
    }

    // ---------------------------------------------------------------------------------------------
    // GET my-join-msels
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task MyJoinMsels_ReturnsADeployedMselTheUserIsOnATeamOf()
    {
        var msel = await SeedDeployed();
        var team = await SeedTeam(msel);
        var actor = await Actor().OnTeam(team).SeedAsync();

        var list = await MyJoinMsels(Client(actor));

        Assert.Equal([msel.Id], list.Select(x => x.Id));
    }

    [Fact]
    public async Task MyJoinMsels_DoesNotReturnAMselTheUserIsNotOnATeamOf()
    {
        var msel = await SeedDeployed();
        await SeedTeam(msel);
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var list = await MyJoinMsels(Client(actor));

        Assert.Empty(list);
    }

    [Fact]
    public async Task MyJoinMsels_DoesNotReturnAMselWithNoPlayerView()
    {
        var msel = await SeedDeployed(m => m.PlayerViewId = null);
        var team = await SeedTeam(msel);
        var actor = await Actor().OnTeam(team).SeedAsync();

        var list = await MyJoinMsels(Client(actor));

        Assert.Empty(list);
    }

    [Theory]
    [InlineData(MselItemStatus.Pending)]
    [InlineData(MselItemStatus.Entered)]
    [InlineData(MselItemStatus.Approved)]
    [InlineData(MselItemStatus.Pushing)]
    [InlineData(MselItemStatus.Complete)]
    [InlineData(MselItemStatus.Archived)]
    public async Task MyJoinMsels_DoesNotReturnAMselThatIsNotDeployed(MselItemStatus status)
    {
        var msel = await SeedDeployed(m => m.Status = status);
        var team = await SeedTeam(msel);
        var actor = await Actor().OnTeam(team).SeedAsync();

        var list = await MyJoinMsels(Client(actor));

        Assert.Empty(list);
    }

    [Fact]
    public async Task MyJoinMsels_ReturnsTheNewestFirst()
    {
        var older = await SeedDeployed();
        var newer = await SeedDeployed();
        var actor = await Actor()
            .OnTeam(await SeedTeam(older))
            .OnTeam(await SeedTeam(newer))
            .SeedAsync();

        // DateCreated is server-stamped, so the two seeds can land on the same tick. Separate them
        // explicitly rather than sleeping.
        await using (var db = NewContext())
        {
            var row = await db.Msels.SingleAsync(m => m.Id == older.Id, Ct);
            row.DateCreated = DateTime.UtcNow.AddDays(-1);
            await db.SaveChangesAsync(Ct);
        }

        var list = await MyJoinMsels(Client(actor));

        Assert.Equal([newer.Id, older.Id], list.Select(x => x.Id));
    }

    /// <summary>My join MSELs for a user holding a valid invitation but no team is empty.</summary>
    [Fact]
    public async Task MyJoinMsels_ForAUserHoldingAValidInvitationButNoTeam_IsEmpty()
    {
        var msel = await SeedDeployed();
        var team = await SeedTeam(msel);
        await SeedInvitation(msel, team);
        var actor = await Actor().SeedAsync();

        var list = await MyJoinMsels(Client(actor));

        Assert.Empty(list);
    }

    [Fact]
    public async Task MyJoinMsels_WithNoSystemPermission_Is200()
    {
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync("/api/my-join-msels", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task MyJoinMsels_Anonymously_Is401()
    {
        var response = await Client().GetAsync("/api/my-join-msels", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // GET my-launch-msels
    // ---------------------------------------------------------------------------------------------

    /// <summary>My launch MSELs with a valid invitation to a template is still empty.</summary>
    [Fact]
    public async Task MyLaunchMsels_WithAValidInvitationToATemplate_IsStillEmpty()
    {
        var template = await SeedTemplate();
        var team = await SeedTeam(template);
        await SeedInvitation(template, team);
        var actor = await Actor().OnTeam(team).WithAllSystemPermissions().SeedAsync();

        var response = await Client(actor).GetAsync("/api/my-launch-msels", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await Read<List<Msel>>(response));
    }

    [Fact]
    public async Task MyLaunchMsels_Anonymously_Is401()
    {
        var response = await Client().GetAsync("/api/my-launch-msels", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // POST msels/{mselId}/join
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Join_ReturnsThePlayerViewId()
    {
        var msel = await SeedDeployed();
        var team = await SeedTeam(msel);
        await SeedInvitation(msel, team);
        var actor = await Actor().SeedAsync();

        var response = await Join(Client(actor), msel.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(msel.PlayerViewId, await Read<Guid>(response));
    }

    [Fact]
    public async Task Join_AddsTheUserToTheInvitedTeam()
    {
        var msel = await SeedDeployed();
        var team = await SeedTeam(msel);
        await SeedInvitation(msel, team);
        var actor = await Actor().SeedAsync();

        await Join(Client(actor), msel.Id);

        await using var db = NewContext();
        Assert.True(await db.TeamUsers.AnyAsync(tu => tu.TeamId == team.Id && tu.UserId == actor.Id, Ct));
    }

    [Fact]
    public async Task Join_GivesTheUserTheViewerRoleOnTheMsel()
    {
        var msel = await SeedDeployed();
        var team = await SeedTeam(msel);
        await SeedInvitation(msel, team);
        var actor = await Actor().SeedAsync();

        await Join(Client(actor), msel.Id);

        await using var db = NewContext();
        var roles = await db.UserMselRoles
            .Where(umr => umr.MselId == msel.Id && umr.UserId == actor.Id)
            .Select(umr => umr.Role)
            .ToListAsync(Ct);
        Assert.Equal([MselRole.Viewer], roles);
    }

    /// <summary>
    /// A role the participant already has is not replaced, so joining cannot demote them.
    /// </summary>
    [Fact]
    public async Task Join_WhenTheUserAlreadyHasARoleOnTheMsel_LeavesItAlone()
    {
        var msel = await SeedDeployed();
        var team = await SeedTeam(msel);
        await SeedInvitation(msel, team);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Join(Client(actor), msel.Id);

        await using var db = NewContext();
        var roles = await db.UserMselRoles
            .Where(umr => umr.MselId == msel.Id && umr.UserId == actor.Id)
            .Select(umr => umr.Role)
            .ToListAsync(Ct);
        Assert.Equal([MselRole.Owner], roles);
    }

    /// <summary>
    /// Membership is checked across every team of the MSEL, not just the invited one, so an invitation
    /// cannot put a participant on two teams of the same exercise.
    /// </summary>
    [Fact]
    public async Task Join_WhenTheUserIsAlreadyOnAnotherTeamOfTheMsel_DoesNotAddASecond()
    {
        var msel = await SeedDeployed();
        var invited = await SeedTeam(msel);
        var existing = await SeedTeam(msel);
        await SeedInvitation(msel, invited);
        var actor = await Actor().OnTeam(existing).SeedAsync();

        await Join(Client(actor), msel.Id);

        await using var db = NewContext();
        var teamIds = await db.TeamUsers
            .Where(tu => tu.UserId == actor.Id)
            .Select(tu => tu.TeamId)
            .ToListAsync(Ct);
        Assert.Equal([existing.Id], teamIds);
    }

    [Fact]
    public async Task Join_ConsumesAnInvitationSeat()
    {
        var msel = await SeedDeployed();
        var team = await SeedTeam(msel);
        var invitation = await SeedInvitation(msel, team, i => i.UserCount = 3);
        var actor = await Actor().SeedAsync();

        await Join(Client(actor), msel.Id);

        await using var db = NewContext();
        Assert.Equal(4, (await db.Invitations.SingleAsync(i => i.Id == invitation.Id, Ct)).UserCount);
    }

    [Fact]
    public async Task Join_QueuesTheOtherApplicationsForTheInvitedTeam()
    {
        var msel = await SeedDeployed(m =>
        {
            m.UsePlayer = true;
            m.UseGallery = true;
            m.UseCite = true;
        });
        var team = await SeedTeam(msel, t => t.CiteTeamTypeId = Guid.NewGuid());
        await SeedInvitation(msel, team);
        var actor = await Actor().SeedAsync();

        await Join(Client(actor), msel.Id);

        var queued = Assert.Single(DrainJoinQueue());
        Assert.Equal(actor.Id, queued.UserId);
        Assert.Equal(team.Id, queued.TeamId);
        Assert.True(queued.UsePlayer);
        Assert.True(queued.UseGallery);
        Assert.True(queued.UseCite);
    }

    /// <summary>
    /// CITE is the one integration the team has to be configured for: a team with no CITE team type is
    /// not sent to CITE even when the MSEL uses it.
    /// </summary>
    [Fact]
    public async Task Join_WhenTheInvitedTeamHasNoCiteTeamType_DoesNotQueueCite()
    {
        var msel = await SeedDeployed(m => m.UseCite = true);
        var team = await SeedTeam(msel);
        await SeedInvitation(msel, team);
        var actor = await Actor().SeedAsync();

        await Join(Client(actor), msel.Id);

        Assert.False(Assert.Single(DrainJoinQueue()).UseCite);
    }

    [Fact]
    public async Task Join_OfAnUnknownMsel_Is404()
    {
        var actor = await Actor().SeedAsync();

        var response = await Join(Client(actor), Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Join_WithNoInvitationsAtAll_Is403()
    {
        var msel = await SeedDeployed();
        await SeedTeam(msel);
        var actor = await Actor().SeedAsync();
        // Data-row gate: the MSEL has no invitation row, and the invitation rows are the only grant the join reads.

        var response = await Join(Client(actor), msel.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("No invitations exist", (await Read<ApiError>(response)).Title);
    }

    [Fact]
    public async Task Join_WithAValidInvitationOnlyToAnotherMsel_Is403()
    {
        var msel = await SeedDeployed();
        await SeedTeam(msel);
        var other = await SeedDeployed();
        await SeedInvitation(other, await SeedTeam(other));
        var actor = await Actor().SeedAsync();
        // Data-row gate: the only invitation row seeded is on another MSEL; invitation rows are the only grant the join reads.

        var response = await Join(Client(actor), msel.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("No invitations exist", (await Read<ApiError>(response)).Title);
    }

    [Fact]
    public async Task Join_WithADeactivatedInvitation_Is403()
    {
        var msel = await SeedDeployed();
        var team = await SeedTeam(msel);
        await SeedInvitation(msel, team, i => i.WasDeactivated = true);
        var actor = await Actor().SeedAsync();
        // Data-row gate: the deactivated invitation seeded above is the only grant the join reads.

        var response = await Join(Client(actor), msel.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("deactivated", (await Read<ApiError>(response)).Title);
    }

    [Fact]
    public async Task Join_WithAnExpiredInvitation_Is403()
    {
        var msel = await SeedDeployed();
        var team = await SeedTeam(msel);
        await SeedInvitation(msel, team, i => i.ExpirationDateTime = DateTime.UtcNow.AddMinutes(-1));
        var actor = await Actor().SeedAsync();
        // Data-row gate: the expired invitation seeded above is the only grant the join reads.

        var response = await Join(Client(actor), msel.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("expired", (await Read<ApiError>(response)).Title);
    }

    /// <summary>Join with an invitation that has no expiration date is answered with a 403 with no reason given.</summary>
    [Fact]
    public async Task Join_WithAnInvitationThatHasNoExpirationDate_Is403_WithNoReasonGiven()
    {
        var msel = await SeedDeployed();
        var team = await SeedTeam(msel);
        await SeedInvitation(msel, team, i => i.ExpirationDateTime = null);
        var actor = await Actor().SeedAsync();
        // Data-row gate: the invitation with no expiry seeded above is the only grant the join reads.

        var response = await Join(Client(actor), msel.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Invitation is not valid: ", (await Read<ApiError>(response)).Title);
    }

    [Fact]
    public async Task Join_WithAnInvitationAtCapacity_Is403()
    {
        var msel = await SeedDeployed();
        var team = await SeedTeam(msel);
        await SeedInvitation(msel, team, i =>
        {
            i.MaxUsersAllowed = 2;
            i.UserCount = 2;
        });
        var actor = await Actor().SeedAsync();
        // Data-row gate: the full invitation seeded above is the only grant the join reads.

        var response = await Join(Client(actor), msel.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("at capacity (2/2)", (await Read<ApiError>(response)).Title);
    }

    [Theory]
    [InlineData(MselItemStatus.Pending)]
    [InlineData(MselItemStatus.Approved)]
    [InlineData(MselItemStatus.Pushing)]
    [InlineData(MselItemStatus.Archived)]
    public async Task Join_ToAMselThatIsNotDeployed_Is403(MselItemStatus status)
    {
        var msel = await SeedDeployed(m => m.Status = status);
        var team = await SeedTeam(msel);
        await SeedInvitation(msel, team);
        var actor = await Actor().SeedAsync();
        // Data-row gate: the invitation seeded above, on a MSEL that is not deployed, is the only grant the join reads.

        var response = await Join(Client(actor), msel.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("MSEL not deployed", (await Read<ApiError>(response)).Title);
    }

    [Fact]
    public async Task Join_WithATeamIdThatIsNotTheInvitedOne_Is403()
    {
        var msel = await SeedDeployed();
        var invited = await SeedTeam(msel);
        var other = await SeedTeam(msel);
        await SeedInvitation(msel, invited);
        var actor = await Actor().SeedAsync();
        // Data-row gate: the invitation seeded above names another team; it is the only grant the join reads.

        var response = await Join(Client(actor), msel.Id, other.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("team mismatch", (await Read<ApiError>(response)).Title);
    }

    [Fact]
    public async Task Join_WithTheInvitedTeamId_Is200()
    {
        var msel = await SeedDeployed();
        var team = await SeedTeam(msel);
        await SeedInvitation(msel, team);
        var actor = await Actor().SeedAsync();

        var response = await Join(Client(actor), msel.Id, team.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>An unparseable <c>teamId</c> is treated as absent, and the caller joins the invited
    /// team.</summary>
    [Fact]
    public async Task Join_WithAnUnparseableTeamId_IgnoresIt()
    {
        var msel = await SeedDeployed();
        var team = await SeedTeam(msel);
        await SeedInvitation(msel, team);
        var actor = await Actor().SeedAsync();

        var response = await Client(actor)
            .PostAsync($"/api/msels/{msel.Id}/join?teamId=not-a-guid", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = NewContext();
        Assert.True(await db.TeamUsers.AnyAsync(tu => tu.TeamId == team.Id && tu.UserId == actor.Id, Ct));
    }

    [Fact]
    public async Task Join_WhenTheInvitationRequiresADomain_AcceptsAMatchingAddress()
    {
        var msel = await SeedDeployed();
        var team = await SeedTeam(msel);
        await SeedInvitation(msel, team, i => i.EmailDomain = "@example.test");
        var actor = await Actor().SeedAsync();

        var response = await Join(ClientWithEmail(actor, "someone@example.test"), msel.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Join_WhenTheInvitationRequiresADomain_RejectsAnotherAddress_Is403()
    {
        var msel = await SeedDeployed();
        var team = await SeedTeam(msel);
        await SeedInvitation(msel, team, i => i.EmailDomain = "@example.test");
        var actor = await Actor().SeedAsync();

        var response = await Join(ClientWithEmail(actor, "someone@elsewhere.test"), msel.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = await Read<ApiError>(response);
        Assert.Contains("does not match the invitation requirements", error.Title);
        Assert.Contains("@example.test", error.Title);
    }

    /// <summary>Join when the domain is written without an at sign rejects everyone.</summary>
    [Fact]
    public async Task Join_WhenTheDomainIsWrittenWithoutAnAtSign_RejectsEveryone()
    {
        var msel = await SeedDeployed();
        var team = await SeedTeam(msel);
        await SeedInvitation(msel, team, i => i.EmailDomain = "example.test");
        var actor = await Actor().SeedAsync();

        var response = await Join(ClientWithEmail(actor, "someone@example.test"), msel.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Join when the address differs only in case is answered with a 403.</summary>
    [Fact]
    public async Task Join_WhenTheAddressDiffersOnlyInCase_Is403()
    {
        var msel = await SeedDeployed();
        var team = await SeedTeam(msel);
        await SeedInvitation(msel, team, i => i.EmailDomain = "@example.test");
        var actor = await Actor().SeedAsync();

        var response = await Join(ClientWithEmail(actor, "Someone@Example.Test"), msel.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// A caller with no email claim is treated as having an empty address, so only an unrestricted
    /// invitation admits them.
    /// </summary>
    [Fact]
    public async Task Join_WithNoEmailClaim_AndARestrictedInvitation_Is403()
    {
        var msel = await SeedDeployed();
        var team = await SeedTeam(msel);
        await SeedInvitation(msel, team, i => i.EmailDomain = "@example.test");
        var actor = await Actor().SeedAsync();
        // Data-row gate: the domain-restricted invitation seeded above is the only grant the join reads.

        var response = await Join(Client(actor), msel.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("Your email ()", (await Read<ApiError>(response)).Title);
    }

    [Fact]
    public async Task Join_WithNoEmailClaim_AndAnUnrestrictedInvitation_Is200()
    {
        var msel = await SeedDeployed();
        var team = await SeedTeam(msel);
        await SeedInvitation(msel, team, i => i.EmailDomain = "");
        var actor = await Actor().SeedAsync();

        var response = await Join(Client(actor), msel.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Join with two invitations that both admit the caller is answered with a 500.</summary>
    [Fact]
    public async Task Join_WithTwoInvitationsThatBothAdmitTheCaller_Is500()
    {
        var msel = await SeedDeployed();
        var first = await SeedTeam(msel);
        var second = await SeedTeam(msel);
        await SeedInvitation(msel, first);
        await SeedInvitation(msel, second);
        var actor = await Actor().SeedAsync();

        var response = await Join(Client(actor), msel.Id);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Sequence contains more than one matching element", failure.Title);
        Assert.Contains("MselService.GetValidInvitationAsync", failure.Detail);
    }

    [Fact]
    public async Task Join_WithTwoInvitations_AndAnExplicitTeamId_Is200()
    {
        var msel = await SeedDeployed();
        var first = await SeedTeam(msel);
        var second = await SeedTeam(msel);
        await SeedInvitation(msel, first);
        await SeedInvitation(msel, second);
        var actor = await Actor().SeedAsync();

        var response = await Join(Client(actor), msel.Id, second.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = NewContext();
        Assert.True(await db.TeamUsers.AnyAsync(tu => tu.TeamId == second.Id && tu.UserId == actor.Id, Ct));
    }

    /// <summary>Join with two unusable invitations explains only one of them.</summary>
    [Fact]
    public async Task Join_WithTwoUnusableInvitations_ExplainsOnlyOneOfThem()
    {
        var msel = await SeedDeployed();
        var first = await SeedTeam(msel);
        var second = await SeedTeam(msel);
        await SeedInvitation(msel, first, i => i.WasDeactivated = true);
        await SeedInvitation(msel, second, i => i.ExpirationDateTime = DateTime.UtcNow.AddMinutes(-1));
        var actor = await Actor().SeedAsync();
        // Data-row gate: the two unusable invitations seeded above are the only grant the join reads.

        var response = await Join(Client(actor), msel.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var title = (await Read<ApiError>(response)).Title;
        Assert.True(
            title.Contains("deactivated") ^ title.Contains("expired"),
            $"Expected exactly one of the two invitations to be explained, got: {title}");
    }

    /// <summary>Join of a deployed MSEL with no player view is answered with a 500 after spending the seat.</summary>
    [Fact]
    public async Task Join_OfADeployedMselWithNoPlayerView_Is500_AfterSpendingTheSeat()
    {
        var msel = await SeedDeployed(m => m.PlayerViewId = null);
        var team = await SeedTeam(msel);
        var invitation = await SeedInvitation(msel, team);
        var actor = await Actor().SeedAsync();

        var response = await Join(Client(actor), msel.Id);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Nullable object must have a value.", failure.Title);
        Assert.Contains("MselService.JoinMselByInvitationAsync", failure.Detail);
        await using var db = NewContext();
        Assert.Equal(1, (await db.Invitations.SingleAsync(i => i.Id == invitation.Id, Ct)).UserCount);
        Assert.True(await db.TeamUsers.AnyAsync(tu => tu.TeamId == team.Id && tu.UserId == actor.Id, Ct));
    }

    /// <summary>
    /// A participant already in the MSEL's Player View may re-join without a usable invitation.
    /// </summary>
    /// <remarks>
    /// This is the deliberate exception to the gate, and the reason is written in the method: someone who
    /// has already joined must not be locked out of their own running exercise because the link they came
    /// in on has since expired or filled up. Their Blueprint team membership is still reconciled, because
    /// it is the one thing being in the Player View does not prove.
    /// </remarks>
    [Fact]
    public async Task Join_ByAParticipantAlreadyInThePlayerView_DoesNotNeedAUsableInvitation()
    {
        var msel = await SeedDeployed();
        var team = await SeedTeam(msel);
        await SeedInvitation(msel, team, i => i.ExpirationDateTime = DateTime.UtcNow.AddMinutes(-1));
        var actor = await Actor().SeedAsync();
        AlreadyInPlayerView(actor, msel);

        var response = await Join(Client(actor), msel.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Join_ByAParticipantAlreadyInThePlayerView_WithAUsableInvitation_DoesNotSpendASeat()
    {
        var msel = await SeedDeployed();
        var team = await SeedTeam(msel);
        var invitation = await SeedInvitation(msel, team);
        var actor = await Actor().SeedAsync();
        AlreadyInPlayerView(actor, msel);

        await Join(Client(actor), msel.Id);

        await using var db = NewContext();
        Assert.Equal(0, (await db.Invitations.SingleAsync(i => i.Id == invitation.Id, Ct)).UserCount);
        Assert.Empty(DrainJoinQueue());
    }

    [Fact]
    public async Task Join_ByAParticipantAlreadyInThePlayerView_StillAddsThemToTheBlueprintTeam()
    {
        var msel = await SeedDeployed();
        var team = await SeedTeam(msel);
        await SeedInvitation(msel, team);
        var actor = await Actor().SeedAsync();
        AlreadyInPlayerView(actor, msel);

        await Join(Client(actor), msel.Id);

        await using var db = NewContext();
        Assert.True(await db.TeamUsers.AnyAsync(tu => tu.TeamId == team.Id && tu.UserId == actor.Id, Ct));
    }

    /// <summary>Join when player answers with an array of views treats the participant as new.</summary>
    [Fact]
    public async Task Join_WhenPlayerAnswersWithAnArrayOfViews_TreatsTheParticipantAsNew()
    {
        var msel = await SeedDeployed();
        var team = await SeedTeam(msel);
        await SeedInvitation(msel, team, i => i.ExpirationDateTime = DateTime.UtcNow.AddMinutes(-1));
        var actor = await Actor().SeedAsync();
        // Data-row gate: the expired invitation seeded above and the Player views answered are the only grant the join reads.
        Factory.PlayerApi
            .GetUserViewsAsync(actor.Id, Arg.Any<CancellationToken>())
            .Returns(new Player.Api.Client.View[] { new() { Id = (Guid)msel.PlayerViewId } });

        var response = await Join(Client(actor), msel.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Join_WithNoSystemPermission_Is200()
    {
        var msel = await SeedDeployed();
        var team = await SeedTeam(msel);
        await SeedInvitation(msel, team);
        var actor = await Actor().SeedAsync();

        var response = await Join(Client(actor), msel.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Join_Anonymously_Is401()
    {
        var msel = await SeedDeployed();

        var response = await Join(Client(), msel.Id);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // POST msels/{mselId}/launch
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Launch_ClonesTheTemplateAndReturnsTheClone()
    {
        var template = await SeedTemplate();
        var team = await SeedTeam(template);
        await SeedInvitation(template, team);
        var actor = await Actor().SeedAsync();

        var response = await Launch(ClientWithEmail(actor, "someone@example.test"), template.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var launched = await Read<Msel>(response);
        Assert.NotEqual(template.Id, launched.Id);
        // The copy renames the clone "<template> - <launching user>"; that naming belongs to
        // MselServiceCopyTests, so only the derivation is asserted here.
        Assert.StartsWith(template.Name, launched.Name);
        await using var db = NewContext();
        Assert.True(await db.Msels.AnyAsync(m => m.Id == launched.Id, Ct));
        Assert.True(await db.Msels.AnyAsync(m => m.Id == template.Id, Ct));
    }

    /// <summary>
    /// The launching participant is added to the clone's copy of the invited team, as a Submitter. This is
    /// the only path in the application that reaches that branch of the copy.
    /// </summary>
    [Fact]
    public async Task Launch_AddsTheParticipantToTheClonedInvitedTeam()
    {
        var template = await SeedTemplate();
        var team = await SeedTeam(template);
        await SeedInvitation(template, team);
        var actor = await Actor().SeedAsync();

        var launched = await Read<Msel>(await Launch(ClientWithEmail(actor, "a@example.test"), template.Id));

        await using var db = NewContext();
        var clonedTeam = await db.Teams.SingleAsync(t => t.MselId == launched.Id, Ct);
        Assert.NotEqual(team.Id, clonedTeam.Id);
        Assert.True(await db.TeamUsers.AnyAsync(tu => tu.TeamId == clonedTeam.Id && tu.UserId == actor.Id, Ct));
        var roles = await db.UserTeamRoles
            .Where(utr => utr.TeamId == clonedTeam.Id && utr.UserId == actor.Id)
            .Select(utr => utr.Role)
            .ToListAsync(Ct);
        Assert.Equal(["Submitter"], roles);
    }

    [Fact]
    public async Task Launch_DoesNotAddTheParticipantToTheOtherTeams()
    {
        var template = await SeedTemplate();
        var invited = await SeedTeam(template);
        var other = await SeedTeam(template);
        await SeedInvitation(template, invited);
        var actor = await Actor().SeedAsync();

        var launched = await Read<Msel>(await Launch(ClientWithEmail(actor, "a@example.test"), template.Id));

        await using var db = NewContext();
        var clonedOther = await db.Teams.SingleAsync(t => t.MselId == launched.Id && t.Name == other.Name, Ct);
        Assert.False(await db.TeamUsers.AnyAsync(tu => tu.TeamId == clonedOther.Id, Ct));
    }

    /// <summary>
    /// Someone who is already on the template's team is not added to the clone's team a second time.
    /// </summary>
    /// <remarks>
    /// The copy carries the template's own team members across, so the guard is needed: <c>addUser</c> is
    /// cleared when any copied <c>TeamUser</c> is the current user. Note what this means in practice -
    /// an author who put themselves on the template team launches into a clone where they are a member
    /// but have no <c>UserTeamRole</c>, because the role is only written alongside the membership.
    /// </remarks>
    [Fact]
    public async Task Launch_WhenTheParticipantIsAlreadyOnTheTemplateTeam_DoesNotAddThemTwice()
    {
        var template = await SeedTemplate();
        var team = await SeedTeam(template);
        await SeedInvitation(template, team);
        var actor = await Actor().OnTeam(team).SeedAsync();

        var launched = await Read<Msel>(await Launch(ClientWithEmail(actor, "a@example.test"), template.Id));

        await using var db = NewContext();
        var clonedTeam = await db.Teams.SingleAsync(t => t.MselId == launched.Id, Ct);
        var memberships = await db.TeamUsers
            .CountAsync(tu => tu.TeamId == clonedTeam.Id && tu.UserId == actor.Id, Ct);
        Assert.Equal(1, memberships);
        Assert.False(await db.UserTeamRoles.AnyAsync(utr => utr.TeamId == clonedTeam.Id, Ct));
    }

    [Fact]
    public async Task Launch_SetsTheCloneStartTimeToNow()
    {
        var template = await SeedTemplate(m => m.StartTime = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var team = await SeedTeam(template);
        await SeedInvitation(template, team);
        var actor = await Actor().SeedAsync();
        var before = DateTime.UtcNow;

        var launched = await Read<Msel>(await Launch(ClientWithEmail(actor, "a@example.test"), template.Id));

        await using var db = NewContext();
        var startTime = (await db.Msels.SingleAsync(m => m.Id == launched.Id, Ct)).StartTime;
        Assert.InRange(startTime, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task Launch_ConsumesAnInvitationSeat()
    {
        var template = await SeedTemplate();
        var team = await SeedTeam(template);
        var invitation = await SeedInvitation(template, team, i => i.UserCount = 1);
        var actor = await Actor().SeedAsync();

        await Launch(ClientWithEmail(actor, "a@example.test"), template.Id);

        await using var db = NewContext();
        Assert.Equal(2, (await db.Invitations.SingleAsync(i => i.Id == invitation.Id, Ct)).UserCount);
    }

    [Fact]
    public async Task Launch_QueuesThePushForTheClone()
    {
        var template = await SeedTemplate();
        var team = await SeedTeam(template);
        await SeedInvitation(template, team);
        var actor = await Actor().SeedAsync();

        var launched = await Read<Msel>(await Launch(ClientWithEmail(actor, "a@example.test"), template.Id));

        var queued = Assert.Single(DrainIntegrationQueue());
        Assert.Equal(launched.Id, queued.MselId);
        Assert.Equal(launched.PlayerViewId, queued.PlayerViewId);
        Assert.True(queued.IsPush);
    }

    /// <summary>Launch answers a Player View id that is generated after the save and handed to the integration
    /// queue.</summary>
    [Fact]
    public async Task Launch_ReturnsAPlayerViewIdThatIsNotYetStored()
    {
        var template = await SeedTemplate();
        var team = await SeedTeam(template);
        await SeedInvitation(template, team);
        var actor = await Actor().SeedAsync();

        var launched = await Read<Msel>(await Launch(ClientWithEmail(actor, "a@example.test"), template.Id));

        Assert.NotNull(launched.PlayerViewId);
        await using var db = NewContext();
        Assert.Null((await db.Msels.SingleAsync(m => m.Id == launched.Id, Ct)).PlayerViewId);
    }

    [Fact]
    public async Task Launch_OfAnUnknownMsel_Is404()
    {
        var actor = await Actor().SeedAsync();

        var response = await Launch(ClientWithEmail(actor, "a@example.test"), Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Launch with no email claim is answered with a 500.</summary>
    [Fact]
    public async Task Launch_WithNoEmailClaim_Is500()
    {
        var template = await SeedTemplate();
        var team = await SeedTeam(template);
        await SeedInvitation(template, team);
        var actor = await Actor().SeedAsync();

        var response = await Launch(Client(actor), template.Id);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Sequence contains no matching element", failure.Title);
        Assert.Contains("MselService.LaunchMselByInvitationAsync", failure.Detail);
    }

    [Fact]
    public async Task Launch_OfAMselThatIsNotATemplate_Is403()
    {
        var msel = await SeedTemplate(m => m.IsTemplate = false);
        var team = await SeedTeam(msel);
        await SeedInvitation(msel, team);
        var actor = await Actor().SeedAsync();

        var response = await Launch(ClientWithEmail(actor, "a@example.test"), msel.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Launch of a template in any status is answered with a 200.</summary>
    [Theory]
    [InlineData(MselItemStatus.Pending)]
    [InlineData(MselItemStatus.Approved)]
    [InlineData(MselItemStatus.Archived)]
    public async Task Launch_OfATemplateInAnyStatus_Is200(MselItemStatus status)
    {
        var template = await SeedTemplate(m => m.Status = status);
        var team = await SeedTeam(template);
        await SeedInvitation(template, team);
        var actor = await Actor().SeedAsync();

        var response = await Launch(ClientWithEmail(actor, "a@example.test"), template.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Launch_WithAValidInvitationOnlyToAnotherTemplate_Is403()
    {
        var template = await SeedTemplate();
        await SeedTeam(template);
        var other = await SeedTemplate();
        await SeedInvitation(other, await SeedTeam(other));
        var actor = await Actor().SeedAsync();

        var response = await Launch(ClientWithEmail(actor, "a@example.test"), template.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Launch_WithADeactivatedInvitation_Is403()
    {
        var template = await SeedTemplate();
        var team = await SeedTeam(template);
        await SeedInvitation(template, team, i => i.WasDeactivated = true);
        var actor = await Actor().SeedAsync();

        var response = await Launch(ClientWithEmail(actor, "a@example.test"), template.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Launch_WithAnExpiredInvitation_Is403()
    {
        var template = await SeedTemplate();
        var team = await SeedTeam(template);
        await SeedInvitation(template, team, i => i.ExpirationDateTime = DateTime.UtcNow.AddMinutes(-1));
        var actor = await Actor().SeedAsync();

        var response = await Launch(ClientWithEmail(actor, "a@example.test"), template.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Launch_WithAnInvitationAtCapacity_Is403()
    {
        var template = await SeedTemplate();
        var team = await SeedTeam(template);
        await SeedInvitation(template, team, i =>
        {
            i.MaxUsersAllowed = 1;
            i.UserCount = 1;
        });
        var actor = await Actor().SeedAsync();

        var response = await Launch(ClientWithEmail(actor, "a@example.test"), template.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Launch_WithATeamIdThatIsNotTheInvitedOne_Is403()
    {
        var template = await SeedTemplate();
        var invited = await SeedTeam(template);
        var other = await SeedTeam(template);
        await SeedInvitation(template, invited);
        var actor = await Actor().SeedAsync();

        var response = await Launch(ClientWithEmail(actor, "a@example.test"), template.Id, other.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Launch_WhenTheInvitationRequiresADomain_RejectsAnotherAddress_Is403()
    {
        var template = await SeedTemplate();
        var team = await SeedTeam(template);
        await SeedInvitation(template, team, i => i.EmailDomain = "@example.test");
        var actor = await Actor().SeedAsync();

        var response = await Launch(ClientWithEmail(actor, "a@elsewhere.test"), template.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Launch twice with one invitation clones the template twice.</summary>
    [Fact]
    public async Task Launch_TwiceWithOneInvitation_ClonesTheTemplateTwice()
    {
        var template = await SeedTemplate();
        var team = await SeedTeam(template);
        await SeedInvitation(template, team, i => i.MaxUsersAllowed = 5);
        var actor = await Actor().SeedAsync();
        var client = ClientWithEmail(actor, "a@example.test");

        var first = await Read<Msel>(await Launch(client, template.Id));
        var second = await Read<Msel>(await Launch(client, template.Id));

        Assert.NotEqual(first.Id, second.Id);
        await using var db = NewContext();
        Assert.Equal(3, await db.Msels.CountAsync(Ct));
        Assert.Equal(2, DrainIntegrationQueue().Count);
    }

    [Fact]
    public async Task Launch_WithNoSystemPermission_Is200()
    {
        var template = await SeedTemplate();
        var team = await SeedTeam(template);
        await SeedInvitation(template, team);
        var actor = await Actor().SeedAsync();

        var response = await Launch(ClientWithEmail(actor, "a@example.test"), template.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Launch_Anonymously_Is401()
    {
        var template = await SeedTemplate();

        var response = await Launch(Client(), template.Id);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A MSEL in the state <c>POST msels/{id}/join</c> expects: deployed, with a Player View.
    /// </summary>
    private async Task<MselEntity> SeedDeployed(Action<MselEntity> arrange = null)
    {
        var msel = TestData.Msel(status: MselItemStatus.Deployed);
        msel.PlayerViewId = Guid.NewGuid();
        arrange?.Invoke(msel);
        await Seed(msel);

        return msel;
    }

    /// <summary>
    /// A template, which is the only kind of MSEL <c>POST msels/{id}/launch</c> will clone.
    /// </summary>
    private async Task<MselEntity> SeedTemplate(Action<MselEntity> arrange = null)
    {
        var msel = TestData.Msel(isTemplate: true);
        arrange?.Invoke(msel);
        await Seed(msel);

        return msel;
    }

    private async Task<TeamEntity> SeedTeam(MselEntity msel, Action<TeamEntity> arrange = null)
    {
        var team = TestData.Team(msel.Id, msel.CreatedBy);
        arrange?.Invoke(team);
        await Seed(team);

        return team;
    }

    /// <summary>
    /// A usable invitation to <paramref name="team"/>. Every field the validity check reads is set, so a
    /// test that wants an unusable one spoils exactly the field it is about.
    /// </summary>
    /// <remarks>
    /// <c>Id</c> is left unset: the column is <c>DatabaseGeneratedOption.Identity</c>, so a value assigned
    /// here would be ignored and the entity returned would not name the row that was written.
    /// </remarks>
    private async Task<InvitationEntity> SeedInvitation(
        MselEntity msel, TeamEntity team, Action<InvitationEntity> arrange = null)
    {
        var invitation = new InvitationEntity
        {
            MselId = msel.Id,
            TeamId = team.Id,
            EmailDomain = null,
            ExpirationDateTime = DateTime.UtcNow.AddDays(1),
            MaxUsersAllowed = 10,
            UserCount = 0
        };
        arrange?.Invoke(invitation);
        await Seed(invitation);

        return invitation;
    }

    /// <summary>
    /// Makes Player answer that <paramref name="actor"/> is in <paramref name="msel"/>'s view, which is
    /// what the join path treats as "has joined before".
    /// </summary>
    /// <remarks>
    /// A <c>List</c> and not an array, deliberately - <c>PlayerService.GetMyViewsAsync</c> casts the
    /// result to <c>List&lt;View&gt;</c> and swallows the failure. See
    /// <see cref="Join_WhenPlayerAnswersWithAnArrayOfViews_TreatsTheParticipantAsNew"/>.
    /// </remarks>
    private void AlreadyInPlayerView(TestActor actor, MselEntity msel) =>
        Factory.PlayerApi
            .GetUserViewsAsync(actor.Id, Arg.Any<CancellationToken>())
            .Returns(new List<Player.Api.Client.View> { new() { Id = (Guid)msel.PlayerViewId } });

    private async Task<List<Msel>> MyJoinMsels(HttpClient client)
    {
        var response = await client.GetAsync("/api/my-join-msels", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<List<Msel>>(response);
    }

    private async Task<HttpResponseMessage> Join(HttpClient client, Guid mselId, Guid? teamId = null) =>
        await client.PostAsync(Route(mselId, "join", teamId), null, Ct);

    private async Task<HttpResponseMessage> Launch(HttpClient client, Guid mselId, Guid? teamId = null) =>
        await client.PostAsync(Route(mselId, "launch", teamId), null, Ct);

    private static string Route(Guid mselId, string action, Guid? teamId) =>
        teamId is null
            ? $"/api/msels/{mselId}/{action}"
            : $"/api/msels/{mselId}/{action}?teamId={teamId}";

    private async Task<T> Read<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);

    private List<JoinInformation> DrainJoinQueue() =>
        Drain(Factory.Services.GetRequiredService<IJoinQueue>().Take);

    private List<IntegrationInformation> DrainIntegrationQueue() =>
        Drain(Factory.Services.GetRequiredService<IIntegrationQueue>().Take);

    /// <summary>
    /// Takes everything the queue is holding now.
    /// </summary>
    /// <remarks>
    /// Both queues are <c>BlockingCollection</c> wrappers exposing only <c>Take</c>, so emptiness can only
    /// be observed as a wait that ends. The wait is short because it is not a race: whatever a request
    /// enqueued was enqueued synchronously, before the response this test already has.
    /// </remarks>
    private static List<T> Drain<T>(Func<CancellationToken, T> take)
    {
        List<T> items = [];

        while (true)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

            try
            {
                items.Add(take(timeout.Token));
            }
            catch (OperationCanceledException)
            {
                return items;
            }
        }
    }
}
