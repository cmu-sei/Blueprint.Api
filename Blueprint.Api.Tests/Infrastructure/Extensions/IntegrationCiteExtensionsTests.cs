// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Tests.Support;
using Cite.Api.Client;
using Xunit;

// The generated CITE client declares an Exception, a Team, a User and a Move of its own, and an Action
// that clashes with System.Action - which is why production spells out Cite.Api.Client.Action.
using Exception = System.Exception;
using MoveEntity = Blueprint.Api.Data.Models.MoveEntity;

namespace Blueprint.Api.Tests.Infrastructure.Extensions;

/// <summary><c>IntegrationCiteExtensions</c> - the thirteen calls blueprint makes to cite.api when a MSEL
/// is pushed, pulled, activated or joined.</summary>
public class IntegrationCiteExtensionsTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    /// <summary>Starts describing an actor to seed over this test's database.</summary>
    private TestActorBuilder Actor() => new(Db, Ct);

    // ---------------------------------------------------------------------------------------------
    // GetCiteApiClient and PullFromCiteAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetCiteApiClient_BuildsAClientCarryingTheTokenAndTheApiUrl()
    {
        var handler = new SiblingApiHandler().Answers($"api/evaluations/{EvaluationId}", HttpStatusCode.NoContent);
        var client = IntegrationCiteExtensions.GetCiteApiClient(
            handler.AsFactory(), "http://cite.example/", await Tokens.Bearer());

        await IntegrationCiteExtensions.PullFromCiteAsync(EvaluationId, client, Ct);

        var sent = Assert.Single(handler.Sent);

        Assert.Equal($"api/evaluations/{EvaluationId}", sent.Path);
        Assert.Equal(Tokens.Header, sent.Authorization);
    }

    [Fact]
    public async Task PullFromCite_DeletesTheEvaluation()
    {
        var handler = new SiblingApiHandler().Answers($"api/evaluations/{EvaluationId}", HttpStatusCode.NoContent);

        await IntegrationCiteExtensions.PullFromCiteAsync(EvaluationId, Client(handler), Ct);

        var sent = Assert.Single(handler.Sent);

        Assert.Equal(HttpMethod.Delete, sent.Method);
        Assert.Equal($"api/evaluations/{EvaluationId}", sent.Path);
    }

    /// <summary>A refused delete is swallowed by an empty <c>catch</c>.</summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task PullFromCite_SwallowsARefusal(HttpStatusCode status)
    {
        var handler = new SiblingApiHandler().Answers($"api/evaluations/{EvaluationId}", status);

        await IntegrationCiteExtensions.PullFromCiteAsync(EvaluationId, Client(handler), Ct);

        Assert.Single(handler.Sent);
    }

    [Fact]
    public async Task PullFromCite_SwallowsAnUnreachableCite()
    {
        var handler = new SiblingApiHandler().Throws($"api/evaluations/{EvaluationId}");

        await IntegrationCiteExtensions.PullFromCiteAsync(EvaluationId, Client(handler), Ct);

        Assert.Single(handler.Sent);
    }

    // ---------------------------------------------------------------------------------------------
    // CreateEvaluationAsync
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// The evaluation's <em>description</em> is the MSEL's <em>name</em>; CITE's <c>Evaluation</c> has no
    /// name of its own, so this is the only place the MSEL's title appears in CITE.
    /// </remarks>
    [Fact]
    public async Task CreateEvaluation_PostsTheEvaluationNamedAfterTheMsel()
    {
        var msel = Msel();
        var handler = Evaluations();

        await IntegrationCiteExtensions.CreateEvaluationAsync(msel, Client(handler), null, Ct);

        Assert.Equal("api/evaluations", handler.Sent[0].Path);
        Assert.Equal(HttpMethod.Post, handler.Sent[0].Method);

        var body = Body(handler.Sent[0].Body);

        Assert.Equal(EvaluationId.ToString(), body["id"].GetString());
        Assert.Equal(msel.Name, body["description"].GetString());
        Assert.Equal(ScoringModelId.ToString(), body["scoringModelId"].GetString());
        Assert.Equal(ExhibitId.ToString(), body["galleryExhibitId"].GetString());
    }

    [Fact]
    public async Task CreateEvaluation_AlwaysAsksForAPendingEvaluationAtMoveZero()
    {
        var handler = Evaluations();

        await IntegrationCiteExtensions.CreateEvaluationAsync(Msel(), Client(handler), null, Ct);

        var body = Body(handler.Sent[0].Body);

        Assert.Equal("Pending", body["status"].GetString());
        Assert.Equal(0, body["currentMoveNumber"].GetInt32());
    }

    /// <remarks>
    /// The placeholder a MSEL with no move 0 gets. It is the only string in the integration layer that a
    /// user will read without having written it.
    /// </remarks>
    [Fact]
    public async Task CreateEvaluation_WithNoMoveZero_UsesAPlaceholderSituationDescription()
    {
        var msel = Msel();
        msel.Moves.Add(Move(1));

        var handler = Evaluations();

        await IntegrationCiteExtensions.CreateEvaluationAsync(msel, Client(handler), null, Ct);

        Assert.Equal(
            "Preparing for the start of the exercise.",
            Body(handler.Sent[0].Body)["situationDescription"].GetString());
    }

    [Fact]
    public async Task CreateEvaluation_WithAMoveZero_TakesItsSituationFromIt()
    {
        var msel = Msel();
        var situationTime = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        msel.Moves.Add(Move(0, "The grid is down", situationTime));

        var handler = Evaluations();

        await IntegrationCiteExtensions.CreateEvaluationAsync(msel, Client(handler), null, Ct);

        var body = Body(handler.Sent[0].Body);

        Assert.Equal("The grid is down", body["situationDescription"].GetString());
        Assert.Equal(situationTime, body["situationTime"].GetDateTimeOffset().UtcDateTime);
    }

    /// <remarks>
    /// CITE creates a move 0 of its own with every new evaluation, and blueprint immediately deletes it so
    /// that <c>CreateMovesAsync</c> can put the MSEL's own moves in. Two requests, in that order, from one
    /// call.
    /// </remarks>
    [Fact]
    public async Task CreateEvaluation_DeletesTheDefaultMoveCiteCreated()
    {
        var handler = Evaluations();

        await IntegrationCiteExtensions.CreateEvaluationAsync(Msel(), Client(handler), null, Ct);

        Assert.Equal(["api/evaluations", $"api/moves/{DefaultMoveId}"], handler.Paths);
        Assert.Equal(HttpMethod.Delete, handler.Sent[1].Method);
    }

    /// <summary>An answer with no moves throws after the evaluation was created in CITE.</summary>
    [Fact]
    public async Task CreateEvaluation_WhenCiteAnswersWithNoMoves_ThrowsAfterCreatingTheEvaluation()
    {
        var handler = new SiblingApiHandler().AnswersJson(
            "api/evaluations",
            $$"""{"id":"{{EvaluationId}}","description":"e","moves":[]}""",
            HttpStatusCode.Created);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IntegrationCiteExtensions.CreateEvaluationAsync(Msel(), Client(handler), null, Ct));

        Assert.Equal("api/evaluations", Assert.Single(handler.Paths));
    }

    /// <summary>An answer with two moves throws from <c>Single()</c>.</summary>
    [Fact]
    public async Task CreateEvaluation_WhenCiteAnswersWithTwoMoves_Throws()
    {
        var handler = new SiblingApiHandler()
            .AnswersJson(
                "api/evaluations",
                $$"""{"id":"{{EvaluationId}}","moves":[{"id":"{{DefaultMoveId}}"},{"id":"{{EvaluationId}}"}]}""",
                HttpStatusCode.Created)
            .Answers($"api/moves/{DefaultMoveId}", HttpStatusCode.NoContent);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IntegrationCiteExtensions.CreateEvaluationAsync(Msel(), Client(handler), null, Ct));

        Assert.Equal("Sequence contains more than one element", thrown.Message);
    }

    /// <summary>A MSEL with two moves numbered 0 throws before any request is sent.</summary>
    [Fact]
    public async Task CreateEvaluation_WithTwoMoveZeroes_ThrowsBeforeSending()
    {
        var msel = Msel();
        msel.Moves.Add(Move(0));
        msel.Moves.Add(Move(0));

        var handler = Evaluations();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IntegrationCiteExtensions.CreateEvaluationAsync(msel, Client(handler), null, Ct));

        Assert.Empty(handler.Sent);
    }

    /// <summary>Create evaluation with a move zero having no situation time throws.</summary>
    [Fact]
    public async Task CreateEvaluation_WithAMoveZeroHavingNoSituationTime_Throws()
    {
        var msel = Msel();
        var move = Move(0);
        move.SituationTime = null;
        msel.Moves.Add(move);

        var handler = Evaluations();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IntegrationCiteExtensions.CreateEvaluationAsync(msel, Client(handler), null, Ct));

        Assert.Equal("Nullable object must have a value.", thrown.Message);
        Assert.Empty(handler.Sent);
    }

    [Fact]
    public async Task CreateEvaluation_WithNoScoringModel_ThrowsBeforeSending()
    {
        var msel = Msel();
        msel.CiteScoringModelId = null;

        var handler = Evaluations();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IntegrationCiteExtensions.CreateEvaluationAsync(msel, Client(handler), null, Ct));

        Assert.Empty(handler.Sent);
    }

    [Fact]
    public async Task CreateEvaluation_ReturnsWhatCiteAnswered()
    {
        var answered = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var handler = new SiblingApiHandler()
            .AnswersJson(
                "api/evaluations",
                $$"""{"id":"{{answered}}","description":"answered","moves":[{"id":"{{DefaultMoveId}}"}]}""",
                HttpStatusCode.Created)
            .Answers($"api/moves/{DefaultMoveId}", HttpStatusCode.NoContent);

        var evaluation = await IntegrationCiteExtensions.CreateEvaluationAsync(
            Msel(), Client(handler), null, Ct);

        Assert.Equal(answered, evaluation.Id);
    }

    // ---------------------------------------------------------------------------------------------
    // ActivateAsync
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// The whole method: one <c>PUT</c> of whatever it was handed, to that object's own id. It exists so
    /// that <c>IntegrationService</c> can set the evaluation's status to Active once everything else has
    /// been pushed, and it neither reads nor changes anything itself.
    /// </remarks>
    [Fact]
    public async Task Activate_PutsTheEvaluationBackToItsOwnId()
    {
        var evaluation = new Evaluation
        {
            Id = EvaluationId,
            Description = "the exercise",
            Status = Cite.Api.Client.ItemStatus.Active
        };

        var handler = new SiblingApiHandler().AnswersJson($"api/evaluations/{EvaluationId}", EvaluationJson);

        await IntegrationCiteExtensions.ActivateAsync(evaluation, Client(handler), null, Ct);

        var sent = Assert.Single(handler.Sent);

        Assert.Equal(HttpMethod.Put, sent.Method);
        Assert.Equal($"api/evaluations/{EvaluationId}", sent.Path);
        Assert.Equal("Active", Body(sent.Body)["status"].GetString());
    }

    // ---------------------------------------------------------------------------------------------
    // CreateMovesAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateMoves_PostsEachMoveUnderTheEvaluation()
    {
        var msel = Msel();
        var situationTime = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        msel.Moves.Add(Move(2, "Second move", situationTime));

        var handler = Moves();

        await IntegrationCiteExtensions.CreateMovesAsync(msel, Client(handler), null, 10, Ct);

        var sent = Assert.Single(handler.Sent);

        Assert.Equal("api/moves", sent.Path);

        var body = Body(sent.Body);

        Assert.Equal(EvaluationId.ToString(), body["evaluationId"].GetString());
        Assert.Equal(2, body["moveNumber"].GetInt32());
        Assert.Equal("Second move", body["description"].GetString());
        Assert.Equal("Second move", body["situationDescription"].GetString());
        Assert.Equal(situationTime, body["situationTime"].GetDateTimeOffset().UtcDateTime);
    }

    /// <summary>A move with no situation time throws.</summary>
    [Fact]
    public async Task CreateMoves_ForAMoveWithNoSituationTime_Throws()
    {
        var msel = Msel();
        var move = Move(1);
        move.SituationTime = null;
        msel.Moves.Add(move);

        var handler = Moves();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IntegrationCiteExtensions.CreateMovesAsync(msel, Client(handler), null, 10, Ct));
    }

    /// <summary>Moves are posted <c>batchSize</c> at a time.</summary>
    [Fact]
    public async Task CreateMoves_HonoursTheBatchSize()
    {
        var msel = Msel();

        for (var i = 0; i < 4; i++)
        {
            msel.Moves.Add(Move(i));
        }

        var handler = new SiblingApiHandler().Holds()
            .AnswersJson("api/moves", MoveJson, HttpStatusCode.Created);

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            IntegrationCiteExtensions.CreateMovesAsync(msel, Client(handler), null, 2, bounded.Token));

        Assert.Equal(2, handler.MaxInFlight);
    }

    [Fact]
    public async Task CreateMoves_WithNoMoves_SendsNothing()
    {
        var handler = Moves();

        await IntegrationCiteExtensions.CreateMovesAsync(Msel(), Client(handler), null, 10, Ct);

        Assert.Empty(handler.Sent);
    }

    // ---------------------------------------------------------------------------------------------
    // CreateTeamsAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateTeams_CreatesEachCiteTeamUnderTheEvaluation()
    {
        var msel = Msel();
        var team = Team(msel.Id);
        msel.Teams.Add(team);

        var handler = Teams();

        await IntegrationCiteExtensions.CreateTeamsAsync(msel, Client(handler), null, [], Ct);

        Assert.Equal("api/team-roles", handler.Sent[0].Path);
        Assert.Equal("api/teams", handler.Sent[1].Path);

        var body = Body(handler.Sent[1].Body);

        Assert.Equal(team.Id.ToString(), body["id"].GetString());
        Assert.Equal(team.Name, body["name"].GetString());
        Assert.Equal(team.ShortName, body["shortName"].GetString());
        Assert.Equal(EvaluationId.ToString(), body["evaluationId"].GetString());
        Assert.Equal(TeamTypeId.ToString(), body["teamTypeId"].GetString());
    }

    /// <summary>A team with no CITE team type is left out, with nothing logged.</summary>
    [Fact]
    public async Task CreateTeams_SkipsATeamWithNoCiteTeamTypeSilently()
    {
        var msel = Msel();
        var team = Team(msel.Id);
        team.CiteTeamTypeId = null;
        msel.Teams.Add(team);

        var handler = Teams();

        await IntegrationCiteExtensions.CreateTeamsAsync(msel, Client(handler), null, [], Ct);

        Assert.Equal("api/team-roles", Assert.Single(handler.Paths));
    }

    [Fact]
    public async Task CreateTeams_CreatesEachTeamsUsersAndMemberships()
    {
        var msel = Msel();
        var team = Team(msel.Id);
        var user = User("Ada");
        team.TeamUsers.Add(new TeamUserEntity(user.Id, team.Id) { User = user });
        msel.Teams.Add(team);

        var handler = Teams();

        await IntegrationCiteExtensions.CreateTeamsAsync(msel, Client(handler), null, [], Ct);

        Assert.Equal(
            ["api/team-roles", "api/teams", "api/users", $"api/teams/{CiteTeamId}/memberships"],
            handler.Paths);
        Assert.Equal("Ada", Body(handler.Sent[2].Body)["name"].GetString());
        Assert.Equal(user.Id.ToString(), Body(handler.Sent[3].Body)["userId"].GetString());
    }

    /// <remarks>
    /// The membership is created against the id CITE answered with, and posted to that id's route - so a
    /// CITE that renumbers the team still gets consistent memberships, as Gallery's does.
    /// </remarks>
    [Fact]
    public async Task CreateTeams_UsesTheTeamIdCiteAnsweredWith()
    {
        var msel = Msel();
        var team = Team(msel.Id);
        var user = User();
        team.TeamUsers.Add(new TeamUserEntity(user.Id, team.Id) { User = user });
        msel.Teams.Add(team);

        var handler = Teams();

        await IntegrationCiteExtensions.CreateTeamsAsync(msel, Client(handler), null, [], Ct);

        Assert.Equal(CiteTeamId.ToString(), Body(handler.Sent[^1].Body)["teamId"].GetString());
        Assert.NotEqual(team.Id, CiteTeamId);
    }

    [Fact]
    public async Task CreateTeams_SkipsCreatingAUserCiteAlreadyHas()
    {
        var msel = Msel();
        var team = Team(msel.Id);
        var user = User();
        team.TeamUsers.Add(new TeamUserEntity(user.Id, team.Id) { User = user });
        msel.Teams.Add(team);

        var handler = Teams();

        await IntegrationCiteExtensions.CreateTeamsAsync(msel, Client(handler), null, [user.Id], Ct);

        Assert.DoesNotContain("api/users", handler.Paths);
    }

    [Fact]
    public async Task CreateTeams_RecordsACreatedUserSoTheNextTeamDoesNotCreateThemAgain()
    {
        var msel = Msel();
        var user = User();

        foreach (var team in new[] { Team(msel.Id), Team(msel.Id) })
        {
            team.TeamUsers.Add(new TeamUserEntity(user.Id, team.Id) { User = user });
            msel.Teams.Add(team);
        }

        var handler = Teams();
        var seen = new HashSet<Guid>();

        await IntegrationCiteExtensions.CreateTeamsAsync(msel, Client(handler), null, seen, Ct);

        Assert.Single(handler.Paths.Where(x => x == "api/users").ToList());
        Assert.Single(seen);
    }

    /// <remarks>
    /// <c>UserTeamRole.Role</c> is a free-text column, matched by name against CITE's own team roles. This
    /// is the one place a blueprint team role reaches CITE.
    /// </remarks>
    [Fact]
    public async Task CreateTeams_MapsTheTeamRoleNameToCitesRoleId()
    {
        var msel = Msel();
        var team = Team(msel.Id);
        var user = User();
        team.TeamUsers.Add(new TeamUserEntity(user.Id, team.Id) { User = user });
        team.UserTeamRoles.Add(new UserTeamRoleEntity(user.Id, team.Id, "Inviter") { Id = Guid.NewGuid() });
        msel.Teams.Add(team);

        var handler = Teams();

        await IntegrationCiteExtensions.CreateTeamsAsync(msel, Client(handler), null, [], Ct);

        Assert.Equal(InviterRoleId.ToString(), Body(handler.Sent[^1].Body)["roleId"].GetString());
    }

    /// <summary>Create teams for a role name CITE does not know creates the membership with no role.</summary>
    [Theory]
    [InlineData("NoSuchRole")]
    [InlineData(null)]
    public async Task CreateTeams_ForARoleNameCiteDoesNotKnow_CreatesTheMembershipWithNoRole(string role)
    {
        var msel = Msel();
        var team = Team(msel.Id);
        var user = User();
        team.TeamUsers.Add(new TeamUserEntity(user.Id, team.Id) { User = user });

        if (role is not null)
        {
            team.UserTeamRoles.Add(new UserTeamRoleEntity(user.Id, team.Id, role) { Id = Guid.NewGuid() });
        }

        msel.Teams.Add(team);

        var handler = Teams();

        await IntegrationCiteExtensions.CreateTeamsAsync(msel, Client(handler), null, [], Ct);

        var membership = Body(handler.Sent[^1].Body);

        Assert.Equal(JsonValueKind.Null, membership["roleId"].ValueKind);
        Assert.Equal(user.Id.ToString(), membership["userId"].GetString());
    }

    /// <summary>A refused team membership is swallowed and the push carries on.</summary>
    [Fact]
    public async Task CreateTeams_SwallowsARefusedMembershipAndCarriesOn()
    {
        var msel = Msel();
        var team = Team(msel.Id);

        foreach (var user in new[] { User(), User() })
        {
            team.TeamUsers.Add(new TeamUserEntity(user.Id, team.Id) { User = user });
        }

        msel.Teams.Add(team);

        var handler = new SiblingApiHandler()
            .AnswersJson("api/team-roles", TeamRolesJson)
            .AnswersJson("api/teams", TeamJson, HttpStatusCode.Created)
            .AnswersJson("api/users", UserJson, HttpStatusCode.Created)
            .Answers($"api/teams/{CiteTeamId}/memberships", HttpStatusCode.Conflict);

        await IntegrationCiteExtensions.CreateTeamsAsync(msel, Client(handler), null, [], Ct);

        Assert.Equal(2, handler.Sent.Count(x => x.Path.EndsWith("memberships")));
    }

    // ---------------------------------------------------------------------------------------------
    // CreateEvaluationMembershipsAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateEvaluationMemberships_ReadsTheRoleListThenPostsOneMembershipPerUser()
    {
        var msel = Msel();
        var ada = Guid.NewGuid();
        var grace = Guid.NewGuid();
        msel.UserMselRoles.Add(RoleFor(msel.Id, ada, "Observer"));
        msel.UserMselRoles.Add(RoleFor(msel.Id, grace, "Participant"));

        var handler = Memberships();

        await IntegrationCiteExtensions.CreateEvaluationMembershipsAsync(msel, Client(handler), null, Ct);

        Assert.Equal("api/evaluation-roles", handler.Sent[0].Path);
        Assert.Equal($"api/evaluations/{EvaluationId}/memberships", handler.Sent[1].Path);
        Assert.Equal(EvaluationId.ToString(), Body(handler.Sent[1].Body)["evaluationId"].GetString());

        var posted = handler.Sent
            .Skip(1)
            .Select(x => Body(x.Body))
            .ToDictionary(x => x["userId"].GetString(), x => x["roleId"].GetString());

        Assert.Equal(ObserverRoleId.ToString(), posted[ada.ToString()]);
        Assert.Equal(ParticipantRoleId.ToString(), posted[grace.ToString()]);
    }

    [Fact]
    public async Task CreateEvaluationMemberships_SkipsARoleWithNoCiteName()
    {
        var msel = Msel();
        msel.UserMselRoles.Add(RoleFor(msel.Id, Guid.NewGuid(), null));
        msel.UserMselRoles.Add(RoleFor(msel.Id, Guid.NewGuid(), string.Empty));

        var handler = Memberships();

        await IntegrationCiteExtensions.CreateEvaluationMembershipsAsync(msel, Client(handler), null, Ct);

        Assert.Equal("api/evaluation-roles", Assert.Single(handler.Paths));
    }

    /// <summary>A role name CITE does not have creates no evaluation membership.</summary>
    [Fact]
    public async Task CreateEvaluationMemberships_SkipsARoleNameCiteDoesNotHave()
    {
        var msel = Msel();
        msel.UserMselRoles.Add(RoleFor(msel.Id, Guid.NewGuid(), "NoSuchRole"));

        var handler = Memberships();

        await IntegrationCiteExtensions.CreateEvaluationMembershipsAsync(msel, Client(handler), null, Ct);

        Assert.Equal("api/evaluation-roles", Assert.Single(handler.Paths));
    }

    [Fact]
    public async Task CreateEvaluationMemberships_ForAUserWithTwoRoles_PostsOneMembership()
    {
        var msel = Msel();
        var ada = Guid.NewGuid();
        msel.UserMselRoles.Add(RoleFor(msel.Id, ada, "Observer"));
        msel.UserMselRoles.Add(RoleFor(msel.Id, ada, "Participant"));

        var handler = Memberships();

        await IntegrationCiteExtensions.CreateEvaluationMembershipsAsync(msel, Client(handler), null, Ct);

        var membership = Body(Assert.Single(handler.Sent.Skip(1).ToList()).Body);

        Assert.Equal(ada.ToString(), membership["userId"].GetString());
        Assert.Contains(
            membership["roleId"].GetString(),
            new[] { ObserverRoleId.ToString(), ParticipantRoleId.ToString() });
    }

    [Fact]
    public async Task CreateEvaluationMemberships_SwallowsAFailedMembershipAndCarriesOn()
    {
        var msel = Msel();
        msel.UserMselRoles.Add(RoleFor(msel.Id, Guid.NewGuid(), "Observer"));
        msel.UserMselRoles.Add(RoleFor(msel.Id, Guid.NewGuid(), "Participant"));

        var handler = new SiblingApiHandler()
            .AnswersJson("api/evaluation-roles", EvaluationRolesJson)
            .Answers($"api/evaluations/{EvaluationId}/memberships", HttpStatusCode.Conflict);

        await IntegrationCiteExtensions.CreateEvaluationMembershipsAsync(msel, Client(handler), null, Ct);

        Assert.Equal(2, handler.Sent.Count(x => x.Path.EndsWith("memberships")));
    }

    // ---------------------------------------------------------------------------------------------
    // CreateDutiesAsync and CreateActionsAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateDuties_PostsEachDutyUnderTheEvaluation()
    {
        var msel = Msel();
        var team = Team(msel.Id);
        msel.Teams.Add(team);
        msel.CiteDuties.Add(Duty(msel.Id, team.Id, "Watch officer"));

        var handler = Duties();

        await IntegrationCiteExtensions.CreateDutiesAsync(msel, Client(handler), null, 10, Ct);

        var sent = Assert.Single(handler.Sent);

        Assert.Equal("api/duties", sent.Path);

        var body = Body(sent.Body);

        Assert.Equal(EvaluationId.ToString(), body["evaluationId"].GetString());
        Assert.Equal("Watch officer", body["name"].GetString());
        Assert.Equal(team.Id.ToString(), body["teamId"].GetString());
    }

    /// <summary>A duty whose team has no CITE team type, or names no team on the MSEL, is not sent.</summary>
    [Fact]
    public async Task CreateDuties_SkipsADutyWhoseTeamIsNotACiteTeam()
    {
        var msel = Msel();
        var citeTeam = Team(msel.Id);
        var plainTeam = Team(msel.Id);
        plainTeam.CiteTeamTypeId = null;
        msel.Teams.Add(citeTeam);
        msel.Teams.Add(plainTeam);

        msel.CiteDuties.Add(Duty(msel.Id, citeTeam.Id, "kept"));
        msel.CiteDuties.Add(Duty(msel.Id, plainTeam.Id, "dropped"));
        msel.CiteDuties.Add(Duty(msel.Id, Guid.NewGuid(), "orphan"));

        var handler = Duties();

        await IntegrationCiteExtensions.CreateDutiesAsync(msel, Client(handler), null, 10, Ct);

        Assert.Equal("kept", Body(Assert.Single(handler.Sent).Body)["name"].GetString());
    }

    [Fact]
    public async Task CreateDuties_HonoursTheBatchSize()
    {
        var msel = Msel();
        var team = Team(msel.Id);
        msel.Teams.Add(team);

        for (var i = 0; i < 4; i++)
        {
            msel.CiteDuties.Add(Duty(msel.Id, team.Id, $"duty-{i}"));
        }

        var handler = new SiblingApiHandler().Holds().AnswersJson("api/duties", DutyJson, HttpStatusCode.Created);

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            IntegrationCiteExtensions.CreateDutiesAsync(msel, Client(handler), null, 2, bounded.Token));

        Assert.Equal(2, handler.MaxInFlight);
    }

    [Fact]
    public async Task CreateActions_PostsEachActionUnderTheEvaluation()
    {
        var msel = Msel();
        var team = Team(msel.Id);
        msel.Teams.Add(team);
        msel.CiteActions.Add(Action(msel.Id, team.Id, moveNumber: 2, injectNumber: 3, description: "Report in"));

        var handler = Actions();

        await IntegrationCiteExtensions.CreateActionsAsync(msel, Client(handler), null, 10, Ct);

        var body = Body(Assert.Single(handler.Sent).Body);

        Assert.Equal(EvaluationId.ToString(), body["evaluationId"].GetString());
        Assert.Equal(team.Id.ToString(), body["teamId"].GetString());
        Assert.Equal(2, body["moveNumber"].GetInt32());
        Assert.Equal(3, body["injectNumber"].GetInt32());
        Assert.Equal("Report in", body["description"].GetString());
    }

    /// <summary>Create actions sends no action number.</summary>
    [Fact]
    public async Task CreateActions_SendsNoActionNumber()
    {
        var msel = Msel();
        var team = Team(msel.Id);
        msel.Teams.Add(team);

        var action = Action(msel.Id, team.Id);
        action.ActionNumber = 7;
        msel.CiteActions.Add(action);

        var handler = Actions();

        await IntegrationCiteExtensions.CreateActionsAsync(msel, Client(handler), null, 10, Ct);

        Assert.Equal(0, Body(Assert.Single(handler.Sent).Body)["actionNumber"].GetInt32());
    }

    [Fact]
    public async Task CreateActions_SkipsAnActionWithNoTeamOrWhoseTeamIsNotACiteTeam()
    {
        var msel = Msel();
        var citeTeam = Team(msel.Id);
        var plainTeam = Team(msel.Id);
        plainTeam.CiteTeamTypeId = null;
        msel.Teams.Add(citeTeam);
        msel.Teams.Add(plainTeam);

        msel.CiteActions.Add(Action(msel.Id, citeTeam.Id, description: "kept"));
        msel.CiteActions.Add(Action(msel.Id, plainTeam.Id, description: "dropped"));
        msel.CiteActions.Add(Action(msel.Id, null, description: "teamless"));

        var handler = Actions();

        await IntegrationCiteExtensions.CreateActionsAsync(msel, Client(handler), null, 10, Ct);

        Assert.Equal("kept", Body(Assert.Single(handler.Sent).Body)["description"].GetString());
    }

    [Fact]
    public async Task CreateActions_HonoursTheBatchSize()
    {
        var msel = Msel();
        var team = Team(msel.Id);
        msel.Teams.Add(team);

        for (var i = 0; i < 4; i++)
        {
            msel.CiteActions.Add(Action(msel.Id, team.Id, description: $"action-{i}"));
        }

        var handler = new SiblingApiHandler().Holds().AnswersJson("api/actions", ActionJson, HttpStatusCode.Created);

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            IntegrationCiteExtensions.CreateActionsAsync(msel, Client(handler), null, 2, bounded.Token));

        Assert.Equal(2, handler.MaxInFlight);
    }

    // ---------------------------------------------------------------------------------------------
    // AddUserToTeamAsync - the one method that reads the database
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AddUserToTeam_CreatesTheUserThenAddsThem()
    {
        var actor = await Actor().WithName("Grace").SeedAsync();
        var teamId = Guid.NewGuid();

        var handler = new SiblingApiHandler()
            .AnswersJson("api/users", UserJson, HttpStatusCode.Created)
            .AnswersJson($"api/teams/{teamId}/memberships", MembershipJson, HttpStatusCode.Created);

        await IntegrationCiteExtensions.AddUserToTeamAsync(actor.Id, teamId, Client(handler), Db, Ct);

        Assert.Equal(["api/users", $"api/teams/{teamId}/memberships"], handler.Paths);
        Assert.Equal("Grace", Body(handler.Sent[0].Body)["name"].GetString());

        var membership = Body(handler.Sent[1].Body);

        Assert.Equal(teamId.ToString(), membership["teamId"].GetString());
        Assert.Equal(actor.Id.ToString(), membership["userId"].GetString());
    }

    /// <summary>Add user to team never sends a role.</summary>
    [Fact]
    public async Task AddUserToTeam_NeverSendsARole()
    {
        var actor = await Actor().SeedAsync();
        var teamId = Guid.NewGuid();

        var handler = new SiblingApiHandler()
            .AnswersJson("api/users", UserJson, HttpStatusCode.Created)
            .AnswersJson($"api/teams/{teamId}/memberships", MembershipJson, HttpStatusCode.Created);

        await IntegrationCiteExtensions.AddUserToTeamAsync(actor.Id, teamId, Client(handler), Db, Ct);

        Assert.Equal(JsonValueKind.Null, Body(handler.Sent[^1].Body)["roleId"].ValueKind);
    }

    /// <summary>A user blueprint does not know is added to the team without being created.</summary>
    [Fact]
    public async Task AddUserToTeam_ForSomeoneBlueprintDoesNotKnow_AddsThemWithoutCreatingThem()
    {
        var stranger = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        var handler = new SiblingApiHandler()
            .AnswersJson($"api/teams/{teamId}/memberships", MembershipJson, HttpStatusCode.Created);

        await IntegrationCiteExtensions.AddUserToTeamAsync(stranger, teamId, Client(handler), Db, Ct);

        Assert.Equal($"api/teams/{teamId}/memberships", Assert.Single(handler.Paths));
    }

    [Fact]
    public async Task AddUserToTeam_SwallowsAFailureCreatingTheUser()
    {
        var actor = await Actor().SeedAsync();
        var teamId = Guid.NewGuid();

        var handler = new SiblingApiHandler()
            .Answers("api/users", HttpStatusCode.Conflict)
            .AnswersJson($"api/teams/{teamId}/memberships", MembershipJson, HttpStatusCode.Created);

        await IntegrationCiteExtensions.AddUserToTeamAsync(actor.Id, teamId, Client(handler), Db, Ct);

        Assert.Equal(["api/users", $"api/teams/{teamId}/memberships"], handler.Paths);
    }

    /// <summary>A failure adding the membership escapes; a failure creating the user is swallowed.</summary>
    [Fact]
    public async Task AddUserToTeam_DoesNotSwallowAFailureAddingTheMembership()
    {
        var actor = await Actor().SeedAsync();
        var teamId = Guid.NewGuid();

        var handler = new SiblingApiHandler()
            .AnswersJson("api/users", UserJson, HttpStatusCode.Created)
            .Answers($"api/teams/{teamId}/memberships", HttpStatusCode.Conflict);

        await Assert.ThrowsAnyAsync<ApiException>(() =>
            IntegrationCiteExtensions.AddUserToTeamAsync(actor.Id, teamId, Client(handler), Db, Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static readonly Guid EvaluationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ScoringModelId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ExhibitId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid DefaultMoveId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid TeamTypeId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid CiteTeamId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid InviterRoleId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ObserverRoleId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid ParticipantRoleId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");

    private static readonly string EvaluationJson =
        $$"""{"id":"{{EvaluationId}}","description":"evaluation","status":"Active"}""";
    private static readonly string MoveJson = $$"""{"id":"{{DefaultMoveId}}","moveNumber":1}""";
    private static readonly string TeamJson = $$"""{"id":"{{CiteTeamId}}","name":"team"}""";
    private static readonly string UserJson = $$"""{"id":"{{CiteTeamId}}","name":"user"}""";
    private static readonly string MembershipJson =
        $$"""{"id":"{{CiteTeamId}}","teamId":"{{CiteTeamId}}","userId":"{{CiteTeamId}}"}""";
    private static readonly string DutyJson = $$"""{"id":"{{CiteTeamId}}","name":"duty"}""";
    private static readonly string ActionJson = $$"""{"id":"{{CiteTeamId}}","description":"action"}""";
    private static readonly string TeamRolesJson = $$"""[{"id":"{{InviterRoleId}}","name":"Inviter"}]""";
    private static readonly string EvaluationRolesJson =
        $$"""[{"id":"{{ObserverRoleId}}","name":"Observer"},{"id":"{{ParticipantRoleId}}","name":"Participant"}]""";

    private static CiteApiClient Client(SiblingApiHandler handler) =>
        new(ApiClientsExtensions.GetHttpClient(handler.AsFactory(), "http://cite.example/", null));

    /// <summary>CITE answering a create with one default move, and accepting its deletion.</summary>
    private static SiblingApiHandler Evaluations() =>
        new SiblingApiHandler()
            .AnswersJson(
                "api/evaluations",
                $$"""{"id":"{{EvaluationId}}","description":"e","moves":[{"id":"{{DefaultMoveId}}","moveNumber":0}]}""",
                HttpStatusCode.Created)
            .Answers($"api/moves/{DefaultMoveId}", HttpStatusCode.NoContent);

    private static SiblingApiHandler Moves() =>
        new SiblingApiHandler().AnswersJson("api/moves", MoveJson, HttpStatusCode.Created);

    private static SiblingApiHandler Teams() =>
        new SiblingApiHandler()
            .AnswersJson("api/team-roles", TeamRolesJson)
            .AnswersJson("api/teams", TeamJson, HttpStatusCode.Created)
            .AnswersJson("api/users", UserJson, HttpStatusCode.Created)
            .AnswersJson($"api/teams/{CiteTeamId}/memberships", MembershipJson, HttpStatusCode.Created);

    private static SiblingApiHandler Memberships() =>
        new SiblingApiHandler()
            .AnswersJson("api/evaluation-roles", EvaluationRolesJson)
            .AnswersJson($"api/evaluations/{EvaluationId}/memberships", "{}", HttpStatusCode.Created);

    private static SiblingApiHandler Duties() =>
        new SiblingApiHandler().AnswersJson("api/duties", DutyJson, HttpStatusCode.Created);

    private static SiblingApiHandler Actions() =>
        new SiblingApiHandler().AnswersJson("api/actions", ActionJson, HttpStatusCode.Created);

    /// <summary>
    /// A MSEL pushed as far as having a CITE evaluation, a scoring model and a Gallery exhibit - built in
    /// memory, because seven of these eight methods never read the database.
    /// </summary>
    private static MselEntity Msel()
    {
        var msel = TestData.Msel();

        msel.CiteEvaluationId = EvaluationId;
        msel.CiteScoringModelId = ScoringModelId;
        msel.GalleryExhibitId = ExhibitId;

        return msel;
    }

    /// <remarks>
    /// <c>SituationTime</c> always gets a value here. A move without one is expressed by assigning
    /// <c>null</c> to the property afterwards, not by passing it - an optional parameter coalescing to a
    /// default cannot express "absent", which is the case two of these tests are about.
    /// </remarks>
    private static MoveEntity Move(int moveNumber, string description = null, DateTime? situationTime = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            MoveNumber = moveNumber,
            Description = description ?? $"move-{moveNumber}",
            SituationDescription = description ?? $"move-{moveNumber}",
            SituationTime = situationTime ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedBy = Guid.NewGuid()
        };

    private static TeamEntity Team(Guid mselId)
    {
        var team = TestData.Team(mselId);
        team.CiteTeamTypeId = TeamTypeId;

        return team;
    }

    private static UserEntity User(string name = null)
    {
        var id = Guid.NewGuid();

        return new UserEntity { Id = id, Name = name ?? $"user-{id}" };
    }

    private static CiteDutyEntity Duty(Guid mselId, Guid? teamId, string name) => new()
    {
        Id = Guid.NewGuid(),
        MselId = mselId,
        TeamId = teamId,
        Name = name,
        CreatedBy = Guid.NewGuid()
    };

    private static CiteActionEntity Action(
        Guid mselId, Guid? teamId, int moveNumber = 0, int injectNumber = 0, string description = null) => new()
    {
        Id = Guid.NewGuid(),
        MselId = mselId,
        TeamId = teamId,
        MoveNumber = moveNumber,
        InjectNumber = injectNumber,
        Description = description ?? "action",
        CreatedBy = Guid.NewGuid()
    };

    private static UserMselRoleEntity RoleFor(Guid mselId, Guid userId, string citeRole) => new()
    {
        Id = Guid.NewGuid(),
        MselId = mselId,
        UserId = userId,
        Role = Blueprint.Api.Data.Enumerations.MselRole.Viewer,
        CiteEvaluationRole = citeRole,
        CreatedBy = Guid.NewGuid()
    };

    private static Dictionary<string, JsonElement> Body(string body) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
}
