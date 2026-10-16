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
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>The nine data-field endpoints, driven over HTTP.</summary>
public class DataFieldEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET dataFields/templates
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Templates_ReturnsOnlyFieldsFlaggedAsTemplates()
    {
        var msel = TestData.Msel();
        var injectType = TestData.InjectType();
        await Seed(msel, injectType);

        var template = TestData.DataField();
        await Seed(
            template,
            TestData.DataField(mselId: msel.Id),
            TestData.DataField(injectTypeId: injectType.Id));

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
        var field = TestData.DataField();
        await Seed(field);
        await Seed(
            TestData.DataOption(field.Id, "red"),
            TestData.DataOption(field.Id, "green"));

        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var returned = Assert.Single(await GetFields(Client(actor), "/api/dataFields/templates"));

        Assert.Equal(
            ["green", "red"],
            returned.DataOptions.Select(x => x.OptionName).Order(StringComparer.Ordinal));
    }

    /// <summary>Templates with no system role is answered with a 200.</summary>
    [Fact]
    public async Task Templates_WithNoSystemRole_Is200()
    {
        await Seed(TestData.DataField());

        var actor = await Actor().SeedAsync();

        Assert.Single(await GetFields(Client(actor), "/api/dataFields/templates"));
    }

    /// <summary>
    /// A template belongs to no MSEL, so a MSEL's column flagged <c>IsTemplate</c> - which a create can no
    /// longer write - stays out of the shared template list any signed-in user may read.
    /// </summary>
    [Fact]
    public async Task Templates_LeavesOutAMselScopedFieldFlaggedAsATemplate()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var field = TestData.DataField(mselId: msel.Id, isTemplate: true);
        await Seed(field);

        var actor = await Actor().SeedAsync();

        Assert.Empty(await GetFields(Client(actor), "/api/dataFields/templates"));
    }

    // ---------------------------------------------------------------------------------------------
    // GET msels/{mselId}/dataFields
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByMsel_AsAViewer_ReturnsTheMselsFields()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var field = TestData.DataField(mselId: msel.Id);
        await Seed(field);

        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var returned = await GetFields(Client(actor), $"/api/msels/{msel.Id}/dataFields");

        Assert.Equal(field.Id, Assert.Single(returned).Id);
    }

    [Fact]
    public async Task GetByMsel_WithViewMselsPermission_ReturnsThemWithNoRole()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(TestData.DataField(mselId: msel.Id));

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Single(await GetFields(Client(actor), $"/api/msels/{msel.Id}/dataFields"));
    }

    [Fact]
    public async Task GetByMsel_DoesNotReturnAnotherMselsFields()
    {
        var msel = TestData.Msel();
        var other = TestData.Msel();
        await Seed(msel, other);

        var field = TestData.DataField(mselId: msel.Id);
        await Seed(field, TestData.DataField(mselId: other.Id));

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var returned = await GetFields(Client(actor), $"/api/msels/{msel.Id}/dataFields");

        Assert.Equal(field.Id, Assert.Single(returned).Id);
    }

    [Fact]
    public async Task GetByMsel_IncludesTheDataOptions()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var field = TestData.DataField(mselId: msel.Id);
        await Seed(field);
        await Seed(TestData.DataOption(field.Id, "only"));

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var returned = Assert.Single(await GetFields(Client(actor), $"/api/msels/{msel.Id}/dataFields"));

        Assert.Equal("only", Assert.Single(returned.DataOptions).OptionName);
    }

    [Fact]
    public async Task GetByMsel_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(TestData.DataField(mselId: msel.Id));

        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

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
        var msel = TestData.Msel(isTemplate: true);
        await Seed(msel);
        await Seed(TestData.DataField(mselId: msel.Id));

        var actor = await Actor().SeedAsync();

        Assert.Single(await GetFields(Client(actor), $"/api/msels/{msel.Id}/dataFields"));
    }

    /// <summary>Get by MSEL for an unknown MSEL is answered with a 500.</summary>
    [Fact]
    public async Task GetByMsel_ForAnUnknownMsel_Is500()
    {
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{Guid.NewGuid()}/dataFields", Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Object reference not set to an instance of an object.", failure.Title);
        Assert.Contains("DataFieldService.GetByMselAsync", failure.Detail);
    }

    /// <summary>Get by MSEL for an unknown MSEL with view MSELs permission is an empty array.</summary>
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
        var injectType = TestData.InjectType();
        var other = TestData.InjectType();
        await Seed(injectType, other);

        var field = TestData.DataField(injectTypeId: injectType.Id);
        await Seed(field, TestData.DataField(injectTypeId: other.Id));

        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var returned = await GetFields(Client(actor), $"/api/injectTypes/{injectType.Id}/dataFields");

        Assert.Equal(field.Id, Assert.Single(returned).Id);
    }

    /// <summary>Get by inject type with no system role is answered with a 200.</summary>
    [Fact]
    public async Task GetByInjectType_WithNoSystemRole_Is200()
    {
        var injectType = TestData.InjectType();
        await Seed(injectType);
        await Seed(TestData.DataField(injectTypeId: injectType.Id));

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
        var msel = TestData.Msel();
        await Seed(msel);

        var field = TestData.DataField(mselId: msel.Id, name: "Assigned To");
        await Seed(field);

        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var returned = await GetField(Client(actor), field.Id);

        Assert.Equal("Assigned To", returned.Name);
        Assert.Equal(msel.Id, returned.MselId);
    }

    [Fact]
    public async Task Get_ForAMselField_WithViewMselsPermission_ReturnsIt()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var field = TestData.DataField(mselId: msel.Id);
        await Seed(field);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Equal(field.Id, (await GetField(Client(actor), field.Id)).Id);
    }

    [Fact]
    public async Task Get_ForAMselField_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var field = TestData.DataField(mselId: msel.Id);
        await Seed(field);

        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

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
        var field = TestData.DataField();
        await Seed(field);
        await Seed(TestData.DataOption(field.Id, "shared"));

        var actor = await Actor().SeedAsync();

        var returned = await GetField(Client(actor), field.Id);

        Assert.Null(returned.MselId);
        Assert.Equal("shared", Assert.Single(returned.DataOptions).OptionName);
    }

    /// <summary>Get for an inject types field with no system role returns it.</summary>
    [Fact]
    public async Task Get_ForAnInjectTypesField_WithNoSystemRole_ReturnsIt()
    {
        var injectType = TestData.InjectType();
        await Seed(injectType);

        var field = TestData.DataField(injectTypeId: injectType.Id);
        await Seed(field);

        var actor = await Actor().SeedAsync();

        Assert.Equal(injectType.Id, (await GetField(Client(actor), field.Id)).InjectTypeId);
    }

    /// <summary>Get for an unknown id is answered with a 500.</summary>
    [Fact]
    public async Task Get_ForAnUnknownId_Is500()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Client(actor).GetAsync($"/api/dataFields/{Guid.NewGuid()}", Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Sequence contains no elements.", failure.Title);
        Assert.Contains("DataFieldService.GetAsync", failure.Detail);
    }

    /// <summary>A data field's type goes out by its name, the address types included.</summary>
    [Theory]
    [InlineData(DataFieldType.IpAddress, "IpAddress")]
    [InlineData(DataFieldType.Network, "Network")]
    public async Task Get_AnswersAnAddressTypeByItsName(DataFieldType dataType, string name)
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var field = TestData.DataField(mselId: msel.Id, dataType: dataType);
        await Seed(field);
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/dataFields/{field.Id}", Ct);

        Assert.Equal(name, (await Json(response)).GetProperty("dataType").GetString());
    }

    /// <summary>A row still holding the retired type 160 is read, and its type goes out as the number.</summary>
    [Fact]
    public async Task Get_ForAFieldOfTheRetiredType160_AnswersTheNumber()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var field = TestData.DataField(mselId: msel.Id, dataType: RetiredType);
        await Seed(field);
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/dataFields/{field.Id}", Ct);

        Assert.Equal(160, (await Json(response)).GetProperty("dataType").GetInt32());
    }

    // ---------------------------------------------------------------------------------------------
    // POST dataFields
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_OnAMsel_AsTheOwner_CreatesTheField()
    {
        var msel = TestData.Msel();
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
    [InlineData(DataFieldType.IpAddress)]
    [InlineData(DataFieldType.Network)]
    public async Task Create_OfAnAddressField_StoresItsType(DataFieldType dataType)
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var created = await Read<DataField>(await Post(Client(actor), Body(msel.Id) with { DataType = dataType }));

        Assert.Equal(dataType, created.DataType);
        Assert.Equal(dataType, (await ReadBack(rb => rb.DataFields.SingleAsync(x => x.Id == created.Id, Ct))).DataType);
    }

    /// <summary>A client may name the type rather than send its number.</summary>
    [Fact]
    public async Task Create_WithTheTypeNamedOnTheWire_StoresIt()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        using var body = new StringContent(
            $$"""{"mselId":"{{msel.Id}}","name":"Subnet","dataType":"Network","displayOrder":1,"createdBy":"{{Guid.Empty}}","dateCreated":"2026-01-01T00:00:00Z"}""",
            Encoding.UTF8, "application/json");

        var created = await Read<DataField>(await Client(actor).PostAsync("/api/dataFields", body, Ct));

        Assert.Equal(DataFieldType.Network, (await ReadBack(rb => rb.DataFields.SingleAsync(x => x.Id == created.Id, Ct))).DataType);
    }

    /// <summary>A data type no member names is stored as sent.</summary>
    [Fact]
    public async Task Create_WithADataTypeNoMemberNames_StoresIt()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var created = await Read<DataField>(await Post(Client(actor), Body(msel.Id) with { DataType = UndefinedType }));

        Assert.Equal(UndefinedType, (await ReadBack(rb => rb.DataFields.SingleAsync(x => x.Id == created.Id, Ct))).DataType);
    }

    /// <summary>The retired type number 160 is stored as sent.</summary>
    [Fact]
    public async Task Create_WithTheRetiredTypeNumber160_StoresIt()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var created = await Read<DataField>(await Post(Client(actor), Body(msel.Id) with { DataType = RetiredType }));

        Assert.Equal(RetiredType, (await ReadBack(rb => rb.DataFields.SingleAsync(x => x.Id == created.Id, Ct))).DataType);
    }

    [Theory]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Owner)]
    public async Task Create_OnAMsel_AsAnEditorOrOwner_CreatesIt(MselRole role)
    {
        var msel = TestData.Msel();
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
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_OnAMsel_WithEditMselsPermission_CreatesIt()
    {
        var msel = TestData.Msel();
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
        var msel = TestData.Msel();
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
    /// A field belongs to one owner, so an MSEL's column cannot also be put in the installation's shared
    /// template list.
    /// </summary>
    [Fact]
    public async Task Create_WithIsTemplateOnAMselField_Is409AndCreatesNothing()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(owner), Body(msel.Id) with { IsTemplate = true });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(await NewContext().DataFields.AsNoTracking().ToListAsync(Ct));
    }

    /// <summary>
    /// An inject type's field cannot also be a template.
    /// </summary>
    [Fact]
    public async Task Create_WithIsTemplateOnAnInjectTypeField_Is409AndCreatesNothing()
    {
        var injectType = TestData.InjectType();
        await Seed(injectType);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Post(Client(actor), Body() with { InjectTypeId = injectType.Id, IsTemplate = true });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(await NewContext().DataFields.AsNoTracking().ToListAsync(Ct));
    }

    /// <summary>Create with no scope and is template false creates a field no list returns.</summary>
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
    /// A field cannot belong to an MSEL and an inject type at once. The service refuses the pair before
    /// the MSEL branch can authorize it, so an MSEL owner cannot add a field to a global inject type.
    /// </summary>
    [Fact]
    public async Task Create_WithBothAMselAndAnInjectType_Is409AndCreatesNothing()
    {
        var msel = TestData.Msel();
        var injectType = TestData.InjectType();
        await Seed(msel, injectType);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id) with { InjectTypeId = injectType.Id });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(await NewContext().DataFields.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task Create_ForAnUnknownMsel_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(Client(actor), Body(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_ForAnUnknownInjectType_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Post(Client(actor), Body() with { InjectTypeId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_StampsTheAuditFieldsOnTheServer()
    {
        var msel = TestData.Msel();
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
        var msel = TestData.Msel();
        await Seed(msel);
        var other = TestData.Msel();
        await Seed(other);
        await Seed(
            TestData.ScenarioEvent(msel.Id, deltaSeconds: 0),
            TestData.ScenarioEvent(msel.Id, deltaSeconds: 60),
            TestData.ScenarioEvent(other.Id));

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
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(TestData.ScenarioEvent(msel.Id));

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        await Post(Client(actor), Body() with { IsTemplate = true });

        await using var context = NewContext();

        Assert.Empty(await context.DataValues.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task Create_OnAMsel_UpdatesTheMselsModifiedInfo()
    {
        var msel = TestData.Msel();
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
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var created = await Read<DataField>(await Post(Client(actor), Body(msel.Id)));

        Assert.Equal(
            [msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.DataFieldCreated, msel.Id));

        var sent = Assert.IsType<DataField>(
            Hub.Of(MainHubMethods.DataFieldCreated, msel.Id).First().Payload);

        Assert.Equal(created.Id, sent.Id);
    }

    /// <summary>Create a template broadcasts to a group named by the empty string.</summary>
    [Fact]
    public async Task Create_ATemplate_BroadcastsToAGroupNamedByTheEmptyString()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        await Post(Client(actor), Body() with { IsTemplate = true });

        Assert.Equal(
            [string.Empty, MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.DataFieldCreated, string.Empty));
    }

    /// <summary>Create is answered with a 200 with no location header.</summary>
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
        var msel = TestData.Msel();
        await Seed(msel);

        var field = TestData.DataField(mselId: msel.Id, name: "Before");
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

    [Fact]
    public async Task Update_OnAMsel_with_EditMsels_and_no_role_changes_the_field()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var field = TestData.DataField(mselId: msel.Id, name: "Before");
        await Seed(field);

        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Put(Client(actor), field.Id, BodyFor(field) with { Name = "After" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("After", (await ReadBack(rb => rb.DataFields.SingleAsync(x => x.Id == field.Id, Ct))).Name);
    }

    [Theory]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.Viewer)]
    public async Task Update_OnAMsel_WithAReadOnlyRole_Is403(MselRole role)
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var field = TestData.DataField(mselId: msel.Id);
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
    /// The edit request has no id, so another id in the body is ignored and the route's field is written.
    /// </summary>
    [Fact]
    public async Task Update_WithAMismatchedBodyId_UpdatesTheRoutesField()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var field = TestData.DataField(mselId: msel.Id);
        await Seed(field);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).PutAsJsonAsync(
            $"/api/dataFields/{field.Id}",
            BodyFor(field) with { Id = Guid.NewGuid(), Name = "Renamed" },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Renamed", (await NewContext().DataFields.AsNoTracking().SingleAsync(x => x.Id == field.Id, Ct)).Name);
    }

    /// <summary>
    /// A caller holding only <c>ManageDataFields</c> cannot edit an MSEL's column: the stored field
    /// belongs to an MSEL, so MSEL rights are required whatever the body says.
    /// </summary>
    [Fact]
    public async Task Update_WithNoMselIdInTheBody_WithOnlyManageDataFields_Is403AndLeavesTheFieldAlone()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var field = TestData.DataField(mselId: msel.Id, name: "Assigned To");
        await Seed(field);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Put(
            Client(actor),
            field.Id,
            BodyFor(field) with { MselId = null, Name = "Mine Now" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var context = NewContext();
        var stored = await context.DataFields.AsNoTracking().SingleAsync(x => x.Id == field.Id, Ct);

        Assert.Equal(msel.Id, stored.MselId);
        Assert.Equal("Assigned To", stored.Name);
    }

    /// <summary>
    /// A shared template needs <c>ManageDataFields</c>, so rights on an MSEL named in the body buy
    /// nothing.
    /// </summary>
    [Fact]
    public async Task Update_OfATemplate_AsAMselEditor_Is403AndLeavesItAlone()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var template = TestData.DataField(name: "Shared Template", isTemplate: true);
        await Seed(template);

        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var response = await Put(
            Client(actor),
            template.Id,
            BodyFor(template) with { MselId = msel.Id, IsTemplate = false });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var context = NewContext();
        var stored = await context.DataFields.AsNoTracking().SingleAsync(x => x.Id == template.Id, Ct);

        Assert.Null(stored.MselId);
        Assert.True(stored.IsTemplate);
    }

    /// <summary>
    /// An editor of one MSEL cannot edit another MSEL's column by naming their own MSEL in the body.
    /// </summary>
    [Fact]
    public async Task Update_OfAnotherMselsField_AsAnEditorNamingTheirOwnMsel_Is403AndLeavesItAlone()
    {
        var mine = TestData.Msel();
        var theirs = TestData.Msel();
        await Seed(mine, theirs);

        var field = TestData.DataField(mselId: theirs.Id, name: "Theirs");
        await Seed(field);

        var actor = await Actor().OnMsel(mine, MselRole.Editor).SeedAsync();

        var response = await Put(Client(actor), field.Id, BodyFor(field) with { MselId = mine.Id, Name = "Mine Now" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var stored = await NewContext().DataFields.AsNoTracking().SingleAsync(x => x.Id == field.Id, Ct);
        Assert.Equal(theirs.Id, stored.MselId);
        Assert.Equal("Theirs", stored.Name);
    }

    /// <summary>
    /// An owner's edit cannot move the field: the owner fields in the body are ignored and the edit is
    /// applied in place.
    /// </summary>
    [Fact]
    public async Task Update_AsAnOwner_NamingAnotherOwnerInTheBody_KeepsTheFieldInItsMsel()
    {
        var msel = TestData.Msel();
        var other = TestData.Msel();
        var injectType = TestData.InjectType();
        await Seed(msel, other, injectType);

        var field = TestData.DataField(mselId: msel.Id);
        await Seed(field);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Put(
            Client(actor),
            field.Id,
            BodyFor(field) with { MselId = other.Id, InjectTypeId = injectType.Id, IsTemplate = true, Name = "Renamed" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await NewContext().DataFields.AsNoTracking().SingleAsync(x => x.Id == field.Id, Ct);
        Assert.Equal(msel.Id, stored.MselId);
        Assert.Null(stored.InjectTypeId);
        Assert.False(stored.IsTemplate);
        Assert.Equal("Renamed", stored.Name);
    }

    /// <summary>
    /// The options are reconciled within the field: an id that names another field's option adds a new
    /// option here and leaves the other field's alone.
    /// </summary>
    [Fact]
    public async Task Update_WithAnotherFieldsOptionId_AddsANewOptionAndLeavesTheOtherAlone()
    {
        var msel = TestData.Msel();
        var theirs = TestData.Msel();
        await Seed(msel, theirs);

        var field = TestData.DataField(mselId: msel.Id);
        var theirField = TestData.DataField(mselId: theirs.Id);
        await Seed(field, theirField);

        var theirOption = TestData.DataOption(theirField.Id, "theirs");
        await Seed(theirOption);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Put(
            Client(actor),
            field.Id,
            BodyFor(field) with
            {
                DataOptions = [new OptionBody { Id = theirOption.Id, DataFieldId = theirField.Id, OptionName = "mine" }]
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = NewContext();
        var stored = await context.DataOptions.AsNoTracking().SingleAsync(x => x.Id == theirOption.Id, Ct);
        Assert.Equal(theirField.Id, stored.DataFieldId);
        Assert.Equal("theirs", stored.OptionName);
        var added = await context.DataOptions.AsNoTracking().SingleAsync(x => x.DataFieldId == field.Id, Ct);
        Assert.NotEqual(theirOption.Id, added.Id);
        Assert.Equal("mine", added.OptionName);
    }

    /// <summary>
    /// An MSEL owner's edit of a template is refused, so the template gains no data values on the MSEL's
    /// events.
    /// </summary>
    [Fact]
    public async Task Update_OfATemplate_AsAMselOwner_AddsNoDataValues()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(TestData.ScenarioEvent(msel.Id));

        var template = TestData.DataField();
        await Seed(template);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Put(
            Client(actor),
            template.Id,
            BodyFor(template) with { MselId = msel.Id, IsTemplate = false });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var context = NewContext();

        Assert.Empty(await context.DataValues.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task Update_PreservesTheCreationAuditAndStampsTheModification()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var creatorId = Guid.NewGuid();
        var field = TestData.DataField(mselId: msel.Id, createdBy: creatorId);
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
        var msel = TestData.Msel();
        await Seed(msel);

        var field = TestData.DataField(mselId: msel.Id);
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
        var field = TestData.DataField();
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
        var field = TestData.DataField();
        await Seed(field);

        var option = TestData.DataOption(field.Id, "before");
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
        var field = TestData.DataField();
        await Seed(field);

        var kept = TestData.DataOption(field.Id, "kept");
        var dropped = TestData.DataOption(field.Id, "dropped");
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

    /// <summary>A body carrying no options removes every option the field has: the update reconciles the
    /// collection.</summary>
    [Fact]
    public async Task Update_WithNoDataOptionsInTheBody_RemovesThemAll()
    {
        var field = TestData.DataField();
        await Seed(field);
        await Seed(
            TestData.DataOption(field.Id, "one"),
            TestData.DataOption(field.Id, "two"));

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        await Put(Client(actor), field.Id, BodyFor(field));

        await using var context = NewContext();

        Assert.Empty(await context.DataOptions.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task Update_NotifiesTheMselGroupWithTheModifiedProperties()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var field = TestData.DataField(mselId: msel.Id, name: "Before");
        await Seed(field);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Put(Client(actor), field.Id, BodyFor(field) with { Name = "After" });

        var send = Hub.ToGroup(msel.Id)
            .Single(x => x.Method == MainHubMethods.DataFieldUpdated);

        Assert.Equal("After", Assert.IsType<DataField>(send.Payload).Name);
        Assert.Contains("name", Assert.IsType<string[]>(send.Arguments[1]));
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE dataFields/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_OnAMsel_AsTheOwner_DeletesIt()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var field = TestData.DataField(mselId: msel.Id);
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
        var msel = TestData.Msel();
        await Seed(msel);

        var scenarioEvent = TestData.ScenarioEvent(msel.Id);
        var field = TestData.DataField(mselId: msel.Id);
        await Seed(scenarioEvent, field);
        await Seed(
            TestData.DataValue(field.Id, scenarioEvent.Id, "entered"),
            TestData.DataOption(field.Id, "option"));

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Client(actor).DeleteAsync($"/api/dataFields/{field.Id}", Ct);

        await using var context = NewContext();

        Assert.Empty(await context.DataValues.AsNoTracking().ToListAsync(Ct));
        Assert.Empty(await context.DataOptions.AsNoTracking().ToListAsync(Ct));
        Assert.Single(await context.ScenarioEvents.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task Delete_OnAMsel_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var field = TestData.DataField(mselId: msel.Id);
        await Seed(field);

        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/dataFields/{field.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Delete_OnAMsel_with_EditMsels_and_no_role_deletes_it()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var field = TestData.DataField(mselId: msel.Id);
        await Seed(field);

        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/dataFields/{field.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await ReadBack(rb => rb.DataFields.Where(x => x.Id == field.Id).ToListAsync(Ct)));
    }

    /// <summary>Delete chooses its permission branch from the stored row, so <c>ManageDataFields</c> alone
    /// cannot reach a MSEL's column.</summary>
    [Fact]
    public async Task Delete_OnAMsel_WithManageDataFieldsOnly_Is403()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var field = TestData.DataField(mselId: msel.Id);
        await Seed(field);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/dataFields/{field.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Delete_ATemplate_WithManageDataFields_DeletesIt()
    {
        var field = TestData.DataField();
        await Seed(field);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/dataFields/{field.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Delete_ATemplate_WithEditMselsOnly_Is403()
    {
        var field = TestData.DataField();
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
        var msel = TestData.Msel();
        await Seed(msel);

        var field = TestData.DataField(mselId: msel.Id);
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
        var msel = TestData.Msel();
        await Seed(msel);

        var field = TestData.DataField(mselId: msel.Id);
        await Seed(field);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Client(actor).DeleteAsync($"/api/dataFields/{field.Id}", Ct);

        var send = Hub.ToGroup(MainHub.ADMIN_DATA_GROUP)
            .Single(x => x.Method == MainHubMethods.DataFieldDeleted);

        Assert.Equal(field.Id, Assert.IsType<Guid>(send.Payload));
    }

    /// <summary>Delete is answered with a 200 not no content.</summary>
    [Fact]
    public async Task Delete_Is200NotNoContent()
    {
        var field = TestData.DataField();
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
        var msel = TestData.Msel();
        var injectType = TestData.InjectType();
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
        var existing = TestData.DataField();
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

    /// <summary>Upload JSON drops the option description.</summary>
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

        Assert.Equal("'n' is an invalid start of a property name. Expected a '\"'. Path: $ | LineNumber: 0 | BytePositionInLine: 1.", (await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
    }

    /// <summary>A request with no file is a 400 from <c>ValidateModelStateFilter</c>, before the service
    /// runs.</summary>
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
        var wanted = TestData.DataField(name: "Wanted");
        await Seed(wanted, TestData.DataField(name: "Unwanted"));

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var body = await Download(Client(actor), wanted.Id);

        Assert.Contains("Wanted", body);
        Assert.DoesNotContain("Unwanted", body);
    }

    [Fact]
    public async Task DownloadJson_IsNamedForTheTemplates()
    {
        var field = TestData.DataField();
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
        var field = TestData.DataField();
        await Seed(field);
        await Seed(TestData.DataOption(field.Id, "included"));

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        Assert.Contains("included", await Download(Client(actor), field.Id));
    }

    /// <summary>The downloaded file is reference-preserving JSON (<c>$id</c> and <c>$values</c>).</summary>
    [Fact]
    public async Task DownloadJson_WritesReferencePreservingJson()
    {
        var field = TestData.DataField();
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

    /// <summary>
    /// The export is for fields that belong to no MSEL. An MSEL's column is read through the MSEL, so its
    /// id exports nothing even for a <c>ManageDataFields</c> holder.
    /// </summary>
    [Fact]
    public async Task DownloadJson_OfAMselsField_ExportsNothing()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var field = TestData.DataField(mselId: msel.Id, name: "Private Column");
        await Seed(field);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var body = await Download(Client(actor), field.Id);

        Assert.DoesNotContain("Private Column", body);
    }

    [Fact]
    public async Task DownloadJson_WithoutManageDataFields_Is403()
    {
        var field = TestData.DataField();
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

    /// <summary>Download JSON then upload JSON loses every option description.</summary>
    [Fact]
    public async Task DownloadJson_ThenUploadJson_LosesEveryOptionDescription()
    {
        var field = TestData.DataField(name: "Round Tripped");
        await Seed(field);

        var option = TestData.DataOption(field.Id, "high");
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

        var response = await Client().SendAsync(request, Ct);

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
    /// shaped as a record: <see cref="Update_WithNoMselIdInTheBody_WithOnlyManageDataFields_Is403AndLeavesTheFieldAlone"/>
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

    /// <summary>The number <c>DataFieldType</c> retired (DeliveryMethod, then IntegrationTarget) and does not
    /// reuse, which older databases may still hold.</summary>
    private const DataFieldType RetiredType = (DataFieldType)160;

    /// <summary>A number no <c>DataFieldType</c> member has ever carried.</summary>
    private const DataFieldType UndefinedType = (DataFieldType)999;

    private async Task<JsonElement> Json(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        return document.RootElement.Clone();
    }

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }
}
