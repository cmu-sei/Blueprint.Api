// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
/// The nine data-field endpoints, driven over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// A data field is a column of an MSEL's scenario-event grid, and the graph underneath it is where a wrong
/// write corrupts an exercise rather than just annoying somebody: creating a field on an MSEL also creates
/// an empty <c>DataValue</c> for every scenario event on it, and deleting one cascades to every value ever
/// entered in that column. A field belongs to an MSEL, or to an inject type, or to neither - in which case
/// it is a template shared by the whole installation. The table enforces the first part of that with a
/// check constraint (<c>msel_id IS NULL OR inject_type_id IS NULL</c>); nothing enforces the second, which
/// is where most of what follows comes from.
/// </para>
/// <para>
/// <strong>The permission branch is chosen from the request body, and both directions are exploitable.</strong>
/// <c>CreateAsync</c> and <c>UpdateAsync</c> ask whether <c>dataField.MselId</c> - the value the caller
/// sent - has a value, and require MSEL rights if it does and <c>ManageDataFields</c> if it does not. So a
/// caller holding only <c>ManageDataFields</c> can PUT an MSEL's field with <c>mselId: null</c> and both
/// pass the check and detach the column from the MSEL
/// (<see cref="Update_WithNoMselIdInTheBody_DetachesAMselsFieldForAnyoneHoldingManageDataFields"/>), and an
/// MSEL editor can PUT a shared template with their own <c>mselId</c> and move the installation's template
/// into their MSEL (<see cref="Update_WithAMselIdInTheBody_MovesASharedTemplateOntoTheCallersMsel"/>).
/// <c>DeleteAsync</c> gets this right by reading the stored row, which is what the other two should do.
/// </para>
/// <para>
/// Two of the four reads check nothing at all. <c>GET dataFields/templates</c> and
/// <c>GET injectTypes/{id}/dataFields</c> ask <c>IBlueprintAuthorizationService</c> nothing, so any
/// signed-in account can read every template in the installation - including, because <c>IsTemplate</c> is
/// mapped straight off the request body, a column somebody created inside their own MSEL and flagged as a
/// template (<see cref="Create_WithIsTemplateOnAMselField_PutsAMselsColumnInTheSharedTemplateList"/>).
/// </para>
/// <para>
/// The rest are smaller and are characterized where they are found: <c>GetByMselAsync</c> answers 500
/// rather than 404 for an MSEL that does not exist, but only for a caller who needs the permission check;
/// <c>GetAsync</c> uses <c>SingleAsync</c> behind a dead null check, so an unknown id is a 500;
/// <c>UploadJsonAsync</c> rebuilds each option by hand and forgets <c>OptionDescription</c>, so a download
/// followed by an upload silently loses every description; and <c>UpdateAsync</c> moving a field onto an
/// MSEL does not create the data values that <c>CreateAsync</c> would, so the column exists with no cells.
/// </para>
/// <para>
/// Per this branch's rule, every test above characterizes rather than fixes, and says what fixing it will
/// do to the test. Ordering and the <c>Reorder</c> helper are in <see cref="DataFieldReorderTests"/>.
/// </para>
/// </remarks>
public class DataFieldEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET dataFields/templates
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Templates_ReturnsOnlyFieldsFlaggedAsTemplates()
    {
        var msel = BlueprintAppFactory.Msel();
        var injectType = BlueprintAppFactory.InjectType();
        await Seed(msel, injectType);

        var template = BlueprintAppFactory.DataField();
        await Seed(
            template,
            BlueprintAppFactory.DataField(mselId: msel.Id),
            BlueprintAppFactory.DataField(injectTypeId: injectType.Id));

        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var returned = await GetFields(Client(actor), "/api/dataFields/templates");

        Assert.Equal(template.Id, Assert.Single(returned).Id);
    }

    [Fact]
    public async Task Templates_WithNoneSeeded_IsAnEmptyArray()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        Assert.Empty(await GetFields(Client(actor), "/api/dataFields/templates"));
    }

    [Fact]
    public async Task Templates_IncludesTheDataOptions()
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);
        await Seed(
            BlueprintAppFactory.DataOption(field.Id, "red"),
            BlueprintAppFactory.DataOption(field.Id, "green"));

        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var returned = Assert.Single(await GetFields(Client(actor), "/api/dataFields/templates"));

        Assert.Equal(
            ["green", "red"],
            returned.DataOptions.Select(x => x.OptionName).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Any authenticated caller may read every template in the installation, including one with no system
    /// role at all.
    /// </summary>
    /// <remarks>
    /// Characterization. <c>DataFieldController.GetDataFieldTemplates</c> asks
    /// <c>IBlueprintAuthorizationService</c> nothing, even though <see cref="SystemPermission.ManageDataFields"/>
    /// exists and gates the upload and download of the same rows. Turns red when a permission check is
    /// added.
    /// </remarks>
    [Fact]
    public async Task Templates_WithNoSystemRole_Is200()
    {
        await Seed(BlueprintAppFactory.DataField());

        var actor = await Actor().SeedAsync();

        Assert.Single(await GetFields(Client(actor), "/api/dataFields/templates"));
    }

    /// <summary>
    /// A column scoped to an MSEL but flagged <c>IsTemplate</c> is returned by the shared template list,
    /// because the query filters on the flag alone and never on the scope.
    /// </summary>
    /// <remarks>
    /// Characterization, and the reason it matters is <see cref="Create_WithIsTemplateOnAMselField_PutsAMselsColumnInTheSharedTemplateList"/>:
    /// a caller can produce this row through the API. Turns red when <c>GetTemplatesAsync</c> also requires
    /// <c>MselId == null</c>.
    /// </remarks>
    [Fact]
    public async Task Templates_IncludesAMselScopedFieldFlaggedAsATemplate()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id, isTemplate: true);
        await Seed(field);

        var actor = await Actor().SeedAsync();

        var returned = Assert.Single(await GetFields(Client(actor), "/api/dataFields/templates"));

        Assert.Equal(msel.Id, returned.MselId);
    }

    // ---------------------------------------------------------------------------------------------
    // GET msels/{mselId}/dataFields
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByMsel_AsAViewer_ReturnsTheMselsFields()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);

        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var returned = await GetFields(Client(actor), $"/api/msels/{msel.Id}/dataFields");

        Assert.Equal(field.Id, Assert.Single(returned).Id);
    }

    [Fact]
    public async Task GetByMsel_WithViewMselsPermission_ReturnsThemWithNoRole()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        await Seed(BlueprintAppFactory.DataField(mselId: msel.Id));

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Single(await GetFields(Client(actor), $"/api/msels/{msel.Id}/dataFields"));
    }

    [Fact]
    public async Task GetByMsel_DoesNotReturnAnotherMselsFields()
    {
        var msel = BlueprintAppFactory.Msel();
        var other = BlueprintAppFactory.Msel();
        await Seed(msel, other);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field, BlueprintAppFactory.DataField(mselId: other.Id));

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var returned = await GetFields(Client(actor), $"/api/msels/{msel.Id}/dataFields");

        Assert.Equal(field.Id, Assert.Single(returned).Id);
    }

    [Fact]
    public async Task GetByMsel_IncludesTheDataOptions()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);
        await Seed(BlueprintAppFactory.DataOption(field.Id, "only"));

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var returned = Assert.Single(await GetFields(Client(actor), $"/api/msels/{msel.Id}/dataFields"));

        Assert.Equal("only", Assert.Single(returned.DataOptions).OptionName);
    }

    [Fact]
    public async Task GetByMsel_WithNoRoleOnTheMsel_Is403()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        await Seed(BlueprintAppFactory.DataField(mselId: msel.Id));

        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/dataFields", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// A template MSEL's columns are readable by anyone: the service catches the failed view requirement
    /// and falls through when <c>msel.IsTemplate</c>.
    /// </summary>
    [Fact]
    public async Task GetByMsel_WithNoRoleOnATemplateMsel_ReturnsThem()
    {
        var msel = BlueprintAppFactory.Msel(isTemplate: true);
        await Seed(msel);
        await Seed(BlueprintAppFactory.DataField(mselId: msel.Id));

        var actor = await Actor().SeedAsync();

        Assert.Single(await GetFields(Client(actor), $"/api/msels/{msel.Id}/dataFields"));
    }

    /// <summary>
    /// An MSEL that does not exist is a 500 rather than a 404.
    /// </summary>
    /// <remarks>
    /// Characterization. The template fall-through loads the MSEL with <c>FindAsync</c> and then reads
    /// <c>msel.IsTemplate</c> without checking for null, so the request dies on a
    /// <c>NullReferenceException</c> inside the branch that was deciding whether to forbid it. Turns red
    /// when the null is handled - as a 403 or a 404, either of which is an answer.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_ForAnUnknownMsel_Is500()
    {
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{Guid.NewGuid()}/dataFields", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// The same unknown MSEL answers an empty array for a caller holding <c>ViewMsels</c>, because the
    /// permission short-circuits the branch that would have dereferenced null.
    /// </summary>
    /// <remarks>
    /// Characterization, and the pair is the point: the same request is a 500 or a 200 depending on the
    /// caller's system role. Turns red with its partner above.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_ForAnUnknownMsel_WithViewMselsPermission_IsAnEmptyArray()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Empty(await GetFields(Client(actor), $"/api/msels/{Guid.NewGuid()}/dataFields"));
    }

    // ---------------------------------------------------------------------------------------------
    // GET injectTypes/{injectTypeId}/dataFields
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByInjectType_ReturnsOnlyThatInjectTypesFields()
    {
        var injectType = BlueprintAppFactory.InjectType();
        var other = BlueprintAppFactory.InjectType();
        await Seed(injectType, other);

        var field = BlueprintAppFactory.DataField(injectTypeId: injectType.Id);
        await Seed(field, BlueprintAppFactory.DataField(injectTypeId: other.Id));

        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var returned = await GetFields(Client(actor), $"/api/injectTypes/{injectType.Id}/dataFields");

        Assert.Equal(field.Id, Assert.Single(returned).Id);
    }

    /// <summary>
    /// An inject type's columns are readable by any authenticated caller.
    /// </summary>
    /// <remarks>
    /// Characterization, the same shape as <see cref="Templates_WithNoSystemRole_Is200"/>:
    /// <c>GetByInjectType</c> asks <c>IBlueprintAuthorizationService</c> nothing. Turns red when a
    /// permission check is added.
    /// </remarks>
    [Fact]
    public async Task GetByInjectType_WithNoSystemRole_Is200()
    {
        var injectType = BlueprintAppFactory.InjectType();
        await Seed(injectType);
        await Seed(BlueprintAppFactory.DataField(injectTypeId: injectType.Id));

        var actor = await Actor().SeedAsync();

        Assert.Single(await GetFields(Client(actor), $"/api/injectTypes/{injectType.Id}/dataFields"));
    }

    [Fact]
    public async Task GetByInjectType_ForAnUnknownInjectType_IsAnEmptyArray()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        Assert.Empty(await GetFields(Client(actor), $"/api/injectTypes/{Guid.NewGuid()}/dataFields"));
    }

    // ---------------------------------------------------------------------------------------------
    // GET dataFields/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ForAMselField_AsAViewer_ReturnsIt()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id, name: "Assigned To");
        await Seed(field);

        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var returned = await GetField(Client(actor), field.Id);

        Assert.Equal("Assigned To", returned.Name);
        Assert.Equal(msel.Id, returned.MselId);
    }

    [Fact]
    public async Task Get_ForAMselField_WithViewMselsPermission_ReturnsIt()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Equal(field.Id, (await GetField(Client(actor), field.Id)).Id);
    }

    [Fact]
    public async Task Get_ForAMselField_WithNoRoleOnTheMsel_Is403()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);

        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync($"/api/dataFields/{field.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// A field with no MSEL is readable by anyone: the service only asks the view requirement when
    /// <c>MselId</c> has a value, which the comment beside it says is deliberate.
    /// </summary>
    [Fact]
    public async Task Get_ForATemplate_WithNoSystemRole_ReturnsIt()
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);
        await Seed(BlueprintAppFactory.DataOption(field.Id, "shared"));

        var actor = await Actor().SeedAsync();

        var returned = await GetField(Client(actor), field.Id);

        Assert.Null(returned.MselId);
        Assert.Equal("shared", Assert.Single(returned.DataOptions).OptionName);
    }

    /// <summary>
    /// An inject type's field is readable by anyone too, for the same reason: only <c>MselId</c> is
    /// consulted.
    /// </summary>
    /// <remarks>
    /// Characterization. <c>InjectTypeId</c> is never checked against anything here or in
    /// <see cref="GetByInjectType_WithNoSystemRole_Is200"/>. Turns red when inject-type fields get a
    /// permission of their own.
    /// </remarks>
    [Fact]
    public async Task Get_ForAnInjectTypesField_WithNoSystemRole_ReturnsIt()
    {
        var injectType = BlueprintAppFactory.InjectType();
        await Seed(injectType);

        var field = BlueprintAppFactory.DataField(injectTypeId: injectType.Id);
        await Seed(field);

        var actor = await Actor().SeedAsync();

        Assert.Equal(injectType.Id, (await GetField(Client(actor), field.Id)).InjectTypeId);
    }

    /// <summary>
    /// An unknown id is a 500 rather than a 404.
    /// </summary>
    /// <remarks>
    /// Characterization, and the same defect <c>OrganizationService.GetAsync</c> has: the query is
    /// <c>SingleAsync</c>, which throws before the <c>item == null</c> check below it can run - and that
    /// check's <c>EntityNotFoundException</c> names <c>DataValueEntity</c>, not <c>DataFieldEntity</c>,
    /// which is how long it has been unreachable. Turns red when the query becomes
    /// <c>SingleOrDefaultAsync</c>.
    /// </remarks>
    [Fact]
    public async Task Get_ForAnUnknownId_Is500()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Client(actor).GetAsync($"/api/dataFields/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // POST dataFields
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_OnAMsel_AsTheOwner_CreatesTheField()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var created = await Read<DataField>(await Post(Client(actor), Body(msel.Id)));

        Assert.Equal("Created Field", created.Name);
        Assert.Equal(msel.Id, created.MselId);
        Assert.Equal(DataFieldType.Html, created.DataType);

        await using var context = NewContext();
        var stored = await context.DataFields.AsNoTracking().SingleAsync(x => x.Id == created.Id, Ct);

        Assert.Equal(msel.Id, stored.MselId);
        Assert.Equal(actor.Id, stored.CreatedBy);
    }

    [Theory]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Owner)]
    public async Task Create_OnAMsel_AsAnEditorOrOwner_CreatesIt(MselRole role)
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.Viewer)]
    [InlineData(MselRole.Evaluator)]
    public async Task Create_OnAMsel_WithAReadOnlyRole_Is403(MselRole role)
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_OnAMsel_WithEditMselsPermission_CreatesIt()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        Assert.Equal(HttpStatusCode.OK, (await Post(Client(actor), Body(msel.Id))).StatusCode);
    }

    /// <summary>
    /// <c>ManageDataFields</c> is not a way into an MSEL's columns: the MSEL branch consults only the MSEL
    /// requirements and the <c>EditMsels</c> permission.
    /// </summary>
    [Fact]
    public async Task Create_OnAMsel_WithManageDataFieldsOnly_Is403()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_ATemplate_WithManageDataFields_CreatesIt()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var created = await Read<DataField>(
            await Post(Client(actor), Body() with { IsTemplate = true }));

        Assert.Null(created.MselId);
        Assert.True(created.IsTemplate);
    }

    /// <summary>
    /// Conversely, <c>EditMsels</c> is not a way into the shared templates.
    /// </summary>
    [Fact]
    public async Task Create_ATemplate_WithEditMselsOnly_Is403()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(Client(actor), Body() with { IsTemplate = true });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// A caller who can edit an MSEL can put a column of it into the installation's shared template list,
    /// by sending <c>isTemplate: true</c> alongside their own <c>mselId</c>.
    /// </summary>
    /// <remarks>
    /// Characterization, and the live half of
    /// <see cref="Templates_IncludesAMselScopedFieldFlaggedAsATemplate"/>: <c>IsTemplate</c> is mapped
    /// straight off the request body, and the branch that decided this caller was allowed only looked at
    /// <c>MselId</c>. Because <c>GET dataFields/templates</c> checks no permission either, the row is then
    /// readable by every account in the installation. Turns red when create forces <c>IsTemplate</c> to
    /// <c>MselId is null</c>, or when the templates query filters on the scope.
    /// </remarks>
    [Fact]
    public async Task Create_WithIsTemplateOnAMselField_PutsAMselsColumnInTheSharedTemplateList()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var created = await Read<DataField>(
            await Post(Client(owner), Body(msel.Id) with { IsTemplate = true }));

        var stranger = await Actor().SeedAsync();

        var templates = await GetFields(Client(stranger), "/api/dataFields/templates");

        Assert.Equal(created.Id, Assert.Single(templates).Id);
    }

    /// <summary>
    /// A field with no MSEL, no inject type and <c>isTemplate: false</c> is created and then appears in no
    /// list at all - only <c>GET dataFields/{id}</c> can reach it.
    /// </summary>
    /// <remarks>
    /// Characterization. The three list endpoints between them filter on <c>IsTemplate</c>, <c>MselId</c>
    /// and <c>InjectTypeId</c>, so this row is invisible to all of them. Turns red when create refuses an
    /// unscoped field that is not a template.
    /// </remarks>
    [Fact]
    public async Task Create_WithNoScopeAndIsTemplateFalse_CreatesAFieldNoListReturns()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var created = await Read<DataField>(await Post(Client(actor), Body()));

        Assert.False(created.IsTemplate);
        Assert.Empty(await GetFields(Client(actor), "/api/dataFields/templates"));
        Assert.Equal(created.Id, (await GetField(Client(actor), created.Id)).Id);
    }

    /// <summary>
    /// A field cannot belong to an MSEL and an inject type at once, and the table is what says so.
    /// </summary>
    /// <remarks>
    /// Characterization. The check constraint <c>data_field_msel_or_inject_type</c> rejects the insert, so
    /// the caller gets a 500 out of <c>DbUpdateException</c> rather than a 400 naming the problem. Turns
    /// red when the service validates the pair.
    /// </remarks>
    [Fact]
    public async Task Create_WithBothAMselAndAnInjectType_Is500()
    {
        var msel = BlueprintAppFactory.Msel();
        var injectType = BlueprintAppFactory.InjectType();
        await Seed(msel, injectType);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id) with { InjectTypeId = injectType.Id });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Create_ForAnUnknownMsel_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(Client(actor), Body(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Create_StampsTheAuditFieldsOnTheServer()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var before = DateTime.UtcNow;
        var created = await Read<DataField>(await Post(
            Client(actor),
            Body(msel.Id) with
            {
                CreatedBy = Guid.NewGuid(),
                DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ModifiedBy = Guid.NewGuid()
            }));

        Assert.Equal(actor.Id, created.CreatedBy);
        AssertStampedBetween(created.DateCreated, before, DateTime.UtcNow);
        Assert.Null(created.DateModified);
        Assert.Null(created.ModifiedBy);
    }

    [Fact]
    public async Task Create_CreatesTheDataOptions()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var created = await Read<DataField>(await Post(
            Client(actor),
            Body() with
            {
                IsTemplate = true,
                DataOptions =
                [
                    new OptionBody { OptionName = "high", OptionValue = "3", DisplayOrder = 1 },
                    new OptionBody { OptionName = "low", OptionValue = "1", DisplayOrder = 2 }
                ]
            }));

        await using var context = NewContext();
        var stored = await context.DataOptions
            .AsNoTracking()
            .Where(x => x.DataFieldId == created.Id)
            .OrderBy(x => x.DisplayOrder)
            .ToListAsync(Ct);

        Assert.Equal(["high", "low"], stored.Select(x => x.OptionName));
        Assert.Equal(["3", "1"], stored.Select(x => x.OptionValue));
        Assert.All(stored, x => Assert.Equal(actor.Id, x.CreatedBy));
    }

    /// <summary>
    /// An id sent with an option is discarded: create always assigns a fresh one.
    /// </summary>
    [Fact]
    public async Task Create_IgnoresTheIdsSentWithTheDataOptions()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();
        var sentId = Guid.NewGuid();

        var created = await Read<DataField>(await Post(
            Client(actor),
            Body() with
            {
                IsTemplate = true,
                DataOptions = [new OptionBody { Id = sentId, OptionName = "only" }]
            }));

        await using var context = NewContext();
        var stored = await context.DataOptions
            .AsNoTracking()
            .SingleAsync(x => x.DataFieldId == created.Id, Ct);

        Assert.NotEqual(sentId, stored.Id);
    }

    /// <summary>
    /// Creating a column on an MSEL creates the cell for it on every scenario event the MSEL already has.
    /// </summary>
    /// <remarks>
    /// This is the reason a data field is not ordinary CRUD: the grid the UI draws is
    /// scenario-events-by-fields, and a missing <c>DataValue</c> is a hole in it.
    /// </remarks>
    [Fact]
    public async Task Create_OnAMsel_AddsADataValueForEveryScenarioEvent()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        var other = BlueprintAppFactory.Msel();
        await Seed(other);
        await Seed(
            BlueprintAppFactory.ScenarioEvent(msel.Id, deltaSeconds: 0),
            BlueprintAppFactory.ScenarioEvent(msel.Id, deltaSeconds: 60),
            BlueprintAppFactory.ScenarioEvent(other.Id));

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var created = await Read<DataField>(await Post(Client(actor), Body(msel.Id)));

        await using var context = NewContext();
        var values = await context.DataValues
            .AsNoTracking()
            .Where(x => x.DataFieldId == created.Id)
            .ToListAsync(Ct);

        Assert.Equal(2, values.Count);
        Assert.All(values, x => Assert.Null(x.Value));
        Assert.All(values, x => Assert.Equal(actor.Id, x.CreatedBy));
    }

    [Fact]
    public async Task Create_ATemplate_AddsNoDataValues()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        await Seed(BlueprintAppFactory.ScenarioEvent(msel.Id));

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        await Post(Client(actor), Body() with { IsTemplate = true });

        await using var context = NewContext();

        Assert.Empty(await context.DataValues.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task Create_OnAMsel_UpdatesTheMselsModifiedInfo()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var before = DateTime.UtcNow;
        await Post(Client(actor), Body(msel.Id));

        await using var context = NewContext();
        var stored = await context.Msels.AsNoTracking().SingleAsync(x => x.Id == msel.Id, Ct);

        Assert.Equal(actor.Id, stored.ModifiedBy);
        AssertStampedBetween(stored.DateModified, before, DateTime.UtcNow);
    }

    [Fact]
    public async Task Create_OnAMsel_NotifiesTheMselGroupAndTheAdminGroup()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var created = await Read<DataField>(await Post(Client(actor), Body(msel.Id)));

        Assert.Equal(
            [msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.DataFieldCreated));

        var sent = Assert.IsType<DataField>(
            Hub.Of(MainHubMethods.DataFieldCreated).First().Payload);

        Assert.Equal(created.Id, sent.Id);
    }

    /// <summary>
    /// A template's creation is broadcast to a group named by the empty string, because the handler builds
    /// the group name from a <c>Guid?</c> that is null.
    /// </summary>
    /// <remarks>
    /// Characterization, and the same defect <c>OrganizationHandler</c> has:
    /// <c>DataFieldHandler.GetGroups</c> calls <c>dataFieldEntity.MselId.ToString()</c>, which is
    /// <see cref="string.Empty"/> for a template. No client is in that group, so the message is merely
    /// wasted rather than misdelivered - but a client that ever joins <c>""</c> would receive every
    /// template change in the installation. Turns red when the handler skips a null MSEL.
    /// </remarks>
    [Fact]
    public async Task Create_ATemplate_BroadcastsToAGroupNamedByTheEmptyString()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        await Post(Client(actor), Body() with { IsTemplate = true });

        Assert.Equal(
            [string.Empty, MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.DataFieldCreated));
    }

    /// <summary>
    /// The create answers 200 with the field in the body and no <c>Location</c> header.
    /// </summary>
    /// <remarks>
    /// Characterization of the contract rather than the behaviour: the action declares
    /// <c>[ProducesResponseType(typeof(DataField), 201)]</c> and returns <c>Ok(result)</c>. The generated
    /// client in blueprint.ui therefore describes a response the API never sends. Belongs on the Phase 4
    /// contract list; turns red when the action returns <c>CreatedAtAction</c>.
    /// </remarks>
    [Fact]
    public async Task Create_Is200WithNoLocationHeader()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Post(Client(actor), Body() with { IsTemplate = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT dataFields/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_OnAMsel_AsTheOwner_ChangesTheField()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id, name: "Before");
        await Seed(field);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var updated = await Read<DataField>(
            await Put(Client(actor), field.Id, BodyFor(field) with { Name = "After" }));

        Assert.Equal("After", updated.Name);

        await using var context = NewContext();

        Assert.Equal(
            "After",
            (await context.DataFields.AsNoTracking().SingleAsync(x => x.Id == field.Id, Ct)).Name);
    }

    [Theory]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.Viewer)]
    public async Task Update_OnAMsel_WithAReadOnlyRole_Is403(MselRole role)
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);

        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await Put(Client(actor), field.Id, BodyFor(field))).StatusCode);
    }

    [Fact]
    public async Task Update_ForAnUnknownId_Is404()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Client(actor).PutAsJsonAsync(
            $"/api/dataFields/{Guid.NewGuid()}", Body(), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The route id decides which row is loaded and the body id is then mapped over it, so a mismatch is a
    /// 500 from EF rather than the 400 the documentation implies.
    /// </summary>
    /// <remarks>
    /// Characterization. The action's own remarks say "The ID from the route MUST MATCH the ID contained
    /// in the dataField parameter", and nothing checks it: the mapping writes the body's id onto a tracked
    /// entity, and EF refuses to modify a key. Turns red when the mismatch is rejected.
    /// </remarks>
    [Fact]
    public async Task Update_WithAMismatchedBodyId_Is500()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).PutAsJsonAsync(
            $"/api/dataFields/{field.Id}",
            Body(msel.Id) with { Id = Guid.NewGuid() },
            Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// A caller holding only <c>ManageDataFields</c> can edit any MSEL's column, and detach it from the
    /// MSEL while doing so, by sending no <c>mselId</c>.
    /// </summary>
    /// <remarks>
    /// Characterization, and the sharper half of the body-driven permission branch.
    /// <c>UpdateAsync</c> asks whether <em>the request body</em> has an <c>MselId</c>; with none it
    /// requires <c>ManageDataFields</c> and nothing else, and then maps the body over the stored row -
    /// which sets <c>MselId</c> to null. The column, its options and every value in it leave the MSEL in
    /// one request by a caller with no rights on it. Turns red when the branch is chosen from the stored
    /// row, as <c>DeleteAsync</c> does.
    /// </remarks>
    [Fact]
    public async Task Update_WithNoMselIdInTheBody_DetachesAMselsFieldForAnyoneHoldingManageDataFields()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id, name: "Assigned To");
        await Seed(field);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Put(
            Client(actor),
            field.Id,
            BodyFor(field) with { MselId = null, Name = "Mine Now" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = NewContext();
        var stored = await context.DataFields.AsNoTracking().SingleAsync(x => x.Id == field.Id, Ct);

        Assert.Null(stored.MselId);
        Assert.Equal("Mine Now", stored.Name);
    }

    /// <summary>
    /// And the other direction: a caller who can edit one MSEL can move the installation's shared template
    /// into it, by sending their own <c>mselId</c> with a template's id in the route.
    /// </summary>
    /// <remarks>
    /// Characterization. The MSEL branch is satisfied by the caller's rights on the MSEL they named, and
    /// the row being edited is never consulted - so the template stops being a template and stops being
    /// available to everybody else. Turns red with its partner above.
    /// </remarks>
    [Fact]
    public async Task Update_WithAMselIdInTheBody_MovesASharedTemplateOntoTheCallersMsel()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var template = BlueprintAppFactory.DataField(name: "Shared Template");
        await Seed(template);

        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var response = await Put(
            Client(actor),
            template.Id,
            BodyFor(template) with { MselId = msel.Id, IsTemplate = false });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = NewContext();
        var stored = await context.DataFields.AsNoTracking().SingleAsync(x => x.Id == template.Id, Ct);

        Assert.Equal(msel.Id, stored.MselId);
        Assert.False(stored.IsTemplate);
    }

    /// <summary>
    /// Moving a field onto an MSEL adds none of the data values that creating it there would, so the
    /// column exists on the grid with no cells behind it.
    /// </summary>
    /// <remarks>
    /// Characterization. <c>CreateAsync</c> calls <c>AddNewDataValues</c>; <c>UpdateAsync</c> has no
    /// equivalent, and neither does anything else - so the values are missing until somebody writes one.
    /// Turns red when update reconciles the values for a field that gained an MSEL.
    /// </remarks>
    [Fact]
    public async Task Update_MovingAFieldOntoAMsel_AddsNoDataValues()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        await Seed(BlueprintAppFactory.ScenarioEvent(msel.Id));

        var template = BlueprintAppFactory.DataField();
        await Seed(template);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Put(
            Client(actor),
            template.Id,
            BodyFor(template) with { MselId = msel.Id, IsTemplate = false });

        await using var context = NewContext();

        Assert.Empty(await context.DataValues.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task Update_PreservesTheCreationAuditAndStampsTheModification()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var creatorId = Guid.NewGuid();
        var field = BlueprintAppFactory.DataField(mselId: msel.Id, createdBy: creatorId);
        await Seed(field);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        // Read back rather than using the seeded object's own DateCreated: a Postgres timestamp holds
        // microseconds where a DateTime holds ticks, so the value in memory is up to 999ns ahead of the
        // one the API will answer with.
        DateTime stampedAtCreation;

        await using (var seeded = NewContext())
        {
            stampedAtCreation = (await seeded.DataFields
                .AsNoTracking()
                .SingleAsync(x => x.Id == field.Id, Ct)).DateCreated;
        }

        var before = DateTime.UtcNow;
        var updated = await Read<DataField>(await Put(
            Client(actor),
            field.Id,
            BodyFor(field) with
            {
                CreatedBy = Guid.NewGuid(),
                DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ModifiedBy = Guid.NewGuid()
            }));

        Assert.Equal(creatorId, updated.CreatedBy);
        Assert.Equal(stampedAtCreation, updated.DateCreated);
        Assert.Equal(actor.Id, updated.ModifiedBy);
        AssertStampedBetween(updated.DateModified, before, DateTime.UtcNow);
    }

    [Fact]
    public async Task Update_OnAMsel_UpdatesTheMselsModifiedInfo()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var before = DateTime.UtcNow;
        await Put(Client(actor), field.Id, BodyFor(field) with { Name = "Touched" });

        await using var context = NewContext();
        var stored = await context.Msels.AsNoTracking().SingleAsync(x => x.Id == msel.Id, Ct);

        Assert.Equal(actor.Id, stored.ModifiedBy);
        AssertStampedBetween(stored.DateModified, before, DateTime.UtcNow);
    }

    [Fact]
    public async Task Update_AddsADataOptionTheBodyIntroduces()
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        await Put(
            Client(actor),
            field.Id,
            BodyFor(field) with
            {
                DataOptions = [new OptionBody { OptionName = "added", DisplayOrder = 1 }]
            });

        await using var context = NewContext();
        var stored = await context.DataOptions
            .AsNoTracking()
            .SingleAsync(x => x.DataFieldId == field.Id, Ct);

        Assert.Equal("added", stored.OptionName);
        Assert.Equal(actor.Id, stored.CreatedBy);
    }

    [Fact]
    public async Task Update_ChangesADataOptionItAlreadyHas()
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);

        var option = BlueprintAppFactory.DataOption(field.Id, "before");
        await Seed(option);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        await Put(
            Client(actor),
            field.Id,
            BodyFor(field) with
            {
                DataOptions =
                [
                    new OptionBody
                    {
                        Id = option.Id,
                        OptionName = "after",
                        OptionValue = "9",
                        DisplayOrder = 4
                    }
                ]
            });

        await using var context = NewContext();
        var stored = await context.DataOptions
            .AsNoTracking()
            .SingleAsync(x => x.DataFieldId == field.Id, Ct);

        Assert.Equal(option.Id, stored.Id);
        Assert.Equal("after", stored.OptionName);
        Assert.Equal("9", stored.OptionValue);
        Assert.Equal(4, stored.DisplayOrder);
        Assert.Equal(actor.Id, stored.ModifiedBy);
    }

    [Fact]
    public async Task Update_RemovesADataOptionTheBodyOmits()
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);

        var kept = BlueprintAppFactory.DataOption(field.Id, "kept");
        var dropped = BlueprintAppFactory.DataOption(field.Id, "dropped");
        await Seed(kept, dropped);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        await Put(
            Client(actor),
            field.Id,
            BodyFor(field) with
            {
                DataOptions = [new OptionBody { Id = kept.Id, OptionName = "kept" }]
            });

        await using var context = NewContext();
        var stored = await context.DataOptions
            .AsNoTracking()
            .Where(x => x.DataFieldId == field.Id)
            .ToListAsync(Ct);

        Assert.Equal(kept.Id, Assert.Single(stored).Id);
    }

    /// <summary>
    /// A body carrying no options at all removes every option the field has, because the update
    /// reconciles the collection rather than patching it.
    /// </summary>
    /// <remarks>
    /// Not a defect - it is what a PUT means - but it is worth pinning, because <c>DataOptions</c> is the
    /// one collection on the view model that a caller can drop by omission, and a client that PUTs a field
    /// it read from <c>GET msels/{id}/dataFields</c> keeps them only because that read includes them.
    /// </remarks>
    [Fact]
    public async Task Update_WithNoDataOptionsInTheBody_RemovesThemAll()
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);
        await Seed(
            BlueprintAppFactory.DataOption(field.Id, "one"),
            BlueprintAppFactory.DataOption(field.Id, "two"));

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        await Put(Client(actor), field.Id, BodyFor(field));

        await using var context = NewContext();

        Assert.Empty(await context.DataOptions.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task Update_NotifiesTheMselGroupWithTheModifiedProperties()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id, name: "Before");
        await Seed(field);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Put(Client(actor), field.Id, BodyFor(field) with { Name = "After" });

        var send = Hub.Of(MainHubMethods.DataFieldUpdated)
            .Single(x => x.Group == msel.Id.ToString());

        Assert.Equal("After", Assert.IsType<DataField>(send.Payload).Name);
        Assert.Contains("name", Assert.IsType<string[]>(send.Args[1]));
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE dataFields/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_OnAMsel_AsTheOwner_DeletesIt()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/dataFields/{field.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(field.Id, await Read<Guid>(response));

        await using var context = NewContext();

        Assert.Empty(await context.DataFields.AsNoTracking().ToListAsync(Ct));
    }

    /// <summary>
    /// Deleting a column deletes every cell in it and every option behind it, by cascade.
    /// </summary>
    [Fact]
    public async Task Delete_CascadesToTheDataValuesAndTheDataOptions()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var scenarioEvent = BlueprintAppFactory.ScenarioEvent(msel.Id);
        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(scenarioEvent, field);
        await Seed(
            BlueprintAppFactory.DataValue(field.Id, scenarioEvent.Id, "entered"),
            BlueprintAppFactory.DataOption(field.Id, "option"));

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Client(actor).DeleteAsync($"/api/dataFields/{field.Id}", Ct);

        await using var context = NewContext();

        Assert.Empty(await context.DataValues.AsNoTracking().ToListAsync(Ct));
        Assert.Empty(await context.DataOptions.AsNoTracking().ToListAsync(Ct));
        Assert.Single(await context.ScenarioEvents.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task Delete_OnAMsel_WithNoRoleOnTheMsel_Is403()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);

        var actor = await Actor().SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/dataFields/{field.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// Delete reads the stored row to choose its permission branch, which is what create and update should
    /// do: <c>ManageDataFields</c> alone cannot reach an MSEL's column here.
    /// </summary>
    /// <remarks>
    /// The contrast with <see cref="Update_WithNoMselIdInTheBody_DetachesAMselsFieldForAnyoneHoldingManageDataFields"/>
    /// is the point of this test: one method on this service already does it correctly, so the fix for the
    /// other two is written down in the same file.
    /// </remarks>
    [Fact]
    public async Task Delete_OnAMsel_WithManageDataFieldsOnly_Is403()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/dataFields/{field.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Delete_ATemplate_WithManageDataFields_DeletesIt()
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/dataFields/{field.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Delete_ATemplate_WithEditMselsOnly_Is403()
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);

        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/dataFields/{field.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Delete_ForAnUnknownId_Is404()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/dataFields/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_OnAMsel_UpdatesTheMselsModifiedInfo()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var before = DateTime.UtcNow;
        await Client(actor).DeleteAsync($"/api/dataFields/{field.Id}", Ct);

        await using var context = NewContext();
        var stored = await context.Msels.AsNoTracking().SingleAsync(x => x.Id == msel.Id, Ct);

        Assert.Equal(actor.Id, stored.ModifiedBy);
        AssertStampedBetween(stored.DateModified, before, DateTime.UtcNow);
    }

    [Fact]
    public async Task Delete_NotifiesTheMselGroupWithTheIdAlone()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        await Seed(field);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Client(actor).DeleteAsync($"/api/dataFields/{field.Id}", Ct);

        var send = Hub.Of(MainHubMethods.DataFieldDeleted)
            .Single(x => x.Group == MainHub.ADMIN_DATA_GROUP);

        Assert.Equal(field.Id, Assert.IsType<Guid>(send.Payload));
    }

    /// <summary>
    /// The delete answers 200 with the deleted id in the body.
    /// </summary>
    /// <remarks>
    /// Characterization of the contract: the action declares
    /// <c>[ProducesResponseType(typeof(Guid), 204)]</c>, which is a pair no response can satisfy - a 204
    /// has no body. Belongs on the Phase 4 contract list. Turns red when the action returns
    /// <c>NoContent()</c>.
    /// </remarks>
    [Fact]
    public async Task Delete_Is200NotNoContent()
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/dataFields/{field.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(0, response.Content.Headers.ContentLength);
    }

    // ---------------------------------------------------------------------------------------------
    // POST dataFields/json
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task UploadJson_CreatesTheTemplates()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await UploadJson(Client(actor), """
            [{"name":"Uploaded","dataType":60,"displayOrder":3,"isTemplate":false}]
            """);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var created = Assert.Single(await Read<List<DataField>>(response));

        Assert.Equal("Uploaded", created.Name);
        Assert.Equal(DataFieldType.Html, created.DataType);
        Assert.Equal(3, created.DisplayOrder);

        await using var context = NewContext();
        var stored = await context.DataFields.AsNoTracking().SingleAsync(Ct);

        Assert.Equal(created.Id, stored.Id);
        Assert.Equal(actor.Id, stored.CreatedBy);
    }

    /// <summary>
    /// Whatever scope the file names is discarded: an uploaded field is always an unscoped template.
    /// </summary>
    [Fact]
    public async Task UploadJson_ForcesEveryFieldToBeAnUnscopedTemplate()
    {
        var msel = BlueprintAppFactory.Msel();
        var injectType = BlueprintAppFactory.InjectType();
        await Seed(msel, injectType);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var created = Assert.Single(await Read<List<DataField>>(await UploadJson(Client(actor), $$"""
            [{"name":"Uploaded","mselId":"{{msel.Id}}","injectTypeId":"{{injectType.Id}}",
              "isTemplate":false}]
            """)));

        Assert.Null(created.MselId);
        Assert.Null(created.InjectTypeId);
        Assert.True(created.IsTemplate);
    }

    [Fact]
    public async Task UploadJson_AssignsAFreshIdRatherThanTheFilesOwn()
    {
        var existing = BlueprintAppFactory.DataField();
        await Seed(existing);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var created = Assert.Single(await Read<List<DataField>>(await UploadJson(Client(actor), $$"""
            [{"id":"{{existing.Id}}","name":"Uploaded"}]
            """)));

        Assert.NotEqual(existing.Id, created.Id);

        await using var context = NewContext();

        Assert.Equal(2, await context.DataFields.AsNoTracking().CountAsync(Ct));
    }

    [Fact]
    public async Task UploadJson_RecreatesTheDataOptionsAgainstTheNewField()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var created = Assert.Single(await Read<List<DataField>>(await UploadJson(Client(actor), """
            [{"name":"Uploaded","dataOptions":[
                {"optionName":"high","optionValue":"3","displayOrder":2},
                {"optionName":"low","optionValue":"1","displayOrder":1}]}]
            """)));

        await using var context = NewContext();
        var stored = await context.DataOptions
            .AsNoTracking()
            .OrderBy(x => x.DisplayOrder)
            .ToListAsync(Ct);

        Assert.Equal(["low", "high"], stored.Select(x => x.OptionName));
        Assert.All(stored, x => Assert.Equal(created.Id, x.DataFieldId));
        Assert.All(stored, x => Assert.Equal(actor.Id, x.CreatedBy));
    }

    /// <summary>
    /// An uploaded option loses its description.
    /// </summary>
    /// <remarks>
    /// Characterization. <c>UploadJsonAsync</c> does not map the option - it constructs a
    /// <c>DataOptionEntity</c> by hand from six properties and <c>OptionDescription</c> is not one of
    /// them - so the field a user wrote to explain the choice is dropped without a word. See
    /// <see cref="DownloadJson_ThenUploadJson_LosesEveryOptionDescription"/> for what that does to a
    /// round trip. Turns red when the property is copied, or when the option is mapped like everything
    /// else.
    /// </remarks>
    [Fact]
    public async Task UploadJson_DropsTheOptionDescription()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        await UploadJson(Client(actor), """
            [{"name":"Uploaded","dataOptions":[
                {"optionName":"high","optionDescription":"the most urgent"}]}]
            """);

        await using var context = NewContext();
        var stored = await context.DataOptions.AsNoTracking().SingleAsync(Ct);

        Assert.Equal("high", stored.OptionName);
        Assert.Null(stored.OptionDescription);
    }

    [Fact]
    public async Task UploadJson_WithAnEmptyArray_CreatesNothing()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await UploadJson(Client(actor), "[]");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await Read<List<DataField>>(response));
    }

    [Fact]
    public async Task UploadJson_WithoutManageDataFields_Is403()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await UploadJson(Client(actor), """[{"name":"Uploaded"}]""");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UploadJson_WithMalformedJson_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await UploadJson(Client(actor), "{not json");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// A request with no file at all is a 400, and never reaches the service.
    /// </summary>
    /// <remarks>
    /// The service would have dereferenced null - it opens <c>form.ToUpload</c> without checking it - but
    /// <c>FileForm.ToUpload</c> carries <c>[Required]</c>, so <c>ValidateModelStateFilter</c> answers
    /// first. Worth a test because the guard is an attribute on a shared view model rather than anything
    /// visible in this endpoint, so a service-level test of the same case would report a 500.
    /// </remarks>
    [Fact]
    public async Task UploadJson_WithNoFile_Is400()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        using var content = new MultipartFormDataContent { { new StringContent("1"), "Unused" } };

        var response = await Client(actor).PostAsync("/api/dataFields/json", content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // POST dataFields/json/download
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task DownloadJson_ReturnsOnlyTheRequestedFields()
    {
        var wanted = BlueprintAppFactory.DataField(name: "Wanted");
        await Seed(wanted, BlueprintAppFactory.DataField(name: "Unwanted"));

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var body = await Download(Client(actor), wanted.Id);

        Assert.Contains("Wanted", body);
        Assert.DoesNotContain("Unwanted", body);
    }

    [Fact]
    public async Task DownloadJson_IsNamedForTheTemplates()
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/dataFields/json/download", new[] { field.Id }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType.MediaType);
        Assert.Equal("data-field-templates.json", response.Content.Headers.ContentDisposition.FileName);
    }

    [Fact]
    public async Task DownloadJson_IncludesTheDataOptions()
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);
        await Seed(BlueprintAppFactory.DataOption(field.Id, "included"));

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        Assert.Contains("included", await Download(Client(actor), field.Id));
    }

    /// <summary>
    /// The downloaded file is reference-preserving JSON, so the array of fields is an object with
    /// <c>$id</c> and <c>$values</c> rather than a bare array.
    /// </summary>
    /// <remarks>
    /// Worth pinning because it is a wire format two sides have to agree on and neither declares:
    /// <c>DownloadJsonAsync</c> serializes with <c>ReferenceHandler.Preserve</c> and
    /// <c>UploadJsonAsync</c> deserializes with it, so the file round-trips here but is not what a client
    /// hand-writing one would produce.
    /// </remarks>
    [Fact]
    public async Task DownloadJson_WritesReferencePreservingJson()
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var body = await Download(Client(actor), field.Id);

        Assert.Contains("\"$id\"", body);
        Assert.Contains("\"$values\"", body);
    }

    [Fact]
    public async Task DownloadJson_ForAnUnknownId_IsAnEmptyCollection()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var body = await Download(Client(actor), Guid.NewGuid());

        Assert.DoesNotContain("name", body);
    }

    [Fact]
    public async Task DownloadJson_WithoutManageDataFields_Is403()
    {
        var field = BlueprintAppFactory.DataField();
        await Seed(field);

        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/dataFields/json/download", new[] { field.Id }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DownloadJson_WithNoBody_Is400()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Client(actor).PostAsync(
            "/api/dataFields/json/download",
            new StringContent("null", Encoding.UTF8, "application/json"),
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// A download followed by an upload keeps the field and its options but loses every option
    /// description.
    /// </summary>
    /// <remarks>
    /// Characterization, and the reason <see cref="UploadJson_DropsTheOptionDescription"/> matters: the
    /// two endpoints exist as a pair, for moving templates between installations, and the pair is not a
    /// round trip. Turns red when the upload copies the description.
    /// </remarks>
    [Fact]
    public async Task DownloadJson_ThenUploadJson_LosesEveryOptionDescription()
    {
        var field = BlueprintAppFactory.DataField(name: "Round Tripped");
        await Seed(field);

        var option = BlueprintAppFactory.DataOption(field.Id, "high");
        option.OptionDescription = "the most urgent";
        await Seed(option);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var downloaded = await Download(Client(actor), field.Id);

        Assert.Contains("the most urgent", downloaded);

        var created = Assert.Single(await Read<List<DataField>>(
            await UploadJson(Client(actor), downloaded)));

        Assert.Equal("Round Tripped", created.Name);
        Assert.NotEqual(field.Id, created.Id);

        await using var context = NewContext();
        var stored = await context.DataOptions
            .AsNoTracking()
            .SingleAsync(x => x.DataFieldId == created.Id, Ct);

        Assert.Equal("high", stored.OptionName);
        Assert.Null(stored.OptionDescription);
    }

    // ---------------------------------------------------------------------------------------------
    // Authentication
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "dataFields/templates")]
    [InlineData("GET", "msels/00000000-0000-0000-0000-000000000001/dataFields")]
    [InlineData("GET", "injectTypes/00000000-0000-0000-0000-000000000001/dataFields")]
    [InlineData("GET", "dataFields/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "dataFields")]
    [InlineData("PUT", "dataFields/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "dataFields/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "dataFields/json")]
    [InlineData("POST", "dataFields/json/download")]
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

    /// <summary>
    /// The wire shape of a data field. A record rather than an anonymous type so a test can vary one
    /// property with a <c>with</c> expression, and so the properties a test never mentions are always
    /// sent the same way.
    /// </summary>
    private sealed record FieldBody
    {
        public Guid Id { get; init; }
        public Guid? MselId { get; init; }
        public Guid? InjectTypeId { get; init; }
        public string Name { get; init; }
        public string Description { get; init; }
        public DataFieldType DataType { get; init; }
        public int DisplayOrder { get; init; }
        public bool IsTemplate { get; init; }
        public bool IsChosenFromList { get; init; }
        public OptionBody[] DataOptions { get; init; } = [];

        // Non-nullable on ViewModels.Base, so these two have to be sent as values rather than nulls:
        // System.Text.Json rejects a null for a Guid or a DateTime and the request never reaches the
        // controller.
        public Guid CreatedBy { get; init; }
        public DateTime DateCreated { get; init; }

        public Guid? ModifiedBy { get; init; }
        public DateTime? DateModified { get; init; }
    }

    private sealed record OptionBody
    {
        public Guid Id { get; init; }
        public Guid DataFieldId { get; init; }
        public string OptionName { get; init; }
        public string OptionValue { get; init; }
        public string OptionDescription { get; init; }
        public int DisplayOrder { get; init; }
    }

    private static FieldBody Body(Guid? mselId = null) => new()
    {
        MselId = mselId,
        Name = "Created Field",
        Description = "<p>Created by a test</p>",
        DataType = DataFieldType.Html,
        DisplayOrder = 1
    };

    private Task<HttpResponseMessage> Post(HttpClient client, FieldBody body) =>
        client.PostAsJsonAsync("/api/dataFields", body, Ct);

    /// <summary>
    /// The body that echoes a stored field back unchanged, for a test to vary one property of with a
    /// <c>with</c> expression.
    /// </summary>
    /// <remarks>
    /// A helper taking <c>Guid? mselId = null</c> would not do, and that is the whole reason this is
    /// shaped as a record: <see cref="Update_WithNoMselIdInTheBody_DetachesAMselsFieldForAnyoneHoldingManageDataFields"/>
    /// needs to send <c>mselId: null</c> deliberately, which such a helper cannot tell apart from a
    /// caller who did not mention it. Note the options default to none, because the service reconciles
    /// the collection rather than patching it - see
    /// <see cref="Update_WithNoDataOptionsInTheBody_RemovesThemAll"/>.
    /// </remarks>
    private static FieldBody BodyFor(DataFieldEntity field) => new()
    {
        Id = field.Id,
        MselId = field.MselId,
        InjectTypeId = field.InjectTypeId,
        Name = field.Name,
        Description = field.Description,
        DataType = field.DataType,
        DisplayOrder = field.DisplayOrder,
        IsTemplate = field.IsTemplate
    };

    private Task<HttpResponseMessage> Put(HttpClient client, Guid id, FieldBody body) =>
        client.PutAsJsonAsync($"/api/dataFields/{id}", body, Ct);

    private async Task<List<DataField>> GetFields(HttpClient client, string route)
    {
        var response = await client.GetAsync(route, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<List<DataField>>(response);
    }

    private async Task<DataField> GetField(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/dataFields/{id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<DataField>(response);
    }

    private async Task<string> Download(HttpClient client, params Guid[] ids)
    {
        var response = await client.PostAsJsonAsync("/api/dataFields/json/download", ids, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadAsStringAsync(Ct);
    }

    /// <remarks>
    /// The <c>await</c> before the <c>using</c> falls out of scope is load-bearing: <c>TestServer</c>
    /// reads the request body inside <c>SendAsync</c>, so returning the task unawaited disposes the
    /// content first and every upload test fails with <c>ObjectDisposedException</c> rather than whatever
    /// it was asserting.
    /// </remarks>
    private async Task<HttpResponseMessage> UploadJson(HttpClient client, string json)
    {
        using var content = new MultipartFormDataContent();

        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "ToUpload", "data-fields.json");

        return await client.PostAsync("/api/dataFields/json", content, Ct);
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
