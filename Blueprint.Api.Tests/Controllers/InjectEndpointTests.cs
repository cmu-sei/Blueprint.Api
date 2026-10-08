// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary><c>InjectService</c> / <c>InjectController</c> - the six routes behind a catalog's injects. An
/// inject is the reusable half of a scenario event: <c>CreateScenarioEventsFromInjectsAsync</c> copies one
/// into a MSEL's timeline along with its data values, which <c>ScenarioEventFromInjectsTests</c> covers
/// from the timeline side and nothing covered from this one.</summary>
public class InjectEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET catalogs/{catalogId}/injects
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByCatalog_ReturnsTheCatalogsInjects()
    {
        var type = await SeedInjectType();
        var catalog = await SeedCatalog(type.Id, isPublic: true);
        var inject = await SeedInject(type.Id, catalog.Id);
        var actor = await Actor().SeedAsync();

        var injects = await Read<List<ViewModels.Injectm>>(
            await Client(actor).GetAsync(InjectsOf(catalog.Id), Ct));

        Assert.Equal(inject.Id, Assert.Single(injects).Id);
        Assert.Equal(inject.Name, Assert.Single(injects).Name);
        Assert.Equal(type.Id, Assert.Single(injects).InjectTypeId);
    }

    [Fact]
    public async Task GetByCatalog_DoesNotReturnAnotherCatalogsInjects()
    {
        var type = await SeedInjectType();
        var catalog = await SeedCatalog(type.Id, isPublic: true);
        var other = await SeedCatalog(type.Id, isPublic: true);
        var inject = await SeedInject(type.Id, catalog.Id);
        await SeedInject(type.Id, other.Id);
        var actor = await Actor().SeedAsync();

        var injects = await Read<List<ViewModels.Injectm>>(
            await Client(actor).GetAsync(InjectsOf(catalog.Id), Ct));

        Assert.Equal(inject.Id, Assert.Single(injects).Id);
    }

    /// <remarks>
    /// The <c>Include(m =&gt; m.Inject.DataValues)</c> survives the <c>Select(m =&gt; m.Inject)</c> projection,
    /// so the list route answers each inject's cells - unlike the two inject-type routes, which answer an
    /// always-empty <c>dataFields</c> (<c>InjectTypeEndpointTests</c>).
    /// </remarks>
    [Fact]
    public async Task GetByCatalog_IncludesTheDataValues()
    {
        var type = await SeedInjectType();
        var field = await SeedDataField(type.Id);
        var catalog = await SeedCatalog(type.Id, isPublic: true);
        var inject = await SeedInject(type.Id, catalog.Id);
        await SeedDataValue(field.Id, inject.Id, "cell");
        var actor = await Actor().SeedAsync();

        var injects = await Read<List<ViewModels.Injectm>>(
            await Client(actor).GetAsync(InjectsOf(catalog.Id), Ct));

        var value = Assert.Single(Assert.Single(injects).DataValues);
        Assert.Equal("cell", value.Value);
        Assert.Equal(field.Id, value.DataFieldId);
        Assert.Equal(inject.Id, value.InjectId);
    }

    [Fact]
    public async Task GetByCatalog_ForAPublicCatalog_WithNoPermissionAndNoUnit_Is200()
    {
        var type = await SeedInjectType();
        var catalog = await SeedCatalog(type.Id, isPublic: true);
        await SeedInject(type.Id, catalog.Id);
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync(InjectsOf(catalog.Id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetByCatalog_ForAPrivateCatalog_is_forbidden_for_a_caller_holding_only_ViewCatalogs()
    {
        var type = await SeedInjectType();
        var catalog = await SeedCatalog(type.Id);
        await SeedInject(type.Id, catalog.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewCatalogs).SeedAsync();

        var response = await Client(actor).GetAsync(InjectsOf(catalog.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetByCatalog_ForAPrivateCatalog_ForItsCreator_Is200()
    {
        var actor = await Actor().SeedAsync();
        var type = await SeedInjectType();
        var catalog = await SeedCatalog(type.Id, createdBy: actor.Id);
        var inject = await SeedInject(type.Id, catalog.Id);

        var injects = await Read<List<ViewModels.Injectm>>(
            await Client(actor).GetAsync(InjectsOf(catalog.Id), Ct));

        Assert.Equal(inject.Id, Assert.Single(injects).Id);
    }

    /// <remarks>
    /// The unit path is a <c>CatalogUnit</c> row plus a <c>UnitUser</c> row, with no role anywhere in it -
    /// the whole conjunction <c>CatalogViewRequirementTests</c> pins. This is the seam
    /// <see cref="Get_ForAUnitMemberOfTheCatalog_Is403"/> shows the single read failing to use.
    /// </remarks>
    [Fact]
    public async Task GetByCatalog_ForAPrivateCatalog_ForAMemberOfAUnitOnIt_Is200()
    {
        var type = await SeedInjectType();
        var catalog = await SeedCatalog(type.Id);
        var inject = await SeedInject(type.Id, catalog.Id);
        var unit = TestData.Unit();
        await Seed(unit, TestData.CatalogUnit(unit.Id, catalog.Id));
        var actor = await Actor().InUnit(unit).SeedAsync();

        var injects = await Read<List<ViewModels.Injectm>>(
            await Client(actor).GetAsync(InjectsOf(catalog.Id), Ct));

        Assert.Equal(inject.Id, Assert.Single(injects).Id);
    }

    [Fact]
    public async Task GetByCatalog_ForAPrivateCatalog_WithViewMsels_Is200()
    {
        var type = await SeedInjectType();
        var catalog = await SeedCatalog(type.Id);
        var inject = await SeedInject(type.Id, catalog.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var injects = await Read<List<ViewModels.Injectm>>(
            await Client(actor).GetAsync(InjectsOf(catalog.Id), Ct));

        Assert.Equal(inject.Id, Assert.Single(injects).Id);
    }

    /// <summary>An unknown catalog is a 403, the answer <c>CatalogViewRequirement</c> gives a catalog it cannot
    /// find.</summary>
    [Fact]
    public async Task GetByCatalog_ForACatalogThatIsNotThere_is_forbidden_for_a_caller_holding_only_ViewCatalogs()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewCatalogs).SeedAsync();

        var response = await Client(actor).GetAsync(InjectsOf(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// And for a caller holding <c>ViewMsels</c> the requirement is never asked, so the same request is an
    /// empty list. One route, two answers to "that catalog does not exist", chosen by permission.
    /// </remarks>
    [Fact]
    public async Task GetByCatalog_ForACatalogThatIsNotThere_WithViewMsels_Is200AndEmpty()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var injects = await Read<List<ViewModels.Injectm>>(
            await Client(actor).GetAsync(InjectsOf(Guid.NewGuid()), Ct));

        Assert.Empty(injects);
    }

    // ---------------------------------------------------------------------------------------------
    // GET injecttypes/{injectTypeId}/injects
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByInjectType_ReturnsTheTypesInjects()
    {
        var type = await SeedInjectType();
        var inject = await SeedInject(type.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var injects = await Read<List<ViewModels.Injectm>>(
            await Client(actor).GetAsync(InjectsOfType(type.Id), Ct));

        Assert.Equal(inject.Id, Assert.Single(injects).Id);
    }

    [Fact]
    public async Task GetByInjectType_DoesNotReturnAnotherTypesInjects()
    {
        var type = await SeedInjectType();
        var other = await SeedInjectType();
        var inject = await SeedInject(type.Id);
        await SeedInject(other.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var injects = await Read<List<ViewModels.Injectm>>(
            await Client(actor).GetAsync(InjectsOfType(type.Id), Ct));

        Assert.Equal(inject.Id, Assert.Single(injects).Id);
    }

    [Fact]
    public async Task GetByInjectType_IncludesTheDataValues()
    {
        var type = await SeedInjectType();
        var field = await SeedDataField(type.Id);
        var inject = await SeedInject(type.Id);
        await SeedDataValue(field.Id, inject.Id, "cell");
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var injects = await Read<List<ViewModels.Injectm>>(
            await Client(actor).GetAsync(InjectsOfType(type.Id), Ct));

        Assert.Equal("cell", Assert.Single(Assert.Single(injects).DataValues).Value);
    }

    /// <summary>Get by inject type is not scoped to any catalog.</summary>
    [Fact]
    public async Task GetByInjectType_IsNotScopedToAnyCatalog()
    {
        var type = await SeedInjectType();
        var privateCatalog = await SeedCatalog(type.Id);
        var inCatalog = await SeedInject(type.Id, privateCatalog.Id);
        var orphan = await SeedInject(type.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var injects = await Read<List<ViewModels.Injectm>>(
            await Client(actor).GetAsync(InjectsOfType(type.Id), Ct));

        Assert.Equal(
            new HashSet<Guid> { inCatalog.Id, orphan.Id },
            injects.Select(x => x.Id).ToHashSet());

        var byCatalog = await Read<List<ViewModels.Injectm>>(
            await ClientFor(actor.Id, null).GetAsync(InjectsOf(privateCatalog.Id), Ct));
        Assert.Equal(inCatalog.Id, Assert.Single(byCatalog).Id);
    }

    [Fact]
    public async Task GetByInjectType_is_forbidden_for_a_caller_holding_only_ViewCatalogs()
    {
        var type = await SeedInjectType();
        await SeedInject(type.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewCatalogs).SeedAsync();

        var response = await Client(actor).GetAsync(InjectsOfType(type.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetByInjectType_ForATypeThatIsNotThere_Is200AndEmpty()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var injects = await Read<List<ViewModels.Injectm>>(
            await Client(actor).GetAsync(InjectsOfType(Guid.NewGuid()), Ct));

        Assert.Empty(injects);
    }

    // ---------------------------------------------------------------------------------------------
    // GET injects/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_WithViewMsels_ReturnsTheInjectAndItsDataValues()
    {
        var type = await SeedInjectType();
        var field = await SeedDataField(type.Id);
        var catalog = await SeedCatalog(type.Id, isPublic: true);
        var inject = await SeedInject(type.Id, catalog.Id);
        await SeedDataValue(field.Id, inject.Id, "cell");
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var answer = await Read<ViewModels.Injectm>(await Client(actor).GetAsync(Inject(inject.Id), Ct));

        Assert.Equal(inject.Id, answer.Id);
        Assert.Equal(inject.Name, answer.Name);
        Assert.Equal("cell", Assert.Single(answer.DataValues).Value);
    }

    /// <summary>Get for the catalogs creator is answered with a 403.</summary>
    [Fact]
    public async Task Get_ForTheCatalogsCreator_Is403()
    {
        var actor = await Actor().SeedAsync();
        var type = await SeedInjectType();
        var catalog = await SeedCatalog(type.Id, createdBy: actor.Id, isPublic: true);
        var inject = await SeedInject(type.Id, catalog.Id);

        var response = await Client(actor).GetAsync(Inject(inject.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await ClientFor(actor.Id, null).GetAsync(InjectsOf(catalog.Id), Ct)).StatusCode);
    }

    /// <summary>Get for a unit member of the catalog is answered with a 403.</summary>
    [Fact]
    public async Task Get_ForAUnitMemberOfTheCatalog_Is403()
    {
        var type = await SeedInjectType();
        var catalog = await SeedCatalog(type.Id);
        var inject = await SeedInject(type.Id, catalog.Id);
        var unit = TestData.Unit();
        await Seed(unit, TestData.CatalogUnit(unit.Id, catalog.Id));
        var actor = await Actor().InUnit(unit).SeedAsync();

        var response = await Client(actor).GetAsync(Inject(inject.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Get for an inject in a public catalog is answered with a 403.</summary>
    [Fact]
    public async Task Get_ForAnInjectInAPublicCatalog_Is403()
    {
        var type = await SeedInjectType();
        var catalog = await SeedCatalog(type.Id, isPublic: true);
        var inject = await SeedInject(type.Id, catalog.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewCatalogs).SeedAsync();

        var response = await Client(actor).GetAsync(Inject(inject.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Get for a catalog unit row whose primary key equals the callers id reads any inject.</summary>
    [Fact]
    public async Task Get_ForACatalogUnitRowWhosePrimaryKeyEqualsTheCallersId_ReadsAnyInject()
    {
        var actor = await Actor().SeedAsync();
        var stranger = await Actor().SeedAsync();
        var type = await SeedInjectType();
        var catalog = await SeedCatalog(type.Id);
        var inCatalog = await SeedInject(type.Id, catalog.Id);
        var orphan = await SeedInject(type.Id);
        var unit = await Db.AddUnitAsync(stranger.Id, Ct);
        await Seed(TestData.CatalogUnit(unit.Id, catalog.Id, id: actor.Id));

        Assert.Equal(
            HttpStatusCode.OK, (await Client(actor).GetAsync(Inject(inCatalog.Id), Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK, (await ClientFor(actor.Id, null).GetAsync(Inject(orphan.Id), Ct)).StatusCode);
    }

    /// <summary>Get for an id that is not there is answered with a 500.</summary>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is500RatherThanThe404TheDeadCheckPromises()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync(Inject(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Sequence contains no elements.", failure.Title);
        Assert.Contains("InjectService.GetAsync", failure.Detail);
    }

    /// <summary>Get for an id that is not there is that same 500 without any permission.</summary>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_IsThatSame500WithoutAnyPermission()
    {
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync(Inject(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Sequence contains no elements.", failure.Title);
        Assert.Contains("InjectService.GetAsync", failure.Detail);
    }

    // ---------------------------------------------------------------------------------------------
    // POST catalog/{catalogId}/injects
    // ---------------------------------------------------------------------------------------------

    /// <summary>Create with edit MSELs is answered with a 200 and stores the inject.</summary>
    [Fact]
    public async Task Create_WithEditMsels_Is200AndStoresTheInject()
    {
        var type = await SeedInjectType();
        var catalog = await SeedCatalog(type.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(Client(actor), catalog.Id, Body(type.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await Read<ViewModels.Injectm>(response);
        var stored = await Stored(created.Id);
        Assert.Equal("posted by the test", stored.Name);
        Assert.Equal(type.Id, stored.InjectTypeId);
    }

    /// <summary>Create declares created and answers 200 with no location header.</summary>
    [Fact]
    public async Task Create_DeclaresCreatedAndAnswers200WithNoLocationHeader()
    {
        var type = await SeedInjectType();
        var catalog = await SeedCatalog(type.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(Client(actor), catalog.Id, Body(type.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Create_CreatesADataValueForEveryFieldOnTheType()
    {
        var type = await SeedInjectType();
        var first = await SeedDataField(type.Id, displayOrder: 1);
        var second = await SeedDataField(type.Id, displayOrder: 2);
        var catalog = await SeedCatalog(type.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var created = await Read<ViewModels.Injectm>(
            await Post(Client(actor), catalog.Id, Body(type.Id)));

        var values = await StoredValues(created.Id);
        Assert.Equal(
            new HashSet<Guid> { first.Id, second.Id },
            values.Select(x => x.DataFieldId).ToHashSet());
        Assert.All(values, x => Assert.Null(x.Value));
        Assert.All(values, x => Assert.Equal(actor.Id, x.CreatedBy));
    }

    [Fact]
    public async Task Create_KeepsTheValuesTheBodyCarriesForTheTypesOwnFields()
    {
        var type = await SeedInjectType();
        var field = await SeedDataField(type.Id);
        var catalog = await SeedCatalog(type.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var body = Body(type.Id) with { DataValues = [Value(field.Id, "posted")] };

        var created = await Read<ViewModels.Injectm>(await Post(Client(actor), catalog.Id, body));

        Assert.Equal("posted", Assert.Single(await StoredValues(created.Id)).Value);
    }

    /// <summary>Create silently drops a value naming a field that is not on the type.</summary>
    [Fact]
    public async Task Create_SilentlyDropsAValueNamingAFieldThatIsNotOnTheType()
    {
        var type = await SeedInjectType();
        var mine = await SeedDataField(type.Id);
        var theirs = await SeedDataField((await SeedInjectType()).Id);
        var catalog = await SeedCatalog(type.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var body = Body(type.Id) with
        {
            DataValues = [Value(mine.Id, "kept"), Value(theirs.Id, "dropped")]
        };

        var response = await Post(Client(actor), catalog.Id, body);

        var created = await Read<ViewModels.Injectm>(response);
        Assert.Equal(mine.Id, Assert.Single(created.DataValues).DataFieldId);
        Assert.Equal("kept", Assert.Single(await StoredValues(created.Id)).Value);
    }

    /// <summary>A create keeps the id the body carries.</summary>
    [Fact]
    public async Task Create_KeepsTheClientsId()
    {
        var type = await SeedInjectType();
        var catalog = await SeedCatalog(type.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var chosen = Guid.NewGuid();

        var created = await Read<ViewModels.Injectm>(
            await Post(Client(actor), catalog.Id, Body(type.Id) with { Id = chosen }));

        Assert.Equal(chosen, created.Id);
        Assert.NotNull(await Stored(chosen));
    }

    [Fact]
    public async Task Create_StampsTheAuditFieldsOnTheServer()
    {
        var type = await SeedInjectType();
        var catalog = await SeedCatalog(type.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var before = DateTime.UtcNow;
        var body = Body(type.Id) with
        {
            CreatedBy = Guid.NewGuid(),
            DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ModifiedBy = Guid.NewGuid(),
            DateModified = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        var created = await Read<ViewModels.Injectm>(await Post(Client(actor), catalog.Id, body));

        var stored = await Stored(created.Id);
        Assert.Equal(actor.Id, stored.CreatedBy);
        AssertStampedBetween(stored.DateCreated, before, DateTime.UtcNow);
        Assert.Null(stored.ModifiedBy);
        Assert.Null(stored.DateModified);
    }

    [Fact]
    public async Task Create_AlsoStoresTheCatalogInjectRow()
    {
        var type = await SeedInjectType();
        var catalog = await SeedCatalog(type.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var created = await Read<ViewModels.Injectm>(
            await Post(Client(actor), catalog.Id, Body(type.Id)));

        await using var context = NewContext();
        var join = await context.CatalogInjects.AsNoTracking()
            .SingleAsync(x => x.InjectId == created.Id, Ct);
        Assert.Equal(catalog.Id, join.CatalogId);
    }

    [Fact]
    public async Task Create_MarksTheCatalogModified()
    {
        var type = await SeedInjectType();
        var catalog = await SeedCatalog(type.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var before = DateTime.UtcNow;

        await Post(Client(actor), catalog.Id, Body(type.Id));

        var stored = await StoredCatalog(catalog.Id);
        AssertStampedBetween(stored.DateModified, before, DateTime.UtcNow);
        Assert.Equal(actor.Id, stored.ModifiedBy);
    }

    /// <summary>Create for an inject type that is not there is answered with a 500.</summary>
    [Fact]
    public async Task Create_ForAnInjectTypeThatIsNotThere_Is500()
    {
        var catalog = await SeedCatalog((await SeedInjectType()).Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(Client(actor), catalog.Id, Body(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Object reference not set to an instance of an object.", failure.Title);
        Assert.Contains("InjectService.CreateAsync", failure.Detail);
    }

    /// <summary>Create for a catalog that is not there is answered with a 500 and stores nothing.</summary>
    [Fact]
    public async Task Create_ForACatalogThatIsNotThere_Is500AndStoresNothing()
    {
        var type = await SeedInjectType();
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var chosen = Guid.NewGuid();

        var response = await Post(
            Client(actor), Guid.NewGuid(), Body(type.Id) with { Id = chosen });

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("An error occurred while saving the entity changes. See the inner exception for details.", failure.Title);
        Assert.Contains("InjectService.CreateAsync", failure.Detail);
        Assert.Null(await Stored(chosen));
    }

    /// <summary>Create in a private catalog the caller cannot read is answered with a 200.</summary>
    [Fact]
    public async Task Create_InAPrivateCatalogTheCallerCannotRead_Is200()
    {
        var type = await SeedInjectType();
        var catalog = await SeedCatalog(type.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(Client(actor), catalog.Id, Body(type.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await ClientFor(actor.Id, null).GetAsync(InjectsOf(catalog.Id), Ct)).StatusCode);
    }

    [Fact]
    public async Task Create_WithoutEditMsels_Is403()
    {
        var type = await SeedInjectType();
        var catalog = await SeedCatalog(type.Id, createdBy: null, isPublic: true);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Post(Client(actor), catalog.Id, Body(type.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithNoBody_Is400()
    {
        var type = await SeedInjectType();
        var catalog = await SeedCatalog(type.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).PostAsync(CreateIn(catalog.Id), EmptyJson(), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>A new inject is broadcast to the admin data group, and its data value to the all-zeros
    /// group.</summary>
    [Fact]
    public async Task Create_BroadcastsToTheAdminDataGroupAndTheAllZerosGroup()
    {
        var type = await SeedInjectType();
        await SeedDataField(type.Id);
        var catalog = await SeedCatalog(type.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var created = await Read<ViewModels.Injectm>(
            await Post(Client(actor), catalog.Id, Body(type.Id)));

        Assert.Equal([MainHub.ADMIN_DATA_GROUP], Hub.Recipients(MainHubMethods.InjectCreated, created.Id, catalog.Id, type.Id));
        Assert.Equal(created.Id, Assert.IsType<ViewModels.Injectm>(
            Hub.Of(MainHubMethods.InjectCreated, created.Id, catalog.Id, type.Id)[0].Payload).Id);
        Assert.Equal(
            [Guid.Empty.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.DataValueCreated, Guid.Empty));
    }

    // ---------------------------------------------------------------------------------------------
    // PUT injects/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_WithEditMsels_Is200AndStoresTheChange()
    {
        var type = await SeedInjectType();
        var inject = await SeedInject(type.Id, (await SeedCatalog(type.Id)).Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Put(
            Client(actor), inject.Id, BodyFor(inject) with { Name = "renamed" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("renamed", (await Stored(inject.Id)).Name);
        Assert.Equal("renamed", (await Read<ViewModels.Injectm>(response)).Name);
    }

    [Fact]
    public async Task Update_StampsTheInjectsOwnAuditFieldsOnTheServer()
    {
        var type = await SeedInjectType();
        var inject = await SeedInject(type.Id, (await SeedCatalog(type.Id)).Id);
        var created = (await Stored(inject.Id)).DateCreated;
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var before = DateTime.UtcNow;
        var body = BodyFor(inject) with
        {
            Name = "renamed",
            CreatedBy = Guid.NewGuid(),
            DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ModifiedBy = Guid.NewGuid()
        };

        await Put(Client(actor), inject.Id, body);

        var stored = await Stored(inject.Id);
        Assert.Equal(inject.CreatedBy, stored.CreatedBy);
        Assert.Equal(created, stored.DateCreated);
        Assert.Equal(actor.Id, stored.ModifiedBy);
        AssertStampedBetween(stored.DateModified, before, DateTime.UtcNow);
    }

    /// <summary>Update lets the client write a data values created by and date created.</summary>
    [Fact]
    public async Task Update_LetsTheClientWriteADataValuesCreatedByAndDateCreated()
    {
        var type = await SeedInjectType();
        var field = await SeedDataField(type.Id);
        var inject = await SeedInject(type.Id, (await SeedCatalog(type.Id)).Id);
        var value = await SeedDataValue(field.Id, inject.Id, "before");
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var spoofed = Guid.NewGuid();
        var body = BodyFor(inject) with
        {
            DataValues =
            [
                ValueFor(value) with
                {
                    Value = "after",
                    CreatedBy = spoofed,
                    DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                }
            ]
        };

        Assert.Equal(HttpStatusCode.OK, (await Put(Client(actor), inject.Id, body)).StatusCode);

        var stored = Assert.Single(await StoredValues(inject.Id));
        Assert.Equal("after", stored.Value);
        Assert.Equal(spoofed, stored.CreatedBy);
        Assert.Equal(new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc), stored.DateCreated);
        Assert.Null(stored.ModifiedBy);
        Assert.NotNull(stored.DateModified);
    }

    /// <summary>Update may add a data value naming another inject types field.</summary>
    [Fact]
    public async Task Update_MayAddADataValueNamingAnotherInjectTypesField()
    {
        var type = await SeedInjectType();
        var mine = await SeedDataField(type.Id);
        var theirs = await SeedDataField((await SeedInjectType()).Id);
        var inject = await SeedInject(type.Id, (await SeedCatalog(type.Id)).Id);
        var value = await SeedDataValue(mine.Id, inject.Id, "before");
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var body = BodyFor(inject) with
        {
            DataValues = [ValueFor(value), Value(theirs.Id, "foreign")]
        };

        Assert.Equal(HttpStatusCode.OK, (await Put(Client(actor), inject.Id, body)).StatusCode);

        var stored = await StoredValues(inject.Id);
        Assert.Equal(2, stored.Count);
        Assert.Equal("foreign", Assert.Single(stored, x => x.DataFieldId == theirs.Id).Value);
    }

    /// <summary>Update that repoints the only data value is answered with a 500 and stores nothing.</summary>
    [Fact]
    public async Task Update_ThatRepointsTheOnlyDataValue_Is500AndStoresNothing()
    {
        var type = await SeedInjectType();
        var mine = await SeedDataField(type.Id);
        var theirs = await SeedDataField((await SeedInjectType()).Id);
        var inject = await SeedInject(type.Id, (await SeedCatalog(type.Id)).Id);
        var value = await SeedDataValue(mine.Id, inject.Id, "before");
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var body = BodyFor(inject) with
        {
            Name = "renamed",
            DataValues = [ValueFor(value) with { DataFieldId = theirs.Id }]
        };

        var response = await Put(Client(actor), inject.Id, body);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Object reference not set to an instance of an object.", failure.Title);
        Assert.Contains("InjectService.UpdateAsync", failure.Detail);
        Assert.Equal(inject.Name, (await Stored(inject.Id)).Name);
        Assert.Equal(mine.Id, Assert.Single(await StoredValues(inject.Id)).DataFieldId);
    }

    /// <summary>Update may retype the inject and orphan its data values.</summary>
    [Fact]
    public async Task Update_MayRetypeTheInjectAndOrphanItsDataValues()
    {
        var type = await SeedInjectType();
        var field = await SeedDataField(type.Id);
        var other = await SeedInjectType();
        var inject = await SeedInject(type.Id, (await SeedCatalog(type.Id)).Id);
        var value = await SeedDataValue(field.Id, inject.Id, "before");
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var body = BodyFor(inject) with
        {
            InjectTypeId = other.Id,
            DataValues = [ValueFor(value)]
        };

        Assert.Equal(HttpStatusCode.OK, (await Put(Client(actor), inject.Id, body)).StatusCode);

        Assert.Equal(other.Id, (await Stored(inject.Id)).InjectTypeId);
        Assert.Equal(field.Id, Assert.Single(await StoredValues(inject.Id)).DataFieldId);
    }

    /// <summary>Update after a data field is added to the type is answered with a 500.</summary>
    [Fact]
    public async Task Update_AfterADataFieldIsAddedToTheType_Is500()
    {
        var type = await SeedInjectType();
        var field = await SeedDataField(type.Id, displayOrder: 1);
        var inject = await SeedInject(type.Id, (await SeedCatalog(type.Id)).Id);
        var value = await SeedDataValue(field.Id, inject.Id, "before");
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        await SeedDataField(type.Id, displayOrder: 2);
        var body = BodyFor(inject) with { Name = "renamed", DataValues = [ValueFor(value)] };

        var response = await Put(Client(actor), inject.Id, body);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Object reference not set to an instance of an object.", failure.Title);
        Assert.Contains("InjectService.UpdateAsync", failure.Detail);
        Assert.Equal(inject.Name, (await Stored(inject.Id)).Name);
    }

    /// <summary>Update with a body that omits a value for a field the type has is answered with a 500.</summary>
    [Fact]
    public async Task Update_WithABodyThatOmitsAValueForAFieldTheTypeHas_Is500()
    {
        var type = await SeedInjectType();
        await SeedDataField(type.Id);
        var inject = await SeedInject(type.Id, (await SeedCatalog(type.Id)).Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Put(Client(actor), inject.Id, BodyFor(inject));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Object reference not set to an instance of an object.", failure.Title);
        Assert.Contains("InjectService.UpdateAsync", failure.Detail);
    }

    /// <remarks>
    /// Whereas an inject type with no data fields at all leaves the loop with nothing to iterate, which is the
    /// only shape in which a PUT succeeds without the client echoing every cell back.
    /// </remarks>
    [Fact]
    public async Task Update_ForATypeWithNoDataFields_Is200()
    {
        var type = await SeedInjectType();
        var inject = await SeedInject(type.Id, (await SeedCatalog(type.Id)).Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Put(Client(actor), inject.Id, BodyFor(inject) with { Name = "renamed" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Update_ForAnIdThatIsNotThere_Is404()
    {
        var type = await SeedInjectType();
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Put(Client(actor), Guid.NewGuid(), Body(type.Id));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Update whose body id is not the routes is answered with a 500.</summary>
    [Fact]
    public async Task Update_WhoseBodyIdIsNotTheRoutes_Is500()
    {
        var type = await SeedInjectType();
        var inject = await SeedInject(type.Id, (await SeedCatalog(type.Id)).Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Put(
            Client(actor), inject.Id, BodyFor(inject) with { Id = Guid.NewGuid() });

        Assert.Equal("The property 'InjectEntity.Id' is part of a key and so cannot be modified or marked as modified. To change the principal of an existing entity with an identifying foreign key, first delete the dependent and invoke 'SaveChanges', and then associate the dependent with the new principal.", (await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
    }

    [Fact]
    public async Task Update_WithoutEditMsels_Is403()
    {
        var type = await SeedInjectType();
        var inject = await SeedInject(type.Id, (await SeedCatalog(type.Id)).Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Put(Client(actor), inject.Id, BodyFor(inject) with { Name = "renamed" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(inject.Name, (await Stored(inject.Id)).Name);
    }

    /// <summary>Update for an inject in a private catalog the caller cannot read is answered with a 200.</summary>
    [Fact]
    public async Task Update_ForAnInjectInAPrivateCatalogTheCallerCannotRead_Is200()
    {
        var type = await SeedInjectType();
        var catalog = await SeedCatalog(type.Id);
        var inject = await SeedInject(type.Id, catalog.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Put(Client(actor), inject.Id, BodyFor(inject) with { Name = "renamed" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await ClientFor(actor.Id, null).GetAsync(InjectsOf(catalog.Id), Ct)).StatusCode);
    }

    /// <summary>Update does not mark the catalog modified.</summary>
    [Fact]
    public async Task Update_DoesNotMarkTheCatalogModified()
    {
        var type = await SeedInjectType();
        var catalog = await SeedCatalog(type.Id);
        var inject = await SeedInject(type.Id, catalog.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        await Put(Client(actor), inject.Id, BodyFor(inject) with { Name = "renamed" });

        Assert.Null((await StoredCatalog(catalog.Id)).DateModified);
    }

    [Fact]
    public async Task Update_BroadcastsToTheAdminDataGroupOnly()
    {
        var type = await SeedInjectType();
        var inject = await SeedInject(type.Id, (await SeedCatalog(type.Id)).Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        await Put(Client(actor), inject.Id, BodyFor(inject) with { Name = "renamed" });

        Assert.Equal([MainHub.ADMIN_DATA_GROUP], Hub.Recipients(MainHubMethods.InjectUpdated, inject.Id, type.Id));
        Assert.Equal("renamed", Assert.IsType<ViewModels.Injectm>(
            Hub.Of(MainHubMethods.InjectUpdated, inject.Id, type.Id)[0].Payload).Name);
    }

    [Fact]
    public async Task Update_WithNoBody_Is400()
    {
        var type = await SeedInjectType();
        var inject = await SeedInject(type.Id, (await SeedCatalog(type.Id)).Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).PutAsync(Inject(inject.Id), EmptyJson(), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE injects/{id}
    // ---------------------------------------------------------------------------------------------

    /// <summary>Delete with edit MSELs is answered with a 200 with true and removes the inject.</summary>
    [Fact]
    public async Task Delete_WithEditMsels_Is200WithTrueAndRemovesTheInject()
    {
        var type = await SeedInjectType();
        var inject = await SeedInject(type.Id, (await SeedCatalog(type.Id)).Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync(Inject(inject.Id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("true", await response.Content.ReadAsStringAsync(Ct));
        Assert.Null(await Stored(inject.Id));
    }

    [Fact]
    public async Task Delete_TakesTheDataValuesAndTheCatalogJoinRowsWithIt()
    {
        var type = await SeedInjectType();
        var field = await SeedDataField(type.Id);
        var catalog = await SeedCatalog(type.Id);
        var inject = await SeedInject(type.Id, catalog.Id);
        await SeedDataValue(field.Id, inject.Id, "cell");
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        await Client(actor).DeleteAsync(Inject(inject.Id), Ct);

        Assert.Empty(await StoredValues(inject.Id));
        await using var context = NewContext();
        Assert.Equal(0, await context.CatalogInjects.CountAsync(x => x.InjectId == inject.Id, Ct));
    }

    /// <summary>Delete for an inject that another inject requires is answered with a 500.</summary>
    [Fact]
    public async Task Delete_ForAnInjectThatAnotherInjectRequires_Is500()
    {
        var type = await SeedInjectType();
        var catalog = await SeedCatalog(type.Id);
        var required = await SeedInject(type.Id, catalog.Id);
        var dependent = TestData.Inject(type.Id);
        dependent.RequiresInjectId = required.Id;
        await Seed(dependent);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync(Inject(required.Id), Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("An error occurred while saving the entity changes. See the inner exception for details.", failure.Title);
        Assert.Contains("InjectService.DeleteAsync", failure.Detail);
        Assert.NotNull(await Stored(required.Id));
    }

    [Fact]
    public async Task Delete_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync(Inject(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_WithoutEditMsels_Is403()
    {
        var type = await SeedInjectType();
        var inject = await SeedInject(type.Id, (await SeedCatalog(type.Id)).Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync(Inject(inject.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(await Stored(inject.Id));
    }

    /// <summary>Delete from a private catalog the caller cannot read is answered with a 200.</summary>
    [Fact]
    public async Task Delete_FromAPrivateCatalogTheCallerCannotRead_Is200()
    {
        var type = await SeedInjectType();
        var catalog = await SeedCatalog(type.Id);
        var inject = await SeedInject(type.Id, catalog.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync(Inject(inject.Id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await ClientFor(actor.Id, null).GetAsync(InjectsOf(catalog.Id), Ct)).StatusCode);
    }

    [Fact]
    public async Task Delete_DoesNotMarkTheCatalogModified()
    {
        var type = await SeedInjectType();
        var catalog = await SeedCatalog(type.Id);
        var inject = await SeedInject(type.Id, catalog.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        await Client(actor).DeleteAsync(Inject(inject.Id), Ct);

        Assert.Null((await StoredCatalog(catalog.Id)).DateModified);
    }

    [Fact]
    public async Task Delete_BroadcastsToTheAdminDataGroupOnly()
    {
        var type = await SeedInjectType();
        var inject = await SeedInject(type.Id, (await SeedCatalog(type.Id)).Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        await Client(actor).DeleteAsync(Inject(inject.Id), Ct);

        Assert.Equal([MainHub.ADMIN_DATA_GROUP], Hub.Recipients(MainHubMethods.InjectDeleted, inject.Id, type.Id));
        Assert.Equal(inject.Id, Assert.IsType<Guid>(Hub.Of(MainHubMethods.InjectDeleted, inject.Id, type.Id)[0].Payload));
    }

    // ---------------------------------------------------------------------------------------------
    // Authentication
    // ---------------------------------------------------------------------------------------------

    /// <summary>Every route answers 401 to a request with no identity; the anonymous client is the case under
    /// test.</summary>
    [Theory]
    [InlineData("GET", "catalogs/00000000-0000-0000-0000-000000000001/injects")]
    [InlineData("GET", "injecttypes/00000000-0000-0000-0000-000000000001/injects")]
    [InlineData("GET", "injects/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "catalog/00000000-0000-0000-0000-000000000001/injects")]
    [InlineData("PUT", "injects/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "injects/00000000-0000-0000-0000-000000000001")]
    public async Task EveryRouteRefusesAnUnauthenticatedRequest(string method, string route)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/{route}")
        {
            Content = JsonContent.Create(new { })
        };

        var response = await Client().SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------

    private const string Injects = "/api/injects";

    private static string Inject(Guid id) => $"{Injects}/{id}";

    private static string InjectsOf(Guid catalogId) => $"/api/catalogs/{catalogId}/injects";

    private static string InjectsOfType(Guid injectTypeId) =>
        $"/api/injecttypes/{injectTypeId}/injects";

    /// <summary>
    /// The create route, spelled <c>catalog</c> in the singular where <see cref="InjectsOf"/> is plural.
    /// </summary>
    private static string CreateIn(Guid catalogId) => $"/api/catalog/{catalogId}/injects";

    /// <summary>
    /// The wire shape of an inject. A record rather than an anonymous type so a test can vary one property of
    /// a stored row with a <c>with</c> expression.
    /// </summary>
    /// <remarks>
    /// <c>DateCreated</c> and <c>CreatedBy</c> are non-nullable on <c>ViewModels.Base</c>, so they are always
    /// sent as values: a null is a 400 that never reaches the controller.
    /// </remarks>
    private sealed record InjectBody
    {
        public Guid Id { get; init; }
        public string Name { get; init; }
        public string Description { get; init; }
        public Guid InjectTypeId { get; init; }
        public Guid? RequiresInjectId { get; init; }
        public List<DataValueBody> DataValues { get; init; } = [];

        public Guid CreatedBy { get; init; }
        public DateTime DateCreated { get; init; }
        public Guid? ModifiedBy { get; init; }
        public DateTime? DateModified { get; init; }
    }

    private sealed record DataValueBody
    {
        public Guid Id { get; init; }
        public string Value { get; init; }
        public Guid? ScenarioEventId { get; init; }
        public Guid? InjectId { get; init; }
        public Guid DataFieldId { get; init; }
        public string CellMetadata { get; init; }

        public Guid CreatedBy { get; init; }
        public DateTime DateCreated { get; init; }
        public Guid? ModifiedBy { get; init; }
        public DateTime? DateModified { get; init; }
    }

    private static InjectBody Body(Guid injectTypeId) => new()
    {
        Name = "posted by the test",
        Description = "description posted by the test",
        InjectTypeId = injectTypeId
    };

    private static InjectBody BodyFor(InjectEntity inject) => new()
    {
        Id = inject.Id,
        Name = inject.Name,
        Description = inject.Description,
        InjectTypeId = inject.InjectTypeId,
        RequiresInjectId = inject.RequiresInjectId,
        CreatedBy = inject.CreatedBy,
        DateCreated = inject.DateCreated
    };

    /// <summary>
    /// A data value for the create path, which supplies the <c>InjectId</c> itself.
    /// </summary>
    private static DataValueBody Value(Guid dataFieldId, string value) => new()
    {
        DataFieldId = dataFieldId,
        Value = value
    };

    /// <summary>
    /// A data value echoing a stored row, which is what a client PUTs back after a GET - and the shape that
    /// makes <c>Update</c> attach it as Modified with the client's own values as its originals.
    /// </summary>
    private static DataValueBody ValueFor(DataValueEntity value) => new()
    {
        Id = value.Id,
        Value = value.Value,
        InjectId = value.InjectId,
        DataFieldId = value.DataFieldId,
        CellMetadata = value.CellMetadata,
        CreatedBy = value.CreatedBy,
        DateCreated = value.DateCreated
    };

    private Task<HttpResponseMessage> Post(HttpClient client, Guid catalogId, InjectBody body) =>
        client.PostAsJsonAsync(CreateIn(catalogId), body, Ct);

    private Task<HttpResponseMessage> Put(HttpClient client, Guid id, InjectBody body) =>
        client.PutAsJsonAsync(Inject(id), body, Ct);

    private async Task<InjectTypeEntity> SeedInjectType()
    {
        var type = TestData.InjectType();
        await Seed(type);

        return type;
    }

    private async Task<DataFieldEntity> SeedDataField(Guid injectTypeId, int displayOrder = 1)
    {
        var field = TestData.DataField(
            injectTypeId: injectTypeId, displayOrder: displayOrder);
        await Seed(field);

        return field;
    }

    private async Task<CatalogEntity> SeedCatalog(
        Guid injectTypeId, Guid? createdBy = null, bool isPublic = false)
    {
        var catalog = TestData.Catalog(injectTypeId, createdBy, isPublic);
        await Seed(catalog);

        return catalog;
    }

    /// <summary>
    /// An inject, with a <c>CatalogInject</c> row when <paramref name="catalogId"/> is given. An inject with
    /// no catalog at all is a reachable state - the create route is the only thing that writes the join row -
    /// and <see cref="GetByInjectType_IsNotScopedToTheCatalogsTheCallerCanRead"/> turns on it.
    /// </summary>
    private async Task<InjectEntity> SeedInject(Guid injectTypeId, Guid? catalogId = null)
    {
        var inject = TestData.Inject(injectTypeId);
        await Seed(inject);

        if (catalogId is not null)
            await Seed(TestData.CatalogInject(catalogId.Value, inject.Id));

        return inject;
    }

    private async Task<DataValueEntity> SeedDataValue(Guid dataFieldId, Guid injectId, string value)
    {
        var dataValue = TestData.DataValueOnInject(dataFieldId, injectId, value);
        await Seed(dataValue);

        return dataValue;
    }

    private async Task<InjectEntity> Stored(Guid id)
    {
        await using var context = NewContext();

        return await context.Injects.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, Ct);
    }

    private async Task<List<DataValueEntity>> StoredValues(Guid injectId)
    {
        await using var context = NewContext();

        return await context.DataValues.AsNoTracking()
            .Where(x => x.InjectId == injectId).ToListAsync(Ct);
    }

    private async Task<CatalogEntity> StoredCatalog(Guid id)
    {
        await using var context = NewContext();

        return await context.Catalogs.AsNoTracking().SingleAsync(x => x.Id == id, Ct);
    }

    private static StringContent EmptyJson() =>
        new(string.Empty, Encoding.UTF8, "application/json");

    private static void AssertStampedBetween(DateTime? actual, DateTime notBefore, DateTime notAfter)
    {
        Assert.NotNull(actual);
        Assert.InRange(actual.Value, notBefore, notAfter);
    }

    private async Task<string> Title(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ViewModels.ApiError>(JsonOptions, Ct))?.Title;

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }
}
