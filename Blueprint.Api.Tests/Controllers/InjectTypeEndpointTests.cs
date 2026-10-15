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

/// <summary><c>InjectTypeService</c> / <c>InjectTypeController</c> - the seven routes behind an inject
/// type, which is the column layout every catalog and every inject of that catalog is built on. The
/// companion file is <see cref="InjectEndpointTests"/>; this one covers the schema and that one covers the
/// rows.</summary>
public class InjectTypeEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET injectTypes
    // ---------------------------------------------------------------------------------------------

    /// <summary>The list carries every inject type, in no defined order, so the assertion is a set.</summary>
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

    /// <summary>List answers an empty data fields collection.</summary>
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

    /// <summary>Get answers an empty data fields collection.</summary>
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
            await ClientFor(actor.Id, null).GetAsync($"/api/injecttypes/{type.Id}/dataFields", Ct));
        Assert.Equal(field.Id, Assert.Single(fields).Id);
    }

    /// <summary>Get for an id that is not there is answered with a 404.</summary>
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

    /// <summary>Create leaves the nested data fields created by unstamped.</summary>
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

    /// <summary>The create answers the type's data fields, from the entities the request's context is still
    /// tracking.</summary>
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
            await ClientFor(actor.Id, null).GetAsync(InjectType(created.Id), Ct));
        Assert.Empty(reread.DataFields);
    }

    /// <summary>A create keeps the id the body carries.</summary>
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

    /// <summary>Create with a duplicate name is answered with a 500.</summary>
    [Fact]
    public async Task Create_WithADuplicateName_Is500()
    {
        var existing = await SeedInjectType();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        var response = await Post(Client(actor), Body(existing.Name));

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("An error occurred while saving the entity changes. See the inner exception for details.", failure.Title);
        Assert.Contains("InjectTypeService.CreateAsync", failure.Detail);
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

    /// <summary>A new type is broadcast to the admin data group, and its fields to the empty-string
    /// group.</summary>
    [Fact]
    public async Task Create_BroadcastsToTheAdminDataGroupAndTheEmptyStringGroup()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();
        var body = Body("a broadcast type") with
        {
            DataFields = [Field("headline", DataFieldType.Html, 1)]
        };

        var created = await Read<ViewModels.InjectType>(await Post(Client(actor), body));

        Assert.Equal([MainHub.ADMIN_DATA_GROUP], Hub.Recipients(MainHubMethods.InjectTypeCreated, created.Id));
        Assert.Equal(created.Id, Assert.IsType<ViewModels.InjectType>(
            Hub.Of(MainHubMethods.InjectTypeCreated, created.Id)[0].Payload).Id);
        Assert.Equal(
            [string.Empty, MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.DataFieldCreated, string.Empty));
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

    /// <summary>Update answers only the data fields the body carried.</summary>
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

    /// <summary>Update stores a data field the body carries.</summary>
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

    /// <summary>Update may steal another inject types data field.</summary>
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

    /// <summary>Update whose body id is not the routes is answered with a 500.</summary>
    [Fact]
    public async Task Update_WhoseBodyIdIsNotTheRoutes_Is500()
    {
        var type = await SeedInjectType();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        var response = await Put(
            Client(actor), type.Id, BodyFor(type) with { Id = Guid.NewGuid() });

        Assert.Equal("The property 'InjectTypeEntity.Id' is part of a key and so cannot be modified or marked as modified. To change the principal of an existing entity with an identifying foreign key, first delete the dependent and invoke 'SaveChanges', and then associate the dependent with the new principal.", (await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
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

        await Put(Client(actor), type.Id, BodyFor(type) with { Name = "renamed" });

        Assert.Equal([MainHub.ADMIN_DATA_GROUP], Hub.Recipients(MainHubMethods.InjectTypeUpdated, type.Id));
        Assert.Equal("renamed", Assert.IsType<ViewModels.InjectType>(
            Hub.Of(MainHubMethods.InjectTypeUpdated, type.Id)[0].Payload).Name);
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
        await Seed(TestData.DataOption(field.Id, "yes", "y"));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        await Client(actor).DeleteAsync(InjectType(type.Id), Ct);

        Assert.Empty(await StoredFields(type.Id));
        await using var context = NewContext();
        Assert.Equal(0, await context.DataOptions.CountAsync(x => x.DataFieldId == field.Id, Ct));
    }

    /// <summary>Delete silently destroys every catalog and inject built on the type.</summary>
    [Fact]
    public async Task Delete_SilentlyDestroysEveryCatalogAndInjectBuiltOnTheType()
    {
        var type = await SeedInjectType();
        var field = await SeedDataField(type.Id);
        var catalog = TestData.Catalog(type.Id);
        await Seed(catalog);
        var inject = TestData.Inject(type.Id);
        await Seed(inject);
        await Seed(TestData.CatalogInject(catalog.Id, inject.Id));
        await Seed(TestData.DataValueOnInject(field.Id, inject.Id, "cell"));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        var response = await Client(actor).DeleteAsync(InjectType(type.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var context = NewContext();
        Assert.Equal(0, await context.Catalogs.CountAsync(x => x.Id == catalog.Id, Ct));
        Assert.Equal(0, await context.Injects.CountAsync(x => x.Id == inject.Id, Ct));
        Assert.Equal(0, await context.CatalogInjects.CountAsync(x => x.CatalogId == catalog.Id, Ct));
        Assert.Equal(0, await context.DataValues.CountAsync(x => x.InjectId == inject.Id, Ct));
    }

    /// <summary>Delete broadcasts only that the type is gone.</summary>
    [Fact]
    public async Task Delete_BroadcastsOnlyThatTheTypeIsGone()
    {
        var type = await SeedInjectType();
        var catalog = TestData.Catalog(type.Id);
        await Seed(catalog);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        await Client(actor).DeleteAsync(InjectType(type.Id), Ct);

        Assert.Equal([MainHub.ADMIN_DATA_GROUP], Hub.Recipients(MainHubMethods.InjectTypeDeleted, type.Id, catalog.Id));
        Assert.Equal(type.Id, Assert.IsType<Guid>(Hub.Of(MainHubMethods.InjectTypeDeleted, type.Id, catalog.Id)[0].Payload));
        Assert.Empty(Hub.Recipients(MainHubMethods.CatalogDeleted, catalog.Id, type.Id));
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
        var field = TestData.DataField(injectTypeId: type.Id, dataType: DataFieldType.Html);
        await Seed(field);
        await Seed(TestData.DataOption(field.Id, "yes", "y"));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        var file = await Downloaded(Client(actor), type.Id);

        var exported = Values(file.RootElement).Single();
        var exportedField = Values(exported.GetProperty("DataFields")).Single();
        Assert.Equal(field.Name, exportedField.GetProperty("Name").GetString());
        var exportedOption = Values(exportedField.GetProperty("DataOptions")).Single();
        Assert.Equal("yes", exportedOption.GetProperty("OptionName").GetString());
    }

    /// <summary>Download speaks a different dialect from every response.</summary>
    [Fact]
    public async Task Download_SpeaksADifferentDialectFromEveryResponse()
    {
        var type = await SeedInjectType();
        await Seed(TestData.DataField(
            injectTypeId: type.Id, dataType: DataFieldType.Html, displayOrder: 3));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        var file = await Downloaded(Client(actor), type.Id);

        Assert.True(file.RootElement.TryGetProperty("$values", out _));
        var exportedField = Values(Values(file.RootElement).Single().GetProperty("DataFields")).Single();
        Assert.Equal(
            (int)DataFieldType.Html, exportedField.GetProperty("DataType").GetInt32());
        Assert.Equal(3, exportedField.GetProperty("DisplayOrder").GetInt32());
    }

    /// <summary>Download is a read behind a manage permission.</summary>
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

    /// <summary>Upload JSON cannot read the enum names the API itself writes.</summary>
    [Fact]
    public async Task UploadJson_CannotReadTheEnumNamesTheApiItselfWrites()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();

        var response = await Upload(
            Client(actor), ExportFile("a type with a named data type", dataType: "\"Html\""));

        Assert.Equal("The JSON value could not be converted to Blueprint.Api.Data.Enumerations.DataFieldType. Path: $[0].dataFields[0].dataType | LineNumber: 16 | BytePositionInLine: 26.", (await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
    }

    /// <summary>Upload JSON of a file from the same installation is answered with a 500.</summary>
    [Fact]
    public async Task UploadJson_OfAFileFromTheSameInstallation_Is500()
    {
        var type = await SeedInjectType();
        await Seed(TestData.DataField(injectTypeId: type.Id, dataType: DataFieldType.Html));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();
        var file = await (await Download(Client(actor), type.Id)).Content.ReadAsStringAsync(Ct);

        var response = await Upload(ClientFor(actor.Id, null), file);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("An error occurred while saving the entity changes. See the inner exception for details.", failure.Title);
        Assert.Contains("InjectTypeService.UploadJsonAsync", failure.Detail);
    }

    /// <summary>Upload JSON of a renamed download loses every option description.</summary>
    [Fact]
    public async Task UploadJson_OfARenamedDownload_LosesEveryOptionDescription()
    {
        var type = await SeedInjectType();
        var field = TestData.DataField(injectTypeId: type.Id, dataType: DataFieldType.Html);
        await Seed(field);
        await Seed(TestData.DataOption(field.Id, "yes", "y"));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageInjectTypes).SeedAsync();
        var file = await (await Download(Client(actor), type.Id)).Content.ReadAsStringAsync(Ct);

        var created = await Read<List<ViewModels.InjectType>>(
            await Upload(ClientFor(actor.Id, null), file.Replace(type.Name, "a renamed import")));

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

    /// <summary>The upload maps its answer before the save, from the tracked entities.</summary>
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

        var created = await Read<List<ViewModels.InjectType>>(
            await Upload(Client(actor), ExportFile("a broadcast import")));

        Assert.Equal([MainHub.ADMIN_DATA_GROUP], Hub.Recipients(MainHubMethods.InjectTypeCreated, Assert.Single(created).Id));
        Assert.Equal(Assert.Single(created).Id, Assert.IsType<ViewModels.InjectType>(
            Hub.Of(MainHubMethods.InjectTypeCreated, Assert.Single(created).Id)[0].Payload).Id);
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

        var response = await Client().SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>An upload with no identity is answered with a 401; the anonymous client is the case under
    /// test.</summary>
    [Fact]
    public async Task UploadJson_Anonymously_Is401()
    {
        var response = await Upload(Client(), ExportFile("a type nobody may import"));

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
    /// <c>SendAsync</c>, so returning the task unawaited is an <c>ObjectDisposedException</c>.
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
