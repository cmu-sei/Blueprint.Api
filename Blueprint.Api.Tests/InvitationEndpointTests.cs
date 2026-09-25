// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>InvitationService</c> / <c>InvitationController</c> - the five routes over the rows that let
/// somebody join a MSEL's team by link rather than by assignment. Reads want <c>ViewMsels</c> or a
/// MSEL role, writes want <c>EditMsels</c> or MSEL ownership.
/// </summary>
/// <remarks>
/// 401 and 403 for these routes live in <see cref="RouteAuthorizationTests"/>.
/// </remarks>
public class InvitationEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET msels/{mselId}/invitations
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByMsel_WithViewMsels_ReturnsOnlyThatMselsInvitations()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();
        var (msel, team) = await SeedMselAndTeam();
        var (otherMsel, otherTeam) = await SeedMselAndTeam();
        var mine = BlueprintAppFactory.Invitation(msel.Id, team.Id);
        await Seed(mine, BlueprintAppFactory.Invitation(otherMsel.Id, otherTeam.Id));

        var response = await Client(actor).GetAsync($"api/msels/{msel.Id}/invitations", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var invitations = await response.Content.ReadFromJsonAsync<List<ViewModels.Invitation>>(JsonOptions, Ct);
        Assert.Single(invitations);
        Assert.Equal(mine.Id, invitations[0].Id);
    }

    [Fact]
    public async Task GetByMsel_ForAnOwnerOfTheMsel_Is200()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).GetAsync($"api/msels/{msel.Id}/invitations", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"api/msels/{Guid.NewGuid()}/invitations", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // GET invitations/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_WithViewMsels_Is200()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();
        var (msel, team) = await SeedMselAndTeam();
        var invitation = BlueprintAppFactory.Invitation(msel.Id, team.Id, "example.test", 4);
        await Seed(invitation);

        var response = await Client(actor).GetAsync($"api/invitations/{invitation.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answered = await response.Content.ReadFromJsonAsync<ViewModels.Invitation>(JsonOptions, Ct);
        Assert.Equal(invitation.Id, answered.Id);
        Assert.Equal(msel.Id, answered.MselId);
        Assert.Equal(team.Id, answered.TeamId);
        Assert.Equal("example.test", answered.EmailDomain);
        Assert.Equal(4, answered.MaxUsersAllowed);
    }

    /// <remarks>
    /// BUG: the missing invitation is reported as <c>EntityNotFoundException&lt;MselEntity&gt;</c>
    /// (<c>InvitationService.cs:66</c>), so the status is right and the message names the wrong entity -
    /// indistinguishable from the MSEL being gone. Same shape as <c>MselUnitService</c> (<c>977f578</c>).
    /// </remarks>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"api/invitations/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <remarks>
    /// <c>MaxUsersAllowed</c> and <c>UserCount</c> are <c>int</c>, so <c>JsonIntegerConverter</c> writes
    /// them as JSON strings.
    /// </remarks>
    [Fact]
    public async Task Get_SerializesTheIntegersAsStrings()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();
        var (msel, team) = await SeedMselAndTeam();
        var invitation = BlueprintAppFactory.Invitation(msel.Id, team.Id, maxUsersAllowed: 7);
        await Seed(invitation);

        var body = await Client(actor).GetStringAsync($"api/invitations/{invitation.Id}", Ct);

        Assert.Contains("\"maxUsersAllowed\":\"7\"", body);
        Assert.Contains("\"userCount\":\"0\"", body);
    }

    // ---------------------------------------------------------------------------------------------
    // POST invitations
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_WithEditMsels_Is201()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var (msel, team) = await SeedMselAndTeam();

        var response = await Client(actor).PostAsJsonAsync("api/invitations", Body(msel.Id, team.Id), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ViewModels.Invitation>(JsonOptions, Ct);
        Assert.Equal(msel.Id, created.MselId);
        Assert.Equal(team.Id, created.TeamId);
        Assert.EndsWith($"/api/invitations/{created.Id}", response.Headers.Location.ToString());

        var stored = await NewContext().Invitations.SingleOrDefaultAsync(x => x.Id == created.Id, Ct);
        Assert.Equal(team.Id, stored.TeamId);
    }

    [Fact]
    public async Task Create_ForAMselThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var (_, team) = await SeedMselAndTeam();

        var response = await Client(actor).PostAsJsonAsync(
            "api/invitations", Body(Guid.NewGuid(), team.Id), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT invitations/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_WithEditMsels_Is200()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var (msel, team) = await SeedMselAndTeam();
        var invitation = BlueprintAppFactory.Invitation(msel.Id, team.Id, "before.test", 3);
        await Seed(invitation);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/invitations/{invitation.Id}",
            Body(msel.Id, team.Id, "after.test", 9) with { id = invitation.Id, wasDeactivated = true },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await NewContext().Invitations.SingleAsync(x => x.Id == invitation.Id, Ct);
        Assert.Equal("after.test", stored.EmailDomain);
        Assert.Equal(9, stored.MaxUsersAllowed);
        Assert.True(stored.WasDeactivated);
    }

    [Fact]
    public async Task Update_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var (msel, team) = await SeedMselAndTeam();
        var id = Guid.NewGuid();

        var response = await Client(actor).PutAsJsonAsync(
            $"api/invitations/{id}", Body(msel.Id, team.Id) with { id = id }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <remarks>
    /// BUG: <c>UpdateAsync</c> takes its permission decision from the request body's <c>MselId</c>
    /// (<c>InvitationService.cs:102</c>) before it looks the row up, and <c>InvitationProfile</c> maps
    /// <c>MselId</c> both ways - so an owner of any MSEL may edit and steal every invitation of every
    /// other one. Eighth instance of this shape on the branch; <c>DeleteAsync</c> fourteen lines below
    /// is the model to copy. This test turns red when it is fixed.
    /// </remarks>
    [Fact]
    public async Task Update_DecidesFromTheRequestBodyAndStealsTheRow()
    {
        var mine = BlueprintAppFactory.Msel();
        await Seed(mine);
        var actor = await Actor().OnMsel(mine, MselRole.Owner).SeedAsync();
        var (theirs, theirTeam) = await SeedMselAndTeam();
        var invitation = BlueprintAppFactory.Invitation(theirs.Id, theirTeam.Id);
        await Seed(invitation);
        var myTeam = BlueprintAppFactory.Team(mine.Id);
        await Seed(myTeam);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/invitations/{invitation.Id}",
            Body(mine.Id, myTeam.Id, "stolen.test") with { id = invitation.Id },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await NewContext().Invitations.SingleAsync(x => x.Id == invitation.Id, Ct);
        Assert.Equal(mine.Id, stored.MselId);
        Assert.Equal("stolen.test", stored.EmailDomain);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE invitations/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_WithEditMsels_Is204()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var (msel, team) = await SeedMselAndTeam();
        var invitation = BlueprintAppFactory.Invitation(msel.Id, team.Id);
        await Seed(invitation);

        var response = await Client(actor).DeleteAsync($"api/invitations/{invitation.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await NewContext().Invitations.Where(x => x.Id == invitation.Id).ToListAsync(Ct));
    }

    /// <remarks>
    /// <c>DeleteAsync</c> checks existence <em>before</em> the permission, so an unknown id is a clean
    /// 404 for a stranger as well - the contrast with <c>UpdateAsync</c>, which answers 403.
    /// </remarks>
    [Fact]
    public async Task Delete_ForAnIdThatIsNotThere_Is404_EvenForAStranger()
    {
        var stranger = await Actor().SeedAsync();

        var response = await Client(stranger).DeleteAsync($"api/invitations/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <remarks>
    /// The cascade is the migrations', declared from the MSEL.
    /// </remarks>
    [Fact]
    public async Task ADeletedMselTakesItsInvitationsWithIt()
    {
        var (msel, team) = await SeedMselAndTeam();
        var invitation = BlueprintAppFactory.Invitation(msel.Id, team.Id);
        await Seed(invitation);

        var context = NewContext();
        context.Msels.Remove(await context.Msels.SingleAsync(x => x.Id == msel.Id, Ct));
        await context.SaveChangesAsync(Ct);

        Assert.Empty(await NewContext().Invitations.Where(x => x.Id == invitation.Id).ToListAsync(Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // What the writes do not do
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// BUG: no write path calls <c>ServiceUtilities.SetMselModifiedAsync</c>, so inviting people to an
    /// exercise leaves its <c>DateModified</c> untouched. Seventh service in the tier with that gap.
    /// </remarks>
    [Fact]
    public async Task Create_DoesNotMarkTheMselModified()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var (msel, team) = await SeedMselAndTeam();
        var before = (await NewContext().Msels.SingleAsync(x => x.Id == msel.Id, Ct)).DateModified;

        var response = await Client(actor).PostAsJsonAsync("api/invitations", Body(msel.Id, team.Id), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var after = await NewContext().Msels.SingleAsync(x => x.Id == msel.Id, Ct);
        Assert.Equal(before, after.DateModified);
    }

    private async Task<(MselEntity Msel, TeamEntity Team)> SeedMselAndTeam()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);

        return (msel, team);
    }

    private static InvitationBody Body(
        Guid mselId, Guid teamId, string emailDomain = "seeded.test", int maxUsersAllowed = 10) =>
        new()
        {
            mselId = mselId,
            teamId = teamId,
            emailDomain = emailDomain,
            expirationDateTime = DateTime.UtcNow.AddDays(7),
            maxUsersAllowed = maxUsersAllowed,
            userCount = 0,
            isTeamLeader = false,
            wasDeactivated = false
        };

    /// <summary>
    /// The request body, as a record so a case can vary one field with <c>with</c>. Neither
    /// <c>InvitationEntity</c> nor <c>ViewModels.Invitation</c> carries an audit field, so unlike the
    /// membership units' bodies this one needs no <c>dateCreated</c>/<c>createdBy</c>.
    /// </summary>
    private record InvitationBody
    {
        public Guid id { get; init; }
        public Guid mselId { get; init; }
        public Guid teamId { get; init; }
        public string emailDomain { get; init; }
        public DateTime? expirationDateTime { get; init; }
        public int maxUsersAllowed { get; init; }
        public int userCount { get; init; }
        public bool isTeamLeader { get; init; }
        public bool wasDeactivated { get; init; }
    }
}
