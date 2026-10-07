// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Tests.Support;
using Cite.Api.Client;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary><c>CiteService</c> and <c>CiteController</c> - the three lists blueprint fetches from cite.api
/// so that its MSEL editor can offer them in a drop-down.</summary>
public class CiteServiceTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    [Fact]
    public async Task ScoringModels_ReturnsWhatCiteAnswered()
    {
        ScoringModelsAre(
        [
            new ScoringModel { Description = "first" },
            new ScoringModel { Description = "second" }
        ]);

        var models = await Read<List<ScoringModel>>(ScoringModels, await AnyCaller());

        Assert.Equal(["first", "second"], models.ConvertAll(x => x.Description));
    }

    [Fact]
    public async Task TeamTypes_ReturnsWhatCiteAnswered()
    {
        Factory.Cite.GetTeamTypesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new TeamType { Name = "blue" },
            new TeamType { Name = "red" }
        ]);

        var types = await Read<List<TeamType>>(TeamTypes, await AnyCaller());

        Assert.Equal(["blue", "red"], types.ConvertAll(x => x.Name));
    }

    [Fact]
    public async Task TeamRoles_ReturnsWhatCiteAnswered()
    {
        Factory.Cite.GetAllTeamRolesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new TeamRole { Name = "Inciter" },
            new TeamRole { Name = "Modifier" }
        ]);

        var roles = await Read<List<TeamRole>>(TeamRoles, await AnyCaller());

        Assert.Equal(["Inciter", "Modifier"], roles.ConvertAll(x => x.Name));
    }

    /// <summary>Every route for a caller with no permissions is answered with a 200.</summary>
    [Theory]
    [InlineData(ScoringModels)]
    [InlineData(TeamTypes)]
    [InlineData(TeamRoles)]
    public async Task EveryRoute_ForACallerWithNoPermissions_Is200(string route)
    {
        ArrangeEmpty();

        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync(route, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(ScoringModels)]
    [InlineData(TeamTypes)]
    [InlineData(TeamRoles)]
    public async Task EveryRoute_Anonymously_Is401(string route)
    {
        var response = await Client().GetAsync(route, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Every route when CITE cannot be reached is an empty list.</summary>
    [Theory]
    [InlineData(ScoringModels)]
    [InlineData(TeamTypes)]
    [InlineData(TeamRoles)]
    public async Task EveryRoute_WhenCiteCannotBeReached_IsAnEmptyList(string route)
    {
        ArrangeEveryRouteDown();

        var response = await (await AnyCaller()).GetAsync(route, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>Scoring models asks for every users models and drops the requests cancellation token.</summary>
    [Fact]
    public async Task ScoringModels_AsksForEveryUsersModels_AndDropsTheRequestsCancellationToken()
    {
        ArrangeEmpty();

        await (await AnyCaller()).GetAsync(ScoringModels, Ct);

        await AssertScoringModelsAskedWithoutAToken();
    }

    /// <summary>The team-type and team-role requests forward the request's cancellation token.</summary>
    [Fact]
    public async Task TeamTypes_AndTeamRoles_PassTheRequestsCancellationToken()
    {
        ArrangeEmpty();

        var client = await AnyCaller();

        await client.GetAsync(TeamTypes, Ct);
        await client.GetAsync(TeamRoles, Ct);

        await Factory.Cite.Received(1)
            .GetTeamTypesAsync(Arg.Is<CancellationToken>(x => x.CanBeCanceled));
        await Factory.Cite.Received(1)
            .GetAllTeamRolesAsync(Arg.Is<CancellationToken>(x => x.CanBeCanceled));
    }

    /// <summary>Scoring models when CITE answers with an array.</summary>
    [Fact]
    public async Task ScoringModels_WhenCiteAnswersWithAnArrayRatherThanAList_IsAnEmptyList()
    {
        ScoringModelsAre(new[] { new ScoringModel { Description = "unreachable" } });

        var response = await (await AnyCaller()).GetAsync(ScoringModels, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync(Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private const string ScoringModels = "/api/scoringmodels";
    private const string TeamTypes = "/api/teamtypes";
    private const string TeamRoles = "/api/teamroles";

    /// <summary>
    /// A signed-in caller holding nothing, which is all any of these three routes asks for.
    /// </summary>
    private async Task<System.Net.Http.HttpClient> AnyCaller() => Client(await Actor().SeedAsync());

    /// <summary>
    /// All three answering with nothing, for a test that is about something other than the contents.
    /// </summary>
    /// <remarks>
    /// Arranged explicitly rather than left to NSubstitute's auto-value, because what an unconfigured
    /// <c>Task&lt;ICollection&lt;T&gt;&gt;</c> returns is the mocking library's business and the cast in
    /// the service is what this file is about.
    /// </remarks>
    private void ArrangeEmpty()
    {
        ScoringModelsAre([]);
        Factory.Cite.GetTeamTypesAsync(Arg.Any<CancellationToken>()).Returns([]);
        Factory.Cite.GetAllTeamRolesAsync(Arg.Any<CancellationToken>()).Returns([]);
    }

    /// <summary>All three failing the way an unreachable CITE fails.</summary>
    private void ArrangeEveryRouteDown()
    {
        Factory.Cite.GetScoringModelsAsync("", "", false).ThrowsAsync(new CiteIsDown());
        Factory.Cite.GetTeamTypesAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new CiteIsDown());
        Factory.Cite.GetAllTeamRolesAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new CiteIsDown());
    }

    /// <summary>
    /// What CITE answers when asked for scoring models, arranged against the overload production calls.
    /// </summary>
    /// <remarks>
    /// A helper rather than an inline arrangement because <c>xUnit1051</c> is an error in this
    /// repository: the three-argument overload has a four-argument sibling taking a
    /// <c>CancellationToken</c>, so the analyzer reports the call production makes as a mistake wherever
    /// it appears in a test method. Naming it here says "deliberately" once instead of four times, and
    /// the assertion that production really does drop the token is
    /// <see cref="AssertScoringModelsAskedWithoutAToken"/>.
    /// </remarks>
    private void ScoringModelsAre(ICollection<ScoringModel> models) =>
        Factory.Cite.GetScoringModelsAsync("", "", false).Returns(models);

    private async Task AssertScoringModelsAskedWithoutAToken()
    {
        await Factory.Cite.Received(1).GetScoringModelsAsync("", "", false);
        await Factory.Cite.DidNotReceive()
            .GetScoringModelsAsync("", "", false, Arg.Any<CancellationToken>());
    }

    private async Task<T> Read<T>(string route, System.Net.Http.HttpClient client)
    {
        var response = await client.GetAsync(route, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
    }

    /// <summary>A failure from the CITE client, standing in for anything that can go wrong.</summary>
    private sealed class CiteIsDown : System.Exception
    {
        public CiteIsDown()
            : base("cite.api is not answering")
        {
        }
    }
}
