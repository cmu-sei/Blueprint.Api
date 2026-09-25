// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Tests.Infrastructure;
using Cite.Api.Client;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>CiteService</c> and <c>CiteController</c> - the three lists blueprint fetches from cite.api so that
/// its MSEL editor can offer them in a drop-down.
/// </summary>
/// <remarks>
/// <para>
/// <c>GET api/scoringmodels</c>, <c>GET api/teamtypes</c> and <c>GET api/teamroles</c>, and all three are
/// the same six lines: call the client inside a <c>try</c>, cast the answer to <c>List&lt;T&gt;</c>, and
/// return an empty list from an empty <c>catch</c>. Reference data on the way <em>in</em> to a MSEL, not
/// exercise content, so this file is deliberately thin - what it exists to pin is the two things a caller
/// cannot see.
/// </para>
/// <para>
/// <strong>An unreachable CITE is indistinguishable from a CITE with nothing in it.</strong> Every failure
/// - unreachable, unauthorized, a wrong url, a malformed answer - becomes <c>200</c> and <c>[]</c>, logged
/// nowhere. <c>CiteService</c> holds an <c>ILogger&lt;CiteService&gt;</c> and the three empty
/// <c>catch</c> blocks do not use it. So a service account whose CITE password has expired presents a MSEL
/// editor with an empty scoring-model list, and the author concludes CITE has no scoring models. See
/// <see cref="EveryRoute_WhenCiteCannotBeReached_IsAnEmptyList"/>.
/// </para>
/// <para>
/// <strong>None of the three is authorized beyond being signed in.</strong> Both the controller and the
/// service take an <c>IAuthorizationService</c> - the framework's, not blueprint's
/// <c>IBlueprintAuthorizationService</c>, so it could not check a <c>SystemPermission</c> if it wanted to
/// - assign it to a field and never read it. Any authenticated caller can enumerate CITE's scoring
/// models, team types and team roles. See <see cref="EveryRoute_ForACallerWithNoPermissions_Is200"/>.
/// </para>
/// <para>
/// Per this branch's rule, every test characterizes rather than fixes, and says what fixing it will do to
/// the test.
/// </para>
/// </remarks>
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

    /// <remarks>
    /// A caller with no system role and no relationship to any MSEL. The three lists describe another
    /// application's configuration, and this is the only endpoint family in the API that hands them over
    /// on nothing but a valid token. Requiring any permission at all turns this red.
    /// </remarks>
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
        var response = await AnonymousClient.GetAsync(route, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <remarks>
    /// The <c>catch</c> is empty and the logger is never used, so this is a 200 and an empty list on every
    /// failure CITE can produce. Logging the exception would not turn this red - the status and the body
    /// are the whole of what a caller sees, and they cannot be improved without changing the contract.
    /// Answering <c>502</c>, or returning the list as nullable so the UI can tell "none" from "could not
    /// ask", is what would.
    /// </remarks>
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

    /// <remarks>
    /// <para>
    /// Two things at once, because they are the same line. CITE's first parameter is
    /// <c>userId</c> and the second is <c>description</c>, so <c>("", "", false)</c> asks for every user's
    /// models rather than the caller's - which is what a drop-down of scoring models wants, but reads like
    /// a filter nobody filled in.
    /// </para>
    /// <para>
    /// And it is the <em>three</em>-argument overload, so the request's <c>CancellationToken</c> is
    /// dropped: a caller who navigates away leaves blueprint waiting on CITE. Its two siblings pass the
    /// token, which is how you can tell this is an oversight rather than a decision. Passing <c>ct</c>
    /// turns this red, and the fix is to call the four-argument overload.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ScoringModels_AsksForEveryUsersModels_AndDropsTheRequestsCancellationToken()
    {
        ArrangeEmpty();

        await (await AnyCaller()).GetAsync(ScoringModels, Ct);

        await AssertScoringModelsAskedWithoutAToken();
    }

    /// <remarks>
    /// The token is matched on <c>CanBeCanceled</c> rather than with <c>Arg.Any</c>, because
    /// <c>CancellationToken.None</c> is a legal <c>CancellationToken</c> and an assertion that accepts it
    /// cannot tell a dropped token from a forwarded one - which is the distinction this whole pair of
    /// tests exists to draw against <see cref="ScoringModels_AsksForEveryUsersModels_AndDropsTheRequestsCancellationToken"/>.
    /// </remarks>
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

    /// <remarks>
    /// <para>
    /// The client is declared to return <c>ICollection&lt;ScoringModel&gt;</c> and the service casts it to
    /// <c>List&lt;ScoringModel&gt;</c>, so an answer that is any other <c>ICollection</c> - an array, a
    /// <c>HashSet</c>, anything a future regeneration of the client might produce - is an
    /// <c>InvalidCastException</c> into the empty <c>catch</c>, and the caller is told CITE has nothing.
    /// Today's NSwag output happens to build a <c>List</c>, so this is latent rather than live.
    /// </para>
    /// <para>
    /// The same cast is in all three methods here and in all three of <c>PlayerService</c>'s. Assigning to
    /// an <c>IEnumerable</c> local instead of casting turns this red, and is the fix.
    /// </para>
    /// </remarks>
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
