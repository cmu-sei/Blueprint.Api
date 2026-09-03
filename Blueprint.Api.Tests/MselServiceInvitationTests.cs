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
using Blueprint.Api.Tests.Infrastructure;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// The four invitation endpoints: <c>GET api/my-join-msels</c>,
/// <c>GET api/my-launch-msels</c>, <c>POST api/msels/{mselId}/join</c> and
/// <c>POST api/msels/{mselId}/launch</c> - how a participant who has no standing in Blueprint at all
/// gets into an exercise.
/// </summary>
/// <remarks>
/// <para>
/// These are the only endpoints in the codebase with <em>no</em> authorization check of any kind. That is
/// deliberate and it is the point of the feature: a participant arrives holding a link, not a permission,
/// so the <c>InvitationEntity</c> is the whole gate. Everything below is therefore really a test of that
/// gate, and the tests that matter most are the ones where it should have closed and did not.
/// </para>
/// <para>
/// Join and launch are different operations on different objects and they disagree about almost
/// everything. Join takes a MSEL that has already been deployed to Player and puts the caller on one of
/// its teams. Launch takes a <em>template</em>, clones it, and puts the caller on the clone's copy of the
/// invited team - so launch is the one path in the application that reaches
/// <c>privateMselCopyAsync</c> with a non-null team, and the only way the copy's "add the current user"
/// branch ever runs. The copy itself is covered by <see cref="MselServiceCopyTests"/>; what is asserted
/// here is only what launch adds to it.
/// </para>
/// <para>
/// Their validity rules differ too, and not in ways anyone would design on purpose: join requires the
/// MSEL to be <c>Deployed</c> and launch requires it to be a template but does not care about its status;
/// join treats a missing email claim as an empty address and launch throws on it; join lets a returning
/// participant in without a usable invitation and launch has no such notion, so the same link can be used
/// to clone the same template over and over until its seat count runs out. Each of those is pinned below.
/// </para>
/// <para>
/// Note where the two list endpoints live: <c>/api/my-join-msels</c> and <c>/api/my-launch-msels</c>, at
/// the API root, while everything else about a MSEL is under <c>/api/msels/</c>. Requesting the natural
/// <c>/api/msels/my-join-msels</c> is a <em>400</em>, because it binds against <c>GET msels/{id}</c> and
/// the segment is not a guid - so a caller who guesses wrongly is told their request was malformed rather
/// than that the route does not exist. Phase 4's OpenAPI snapshot is where that shape gets pinned; the
/// helpers below just use the real paths.
/// </para>
/// <para>
/// Two things about the harness are load-bearing here. The <c>email</c> claim comes from
/// <see cref="ApiTestBase.ClientWithEmail"/>, because an invitation may be restricted to an email domain
/// and these two methods are the only readers of that claim in the codebase. And the join and integration
/// queues are <em>real</em> singletons on the host with no worker draining them - that is what lets a test
/// assert what the request handed off - so this class drains both before every test, or one test's
/// handoff would be the next one's evidence.
/// </para>
/// <para>
/// Three notes for anyone mutation-checking this file. <c>JoinMselByInvitationAsync</c> has <em>two</em>
/// separate <c>!isAlreadyInPlayerView</c> guards, one around the seat increment and one around the queue
/// handoff, so breaking the queue guard while also removing the write it guards changes nothing
/// observable. The Player-cast characterization
/// (<see cref="Join_WhenPlayerAnswersWithAnArrayOfViews_TreatsTheParticipantAsNew"/>) hides behind any
/// mutation that also forces the invitation to be required, because both produce the 403 it expects. And
/// the <c>_WithNoSystemPermission_Is200</c> tests here are pinned by construction rather than by a
/// mutation of their own: every other test in the class also uses an actor with no system permission, so
/// adding an authorization check to any of these four endpoints reddens the whole file at once.
/// </para>
/// </remarks>
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
        using (var db = NewContext())
        {
            var row = await db.Msels.SingleAsync(m => m.Id == older.Id, Ct);
            row.DateCreated = DateTime.UtcNow.AddDays(-1);
            await db.SaveChangesAsync(Ct);
        }

        var list = await MyJoinMsels(Client(actor));

        Assert.Equal([newer.Id, older.Id], list.Select(x => x.Id));
    }

    /// <summary>
    /// Characterizes the endpoint's name: it lists MSELs the caller is <em>already</em> on a team of, and
    /// ignores invitations entirely.
    /// </summary>
    /// <remarks>
    /// The method behind it is <c>GetMyJoinInvitationMselsAsync</c> and the route is
    /// <c>my-join-msels</c>, so a reader would expect the MSELs the caller has been invited to. Its body
    /// says otherwise, and says so on purpose - the email-domain auto-discovery it used to do is
    /// commented out, leaving a query over <c>TeamUsers</c>. The consequence is that the list is empty
    /// for exactly the person it was meant to serve: someone holding a valid invitation who has not
    /// joined yet. They cannot discover the MSEL through the API, only through the link.
    /// <para>
    /// Fixing the name is free; restoring the discovery is a decision. Either way this test turns red.
    /// </para>
    /// </remarks>
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
        var response = await AnonymousClient.GetAsync("/api/my-join-msels", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // GET my-launch-msels
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Characterizes <c>GET my-launch-msels</c>: it returns an empty list unconditionally.
    /// </summary>
    /// <remarks>
    /// <c>GetMyLaunchInvitationMselsAsync</c> is a single <c>return new List&lt;Msel&gt;()</c> - the
    /// email-domain discovery behind it was commented out and nothing replaced it. The endpoint, its
    /// <c>[SwaggerOperation(OperationId = "getMyLaunchMsels")]</c> and the generated client method in
    /// <c>blueprint.ui</c> all still exist, so a caller gets a successful, permanently empty answer
    /// rather than a 404 or a deprecation. This test seeds every row the method would have needed and
    /// asserts the emptiness anyway, so it fails the moment the body does anything.
    /// </remarks>
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
        var response = await AnonymousClient.GetAsync("/api/my-launch-msels", Ct);

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

        using var db = NewContext();
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

        using var db = NewContext();
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

        using var db = NewContext();
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

        using var db = NewContext();
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

        using var db = NewContext();
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

        var response = await Join(Client(actor), msel.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("expired", (await Read<ApiError>(response)).Title);
    }

    /// <summary>
    /// Characterizes an invitation with no expiration date: it never works, rather than never expiring -
    /// and the 403 gives no reason at all.
    /// </summary>
    /// <remarks>
    /// <c>ExpirationDateTime</c> is nullable, the filter asks <c>i.ExpirationDateTime &gt; now</c> and the
    /// explanation asks <c>i.ExpirationDateTime &lt;= now</c>. Both are false for null, so the natural
    /// reading of a blank expiry - "this link does not expire" - is exactly inverted, and then no reason
    /// is added to the list: the message is <c>"Invitation is not valid: "</c>, ending in the colon. Every
    /// other way an invitation can fail names itself, so this is the one case where a participant and a
    /// support engineer both have nothing to go on. Nothing stops one being created either;
    /// <c>POST api/invitations</c> takes the field as given.
    /// <para>
    /// The exact-equality assertion is the point of this test rather than incidental precision: a fix
    /// that only reworded the reasons would leave a <c>Contains</c> green.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Join_WithAnInvitationThatHasNoExpirationDate_Is403_WithNoReasonGiven()
    {
        var msel = await SeedDeployed();
        var team = await SeedTeam(msel);
        await SeedInvitation(msel, team, i => i.ExpirationDateTime = null);
        var actor = await Actor().SeedAsync();

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

    /// <summary>
    /// The controller parses <c>teamId</c> itself and treats anything unparseable as absent, so a
    /// malformed team in the link is a join to whatever team was invited rather than a 400.
    /// </summary>
    /// <remarks>
    /// The parameter is declared <c>string</c> and fed to <c>Guid.TryParse</c>, whose failure branch
    /// passes <c>null</c> to the service. <c>TryParse(null)</c> also fails, so "no team was asked for"
    /// and "the team asked for was nonsense" are the same request by the time the service sees it -
    /// there is no state in which the two differ, which is why no mutation can redden this test without
    /// reddening every test in the class that omits the parameter. Declaring the parameter <c>Guid?</c>
    /// and letting model binding answer 400 is the fix, and it would redden this test alone.
    /// </remarks>
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
        using var db = NewContext();
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

    /// <summary>
    /// Characterizes the required shape of <c>EmailDomain</c>: it must contain an <c>@</c>, or it matches
    /// nobody.
    /// </summary>
    /// <remarks>
    /// The match is <c>i.EmailDomain.Contains('@') &amp;&amp; email.EndsWith(i.EmailDomain)</c>, so a
    /// domain written the way a domain is normally written - <c>example.test</c> - fails the first half
    /// and the invitation is unusable by everyone, including the addresses it names. The failure is a 403
    /// whose message repeats the domain back, which reads as though the participant's address were the
    /// problem. Nothing validates the field on the way in.
    /// </remarks>
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

    /// <summary>
    /// Characterizes the domain match as case-sensitive.
    /// </summary>
    /// <remarks>
    /// <c>string.EndsWith(string)</c> is culture-sensitive but not case-insensitive, and the domain part
    /// of an email address is not. An identity provider that presents the address as the user typed it -
    /// <c>Someone@Example.test</c> - therefore fails an invitation for <c>@example.test</c>.
    /// </remarks>
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

    /// <summary>
    /// Characterizes two usable invitations on one MSEL as a 500.
    /// </summary>
    /// <remarks>
    /// The final choice is a <c>SingleOrDefault</c>, so when more than one invitation admits the caller it
    /// throws <c>InvalidOperationException</c> rather than picking one. Two unrestricted invitations to
    /// two teams is the obvious way to reach it and is a reasonable thing for an author to have created -
    /// one link per team, both open to anyone - and after it happens <em>nobody</em> can join that MSEL by
    /// any link until one invitation is deactivated. Passing an explicit <c>teamId</c> is the only way
    /// through, because that filter runs earlier.
    /// </remarks>
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

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
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
        using var db = NewContext();
        Assert.True(await db.TeamUsers.AnyAsync(tu => tu.TeamId == second.Id && tu.UserId == actor.Id, Ct));
    }

    /// <summary>
    /// Characterizes the explanation given for an unusable invitation: it describes an arbitrary one of
    /// the MSEL's invitations, not the one the caller was refused by.
    /// </summary>
    /// <remarks>
    /// The reasons are computed from <c>allInvitations.First()</c> - the first row the database happened
    /// to return - after the filter has already rejected every invitation. With two unusable invitations
    /// for different reasons the message names one of them and stays silent about the other, so a
    /// participant reporting "it says the link is expired" may be holding a link that was deactivated.
    /// The assertion is written as "exactly one of the two reasons" because the row order is genuinely
    /// arbitrary: there is no <c>OrderBy</c>.
    /// </remarks>
    [Fact]
    public async Task Join_WithTwoUnusableInvitations_ExplainsOnlyOneOfThem()
    {
        var msel = await SeedDeployed();
        var first = await SeedTeam(msel);
        var second = await SeedTeam(msel);
        await SeedInvitation(msel, first, i => i.WasDeactivated = true);
        await SeedInvitation(msel, second, i => i.ExpirationDateTime = DateTime.UtcNow.AddMinutes(-1));
        var actor = await Actor().SeedAsync();

        var response = await Join(Client(actor), msel.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var title = (await Read<ApiError>(response)).Title;
        Assert.True(
            title.Contains("deactivated") ^ title.Contains("expired"),
            $"Expected exactly one of the two invitations to be explained, got: {title}");
    }

    /// <summary>
    /// Characterizes joining a MSEL that is <c>Deployed</c> but has no Player View: a 500, after the
    /// invitation seat has already been spent.
    /// </summary>
    /// <remarks>
    /// <c>Status</c> and <c>PlayerViewId</c> are set by different steps of the deploy, and nothing keeps
    /// them consistent - <c>PUT api/msels/{id}</c> will write either. The join validates the status and
    /// then returns <c>(Guid)msel.PlayerViewId</c>, so a MSEL marked deployed without a view fails the
    /// cast. What makes it worth pinning is the order: the team row, the MSEL role and the incremented
    /// <c>UserCount</c> are all saved <em>before</em> the cast, and the handoff to the other applications
    /// is queued too, so the seat is consumed by a request that returned a 500. Retrying the link burns
    /// another.
    /// </remarks>
    [Fact]
    public async Task Join_OfADeployedMselWithNoPlayerView_Is500_AfterSpendingTheSeat()
    {
        var msel = await SeedDeployed(m => m.PlayerViewId = null);
        var team = await SeedTeam(msel);
        var invitation = await SeedInvitation(msel, team);
        var actor = await Actor().SeedAsync();

        var response = await Join(Client(actor), msel.Id);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var db = NewContext();
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

        using var db = NewContext();
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

        using var db = NewContext();
        Assert.True(await db.TeamUsers.AnyAsync(tu => tu.TeamId == team.Id && tu.UserId == actor.Id, Ct));
    }

    /// <summary>
    /// Characterizes <c>PlayerService.GetMyViewsAsync</c>'s hard cast: a client that answers with
    /// anything but a <c>List&lt;View&gt;</c> leaves the participant looking new.
    /// </summary>
    /// <remarks>
    /// The line is <c>views = (List&lt;View&gt;)await _playerApiClient.GetUserViewsAsync(...)</c> inside a
    /// <c>try</c> whose <c>catch</c> is empty. <c>GetUserViewsAsync</c> is declared to return an
    /// <c>ICollection&lt;View&gt;</c>, so an array - or any other implementation - is an
    /// <c>InvalidCastException</c>, swallowed, and the caller gets no views rather than an error. Nothing
    /// in Blueprint decides what the Player client returns; today's generated client happens to build a
    /// <c>List</c>, and a regenerated one need not. The visible consequence here is that the
    /// already-joined exception above stops applying and a returning participant is refused by an expired
    /// link.
    /// </remarks>
    [Fact]
    public async Task Join_WhenPlayerAnswersWithAnArrayOfViews_TreatsTheParticipantAsNew()
    {
        var msel = await SeedDeployed();
        var team = await SeedTeam(msel);
        await SeedInvitation(msel, team, i => i.ExpirationDateTime = DateTime.UtcNow.AddMinutes(-1));
        var actor = await Actor().SeedAsync();
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

        var response = await Join(AnonymousClient, msel.Id);

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
        using var db = NewContext();
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

        using var db = NewContext();
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

        using var db = NewContext();
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

        using var db = NewContext();
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

        using var db = NewContext();
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

        using var db = NewContext();
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

    /// <summary>
    /// Characterizes the <c>PlayerViewId</c> in the response: it is a fresh guid that exists nowhere but
    /// the response and the queued push.
    /// </summary>
    /// <remarks>
    /// The clone is saved with a null <c>PlayerViewId</c>; the guid returned to the caller is generated
    /// afterwards and handed to <c>IIntegrationQueue</c>, on the understanding that the background worker
    /// will create a Player View with that id and store it. So the response promises something that has
    /// not happened, and if the push fails the UI polls for a view id the database never learns. A caller
    /// cannot tell the difference between "launching" and "launched" from this response.
    /// </remarks>
    [Fact]
    public async Task Launch_ReturnsAPlayerViewIdThatIsNotYetStored()
    {
        var template = await SeedTemplate();
        var team = await SeedTeam(template);
        await SeedInvitation(template, team);
        var actor = await Actor().SeedAsync();

        var launched = await Read<Msel>(await Launch(ClientWithEmail(actor, "a@example.test"), template.Id));

        Assert.NotNull(launched.PlayerViewId);
        using var db = NewContext();
        Assert.Null((await db.Msels.SingleAsync(m => m.Id == launched.Id, Ct)).PlayerViewId);
    }

    [Fact]
    public async Task Launch_OfAnUnknownMsel_Is404()
    {
        var actor = await Actor().SeedAsync();

        var response = await Launch(ClientWithEmail(actor, "a@example.test"), Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Characterizes a launch with no email claim: a 500 rather than the 403 the join path gives.
    /// </summary>
    /// <remarks>
    /// The line is <c>_user.Claims.First(c =&gt; c.Type == "email")?.Value</c>. The null-conditional says
    /// the author expected a missing claim to yield null, but <c>First</c> throws before it is reached -
    /// the operator can only ever apply to a claim that was found, so it is dead. <c>FirstOrDefault</c> is
    /// the intended call and is what the join path uses. Until then, an identity provider not configured
    /// to release the email scope turns every launch into a server error.
    /// </remarks>
    [Fact]
    public async Task Launch_WithNoEmailClaim_Is500()
    {
        var template = await SeedTemplate();
        var team = await SeedTeam(template);
        await SeedInvitation(template, team);
        var actor = await Actor().SeedAsync();

        var response = await Launch(Client(actor), template.Id);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
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

    /// <summary>
    /// Unlike the join path, launch does not care what status the template is in.
    /// </summary>
    /// <remarks>
    /// Join requires <c>Deployed</c>; launch checks only <c>IsTemplate</c>. So a template still being
    /// written - <c>Pending</c>, the status every new MSEL starts in - can be launched into a live
    /// exercise by anyone holding a link, and the author gets no say in when it becomes launchable. This
    /// is characterization: the test asserts the 200.
    /// </remarks>
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

    /// <summary>
    /// Characterizes launch as repeatable: one link clones the template again on every use.
    /// </summary>
    /// <remarks>
    /// Launch has no equivalent of the join path's "already in the Player View" check, and nothing records
    /// that a given user has launched. The only limit is <c>MaxUsersAllowed</c>, which is counted in uses
    /// rather than users - so a link with ten seats lets one participant create ten live exercises, each a
    /// full clone of the template and each pushed to Player, Gallery and CITE.
    /// </remarks>
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
        using var db = NewContext();
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

        var response = await Launch(AnonymousClient, template.Id);

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
        var msel = BlueprintAppFactory.Msel(status: MselItemStatus.Deployed);
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
        var msel = BlueprintAppFactory.Msel(isTemplate: true);
        arrange?.Invoke(msel);
        await Seed(msel);

        return msel;
    }

    private async Task<TeamEntity> SeedTeam(MselEntity msel, Action<TeamEntity> arrange = null)
    {
        var team = BlueprintAppFactory.Team(msel.Id, msel.CreatedBy);
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
