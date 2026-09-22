// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
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
/// <c>CatalogUnitService</c> / <c>CatalogUnitController</c> - the six routes over the join rows that
/// assign a catalog to a unit, which is how anybody without <c>ViewCatalogs</c> reaches one. Five routes
/// want <c>ManageCatalogs</c>; the single read accepts membership of the unit instead.
/// </summary>
/// <remarks>
/// 401 and 403 for these routes live in <see cref="RouteAuthorizationTests"/>.
/// <para />
/// This is the well-behaved service of the pair: every method reads its parents before it decides
/// anything, both creates' parents answer clean 404s, and <c>DeleteByIdsAsync(catalogId, unitId, ct)</c>
/// is called in the order its signature declares - the second positive control in this commit for
/// <c>CardTeamController.cs:192</c>.
/// </remarks>
public class CatalogUnitEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET catalogs/{catalogId}/catalogunits
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// BUG: this route requires <c>ManageCatalogs</c> where the inject list one controller over requires
    /// <c>ViewCatalogs</c> and accepts a unit member besides - so the caller who can see a catalog's
    /// contents cannot see who it is shared with, and the single read below is more permissive than the
    /// list it belongs to.
    /// </remarks>
    [Fact]
    public async Task GetByCatalog_WithManageCatalogs_ReturnsOnlyThatCatalogsUnits()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();
        var graph = await SeedGraph();
        var other = BlueprintAppFactory.Catalog(graph.InjectType.Id);
        await Seed(other);
        await Seed(BlueprintAppFactory.CatalogUnit(graph.Unit.Id, other.Id));

        var response = await Client(actor).GetAsync(
            $"api/catalogs/{graph.Catalog.Id}/catalogunits", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<List<ViewModels.CatalogUnit>>(JsonOptions, Ct);
        Assert.Single(list);
        Assert.Equal(graph.CatalogUnit.Id, list[0].Id);
        Assert.Equal(graph.Unit.Name, list[0].Unit.Name);
    }

    [Fact]
    public async Task GetByCatalog_ForACatalogThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();

        var response = await Client(actor).GetAsync(
            $"api/catalogs/{Guid.NewGuid()}/catalogunits", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // GET catalogunits/{id}
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// The minimum a caller can hold: membership of the unit the row names, which is what
    /// <c>CatalogViewRequirement</c> resolves from the <em>stored</em> row's <c>CatalogId</c> - the id
    /// <c>CatalogInjectService.GetAsync</c> gets wrong.
    /// </remarks>
    [Fact]
    public async Task Get_ForAMemberOfTheUnit_Is200()
    {
        var actor = await Actor().SeedAsync();
        var graph = await SeedGraph();
        await Seed(BlueprintAppFactory.UnitUser(actor.Id, graph.Unit.Id));

        var response = await Client(actor).GetAsync($"api/catalogunits/{graph.CatalogUnit.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answered = await response.Content.ReadFromJsonAsync<ViewModels.CatalogUnit>(JsonOptions, Ct);
        Assert.Equal(graph.CatalogUnit.Id, answered.Id);
        Assert.Equal(graph.Catalog.Id, answered.CatalogId);
        Assert.Equal(graph.Unit.Id, answered.UnitId);
    }

    /// <remarks>
    /// BUG: the missing row is reported as <c>EntityNotFoundException&lt;CatalogEntity&gt;</c>
    /// (<c>CatalogUnitService.cs:68</c>), so the status is right and the message names the catalog -
    /// indistinguishable from the catalog being gone. The same service's other four methods name
    /// <c>CatalogUnit</c>. The null check runs before the permission check, so this is a clean 404 for a
    /// stranger too.
    /// </remarks>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is404_EvenForAStranger()
    {
        var stranger = await Actor().SeedAsync();

        var response = await Client(stranger).GetAsync($"api/catalogunits/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // POST catalogunits
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_WithManageCatalogs_Is201()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();
        var graph = await SeedGraph(withJoinRow: false);

        var response = await Client(actor).PostAsJsonAsync(
            "api/catalogunits", Body(graph.Catalog.Id, graph.Unit.Id), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ViewModels.CatalogUnit>(JsonOptions, Ct);
        Assert.Equal(graph.Catalog.Id, created.CatalogId);
        Assert.Equal(graph.Unit.Id, created.UnitId);
        Assert.EndsWith($"/api/catalogunits/{created.Id}", response.Headers.Location.ToString());

        var stored = await NewContext().CatalogUnits.SingleAsync(x => x.Id == created.Id, Ct);
        Assert.Equal(graph.Unit.Id, stored.UnitId);
    }

    [Fact]
    public async Task Create_ForACatalogThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();
        var graph = await SeedGraph(withJoinRow: false);

        var response = await Client(actor).PostAsJsonAsync(
            "api/catalogunits", Body(Guid.NewGuid(), graph.Unit.Id), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_ForAUnitThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();
        var graph = await SeedGraph(withJoinRow: false);

        var response = await Client(actor).PostAsJsonAsync(
            "api/catalogunits", Body(graph.Catalog.Id, Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <remarks>
    /// BUG: the duplicate is refused by an <c>ArgumentException</c>, which is not an
    /// <c>IApiException</c>, so <c>JsonExceptionFilter</c> answers 500 where the case deserves a 409.
    /// The check itself is the right shape and is what the inject join has none of.
    /// </remarks>
    [Fact]
    public async Task Create_ForAPairThatIsAlreadyThere_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();
        var graph = await SeedGraph();

        var response = await Client(actor).PostAsJsonAsync(
            "api/catalogunits", Body(graph.Catalog.Id, graph.Unit.Id), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT catalogunits/{id}
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// The profile maps <c>CatalogId</c> and <c>UnitId</c> both ways, so a PUT repoints the row at
    /// whatever pair the body names - which is what this asserts, there being nothing else on the row to
    /// update. The route also declares <c>typeof(Unit)</c> while returning a <c>CatalogUnit</c>; see
    /// the contract notes.
    /// </remarks>
    [Fact]
    public async Task Update_WithManageCatalogs_RepointsTheRow()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();
        var graph = await SeedGraph();
        var second = BlueprintAppFactory.Unit();
        await Seed(second);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/catalogunits/{graph.CatalogUnit.Id}",
            Body(graph.Catalog.Id, second.Id) with { id = graph.CatalogUnit.Id },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await NewContext().CatalogUnits.SingleAsync(x => x.Id == graph.CatalogUnit.Id, Ct);
        Assert.Equal(second.Id, stored.UnitId);
    }

    [Fact]
    public async Task Update_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();
        var graph = await SeedGraph(withJoinRow: false);
        var id = Guid.NewGuid();

        var response = await Client(actor).PutAsJsonAsync(
            $"api/catalogunits/{id}", Body(graph.Catalog.Id, graph.Unit.Id) with { id = id }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE catalogunits/{id}, DELETE catalogs/{catalogId}/units/{unitId}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_WithManageCatalogs_Is204()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();
        var graph = await SeedGraph();

        var response = await Client(actor).DeleteAsync(
            $"api/catalogunits/{graph.CatalogUnit.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await NewContext().CatalogUnits.ToListAsync(Ct));
    }

    [Fact]
    public async Task Delete_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();

        var response = await Client(actor).DeleteAsync($"api/catalogunits/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteByIds_WithManageCatalogs_Is204()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();
        var graph = await SeedGraph();

        var response = await Client(actor).DeleteAsync(
            $"api/catalogs/{graph.Catalog.Id}/units/{graph.Unit.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await NewContext().CatalogUnits.ToListAsync(Ct));
    }

    [Fact]
    public async Task DeleteByIds_ForAPairThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();
        var graph = await SeedGraph();

        var response = await Client(actor).DeleteAsync(
            $"api/catalogs/{graph.Catalog.Id}/units/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed record Graph(
        InjectTypeEntity InjectType,
        CatalogEntity Catalog,
        UnitEntity Unit,
        CatalogUnitEntity CatalogUnit);

    private async Task<Graph> SeedGraph(bool withJoinRow = true)
    {
        var injectType = BlueprintAppFactory.InjectType();
        var unit = BlueprintAppFactory.Unit();
        await Seed(injectType, unit);
        var catalog = BlueprintAppFactory.Catalog(injectType.Id);
        await Seed(catalog);

        var catalogUnit = BlueprintAppFactory.CatalogUnit(unit.Id, catalog.Id);
        if (withJoinRow)
            await Seed(catalogUnit);

        return new Graph(injectType, catalog, unit, catalogUnit);
    }

    private static CatalogUnitBody Body(Guid catalogId, Guid unitId) =>
        new() { catalogId = catalogId, unitId = unitId };

    /// <remarks>
    /// <c>ViewModels.CatalogUnit</c> does not derive from <c>Base</c>, so there are no audit fields to
    /// leave out - and nothing records who shared a catalog with a unit or when.
    /// </remarks>
    private record CatalogUnitBody
    {
        public Guid id { get; init; }
        public Guid catalogId { get; init; }
        public Guid unitId { get; init; }
    }
}
