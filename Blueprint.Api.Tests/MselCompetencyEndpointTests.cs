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
/// <c>MselCompetencyService</c> / <c>MselCompetencyController</c> - the five routes over the competencies a
/// MSEL is assessed against. Reads want <c>ViewMsels</c> or a view of the MSEL; writes want
/// <c>ManageMsels</c> or the MSEL's <c>Owner</c> role.
/// </summary>
/// <remarks>
/// 401 and 403 for these routes live in <see cref="RouteAuthorizationTests"/>; this file covers the happy
/// path and the not-found path per route, per the thin protocol.
/// <para />
/// Mostly a positive control for the three shapes that opened every other file in this tier: both writes
/// validate both parents with clean, correctly-named 404s, there is no update method at all so nothing can
/// decide a permission from a request body, and <c>DeleteByIdsAsync</c>'s argument order matches its call
/// site - where <c>CardTeamController.cs:192</c> and <c>PlayerApplicationTeamController.cs:192</c> transpose
/// the same shape. The competencies themselves are the reference data
/// <c>CompetencyFrameworkService</c> imports (<c>abb8fdd</c>); nothing here re-covers the import side.
/// </remarks>
public class MselCompetencyEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET msels/{mselId}/mselcompetencies
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByMsel_ForAViewerOfTheMsel_ReturnsOnlyThatMselsCompetencies()
    {
        var graph = await SeedGraph();
        var actor = await Actor().OnMsel(graph.Msel, MselRole.Viewer).SeedAsync();
        var otherMsel = BlueprintAppFactory.Msel();
        await Seed(otherMsel);
        var otherCompetency = BlueprintAppFactory.Competency(graph.Framework.Id);
        await Seed(otherCompetency);
        await Seed(BlueprintAppFactory.MselCompetency(otherMsel.Id, otherCompetency.Id));

        var response = await Client(actor)
            .GetAsync($"api/msels/{graph.Msel.Id}/mselcompetencies", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content
            .ReadFromJsonAsync<List<ViewModels.MselCompetency>>(JsonOptions, Ct);
        Assert.Single(list);
        Assert.Equal(graph.Row.Id, list[0].Id);
        Assert.Equal(graph.Competency.Id, list[0].CompetencyId);
    }

    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor)
            .GetAsync($"api/msels/{Guid.NewGuid()}/mselcompetencies", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // GET mselcompetencies/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ForAViewerOfTheMsel_Is200()
    {
        var graph = await SeedGraph();
        var actor = await Actor().OnMsel(graph.Msel, MselRole.Viewer).SeedAsync();

        var response = await Client(actor).GetAsync($"api/mselcompetencies/{graph.Row.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answered = await response.Content
            .ReadFromJsonAsync<ViewModels.MselCompetency>(JsonOptions, Ct);
        Assert.Equal(graph.Row.Id, answered.Id);
        Assert.Equal(graph.Msel.Id, answered.MselId);
        Assert.Equal(graph.Competency.IdNumber, answered.Competency.IdNumber);
        Assert.Empty(answered.Competency.RelatedIdNumbers);
    }

    /// <remarks>
    /// BUG: both reads resolve a competency's relationships against the MSEL's own pool alone, and a
    /// relationship whose other end is not in that pool is dropped by
    /// <c>.Where(n =&gt; n != null)</c> with nothing said. So the same competency answers a different
    /// relationship list per MSEL, and "these two are related" and "one of them is not on this MSEL" are
    /// the same answer - which is the one thing an author adding competencies to an exercise needs to tell
    /// apart. Both halves are asserted here.
    /// </remarks>
    [Fact]
    public async Task Get_AnswersOnlyTheRelationshipsWhoseOtherEndIsOnTheSameMsel()
    {
        var graph = await SeedGraph();
        var actor = await Actor().OnMsel(graph.Msel, MselRole.Viewer).SeedAsync();
        var onTheMsel = BlueprintAppFactory.Competency(graph.Framework.Id, "IN-POOL");
        var offTheMsel = BlueprintAppFactory.Competency(graph.Framework.Id, "OUT-OF-POOL");
        await Seed(onTheMsel, offTheMsel);
        await Seed(BlueprintAppFactory.MselCompetency(graph.Msel.Id, onTheMsel.Id));
        await Seed(
            BlueprintAppFactory.CompetencyRelationship(graph.Competency.Id, onTheMsel.Id),
            BlueprintAppFactory.CompetencyRelationship(graph.Competency.Id, offTheMsel.Id));

        var answered = await Get<ViewModels.MselCompetency>(
            Client(actor), $"api/mselcompetencies/{graph.Row.Id}");

        Assert.Equal(["IN-POOL"], answered.Competency.RelatedIdNumbers);
    }

    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"api/mselcompetencies/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // POST mselcompetencies
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_ForAnOwnerOfTheMsel_Is201()
    {
        var graph = await SeedGraph();
        var actor = await Actor().OnMsel(graph.Msel, MselRole.Owner).SeedAsync();
        var competency = BlueprintAppFactory.Competency(graph.Framework.Id);
        await Seed(competency);

        var response = await Client(actor).PostAsJsonAsync(
            "api/mselcompetencies", Body(graph.Msel.Id, competency.Id), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content
            .ReadFromJsonAsync<ViewModels.MselCompetency>(JsonOptions, Ct);
        Assert.Equal(competency.Id, created.CompetencyId);
        Assert.EndsWith($"/api/mselcompetencies/{created.Id}", response.Headers.Location.ToString());

        Assert.Equal(2, await NewContext().MselCompetencies
            .CountAsync(x => x.MselId == graph.Msel.Id, Ct));
    }

    /// <remarks>
    /// Both parents are validated and each names its own entity, which is the shape every other create in
    /// this tier should have had: <c>PlayerApplicationService</c>, <c>CiteActionService</c> and
    /// <c>CatalogInjectService</c> all leave an unknown parent to the foreign key and answer 500.
    /// </remarks>
    [Theory]
    [InlineData("msel")]
    [InlineData("competency")]
    public async Task Create_ForAParentThatIsNotThere_Is404(string missing)
    {
        var graph = await SeedGraph();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();
        var body = missing == "msel"
            ? Body(Guid.NewGuid(), graph.Competency.Id)
            : Body(graph.Msel.Id, Guid.NewGuid());

        var response = await Client(actor).PostAsJsonAsync("api/mselcompetencies", body, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <remarks>
    /// BUG: a pair that is already there is refused by a bare <c>ArgumentException</c>, which is not an
    /// <c>IApiException</c>, so the answer is a 500 where the case deserves a 409 - and the message the
    /// service took the trouble to write ("MSEL Competency already exists.") reaches the caller only as a
    /// 500's <c>Detail</c> in Development. Same shape as <c>CatalogUnitService</c> and
    /// <c>TeamCompetencyService</c>.
    /// </remarks>
    [Fact]
    public async Task Create_ForAPairThatIsAlreadyThere_Is500()
    {
        var graph = await SeedGraph();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "api/mselcompetencies", Body(graph.Msel.Id, graph.Competency.Id), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Single(await NewContext().MselCompetencies.ToListAsync(Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE mselcompetencies/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_ForAnOwnerOfTheMsel_Is204()
    {
        var graph = await SeedGraph();
        var actor = await Actor().OnMsel(graph.Msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync($"api/mselcompetencies/{graph.Row.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await NewContext().MselCompetencies.ToListAsync(Ct));
        Assert.Single(await NewContext().Competencies.ToListAsync(Ct));
    }

    [Fact]
    public async Task Delete_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync($"api/mselcompetencies/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE msels/{mselId}/competencies/{competencyId}
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// The positive control for the transposed-ids defect: <c>MselCompetencyController.cs:129</c> calls
    /// <c>DeleteByIdsAsync(mselId, competencyId, …)</c> against a matching signature, so the route deletes
    /// the row a client naming the two ids in the order the route spells them means. Two same-typed ids
    /// either side of a call site is a defect no compiler can find, which is why the working copy earns a
    /// test of its own.
    /// </remarks>
    [Fact]
    public async Task DeleteByIds_ForAnOwnerOfTheMsel_Is204()
    {
        var graph = await SeedGraph();
        var actor = await Actor().OnMsel(graph.Msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync(
            $"api/msels/{graph.Msel.Id}/competencies/{graph.Competency.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await NewContext().MselCompetencies.ToListAsync(Ct));
    }

    [Fact]
    public async Task DeleteByIds_ForAPairThatIsNotThere_Is404()
    {
        var graph = await SeedGraph();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync(
            $"api/msels/{graph.Msel.Id}/competencies/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(await NewContext().MselCompetencies.ToListAsync(Ct));
    }

    private async Task<T> Get<T>(System.Net.Http.HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
    }

    private async Task<(MselEntity Msel, CompetencyFrameworkEntity Framework,
        CompetencyEntity Competency, MselCompetencyEntity Row)> SeedGraph()
    {
        var msel = BlueprintAppFactory.Msel();
        var framework = BlueprintAppFactory.CompetencyFramework();
        await Seed(msel, framework);
        var competency = BlueprintAppFactory.Competency(framework.Id);
        await Seed(competency);
        var row = BlueprintAppFactory.MselCompetency(msel.Id, competency.Id);
        await Seed(row);

        return (msel, framework, competency, row);
    }

    /// <remarks>
    /// <c>ViewModels.MselCompetency</c> does not derive from <c>Base</c> and <c>MselCompetencyEntity</c> is
    /// not a <c>BaseEntity</c>, so nothing records who added a competency to an exercise or when - the
    /// service writes a <c>LogWarning</c> per write instead, which is the audit trail and is filed at the
    /// wrong level.
    /// </remarks>
    private static MselCompetencyBody Body(Guid mselId, Guid competencyId) =>
        new() { mselId = mselId, competencyId = competencyId };

    private record MselCompetencyBody
    {
        public Guid id { get; init; }
        public Guid mselId { get; init; }
        public Guid competencyId { get; init; }
    }
}
