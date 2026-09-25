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
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>InjectService</c> / <c>InjectController</c> - the six routes behind a catalog's injects. An inject is
/// the reusable half of a scenario event: <c>CreateScenarioEventsFromInjectsAsync</c> copies one into a MSEL's
/// timeline along with its data values, which <c>ScenarioEventFromInjectsTests</c> covers from the timeline
/// side and nothing covered from this one.
/// </summary>
/// <remarks>
/// <para>
/// <strong><c>GET injects/{id}</c> is a 403 for every caller without <c>ViewMsels</c>, unconditionally.</strong>
/// The service's own check reads <c>CatalogUnits.Where(m =&gt; m.Id == userId)</c> - the join row's own primary
/// key compared against a <em>user</em> id, on a table that has no <c>UserId</c> column at all - and then asks
/// whether <em>any</em> catalog reachable that way holds <em>any</em> inject, never mentioning the <c>id</c>
/// being read. So the catalog's creator, a member of a unit the catalog is assigned to, and a stranger are all
/// refused alike (<see cref="Get_ForTheCatalogsCreator_Is403"/>), and the one caller it admits is the one whose
/// user id happens to equal some <c>CatalogUnit</c> row's primary key - who may then read every inject in the
/// installation (<see cref="Get_ForACatalogUnitRowWhosePrimaryKeyEqualsTheCallersId_ReadsAnyInject"/>).
/// <c>CatalogViewRequirement</c>, used correctly by the list route eleven lines above, is the model to copy.
/// </para>
/// <para>
/// <strong><c>UpdateAsync</c>'s data-value loop is dead code whose only reachable effect is a 500.</strong>
/// Lines 160-162 - <c>_mapper.Map</c> onto the <em>unloaded</em> <c>DataValues</c> navigation, then
/// <c>Update</c>, then a save - are what really write the body's data values, so by the time the loop runs
/// every value it looks for is already stored and its <c>else if</c> cannot fire. The <c>if</c> branch is
/// reachable only when the body omits a value, and in that case the value it dereferences is null too. So
/// <strong>adding a data field to an inject type makes every later update of an existing inject a 500</strong>
/// (<see cref="Update_AfterADataFieldIsAddedToTheType_Is500"/>), because the client is echoing back a row that
/// has no value for the new field.
/// </para>
/// <para>
/// <strong>A PUT lets the client write a data value's <c>CreatedBy</c> and <c>DateCreated</c></strong>
/// (<see cref="Update_LetsTheClientWriteADataValuesCreatedByAndDateCreated"/>), which is the documented
/// exception to Phase 1's finding that audit fields are immune to request-body spoofing. The mechanism is
/// worth knowing because it applies to every service that maps a nested collection onto a navigation it never
/// loaded: <c>_context.Injects.Update(injectToUpdate)</c> attaches the data values as <em>Modified</em>, and an
/// entity attached rather than loaded has the client's own values as its <c>OriginalValues</c> - so
/// <c>BlueprintContext.SaveEntries</c>' "restore <c>CreatedBy</c> and <c>DateCreated</c> from
/// <c>OriginalValues</c>" restores what the client sent. The same map is why a PUT may point a value at another
/// inject type's data field and why it may retype the inject itself.
/// </para>
/// <para>
/// <strong>All three writes require only <c>EditMsels</c>, and no write consults the catalog.</strong> Not one
/// of <c>CreateAsync</c>, <c>UpdateAsync</c> or <c>DeleteAsync</c> takes a permission argument or calls
/// <c>CatalogViewRequirement</c>, so an <c>EditMsels</c> holder writes into - and deletes out of - a private
/// catalog they cannot read through the list route. And only <c>CreateAsync</c> calls
/// <c>ServiceUtilities.SetCatalogModifiedAsync</c>: a catalog's <c>DateModified</c> does not move when its
/// injects are edited or removed.
/// </para>
/// <para>
/// Per this branch's rule, every test characterizes rather than fixes, and says what fixing it will do to the
/// test.
/// </para>
/// </remarks>
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
    public async Task GetByCatalog_ForAPrivateCatalog_WithNoPermissionAndNoUnit_Is403()
    {
        var type = await SeedInjectType();
        var catalog = await SeedCatalog(type.Id);
        await SeedInject(type.Id, catalog.Id);
        var actor = await Actor().SeedAsync();

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
        var actor = await Actor().SeedAsync();
        var type = await SeedInjectType();
        var catalog = await SeedCatalog(type.Id);
        var inject = await SeedInject(type.Id, catalog.Id);
        var unit = await Db.AddUnitAsync(actor.Id, Ct);
        await Seed(BlueprintAppFactory.CatalogUnit(unit.Id, catalog.Id));

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

    /// <remarks>
    /// <c>CatalogViewRequirement</c> answers false for a catalog that is not there, so "no such catalog" and
    /// "not yours" are one answer for an ordinary caller. Fixing the route to distinguish them - a 404 for the
    /// first - reddens this test.
    /// </remarks>
    [Fact]
    public async Task GetByCatalog_ForACatalogThatIsNotThere_Is403()
    {
        var actor = await Actor().SeedAsync();

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

    /// <remarks>
    /// <c>GetByInjectTypeAsync</c> is the only read in this service with no check of its own, and the
    /// controller requires <c>ViewMsels</c> outright rather than passing it down - so the route is scoped by
    /// nothing but the type. It lists injects from every catalog in the installation at once, private ones
    /// included, <em>and</em> an inject with no <c>CatalogInject</c> row at all, which no catalog route can
    /// show anybody however they are permissioned. Scoping it the way the catalog route is scoped reddens
    /// both halves.
    /// </remarks>
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
            await Client(actor.Id).GetAsync(InjectsOf(privateCatalog.Id), Ct));
        Assert.Equal(inCatalog.Id, Assert.Single(byCatalog).Id);
    }

    [Fact]
    public async Task GetByInjectType_WithoutViewMsels_Is403()
    {
        var type = await SeedInjectType();
        await SeedInject(type.Id);
        var actor = await Actor().SeedAsync();

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

    /// <remarks>
    /// The creator of the catalog the inject is in - the caller the list route admits without any permission
    /// at all - is refused by the single read, because the service's check reads a table that records no user
    /// and then asks a question that never mentions this inject. Replacing the whole branch with
    /// <c>CatalogViewRequirement.IsMet(userId, catalogId, _context)</c> over the inject's own catalogs makes
    /// this a 200 and reddens the test.
    /// </remarks>
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
            (await Client(actor.Id).GetAsync(InjectsOf(catalog.Id), Ct)).StatusCode);
    }

    /// <remarks>
    /// Nor does the unit path reach it. <c>CatalogUnitEntity</c> has no <c>UserId</c> column, so no
    /// combination of <c>UnitUser</c> and <c>CatalogUnit</c> rows can satisfy
    /// <c>CatalogUnits.Where(m =&gt; m.Id == userId)</c> - only a coincidence of primary keys can, which is
    /// what <see cref="Get_ForACatalogUnitRowWhosePrimaryKeyEqualsTheCallersId_ReadsAnyInject"/> arranges.
    /// </remarks>
    [Fact]
    public async Task Get_ForAUnitMemberOfTheCatalog_Is403()
    {
        var actor = await Actor().SeedAsync();
        var type = await SeedInjectType();
        var catalog = await SeedCatalog(type.Id);
        var inject = await SeedInject(type.Id, catalog.Id);
        var unit = await Db.AddUnitAsync(actor.Id, Ct);
        await Seed(BlueprintAppFactory.CatalogUnit(unit.Id, catalog.Id));

        var response = await Client(actor).GetAsync(Inject(inject.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// Even a public catalog does not help: the single read never looks at <c>IsPublic</c>, so the inject
    /// inside a catalog every signed-in caller may list is unreadable by its own id.
    /// </remarks>
    [Fact]
    public async Task Get_ForAnInjectInAPublicCatalog_Is403()
    {
        var type = await SeedInjectType();
        var catalog = await SeedCatalog(type.Id, isPublic: true);
        var inject = await SeedInject(type.Id, catalog.Id);
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync(Inject(inject.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// <para>
    /// The one ordinary caller the route admits, and the shape of the defect in one test. Seed a
    /// <c>CatalogUnit</c> row whose own primary key equals the caller's user id and the first query matches
    /// it, so the caller is treated as a member of whatever unit that row names - a unit they are not in and
    /// which belongs to somebody else. The check then asks whether <em>any</em> inject sits in <em>any</em>
    /// catalog reachable that way and never mentions the inject being read, so both injects answer 200,
    /// including the one in no catalog at all.
    /// </para>
    /// <para>
    /// <c>BlueprintAppFactory.CatalogUnit</c> takes an explicit <c>id</c> for this test alone. Fixing either
    /// half - reading <c>UnitUsers</c> for the unit list, or filtering the last query on this inject - reddens
    /// it.
    /// </para>
    /// </remarks>
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
        await Seed(BlueprintAppFactory.CatalogUnit(unit.Id, catalog.Id, id: actor.Id));

        Assert.Equal(
            HttpStatusCode.OK, (await Client(actor).GetAsync(Inject(inCatalog.Id), Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK, (await Client(actor.Id).GetAsync(Inject(orphan.Id), Ct)).StatusCode);
    }

    /// <remarks>
    /// <c>SingleAsync</c> throws before the null check below it can run, so an unknown id is a 500 rather
    /// than the 404 that check promises - and unlike the copies of this line in <c>OrganizationService</c>,
    /// <c>MoveService</c> and <c>CardService</c>, this one at least names the right entity. Changing the call
    /// to <c>SingleOrDefaultAsync</c> makes the dead check live and this a 404, which reddens the test.
    /// </remarks>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is500RatherThanThe404TheDeadCheckPromises()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync(Inject(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("Sequence contains no elements.", await Title(response));
    }

    /// <remarks>
    /// And the row is loaded before any permission decision is taken, so a caller who would have been refused
    /// gets the same 500 - the read happens either way.
    /// </remarks>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_IsThatSame500WithoutAnyPermission()
    {
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync(Inject(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("Sequence contains no elements.", await Title(response));
    }

    // ---------------------------------------------------------------------------------------------
    // POST catalog/{catalogId}/injects
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// The route is spelled <c>catalog/{catalogId}/injects</c> - singular - where the list route it pairs with
    /// is <c>catalogs/{catalogId}/injects</c>. Renaming either to match the other reddens this test and the
    /// anonymous sweep's entry for it.
    /// </remarks>
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

    /// <remarks>
    /// <c>InjectController.Create</c> declares 201 and returns <c>Ok</c>, so there is no <c>Location</c>
    /// header - and the route that would have been in it, <c>injects/{id}</c>, is the one route a caller
    /// without <c>ViewMsels</c> cannot use. Answering <c>CreatedAtAction</c> reddens this test.
    /// </remarks>
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

    /// <remarks>
    /// <c>CreateAsync</c> builds its answer from the inject type's field list and assigns the result over
    /// <c>inject.DataValues</c>, so a value naming a field the type does not have is dropped without a word -
    /// no 400, no message, and the caller's own answer does not contain it either. Rejecting the unknown field
    /// reddens this test.
    /// </remarks>
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

    /// <remarks>
    /// The body's <c>Id</c> is kept when it is not all-zeros, so a client chooses the primary key of an inject
    /// it creates - and a second create naming the same id is a 500 from the database rather than a 409.
    /// <c>ScenarioEventService</c> and <c>DataFieldService</c> both do the same; ignoring the body's id
    /// reddens this test.
    /// </remarks>
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

    /// <remarks>
    /// <c>dataFieldList</c> comes from <c>SingleOrDefaultAsync</c>, so an inject type that is not there gives
    /// null and the unguarded <c>foreach</c> over it is a 500 - where a 400 or a 404 naming the type is what a
    /// caller can act on. Guarding the loop reddens this test.
    /// </remarks>
    [Fact]
    public async Task Create_ForAnInjectTypeThatIsNotThere_Is500()
    {
        var catalog = await SeedCatalog((await SeedInjectType()).Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(Client(actor), catalog.Id, Body(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("Object reference not set to an instance of an object.", await Title(response));
    }

    /// <remarks>
    /// Nothing checks the catalog exists either, so the inject is inserted and the <c>CatalogInject</c> row's
    /// foreign key is what refuses the request - a 500 from the database, with the inject rolled back by the
    /// transaction. Checking the catalog first reddens this test.
    /// </remarks>
    [Fact]
    public async Task Create_ForACatalogThatIsNotThere_Is500AndStoresNothing()
    {
        var type = await SeedInjectType();
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var chosen = Guid.NewGuid();

        var response = await Post(
            Client(actor), Guid.NewGuid(), Body(type.Id) with { Id = chosen });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Null(await Stored(chosen));
    }

    /// <remarks>
    /// <c>CreateAsync</c> takes no permission argument and never asks <c>CatalogViewRequirement</c>, so
    /// <c>EditMsels</c> alone writes into a private catalog created by somebody else - one the same caller
    /// cannot list. Passing the catalog through a view or an edit requirement reddens this test.
    /// </remarks>
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
            (await Client(actor.Id).GetAsync(InjectsOf(catalog.Id), Ct)).StatusCode);
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

    /// <remarks>
    /// <c>InjectHandler.GetGroups</c> returns the admin data group and nothing else, so a catalog's editors
    /// are told about a new inject only if they are administrators watching that group - and the data value
    /// created alongside it is broadcast to a group named by the all-zeros guid, because
    /// <c>DataValueHandler.GetGroups</c> looks up the MSEL of a scenario event this value does not have.
    /// Giving either handler a catalog-shaped group reddens this test.
    /// </remarks>
    [Fact]
    public async Task Create_BroadcastsToTheAdminDataGroupAndTheAllZerosGroup()
    {
        var type = await SeedInjectType();
        await SeedDataField(type.Id);
        var catalog = await SeedCatalog(type.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        Hub.Clear();

        var created = await Read<ViewModels.Injectm>(
            await Post(Client(actor), catalog.Id, Body(type.Id)));

        Assert.Equal([MainHub.ADMIN_DATA_GROUP], Hub.Recipients(MainHubMethods.InjectCreated));
        Assert.Equal(created.Id, Assert.IsType<ViewModels.Injectm>(
            Hub.Of(MainHubMethods.InjectCreated)[0].Payload).Id);
        Assert.Equal(
            [Guid.Empty.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.DataValueCreated));
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

    /// <remarks>
    /// <para>
    /// The exception to Phase 1's finding that audit fields are immune to request-body spoofing, and the
    /// reason is mechanical rather than particular to injects. The inject's own row was loaded, so its
    /// <c>OriginalValues</c> come from the database and the test above passes; the data values in the body were
    /// not, and <c>_context.Injects.Update(injectToUpdate)</c> attaches them as <em>Modified</em> with the
    /// client's own values as their originals - so <c>SaveEntries</c>' restore-from-originals writes the
    /// client's <c>CreatedBy</c> and <c>DateCreated</c> back over its own stamp. <c>ModifiedBy</c> is left
    /// alone by that branch and the body sent none, so the row records a modification by nobody.
    /// </para>
    /// <para>
    /// Loading the data values with the inject - an <c>Include</c> on line 151 - reddens this test.
    /// </para>
    /// </remarks>
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

    /// <remarks>
    /// The same map writes each value's <c>DataFieldId</c>, and nothing checks that the field belongs to the
    /// inject's type - so a PUT stores a cell against another inject type's column, where it is listed by
    /// neither type's routes and read by no consumer. It is only reachable alongside a well-formed value for
    /// every field the type does have, because that is what keeps the loop below from dereferencing null; the
    /// extra value is invisible to the loop entirely. Validating each field against the type reddens this
    /// test.
    /// </remarks>
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

    /// <remarks>
    /// Repointing the <em>only</em> value instead is a 500, because the loop then finds nothing for the
    /// type's own field in either the database or the body and dereferences the null - and the whole request
    /// is rolled back with it. The throw is inside the loop, so the <c>CommitTransactionAsync</c> below it is
    /// never reached, and <c>UpdateAsync</c> has no <c>catch</c> and never calls
    /// <c>RollbackTransactionAsync</c> - what undoes the write is the implicit rollback EF performs when the
    /// uncommitted transaction is disposed with the request-scoped context. That is what makes the PUT atomic
    /// and every 500 in this section a no-op rather than a half-write, and it is luck rather than design.
    /// Committing before the loop - or guarding the loop - reddens this test.
    /// </remarks>
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

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(inject.Name, (await Stored(inject.Id)).Name);
        Assert.Equal(mine.Id, Assert.Single(await StoredValues(inject.Id)).DataFieldId);
    }

    /// <remarks>
    /// <c>InjectProfile</c> maps <c>InjectTypeId</c>, so a PUT retypes the inject - and its stored data values
    /// keep pointing at the old type's fields, which the new type does not have. The inject is then listed
    /// under a type whose columns do not describe it, and <c>CreateScenarioEventsFromInjectsAsync</c> will
    /// reconcile those orphaned values into whatever MSEL it is copied into. Ignoring the body's
    /// <c>InjectTypeId</c> reddens this test.
    /// </remarks>
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

    /// <remarks>
    /// <para>
    /// The headline consequence of the dead loop. An inject type gains a data field - the ordinary way a
    /// catalog's schema grows - and every existing inject of that type now has no value for it. A client GETs
    /// the inject and PUTs it back, so the body has no value for the new field either; the loop's
    /// <c>dataValueToUpdate</c> is null, the <c>dataValue</c> it was going to copy from is null too, and line
    /// 177 dereferences it. Every edit of every existing inject is a 500 until somebody supplies a value for
    /// the new field by hand.
    /// </para>
    /// <para>
    /// The transaction's implicit rollback at least makes it a clean failure - the rename the same request
    /// carried is discarded too, as <see cref="Update_ThatRepointsTheOnlyDataValue_Is500AndStoresNothing"/>
    /// spells out. Guarding the branch - or deleting the whole loop, which writes nothing reachable -
    /// reddens this test.
    /// </para>
    /// </remarks>
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
        Assert.Equal("Object reference not set to an instance of an object.", await Title(response));
        Assert.Equal(inject.Name, (await Stored(inject.Id)).Name);
    }

    /// <remarks>
    /// The same 500 reached the plain way: a body that simply does not mention a field the type has. A client
    /// sending only the fields it means to change gets it on the first request.
    /// </remarks>
    [Fact]
    public async Task Update_WithABodyThatOmitsAValueForAFieldTheTypeHas_Is500()
    {
        var type = await SeedInjectType();
        await SeedDataField(type.Id);
        var inject = await SeedInject(type.Id, (await SeedCatalog(type.Id)).Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Put(Client(actor), inject.Id, BodyFor(inject));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("Object reference not set to an instance of an object.", await Title(response));
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

    /// <remarks>
    /// <c>InjectProfile</c> maps <c>Id</c>, so the mapper writes the body's id onto the tracked row and EF
    /// refuses to modify a key - a 500 where a 400 comparing the two ids is what the rest of the estate does
    /// not do either. Ignoring the body's id on update reddens this test.
    /// </remarks>
    [Fact]
    public async Task Update_WhoseBodyIdIsNotTheRoutes_Is500()
    {
        var type = await SeedInjectType();
        var inject = await SeedInject(type.Id, (await SeedCatalog(type.Id)).Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Put(
            Client(actor), inject.Id, BodyFor(inject) with { Id = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
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

    /// <remarks>
    /// Nothing about the catalog is consulted, so an <c>EditMsels</c> holder edits an inject in a private
    /// catalog they cannot list. Same shape as <see cref="Create_InAPrivateCatalogTheCallerCannotRead_Is200"/>
    /// and <see cref="Delete_FromAPrivateCatalogTheCallerCannotRead_Is200"/>; all three redden together when
    /// the catalog is checked.
    /// </remarks>
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
            (await Client(actor.Id).GetAsync(InjectsOf(catalog.Id), Ct)).StatusCode);
    }

    /// <remarks>
    /// <c>CreateAsync</c> calls <c>ServiceUtilities.SetCatalogModifiedAsync</c> and neither <c>UpdateAsync</c>
    /// nor <c>DeleteAsync</c> does, so a catalog's <c>DateModified</c> records when an inject was added to it
    /// and never that one was changed or removed. Adding the call reddens this test.
    /// </remarks>
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
        Hub.Clear();

        await Put(Client(actor), inject.Id, BodyFor(inject) with { Name = "renamed" });

        Assert.Equal([MainHub.ADMIN_DATA_GROUP], Hub.Recipients(MainHubMethods.InjectUpdated));
        Assert.Equal("renamed", Assert.IsType<ViewModels.Injectm>(
            Hub.Of(MainHubMethods.InjectUpdated)[0].Payload).Name);
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

    /// <remarks>
    /// The action declares 204 and returns <c>Ok(returnVal)</c>, so the answer is 200 with the JSON literal
    /// <c>true</c> - the same pair as <c>deleteScenarioEvent</c> and <c>deleteDataField</c>. Returning
    /// <c>NoContent</c> reddens this test.
    /// </remarks>
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

    /// <remarks>
    /// <c>InjectEntity.RequiresInjectId</c> is nullable, so the convention gives it no cascade and the
    /// database refuses the delete - a 500 where a 409 naming the dependent inject is what a caller can act
    /// on, and there is no route that tells them which inject requires this one. Checking for dependents
    /// first, or clearing them, reddens this test.
    /// </remarks>
    [Fact]
    public async Task Delete_ForAnInjectThatAnotherInjectRequires_Is500()
    {
        var type = await SeedInjectType();
        var catalog = await SeedCatalog(type.Id);
        var required = await SeedInject(type.Id, catalog.Id);
        var dependent = BlueprintAppFactory.Inject(type.Id);
        dependent.RequiresInjectId = required.Id;
        await Seed(dependent);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync(Inject(required.Id), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
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
            (await Client(actor.Id).GetAsync(InjectsOf(catalog.Id), Ct)).StatusCode);
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
        Hub.Clear();

        await Client(actor).DeleteAsync(Inject(inject.Id), Ct);

        Assert.Equal([MainHub.ADMIN_DATA_GROUP], Hub.Recipients(MainHubMethods.InjectDeleted));
        Assert.Equal(inject.Id, Assert.IsType<Guid>(Hub.Of(MainHubMethods.InjectDeleted)[0].Payload));
    }

    // ---------------------------------------------------------------------------------------------
    // Authentication
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// The POST route is spelled <c>catalog</c> where the matching GET is <c>catalogs</c>; both spellings are
    /// in the sweep so a rename of either shows up here as a 404 rather than a 401, which is the one way an
    /// anonymous-sweep case is killable.
    /// </remarks>
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

        var response = await AnonymousClient.SendAsync(request, Ct);

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
        var type = BlueprintAppFactory.InjectType();
        await Seed(type);

        return type;
    }

    private async Task<DataFieldEntity> SeedDataField(Guid injectTypeId, int displayOrder = 1)
    {
        var field = BlueprintAppFactory.DataField(
            injectTypeId: injectTypeId, displayOrder: displayOrder);
        await Seed(field);

        return field;
    }

    private async Task<CatalogEntity> SeedCatalog(
        Guid injectTypeId, Guid? createdBy = null, bool isPublic = false)
    {
        var catalog = BlueprintAppFactory.Catalog(injectTypeId, createdBy, isPublic);
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
        var inject = BlueprintAppFactory.Inject(injectTypeId);
        await Seed(inject);

        if (catalogId is not null)
            await Seed(BlueprintAppFactory.CatalogInject(catalogId.Value, inject.Id));

        return inject;
    }

    private async Task<DataValueEntity> SeedDataValue(Guid dataFieldId, Guid injectId, string value)
    {
        var dataValue = BlueprintAppFactory.DataValueOnInject(dataFieldId, injectId, value);
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
