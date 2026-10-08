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
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// <c>CatalogInjectService</c> / <c>CatalogInjectController</c> - the six routes over the join rows that
/// put an inject in a catalog. Reads want <c>ViewCatalogs</c> or a unit the catalog is assigned to; writes
/// want <c>ManageCatalogs</c>. 401 and 403 for these routes live in <c>RouteAuthorizationTests</c>.
/// </summary>
public class CatalogInjectEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET catalogs/{catalogId}/cataloginjects
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByCatalog_WithViewCatalogs_ReturnsOnlyThatCatalogsInjects()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewCatalogs).SeedAsync();
        var graph = await SeedGraph();
        var other = TestData.Catalog(graph.InjectType.Id);
        await Seed(other);
        await Seed(TestData.CatalogInject(other.Id, graph.Inject.Id));

        var response = await Client(actor).GetAsync(
            $"api/catalogs/{graph.Catalog.Id}/cataloginjects", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<List<ViewModels.CatalogInject>>(JsonOptions, Ct);
        Assert.Single(list);
        Assert.Equal(graph.CatalogInject.Id, list[0].Id);
        Assert.Equal(graph.Inject.Id, list[0].Inject.Id);
    }

    /// <remarks>
    /// The minimum a caller can hold and still read a private catalog's injects: membership of a unit
    /// the catalog is assigned to, which is all <c>CatalogViewRequirement</c> asks for.
    /// </remarks>
    [Fact]
    public async Task GetByCatalog_ForAMemberOfAUnitTheCatalogIsAssignedTo_Is200()
    {
        var graph = await SeedGraph();
        var actor = await Actor().InUnit(await UnitSharing(graph.Catalog.Id)).SeedAsync();

        var response = await Client(actor).GetAsync(
            $"api/catalogs/{graph.Catalog.Id}/cataloginjects", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetByCatalog_ForACatalogThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewCatalogs).SeedAsync();

        var response = await Client(actor).GetAsync(
            $"api/catalogs/{Guid.NewGuid()}/cataloginjects", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // GET cataloginjects/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_WithViewCatalogs_Is200()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewCatalogs).SeedAsync();
        var graph = await SeedGraph();

        var response = await Client(actor).GetAsync(
            $"api/cataloginjects/{graph.CatalogInject.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answered = await response.Content.ReadFromJsonAsync<ViewModels.CatalogInject>(JsonOptions, Ct);
        Assert.Equal(graph.CatalogInject.Id, answered.Id);
        Assert.Equal(graph.Catalog.Id, answered.CatalogId);
        Assert.Equal(graph.Inject.Id, answered.InjectId);
    }

    /// <summary>Get for a member of a unit the catalog is assigned to is answered with a 403 though they may list the same row.</summary>
    [Fact]
    public async Task Get_ForAMemberOfAUnitTheCatalogIsAssignedTo_Is403_ThoughTheyMayListTheSameRow()
    {
        var graph = await SeedGraph();
        var actor = await Actor().InUnit(await UnitSharing(graph.Catalog.Id)).SeedAsync();

        var response = await Client(actor).GetAsync(
            $"api/cataloginjects/{graph.CatalogInject.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var list = await Client(actor).GetAsync(
            $"api/catalogs/{graph.Catalog.Id}/cataloginjects", Ct);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewCatalogs).SeedAsync();

        var response = await Client(actor).GetAsync($"api/cataloginjects/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // POST cataloginjects, POST cataloginjects/multiple
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_WithManageCatalogs_Is201()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();
        var graph = await SeedGraph(withJoinRow: false);

        var response = await Client(actor).PostAsJsonAsync(
            "api/cataloginjects",
            Body(graph.Catalog.Id, graph.Inject.Id) with { isNew = true, displayOrder = 3 },
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ViewModels.CatalogInject>(JsonOptions, Ct);
        Assert.Equal(graph.Inject.Id, created.InjectId);
        Assert.True(created.IsNew);
        Assert.Equal(3, created.DisplayOrder);
        Assert.EndsWith($"/api/cataloginjects/{created.Id}", response.Headers.Location.ToString());

        var stored = await ReadBack(rb => rb.CatalogInjects.SingleAsync(x => x.Id == created.Id, Ct));
        Assert.Equal(graph.Catalog.Id, stored.CatalogId);
    }

    /// <summary>Create for a catalog that is not there is answered with a 500.</summary>
    [Fact]
    public async Task Create_ForACatalogThatIsNotThere_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();
        var graph = await SeedGraph(withJoinRow: false);

        var response = await Client(actor).PostAsJsonAsync(
            "api/cataloginjects", Body(Guid.NewGuid(), graph.Inject.Id), Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("An error occurred while saving the entity changes. See the inner exception for details.", failure.Title);
        Assert.Contains("CatalogInjectService.CreateAsync", failure.Detail);
    }

    /// <remarks>
    /// The bulk route answers 200 with the list rather than 201, having no single row to point at.
    /// </remarks>
    [Fact]
    public async Task CreateMultiple_WithManageCatalogs_Is200WithEveryRow()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();
        var graph = await SeedGraph(withJoinRow: false);
        var second = TestData.Inject(graph.InjectType.Id);
        await Seed(second);

        var response = await Client(actor).PostAsJsonAsync(
            "api/cataloginjects/multiple",
            new[] { Body(graph.Catalog.Id, graph.Inject.Id), Body(graph.Catalog.Id, second.Id) },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<List<ViewModels.CatalogInject>>(JsonOptions, Ct);
        Assert.Equal(2, created.Count);
        Assert.Contains(graph.Inject.Id, created.Select(x => x.InjectId));
        Assert.Contains(second.Id, created.Select(x => x.InjectId));
        Assert.Equal(2, await ReadBack(rb => rb.CatalogInjects.CountAsync(Ct)));
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE cataloginjects/{id}, DELETE catalogs/{catalogId}/injects/{injectId}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_WithManageCatalogs_Is204()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();
        var graph = await SeedGraph();

        var response = await Client(actor).DeleteAsync(
            $"api/cataloginjects/{graph.CatalogInject.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await ReadBack(rb => rb.CatalogInjects.ToListAsync(Ct)));
    }

    [Fact]
    public async Task Delete_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();

        var response = await Client(actor).DeleteAsync($"api/cataloginjects/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteByIds_WithManageCatalogs_Is204()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();
        var graph = await SeedGraph();

        var response = await Client(actor).DeleteAsync(
            $"api/catalogs/{graph.Catalog.Id}/injects/{graph.Inject.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await ReadBack(rb => rb.CatalogInjects.ToListAsync(Ct)));
    }

    [Fact]
    public async Task DeleteByIds_ForAPairThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();
        var graph = await SeedGraph();

        var response = await Client(actor).DeleteAsync(
            $"api/catalogs/{graph.Catalog.Id}/injects/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed record Graph(
        InjectTypeEntity InjectType,
        InjectEntity Inject,
        CatalogEntity Catalog,
        CatalogInjectEntity CatalogInject);

    private async Task<Graph> SeedGraph(bool withJoinRow = true)
    {
        var injectType = TestData.InjectType();
        await Seed(injectType);
        var inject = TestData.Inject(injectType.Id);
        var catalog = TestData.Catalog(injectType.Id);
        await Seed(inject, catalog);

        var catalogInject = TestData.CatalogInject(catalog.Id, inject.Id);
        if (withJoinRow)
            await Seed(catalogInject);

        return new Graph(injectType, inject, catalog, catalogInject);
    }

    /// <summary>A unit the catalog is shared with, for an actor to be put in with <c>InUnit</c>.</summary>
    private async Task<UnitEntity> UnitSharing(Guid catalogId)
    {
        var unit = TestData.Unit();
        await Seed(unit);
        await Seed(TestData.CatalogUnit(unit.Id, catalogId));

        return unit;
    }

    private static CatalogInjectBody Body(Guid catalogId, Guid injectId) =>
        new() { catalogId = catalogId, injectId = injectId };

    /// <remarks>
    /// <c>ViewModels.CatalogInject</c> does not derive from <c>Base</c>, so unlike the catalog's own body
    /// this one has no audit fields to leave out. <c>DisplayOrder</c> is an <c>int</c>, so it comes back
    /// as a JSON string; <c>JsonIntegerConverter</c> reads either form, so sending a number is fine.
    /// </remarks>
    private record CatalogInjectBody
    {
        public Guid id { get; init; }
        public Guid catalogId { get; init; }
        public Guid injectId { get; init; }
        public bool isNew { get; init; }
        public int displayOrder { get; init; }
    }
}
