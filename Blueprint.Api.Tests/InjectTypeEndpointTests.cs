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
/// <c>InjectTypeService</c> / <c>InjectTypeController</c> - the seven routes behind an inject type, which is
/// the column layout every catalog and every inject of that catalog is built on. The companion file is
/// <see cref="InjectEndpointTests"/>; this one covers the schema and that one covers the rows.
/// </summary>
/// <remarks>
/// <para>
/// <strong><c>DELETE injectTypes/{id}</c> silently destroys every catalog built on the type, every inject of
/// it, every data value on those injects and every catalog-inject join row</strong>
/// (<see cref="Delete_SilentlyDestroysEveryCatalogAndInjectBuiltOnTheType"/>). It answers 204, broadcasts one
/// <c>InjectTypeDeleted</c> to the admin data group, and nothing anywhere counts dependents or offers a 409.
/// The cascade is the database's, so the service has no opportunity to report what it took - which is the
/// argument for checking before deleting rather than for changing the cascade.
/// </para>
/// <para>
/// <strong>Nothing in this service checks a permission, and nothing about it is scoped.</strong> Every
/// decision is the controller's, and its two reads ask for <c>ViewInjectTypes</c> while its five other routes
/// ask for <c>ManageInjectTypes</c> - including <c>POST injectTypes/json/download</c>, which writes no data
/// (<see cref="Download_IsAReadBehindAManagePermission"/>). An inject type is installation-wide reference
/// data, so there is no MSEL and no catalog to scope it by; that is the design, and it is why the two
/// requirement helpers this branch has found misused elsewhere do not appear here at all.
/// </para>
/// <para>
/// <strong>Both read routes answer an always-empty <c>dataFields</c></strong> - neither
/// <c>GetAsync</c> overload has an <c>Include</c> - while <c>POST injectTypes</c> and
/// <c>POST injectTypes/json</c> both answer the full graph, because the entities they just added are tracked
/// by the same context and EF's change-tracker fix-up populates the navigation the query never asked for.
/// So the same view model crosses the same API with and without its children depending on which verb produced
/// it, and a client reading a type back has to call <c>GET injectTypes/{injectTypeId}/dataFields</c> to learn
/// what is in it (<see cref="Get_AnswersAnEmptyDataFieldsCollection"/>). This is the fourth appearance of the
/// fix-up mechanism on this branch, after <c>PlayerService</c> (<c>ff5bdad</c>) and both halves of this
/// service.
/// </para>
/// <para>
/// <strong>The upload cannot read the API's own dialect.</strong> Both file routes build their own
/// <c>JsonSerializerOptions</c> rather than using the MVC ones - the same shape as the card templates in
/// <c>b5d2d86</c> - but here the pair is not even self-consistent with the wire format the rest of the
/// service speaks: with no <c>JsonStringEnumConverter</c> a <c>"dataType": "String"</c> is a 500
/// (<see cref="UploadJson_CannotReadTheEnumNamesTheApiItselfWrites"/>) where every response writes that field
/// as a name. The download and the upload do agree with each other, so a file round-trips - once it is
/// renamed past the unique index (<see cref="UploadJson_OfAFileFromTheSameInstallation_Is500"/>), and at the
/// cost of every option's description (<see cref="UploadJson_OfARenamedDownload_LosesEveryOptionDescription"/>).
/// </para>
/// <para>
/// <strong><c>IInjectTypeService</c> is registered twice</strong> - <c>Startup.cs:228</c> and
/// <c>Startup.cs:231</c>, the same line written out twice with two unrelated registrations between them. It
/// is harmless, the last registration winning, and it is what the composition test planned for Phase 3 item 9
/// exists to surface. Recorded here because this is the file a reader looking for it will open.
/// </para>
/// <para>
/// Per this branch's rule, every test characterizes rather than fixes, and says what fixing it does to the
/// test.
/// </para>
/// </remarks>
public class InjectTypeEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET injectTypes
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// There is no <c>OrderBy</c> on the list, so the order is whatever the database returns and the
    /// assertion is a set. Same finding as the move list (<c>997a9c4</c>) and the card lists
    /// (<c>b5d2d86</c>); this list is the one the inject editor's type picker is built from.
    /// </remarks>
    [Fact]
    public async Task List_WithViewInjectTypes_ReturnsEveryInjectTypeInNoParticularOrder()
    {
        var first = await SeedInjectType();
        var second = await SeedInjectType();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewInjectTypes).SeedAsync();

        var types = await Read<List<ViewModels.InjectType>>(
            await Client(actor).GetAsync(InjectTypes, Ct));

        Assert.Equal(
            new HashSet<Guid> { first.Id, second.Id }, types.Select(x => x.Id).ToHashSet());
    }

    /// <remarks>
    /// Neither <c>GetAsync</c> overload includes the data fields, so the list answers an empty collection for
    /// a type that has two of them - and the create route answers the same type's fields in full. Adding the
    /// <c>Include</c> reddens this test and <see cref="Get_AnswersAnEmptyDataFieldsCollection"/>.
    /// </remarks>
    [Fact]
    public async Task List_AnswersAnEmptyDataFieldsCollection()
    {
        var type = await SeedInjectType();
        await SeedDataField(type.Id);
        await SeedDataField(type.Id, displayOrder: 2);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewInjectTypes).SeedAsync();

        var types = await Read<List<ViewModels.InjectType>>(
            await Client(actor).GetAsync(InjectTypes, Ct));

        Assert.Empty(Assert.Single(types).DataFields);
    }

    [Fact]
    public async Task List_WithoutViewInjectTypes_Is403()
    {
        await SeedInjectType();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        var response = await Client(actor).GetAsync(InjectTypes, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // GET injectTypes/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_WithViewInjectTypes_ReturnsTheInjectType()
    {
        var type = await SeedInjectType();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewInjectTypes).SeedAsync();

        var answer = await Read<ViewModels.InjectType>(
            await Client(actor).GetAsync(InjectType(type.Id), Ct));

        Assert.Equal(type.Id, answer.Id);
        Assert.Equal(type.Name, answer.Name);
        Assert.Equal(type.Description, answer.Description);
    }

    /// <remarks>
    /// The route a client has to call instead is <c>GET injectTypes/{injectTypeId}/dataFields</c> on
    /// <c>DataFieldController</c>, which is asserted here beside the empty answer so the pair is on one
    /// screen. Adding the <c>Include</c> reddens the first assertion only.
    /// </remarks>
    [Fact]
    public async Task Get_AnswersAnEmptyDataFieldsCollection()
    {
        var type = await SeedInjectType();
        var field = await SeedDataField(type.Id);
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ViewInjectTypes, SystemPermission.ViewMsels)
            .SeedAsync();

        var answer = await Read<ViewModels.InjectType>(
            await Client(actor).GetAsync(InjectType(type.Id), Ct));

        Assert.Empty(answer.DataFields);

        var fields = await Read<List<ViewModels.DataField>>(
            await Client(actor.Id).GetAsync($"/api/injecttypes/{type.Id}/dataFields", Ct));
        Assert.Equal(field.Id, Assert.Single(fields).Id);
    }

    /// <remarks>
    /// <c>GetAsync</c> maps a null entity to a null view model and the controller's own null check turns it
    /// into a 404 - so this is the one read in the service pair that answers correctly, unlike
    /// <c>InjectService.GetAsync</c>'s <c>SingleAsync</c> and the three other copies of that line this branch
    /// has found.
    /// </remarks>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewInjectTypes).SeedAsync();

        var response = await Client(actor).GetAsync(InjectType(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_WithoutViewInjectTypes_Is403()
    {
        var type = await SeedInjectType();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        var response = await Client(actor).GetAsync(InjectType(type.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // POST injectTypes
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_WithManageInjectTypes_Is201WithALocationHeader()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        var response = await Post(Client(actor), Body("a type posted by the test"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await Read<ViewModels.InjectType>(response);
        Assert.EndsWith($"/api/injecttypes/{created.Id}", response.Headers.Location?.ToString());
        Assert.Equal("a type posted by the test", (await Stored(created.Id)).Name);
    }

    /// <remarks>
    /// <c>InjectTypeProfile</c> maps <c>DataFields</c> in both directions with nothing ignored, so a nested
    /// collection in the body is written through by <c>_context.InjectTypes.Add</c>'s graph traversal - the
    /// route creates a whole column layout in one request, although nothing in the surface says so. The
    /// fields' <c>InjectTypeId</c> is filled by EF from the navigation, so the body need not carry it.
    /// </remarks>
    [Fact]
    public async Task Create_AlsoCreatesTheDataFieldsTheBodyCarries()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();
        var body = Body("a type with fields") with
        {
            DataFields = [Field("headline", DataFieldType.Html, 1), Field("count", DataFieldType.Integer, 2)]
        };

        var created = await Read<ViewModels.InjectType>(await Post(Client(actor), body));

        var stored = await StoredFields(created.Id);
        Assert.Equal(
            new HashSet<string> { "headline", "count" }, stored.Select(x => x.Name).ToHashSet());
        Assert.Equal(DataFieldType.Html, Assert.Single(stored, x => x.Name == "headline").DataType);
        Assert.All(stored, x => Assert.Equal(created.Id, x.InjectTypeId));
    }

    /// <remarks>
    /// <c>CreateAsync</c> stamps <c>CreatedBy</c> on the inject type and on nothing else, so a field created
    /// this way records no creator - <c>SaveEntries</c> stamps the dates for every added entity but never
    /// touches <c>CreatedBy</c>, which is the asymmetry Phase 1 recorded. Stamping the children reddens this
    /// test.
    /// </remarks>
    [Fact]
    public async Task Create_LeavesTheNestedDataFieldsCreatedByUnstamped()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();
        var body = Body("a type with an unattributed field") with
        {
            DataFields = [Field("headline", DataFieldType.Html, 1)]
        };
        var before = DateTime.UtcNow;

        var created = await Read<ViewModels.InjectType>(await Post(Client(actor), body));

        Assert.Equal(actor.Id, (await Stored(created.Id)).CreatedBy);
        var field = Assert.Single(await StoredFields(created.Id));
        Assert.Equal(Guid.Empty, field.CreatedBy);
        AssertStampedBetween(field.DateCreated, before, DateTime.UtcNow);
    }

    /// <remarks>
    /// <c>CreateAsync</c> returns <c>await GetAsync(entity.Id, ct)</c> - the same query
    /// <see cref="Get_AnswersAnEmptyDataFieldsCollection"/> shows answering nothing - and yet the fields are
    /// there, because the request-scoped context is still tracking the ones it just added and EF fixes them
    /// into the navigation. So the create's answer and an immediate re-read of the same row disagree, in the
    /// same request pair and over the same code. Adding the <c>Include</c> makes them agree and reddens the
    /// other test rather than this one.
    /// </remarks>
    [Fact]
    public async Task Create_AnswersTheDataFieldsThatAReadOfTheSameRowWillNot()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageInjectTypes, SystemPermission.ViewInjectTypes)
            .SeedAsync();
        var body = Body("a type answered twice") with
        {
            DataFields = [Field("headline", DataFieldType.Html, 1)]
        };

        var created = await Read<ViewModels.InjectType>(await Post(Client(actor), body));

        Assert.Equal("headline", Assert.Single(created.DataFields).Name);

        var reread = await Read<ViewModels.InjectType>(
            await Client(actor.Id).GetAsync(InjectType(created.Id), Ct));
        Assert.Empty(reread.DataFields);
    }

    /// <remarks>
    /// The body's <c>Id</c> is written through untouched - <c>CreateAsync</c> has no
    /// <c>Id != Guid.Empty ? … : Guid.NewGuid()</c> line at all, unlike <c>InjectService.CreateAsync</c> -
    /// so a client chooses the primary key, and a second create naming the same id is a 500 from the
    /// database rather than a 409. Ignoring the body's id reddens this test.
    /// </remarks>
    [Fact]
    public async Task Create_KeepsTheClientsId()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();
        var chosen = Guid.NewGuid();

        var created = await Read<ViewModels.InjectType>(
            await Post(Client(actor), Body("a type with a chosen id") with { Id = chosen }));

        Assert.Equal(chosen, created.Id);
        Assert.NotNull(await Stored(chosen));
    }

    [Fact]
    public async Task Create_StampsTheAuditFieldsOnTheServer()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();
        var before = DateTime.UtcNow;
        var body = Body("a type with hostile audit fields") with
        {
            CreatedBy = Guid.NewGuid(),
            DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ModifiedBy = Guid.NewGuid(),
            DateModified = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        var created = await Read<ViewModels.InjectType>(await Post(Client(actor), body));

        var stored = await Stored(created.Id);
        Assert.Equal(actor.Id, stored.CreatedBy);
        AssertStampedBetween(stored.DateCreated, before, DateTime.UtcNow);
        Assert.Null(stored.ModifiedBy);
        Assert.Null(stored.DateModified);
    }

    /// <remarks>
    /// <c>InjectTypeEntityConfiguration</c> declares <c>Name</c> unique, and nothing checks it first, so a
    /// name a caller cannot see - the list route needs a different permission from the create route - is a
    /// 500 rather than the 409 <c>CompetencyFrameworkService</c> answers for its own duplicate-id-number
    /// case. This index is also what makes <see cref="UploadJson_OfAFileFromTheSameInstallation_Is500"/> a
    /// 500. Answering 409 reddens this test.
    /// </remarks>
    [Fact]
    public async Task Create_WithADuplicateName_Is500()
    {
        var existing = await SeedInjectType();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        var response = await Post(Client(actor), Body(existing.Name));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithoutManageInjectTypes_Is403()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewInjectTypes).SeedAsync();

        var response = await Post(Client(actor), Body("a type nobody may create"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithNoBody_Is400()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        var response = await Client(actor).PostAsync(InjectTypes, EmptyJson(), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <remarks>
    /// <c>InjectTypeHandler.GetGroups</c> returns the admin data group and nothing else, which for
    /// installation-wide reference data is defensible - but it means a content developer holding
    /// <c>ManageInjectTypes</c> and not watching that group sees no new type until they reload. The nested
    /// data fields are broadcast by <c>DataFieldHandler</c>, whose group for a field with no MSEL is named by
    /// the empty string - the defect recorded for <c>OrganizationHandler</c> in Phase 1 and for
    /// <c>DataFieldHandler</c> in <c>081e05b</c>, reached here through a route that always produces it.
    /// </remarks>
    [Fact]
    public async Task Create_BroadcastsToTheAdminDataGroupAndTheEmptyStringGroup()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();
        var body = Body("a broadcast type") with
        {
            DataFields = [Field("headline", DataFieldType.Html, 1)]
        };
        Hub.Clear();

        var created = await Read<ViewModels.InjectType>(await Post(Client(actor), body));

        Assert.Equal([MainHub.ADMIN_DATA_GROUP], Hub.Recipients(MainHubMethods.InjectTypeCreated));
        Assert.Equal(created.Id, Assert.IsType<ViewModels.InjectType>(
            Hub.Of(MainHubMethods.InjectTypeCreated)[0].Payload).Id);
        Assert.Equal(
            [string.Empty, MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.DataFieldCreated));
    }

    // ---------------------------------------------------------------------------------------------
    // PUT injectTypes/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_WithManageInjectTypes_Is200AndStoresTheChange()
    {
        var type = await SeedInjectType();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        var response = await Put(Client(actor), type.Id, BodyFor(type) with { Name = "renamed" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("renamed", (await Stored(type.Id)).Name);
        Assert.Equal("renamed", (await Read<ViewModels.InjectType>(response)).Name);
    }

    [Fact]
    public async Task Update_StampsTheAuditFieldsAndPreservesCreation()
    {
        var type = await SeedInjectType();
        var created = (await Stored(type.Id)).DateCreated;
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();
        var before = DateTime.UtcNow;
        var body = BodyFor(type) with
        {
            Name = "renamed",
            CreatedBy = Guid.NewGuid(),
            DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ModifiedBy = Guid.NewGuid()
        };

        await Put(Client(actor), type.Id, body);

        var stored = await Stored(type.Id);
        Assert.Equal(type.CreatedBy, stored.CreatedBy);
        Assert.Equal(created, stored.DateCreated);
        Assert.Equal(actor.Id, stored.ModifiedBy);
        AssertStampedBetween(stored.DateModified, before, DateTime.UtcNow);
    }

    /// <remarks>
    /// <para>
    /// <c>UpdateAsync</c> ends <c>return _mapper.Map(injectTypeToUpdate, injectType)</c> - it maps the stored
    /// entity back over the <em>request</em> object rather than answering a fresh read the way
    /// <c>CreateAsync</c> does. The entity's own <c>DataFields</c> was never loaded and its collection holds
    /// exactly what the mapper put there on the way in, so the answer's <c>dataFields</c> is the request's
    /// <c>dataFields</c> echoed back: a PUT that adds one column to a type that has two answers as though the
    /// type has one.
    /// </para>
    /// <para>
    /// So the collection means something different on each of the three verbs - the real graph on a create,
    /// the request's own graph on an update, and always empty on a read. Returning
    /// <c>await GetAsync(id, ct)</c> reddens this test and makes the update agree with the read.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Update_AnswersOnlyTheDataFieldsTheBodyCarried()
    {
        var type = await SeedInjectType();
        var existing = await SeedDataField(type.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();
        var body = BodyFor(type) with { DataFields = [Field("added", DataFieldType.Html, 2)] };

        var answer = await Read<ViewModels.InjectType>(await Put(Client(actor), type.Id, body));

        Assert.Equal("added", Assert.Single(answer.DataFields).Name);
        Assert.DoesNotContain(existing.Name, answer.DataFields.Select(x => x.Name));
    }

    /// <remarks>
    /// And a body that mentions no field at all answers none and deletes none - the stored field is neither
    /// loaded nor tracked, so there is no orphan for EF to remove. That is the shape every request from
    /// blueprint.ui takes, because the read route it filled its form from answers an empty collection: the
    /// column layout is safe from a round-trip only because two absences happen to cancel out.
    /// </remarks>
    [Fact]
    public async Task Update_WithNoDataFieldsInTheBody_AnswersNoneAndKeepsTheStoredOnes()
    {
        var type = await SeedInjectType();
        var existing = await SeedDataField(type.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        var answer = await Read<ViewModels.InjectType>(
            await Put(Client(actor), type.Id, BodyFor(type) with { Name = "renamed" }));

        Assert.Empty(answer.DataFields);
        Assert.Equal(existing.Id, Assert.Single(await StoredFields(type.Id)).Id);
    }

    /// <remarks>
    /// The field the body carries is stored, because the profile maps <c>DataFields</c> and
    /// <c>_context.InjectTypes.Update</c> traverses the graph - so a PUT is a second way to add a column to a
    /// type, and nothing in the route's name or its surface says so. The field it adds has no value on any
    /// existing inject of the type, which is the state
    /// <c>InjectEndpointTests.Update_AfterADataFieldIsAddedToTheType_Is500</c> shows making every later edit
    /// of those injects a 500. Ignoring the body's <c>DataFields</c> reddens this test.
    /// </remarks>
    [Fact]
    public async Task Update_StoresADataFieldTheBodyCarries()
    {
        var type = await SeedInjectType();
        var existing = await SeedDataField(type.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();
        var body = BodyFor(type) with { DataFields = [Field("added", DataFieldType.Html, 2)] };

        Assert.Equal(HttpStatusCode.OK, (await Put(Client(actor), type.Id, body)).StatusCode);

        var stored = await StoredFields(type.Id);
        Assert.Equal(
            new HashSet<string> { existing.Name, "added" }, stored.Select(x => x.Name).ToHashSet());
    }

    /// <remarks>
    /// The same map writes an existing field's <c>InjectTypeId</c>, so a PUT naming another type's field by
    /// id moves the column out of that type and into this one - the type it left keeps whatever injects and
    /// values referred to it, with no column to describe them. Nothing checks that a field in the body
    /// belongs to the type being updated. Validating the collection against the route's id reddens this
    /// test.
    /// </remarks>
    [Fact]
    public async Task Update_MayStealAnotherInjectTypesDataField()
    {
        var type = await SeedInjectType();
        var other = await SeedInjectType();
        var theirs = await SeedDataField(other.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();
        var body = BodyFor(type) with
        {
            DataFields = [Field(theirs.Name, theirs.DataType, theirs.DisplayOrder) with { Id = theirs.Id }]
        };

        Assert.Equal(HttpStatusCode.OK, (await Put(Client(actor), type.Id, body)).StatusCode);

        Assert.Equal(theirs.Id, Assert.Single(await StoredFields(type.Id)).Id);
        Assert.Empty(await StoredFields(other.Id));
    }

    /// <remarks>
    /// <c>InjectTypeProfile</c> maps <c>Id</c>, so the mapper writes the body's id onto the tracked row and
    /// EF refuses to modify a key - a 500 where comparing the two ids would be a 400. Same shape as every
    /// other update in the estate; ignoring the body's id reddens this test.
    /// </remarks>
    [Fact]
    public async Task Update_WhoseBodyIdIsNotTheRoutes_Is500()
    {
        var type = await SeedInjectType();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        var response = await Put(
            Client(actor), type.Id, BodyFor(type) with { Id = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <remarks>
    /// The 404 is right, and the exception behind it names <c>InjectType</c> - the <em>view model</em> -
    /// where every other service in the estate names the entity. Nothing observable turns on it; it is
    /// recorded because the message is what a developer reads in a log, and
    /// <c>EntityNotFoundException&lt;T&gt;</c> is the only place the distinction shows.
    /// </remarks>
    [Fact]
    public async Task Update_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        var response = await Put(Client(actor), Guid.NewGuid(), Body("a type that is not there"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_WithoutManageInjectTypes_Is403()
    {
        var type = await SeedInjectType();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewInjectTypes).SeedAsync();

        var response = await Put(Client(actor), type.Id, BodyFor(type) with { Name = "renamed" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(type.Name, (await Stored(type.Id)).Name);
    }

    [Fact]
    public async Task Update_WithNoBody_Is400()
    {
        var type = await SeedInjectType();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        var response = await Client(actor).PutAsync(InjectType(type.Id), EmptyJson(), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Update_BroadcastsToTheAdminDataGroupOnly()
    {
        var type = await SeedInjectType();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();
        Hub.Clear();

        await Put(Client(actor), type.Id, BodyFor(type) with { Name = "renamed" });

        Assert.Equal([MainHub.ADMIN_DATA_GROUP], Hub.Recipients(MainHubMethods.InjectTypeUpdated));
        Assert.Equal("renamed", Assert.IsType<ViewModels.InjectType>(
            Hub.Of(MainHubMethods.InjectTypeUpdated)[0].Payload).Name);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE injectTypes/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_WithManageInjectTypes_Is204AndRemovesTheInjectType()
    {
        var type = await SeedInjectType();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        var response = await Client(actor).DeleteAsync(InjectType(type.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await Stored(type.Id));
    }

    [Fact]
    public async Task Delete_TakesItsDataFieldsAndTheirOptionsWithIt()
    {
        var type = await SeedInjectType();
        var field = await SeedDataField(type.Id);
        await Seed(BlueprintAppFactory.DataOption(field.Id, "yes", "y"));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        await Client(actor).DeleteAsync(InjectType(type.Id), Ct);

        Assert.Empty(await StoredFields(type.Id));
        await using var context = NewContext();
        Assert.Equal(0, await context.DataOptions.CountAsync(x => x.DataFieldId == field.Id, Ct));
    }

    /// <remarks>
    /// <para>
    /// The headline finding. A catalog's <c>InjectTypeId</c> is required, so the cascade takes the catalog;
    /// a <c>CatalogInject</c> row's <c>CatalogId</c> is required, so it takes those; an inject's
    /// <c>InjectTypeId</c> is required, so it takes every inject of the type; and a data value's
    /// <c>DataFieldId</c> is required, so it takes every cell of every one of them. One 204 removes a
    /// library of reusable exercise content, and the caller is told only that the type is gone.
    /// </para>
    /// <para>
    /// <c>DeleteAsync</c> is six lines and asks nothing, so there is nowhere the count could have come from.
    /// Counting dependents and answering 409 - the shape <c>CompetencyFrameworkService</c> already uses for
    /// a duplicate id number - reddens this test.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Delete_SilentlyDestroysEveryCatalogAndInjectBuiltOnTheType()
    {
        var type = await SeedInjectType();
        var field = await SeedDataField(type.Id);
        var catalog = BlueprintAppFactory.Catalog(type.Id);
        await Seed(catalog);
        var inject = BlueprintAppFactory.Inject(type.Id);
        await Seed(inject);
        await Seed(BlueprintAppFactory.CatalogInject(catalog.Id, inject.Id));
        await Seed(BlueprintAppFactory.DataValueOnInject(field.Id, inject.Id, "cell"));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        var response = await Client(actor).DeleteAsync(InjectType(type.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var context = NewContext();
        Assert.Equal(0, await context.Catalogs.CountAsync(x => x.Id == catalog.Id, Ct));
        Assert.Equal(0, await context.Injects.CountAsync(x => x.Id == inject.Id, Ct));
        Assert.Equal(0, await context.CatalogInjects.CountAsync(x => x.CatalogId == catalog.Id, Ct));
        Assert.Equal(0, await context.DataValues.CountAsync(x => x.InjectId == inject.Id, Ct));
    }

    /// <remarks>
    /// And the only notification any of it produces is one <c>InjectTypeDeleted</c> to the admin data group.
    /// Nothing tells a client watching a catalog that the catalog is gone, so blueprint's UI keeps showing
    /// it until something else reloads. Broadcasting the cascade - or refusing the delete - reddens this
    /// test.
    /// </remarks>
    [Fact]
    public async Task Delete_BroadcastsOnlyThatTheTypeIsGone()
    {
        var type = await SeedInjectType();
        var catalog = BlueprintAppFactory.Catalog(type.Id);
        await Seed(catalog);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();
        Hub.Clear();

        await Client(actor).DeleteAsync(InjectType(type.Id), Ct);

        Assert.Equal([MainHub.ADMIN_DATA_GROUP], Hub.Recipients(MainHubMethods.InjectTypeDeleted));
        Assert.Equal(type.Id, Assert.IsType<Guid>(Hub.Of(MainHubMethods.InjectTypeDeleted)[0].Payload));
        Assert.Empty(Hub.Recipients(MainHubMethods.CatalogDeleted));
    }

    [Fact]
    public async Task Delete_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        var response = await Client(actor).DeleteAsync(InjectType(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_WithoutManageInjectTypes_Is403()
    {
        var type = await SeedInjectType();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewInjectTypes).SeedAsync();

        var response = await Client(actor).DeleteAsync(InjectType(type.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(await Stored(type.Id));
    }

    // ---------------------------------------------------------------------------------------------
    // POST injectTypes/json/download
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Download_AnswersAFileNamedForTheExport()
    {
        var type = await SeedInjectType();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        var response = await Download(Client(actor), type.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inject-type-export.json", response.Content.Headers.ContentDisposition?.FileName);
    }

    [Fact]
    public async Task Download_ReturnsOnlyTheRequestedInjectTypes()
    {
        var wanted = await SeedInjectType();
        var other = await SeedInjectType();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        var names = await DownloadedNames(Client(actor), wanted.Id);

        Assert.Equal(wanted.Name, Assert.Single(names));
        Assert.DoesNotContain(other.Name, names);
    }

    [Fact]
    public async Task Download_IncludesTheDataFieldsAndTheirOptions()
    {
        var type = await SeedInjectType();
        var field = BlueprintAppFactory.DataField(injectTypeId: type.Id, dataType: DataFieldType.Html);
        await Seed(field);
        await Seed(BlueprintAppFactory.DataOption(field.Id, "yes", "y"));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        var file = await Downloaded(Client(actor), type.Id);

        var exported = Values(file.RootElement).Single();
        var exportedField = Values(exported.GetProperty("DataFields")).Single();
        Assert.Equal(field.Name, exportedField.GetProperty("Name").GetString());
        var exportedOption = Values(exportedField.GetProperty("DataOptions")).Single();
        Assert.Equal("yes", exportedOption.GetProperty("OptionName").GetString());
    }

    /// <remarks>
    /// <c>DownloadJsonAsync</c> builds its own <c>JsonSerializerOptions</c> rather than using the MVC ones,
    /// so the file speaks a different dialect from every response this API sends: PascalCase names where the
    /// wire is camelCase, raw integers where <c>JsonIntegerConverter</c> writes <c>int</c> as a JSON string,
    /// a numeric enum where <c>JsonStringEnumConverter</c> writes the name, and a
    /// <c>ReferenceHandler.Preserve</c> wrapper (<c>$id</c>/<c>$values</c>) where responses use
    /// <c>IgnoreCycles</c>. Third instance of the own-options pattern on this branch, after the card
    /// templates (<c>b5d2d86</c>) and the data-option import (<c>3c31930</c>). Using the MVC options reddens
    /// this test and fixes <see cref="UploadJson_CannotReadTheEnumNamesTheApiItselfWrites"/>.
    /// </remarks>
    [Fact]
    public async Task Download_SpeaksADifferentDialectFromEveryResponse()
    {
        var type = await SeedInjectType();
        await Seed(BlueprintAppFactory.DataField(
            injectTypeId: type.Id, dataType: DataFieldType.Html, displayOrder: 3));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        var file = await Downloaded(Client(actor), type.Id);

        Assert.True(file.RootElement.TryGetProperty("$values", out _));
        var exportedField = Values(Values(file.RootElement).Single().GetProperty("DataFields")).Single();
        Assert.Equal(
            (int)DataFieldType.Html, exportedField.GetProperty("DataType").GetInt32());
        Assert.Equal(3, exportedField.GetProperty("DisplayOrder").GetInt32());
    }

    /// <remarks>
    /// The download writes no data and is behind <c>ManageInjectTypes</c> all the same, so a caller who may
    /// read every inject type through <c>GET injectTypes</c> may not export them. Requiring
    /// <c>ViewInjectTypes</c> instead reddens this test.
    /// </remarks>
    [Fact]
    public async Task Download_IsAReadBehindAManagePermission()
    {
        var type = await SeedInjectType();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewInjectTypes).SeedAsync();

        var response = await Download(Client(actor), type.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Download_ForAnIdThatIsNotThere_IsAnEmptyExport()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        var names = await DownloadedNames(Client(actor), Guid.NewGuid());

        Assert.Empty(names);
    }

    // ---------------------------------------------------------------------------------------------
    // POST injectTypes/json
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task UploadJson_CreatesTheInjectTypeItsFieldsAndItsOptions()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        var created = await Read<List<ViewModels.InjectType>>(
            await Upload(Client(actor), ExportFile("an imported type")));

        var type = Assert.Single(created);
        Assert.Equal("an imported type", type.Name);
        var field = Assert.Single(await StoredFields(type.Id));
        Assert.Equal("imported field", field.Name);
        Assert.Equal(DataFieldType.Html, field.DataType);
        await using var context = NewContext();
        var option = await context.DataOptions.AsNoTracking()
            .SingleAsync(x => x.DataFieldId == field.Id, Ct);
        Assert.Equal("yes", option.OptionName);
        Assert.Equal("y", option.OptionValue);
    }

    [Fact]
    public async Task UploadJson_ForcesAFreshIdAndStampsTheAuditFieldsOverTheFiles()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();
        var before = DateTime.UtcNow;

        var created = await Read<List<ViewModels.InjectType>>(
            await Upload(Client(actor), ExportFile("a re-identified type")));

        var type = Assert.Single(created);
        Assert.NotEqual(FileId, type.Id);
        var stored = await Stored(type.Id);
        Assert.Equal(actor.Id, stored.CreatedBy);
        AssertStampedBetween(stored.DateCreated, before, DateTime.UtcNow);
        Assert.Null(stored.ModifiedBy);
        Assert.Null(stored.DateModified);
        var field = Assert.Single(await StoredFields(type.Id));
        Assert.NotEqual(FileId, field.Id);
        Assert.Equal(actor.Id, field.CreatedBy);
    }

    /// <remarks>
    /// The three properties that decide where a field belongs are overwritten whatever the file says:
    /// <c>InjectTypeId</c> becomes the new type's, <c>MselId</c> becomes null and <c>IsTemplate</c> becomes
    /// false. So an export from a live MSEL cannot be imported as a MSEL's field or as a template, which is
    /// the right call and the only one of the two file routes that makes it - the download filters on
    /// nothing but the requested ids.
    /// </remarks>
    [Fact]
    public async Task UploadJson_ForcesEveryDataFieldOntoTheNewTypeWithNoMselAndNotATemplate()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        var created = await Read<List<ViewModels.InjectType>>(
            await Upload(Client(actor), ExportFile("a re-parented type")));

        var field = Assert.Single(await StoredFields(Assert.Single(created).Id));
        Assert.Equal(Assert.Single(created).Id, field.InjectTypeId);
        Assert.Null(field.MselId);
        Assert.False(field.IsTemplate);
    }

    /// <remarks>
    /// <c>UploadJsonAsync</c>'s own options carry no <c>JsonStringEnumConverter</c>, so a file whose
    /// <c>dataType</c> is the name every response in this API writes is a 500 - the API cannot read back
    /// what it says. A file has to use the numbers, which nothing documents and which change meaning if the
    /// enum is ever renumbered. Adding the converter reddens this test.
    /// </remarks>
    [Fact]
    public async Task UploadJson_CannotReadTheEnumNamesTheApiItselfWrites()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        var response = await Upload(
            Client(actor), ExportFile("a type with a named data type", dataType: "\"Html\""));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <remarks>
    /// Downloading a type and importing it straight back is a 500 from the unique <c>Name</c> index - the
    /// obvious way to copy an inject type on one installation, and it fails with no message a caller can
    /// act on. The upload has no transaction either, so a file holding several types imports the ones
    /// before the collision and abandons the rest. Making the upload rename, or answer 409, reddens this
    /// test.
    /// </remarks>
    [Fact]
    public async Task UploadJson_OfAFileFromTheSameInstallation_Is500()
    {
        var type = await SeedInjectType();
        await Seed(BlueprintAppFactory.DataField(injectTypeId: type.Id, dataType: DataFieldType.Html));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();
        var file = await (await Download(Client(actor), type.Id)).Content.ReadAsStringAsync(Ct);

        var response = await Upload(Client(actor.Id), file);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <remarks>
    /// Renamed past the index the same file round-trips - the two routes' shared dialect is at least
    /// self-consistent - except that <c>UploadJsonAsync</c> builds each <c>DataOptionEntity</c> by hand and
    /// its initializer has no <c>OptionDescription</c>, so every option's description is dropped. It is the
    /// one field of the three that is not part of the option's identity, which is presumably how it was
    /// missed; the download exports it faithfully, so the loss is silent and one-way. Adding the property
    /// reddens this test.
    /// </remarks>
    [Fact]
    public async Task UploadJson_OfARenamedDownload_LosesEveryOptionDescription()
    {
        var type = await SeedInjectType();
        var field = BlueprintAppFactory.DataField(injectTypeId: type.Id, dataType: DataFieldType.Html);
        await Seed(field);
        await Seed(BlueprintAppFactory.DataOption(field.Id, "yes", "y"));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();
        var file = await (await Download(Client(actor), type.Id)).Content.ReadAsStringAsync(Ct);

        var created = await Read<List<ViewModels.InjectType>>(
            await Upload(Client(actor.Id), file.Replace(type.Name, "a renamed import")));

        var importedField = Assert.Single(await StoredFields(Assert.Single(created).Id));
        await using var context = NewContext();
        var imported = await context.DataOptions.AsNoTracking()
            .SingleAsync(x => x.DataFieldId == importedField.Id, Ct);
        Assert.Equal("yes", imported.OptionName);
        Assert.Equal("y", imported.OptionValue);
        Assert.Null(imported.OptionDescription);

        var original = await context.DataOptions.AsNoTracking()
            .SingleAsync(x => x.DataFieldId == field.Id, Ct);
        Assert.NotNull(original.OptionDescription);
    }

    /// <remarks>
    /// The upload's answer carries the data fields for the same reason the create's does - the entities are
    /// tracked by the context that maps them - and the mapping happens at line 193, <em>before</em> the save
    /// on line 195. So the answer describes rows that do not exist yet, which is harmless only because the
    /// save is the next statement. Moving the map after the save reddens nothing; adding an <c>Include</c>
    /// to the read routes is what makes the API consistent.
    /// </remarks>
    [Fact]
    public async Task UploadJson_AnswersTheDataFieldsBeforeTheyAreSaved()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        var created = await Read<List<ViewModels.InjectType>>(
            await Upload(Client(actor), ExportFile("a type answered with its fields")));

        Assert.Equal("imported field", Assert.Single(Assert.Single(created).DataFields).Name);
    }

    [Fact]
    public async Task UploadJson_WithNoFilePart_Is400()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        using var content = new MultipartFormDataContent
        {
            { new StringContent("not a file"), "somethingElse" }
        };
        var response = await Client(actor).PostAsync(UploadRoute, content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UploadJson_WithoutManageInjectTypes_Is403()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewInjectTypes).SeedAsync();

        var response = await Upload(Client(actor), ExportFile("a type nobody may import"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UploadJson_BroadcastsToTheAdminDataGroupOnly()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();
        Hub.Clear();

        var created = await Read<List<ViewModels.InjectType>>(
            await Upload(Client(actor), ExportFile("a broadcast import")));

        Assert.Equal([MainHub.ADMIN_DATA_GROUP], Hub.Recipients(MainHubMethods.InjectTypeCreated));
        Assert.Equal(Assert.Single(created).Id, Assert.IsType<ViewModels.InjectType>(
            Hub.Of(MainHubMethods.InjectTypeCreated)[0].Payload).Id);
    }

    // ---------------------------------------------------------------------------------------------
    // Authentication
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "injecttypes")]
    [InlineData("GET", "injecttypes/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "injecttypes")]
    [InlineData("PUT", "injecttypes/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "injecttypes/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "injecttypes/json/download")]
    public async Task EveryRouteRefusesAnUnauthenticatedRequest(string method, string route)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/{route}")
        {
            Content = JsonContent.Create(new { })
        };

        var response = await AnonymousClient.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <remarks>
    /// The upload takes a <c>FileForm</c> rather than a bare <c>IFormFile</c>, so MVC infers no
    /// <c>[Consumes]</c> for it and the request reaches authentication - a route taking the bare type is a
    /// 415 before authentication instead, which is why <c>DataOptionEndpointTests</c> keeps its equivalent
    /// out of the sweep. This one could join the sweep and is separate for the same reason that one is: the
    /// request has to be real multipart for the answer to mean anything.
    /// </remarks>
    [Fact]
    public async Task UploadJson_Anonymously_Is401()
    {
        var response = await Upload(AnonymousClient, ExportFile("a type nobody may import"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------

    private const string InjectTypes = "/api/injecttypes";

    private const string UploadRoute = "/api/injecttypes/json";

    private const string DownloadRoute = "/api/injecttypes/json/download";

    private static string InjectType(Guid id) => $"{InjectTypes}/{id}";

    /// <summary>
    /// The id every entity in <see cref="ExportFile"/> carries, so a test can assert the upload replaced it.
    /// </summary>
    private static readonly Guid FileId = new("11111111-1111-1111-1111-111111111111");

    private sealed record InjectTypeBody
    {
        public Guid Id { get; init; }
        public string Name { get; init; }
        public string Description { get; init; }
        public List<DataFieldBody> DataFields { get; init; } = [];

        public Guid CreatedBy { get; init; }
        public DateTime DateCreated { get; init; }
        public Guid? ModifiedBy { get; init; }
        public DateTime? DateModified { get; init; }
    }

    private sealed record DataFieldBody
    {
        public Guid Id { get; init; }
        public Guid? MselId { get; init; }
        public Guid? InjectTypeId { get; init; }
        public string Name { get; init; }
        public string Description { get; init; }
        public DataFieldType DataType { get; init; }
        public int DisplayOrder { get; init; }

        /// <remarks>
        /// Not <c>bool?</c>: <c>ViewModels.DataField.IsTemplate</c> is a non-nullable <c>bool</c>, so a null
        /// is a 400 from System.Text.Json that never reaches the controller - the same trap
        /// <c>ViewModels.Base</c>'s audit fields set, recorded for the scenario-event unit.
        /// </remarks>
        public bool IsTemplate { get; init; }

        public Guid CreatedBy { get; init; }
        public DateTime DateCreated { get; init; }
        public Guid? ModifiedBy { get; init; }
        public DateTime? DateModified { get; init; }
    }

    private static InjectTypeBody Body(string name) => new()
    {
        Name = name,
        Description = "posted by the test"
    };

    private static InjectTypeBody BodyFor(InjectTypeEntity type) => new()
    {
        Id = type.Id,
        Name = type.Name,
        Description = type.Description,
        CreatedBy = type.CreatedBy,
        DateCreated = type.DateCreated
    };

    private static DataFieldBody Field(string name, DataFieldType dataType, int displayOrder) => new()
    {
        Name = name,
        Description = "a field posted by the test",
        DataType = dataType,
        DisplayOrder = displayOrder
    };

    /// <summary>
    /// A file in the dialect <c>DownloadJsonAsync</c> writes and <c>UploadJsonAsync</c> reads: a plain array
    /// (the <c>Preserve</c> reader accepts one), camel-cased because the reader is case-insensitive, and with
    /// <c>dataType</c> as a <em>number</em> because the reader has no enum converter. Every id and audit
    /// field is hostile so a test can assert what the upload overwrites.
    /// </summary>
    private static string ExportFile(string typeName, string dataType = "60") =>
        $$"""
        [
          {
            "id": "{{FileId}}",
            "name": "{{typeName}}",
            "description": "imported by the test",
            "createdBy": "{{FileId}}",
            "dateCreated": "1999-01-01T00:00:00Z",
            "modifiedBy": "{{FileId}}",
            "dateModified": "1999-01-01T00:00:00Z",
            "dataFields": [
              {
                "id": "{{FileId}}",
                "mselId": "{{FileId}}",
                "injectTypeId": "{{FileId}}",
                "name": "imported field",
                "description": "a field from the file",
                "dataType": {{dataType}},
                "displayOrder": 3,
                "isTemplate": true,
                "createdBy": "{{FileId}}",
                "dateCreated": "1999-01-01T00:00:00Z",
                "dataOptions": [
                  {
                    "id": "{{FileId}}",
                    "dataFieldId": "{{FileId}}",
                    "optionName": "yes",
                    "optionValue": "y",
                    "optionDescription": "a description from the file",
                    "displayOrder": 1,
                    "createdBy": "{{FileId}}",
                    "dateCreated": "1999-01-01T00:00:00Z"
                  }
                ]
              }
            ]
          }
        ]
        """;

    private Task<HttpResponseMessage> Post(HttpClient client, InjectTypeBody body) =>
        client.PostAsJsonAsync(InjectTypes, body, Ct);

    private Task<HttpResponseMessage> Put(HttpClient client, Guid id, InjectTypeBody body) =>
        client.PutAsJsonAsync(InjectType(id), body, Ct);

    private Task<HttpResponseMessage> Download(HttpClient client, params Guid[] ids) =>
        client.PostAsJsonAsync(DownloadRoute, ids, Ct);

    /// <remarks>
    /// The content is disposed only after the request completes - <c>TestServer</c> reads the body inside
    /// <c>SendAsync</c>, so returning the task unawaited is an <c>ObjectDisposedException</c>. Phase 1's
    /// gotcha, repeated in every file with an upload.
    /// </remarks>
    private async Task<HttpResponseMessage> Upload(HttpClient client, string json)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        file.Headers.ContentType = new("application/json");
        content.Add(file, "toUpload", "inject-type-export.json");

        return await client.PostAsync(UploadRoute, content, Ct);
    }

    private async Task<JsonDocument> Downloaded(HttpClient client, params Guid[] ids)
    {
        var response = await Download(client, ids);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        return JsonDocument.Parse(body);
    }

    private async Task<List<string>> DownloadedNames(HttpClient client, params Guid[] ids)
    {
        using var file = await Downloaded(client, ids);

        return Values(file.RootElement).Select(x => x.GetProperty("Name").GetString()).ToList();
    }

    /// <summary>
    /// Unwraps a <c>ReferenceHandler.Preserve</c> collection. An empty collection is written as a bare
    /// <c>[]</c> rather than a wrapper, so both shapes are handled.
    /// </summary>
    private static List<JsonElement> Values(JsonElement element) =>
        (element.ValueKind == JsonValueKind.Array
            ? element
            : element.GetProperty("$values")).EnumerateArray().ToList();

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

    private async Task<InjectTypeEntity> Stored(Guid id)
    {
        await using var context = NewContext();

        return await context.InjectTypes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, Ct);
    }

    private async Task<List<DataFieldEntity>> StoredFields(Guid injectTypeId)
    {
        await using var context = NewContext();

        return await context.DataFields.AsNoTracking()
            .Where(x => x.InjectTypeId == injectTypeId).ToListAsync(Ct);
    }

    private static StringContent EmptyJson() =>
        new(string.Empty, Encoding.UTF8, "application/json");

    private static void AssertStampedBetween(DateTime? actual, DateTime notBefore, DateTime notAfter)
    {
        Assert.NotNull(actual);
        Assert.InRange(actual.Value, notBefore, notAfter);
    }

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }
}
