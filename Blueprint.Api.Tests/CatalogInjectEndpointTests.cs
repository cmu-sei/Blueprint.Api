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
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>CatalogInjectService</c> / <c>CatalogInjectController</c> - the six routes over the join rows that
/// put an inject in a catalog. Reads want <c>ViewCatalogs</c> or a unit the catalog is assigned to;
/// writes want <c>ManageCatalogs</c>.
/// </summary>
/// <remarks>
/// 401 and 403 for these routes live in <see cref="RouteAuthorizationTests"/>.
/// <para />
/// <c>DELETE catalogs/{catalogId}/injects/{injectId}</c> passes its two ids to
/// <c>DeleteByIdsAsync(catalogId, injectId, ct)</c> in the order the signature declares them - the
/// positive control for <c>CardTeamController.cs:192</c>, which transposes the same shape and so
/// deletes nothing. Two same-typed ids either side of a call site is a defect no compiler can find, so
/// the working copy earns <see cref="DeleteByIds_WithManageCatalogs_Is204"/>.
/// </remarks>
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
        var other = BlueprintAppFactory.Catalog(graph.InjectType.Id);
        await Seed(other);
        await Seed(BlueprintAppFactory.CatalogInject(other.Id, graph.Inject.Id));

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
        var actor = await Actor().SeedAsync();
        var graph = await SeedGraph();
        await AssignToAUnitWith(actor.Id, graph.Catalog.Id);

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

    /// <remarks>
    /// BUG: <c>GetAsync</c> puts the <em>CatalogInject</em>'s id to <c>CatalogViewRequirement.IsMet</c>
    /// as if it were a catalog id (<c>CatalogInjectService.cs:70</c>), so no catalog is ever found and
    /// the requirement is false for every caller. A unit member of the catalog therefore reads the whole
    /// list eleven lines above and cannot read one row of it. <c>GetByCatalogAsync</c> passes the right
    /// id and is the model to copy.
    /// </remarks>
    [Fact]
    public async Task Get_ForAMemberOfAUnitTheCatalogIsAssignedTo_Is403_ThoughTheyMayListTheSameRow()
    {
        var actor = await Actor().SeedAsync();
        var graph = await SeedGraph();
        await AssignToAUnitWith(actor.Id, graph.Catalog.Id);

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

        var stored = await NewContext().CatalogInjects.SingleAsync(x => x.Id == created.Id, Ct);
        Assert.Equal(graph.Catalog.Id, stored.CatalogId);
    }

    /// <remarks>
    /// BUG: <c>CreateAsync</c> validates neither parent, so an unknown catalog is a 500 from the foreign
    /// key rather than a 404. <c>CatalogUnitService.CreateAsync</c> checks both of its parents and
    /// answers two clean 404s; it is the model to copy and it is in the same commit.
    /// </remarks>
    [Fact]
    public async Task Create_ForACatalogThatIsNotThere_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();
        var graph = await SeedGraph(withJoinRow: false);

        var response = await Client(actor).PostAsJsonAsync(
            "api/cataloginjects", Body(Guid.NewGuid(), graph.Inject.Id), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <remarks>
    /// The bulk route answers 200 with the list rather than 201, having no single row to point at.
    /// </remarks>
    [Fact]
    public async Task CreateMultiple_WithManageCatalogs_Is200WithEveryRow()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();
        var graph = await SeedGraph(withJoinRow: false);
        var second = BlueprintAppFactory.Inject(graph.InjectType.Id);
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
        Assert.Equal(2, await NewContext().CatalogInjects.CountAsync(Ct));
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
        Assert.Empty(await NewContext().CatalogInjects.ToListAsync(Ct));
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
        Assert.Empty(await NewContext().CatalogInjects.ToListAsync(Ct));
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
        var injectType = BlueprintAppFactory.InjectType();
        await Seed(injectType);
        var inject = BlueprintAppFactory.Inject(injectType.Id);
        var catalog = BlueprintAppFactory.Catalog(injectType.Id);
        await Seed(inject, catalog);

        var catalogInject = BlueprintAppFactory.CatalogInject(catalog.Id, inject.Id);
        if (withJoinRow)
            await Seed(catalogInject);

        return new Graph(injectType, inject, catalog, catalogInject);
    }

    private async Task AssignToAUnitWith(Guid userId, Guid catalogId)
    {
        var unit = BlueprintAppFactory.Unit();
        await Seed(unit);
        await Seed(
            BlueprintAppFactory.UnitUser(userId, unit.Id),
            BlueprintAppFactory.CatalogUnit(unit.Id, catalogId));
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
