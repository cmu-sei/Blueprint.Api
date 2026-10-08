// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

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

    /// <summary>Get by catalog with manage catalogs returns only that catalogs units.</summary>
    [Fact]
    public async Task GetByCatalog_WithManageCatalogs_ReturnsOnlyThatCatalogsUnits()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();
        var graph = await SeedGraph();
        var other = TestData.Catalog(graph.InjectType.Id);
        await Seed(other);
        await Seed(TestData.CatalogUnit(graph.Unit.Id, other.Id));

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

    /// <summary>Membership of the unit the row names reads it.</summary>
    [Fact]
    public async Task Get_ForAMemberOfTheUnit_Is200()
    {
        var actor = await Actor().SeedAsync();
        var graph = await SeedGraph();
        await Seed(TestData.UnitUser(actor.Id, graph.Unit.Id));

        var response = await Client(actor).GetAsync($"api/catalogunits/{graph.CatalogUnit.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answered = await response.Content.ReadFromJsonAsync<ViewModels.CatalogUnit>(JsonOptions, Ct);
        Assert.Equal(graph.CatalogUnit.Id, answered.Id);
        Assert.Equal(graph.Catalog.Id, answered.CatalogId);
        Assert.Equal(graph.Unit.Id, answered.UnitId);
    }

    /// <summary>Get for an id that is not there is answered with a 404 even for a stranger.</summary>
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

        var stored = await ReadBack(rb => rb.CatalogUnits.SingleAsync(x => x.Id == created.Id, Ct));
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

    /// <summary>Create for a pair that is already there is answered with a 500.</summary>
    [Fact]
    public async Task Create_ForAPairThatIsAlreadyThere_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();
        var graph = await SeedGraph();

        var response = await Client(actor).PostAsJsonAsync(
            "api/catalogunits", Body(graph.Catalog.Id, graph.Unit.Id), Ct);

        Assert.Equal("Catalog Unit already exists.", (await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
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
        var second = TestData.Unit();
        await Seed(second);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/catalogunits/{graph.CatalogUnit.Id}",
            Body(graph.Catalog.Id, second.Id) with { id = graph.CatalogUnit.Id },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await ReadBack(rb => rb.CatalogUnits.SingleAsync(x => x.Id == graph.CatalogUnit.Id, Ct));
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
        Assert.Empty(await ReadBack(rb => rb.CatalogUnits.ToListAsync(Ct)));
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
        Assert.Empty(await ReadBack(rb => rb.CatalogUnits.ToListAsync(Ct)));
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
        var injectType = TestData.InjectType();
        var unit = TestData.Unit();
        await Seed(injectType, unit);
        var catalog = TestData.Catalog(injectType.Id);
        await Seed(catalog);

        var catalogUnit = TestData.CatalogUnit(unit.Id, catalog.Id);
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
