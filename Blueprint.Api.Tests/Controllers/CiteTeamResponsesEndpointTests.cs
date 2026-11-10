// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Cite.Api.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

using Move = Cite.Api.Client.Move;
using SystemPermission = Blueprint.Api.Data.Enumerations.SystemPermission;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// <c>GET api/msels/{mselId}/citeSubmissions?teamId=&amp;moveNumber=</c>, and through it
/// <c>CiteSubmissionService</c> - a team's CITE answers for one move, for the Assessor View.
/// </summary>
/// <remarks>
/// CITE is read as the service account rather than with the caller's token, so the CITE half runs over the
/// factory's <see cref="BlueprintAppFactory.OutboundHttp"/>: the identity provider's discovery and token
/// routes, then the generated CITE client for real. Every test uses an evaluation id of its own, so the route
/// rules one test adds never answer another's requests.
/// </remarks>
public class CiteTeamResponsesEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    private const string Realm = "realms/crucible";

    private const string Discovery = """
        {
          "issuer": "http://localhost:8080/realms/crucible",
          "authorization_endpoint": "http://localhost:8080/realms/crucible/protocol/openid-connect/auth",
          "token_endpoint": "http://localhost:8080/realms/crucible/protocol/openid-connect/token",
          "jwks_uri": "http://localhost:8080/realms/crucible/protocol/openid-connect/certs",
          "response_types_supported": ["code"],
          "subject_types_supported": ["public"],
          "id_token_signing_alg_values_supported": ["RS256"]
        }
        """;

    private const string ServiceAccountToken = "service-account-token";

    // ---------------------------------------------------------------------------------------------
    // Who may read
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Anonymously_Is401()
    {
        var response = await Client().GetAsync(Route(Guid.NewGuid(), Guid.NewGuid(), 0), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>The MSEL roles that may open the Assessor View read the answers, as they read its xAPI evidence.</summary>
    [Theory]
    [InlineData(MselRole.Owner)]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.Evaluator)]
    public async Task AnAssessorRoleOnTheMsel_ReadsTheAnswers(MselRole role)
    {
        var graph = await SeedPushedGraph();
        ArrangeCite(graph);
        var actor = await Actor().OnMsel(graph.Msel, role).SeedAsync();

        var responses = await Read(Client(actor), graph.Msel.Id, graph.Team.Id, 1);

        Assert.Equal(CiteTeamResponsesStatus.Answered, responses.Status);
    }

    [Fact]
    public async Task ACallerHoldingViewMsels_ReadsTheAnswers()
    {
        var graph = await SeedPushedGraph();
        ArrangeCite(graph);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var responses = await Read(Client(actor), graph.Msel.Id, graph.Team.Id, 1);

        Assert.Equal(CiteTeamResponsesStatus.Answered, responses.Status);
    }

    [Theory]
    [InlineData(MselRole.Viewer)]
    [InlineData(MselRole.MoveEditor)]
    public async Task ARoleThatCannotAssess_Is403AndCiteIsNotAsked(MselRole role)
    {
        var graph = await SeedPushedGraph();
        ArrangeCite(graph);
        var actor = await Actor().OnMsel(graph.Msel, role).SeedAsync();

        var response = await Client(actor).GetAsync(Route(graph.Msel.Id, graph.Team.Id, 1), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(CiteRequests(graph));
    }

    /// <summary>A participant on the team itself does not read its answers here.</summary>
    [Fact]
    public async Task AParticipantOnTheTeam_Is403AndCiteIsNotAsked()
    {
        var graph = await SeedPushedGraph();
        ArrangeCite(graph);
        var actor = await Actor().OnTeam(graph.Team).SeedAsync();

        var response = await Client(actor).GetAsync(Route(graph.Msel.Id, graph.Team.Id, 1), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(CiteRequests(graph));
    }

    [Theory]
    [InlineData(MselRole.Owner)]
    [InlineData(MselRole.Evaluator)]
    public async Task AnAssessorRoleOnAnotherMsel_Is403AndCiteIsNotAsked(MselRole role)
    {
        var graph = await SeedPushedGraph();
        ArrangeCite(graph);
        var actor = await Actor().OnNewMsel(role).SeedAsync();

        var response = await Client(actor).GetAsync(Route(graph.Msel.Id, graph.Team.Id, 1), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(CiteRequests(graph));
    }

    [Fact]
    public async Task ATeamOfAnotherMsel_Is404()
    {
        var graph = await SeedPushedGraph();
        var other = TestData.Msel();
        await Seed(other);
        var otherTeam = TestData.Team(other.Id);
        otherTeam.CiteTeamTypeId = Guid.NewGuid();
        await Seed(otherTeam);

        var response = await RootClient.GetAsync(Route(graph.Msel.Id, otherTeam.Id, 1), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Nothing to read
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AMselThatDoesNotUseCite_IsNotPushedAndCiteIsNotAsked()
    {
        var graph = await SeedPushedGraph(msel => msel.UseCite = false);
        ArrangeCite(graph);

        var responses = await Read(RootClient, graph.Msel.Id, graph.Team.Id, 1);

        Assert.Equal(CiteTeamResponsesStatus.NotPushed, responses.Status);
        Assert.Empty(CiteRequests(graph));
    }

    [Fact]
    public async Task AMselNotYetPushedToCite_IsNotPushedAndCiteIsNotAsked()
    {
        var graph = await SeedPushedGraph(msel => msel.CiteEvaluationId = null);
        var tokenRequests = TokenRequests();

        var responses = await Read(RootClient, graph.Msel.Id, graph.Team.Id, 1);

        Assert.Equal(CiteTeamResponsesStatus.NotPushed, responses.Status);
        Assert.NotEmpty(responses.Message);
        // no service account token is asked for, so nothing is sent to CITE
        Assert.Equal(tokenRequests, TokenRequests());
    }

    /// <summary>The push makes a CITE team only for a team with a CITE team type.</summary>
    [Fact]
    public async Task ATeamWithNoCiteTeamType_IsTeamNotInCiteAndCiteIsNotAsked()
    {
        var graph = await SeedPushedGraph();
        ArrangeCite(graph);
        var untyped = TestData.Team(graph.Msel.Id);
        await Seed(untyped);

        var responses = await Read(RootClient, graph.Msel.Id, untyped.Id, 1);

        Assert.Equal(CiteTeamResponsesStatus.TeamNotInCite, responses.Status);
        Assert.Empty(CiteRequests(graph));
    }

    [Fact]
    public async Task AnEvaluationCiteNoLongerHas_IsNotPushed()
    {
        var graph = await SeedPushedGraph();
        ArrangeToken();
        Factory.OutboundHttp.Answers($"GET api/evaluations/{graph.EvaluationId}", HttpStatusCode.NotFound);

        var responses = await Read(RootClient, graph.Msel.Id, graph.Team.Id, 1);

        Assert.Equal(CiteTeamResponsesStatus.NotPushed, responses.Status);
    }

    /// <summary>A move added after the push has no CITE move, which is said rather than shown as unanswered.</summary>
    [Fact]
    public async Task AMoveAddedAfterThePush_IsMoveNotInCiteAndNoSubmissionsAreRead()
    {
        var graph = await SeedPushedGraph();
        ArrangeCite(graph);

        var responses = await Read(RootClient, graph.Msel.Id, graph.Team.Id, 5);

        Assert.Equal(CiteTeamResponsesStatus.MoveNotInCite, responses.Status);
        Assert.Contains("No CITE move 5", responses.Message);
        Assert.Null(responses.TeamSubmission);
        Assert.DoesNotContain(Factory.OutboundHttp.Paths, p => p.Contains($"{graph.EvaluationId}/teams/"));
    }

    [Fact]
    public async Task AMoveCiteHasNotReached_IsMoveNotStarted()
    {
        var graph = await SeedPushedGraph();
        ArrangeCite(graph, currentMoveNumber: 0);

        var responses = await Read(RootClient, graph.Msel.Id, graph.Team.Id, 1);

        Assert.Equal(CiteTeamResponsesStatus.MoveNotStarted, responses.Status);
        Assert.Equal(0, responses.CurrentMoveNumber);
    }

    [Fact]
    public async Task ATeamWithNoSubmissionForTheMove_IsNotAnswered()
    {
        var graph = await SeedPushedGraph();
        ArrangeCite(graph, submissions: []);

        var responses = await Read(RootClient, graph.Msel.Id, graph.Team.Id, 1);

        Assert.Equal(CiteTeamResponsesStatus.NotAnswered, responses.Status);
        Assert.Null(responses.TeamSubmission);
    }

    /// <summary>CITE makes a submission before anything is chosen in it; nothing chosen is not answered.</summary>
    [Fact]
    public async Task ATeamSubmissionWithNothingSelected_IsNotAnswered()
    {
        var graph = await SeedPushedGraph();
        var blank = TeamSubmission(graph, 1, selectedOptionIds: [], comments: []);
        ArrangeCite(graph, submissions: [blank]);

        var responses = await Read(RootClient, graph.Msel.Id, graph.Team.Id, 1);

        Assert.Equal(CiteTeamResponsesStatus.NotAnswered, responses.Status);
        Assert.NotNull(responses.TeamSubmission);
        Assert.All(responses.TeamSubmission.Categories, c => Assert.Empty(c.SelectedOptions));
    }

    // ---------------------------------------------------------------------------------------------
    // What is read
    // ---------------------------------------------------------------------------------------------

    /// <summary>CITE is asked as the service account, with its token, not the caller's.</summary>
    [Fact]
    public async Task Cite_IsAskedWithTheServiceAccountToken()
    {
        var graph = await SeedPushedGraph();
        ArrangeCite(graph);

        await Read(RootClient, graph.Msel.Id, graph.Team.Id, 1);

        var sent = Factory.OutboundHttp.Sent.Where(x => x.Path.Contains(graph.EvaluationId.ToString())).ToList();
        Assert.NotEmpty(sent);
        Assert.All(sent, x => Assert.Equal($"Bearer {ServiceAccountToken}", x.Authorization));
    }

    [Fact]
    public async Task TheTeamSubmission_IsResolvedToTheScoringModelsText()
    {
        var graph = await SeedPushedGraph();
        ArrangeCite(graph);

        var responses = await Read(RootClient, graph.Msel.Id, graph.Team.Id, 1);

        Assert.Equal(CiteTeamResponsesStatus.Answered, responses.Status);
        Assert.Equal("the scoring model", responses.ScoringModelDescription);
        Assert.Equal(1, responses.CurrentMoveNumber);
        var submission = responses.TeamSubmission;
        Assert.Equal(7.5, submission.Score);
        Assert.False(submission.IsAverage);
        // in the scoring model's display order, not the order CITE sent them in
        Assert.Equal(["Containment", "Detection"], submission.Categories.Select(c => c.Description));
        var containment = submission.Categories[0];
        Assert.Equal(5, containment.Score);
        var option = Assert.Single(containment.SelectedOptions);
        Assert.Equal("Isolated", option.Description);
        Assert.Equal(5, option.Value);
        var comment = Assert.Single(containment.Comments);
        Assert.Equal("Isolated", comment.OptionDescription);
        Assert.Equal("Pulled the host off the network", comment.Text);
        Assert.Equal(["Alerted", "Escalated"], submission.Categories[1].SelectedOptions.Select(o => o.Description));
    }

    /// <summary>
    /// CITE answers an Owner with every submission the team has: other moves, each member's own, the
    /// official one, the averages. Only the team's own, the official and the team average for the move
    /// come back, and nothing from a member's own submission.
    /// </summary>
    [Fact]
    public async Task OnlyTheTeamOfficialAndTeamAverageSubmissionsForTheMove_AreReturned()
    {
        var graph = await SeedPushedGraph();
        ArrangeCite(graph);

        var responses = await Read(RootClient, graph.Msel.Id, graph.Team.Id, 1);
        var json = System.Text.Json.JsonSerializer.Serialize(responses);

        Assert.Equal(7.5, responses.TeamSubmission.Score);
        Assert.Equal(9, responses.OfficialSubmission.Score);
        Assert.False(responses.OfficialSubmission.IsAverage);
        Assert.Equal(4, responses.TeamAverageSubmission.Score);
        Assert.True(responses.TeamAverageSubmission.IsAverage);
        var averaged = responses.TeamAverageSubmission.Categories.Single(c => c.Description == "Containment");
        Assert.Equal(2, Assert.Single(averaged.SelectedOptions).SelectedCount);
        // an average's comments are one member's, so they are not passed on
        Assert.All(responses.TeamAverageSubmission.Categories, c => Assert.Empty(c.Comments));
        Assert.DoesNotContain("a member's own comment", json);
        Assert.DoesNotContain("move zero comment", json);
        Assert.DoesNotContain("type average comment", json);
    }

    [Fact]
    public async Task AnEarlierMove_ReadsThatMovesSubmission()
    {
        var graph = await SeedPushedGraph();
        ArrangeCite(graph);

        var responses = await Read(RootClient, graph.Msel.Id, graph.Team.Id, 0);

        Assert.Equal(CiteTeamResponsesStatus.Answered, responses.Status);
        Assert.Equal(1, responses.TeamSubmission.Score);
        Assert.Null(responses.OfficialSubmission);
        Assert.Null(responses.TeamAverageSubmission);
    }

    // ---------------------------------------------------------------------------------------------
    // CITE failing
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "CITE answered 500.")]
    [InlineData(HttpStatusCode.Forbidden, "CITE refused Blueprint's service account.")]
    public async Task CiteRefusingTheSubmissions_IsUnavailable(HttpStatusCode status, string message)
    {
        var graph = await SeedPushedGraph();
        ArrangeCite(graph, submissionsStatus: status);

        var responses = await Read(RootClient, graph.Msel.Id, graph.Team.Id, 1);

        Assert.Equal(CiteTeamResponsesStatus.Unavailable, responses.Status);
        Assert.Equal(message, responses.Message);
        Assert.Null(responses.TeamSubmission);
    }

    [Fact]
    public async Task CiteUnreachable_IsUnavailable()
    {
        var graph = await SeedPushedGraph();
        ArrangeToken();
        Factory.OutboundHttp.Throws($"GET api/evaluations/{graph.EvaluationId}");

        var responses = await Read(RootClient, graph.Msel.Id, graph.Team.Id, 1);

        Assert.Equal(CiteTeamResponsesStatus.Unavailable, responses.Status);
        Assert.Equal("CITE could not be reached.", responses.Message);
    }

    /// <summary>
    /// HttpClient's own timeout throws a <c>TaskCanceledException</c> the caller did not ask for. That is CITE
    /// not answering, said as such, not a 500 after the wait.
    /// </summary>
    [Fact]
    public async Task CiteTimingOut_IsUnavailableRatherThanA500()
    {
        var graph = await SeedPushedGraph();
        ArrangeCite(graph);
        // the url table answers before the route rules ArrangeCite added
        Factory.OutboundHttp.RespondByThrowing(
            $"{CiteApiUrl()}api/evaluations/{graph.EvaluationId}/teams/{graph.Team.Id}/submissions",
            new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.",
                new TimeoutException("The operation was canceled.")));

        var responses = await Read(RootClient, graph.Msel.Id, graph.Team.Id, 1);

        Assert.Equal(CiteTeamResponsesStatus.Unavailable, responses.Status);
        Assert.Equal("CITE did not answer in time.", responses.Message);
        Assert.Null(responses.TeamSubmission);
    }

    /// <summary>
    /// The generated client throws with the 200 it was sent when the body is not the JSON it expects, which
    /// is not "CITE answered 200".
    /// </summary>
    [Fact]
    public async Task CiteAnsweringWithSomethingUnreadable_SaysSoRatherThanItsStatus()
    {
        var graph = await SeedPushedGraph();
        ArrangeToken();
        Factory.OutboundHttp.AnswersJson($"GET api/evaluations/{graph.EvaluationId}", "<html>a proxy's page</html>");

        var responses = await Read(RootClient, graph.Msel.Id, graph.Team.Id, 1);

        Assert.Equal(CiteTeamResponsesStatus.Unavailable, responses.Status);
        Assert.Equal("CITE sent an answer Blueprint could not read.", responses.Message);
    }

    // ---------------------------------------------------------------------------------------------
    // The request
    // ---------------------------------------------------------------------------------------------

    /// <summary>A move number left out, empty or not a number is a 400, never move 0's answers.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("&moveNumber=")]
    [InlineData("&moveNumber=one")]
    public async Task NoMoveNumber_Is400AndCiteIsNotAsked(string moveNumberQuery)
    {
        var graph = await SeedPushedGraph();
        ArrangeCite(graph);
        var tokenRequests = TokenRequests();

        var response = await RootClient.GetAsync(
            $"/api/msels/{graph.Msel.Id}/citeSubmissions?teamId={graph.Team.Id}{moveNumberQuery}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("moveNumber", await response.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(CiteRequests(graph));
        Assert.Equal(tokenRequests, TokenRequests());
    }

    // ---------------------------------------------------------------------------------------------
    // Arrangement
    // ---------------------------------------------------------------------------------------------

    private sealed record Graph(MselEntity Msel, TeamEntity Team, Guid EvaluationId, Guid ScoringModelId)
    {
        public Guid Containment { get; } = Guid.NewGuid();
        public Guid Isolated { get; } = Guid.NewGuid();
        public Guid Monitored { get; } = Guid.NewGuid();
        public Guid Detection { get; } = Guid.NewGuid();
        public Guid Alerted { get; } = Guid.NewGuid();
        public Guid Escalated { get; } = Guid.NewGuid();
    }

    private async Task<Graph> SeedPushedGraph(Action<MselEntity> adjust = null)
    {
        var msel = TestData.Msel(status: MselItemStatus.Deployed);
        msel.UseCite = true;
        msel.CiteEvaluationId = Guid.NewGuid();
        msel.CiteScoringModelId = Guid.NewGuid();
        adjust?.Invoke(msel);
        await Seed(msel);
        var team = TestData.Team(msel.Id);
        team.CiteTeamTypeId = Guid.NewGuid();
        await Seed(team, TestData.Move(msel.Id, 0), TestData.Move(msel.Id, 1));

        return new Graph(msel, team, msel.CiteEvaluationId ?? Guid.NewGuid(), Guid.NewGuid());
    }

    private void ArrangeToken()
    {
        Factory.OutboundHttp
            .AnswersJson($"{Realm}/.well-known/openid-configuration", Discovery)
            .AnswersJson($"{Realm}/protocol/openid-connect/certs", """{"keys":[]}""")
            .AnswersJson(
                $"{Realm}/protocol/openid-connect/token",
                $$"""{"access_token":"{{ServiceAccountToken}}","token_type":"Bearer","expires_in":300}""");
    }

    private void ArrangeCite(
        Graph graph,
        int currentMoveNumber = 1,
        List<Submission> submissions = null,
        HttpStatusCode submissionsStatus = HttpStatusCode.OK)
    {
        ArrangeToken();
        Factory.OutboundHttp.Answers($"GET api/evaluations/{graph.EvaluationId}", new Evaluation
        {
            Id = graph.EvaluationId,
            Description = "the evaluation",
            CurrentMoveNumber = currentMoveNumber,
            ScoringModelId = graph.ScoringModelId,
            Moves =
            [
                new Move { Id = Guid.NewGuid(), MoveNumber = 0, EvaluationId = graph.EvaluationId },
                new Move { Id = Guid.NewGuid(), MoveNumber = 1, EvaluationId = graph.EvaluationId }
            ]
        });
        Factory.OutboundHttp.Answers($"GET api/scoringmodels/{graph.ScoringModelId}", ScoringModel(graph));

        var route = $"GET api/evaluations/{graph.EvaluationId}/teams/{graph.Team.Id}/submissions";
        if (submissionsStatus != HttpStatusCode.OK)
            Factory.OutboundHttp.Answers(route, submissionsStatus);
        else
            Factory.OutboundHttp.Answers(route, submissions ?? EverySubmissionTheTeamHas(graph));
    }

    private static ScoringModel ScoringModel(Graph graph) => new()
    {
        Id = graph.ScoringModelId,
        Description = "the scoring model",
        ScoringCategories =
        [
            // sent out of display order, so the order the answers come back in is the service's doing
            new ScoringCategory
            {
                Id = graph.Detection, Description = "Detection", DisplayOrder = 2,
                ScoringOptions =
                [
                    new ScoringOption { Id = graph.Escalated, Description = "Escalated", DisplayOrder = 2, Value = 3 },
                    new ScoringOption { Id = graph.Alerted, Description = "Alerted", DisplayOrder = 1, Value = 2 }
                ]
            },
            new ScoringCategory
            {
                Id = graph.Containment, Description = "Containment", DisplayOrder = 1,
                ScoringOptions =
                [
                    new ScoringOption { Id = graph.Isolated, Description = "Isolated", DisplayOrder = 1, Value = 5 },
                    new ScoringOption { Id = graph.Monitored, Description = "Monitored", DisplayOrder = 2, Value = 1 }
                ]
            }
        ]
    };

    private static List<Submission> EverySubmissionTheTeamHas(Graph graph)
    {
        var team = TeamSubmission(graph, 1, [graph.Isolated, graph.Escalated, graph.Alerted],
            [(graph.Isolated, "Pulled the host off the network"), (graph.Monitored, "  ")]);
        team.Score = 7.5;

        var moveZero = TeamSubmission(graph, 0, [graph.Monitored], [(graph.Monitored, "move zero comment")]);
        moveZero.Score = 1;

        var membersOwn = TeamSubmission(graph, 1, [graph.Monitored], [(graph.Monitored, "a member's own comment")]);
        membersOwn.UserId = Guid.NewGuid();
        membersOwn.Score = 2;

        var official = TeamSubmission(graph, 1, [graph.Isolated], []);
        official.TeamId = null;
        official.Score = 9;

        var teamAverage = TeamSubmission(graph, 1, [], [(graph.Isolated, "a member's own comment")]);
        teamAverage.ScoreIsAnAverage = true;
        teamAverage.Score = 4;
        teamAverage.SubmissionCategories.First(c => c.ScoringCategoryId == graph.Containment)
            .SubmissionOptions.First(o => o.ScoringOptionId == graph.Isolated).SelectedCount = 2;

        var typeAverage = TeamSubmission(graph, 1, [graph.Monitored], [(graph.Monitored, "type average comment")]);
        typeAverage.TeamId = null;
        typeAverage.GroupId = graph.Team.CiteTeamTypeId;
        typeAverage.ScoreIsAnAverage = true;
        typeAverage.Score = 3;

        return [moveZero, membersOwn, typeAverage, team, official, teamAverage];
    }

    private static Submission TeamSubmission(
        Graph graph, int moveNumber, Guid[] selectedOptionIds, (Guid OptionId, string Text)[] comments)
    {
        var categories = new Dictionary<Guid, Guid[]>
        {
            [graph.Containment] = [graph.Isolated, graph.Monitored],
            [graph.Detection] = [graph.Alerted, graph.Escalated]
        };
        var submissionId = Guid.NewGuid();

        return new Submission
        {
            Id = submissionId,
            EvaluationId = graph.EvaluationId,
            ScoringModelId = graph.ScoringModelId,
            TeamId = graph.Team.Id,
            MoveNumber = moveNumber,
            Status = ItemStatus.Active,
            SubmissionCategories = categories.Select(category =>
            {
                var categoryId = Guid.NewGuid();
                return new SubmissionCategory
                {
                    Id = categoryId,
                    SubmissionId = submissionId,
                    ScoringCategoryId = category.Key,
                    Score = category.Key == graph.Containment && selectedOptionIds.Contains(graph.Isolated) ? 5 : 0,
                    SubmissionOptions = category.Value.Select(optionId =>
                    {
                        var submissionOptionId = Guid.NewGuid();
                        return new SubmissionOption
                        {
                            Id = submissionOptionId,
                            SubmissionCategoryId = categoryId,
                            ScoringOptionId = optionId,
                            IsSelected = selectedOptionIds.Contains(optionId),
                            SubmissionComments = comments
                                .Where(c => c.OptionId == optionId)
                                .Select(c => new SubmissionComment
                                {
                                    Id = Guid.NewGuid(), SubmissionOptionId = submissionOptionId, Comment = c.Text
                                })
                                .ToList()
                        };
                    }).ToList()
                };
            }).ToList()
        };
    }

    private int TokenRequests() =>
        Factory.OutboundHttp.Paths.Count(p => p == $"{Realm}/protocol/openid-connect/token");

    private IEnumerable<string> CiteRequests(Graph graph) =>
        Factory.OutboundHttp.Paths.Where(p => p.Contains(graph.EvaluationId.ToString()));

    /// <summary>The CITE base url the host is configured with, for the url table's absolute urls.</summary>
    private string CiteApiUrl() =>
        Factory.Services.GetRequiredService<IOptionsMonitor<ClientOptions>>().CurrentValue.CiteApiUrl;

    private static string Route(Guid mselId, Guid teamId, int moveNumber) =>
        $"/api/msels/{mselId}/citeSubmissions?teamId={teamId}&moveNumber={moveNumber}";

    private async Task<CiteTeamResponses> Read(HttpClient client, Guid mselId, Guid teamId, int moveNumber)
    {
        var response = await client.GetAsync(Route(mselId, teamId, moveNumber), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<CiteTeamResponses>(JsonOptions, Ct);
    }
}
