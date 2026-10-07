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
using Blueprint.Api.Hubs;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary><c>DataFieldService.Reorder</c> - what creating, moving and deleting a data field does to the
/// <c>DisplayOrder</c> of every other one.</summary>
public class DataFieldReorderTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // Creating
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_InTheMiddle_ShiftsTheFieldsAtAndBelowItDown()
    {
        var msel = await SeedMselWith("A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Create(Client(actor), msel.Id, displayOrder: 2);

        Assert.Equal(["A=1", "N=2", "B=3", "C=4"], await Orders(msel.Id));
    }

    [Fact]
    public async Task Create_AtTheHead_ShiftsEverythingDown()
    {
        var msel = await SeedMselWith("A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Create(Client(actor), msel.Id, displayOrder: 1);

        Assert.Equal(["N=1", "A=2", "B=3", "C=4"], await Orders(msel.Id));
    }

    [Fact]
    public async Task Create_AtTheTail_LeavesTheOthersWhereTheyWere()
    {
        var msel = await SeedMselWith("A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Create(Client(actor), msel.Id, displayOrder: 4);

        Assert.Equal(["A=1", "B=2", "C=3", "N=4"], await Orders(msel.Id));
    }

    /// <summary>
    /// A number past the end of the list is clamped to the end rather than left as a gap.
    /// </summary>
    [Fact]
    public async Task Create_PastTheEnd_IsClampedToTheCount()
    {
        var msel = await SeedMselWith("A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Create(Client(actor), msel.Id, displayOrder: 9);

        Assert.Equal(["A=1", "B=2", "C=3", "N=4"], await Orders(msel.Id));
    }

    /// <summary>Create with a display order of zero leaves position one empty.</summary>
    [Fact]
    public async Task Create_WithADisplayOrderOfZero_LeavesPositionOneEmpty()
    {
        var msel = await SeedMselWith("A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Create(Client(actor), msel.Id, displayOrder: 0);

        Assert.Equal(["N=0", "A=2", "B=3", "C=4"], await Orders(msel.Id));
    }

    [Fact]
    public async Task Create_RenumbersOnlyTheMselItIsOn()
    {
        var msel = await SeedMselWith("A", "B", "C");
        var other = await SeedMselWith("X", "Y", "Z");

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Create(Client(actor), msel.Id, displayOrder: 1);

        Assert.Equal(["X=1", "Y=2", "Z=3"], await Orders(other.Id));
    }

    [Fact]
    public async Task Create_ForAnInjectType_RenumbersOnlyThatInjectType()
    {
        var injectType = TestData.InjectType();
        var other = TestData.InjectType();
        await Seed(injectType, other);
        await Seed(
            TestData.DataField(injectTypeId: injectType.Id, displayOrder: 1, name: "A"),
            TestData.DataField(injectTypeId: injectType.Id, displayOrder: 2, name: "B"),
            TestData.DataField(injectTypeId: other.Id, displayOrder: 1, name: "X"));

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/dataFields",
            new FieldBody { InjectTypeId = injectType.Id, Name = "N", DisplayOrder = 1 },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(["N=1", "A=2", "B=3"], await Orders(injectTypeId: injectType.Id));
        Assert.Equal(["X=1"], await Orders(injectTypeId: other.Id));
    }

    /// <summary>Create a template does not renumber the other templates.</summary>
    [Fact]
    public async Task Create_ATemplate_DoesNotRenumberTheOtherTemplates()
    {
        await Seed(
            TestData.DataField(displayOrder: 1, name: "A"),
            TestData.DataField(displayOrder: 2, name: "B"));

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/dataFields",
            new FieldBody { Name = "N", DisplayOrder = 2, IsTemplate = true },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(["A=1", "B=2", "N=2"], await Orders());
    }

    /// <summary>
    /// Every field the renumbering touches is broadcast as an update, and the ones whose number did not
    /// change are not.
    /// </summary>
    /// <remarks>
    /// The renumbering saves inside the request's transaction, so these arrive with the create rather than
    /// separately - and a client redrawing the grid from the <c>DataFieldCreated</c> message alone would
    /// have the other columns' positions wrong.
    /// </remarks>
    [Fact]
    public async Task Create_BroadcastsAnUpdateForEveryFieldItRenumbers()
    {
        var msel = await SeedMselWith("A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Create(Client(actor), msel.Id, displayOrder: 2);

        Assert.Equal(
            ["B", "C"],
            Hub.ToGroup(msel.Id)
                .Where(x => x.Method == MainHubMethods.DataFieldUpdated)
                .Select(x => ((DataField)x.Payload).Name)
                .Order(StringComparer.Ordinal));

        Assert.Equal(
            "N",
            ((DataField)Hub.Of(MainHubMethods.DataFieldCreated, msel.Id).First().Payload).Name);
    }

    // ---------------------------------------------------------------------------------------------
    // Moving
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_MovingAFieldDownOne_SwapsItWithItsNeighbour()
    {
        var msel = await SeedMselWith("A", "B", "C", "D");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel, "B", to: 3);

        Assert.Equal(["A=1", "C=2", "B=3", "D=4"], await Orders(msel.Id));
    }

    [Fact]
    public async Task Update_MovingAFieldUpOne_SwapsItWithItsNeighbour()
    {
        var msel = await SeedMselWith("A", "B", "C", "D");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel, "D", to: 3);

        Assert.Equal(["A=1", "B=2", "D=3", "C=4"], await Orders(msel.Id));
    }

    [Fact]
    public async Task Update_MovingAFieldSeveralPlacesDown_ShiftsTheOnesItPassesUp()
    {
        var msel = await SeedMselWith("A", "B", "C", "D", "E");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel, "A", to: 4);

        Assert.Equal(["B=1", "C=2", "D=3", "A=4", "E=5"], await Orders(msel.Id));
    }

    [Fact]
    public async Task Update_MovingAFieldSeveralPlacesUp_ShiftsTheOnesItPassesDown()
    {
        var msel = await SeedMselWith("A", "B", "C", "D", "E");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel, "E", to: 2);

        Assert.Equal(["A=1", "E=2", "B=3", "C=4", "D=5"], await Orders(msel.Id));
    }

    [Fact]
    public async Task Update_MovingAFieldToTheHead_ShiftsEverythingDown()
    {
        var msel = await SeedMselWith("A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel, "C", to: 1);

        Assert.Equal(["C=1", "A=2", "B=3"], await Orders(msel.Id));
    }

    [Fact]
    public async Task Update_MovingAFieldPastTheEnd_IsClampedToTheCount()
    {
        var msel = await SeedMselWith("A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel, "A", to: 9);

        Assert.Equal(["B=1", "C=2", "A=3"], await Orders(msel.Id));
    }

    /// <summary>Moving a field to zero leaves position 1 empty.</summary>
    [Fact]
    public async Task Update_MovingAFieldToZero_LeavesPositionOneEmpty()
    {
        var msel = await SeedMselWith("A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel, "C", to: 0);

        Assert.Equal(["C=0", "A=2", "B=3"], await Orders(msel.Id));
    }

    /// <summary>An update that does not change <c>DisplayOrder</c> renumbers nothing, so an existing duplicate
    /// stays.</summary>
    [Fact]
    public async Task Update_WithoutChangingTheDisplayOrder_LeavesADuplicateInPlace()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(
            TestData.DataField(mselId: msel.Id, displayOrder: 1, name: "A"),
            TestData.DataField(mselId: msel.Id, displayOrder: 1, name: "B"),
            TestData.DataField(mselId: msel.Id, displayOrder: 3, name: "C"));

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var field = await Field(msel.Id, "B");
        var response = await Client(actor).PutAsJsonAsync(
            $"/api/dataFields/{field.Id}",
            BodyFor(field) with { Name = "B renamed" },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(["A=1", "B renamed=1", "C=3"], await Orders(msel.Id));
    }

    [Fact]
    public async Task Update_RenumbersOnlyTheMselTheFieldIsOn()
    {
        var msel = await SeedMselWith("A", "B", "C");
        var other = await SeedMselWith("X", "Y", "Z");

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Move(Client(actor), msel, "C", to: 1);

        Assert.Equal(["X=1", "Y=2", "Z=3"], await Orders(other.Id));
    }

    /// <summary>Moving one template renumbers every template in the installation.</summary>
    [Fact]
    public async Task Update_ATemplatesDisplayOrder_RenumbersEveryTemplateInTheInstallation()
    {
        await Seed(
            TestData.DataField(displayOrder: 1, name: "A"),
            TestData.DataField(displayOrder: 5, name: "B"),
            TestData.DataField(displayOrder: 9, name: "C"));

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        var field = await Field(null, "C");
        var response = await Client(actor).PutAsJsonAsync(
            $"/api/dataFields/{field.Id}",
            BodyFor(field) with { DisplayOrder = 1 },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(["C=1", "A=2", "B=3"], await Orders());

        Assert.Equal(
            ["A", "B", "C"],
            Hub.ToGroup(MainHub.ADMIN_DATA_GROUP)
                .Where(x => x.Method == MainHubMethods.DataFieldUpdated)
                .Select(x => ((DataField)x.Payload).Name)
                .Distinct()
                .Order(StringComparer.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------
    // Deleting
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_ClosesTheGapItLeaves()
    {
        var msel = await SeedMselWith("A", "B", "C", "D");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Delete(Client(actor), msel.Id, "B");

        Assert.Equal(["A=1", "C=2", "D=3"], await Orders(msel.Id));
    }

    [Fact]
    public async Task Delete_TheLastField_LeavesTheOthersWhereTheyWere()
    {
        var msel = await SeedMselWith("A", "B", "C");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Delete(Client(actor), msel.Id, "C");

        Assert.Equal(["A=1", "B=2"], await Orders(msel.Id));
    }

    [Fact]
    public async Task Delete_TheOnlyField_LeavesTheMselWithNone()
    {
        var msel = await SeedMselWith("A");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Delete(Client(actor), msel.Id, "A");

        Assert.Empty(await Orders(msel.Id));
    }

    /// <summary>
    /// A delete compacts the whole ordering, so it also repairs a gap and a duplicate that had nothing to
    /// do with the field being removed.
    /// </summary>
    /// <remarks>
    /// The assertion is on the positions alone rather than on which field holds which. Two of the three
    /// survivors start out sharing a number, and <c>Reorder</c> sorts on that number alone - so which of
    /// the pair ends up first is Postgres's choice, and a test that named them would be asserting the
    /// tie-break rather than the compaction.
    /// </remarks>
    [Fact]
    public async Task Delete_AlsoCompactsAGapAndADuplicateItDidNotCause()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(
            TestData.DataField(mselId: msel.Id, displayOrder: 1, name: "A"),
            TestData.DataField(mselId: msel.Id, displayOrder: 2, name: "B"),
            TestData.DataField(mselId: msel.Id, displayOrder: 2, name: "C"),
            TestData.DataField(mselId: msel.Id, displayOrder: 7, name: "D"));

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Delete(Client(actor), msel.Id, "A");

        var positions = await Positions(msel.Id);

        Assert.Equal([1, 2, 3], positions);
    }

    [Fact]
    public async Task Delete_RenumbersOnlyTheMselTheFieldWasOn()
    {
        var msel = await SeedMselWith("A", "B", "C");
        var other = await SeedMselWith("X", "Y", "Z");

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Delete(Client(actor), msel.Id, "A");

        Assert.Equal(["X=1", "Y=2", "Z=3"], await Orders(other.Id));
    }

    /// <summary>Deleting one template renumbers every template in the installation.</summary>
    [Fact]
    public async Task Delete_ATemplate_RenumbersEveryTemplateInTheInstallation()
    {
        await Seed(
            TestData.DataField(displayOrder: 1, name: "A"),
            TestData.DataField(displayOrder: 5, name: "B"),
            TestData.DataField(displayOrder: 9, name: "C"));

        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageDataFields).SeedAsync();

        await Delete(Client(actor), null, "A");

        Assert.Equal(["B=1", "C=2"], await Orders());
    }

    [Fact]
    public async Task Delete_BroadcastsAnUpdateForEveryFieldItRenumbers()
    {
        var msel = await SeedMselWith("A", "B", "C", "D");
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Delete(Client(actor), msel.Id, "B");

        Assert.Equal(
            ["C", "D"],
            Hub.ToGroup(msel.Id)
                .Where(x => x.Method == MainHubMethods.DataFieldUpdated)
                .Select(x => ((DataField)x.Payload).Name)
                .Order(StringComparer.Ordinal));

        Assert.Single(Hub.ToGroup(msel.Id), x => x.Method == MainHubMethods.DataFieldDeleted);
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The wire shape of a data field, kept to the properties these tests set. <c>DateCreated</c> and
    /// <c>CreatedBy</c> are non-nullable on <c>ViewModels.Base</c>, so they have to be sent as values -
    /// a null is a 400 that never reaches the controller.
    /// </summary>
    private sealed record FieldBody
    {
        public Guid Id { get; init; }
        public Guid? MselId { get; init; }
        public Guid? InjectTypeId { get; init; }
        public string Name { get; init; }
        public DataFieldType DataType { get; init; }
        public int DisplayOrder { get; init; }
        public bool IsTemplate { get; init; }
        public Guid CreatedBy { get; init; }
        public DateTime DateCreated { get; init; }
        public Guid? ModifiedBy { get; init; }
        public DateTime? DateModified { get; init; }
    }

    private static FieldBody BodyFor(DataFieldEntity field) => new()
    {
        Id = field.Id,
        MselId = field.MselId,
        InjectTypeId = field.InjectTypeId,
        Name = field.Name,
        DataType = field.DataType,
        DisplayOrder = field.DisplayOrder,
        IsTemplate = field.IsTemplate
    };

    /// <summary>
    /// An MSEL carrying one field per name, numbered from 1 in the order given.
    /// </summary>
    private async Task<MselEntity> SeedMselWith(params string[] names)
    {
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(names
            .Select((name, i) => (object)TestData.DataField(
                mselId: msel.Id, displayOrder: i + 1, name: name))
            .ToArray());

        return msel;
    }

    /// <summary>
    /// The fields of one MSEL, one inject type, or - given neither - the installation's templates, as
    /// <c>name=displayOrder</c> in display order. The tie-break on name is the test's, not the API's:
    /// <c>Reorder</c> sorts on <c>DisplayOrder</c> alone, so two fields sharing a number have no defined
    /// order and an assertion cannot depend on one.
    /// </summary>
    private async Task<string[]> Orders(Guid? mselId = null, Guid? injectTypeId = null)
    {
        await using var context = NewContext();

        var rows = await context.DataFields
            .AsNoTracking()
            .Where(x => x.MselId == mselId && x.InjectTypeId == injectTypeId)
            .OrderBy(x => x.DisplayOrder)
            .ThenBy(x => x.Name)
            .Select(x => new { x.Name, x.DisplayOrder })
            .ToListAsync(Ct);

        return rows.Select(x => $"{x.Name}={x.DisplayOrder}").ToArray();
    }

    /// <summary>
    /// The same set as <see cref="Orders"/>, as display orders alone - for the cases where the pairing of
    /// field to position is not determined but the shape of the ordering is.
    /// </summary>
    private async Task<int[]> Positions(Guid? mselId = null, Guid? injectTypeId = null)
    {
        await using var context = NewContext();

        return await context.DataFields
            .AsNoTracking()
            .Where(x => x.MselId == mselId && x.InjectTypeId == injectTypeId)
            .OrderBy(x => x.DisplayOrder)
            .Select(x => x.DisplayOrder)
            .ToArrayAsync(Ct);
    }

    private async Task<DataFieldEntity> Field(Guid? mselId, string name)
    {
        await using var context = NewContext();

        return await context.DataFields
            .AsNoTracking()
            .SingleAsync(x => x.MselId == mselId && x.Name == name, Ct);
    }

    private async Task Create(HttpClient client, Guid mselId, int displayOrder)
    {
        var response = await client.PostAsJsonAsync(
            "/api/dataFields",
            new FieldBody { MselId = mselId, Name = "N", DisplayOrder = displayOrder },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task Move(HttpClient client, MselEntity msel, string name, int to)
    {
        var field = await Field(msel.Id, name);

        var response = await client.PutAsJsonAsync(
            $"/api/dataFields/{field.Id}",
            BodyFor(field) with { DisplayOrder = to },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task Delete(HttpClient client, Guid? mselId, string name)
    {
        var field = await Field(mselId, name);

        var response = await client.DeleteAsync($"/api/dataFields/{field.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
