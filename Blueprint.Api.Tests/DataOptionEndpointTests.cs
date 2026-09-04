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
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// The seven data-option endpoints, driven over HTTP, and the three file parsers behind the last of them.
/// </summary>
/// <remarks>
/// <para>
/// A data option is one entry in the drop-down list a <c>DataField</c> offers, so this file closes the
/// data-field graph: <see cref="DataFieldEndpointTests"/> covers the column, <see cref="DataValueEndpointTests"/>
/// covers the cell, and this covers the vocabulary the cell is allowed to hold. An option's scope is
/// inherited rather than declared - it has no <c>MselId</c> of its own, only a <c>DataFieldId</c>, and every
/// permission decision here is taken by looking up that field and asking whether it belongs to an MSEL.
/// </para>
/// <para>
/// <strong>There is no <c>DataOptionHandler</c>, so an option is never broadcast in its own right.</strong>
/// <c>MainHub</c> declares no <c>DataOption*</c> method and
/// <c>Infrastructure/EventHandlers/</c> holds no handler for the entity, so the only way a change to an
/// option reaches a connected client is as a side effect of its parent field being marked modified and
/// re-sent with <c>.Include(f =&gt; f.DataOptions)</c>. Create and delete do mark the field. Update does
/// not - <c>UpdateAsync</c> line 171 assigns <c>dataField.ModifiedBy = dataFieldEntity.ModifiedBy</c> on
/// what is the same tracked instance, i.e. a self-assignment - so **renaming a drop-down option is
/// invisible to every client until something else touches the column**. See
/// <see cref="Update_DoesNotMarkTheDataFieldModified"/> and <see cref="Update_BroadcastsNothing"/>.
/// </para>
/// <para>
/// <strong>The three mutating methods each answer "who modified the field" differently, and only one is
/// right.</strong> <c>CreateAsync</c> lines 132-133 re-fetch the field into a second variable that
/// <c>FindAsync</c> resolves from the change tracker to the instance already held, then assign
/// <c>dataField.ModifiedBy = dataFieldEntity.CreatedBy</c> - so adding an option records the field's own
/// author as the last person to touch it, whoever actually did
/// (<see cref="Create_MarksTheFieldModifiedByItsOwnCreatorRatherThanTheCaller"/>). <c>UpdateAsync</c>
/// records nobody. <c>DeleteAsync</c> line 207 records the caller, which is what all three should do.
/// </para>
/// <para>
/// <strong><c>UpdateAsync</c> takes its permission decision from the request body's <c>DataFieldId</c>,</strong>
/// never from the stored option's, so a caller who owns any MSEL at all may reassign every option of every
/// other one into their own column - the third instance of this defect in the graph, after
/// <c>OrganizationService.UpdateAsync</c> and <c>ScenarioEventService.UpdateAsync</c>. See
/// <see cref="Update_TakesItsPermissionDecisionFromTheRequestBodysDataField"/>.
/// </para>
/// <para>
/// The reads are inconsistent in the other direction. <c>GetByDataFieldAsync</c> and <c>GetAsync</c> both
/// treat a field with no <c>MselId</c> as world-readable, which is intended for a shared template but also
/// silently exposes every inject type's option list to any signed-in account. And
/// <c>PreviewImportAsync</c> resolves its MSEL check through the *three*-argument
/// <c>MselViewRequirement.IsMet</c> overload, where the reads use the four-argument one, so a
/// <c>CreateMsels</c> holder may read every option on a template MSEL's field and not preview a file
/// against it - and, in the other direction, a bare team member holding no MSEL role may preview an import
/// they could never apply, because a *view* requirement gates a *create* operation.
/// </para>
/// <para>
/// <strong><c>previewDataOptionImport</c> has no import counterpart anywhere in the API.</strong> Nothing
/// consumes a <c>DataOptionImportPreview</c>; the only way to get the options in is one
/// <c>POST dataOptions</c> at a time. So the whole of <c>ParseJsonAsync</c>/<c>ParseCsvAsync</c>/
/// <c>ParseXlsxAsync</c> - 380 of the service's 640 lines - is advisory, which is worth knowing before
/// reading their defects as urgent. They are covered here anyway, because the preview is what a user
/// believes an import will do.
/// </para>
/// <para>
/// Per this branch's rule, every test above characterizes rather than fixes, and says what fixing it will
/// do to the test.
/// </para>
/// </remarks>
public class DataOptionEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET msels/{mselId}/dataOptions
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByMsel_ReturnsTheOptionsOfEveryFieldOnTheMsel()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var first = BlueprintAppFactory.DataField(mselId: msel.Id);
        var second = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(first, second);
        await Seed(
            BlueprintAppFactory.DataOption(first.Id, "red"),
            BlueprintAppFactory.DataOption(first.Id, "green"),
            BlueprintAppFactory.DataOption(second.Id, "blue"));

        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var returned = await GetOptions(Client(actor), $"/api/msels/{msel.Id}/dataOptions");

        Assert.Equal(
            ["blue", "green", "red"],
            returned.Select(x => x.OptionName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task GetByMsel_DoesNotReturnAnotherMselsOptions()
    {
        var mine = BlueprintAppFactory.Msel();
        var theirs = BlueprintAppFactory.Msel();
        await Seed(mine, theirs);

        var myField = BlueprintAppFactory.DataField(mselId: mine.Id);
        var theirField = BlueprintAppFactory.DataField(mselId: theirs.Id);
        await Seed(myField, theirField);
        await Seed(
            BlueprintAppFactory.DataOption(myField.Id, "mine"),
            BlueprintAppFactory.DataOption(theirField.Id, "theirs"));

        var actor = await Actor().OnMsel(mine, MselRole.Viewer).SeedAsync();

        var returned = await GetOptions(Client(actor), $"/api/msels/{mine.Id}/dataOptions");

        Assert.Equal("mine", Assert.Single(returned).OptionName);
    }

    /// <remarks>
    /// The route resolves the MSEL's own fields only, so a template column the grid also displays - and
    /// every option on it - is absent from this answer. The UI has to fetch
    /// <c>GET dataFields/templates</c> separately to fill those drop-downs.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_ReturnsNothingForATemplateOrAnInjectTypeField()
    {
        var msel = BlueprintAppFactory.Msel();
        var injectType = BlueprintAppFactory.InjectType();
        await Seed(msel, injectType);

        var template = BlueprintAppFactory.DataField();
        var onInjectType = BlueprintAppFactory.DataField(injectTypeId: injectType.Id);
        await Seed(template, onInjectType);
        await Seed(
            BlueprintAppFactory.DataOption(template.Id, "template"),
            BlueprintAppFactory.DataOption(onInjectType.Id, "injectType"));

        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        Assert.Empty(await GetOptions(Client(actor), $"/api/msels/{msel.Id}/dataOptions"));
    }

    [Fact]
    public async Task GetByMsel_ForTheMselsCreator_Is200()
    {
        var actor = await Actor().SeedAsync();

        var msel = BlueprintAppFactory.Msel(createdBy: actor.Id);
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);
        await Seed(BlueprintAppFactory.DataOption(field.Id, "mine"));

        var returned = await GetOptions(Client(actor), $"/api/msels/{msel.Id}/dataOptions");

        Assert.Equal("mine", Assert.Single(returned).OptionName);
    }

    /// <remarks>
    /// A team member holding no MSEL role at all satisfies <c>MselViewRequirement</c>, which is the
    /// loosest of the read paths in the graph and the one the exercise participants themselves use.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_ForABareTeamMember_Is200()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);
        await Seed(BlueprintAppFactory.DataOption(field.Id, "visible"));

        var actor = await Actor().OnTeam(team).SeedAsync();

        var returned = await GetOptions(Client(actor), $"/api/msels/{msel.Id}/dataOptions");

        Assert.Equal("visible", Assert.Single(returned).OptionName);
    }

    /// <remarks>
    /// <para>
    /// The controller resolves <c>CreateMsels</c> as well as <c>ViewMsels</c> and passes it into the
    /// four-argument <c>MselViewRequirement.IsMet</c>, whose first clause admits anybody holding
    /// <c>CreateMsels</c> to any MSEL flagged <c>IsTemplate</c> - the mechanism by which a would-be author
    /// browses the installation's starting points.
    /// </para>
    /// <para>
    /// <c>PreviewImportAsync</c> uses the three-argument overload instead, which hardcodes that flag to
    /// false, so the same caller on the same field cannot preview an import against it. See
    /// <see cref="Preview_OnATemplateMselsField_WithCreateMsels_Is403"/>; the pair is the finding.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GetByMsel_OnATemplateMsel_WithCreateMsels_Is200()
    {
        var msel = BlueprintAppFactory.Msel(isTemplate: true);
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);
        await Seed(BlueprintAppFactory.DataOption(field.Id, "starting-point"));

        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var returned = await GetOptions(Client(actor), $"/api/msels/{msel.Id}/dataOptions");

        Assert.Equal("starting-point", Assert.Single(returned).OptionName);
    }

    [Fact]
    public async Task GetByMsel_WithViewMsels_Is200()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);
        await Seed(BlueprintAppFactory.DataOption(field.Id, "visible"));

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var returned = await GetOptions(Client(actor), $"/api/msels/{msel.Id}/dataOptions");

        Assert.Equal("visible", Assert.Single(returned).OptionName);
    }

    [Fact]
    public async Task GetByMsel_WithNoRoleOnTheMsel_Is403()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);
        await Seed(BlueprintAppFactory.DataOption(field.Id, "secret"));

        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/dataOptions", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// The route answers 200 with an empty array for an MSEL that does not exist, because the permission
    /// gate is satisfied by <c>ViewMsels</c> without the MSEL ever being loaded and the query underneath
    /// is a filter rather than a lookup. This is the same shape as
    /// <c>ScenarioEventEndpointTests.GetByMsel_ForAnUnknownMsel_IsAnEmptyArray</c>, and it is why the UI
    /// cannot distinguish "this MSEL has no options" from "this MSEL is gone". Fixing it to 404 turns this
    /// test red.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_ForAnUnknownMsel_IsAnEmptyArray()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Empty(await GetOptions(Client(actor), $"/api/msels/{Guid.NewGuid()}/dataOptions"));
    }

    /// <remarks>
    /// The same unknown MSEL answers 403 to a caller without <c>ViewMsels</c>, because
    /// <c>MselViewRequirement.IsMet</c> returns false rather than throwing when the MSEL is missing. So the
    /// status code for an id that does not exist depends on the caller's system permissions, which is the
    /// worst of both answers. Compare <c>DataFieldEndpointTests.GetByMsel_ForAnUnknownMsel_Is500</c>: the
    /// sibling route dereferences the null instead.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_ForAnUnknownMsel_WithoutViewMsels_Is403()
    {
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{Guid.NewGuid()}/dataOptions", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // GET datafields/{dataFieldId}/dataOptions
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByDataField_ReturnsOnlyThatFieldsOptions()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var wanted = BlueprintAppFactory.DataField(mselId: msel.Id);
        var other = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(wanted, other);
        await Seed(
            BlueprintAppFactory.DataOption(wanted.Id, "wanted"),
            BlueprintAppFactory.DataOption(other.Id, "other"));

        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var returned = await GetOptions(Client(actor), $"/api/datafields/{wanted.Id}/dataOptions");

        Assert.Equal("wanted", Assert.Single(returned).OptionName);
    }

    [Fact]
    public async Task GetByDataField_WithNoOptions_IsAnEmptyArray()
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);

        var actor = await Actor().SeedAsync();

        Assert.Empty(await GetOptions(Client(actor), $"/api/datafields/{field.Id}/dataOptions"));
    }

    [Fact]
    public async Task GetByDataField_ForAnUnknownField_Is404()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Client(actor).GetAsync($"/api/datafields/{Guid.NewGuid()}/dataOptions", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetByDataField_OnAMselsField_WithNoRoleOnTheMsel_Is403()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);
        await Seed(BlueprintAppFactory.DataOption(field.Id, "secret"));

        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync($"/api/datafields/{field.Id}/dataOptions", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// <c>GetByDataFieldAsync</c> guards its permission check with <c>!(dataField.MselId == null)</c>, so
    /// every field outside an MSEL is world-readable to any signed-in account. For a shared template that
    /// is the intent. For an inject type it is not: an inject type belongs to a <c>Catalog</c>, whose own
    /// read path runs through <c>CatalogViewRequirement</c>, and this route bypasses it. Both cases are
    /// asserted here because they are one condition, and giving the inject-type branch its own check turns
    /// the second theory case red.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetByDataField_OnAFieldWithNoMsel_IsReadableByAnybody(bool template)
    {
        var injectType = BlueprintAppFactory.InjectType();
        await Seed(injectType);

        var field = template
            ? BlueprintAppFactory.DataField()
            : BlueprintAppFactory.DataField(injectTypeId: injectType.Id);
        await Seed(field);
        await Seed(BlueprintAppFactory.DataOption(field.Id, "exposed"));

        var actor = await Actor().SeedAsync();

        var returned = await GetOptions(Client(actor), $"/api/datafields/{field.Id}/dataOptions");

        Assert.Equal("exposed", Assert.Single(returned).OptionName);
    }

    // ---------------------------------------------------------------------------------------------
    // GET dataOptions/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ReturnsTheStoredOption()
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);

        var option = BlueprintAppFactory.DataOption(field.Id, "high", displayOrder: 3);
        await Seed(option);

        var actor = await Actor().SeedAsync();

        var returned = await GetOption(Client(actor), option.Id);

        Assert.Equal(option.Id, returned.Id);
        Assert.Equal(field.Id, returned.DataFieldId);
        Assert.Equal("high", returned.OptionName);
        Assert.Equal("high", returned.OptionValue);
        Assert.Equal(3, returned.DisplayOrder);
    }

    [Fact]
    public async Task Get_ForAnUnknownId_Is404()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Client(actor).GetAsync($"/api/dataOptions/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_OnAMselsField_WithNoRoleOnTheMsel_Is403()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);

        var option = BlueprintAppFactory.DataOption(field.Id, "secret");
        await Seed(option);

        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync($"/api/dataOptions/{option.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// Same condition as <see cref="GetByDataField_OnAFieldWithNoMsel_IsReadableByAnybody"/>, written here
    /// as <c>if (dataField.MselId.HasValue)</c> around the whole check.
    /// </remarks>
    [Fact]
    public async Task Get_OnATemplateField_IsReadableByAnybody()
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);

        var option = BlueprintAppFactory.DataOption(field.Id, "shared");
        await Seed(option);

        var actor = await Actor().SeedAsync();

        Assert.Equal("shared", (await GetOption(Client(actor), option.Id)).OptionName);
    }

    [Fact]
    public async Task Get_SerializesPropertyNamesInCamelCase()
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);

        var option = BlueprintAppFactory.DataOption(field.Id, "high");
        await Seed(option);

        var actor = await Actor().SeedAsync();

        var json = await (await Client(actor).GetAsync($"/api/dataOptions/{option.Id}", Ct))
            .Content.ReadAsStringAsync(Ct);

        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            [
                "createdBy", "dataFieldId", "dateCreated", "dateModified", "displayOrder", "id",
                "modifiedBy", "optionDescription", "optionName", "optionValue"
            ],
            document.RootElement.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal));
    }

    /// <remarks>
    /// <c>Startup.cs:164-167</c> registers a <c>JsonIntegerConverter</c> alongside the nullable-Guid and
    /// double converters, and it writes every <c>int</c> as a JSON <em>string</em>. So
    /// <c>displayOrder</c> - the property the whole drop-down ordering turns on - goes out as
    /// <c>"3"</c>, not <c>3</c>. The converter reads both forms back, which is why nothing has ever
    /// noticed, and why a generated TypeScript client types this as <c>number</c> and is wrong. Removing
    /// the converter turns this test red; it belongs in the contract snapshot either way.
    /// </remarks>
    [Fact]
    public async Task Get_SerializesDisplayOrderAsAJsonString()
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);

        var option = BlueprintAppFactory.DataOption(field.Id, "high", displayOrder: 3);
        await Seed(option);

        var actor = await Actor().SeedAsync();

        var json = await (await Client(actor).GetAsync($"/api/dataOptions/{option.Id}", Ct))
            .Content.ReadAsStringAsync(Ct);

        using var document = JsonDocument.Parse(json);
        var displayOrder = document.RootElement.GetProperty("displayOrder");

        Assert.Equal(JsonValueKind.String, displayOrder.ValueKind);
        Assert.Equal("3", displayOrder.GetString());
    }

    // ---------------------------------------------------------------------------------------------
    // POST dataOptions
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_ReturnsTheCreatedOptionAndItsLocation()
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Post(Client(actor), Body(field.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await Read<DataOption>(response);

        Assert.Equal(field.Id, created.DataFieldId);
        Assert.Equal("Created Option", created.OptionName);
        Assert.Equal("created", created.OptionValue);
        Assert.Equal("Created by a test", created.OptionDescription);
        Assert.Equal(1, created.DisplayOrder);

        // Startup.cs:203-205 sets RouteOptions.LowercaseUrls, so the generated Location is lowercased
        // even though the route template spells the segment "dataOptions".
        Assert.Equal(
            $"http://localhost/api/dataoptions/{created.Id}",
            response.Headers.Location?.ToString());

        await using var context = NewContext();

        Assert.Equal("Created Option", (await context.DataOptions.SingleAsync(Ct)).OptionName);
    }

    [Fact]
    public async Task Create_StampsTheAuditFieldsOnTheServer()
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var before = DateTime.UtcNow;

        var created = await Read<DataOption>(await Post(
            Client(actor),
            Body(field.Id) with
            {
                CreatedBy = Guid.NewGuid(),
                DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ModifiedBy = Guid.NewGuid(),
                DateModified = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            }));

        Assert.Equal(actor.Id, created.CreatedBy);
        AssertStampedBetween(created.DateCreated, before, DateTime.UtcNow);
        Assert.Null(created.ModifiedBy);
        Assert.Null(created.DateModified);
    }

    /// <remarks>
    /// <c>DataOptionEntity.Id</c> is <c>DatabaseGeneratedOption.Identity</c>, but <c>CreateAsync</c>
    /// line 126 keeps a non-empty id from the body, and <c>DataOptionProfile</c> does not ignore it. That
    /// is deliberate here - <c>DataFieldService.UploadJsonAsync</c> relies on it to rebuild a field's
    /// options with their original ids - and it is the same mechanism that makes
    /// <see cref="Update_WithAMismatchedIdInTheBody_Is500"/> a 500 rather than a 404.
    /// </remarks>
    [Fact]
    public async Task Create_HonoursAClientSuppliedId()
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var id = Guid.NewGuid();

        var created = await Read<DataOption>(await Post(Client(actor), Body(field.Id) with { Id = id }));

        Assert.Equal(id, created.Id);
    }

    [Fact]
    public async Task Create_WithoutABody_Is400()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        using var empty = new StringContent(string.Empty, Encoding.UTF8, "application/json");

        var response = await Client(actor).PostAsync("/api/dataOptions", empty, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_ForAnUnknownDataField_Is404()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Post(Client(actor), Body(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <remarks>
    /// There is no <c>IEntityTypeConfiguration</c> for <c>DataOptionEntity</c> at all, so nothing forbids
    /// two options on one field sharing a name - and because <c>OptionName</c> is what a
    /// <c>DataValue</c> stores, a duplicated name makes the stored value ambiguous. Adding a unique index
    /// on <c>(DataFieldId, OptionName)</c> turns this test red.
    /// </remarks>
    [Fact]
    public async Task Create_WithADuplicateOptionName_Succeeds()
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);
        await Seed(BlueprintAppFactory.DataOption(field.Id, "Created Option"));

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Post(Client(actor), Body(field.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var context = NewContext();

        Assert.Equal(2, await context.DataOptions.CountAsync(x => x.OptionName == "Created Option", Ct));
    }

    /// <remarks>
    /// <c>CreateAsync</c> lines 132-133 read
    /// <code>
    /// var dataFieldEntity = await _context.DataFields.FindAsync(dataOption.DataFieldId);
    /// dataField.ModifiedBy = dataFieldEntity.CreatedBy;
    /// </code>
    /// and <c>FindAsync</c> returns the instance already in the change tracker, so <c>dataFieldEntity</c>
    /// and <c>dataField</c> are the same object and the line means
    /// <c>dataField.ModifiedBy = dataField.CreatedBy</c>. The column's audit trail therefore says its
    /// author last touched it, whoever actually added the option. <c>DeleteAsync</c> line 207 does the
    /// same job correctly with <c>_user.GetId()</c>; matching it turns this test red.
    /// </remarks>
    [Fact]
    public async Task Create_MarksTheFieldModifiedByItsOwnCreatorRatherThanTheCaller()
    {
        var author = Guid.NewGuid();

        var field = BlueprintAppFactory.DataField(createdBy: author);
        await Seed(field);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var before = DateTime.UtcNow;

        Assert.Equal(HttpStatusCode.Created, (await Post(Client(actor), Body(field.Id))).StatusCode);

        await using var context = NewContext();
        var stored = await context.DataFields.SingleAsync(Ct);

        Assert.Equal(author, stored.ModifiedBy);
        Assert.NotEqual(actor.Id, stored.ModifiedBy);
        AssertStampedBetween(stored.DateModified, before, DateTime.UtcNow);
    }

    /// <remarks>
    /// Every other write in the graph calls <c>ServiceUtilities.SetMselModifiedAsync</c> so the MSEL's own
    /// <c>DateModified</c> tracks changes anywhere beneath it; none of the three data-option writes does,
    /// and none of them opens a transaction either. Adding the call turns this test red.
    /// </remarks>
    [Fact]
    public async Task Create_DoesNotMarkTheMselModified()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        Assert.Equal(HttpStatusCode.Created, (await Post(Client(actor), Body(field.Id))).StatusCode);

        await using var context = NewContext();
        var stored = await context.Msels.SingleAsync(Ct);

        Assert.Null(stored.ModifiedBy);
        Assert.Null(stored.DateModified);
    }

    /// <remarks>
    /// The option itself is never broadcast - there is no <c>DataOptionHandler</c> - so the client learns
    /// about it only because the parent field was marked modified on the second save, and
    /// <c>DataFieldHandler.HandleCreateOrUpdate</c> re-reads that field with its options included.
    /// </remarks>
    [Fact]
    public async Task Create_NotifiesTheMselGroupThatTheFieldChanged()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Post(Client(actor), Body(field.Id));

        Assert.Equal(
            [msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.DataFieldUpdated));

        var send = Hub.Of(MainHubMethods.DataFieldUpdated)
            .Single(x => x.Group == msel.Id.ToString());

        Assert.Equal(
            "Created Option",
            Assert.Single(Assert.IsType<DataField>(send.Payload).DataOptions).OptionName);
    }

    [Theory]
    [InlineData(MselRole.Owner, HttpStatusCode.Created)]
    [InlineData(MselRole.Editor, HttpStatusCode.Created)]
    [InlineData(MselRole.Approver, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.MoveEditor, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Viewer, HttpStatusCode.Forbidden)]
    public async Task Create_OnAMselsField_AllowsOnlyTheOwnerAndTheEditor(
        MselRole role, HttpStatusCode expected)
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);

        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        Assert.Equal(expected, (await Post(Client(actor), Body(field.Id))).StatusCode);
    }

    [Fact]
    public async Task Create_OnAMselsField_WithEditMsels_Is201()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);

        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        Assert.Equal(HttpStatusCode.Created, (await Post(Client(actor), Body(field.Id))).StatusCode);
    }

    /// <remarks>
    /// The two branches of <c>CreateAsync</c>'s permission check do not overlap, which is the one place in
    /// this graph where the MSEL and template permissions are cleanly separated - unlike
    /// <c>DataFieldService</c>, where the branch is chosen from the request body and either permission can
    /// be made to answer for the other. Both directions are asserted so a change that merges them fails.
    /// </remarks>
    [Fact]
    public async Task Create_OnAMselsField_WithManageDataFieldsOnly_Is403()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await Post(Client(actor), Body(field.Id))).StatusCode);
    }

    [Fact]
    public async Task Create_OnATemplateField_ForAMselOwner_Is403()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField();
        await Seed(field);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await Post(Client(actor), Body(field.Id))).StatusCode);
    }

    [Fact]
    public async Task Create_OnAnInjectTypesField_WithManageDataFields_Is201()
    {
        var injectType = BlueprintAppFactory.InjectType();
        await Seed(injectType);

        var field = BlueprintAppFactory.DataField(injectTypeId: injectType.Id);
        await Seed(field);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        Assert.Equal(HttpStatusCode.Created, (await Post(Client(actor), Body(field.Id))).StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT dataOptions/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_ReplacesTheStoredOption()
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);

        var option = BlueprintAppFactory.DataOption(field.Id, "before");
        await Seed(option);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Put(
            Client(actor),
            option.Id,
            BodyFor(option) with { OptionName = "after", OptionValue = "AFTER", DisplayOrder = 7 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = await Read<DataOption>(response);

        Assert.Equal("after", updated.OptionName);
        Assert.Equal("AFTER", updated.OptionValue);
        Assert.Equal(7, updated.DisplayOrder);

        await using var context = NewContext();
        var stored = await context.DataOptions.SingleAsync(Ct);

        Assert.Equal("after", stored.OptionName);
        Assert.Equal(7, stored.DisplayOrder);
    }

    /// <remarks>
    /// <c>UpdateAsync</c> maps the whole view model over the stored row, so a property the body does not
    /// mention is written as its default rather than left alone. A PATCH-shaped caller loses the
    /// description and resets the ordering.
    /// </remarks>
    [Fact]
    public async Task Update_IsAFullReplaceRatherThanAPatch()
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);

        var option = BlueprintAppFactory.DataOption(field.Id, "before", displayOrder: 4);
        await Seed(option);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        await Put(
            Client(actor),
            option.Id,
            new OptionBody { Id = option.Id, DataFieldId = field.Id, OptionName = "after" });

        await using var context = NewContext();
        var stored = await context.DataOptions.SingleAsync(Ct);

        Assert.Equal("after", stored.OptionName);
        Assert.Null(stored.OptionValue);
        Assert.Null(stored.OptionDescription);
        Assert.Equal(0, stored.DisplayOrder);
    }

    [Fact]
    public async Task Update_StampsTheAuditFieldsAndPreservesCreation()
    {
        var author = Guid.NewGuid();

        var field = BlueprintAppFactory.DataField();
        await Seed(field);

        var option = BlueprintAppFactory.DataOption(field.Id, "before", createdBy: author);
        await Seed(option);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var before = DateTime.UtcNow;

        var updated = await Read<DataOption>(await Put(
            Client(actor),
            option.Id,
            BodyFor(option) with
            {
                OptionName = "after",
                CreatedBy = Guid.NewGuid(),
                DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ModifiedBy = Guid.NewGuid()
            }));

        Assert.Equal(author, updated.CreatedBy);
        Assert.NotEqual(new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc), updated.DateCreated);
        Assert.Equal(actor.Id, updated.ModifiedBy);
        AssertStampedBetween(updated.DateModified, before, DateTime.UtcNow);
    }

    /// <remarks>
    /// <c>DataOptionProfile</c> ignores only <c>DataField</c>, so <c>_mapper.Map(dataOption, dataOptionToUpdate)</c>
    /// writes the body's <c>Id</c> onto the tracked row and EF refuses to modify a key. The exception
    /// surfaces from <c>BlueprintContext.SaveEntries</c> as a 500, where the route ought to answer 400 -
    /// and a body carrying <c>Guid.Empty</c>, which is what a client that never sets the id sends, gets
    /// the same 500. Ignoring <c>Id</c> on the inbound map turns this test red; note it cannot simply be
    /// ignored, because <see cref="Create_HonoursAClientSuppliedId"/> shares the map.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_WithAMismatchedIdInTheBody_Is500(bool empty)
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);

        var option = BlueprintAppFactory.DataOption(field.Id, "before");
        await Seed(option);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Put(
            Client(actor),
            option.Id,
            BodyFor(option) with { Id = empty ? Guid.Empty : Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Update_ForAnUnknownId_Is404()
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var id = Guid.NewGuid();

        var response = await Put(
            Client(actor),
            id,
            Body(field.Id) with { Id = id });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <remarks>
    /// The field is looked up from the body before the option is looked up by route id, so an unknown
    /// <c>dataFieldId</c> is reported instead of an unknown option even when both are wrong.
    /// </remarks>
    [Fact]
    public async Task Update_ForAnUnknownDataFieldInTheBody_Is404()
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);

        var option = BlueprintAppFactory.DataOption(field.Id, "before");
        await Seed(option);

        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Put(
            Client(actor),
            option.Id,
            BodyFor(option) with { DataFieldId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <remarks>
    /// <para>
    /// <strong>Privilege escalation.</strong> <c>UpdateAsync</c> lines 142-158 resolve the data field from
    /// <c>dataOption.DataFieldId</c> - the value in the request body - and check the caller's rights
    /// against <em>that</em> field's MSEL. The stored option's own field is never consulted. So an owner
    /// of any MSEL may PUT any option in the installation, naming one of their own columns, and both pass
    /// the check and take the option with them: the mapper writes the body's <c>DataFieldId</c> onto the
    /// row, so the victim's drop-down loses an entry and the attacker's gains it.
    /// </para>
    /// <para>
    /// This is the third instance of the same defect in the graph, after
    /// <c>OrganizationService.UpdateAsync</c> and <c>ScenarioEventService.UpdateAsync</c>.
    /// <c>DeleteAsync</c> gets it right by reading the stored row, which is what this should do; making
    /// that change turns this test red.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Update_TakesItsPermissionDecisionFromTheRequestBodysDataField()
    {
        var mine = BlueprintAppFactory.Msel();
        var theirs = BlueprintAppFactory.Msel();
        await Seed(mine, theirs);

        var myField = BlueprintAppFactory.DataField(mselId: mine.Id);
        var theirField = BlueprintAppFactory.DataField(mselId: theirs.Id);
        await Seed(myField, theirField);

        var theirOption = BlueprintAppFactory.DataOption(theirField.Id, "theirs");
        await Seed(theirOption);

        var actor = await Actor().OnMsel(mine, MselRole.Owner).SeedAsync();

        var response = await Put(
            Client(actor),
            theirOption.Id,
            BodyFor(theirOption) with { DataFieldId = myField.Id, OptionName = "stolen" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = NewContext();
        var stored = await context.DataOptions.SingleAsync(Ct);

        Assert.Equal(myField.Id, stored.DataFieldId);
        Assert.Equal("stolen", stored.OptionName);
    }

    /// <remarks>
    /// The same defect in the other direction: naming a shared template as the body's field satisfies the
    /// <c>ManageDataFields</c> branch, and the option leaves the MSEL for the installation-wide template
    /// list. Compare <c>DataFieldEndpointTests.Update_WithAMselIdInTheBody_MovesASharedTemplateOntoTheCallersMsel</c>,
    /// which is the column-level version of this.
    /// </remarks>
    [Fact]
    public async Task Update_WithATemplateFieldInTheBody_MovesAMselsOptionIntoTheSharedTemplateList()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var mselField = BlueprintAppFactory.DataField(mselId: msel.Id);
        var template = BlueprintAppFactory.DataField();
        await Seed(mselField, template);

        var option = BlueprintAppFactory.DataOption(mselField.Id, "private");
        await Seed(option);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Put(
            Client(actor),
            option.Id,
            BodyFor(option) with { DataFieldId = template.Id });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = NewContext();

        Assert.Equal(template.Id, (await context.DataOptions.SingleAsync(Ct)).DataFieldId);
    }

    [Fact]
    public async Task Update_MovingAMselsOptionOntoATemplateField_AsAMselOwner_Is403()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var mselField = BlueprintAppFactory.DataField(mselId: msel.Id);
        var template = BlueprintAppFactory.DataField();
        await Seed(mselField, template);

        var option = BlueprintAppFactory.DataOption(mselField.Id, "private");
        await Seed(option);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Put(
            Client(actor),
            option.Id,
            BodyFor(option) with { DataFieldId = template.Id });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// <c>UpdateAsync</c> lines 170-171 fetch the field a second time and assign
    /// <c>dataField.ModifiedBy = dataFieldEntity.ModifiedBy</c>, which is a self-assignment because
    /// <c>FindAsync</c> hands back the tracked instance. Nothing about the field changes, so
    /// <c>SaveEntries</c> sees no modified entry, no <c>DateModified</c> is stamped, and - see
    /// <see cref="Update_BroadcastsNothing"/> - no entity event is raised. Replacing the line with
    /// <c>_user.GetId()</c> turns both tests red.
    /// </remarks>
    [Fact]
    public async Task Update_DoesNotMarkTheDataFieldModified()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);

        var option = BlueprintAppFactory.DataOption(field.Id, "before");
        await Seed(option);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Put(Client(actor), option.Id, BodyFor(option) with { OptionName = "after" });

        await using var context = NewContext();
        var stored = await context.DataFields.SingleAsync(Ct);

        Assert.Null(stored.ModifiedBy);
        Assert.Null(stored.DateModified);
    }

    /// <remarks>
    /// The consequence of the line above, and the reason it matters: renaming or reordering a drop-down
    /// option reaches no connected client, because the option has no handler of its own and its field was
    /// never marked modified. Every open MSEL editor keeps showing the old label until something else
    /// touches the column.
    /// </remarks>
    [Fact]
    public async Task Update_BroadcastsNothing()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);

        var option = BlueprintAppFactory.DataOption(field.Id, "before");
        await Seed(option);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        Assert.Equal(
            HttpStatusCode.OK,
            (await Put(Client(actor), option.Id, BodyFor(option) with { OptionName = "after" }))
                .StatusCode);

        Assert.Empty(Hub.Sends);
    }

    [Theory]
    [InlineData(MselRole.Owner, HttpStatusCode.OK)]
    [InlineData(MselRole.Editor, HttpStatusCode.OK)]
    [InlineData(MselRole.Approver, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.MoveEditor, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Viewer, HttpStatusCode.Forbidden)]
    public async Task Update_OnAMselsField_AllowsOnlyTheOwnerAndTheEditor(
        MselRole role, HttpStatusCode expected)
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);

        var option = BlueprintAppFactory.DataOption(field.Id, "before");
        await Seed(option);

        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Put(Client(actor), option.Id, BodyFor(option) with { OptionName = "after" });

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Update_OnAMselsField_WithManageDataFieldsOnly_Is403()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);

        var option = BlueprintAppFactory.DataOption(field.Id, "before");
        await Seed(option);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Put(Client(actor), option.Id, BodyFor(option) with { OptionName = "after" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE dataOptions/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_RemovesTheOptionAndAnswersNoContent()
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);

        var option = BlueprintAppFactory.DataOption(field.Id, "doomed");
        var survivor = BlueprintAppFactory.DataOption(field.Id, "survivor");
        await Seed(option, survivor);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/dataOptions/{option.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync(Ct));

        await using var context = NewContext();

        Assert.Equal(survivor.Id, (await context.DataOptions.SingleAsync(Ct)).Id);
    }

    [Fact]
    public async Task Delete_ForAnUnknownId_Is404()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/dataOptions/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <remarks>
    /// The one write of the three that records the caller. <c>DeleteAsync</c> line 207 uses
    /// <c>_user.GetId()</c>; contrast <see cref="Create_MarksTheFieldModifiedByItsOwnCreatorRatherThanTheCaller"/>
    /// and <see cref="Update_DoesNotMarkTheDataFieldModified"/>. Line 206 fetches the field a second time
    /// into a variable nothing reads, which is the vestige of whatever produced the other two.
    /// </remarks>
    [Fact]
    public async Task Delete_MarksTheDataFieldModifiedByTheCaller()
    {
        var field = BlueprintAppFactory.DataField(createdBy: Guid.NewGuid());
        await Seed(field);

        var option = BlueprintAppFactory.DataOption(field.Id, "doomed");
        await Seed(option);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var before = DateTime.UtcNow;

        await Client(actor).DeleteAsync($"/api/dataOptions/{option.Id}", Ct);

        await using var context = NewContext();
        var stored = await context.DataFields.SingleAsync(Ct);

        Assert.Equal(actor.Id, stored.ModifiedBy);
        AssertStampedBetween(stored.DateModified, before, DateTime.UtcNow);
    }

    [Fact]
    public async Task Delete_NotifiesTheMselGroupThatTheFieldChanged()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);

        var option = BlueprintAppFactory.DataOption(field.Id, "doomed");
        await Seed(option);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Client(actor).DeleteAsync($"/api/dataOptions/{option.Id}", Ct);

        Assert.Equal(
            [msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.DataFieldUpdated));

        var send = Hub.Of(MainHubMethods.DataFieldUpdated)
            .Single(x => x.Group == msel.Id.ToString());

        Assert.Empty(Assert.IsType<DataField>(send.Payload).DataOptions);
    }

    /// <remarks>
    /// <c>DeleteAsync</c> lines 185-188 resolve the field from the <em>stored</em> option, so unlike
    /// <see cref="Update_TakesItsPermissionDecisionFromTheRequestBodysDataField"/> there is nothing to
    /// spoof - a DELETE has no body. Both theory cases assert the same thing from opposite ends: the
    /// permission that answers is the one the stored option's field requires, not one the caller happens
    /// to hold for the other kind of field.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Delete_ChecksTheStoredOptionsOwnField(bool onAMsel)
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = onAMsel
            ? BlueprintAppFactory.DataField(mselId: msel.Id)
            : BlueprintAppFactory.DataField();
        await Seed(field);

        var option = BlueprintAppFactory.DataOption(field.Id, "doomed");
        await Seed(option);

        // The permission that would answer for the *other* kind of field, and only that one.
        var actor = onAMsel
            ? await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync()
            : await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/dataOptions/{option.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(MselRole.Owner, HttpStatusCode.NoContent)]
    [InlineData(MselRole.Editor, HttpStatusCode.NoContent)]
    [InlineData(MselRole.Approver, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.MoveEditor, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Viewer, HttpStatusCode.Forbidden)]
    public async Task Delete_OnAMselsField_AllowsOnlyTheOwnerAndTheEditor(
        MselRole role, HttpStatusCode expected)
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);

        var option = BlueprintAppFactory.DataOption(field.Id, "doomed");
        await Seed(option);

        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/dataOptions/{option.Id}", Ct);

        Assert.Equal(expected, response.StatusCode);
    }

    /// <remarks>
    /// There is no <c>IEntityTypeConfiguration</c> for <c>DataOptionEntity</c>, so the relationship to
    /// <c>DataFieldEntity</c> is built by convention: the required navigation gives it
    /// <c>DeleteBehavior.Cascade</c>. Deleting a column therefore takes its whole vocabulary with it,
    /// which is what <c>DataFieldEndpointTests</c> relies on and worth pinning from this side too.
    /// </remarks>
    [Fact]
    public async Task DeletingTheDataField_CascadesToItsOptions()
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);
        await Seed(
            BlueprintAppFactory.DataOption(field.Id, "red"),
            BlueprintAppFactory.DataOption(field.Id, "green"));

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/dataFields/{field.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = NewContext();

        Assert.Empty(await context.DataOptions.ToListAsync(Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // POST datafields/{dataFieldId}/options/preview - the route
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// <para>
    /// The controller checks <c>file == null || file.Length == 0</c> itself and answers 400 with the bare
    /// string <c>"No file provided."</c>, which is the right code and an <em>undeclared</em> one - the action
    /// carries only <c>[ProducesResponseType(typeof(DataOptionImportPreview), 200)]</c>, so a generated
    /// client has no 400 to handle, and the payload is not the <c>ApiError</c> shape every other error in
    /// the API uses. Unlike <c>FileForm.ToUpload</c> elsewhere the parameter carries no <c>[Required]</c>,
    /// so this is the controller's own guard rather than <c>ValidateModelStateFilter</c>'s.
    /// </para>
    /// <para>
    /// The request has to carry a well-formed multipart body holding some <em>other</em> part. A
    /// <c>MultipartFormDataContent</c> with no parts at all is a 400 too, but a different one - the form
    /// reader rejects the body before model binding, with
    /// <c>"Failed to read the request form. Form section has invalid Content-Disposition value: "</c> - and
    /// asserting on the status alone cannot tell the two apart.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Preview_WithNoFilePart_Is400()
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent([1, 2, 3]), "notTheFile", "options.json");

        var response = await Client(actor).PostAsync(
            $"/api/datafields/{field.Id}/options/preview", content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("No file provided.", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Preview_ForAnUnknownDataField_Is404()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Preview(Client(actor), Guid.NewGuid(), "options.json", "[{\"id\":\"a\"}]");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <remarks>
    /// The extension dispatch falls through to <c>preview.Error</c> rather than a status code, so an
    /// unsupported file is a 200 whose body says it failed. Every parse failure below behaves the same
    /// way, which is a defensible design for a preview - the UI has one place to look - but it means no
    /// caller can tell success from failure by status alone.
    /// </remarks>
    [Theory]
    [InlineData("options.txt")]
    [InlineData("options")]
    [InlineData("options.pdf")]
    public async Task Preview_WithAnUnsupportedExtension_Is200WithAnError(string fileName)
    {
        var preview = await Parse(fileName, "[{\"id\":\"a\"}]");

        Assert.Equal("Unsupported file type. Please use JSON, CSV, or XLSX.", preview.Error);
        Assert.Empty(preview.Items);
    }

    [Fact]
    public async Task Preview_WritesNothing()
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);
        await Seed(BlueprintAppFactory.DataOption(field.Id, "existing"));

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        await Preview(Client(actor), field.Id, "options.json", "[{\"id\":\"new\"}]");

        await using var context = NewContext();

        Assert.Equal("existing", (await context.DataOptions.SingleAsync(Ct)).OptionName);
        Assert.Null((await context.DataFields.SingleAsync(Ct)).ModifiedBy);
        Assert.Empty(Hub.Sends);
    }

    /// <remarks>
    /// <c>PreviewImportAsync</c> line 231 calls the <em>three</em>-argument
    /// <c>MselViewRequirement.IsMet(userId, mselId, context)</c>, which hardcodes
    /// <c>hasCreateMselsPermission: false</c>. The reads all use the four-argument overload. So a
    /// <c>CreateMsels</c> holder browsing a template MSEL can list every option on this very field - see
    /// <see cref="GetByMsel_OnATemplateMsel_WithCreateMsels_Is200"/> - and cannot preview a file against
    /// it. Passing the flag through turns this test red.
    /// </remarks>
    [Fact]
    public async Task Preview_OnATemplateMselsField_WithCreateMsels_Is403()
    {
        var msel = BlueprintAppFactory.Msel(isTemplate: true);
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);

        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Preview(Client(actor), field.Id, "options.json", "[{\"id\":\"a\"}]");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// <para>
    /// The other half of line 231's problem: it is a <em>view</em> requirement gating an operation whose
    /// create counterpart needs owner or editor. So every reader of an MSEL - a viewer, and even a bare
    /// team member holding no MSEL role at all - can preview an import they have no way to apply, while
    /// <see cref="Create_OnAMselsField_AllowsOnlyTheOwnerAndTheEditor"/> shows the same accounts refused
    /// on the option itself.
    /// </para>
    /// <para>
    /// Both theory cases turn red together when the check is aligned with <c>CreateAsync</c>'s.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Preview_OnAMselsField_IsAllowedToAnybodyWhoCanReadTheMsel(bool viaATeam)
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);

        var actor = viaATeam
            ? await Actor().OnTeam(team).SeedAsync()
            : await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var response = await Preview(Client(actor), field.Id, "options.json", "[{\"id\":\"a\"}]");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("a", Assert.Single((await Read<DataOptionImportPreview>(response)).Items).OptionName);
    }

    [Fact]
    public async Task Preview_OnAMselsField_WithManageDataFieldsOnly_Is403()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Preview(Client(actor), field.Id, "options.json", "[{\"id\":\"a\"}]");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Preview_OnATemplateField_ForAMselOwner_Is403()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField();
        await Seed(field);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Preview(Client(actor), field.Id, "options.json", "[{\"id\":\"a\"}]");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Preview_OnAFieldWithNoMsel_WithManageDataFields_Is200(bool template)
    {
        var injectType = BlueprintAppFactory.InjectType();
        await Seed(injectType);

        var field = template
            ? BlueprintAppFactory.DataField()
            : BlueprintAppFactory.DataField(injectTypeId: injectType.Id);
        await Seed(field);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Preview(Client(actor), field.Id, "options.json", "[{\"id\":\"a\"}]");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // ParseJsonAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task PreviewJson_ReturnsAnItemPerObjectInTheArray()
    {
        var preview = await Parse(
            "options.json",
            """
            [
              { "id": "T0001", "name": "Do a thing", "description": "At length" },
              { "id": "T0002", "name": "Do another", "description": "Briefly" }
            ]
            """);

        Assert.Null(preview.Error);
        Assert.Equal(["T0001", "T0002"], preview.Items.Select(x => x.OptionName));
        Assert.Equal(["Do a thing", "Do another"], preview.Items.Select(x => x.OptionValue));
        Assert.Equal(["At length", "Briefly"], preview.Items.Select(x => x.OptionDescription));
        Assert.All(preview.Items, x => Assert.False(x.Exists));
    }

    [Fact]
    public async Task PreviewJson_FlagsAnOptionTheFieldAlreadyHas()
    {
        var preview = await Parse(
            "options.json",
            """[{ "id": "T0001" }, { "id": "T0002" }]""",
            "t0001");

        Assert.True(preview.Items[0].Exists);
        Assert.False(preview.Items[1].Exists);
    }

    /// <remarks>
    /// Both sides of the comparison are lowercased, so a file whose casing differs from the stored option
    /// is correctly reported as an existing entry rather than a new one.
    /// </remarks>
    [Fact]
    public async Task PreviewJson_MatchesAnExistingOptionCaseInsensitively()
    {
        var preview = await Parse("options.json", """[{ "id": "T0001" }]""", "T0001");

        Assert.True(Assert.Single(preview.Items).Exists);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("identifier")]
    [InlineData("code")]
    [InlineData("optionName")]
    [InlineData("element_identifier")]
    public async Task PreviewJson_TakesTheOptionNameFromAnyOfFiveKeys(string key)
    {
        var preview = await Parse("options.json", $"[{{ \"{key}\": \"T0001\" }}]");

        Assert.Equal("T0001", Assert.Single(preview.Items).OptionName);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("title")]
    [InlineData("optionValue")]
    [InlineData("value")]
    public async Task PreviewJson_TakesTheOptionValueFromAnyOfFourKeys(string key)
    {
        var preview = await Parse("options.json", $"[{{ \"id\": \"T0001\", \"{key}\": \"Named\" }}]");

        Assert.Equal("Named", Assert.Single(preview.Items).OptionValue);
    }

    [Theory]
    [InlineData("description")]
    [InlineData("text")]
    [InlineData("optionDescription")]
    public async Task PreviewJson_TakesTheDescriptionFromAnyOfThreeKeys(string key)
    {
        var preview = await Parse("options.json", $"[{{ \"id\": \"T0001\", \"{key}\": \"Described\" }}]");

        Assert.Equal("Described", Assert.Single(preview.Items).OptionDescription);
    }

    [Fact]
    public async Task PreviewJson_DefaultsTheValueAndDescriptionToEmptyStrings()
    {
        var preview = await Parse("options.json", """[{ "id": "T0001" }]""");

        var item = Assert.Single(preview.Items);

        Assert.Equal("", item.OptionValue);
        Assert.Equal("", item.OptionDescription);
    }

    /// <remarks>
    /// <c>ExtractFields</c> tries its candidate keys through <c>JsonElement.TryGetProperty</c>, which is
    /// case-sensitive, so an export whose header row was upper-cased - the ordinary output of a
    /// spreadsheet saved as JSON - parses as nothing at all and is reported as a badly-shaped file rather
    /// than a casing problem. Passing <c>StringComparison.OrdinalIgnoreCase</c> is not available on
    /// <c>TryGetProperty</c>; a fix means enumerating the object, and turns this test red.
    /// </remarks>
    [Theory]
    [InlineData("ID")]
    [InlineData("Id")]
    [InlineData("CODE")]
    public async Task PreviewJson_IsCaseSensitiveAboutItsKeys(string key)
    {
        var preview = await Parse("options.json", $"[{{ \"{key}\": \"T0001\" }}]");

        Assert.Equal(
            "No options found. Expected an array of objects with ID and name fields.",
            preview.Error);
        Assert.Empty(preview.Items);
    }

    /// <remarks>
    /// <c>TryGetProperty</c> is followed by an unconditional <c>GetString()</c>, which throws for any
    /// other value kind. A numeric id - what <c>JSON.stringify</c> produces from a spreadsheet's numeric
    /// column - is therefore reported through the outer <c>catch (Exception ex)</c> as
    /// <c>"Error parsing file: ..."</c> with System.Text.Json's own wording, which says nothing about
    /// which row or column was at fault.
    /// </remarks>
    [Fact]
    public async Task PreviewJson_WithANumericId_IsAnErrorNamingTheValueKind()
    {
        var preview = await Parse("options.json", """[{ "id": 1 }]""");

        Assert.Equal(
            "Error parsing file: The requested operation requires an element of type 'String', " +
            "but the target element has type 'Number'.",
            preview.Error);
    }

    [Fact]
    public async Task PreviewJson_SkipsAnArrayElementThatIsNotAnObject()
    {
        var preview = await Parse("options.json", """["bare", 5, { "id": "T0001" }]""");

        Assert.Equal("T0001", Assert.Single(preview.Items).OptionName);
    }

    [Fact]
    public async Task PreviewJson_SkipsAWhitespaceOnlyOptionName()
    {
        var preview = await Parse("options.json", """[{ "id": "   " }, { "id": "T0001" }]""");

        Assert.Equal("T0001", Assert.Single(preview.Items).OptionName);
    }

    /// <remarks>
    /// The preview reports duplicates within one file as two separate new options, so a caller cannot see
    /// from it that applying the import would create the ambiguity
    /// <see cref="Create_WithADuplicateOptionName_Succeeds"/> permits.
    /// </remarks>
    [Fact]
    public async Task PreviewJson_KeepsDuplicatesWithinOneFile()
    {
        var preview = await Parse("options.json", """[{ "id": "T0001" }, { "id": "T0001" }]""");

        Assert.Equal(["T0001", "T0001"], preview.Items.Select(x => x.OptionName));
    }

    [Fact]
    public async Task PreviewJson_WithAnEmptyArray_IsAnError()
    {
        var preview = await Parse("options.json", "[]");

        Assert.Equal(
            "No options found. Expected an array of objects with ID and name fields.",
            preview.Error);
    }

    [Fact]
    public async Task PreviewJson_ReadsANestedNiceFrameworkDocument()
    {
        var preview = await Parse(
            "nice.json",
            """
            {
              "response": {
                "elements": {
                  "elements": [
                    { "element_identifier": "T0001", "title": "Acquire", "text": "Acquire a thing" }
                  ]
                }
              }
            }
            """);

        var item = Assert.Single(preview.Items);

        Assert.Equal("T0001", item.OptionName);
        Assert.Equal("Acquire", item.OptionValue);
        Assert.Equal("Acquire a thing", item.OptionDescription);
    }

    [Fact]
    public async Task PreviewJson_ReadsAFlatNiceFrameworkDocument()
    {
        var preview = await Parse(
            "nice.json",
            """
            { "elements": [{ "element_identifier": "T0001", "title": "Acquire" }] }
            """);

        Assert.Equal("T0001", Assert.Single(preview.Items).OptionName);
    }

    [Theory]
    [InlineData("sort")]
    [InlineData("opm_code")]
    public async Task PreviewJson_SkipsTheNiceElementTypesThatAreNotOptions(string elementType)
    {
        var preview = await Parse(
            "nice.json",
            $$"""
            {
              "elements": [
                { "element_identifier": "SKIP", "element_type": "{{elementType}}" },
                { "element_identifier": "T0001", "element_type": "Task" }
              ]
            }
            """);

        Assert.Equal("T0001", Assert.Single(preview.Items).OptionName);
    }

    /// <remarks>
    /// The NICE branches read <c>element_identifier</c> directly rather than through
    /// <c>ExtractFields</c>, so an element without one is dropped silently - no error, no item, nothing
    /// in the preview to say a row was skipped.
    /// </remarks>
    [Fact]
    public async Task PreviewJson_SkipsANiceElementWithNoIdentifier()
    {
        var preview = await Parse(
            "nice.json",
            """{ "elements": [{ "title": "Nameless" }, { "element_identifier": "T0001" }] }""");

        Assert.Equal("T0001", Assert.Single(preview.Items).OptionName);
    }

    /// <remarks>
    /// The object-with-an-array-property branch <c>break</c>s after the first array property it finds,
    /// whether or not that array yielded anything. So a document whose first array is metadata - a
    /// <c>"warnings": []</c> that an exporter puts before the payload - is reported as containing no
    /// options at all, though the options are right there in the next property. Moving the
    /// <c>break</c> inside a "did this array produce items" test turns this test red.
    /// </remarks>
    [Fact]
    public async Task PreviewJson_GivesUpAfterTheFirstArrayPropertyEvenWhenItIsEmpty()
    {
        var preview = await Parse(
            "options.json",
            """{ "warnings": [], "items": [{ "id": "T0001" }] }""");

        Assert.Equal(
            "No options found. Expected an array of objects with ID and name fields.",
            preview.Error);
        Assert.Empty(preview.Items);
    }

    [Fact]
    public async Task PreviewJson_ReadsTheFirstArrayPropertyOfAnObject()
    {
        var preview = await Parse("options.json", """{ "items": [{ "id": "T0001" }] }""");

        Assert.Equal("T0001", Assert.Single(preview.Items).OptionName);
    }

    /// <remarks>
    /// The search is one level deep, so an array nested inside another object is not found. Combined with
    /// the <c>break</c> above, only a very particular document shape parses.
    /// </remarks>
    [Fact]
    public async Task PreviewJson_DoesNotLookInsideANestedObject()
    {
        var preview = await Parse("options.json", """{ "wrap": { "items": [{ "id": "T0001" }] } }""");

        Assert.Equal(
            "No options found. Expected an array of objects with ID and name fields.",
            preview.Error);
    }

    /// <remarks>
    /// The first thing <c>ParseJsonAsync</c> does with a document that is not an array is
    /// <c>TryGetProperty("response", ...)</c>, which throws rather than returning false when the document
    /// is a scalar - so a JSON file containing a bare string is reported as a parse error naming the
    /// wrong operation instead of as a wrongly-shaped document.
    /// </remarks>
    [Fact]
    public async Task PreviewJson_WithAScalarDocument_IsAnErrorNamingTheValueKind()
    {
        var preview = await Parse("options.json", "\"just a string\"");

        Assert.Equal(
            "Error parsing file: The requested operation requires an element of type 'Object', " +
            "but the target element has type 'String'.",
            preview.Error);
    }

    [Fact]
    public async Task PreviewJson_WithMalformedJson_IsAnError()
    {
        var preview = await Parse("options.json", "not json at all");

        Assert.StartsWith("Error parsing file:", preview.Error);
        Assert.Empty(preview.Items);
    }

    // ---------------------------------------------------------------------------------------------
    // ParseCsvAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task PreviewCsv_ReturnsAnItemPerDataRow()
    {
        var preview = await Parse(
            "options.csv",
            "id,name,description\nT0001,Acquire,At length\nT0002,Assess,Briefly\n",
            "t0002");

        Assert.Null(preview.Error);
        Assert.Equal(["T0001", "T0002"], preview.Items.Select(x => x.OptionName));
        Assert.Equal(["Acquire", "Assess"], preview.Items.Select(x => x.OptionValue));
        Assert.Equal(["At length", "Briefly"], preview.Items.Select(x => x.OptionDescription));
        Assert.Equal([false, true], preview.Items.Select(x => x.Exists));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("identifier")]
    [InlineData("code")]
    [InlineData("optionname")]
    public async Task PreviewCsv_TakesTheOptionNameFromAnyOfFourHeaders(string header)
    {
        var preview = await Parse("options.csv", $"{header},name\nT0001,Acquire\n");

        Assert.Equal("T0001", Assert.Single(preview.Items).OptionName);
    }

    /// <remarks>
    /// <para>
    /// The headers are lowercased and trimmed before matching, so the spacing and casing a spreadsheet
    /// export puts in its header row do not matter. The <em>values</em> are only trimmed.
    /// </para>
    /// <para>
    /// The columns are deliberately out of the order the positional fallback assumes. With them in
    /// <c>id,name,description</c> order this test passes even when header matching is broken, because
    /// the fallback then reads columns 0/1/2 and gets the same answer by accident.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task PreviewCsv_MatchesItsHeadersLowercasedAndTrimmed()
    {
        var preview = await Parse(
            "options.csv",
            "  DESCRIPTION  ,  ID  ,  NAME  \n  At length  ,  T0001  ,  Acquire  \n");

        var item = Assert.Single(preview.Items);

        Assert.Equal("T0001", item.OptionName);
        Assert.Equal("Acquire", item.OptionValue);
        Assert.Equal("At length", item.OptionDescription);
    }

    /// <remarks>
    /// The JSON parser accepts <c>value</c> as a name for the option's value; the CSV parser's candidate
    /// list is <c>name|title|optionvalue</c> and does not. So the same export saved in the two formats
    /// produces different previews - the CSV one silently leaving every value empty. Adding
    /// <c>"value"</c> to <c>FindColumn</c>'s candidates turns this test red.
    /// </remarks>
    [Fact]
    public async Task PreviewCsv_DoesNotRecogniseAValueHeaderThoughTheJsonParserDoes()
    {
        var preview = await Parse("options.csv", "id,value\nT0001,Acquire\n");

        Assert.Equal("", Assert.Single(preview.Items).OptionValue);
    }

    /// <remarks>
    /// <para>
    /// The row loop starts at index 1 unconditionally, so a file with no header row loses its first data
    /// row - and says nothing about it. The positional fallback below exists precisely for a file with no
    /// recognizable headers, which makes the two halves of the same feature contradict one another: the
    /// fallback reads columns by position and the loop still treats row one as a header.
    /// </para>
    /// <para>
    /// Deciding whether row one is a header - it is not, if <c>FindColumn</c> matched nothing - and
    /// starting at 0 turns this test red.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task PreviewCsv_WithNoHeaderRow_SilentlyDropsTheFirstRow()
    {
        var preview = await Parse("options.csv", "T0001,Acquire\nT0002,Assess\n");

        Assert.Equal("T0002", Assert.Single(preview.Items).OptionName);
        Assert.Equal("Assess", preview.Items[0].OptionValue);
    }

    [Fact]
    public async Task PreviewCsv_WithOnlyOneLine_IsAnError()
    {
        var preview = await Parse("options.csv", "id,name\n");

        Assert.Equal("CSV file must have a header row and at least one data row.", preview.Error);
    }

    /// <remarks>
    /// Blank lines are dropped <em>before</em> the row count is taken, which is what the second half of
    /// this test pins: a header row followed by nothing but blank lines is the "must have a header row
    /// and at least one data row" error, not the "no valid rows" one it would be if the count saw them.
    /// Without that, trailing newlines - which every editor adds - would make a header-only file look
    /// like a file with data.
    /// </remarks>
    [Fact]
    public async Task PreviewCsv_IgnoresBlankLines()
    {
        var preview = await Parse("options.csv", "id,name\n\nT0001,Acquire\n   \n\nT0002,Assess\n\n");

        Assert.Equal(["T0001", "T0002"], preview.Items.Select(x => x.OptionName));

        var blank = await Parse("options.csv", "id,name\n\n   \n\n");

        Assert.Equal("CSV file must have a header row and at least one data row.", blank.Error);
    }

    [Fact]
    public async Task PreviewCsv_ReadsQuotedFieldsAndEscapedQuotes()
    {
        var preview = await Parse(
            "options.csv",
            "id,name,description\n\"T,0001\",\"say \"\"hi\"\"\",plain\n");

        var item = Assert.Single(preview.Items);

        Assert.Equal("T,0001", item.OptionName);
        Assert.Equal("say \"hi\"", item.OptionValue);
        Assert.Equal("plain", item.OptionDescription);
    }

    [Fact]
    public async Task PreviewCsv_WithAShortRow_LeavesTheLaterColumnsEmpty()
    {
        var preview = await Parse("options.csv", "id,name,description\nT0001\n");

        var item = Assert.Single(preview.Items);

        Assert.Equal("T0001", item.OptionName);
        Assert.Equal("", item.OptionValue);
        Assert.Equal("", item.OptionDescription);
    }

    [Fact]
    public async Task PreviewCsv_SkipsARowWithNoOptionName()
    {
        var preview = await Parse("options.csv", "id,name\n,Nameless\nT0001,Acquire\n");

        Assert.Equal("T0001", Assert.Single(preview.Items).OptionName);
    }

    [Fact]
    public async Task PreviewCsv_WithNoValidRows_IsAnError()
    {
        var preview = await Parse("options.csv", "id,name\n,Nameless\n,AlsoNameless\n");

        Assert.Equal("No valid rows found in CSV.", preview.Error);
    }

    // ---------------------------------------------------------------------------------------------
    // ParseXlsxAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task PreviewXlsx_ReturnsAnItemPerDataRow()
    {
        var preview = await Parse(
            "options.xlsx",
            Workbooks.Build(new Workbooks.Sheet(
                "Options",
                ["ID", "Name", "Description"],
                ["T0001", "Acquire", "At length"],
                ["T0002", "Assess", "Briefly"])),
            "t0002");

        Assert.Null(preview.Error);
        Assert.Equal(["T0001", "T0002"], preview.Items.Select(x => x.OptionName));
        Assert.Equal(["Acquire", "Assess"], preview.Items.Select(x => x.OptionValue));
        Assert.Equal(["At length", "Briefly"], preview.Items.Select(x => x.OptionDescription));
        Assert.Equal([false, true], preview.Items.Select(x => x.Exists));
    }

    /// <remarks>
    /// A workbook Excel itself writes stores its strings in a workbook-level table and its cells as
    /// indexes into it, which <c>GetCellValue</c> resolves down a different branch from the inline
    /// strings every other test here uses.
    /// </remarks>
    [Fact]
    public async Task PreviewXlsx_ReadsASharedStringWorkbook()
    {
        var preview = await Parse(
            "options.xlsx",
            Workbooks.SharedStrings(new Workbooks.Sheet(
                "Options",
                ["ID", "Name"],
                ["T0001", "Acquire"])));

        var item = Assert.Single(preview.Items);

        Assert.Equal("T0001", item.OptionName);
        Assert.Equal("Acquire", item.OptionValue);
    }

    /// <remarks>
    /// Same defect as <see cref="PreviewCsv_WithNoHeaderRow_SilentlyDropsTheFirstRow"/>: the "no headers
    /// found, assume the first three columns" fallback at lines 538-543 coexists with a row loop that
    /// still starts at index 1.
    /// </remarks>
    [Fact]
    public async Task PreviewXlsx_WithNoHeaderRow_SilentlyDropsTheFirstRow()
    {
        var preview = await Parse(
            "options.xlsx",
            Workbooks.Build(new Workbooks.Sheet(
                "Options",
                ["T0001", "Acquire"],
                ["T0002", "Assess"])));

        Assert.Equal("T0002", Assert.Single(preview.Items).OptionName);
    }

    /// <remarks>
    /// <c>workbookPart.WorksheetParts.First()</c> reads one sheet and never says so, so an export whose
    /// options are on a named second sheet - the ordinary shape of a framework release, and what
    /// <c>MselService</c>'s own DCWF importer handles by name - previews as empty.
    /// </remarks>
    [Fact]
    public async Task PreviewXlsx_IgnoresEverySheetButTheFirst()
    {
        var preview = await Parse(
            "options.xlsx",
            Workbooks.Build(
                new Workbooks.Sheet("Cover", ["Read me"]),
                new Workbooks.Sheet("Options", ["ID", "Name"], ["T0001", "Acquire"])));

        Assert.Equal("Spreadsheet must have a header row and at least one data row.", preview.Error);
        Assert.Empty(preview.Items);
    }

    [Fact]
    public async Task PreviewXlsx_WithOnlyAHeaderRow_IsAnError()
    {
        var preview = await Parse(
            "options.xlsx",
            Workbooks.Build(new Workbooks.Sheet("Options", ["ID", "Name"])));

        Assert.Equal("Spreadsheet must have a header row and at least one data row.", preview.Error);
    }

    /// <remarks>
    /// Columns are resolved from each cell's <c>r</c> attribute, which the schema makes optional. Without
    /// it <c>GetColumnIndex</c> returns -1, every cell is discarded by the <c>colIndex &gt;= 0</c> guard,
    /// and the file previews as empty rather than as unreadable. Same fragility
    /// <c>Workbooks.WithoutCellReferences</c> was written for on the DCWF side.
    /// </remarks>
    [Fact]
    public async Task PreviewXlsx_WithoutCellReferences_FindsNothing()
    {
        var preview = await Parse(
            "options.xlsx",
            Workbooks.WithoutCellReferences(new Workbooks.Sheet(
                "Options",
                ["ID", "Name"],
                ["T0001", "Acquire"])));

        Assert.Equal("No options found in spreadsheet.", preview.Error);
    }

    [Fact]
    public async Task PreviewXlsx_SkipsARowWithNoOptionName()
    {
        var preview = await Parse(
            "options.xlsx",
            Workbooks.Build(new Workbooks.Sheet(
                "Options",
                ["ID", "Name"],
                [null, "Nameless"],
                ["T0001", "Acquire"])));

        Assert.Equal("T0001", Assert.Single(preview.Items).OptionName);
    }

    /// <remarks>
    /// The dispatch matches on the extension alone, so a real <c>.xls</c> - a different, binary format
    /// Open XML cannot open - is sent to the XLSX parser and fails there, and an upper-cased
    /// <c>.XLSX</c> is accepted. Both are asserted through a genuine XLSX payload, because it is the
    /// dispatch and not the parser that is under test.
    /// </remarks>
    [Theory]
    [InlineData("options.xls")]
    [InlineData("options.XLSX")]
    [InlineData("options.XlSx")]
    public async Task PreviewXlsx_AcceptsAnyCasingAndTheXlsExtension(string fileName)
    {
        var preview = await Parse(
            fileName,
            Workbooks.Build(new Workbooks.Sheet("Options", ["ID", "Name"], ["T0001", "Acquire"])));

        Assert.Null(preview.Error);
        Assert.Equal("T0001", Assert.Single(preview.Items).OptionName);
    }

    /// <remarks>
    /// <c>ParseXlsxAsync</c> has its own inner <c>try</c>, so a corrupt spreadsheet is reported with
    /// "Failed to parse XLSX file" rather than the outer handler's "Error parsing file". Either way the
    /// message is Open XML's, and it names neither the file nor the sheet.
    /// </remarks>
    [Fact]
    public async Task PreviewXlsx_WithBytesThatAreNotASpreadsheet_IsAnError()
    {
        var preview = await Parse("options.xlsx", Encoding.UTF8.GetBytes("not a spreadsheet"));

        Assert.StartsWith("Failed to parse XLSX file:", preview.Error);
        Assert.Empty(preview.Items);
    }

    // ---------------------------------------------------------------------------------------------
    // Authentication
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "msels/00000000-0000-0000-0000-000000000001/dataOptions")]
    [InlineData("GET", "datafields/00000000-0000-0000-0000-000000000001/dataOptions")]
    [InlineData("GET", "dataOptions/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "dataOptions")]
    [InlineData("PUT", "dataOptions/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "dataOptions/00000000-0000-0000-0000-000000000001")]
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
    /// The preview route is swept separately because it is the only one that has to be asked in
    /// multipart: see <see cref="Preview_WithTheWrongContentType_Is415BeforeItIs401"/>.
    /// </remarks>
    [Fact]
    public async Task Preview_RefusesAnUnauthenticatedRequest()
    {
        var response = await Preview(
            AnonymousClient,
            Guid.NewGuid(),
            "options.json",
            "[{\"id\":\"a\"}]");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <remarks>
    /// <para>
    /// <c>previewDataOptionImport</c> is the only upload in the API that takes a bare
    /// <c>IFormFile</c> rather than a <c>FileForm</c> model, and that difference is load-bearing in a way
    /// nothing about the source suggests: MVC's <c>InferParameterBindingInfoConvention</c> adds an
    /// implicit <c>[Consumes("multipart/form-data")]</c> to an action with a <c>BindingSource.FormFile</c>
    /// parameter, and not to one whose form data arrives as a complex type. That attribute is enforced by
    /// <c>ConsumesMatcherPolicy</c> during <em>endpoint selection</em>, which runs before
    /// <c>AuthorizationMiddleware</c> - so an anonymous caller sending the wrong content type is told 415
    /// and never asked for a token.
    /// </para>
    /// <para>
    /// The same request against <c>POST dataFields/json</c>, whose parameter is a <c>FileForm</c>, is a
    /// 401. Both halves are asserted here so the asymmetry is on one screen. Nothing is exposed by the
    /// 415, so this is a footnote rather than a finding - but it is the reason the sweep above cannot
    /// include the route, and giving the action a <c>FileForm</c> turns this test red.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Preview_WithTheWrongContentType_Is415BeforeItIs401()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/datafields/{Guid.NewGuid()}/options/preview")
        {
            Content = JsonContent.Create(new { })
        };

        var response = await AnonymousClient.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);

        using var toAFileForm = new HttpRequestMessage(HttpMethod.Post, "/api/dataFields/json")
        {
            Content = JsonContent.Create(new { })
        };

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await AnonymousClient.SendAsync(toAFileForm, Ct)).StatusCode);
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The wire shape of a data option. A record rather than an anonymous type so a test can vary one
    /// property with a <c>with</c> expression, and so the properties a test never mentions are always
    /// sent the same way.
    /// </summary>
    private sealed record OptionBody
    {
        public Guid Id { get; init; }
        public Guid DataFieldId { get; init; }
        public string OptionName { get; init; }
        public string OptionValue { get; init; }
        public string OptionDescription { get; init; }
        public int DisplayOrder { get; init; }

        // Non-nullable on ViewModels.Base, so these two have to be sent as values rather than nulls:
        // System.Text.Json rejects a null for a Guid or a DateTime and the request never reaches the
        // controller.
        public Guid CreatedBy { get; init; }
        public DateTime DateCreated { get; init; }

        public Guid? ModifiedBy { get; init; }
        public DateTime? DateModified { get; init; }
    }

    private static OptionBody Body(Guid dataFieldId) => new()
    {
        DataFieldId = dataFieldId,
        OptionName = "Created Option",
        OptionValue = "created",
        OptionDescription = "Created by a test",
        DisplayOrder = 1
    };

    /// <summary>
    /// The body that echoes a stored option back unchanged, for a test to vary one property of with a
    /// <c>with</c> expression.
    /// </summary>
    /// <remarks>
    /// It carries the stored <c>Id</c>, which is load-bearing rather than tidy:
    /// <see cref="Update_WithAMismatchedIdInTheBody_Is500"/> shows that an update whose body id does not
    /// match the route is a 500, so every other PUT test has to send it.
    /// </remarks>
    private static OptionBody BodyFor(DataOptionEntity option) => new()
    {
        Id = option.Id,
        DataFieldId = option.DataFieldId,
        OptionName = option.OptionName,
        OptionValue = option.OptionValue,
        OptionDescription = option.OptionDescription,
        DisplayOrder = option.DisplayOrder
    };

    private Task<HttpResponseMessage> Post(HttpClient client, OptionBody body) =>
        client.PostAsJsonAsync("/api/dataOptions", body, Ct);

    private Task<HttpResponseMessage> Put(HttpClient client, Guid id, OptionBody body) =>
        client.PutAsJsonAsync($"/api/dataOptions/{id}", body, Ct);

    private async Task<List<DataOption>> GetOptions(HttpClient client, string route)
    {
        var response = await client.GetAsync(route, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<List<DataOption>>(response);
    }

    private async Task<DataOption> GetOption(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/dataOptions/{id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<DataOption>(response);
    }

    /// <remarks>
    /// The <c>await</c> before the <c>using</c> falls out of scope is load-bearing: <c>TestServer</c>
    /// reads the request body inside <c>SendAsync</c>, so returning the task unawaited disposes the
    /// content first and every upload test fails with <c>ObjectDisposedException</c> rather than whatever
    /// it was asserting.
    /// </remarks>
    private async Task<HttpResponseMessage> Preview(
        HttpClient client, Guid dataFieldId, string fileName, byte[] bytes)
    {
        using var content = new MultipartFormDataContent();

        // The action's parameter is a bare IFormFile named "file", so that is the part name.
        content.Add(new ByteArrayContent(bytes), "file", fileName);

        return await client.PostAsync(
            $"/api/datafields/{dataFieldId}/options/preview", content, Ct);
    }

    private Task<HttpResponseMessage> Preview(
        HttpClient client, Guid dataFieldId, string fileName, string text) =>
        Preview(client, dataFieldId, fileName, Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Previews a file against a freshly seeded template data field as an administrator, and returns the
    /// parsed preview. The parser tests care about nothing else, so the whole arrangement is one call.
    /// </summary>
    /// <param name="existingOptionNames">
    /// Options the field already has, which is what the preview's <c>Exists</c> flag is computed against.
    /// </param>
    private Task<DataOptionImportPreview> Parse(
        string fileName, string text, params string[] existingOptionNames) =>
        Parse(fileName, Encoding.UTF8.GetBytes(text), existingOptionNames);

    private async Task<DataOptionImportPreview> Parse(
        string fileName, byte[] bytes, params string[] existingOptionNames)
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);

        foreach (var name in existingOptionNames)
            await Seed(BlueprintAppFactory.DataOption(field.Id, name));

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Preview(Client(actor), field.Id, fileName, bytes);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<DataOptionImportPreview>(response);
    }

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
