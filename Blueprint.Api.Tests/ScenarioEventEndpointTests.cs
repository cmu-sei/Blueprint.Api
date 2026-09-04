// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
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
/// Six of the eight <c>scenarioEvents</c> routes: the rows of an MSEL's timeline, each one an event the
/// exercise delivers at a point in time. The two bulk routes - <c>fromInjects</c> and <c>copy</c> - and the
/// <c>GroupOrder</c> reordering contract have files of their own.
/// </summary>
/// <remarks>
/// <para>
/// A scenario event is the row that <c>DataValueEndpointTests</c>' cells hang off, so the two files share a
/// shape and disagree about almost everything else. Reading a row is <c>MselViewRequirement</c> where
/// reading a cell list is <c>MselUserRequirement</c>; changing a row is owner-or-approver-or-editor where
/// changing a cell runs a cascade keyed on the field's type; and creating or deleting a row is
/// owner-only, which no cell operation is.
/// </para>
/// <para>
/// Four characterizations here, in rough order of how much they matter.
/// </para>
/// <para>
/// <c>UpdateAsync</c> takes its permission decision from the <em>request body's</em> <c>MselId</c> and then
/// finds the row by the route's id, so a caller who owns any MSEL at all may edit every row of every other
/// one - and the mapper writes that <c>MselId</c> onto the row, moving it (see
/// <see cref="Update_ChoosesItsPermissionBranchFromTheRequestBodyAndMovesTheRow"/>). This is the same
/// defect <c>OrganizationService.UpdateAsync</c> has, one step more serious: there the body's MSEL id is
/// nullable on both sides, here it is the row's whole identity.
/// </para>
/// <para>
/// Any PUT rewrites the <c>CellMetadata</c> of <em>every</em> cell on the row from the row's
/// <c>RowMetadata</c>, whether the caller mentioned that cell or not, so per-cell formatting cannot survive
/// an edit (<see cref="Update_OverwritesTheMetadataOfEveryCellOnTheRow"/>). The translation itself drops
/// digits, because it formats each colour component with <c>"X"</c> and never pads to two
/// (<see cref="Update_TranslatesRowMetadataIntoCellMetadata"/>): red is <c>FF00</c> rather than
/// <c>FF0000</c>.
/// </para>
/// <para>
/// <c>GetAsync</c> reads with <c>SingleAsync</c> and then tests the result for null, so an unknown id is a
/// 500 - and the throw happens before the permission check, so it is a 500 for callers who should have got
/// a 403 as well (<see cref="Get_ForAnUnknownId_Is500"/>). <c>OrganizationService</c> and
/// <c>ScenarioEventService</c> are the two services with this shape.
/// </para>
/// <para>
/// And <c>MselRole.Evaluator</c> is missing from <c>MselViewRequirement</c>'s role list, so the person
/// running a live exercise cannot read the timeline they are evaluating
/// (<see cref="GetByMsel_TheRolesThatMayReadTheTimeline"/>) although they can tick its checkboxes. Pinned
/// in <c>MselViewRequirementTests</c> as well; asserted here because this is where it is felt.
/// </para>
/// <para>
/// Two contract lies are noted rather than characterized, because <c>ContractTests</c> is where they
/// belong: <c>createScenarioEvent</c> declares 201 and answers 200 with no <c>Location</c> header, and
/// <c>deleteScenarioEvent</c> and <c>batchDeleteScenarioEvents</c> declare 204 and answer 200 with the
/// JSON literal <c>true</c>.
/// </para>
/// </remarks>
public class ScenarioEventEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET msels/{mselId}/scenarioEvents
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByMsel_ReturnsEveryEventOnTheMsel()
    {
        var msel = await SeedMsel();
        await SeedEvent(msel, information: "first");
        await SeedEvent(msel, deltaSeconds: 60, information: "second");
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var events = await GetEvents(Client(actor), msel.Id);

        Assert.Equal(
            ["first", "second"],
            events.Select(x => x.Information).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task GetByMsel_DoesNotReturnAnotherMselsEvents()
    {
        var msel = await SeedMsel();
        var mine = await SeedEvent(msel);
        await SeedEvent(await SeedMsel());
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var events = await GetEvents(Client(actor), msel.Id);

        Assert.Equal(mine.Id, Assert.Single(events).Id);
    }

    /// <summary>
    /// The timeline comes back in the order the grid draws it: by time, then by position within the
    /// events sharing a time.
    /// </summary>
    [Fact]
    public async Task GetByMsel_OrdersByDeltaSecondsThenGroupOrder()
    {
        var msel = await SeedMsel();
        await SeedEvent(msel, deltaSeconds: 100, groupOrder: 1, information: "c");
        await SeedEvent(msel, deltaSeconds: 0, groupOrder: 2, information: "b");
        await SeedEvent(msel, deltaSeconds: 0, groupOrder: 1, information: "a");
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var events = await GetEvents(Client(actor), msel.Id);

        Assert.Equal(["a", "b", "c"], events.Select(x => x.Information));
    }

    /// <summary>
    /// The list route answers rows with no cells; only the by-id route fills them in.
    /// </summary>
    /// <remarks>
    /// Characterization. <c>GetByMselAsync</c> includes <c>SteamfitterTask</c> and not <c>DataValues</c>,
    /// so a client drawing the grid from this route sees every row and no contents, and has to follow up
    /// with <c>GET msels/{id}/datavalues</c> - a route that answers to a different permission helper
    /// (<c>DataValueEndpointTests.GetByMsel_ForAUnitMemberWithNoRole_Is200_WhileTheSameValueByIdIs403</c>).
    /// Both halves are asserted here because the contrast is the finding. Turns red when the include is
    /// added.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_OmitsTheCellsThatTheByIdRouteIncludes()
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, MselRole.Viewer).SeedAsync();

        var events = await GetEvents(Client(actor), row.Msel.Id);

        Assert.Empty(Assert.Single(events).DataValues);

        var byId = await GetEvent(Client(actor), row.Event.Id);

        Assert.Equal(row.Value.Id, Assert.Single(byId.DataValues).Id);
    }

    /// <summary>
    /// Every MSEL role reads the timeline except the one whose job is to watch it happen.
    /// </summary>
    /// <remarks>
    /// Characterization of the <c>Evaluator</c> row. <c>MselViewRequirement</c> lists five of the six
    /// roles, and the missing one is the role an exercise's evaluators hold - who can therefore change a
    /// checkbox on a row (<c>DataValueEndpointTests.Update_ThePermissionCascade</c>) that they cannot
    /// read. Turns red when the role list is completed.
    /// </remarks>
    [Theory]
    [InlineData(MselRole.Owner, HttpStatusCode.OK)]
    [InlineData(MselRole.Editor, HttpStatusCode.OK)]
    [InlineData(MselRole.Approver, HttpStatusCode.OK)]
    [InlineData(MselRole.MoveEditor, HttpStatusCode.OK)]
    [InlineData(MselRole.Viewer, HttpStatusCode.OK)]
    [InlineData(MselRole.Evaluator, HttpStatusCode.Forbidden)]
    public async Task GetByMsel_TheRolesThatMayReadTheTimeline(MselRole role, HttpStatusCode expected)
    {
        var msel = await SeedMsel();
        await SeedEvent(msel);
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/scenarioEvents", Ct);

        Assert.Equal(expected, response.StatusCode);
    }

    /// <summary>
    /// A member of one of the MSEL's teams reads the timeline holding no role at all, which is what makes
    /// it visible to the people playing the exercise.
    /// </summary>
    [Fact]
    public async Task GetByMsel_ForAMemberOfOneOfTheMselsTeams_Is200()
    {
        var msel = await SeedMsel();
        var scenarioEvent = await SeedEvent(msel);
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);
        var actor = await Actor().OnTeam(team).SeedAsync();

        var events = await GetEvents(Client(actor), msel.Id);

        Assert.Equal(scenarioEvent.Id, Assert.Single(events).Id);
    }

    /// <summary>
    /// Membership of a unit the MSEL is assigned to is not enough on its own, unlike on the cell list.
    /// </summary>
    [Fact]
    public async Task GetByMsel_ForAUnitMemberWithNoRole_Is403()
    {
        var msel = await SeedMsel();
        await SeedEvent(msel);
        var actor = await Actor().SeedAsync();
        await Db.AddUnitMembershipAsync(actor.Id, msel.Id, Ct);

        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/scenarioEvents", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetByMsel_WithNoRelationshipToTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/scenarioEvents", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetByMsel_WithViewMsels_Is200()
    {
        var msel = await SeedMsel();
        var scenarioEvent = await SeedEvent(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var events = await GetEvents(Client(actor), msel.Id);

        Assert.Equal(scenarioEvent.Id, Assert.Single(events).Id);
    }

    /// <summary>
    /// <c>CreateMsels</c> reads a template's timeline and nothing else's, which is the only reason this
    /// route resolves two system permissions instead of one.
    /// </summary>
    /// <remarks>
    /// Both halves in one test: the permission is worth nothing on an ordinary MSEL, and it is the
    /// four-argument <c>MselViewRequirement.IsMet</c> overload - used by this route and by the by-id route
    /// and by nothing else - that makes the template case work.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_ForATemplate_WithCreateMselsOnly_Is200_WhileAnOrdinaryMselIs403()
    {
        var template = await SeedMsel(isTemplate: true);
        await SeedEvent(template);
        var ordinary = await SeedMsel();
        await SeedEvent(ordinary);
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var events = await GetEvents(Client(actor), template.Id);

        Assert.Single(events);

        var response = await Client(actor).GetAsync($"/api/msels/{ordinary.Id}/scenarioEvents", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// An unknown MSEL is a 403 here, where the cell list answers 500 for the same request: this route's
    /// permission helper guards its lookup and that one's does not.
    /// </summary>
    [Fact]
    public async Task GetByMsel_ForAnUnknownMsel_Is403()
    {
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{Guid.NewGuid()}/scenarioEvents", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetByMsel_ForAnUnknownMsel_WithViewMsels_IsAnEmptyList()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var events = await GetEvents(Client(actor), Guid.NewGuid());

        Assert.Empty(events);
    }

    // ---------------------------------------------------------------------------------------------
    // GET scenarioEvents/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ReturnsTheRowAndItsCells()
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, MselRole.Viewer).SeedAsync();

        var scenarioEvent = await GetEvent(Client(actor), row.Event.Id);

        Assert.Equal(row.Msel.Id, scenarioEvent.MselId);
        Assert.Equal(row.Event.Information, scenarioEvent.Information);
        Assert.Equal(EventType.Inject, scenarioEvent.ScenarioEventType);

        var cell = Assert.Single(scenarioEvent.DataValues);

        Assert.Equal(row.Value.Id, cell.Id);
        Assert.Equal("before", cell.Value);
    }

    /// <summary>
    /// An unknown id is a 500, and it is a 500 before anybody's permissions are considered.
    /// </summary>
    /// <remarks>
    /// Characterization. <c>GetAsync</c> reads with <c>SingleAsync</c>, whose exception for no rows is an
    /// <c>InvalidOperationException</c> rather than a null, so both the service's own null check and the
    /// controller's are unreachable. The lookup also runs before the permission check, so a caller with no
    /// relationship to the installation learns nothing from a 500 that they would not have learned from a
    /// 403 - the point of the pairing is that the ordering makes the status independent of who asks. Turns
    /// red when the read becomes <c>SingleOrDefaultAsync</c>.
    /// </remarks>
    [Fact]
    public async Task Get_ForAnUnknownId_Is500()
    {
        var stranger = await Actor().SeedAsync();
        var admin = await Actor().WithAllSystemPermissions().SeedAsync();
        var id = Guid.NewGuid();

        Assert.Equal(
            HttpStatusCode.InternalServerError,
            (await Client(stranger).GetAsync($"/api/scenarioEvents/{id}", Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.InternalServerError,
            (await Client(admin).GetAsync($"/api/scenarioEvents/{id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Get_WithNoRelationshipToTheMsel_Is403()
    {
        var row = await SeedRow();
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync($"/api/scenarioEvents/{row.Event.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_ForAMemberOfOneOfTheMselsTeams_Is200()
    {
        var row = await SeedRow();
        var team = BlueprintAppFactory.Team(row.Msel.Id);
        await Seed(team);
        var actor = await Actor().OnTeam(team).SeedAsync();

        var scenarioEvent = await GetEvent(Client(actor), row.Event.Id);

        Assert.Equal(row.Event.Id, scenarioEvent.Id);
    }

    [Fact]
    public async Task Get_WithViewMsels_Is200()
    {
        var row = await SeedRow();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var scenarioEvent = await GetEvent(Client(actor), row.Event.Id);

        Assert.Equal(row.Event.Id, scenarioEvent.Id);
    }

    [Fact]
    public async Task Get_ForTheMselsCreator_Is200()
    {
        var creator = await Actor().SeedAsync();
        var row = await SeedRow(createdBy: creator.Id);

        var scenarioEvent = await GetEvent(Client(creator), row.Event.Id);

        Assert.Equal(row.Event.Id, scenarioEvent.Id);
    }

    // ---------------------------------------------------------------------------------------------
    // POST scenarioEvents
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_StoresTheRow()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(
            Client(actor),
            Body(msel.Id, deltaSeconds: 300, information: "new row") with
            {
                IsHidden = true,
                IntegrationTarget = "gallery"
            });

        var created = Assert.Single(await Read<List<ScenarioEvent>>(response));
        var stored = await Stored(created.Id);

        Assert.Equal(msel.Id, stored.MselId);
        Assert.Equal("new row", stored.Information);
        Assert.Equal(300, stored.DeltaSeconds);
        Assert.True(stored.IsHidden);
        Assert.Equal("gallery", stored.IntegrationTarget);
        Assert.Equal(EventType.Inject, stored.ScenarioEventType);
    }

    /// <summary>
    /// The create answers 200 with a list and no <c>Location</c> header, having declared 201.
    /// </summary>
    /// <remarks>
    /// Characterization of the contract rather than of the behaviour, and deliberately not a defect to fix
    /// here: the list is the point of the route - a create can renumber its siblings, so the response is
    /// everything that changed - and a single-resource 201 could not carry it. What is wrong is the
    /// declaration. <c>ContractTests</c> is where this is pinned for the generated clients;
    /// <c>createScenarioEventsFromInjects</c> and <c>copyScenarioEventsToMsel</c> are the same.
    /// </remarks>
    [Fact]
    public async Task Create_Is200WithAListAndNoLocationHeader()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Single(await Read<List<ScenarioEvent>>(response));
    }

    /// <summary>
    /// Adding a row is owner-only: the roles that may change a row are not the roles that may add one.
    /// </summary>
    [Theory]
    [InlineData(MselRole.Owner, HttpStatusCode.OK)]
    [InlineData(MselRole.Editor, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Approver, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.MoveEditor, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Viewer, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Evaluator, HttpStatusCode.Forbidden)]
    public async Task Create_TheRolesThatMayAddARow(MselRole role, HttpStatusCode expected)
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id));

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(expected == HttpStatusCode.OK ? 1 : 0, await CountOn(msel.Id));
    }

    [Fact]
    public async Task Create_ForTheMselsCreator_Is200()
    {
        var creator = await Actor().SeedAsync();
        var msel = await SeedMsel(createdBy: creator.Id);

        var response = await Post(Client(creator), Body(msel.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithEditMsels_Is200()
    {
        var msel = await SeedMsel();
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// An MSEL that does not exist is a 500, not a 404 or a 403.
    /// </summary>
    /// <remarks>
    /// Characterization. <c>MselOwnerRequirement.IsMet</c> reads <c>.CreatedBy</c> off the result of a
    /// <c>FirstOrDefaultAsync</c>, so an id matching nothing is a null reference rather than a refusal.
    /// Turns red when the helper guards its lookup - at which point this becomes the 403 it should always
    /// have been. A caller holding <c>EditMsels</c> gets a 500 too, from the foreign key, which is why
    /// only the permission path is asserted.
    /// </remarks>
    [Fact]
    public async Task Create_ForAnUnknownMsel_Is500()
    {
        var actor = await Actor().SeedAsync();

        var response = await Post(Client(actor), Body(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// A new row arrives with one cell per data field on the MSEL, whether the caller filled it in or not,
    /// so the grid is rectangular from the moment the row exists.
    /// </summary>
    [Fact]
    public async Task Create_AddsACellForEveryDataFieldOnTheMsel()
    {
        var msel = await SeedMsel();
        var first = BlueprintAppFactory.DataField(mselId: msel.Id, displayOrder: 1);
        var second = BlueprintAppFactory.DataField(mselId: msel.Id, displayOrder: 2);
        var third = BlueprintAppFactory.DataField(mselId: msel.Id, displayOrder: 3);
        await Seed(first, second, third);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(
            Client(actor),
            Body(msel.Id) with
            {
                DataValues = [new ValueBody { DataFieldId = second.Id, Value = "typed" }]
            });
        var created = Assert.Single(await Read<List<ScenarioEvent>>(response));
        var cells = await CellsOn(created.Id);
        var byField = cells.ToDictionary(x => x.DataFieldId, x => x.Value);

        Assert.Equal(3, byField.Count);
        Assert.Null(byField[first.Id]);
        Assert.Equal("typed", byField[second.Id]);
        Assert.Null(byField[third.Id]);
        Assert.All(cells, x => Assert.Equal(actor.Id, x.CreatedBy));
    }

    /// <summary>
    /// A cell for a data field belonging to another MSEL is dropped rather than written or refused.
    /// </summary>
    /// <remarks>
    /// Characterization, and the benign counterpart of
    /// <c>DataValueEndpointTests.Update_MovesTheValueIntoAnotherMselsCell</c>: here the service walks the
    /// MSEL's own data fields and only reads the body where one matches, so a foreign field silently
    /// contributes nothing. Turns red if the service ever validates the body instead.
    /// </remarks>
    [Fact]
    public async Task Create_IgnoresACellForAnotherMselsDataField()
    {
        var msel = await SeedMsel();
        var mine = BlueprintAppFactory.DataField(mselId: msel.Id);
        var elsewhere = BlueprintAppFactory.DataField(mselId: (await SeedMsel()).Id);
        await Seed(mine, elsewhere);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(
            Client(actor),
            Body(msel.Id) with
            {
                DataValues =
                [
                    new ValueBody { DataFieldId = mine.Id, Value = "kept" },
                    new ValueBody { DataFieldId = elsewhere.Id, Value = "dropped" }
                ]
            });
        var created = Assert.Single(await Read<List<ScenarioEvent>>(response));
        var cell = Assert.Single(await CellsOn(created.Id));

        Assert.Equal(mine.Id, cell.DataFieldId);
        Assert.Equal("kept", cell.Value);
    }

    [Fact]
    public async Task Create_StampsTheAuditFieldsOnTheServer()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var before = DateTime.UtcNow;

        var response = await Post(
            Client(actor),
            Body(msel.Id) with
            {
                CreatedBy = Guid.NewGuid(),
                DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ModifiedBy = Guid.NewGuid(),
                DateModified = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });
        var stored = await Stored(Assert.Single(await Read<List<ScenarioEvent>>(response)).Id);

        Assert.Equal(actor.Id, stored.CreatedBy);
        Assert.InRange(stored.DateCreated, before, DateTime.UtcNow);
        Assert.Null(stored.ModifiedBy);
        Assert.Null(stored.DateModified);
    }

    [Fact]
    public async Task Create_MarksTheMselModified()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var before = DateTime.UtcNow;

        await Post(Client(actor), Body(msel.Id));

        var stored = await StoredMsel(msel.Id);

        Assert.Equal(actor.Id, stored.ModifiedBy);
        Assert.NotNull(stored.DateModified);
        Assert.InRange(stored.DateModified.Value, before, DateTime.UtcNow);
    }

    /// <summary>
    /// The client may choose the row's id, which is how a grid that has already drawn a row can address
    /// its cells before the server has answered.
    /// </summary>
    [Fact]
    public async Task Create_WithAnIdInTheBody_UsesIt()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var id = Guid.NewGuid();

        var response = await Post(Client(actor), Body(msel.Id, id: id));
        var created = Assert.Single(await Read<List<ScenarioEvent>>(response));

        Assert.Equal(id, created.Id);
        Assert.NotNull(await Stored(id));
    }

    /// <summary>
    /// Choosing an id that is already taken is a 500 rather than a 409.
    /// </summary>
    /// <remarks>
    /// Characterization. Nothing checks the chosen id, so the primary key does - and a client retrying a
    /// create it is not sure went through gets a server error rather than an answer it can act on. Turns
    /// red when the service looks the id up first.
    /// </remarks>
    [Fact]
    public async Task Create_WithTheIdOfAnExistingRow_Is500()
    {
        var msel = await SeedMsel();
        var existing = await SeedEvent(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id, id: existing.Id, deltaSeconds: 60));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Create_BroadcastsToTheMselAndTheAdminGroup()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id, information: "broadcast"));
        var created = Assert.Single(await Read<List<ScenarioEvent>>(response));

        Assert.Equal(
            [msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.ScenarioEventCreated));

        var sent = (ScenarioEvent)Hub.Of(MainHubMethods.ScenarioEventCreated)[0].Payload;

        Assert.Equal(created.Id, sent.Id);
        Assert.Equal("broadcast", sent.Information);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT scenarioEvents/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_StoresTheRowAndStampsTheAuditFields()
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, MselRole.Editor).SeedAsync();
        var before = DateTime.UtcNow;

        var response = await Put(
            Client(actor),
            row.Event.Id,
            BodyFor(row.Event) with
            {
                Information = "after",
                IsHidden = true,
                ModifiedBy = Guid.NewGuid(),
                DateModified = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await Stored(row.Event.Id);

        Assert.Equal("after", stored.Information);
        Assert.True(stored.IsHidden);
        Assert.Equal(actor.Id, stored.ModifiedBy);
        Assert.NotNull(stored.DateModified);
        Assert.InRange(stored.DateModified.Value, before, DateTime.UtcNow);
        Assert.Equal(row.Event.CreatedBy, stored.CreatedBy);
    }

    /// <summary>
    /// Changing a row is owner-or-approver-or-editor, so the two roles that cannot add or remove a row can
    /// rewrite every one that exists.
    /// </summary>
    [Theory]
    [InlineData(MselRole.Owner, HttpStatusCode.OK)]
    [InlineData(MselRole.Approver, HttpStatusCode.OK)]
    [InlineData(MselRole.Editor, HttpStatusCode.OK)]
    [InlineData(MselRole.MoveEditor, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Viewer, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Evaluator, HttpStatusCode.Forbidden)]
    public async Task Update_TheRolesThatMayChangeARow(MselRole role, HttpStatusCode expected)
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, role).SeedAsync();

        var response = await Put(
            Client(actor),
            row.Event.Id,
            BodyFor(row.Event) with { Information = "after" });

        Assert.Equal(expected, response.StatusCode);

        if (expected == HttpStatusCode.Forbidden)
        {
            Assert.Equal("No update permissions", (await ReadError(response)).Title);
        }

        Assert.Equal(
            expected == HttpStatusCode.OK ? "after" : row.Event.Information,
            (await Stored(row.Event.Id)).Information);
    }

    [Fact]
    public async Task Update_WithEditMsels_Is200()
    {
        var row = await SeedRow();
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Put(
            Client(actor),
            row.Event.Id,
            BodyFor(row.Event) with { Information = "after" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Update_ForAnUnknownId_Is404()
    {
        var msel = await SeedMsel();
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Put(Client(actor), Guid.NewGuid(), Body(msel.Id, id: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Anybody who owns any MSEL may rewrite any row of any other one, and doing so moves the row onto
    /// their MSEL.
    /// </summary>
    /// <remarks>
    /// Characterization, and the most serious finding on this endpoint. The three permission tests all run
    /// against the <em>request body's</em> <c>MselId</c>; the row is then found by the route's id and the
    /// body mapped over it, <c>MselId</c> included. So the caller here holds no role on the MSEL the row
    /// belongs to, is refused nothing, and ends up with the row - and its cells, which still point at the
    /// old MSEL's data fields - on theirs. Only the destination MSEL is marked modified, so the MSEL that
    /// lost a row has nothing recording that it changed. Turns red when the permission check uses the
    /// stored row's MSEL, which is also what makes the move impossible.
    /// </remarks>
    [Fact]
    public async Task Update_ChoosesItsPermissionBranchFromTheRequestBodyAndMovesTheRow()
    {
        var row = await SeedRow();
        var mine = await SeedMsel();
        var actor = await Actor().OnMsel(mine, MselRole.Owner).SeedAsync();

        var response = await Put(
            Client(actor),
            row.Event.Id,
            BodyFor(row.Event) with { MselId = mine.Id, Information = "taken" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await Stored(row.Event.Id);

        Assert.Equal(mine.Id, stored.MselId);
        Assert.Equal("taken", stored.Information);
        Assert.Equal(row.Field.Id, Assert.Single(await CellsOn(row.Event.Id)).DataFieldId);
        Assert.NotNull((await StoredMsel(mine.Id)).DateModified);
        Assert.Null((await StoredMsel(row.Msel.Id)).DateModified);
    }

    /// <summary>
    /// A body carrying an id other than the route's is a 500.
    /// </summary>
    /// <remarks>
    /// Characterization, the same shape as <c>DataValueEndpointTests.Update_WithNoIdInTheBody_Is500</c>:
    /// the route id finds the row and the body is then mapped over it including its key, so EF is asked to
    /// change a primary key and refuses. Turns red when the service forces the route id onto the body.
    /// </remarks>
    [Fact]
    public async Task Update_WithADifferentIdInTheBody_Is500()
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, MselRole.Editor).SeedAsync();

        var response = await Put(
            Client(actor),
            row.Event.Id,
            BodyFor(row.Event) with { Id = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Update_StoresTheValuesOfTheCellsOnTheRow()
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, MselRole.Editor).SeedAsync();

        var response = await Put(
            Client(actor),
            row.Event.Id,
            BodyFor(row.Event) with { DataValues = [Cell(row.Value) with { Value = "after" }] });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var cell = Assert.Single(await CellsOn(row.Event.Id));

        Assert.Equal("after", cell.Value);
        Assert.Equal(actor.Id, cell.ModifiedBy);
    }

    /// <summary>
    /// A cell in the body that does not name its scenario event is ignored, and the request still answers
    /// 200.
    /// </summary>
    /// <remarks>
    /// Characterization. The body's cells are matched with
    /// <c>SingleOrDefault(dv =&gt; dv.ScenarioEventId == scenarioEvent.Id &amp;&amp; ...)</c>, so a client
    /// that sends a cell as the pair it thinks it is - a field and a value - has its edit accepted and
    /// discarded. Nothing distinguishes the response from the one that saved it. Turns red when the cells
    /// are matched on the data field alone, or the mismatch refused.
    /// </remarks>
    [Fact]
    public async Task Update_ACellThatDoesNotNameItsRow_IsIgnored()
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, MselRole.Editor).SeedAsync();

        var response = await Put(
            Client(actor),
            row.Event.Id,
            BodyFor(row.Event) with
            {
                DataValues = [Cell(row.Value) with { ScenarioEventId = null, Value = "after" }]
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("before", Assert.Single(await CellsOn(row.Event.Id)).Value);
    }

    /// <summary>
    /// A PUT fills in a cell the row never had, which is how a data field added after the row was created
    /// gets a value.
    /// </summary>
    [Fact]
    public async Task Update_AddsACellTheRowDidNotHave()
    {
        var row = await SeedRow();
        var added = BlueprintAppFactory.DataField(mselId: row.Msel.Id, displayOrder: 2);
        await Seed(added);
        var actor = await Actor().OnMsel(row.Msel, MselRole.Editor).SeedAsync();

        var response = await Put(
            Client(actor),
            row.Event.Id,
            BodyFor(row.Event) with
            {
                DataValues =
                [
                    new ValueBody
                    {
                        DataFieldId = added.Id,
                        ScenarioEventId = row.Event.Id,
                        Value = "typed"
                    }
                ]
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var cells = await CellsOn(row.Event.Id);

        Assert.Equal(2, cells.Count);

        var cell = cells.Single(x => x.DataFieldId == added.Id);

        Assert.Equal("typed", cell.Value);
        Assert.Equal(actor.Id, cell.CreatedBy);
        Assert.Equal("FFFFFF,0,normal,0", cell.CellMetadata);
    }

    /// <summary>
    /// Every cell on the row has its formatting rewritten from the row's, including the cells the caller
    /// said nothing about.
    /// </summary>
    /// <remarks>
    /// Characterization. <c>UpdateAsync</c> computes one <c>cellMetadata</c> string from the row's
    /// <c>RowMetadata</c> and then walks every data field on the MSEL, writing it over whatever each cell
    /// held. So per-cell formatting - which <c>PUT dataValues/{id}</c> exists to set, and which the grid
    /// offers - survives only until somebody edits the row it is on. Turns red when the row's metadata
    /// stops being applied to cells that already have their own.
    /// </remarks>
    [Fact]
    public async Task Update_OverwritesTheMetadataOfEveryCellOnTheRow()
    {
        var row = await SeedRow();
        row.Value.CellMetadata = "FF0000,0.7,bold,0";
        await Db.SaveChangesAsync(Ct);
        var actor = await Actor().OnMsel(row.Msel, MselRole.Editor).SeedAsync();

        var response = await Put(
            Client(actor),
            row.Event.Id,
            BodyFor(row.Event) with { Information = "after" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var cell = Assert.Single(await CellsOn(row.Event.Id));

        Assert.Equal("FFFFFF,0,normal,0", cell.CellMetadata);
        Assert.Equal("before", cell.Value);
    }

    /// <summary>
    /// The row's <c>RowMetadata</c> - a height and three colour components - as the <c>CellMetadata</c>
    /// every cell on the row is given.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Characterization, in the third and fourth rows. Each colour component is formatted with <c>"X"</c>
    /// and never padded, so a component below 16 contributes one digit instead of two and the colour that
    /// comes out is not the colour that went in: pure red arrives as <c>FF00</c>, which a CSS parser reads
    /// as transparent-ish or discards. Only a row whose three components are all 16 or more survives.
    /// Turns red when the format string becomes <c>"X2"</c>.
    /// </para>
    /// <para>
    /// The rest is the shape of the thing and is correct as written, if surprising: the height is
    /// discarded, anything that is not four comma-separated parts becomes white, and the two no-metadata
    /// cases differ from every other case in the second field - <c>0</c> rather than <c>0.7</c> - so a row
    /// that has never been formatted is distinguishable from one formatted white.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(null, "FFFFFF,0,normal,0")]
    [InlineData("", "FFFFFF,0,normal,0")]
    [InlineData("20,255,255,255", "FFFFFF,0.7,normal,0")]
    [InlineData("20,255,0,0", "FF00,0.7,normal,0")]
    [InlineData("20,0,0,0", "000,0.7,normal,0")]
    [InlineData("20,1,2,3", "123,0.7,normal,0")]
    [InlineData("20,10,11,12", "ABC,0.7,normal,0")]
    [InlineData("20,255,255", "FFFFFF,0.7,normal,0")]
    [InlineData("nonsense", "FFFFFF,0.7,normal,0")]
    public async Task Update_TranslatesRowMetadataIntoCellMetadata(string rowMetadata, string expected)
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, MselRole.Editor).SeedAsync();

        var response = await Put(
            Client(actor),
            row.Event.Id,
            BodyFor(row.Event) with { RowMetadata = rowMetadata });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, Assert.Single(await CellsOn(row.Event.Id)).CellMetadata);
    }

    /// <summary>
    /// Four parts that are not four numbers is a 500.
    /// </summary>
    /// <remarks>
    /// Characterization. <c>int.Parse</c> throws on anything but a number, and the row metadata is a free
    /// text column no request has ever had to justify. Turns red when the parse becomes a
    /// <c>TryParse</c>.
    /// </remarks>
    [Fact]
    public async Task Update_WithNonNumericRowMetadata_Is500()
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, MselRole.Editor).SeedAsync();

        var response = await Put(
            Client(actor),
            row.Event.Id,
            BodyFor(row.Event) with { RowMetadata = "20,red,green,blue" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// Two cells for one data field in the body is a 500.
    /// </summary>
    /// <remarks>
    /// Characterization. The body's cells are looked up with <c>SingleOrDefault</c>, which throws for two
    /// matches - and the database accepts two rows for one cell
    /// (<c>DataValueEndpointTests.Create_ASecondValueForTheSameCell_IsAccepted</c>), so a row that has
    /// acquired a duplicate cannot be updated at all: the same <c>SingleOrDefaultAsync</c> two lines above
    /// throws on the stored side. Turns red when duplicates are refused or tolerated.
    /// </remarks>
    [Fact]
    public async Task Update_WithTwoCellsForOneDataField_Is500()
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, MselRole.Editor).SeedAsync();

        var response = await Put(
            Client(actor),
            row.Event.Id,
            BodyFor(row.Event) with
            {
                DataValues =
                [
                    Cell(row.Value) with { Value = "one" },
                    Cell(row.Value) with { Id = Guid.NewGuid(), Value = "two" }
                ]
            });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// The update broadcast names the properties that changed, camel-cased for the client.
    /// </summary>
    [Fact]
    public async Task Update_BroadcastsToTheMselAndTheAdminGroup()
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, MselRole.Editor).SeedAsync();

        await Put(Client(actor), row.Event.Id, BodyFor(row.Event) with { Information = "after" });

        Assert.Equal(
            [row.Msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.ScenarioEventUpdated));

        var send = Hub.Of(MainHubMethods.ScenarioEventUpdated)[0];
        var sent = (ScenarioEvent)send.Payload;

        Assert.Equal(row.Event.Id, sent.Id);
        Assert.Equal("after", sent.Information);
        Assert.Contains("information", (string[])send.Args[1]);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE scenarioEvents/{id}
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The delete answers 200 with the JSON literal <c>true</c>, having declared 204.
    /// </summary>
    /// <remarks>
    /// Characterization of the contract. The generated client declares a <c>void</c> return and reads the
    /// body of a 204, so a 200 with a body is the case it does not expect - and the value carries no
    /// information, because every other outcome is an exception. <c>batchDeleteScenarioEvents</c> is the
    /// same. Pinned again in <c>ContractTests</c>.
    /// </remarks>
    [Fact]
    public async Task Delete_RemovesTheRowAndAnswers200WithTrue()
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/scenarioEvents/{row.Event.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await Read<bool>(response));
        Assert.Null(await Stored(row.Event.Id));
    }

    /// <summary>
    /// The row's cells go with it, and nothing is broadcast about them.
    /// </summary>
    /// <remarks>
    /// Characterization. The cells are removed by the foreign key's <c>ON DELETE CASCADE</c>, which the
    /// change tracker never sees, so no <c>DataValueDeleted</c> entity event is published and a client
    /// holding the grid in memory is told the row went and not its contents. Harmless for a client that
    /// drops the cells with the row; wrong for one that keeps them by id. Turns red when the service loads
    /// and removes the cells itself.
    /// </remarks>
    [Fact]
    public async Task Delete_RemovesTheCellsWithoutBroadcastingIt()
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, MselRole.Owner).SeedAsync();

        await Client(actor).DeleteAsync($"/api/scenarioEvents/{row.Event.Id}", Ct);

        Assert.Empty(await CellsOn(row.Event.Id));
        Assert.Empty(Hub.Of(MainHubMethods.DataValueDeleted));
    }

    [Fact]
    public async Task Delete_ForAnUnknownId_Is404()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/scenarioEvents/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Removing a row is owner-only, like adding one - so an editor may empty every cell on a row and not
    /// remove it.
    /// </summary>
    [Theory]
    [InlineData(MselRole.Owner, HttpStatusCode.OK)]
    [InlineData(MselRole.Editor, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Approver, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.MoveEditor, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Viewer, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Evaluator, HttpStatusCode.Forbidden)]
    public async Task Delete_TheRolesThatMayRemoveARow(MselRole role, HttpStatusCode expected)
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, role).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/scenarioEvents/{row.Event.Id}", Ct);

        Assert.Equal(expected, response.StatusCode);

        if (expected == HttpStatusCode.OK)
        {
            Assert.Null(await Stored(row.Event.Id));
        }
        else
        {
            Assert.NotNull(await Stored(row.Event.Id));
        }
    }

    [Fact]
    public async Task Delete_WithEditMsels_Is200()
    {
        var row = await SeedRow();
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/scenarioEvents/{row.Event.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Delete_MarksTheMselModified()
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, MselRole.Owner).SeedAsync();
        var before = DateTime.UtcNow;

        await Client(actor).DeleteAsync($"/api/scenarioEvents/{row.Event.Id}", Ct);

        var stored = await StoredMsel(row.Msel.Id);

        Assert.Equal(actor.Id, stored.ModifiedBy);
        Assert.NotNull(stored.DateModified);
        Assert.InRange(stored.DateModified.Value, before, DateTime.UtcNow);
    }

    [Fact]
    public async Task Delete_BroadcastsTheIdToTheMselAndTheAdminGroup()
    {
        var row = await SeedRow();
        var actor = await Actor().OnMsel(row.Msel, MselRole.Owner).SeedAsync();

        await Client(actor).DeleteAsync($"/api/scenarioEvents/{row.Event.Id}", Ct);

        Assert.Equal(
            [row.Msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.ScenarioEventDeleted));
        Assert.Equal(row.Event.Id, Hub.Of(MainHubMethods.ScenarioEventDeleted)[0].Payload);
    }

    // ---------------------------------------------------------------------------------------------
    // POST scenarioEvents/batchDelete
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task BatchDelete_RemovesEveryRow()
    {
        var msel = await SeedMsel();
        var first = await SeedEvent(msel);
        var second = await SeedEvent(msel, deltaSeconds: 60);
        var kept = await SeedEvent(msel, deltaSeconds: 120);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await BatchDelete(Client(actor), first.Id, second.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await Read<bool>(response));
        Assert.Equal(kept.Id, (await Stored(kept.Id)).Id);
        Assert.Null(await Stored(first.Id));
        Assert.Null(await Stored(second.Id));
    }

    /// <summary>
    /// Rows from two MSELs is a 500, and removes neither.
    /// </summary>
    /// <remarks>
    /// Characterization of the status only. Refusing the mixed list is deliberate and right - the caller's
    /// permission is checked against one MSEL, so a mixed batch would be a hole - but the refusal is an
    /// <c>ArgumentException</c>, which <c>JsonExceptionFilter</c> has no case for and answers 500. Turns
    /// red when the exception becomes one that implements <c>IApiException</c>.
    /// </remarks>
    [Fact]
    public async Task BatchDelete_ForRowsOnTwoMsels_Is500_AndRemovesNothing()
    {
        var msel = await SeedMsel();
        var mine = await SeedEvent(msel);
        var elsewhere = await SeedEvent(await SeedMsel());
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await BatchDelete(Client(actor), mine.Id, elsewhere.Id);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.NotNull(await Stored(mine.Id));
        Assert.NotNull(await Stored(elsewhere.Id));
    }

    /// <summary>
    /// One unknown id refuses the whole batch, which is what makes the route safe to retry.
    /// </summary>
    [Fact]
    public async Task BatchDelete_WithAnUnknownId_Is404_AndRemovesNothing()
    {
        var msel = await SeedMsel();
        var scenarioEvent = await SeedEvent(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await BatchDelete(Client(actor), scenarioEvent.Id, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotNull(await Stored(scenarioEvent.Id));
    }

    /// <summary>
    /// An empty list is a 404, from a lookup of the all-zeros MSEL id.
    /// </summary>
    /// <remarks>
    /// Characterization. <c>BatchDeleteAsync</c> derives the MSEL to mark modified from the first row in
    /// the list, so an empty list leaves it <see cref="Guid.Empty"/> and
    /// <c>ServiceUtilities.SetMselModifiedAsync</c> - whose only guard is against <c>null</c> - looks that
    /// up and throws for the MSEL it cannot find. Note what the route did before reaching it: nothing, and
    /// no permission check either, because the check is inside the loop over the list. So the 404 is the
    /// only thing standing between an unauthenticated-in-all-but-name caller and a 200 for a request that
    /// does nothing. Turns red when the empty list is refused, or short-circuited, or the MSEL update
    /// skipped.
    /// </remarks>
    [Fact]
    public async Task BatchDelete_WithAnEmptyList_Is404()
    {
        var actor = await Actor().SeedAsync();

        var response = await BatchDelete(Client(actor));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task BatchDelete_ForAnEditor_Is403()
    {
        var msel = await SeedMsel();
        var scenarioEvent = await SeedEvent(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var response = await BatchDelete(Client(actor), scenarioEvent.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(await Stored(scenarioEvent.Id));
    }

    [Fact]
    public async Task BatchDelete_BroadcastsOneDeletionPerRow()
    {
        var msel = await SeedMsel();
        var first = await SeedEvent(msel);
        var second = await SeedEvent(msel, deltaSeconds: 60);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        await BatchDelete(Client(actor), first.Id, second.Id);

        var deletions = Hub.Of(MainHubMethods.ScenarioEventDeleted);

        Assert.Equal(
            [first.Id, second.Id],
            deletions.Where(x => x.Group == msel.Id.ToString()).Select(x => (Guid)x.Payload));
        Assert.Equal(
            [msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.ScenarioEventDeleted));
    }

    // ---------------------------------------------------------------------------------------------
    // Authentication
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "msels/00000000-0000-0000-0000-000000000001/scenarioEvents")]
    [InlineData("GET", "scenarioEvents/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "scenarioEvents")]
    [InlineData("POST", "scenarioEvents/fromInjects")]
    [InlineData("POST", "msels/00000000-0000-0000-0000-000000000001/scenarioEvents/copy")]
    [InlineData("PUT", "scenarioEvents/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "scenarioEvents/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "scenarioEvents/batchDelete")]
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
    /// One row of an MSEL's timeline, with one data field and the one cell where they meet.
    /// </summary>
    private sealed record Row(
        MselEntity Msel,
        ScenarioEventEntity Event,
        DataFieldEntity Field,
        DataValueEntity Value);

    /// <summary>
    /// The wire shape of a scenario event. A record rather than an anonymous type so a test can vary one
    /// property of a stored row with a <c>with</c> expression. <c>DateCreated</c> and <c>CreatedBy</c> are
    /// non-nullable on <c>ViewModels.Base</c>, so they are sent as values: a null is a 400 that never
    /// reaches the controller.
    /// </summary>
    private sealed record EventBody
    {
        public Guid Id { get; init; }
        public Guid MselId { get; init; }
        public int DeltaSeconds { get; init; }
        public int GroupOrder { get; init; }
        public bool IsHidden { get; init; }
        public string RowMetadata { get; init; }
        public EventType ScenarioEventType { get; init; }
        public Guid? InjectId { get; init; }
        public string IntegrationTarget { get; init; }
        public string Information { get; init; }
        public ValueBody[] DataValues { get; init; } = [];

        public Guid CreatedBy { get; init; }
        public DateTime DateCreated { get; init; }
        public Guid? ModifiedBy { get; init; }
        public DateTime? DateModified { get; init; }
    }

    /// <summary>
    /// The wire shape of one cell, as it arrives nested inside a row.
    /// </summary>
    private sealed record ValueBody
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

    private static EventBody Body(
        Guid mselId,
        int deltaSeconds = 0,
        string information = "posted by the test",
        Guid? id = null) =>
        new()
        {
            Id = id ?? Guid.Empty,
            MselId = mselId,
            DeltaSeconds = deltaSeconds,
            ScenarioEventType = EventType.Inject,
            Information = information
        };

    private static EventBody BodyFor(ScenarioEventEntity scenarioEvent) => new()
    {
        Id = scenarioEvent.Id,
        MselId = scenarioEvent.MselId,
        DeltaSeconds = scenarioEvent.DeltaSeconds,
        GroupOrder = scenarioEvent.GroupOrder,
        IsHidden = scenarioEvent.IsHidden,
        RowMetadata = scenarioEvent.RowMetadata,
        ScenarioEventType = scenarioEvent.ScenarioEventType,
        InjectId = scenarioEvent.InjectId,
        IntegrationTarget = scenarioEvent.IntegrationTarget,
        Information = scenarioEvent.Information
    };

    private static ValueBody Cell(DataValueEntity dataValue) => new()
    {
        Id = dataValue.Id,
        Value = dataValue.Value,
        ScenarioEventId = dataValue.ScenarioEventId,
        InjectId = dataValue.InjectId,
        DataFieldId = dataValue.DataFieldId,
        CellMetadata = dataValue.CellMetadata
    };

    private async Task<MselEntity> SeedMsel(Guid? createdBy = null, bool isTemplate = false)
    {
        var msel = BlueprintAppFactory.Msel(createdBy: createdBy, isTemplate: isTemplate);
        await Seed(msel);

        return msel;
    }

    private async Task<ScenarioEventEntity> SeedEvent(
        MselEntity msel,
        int deltaSeconds = 0,
        int groupOrder = 1,
        string information = null)
    {
        var scenarioEvent = BlueprintAppFactory.ScenarioEvent(msel.Id, deltaSeconds, groupOrder);

        if (information is not null)
        {
            scenarioEvent.Information = information;
        }

        await Seed(scenarioEvent);

        return scenarioEvent;
    }

    private async Task<Row> SeedRow(Guid? createdBy = null)
    {
        var msel = await SeedMsel(createdBy);
        var field = BlueprintAppFactory.DataField(mselId: msel.Id);
        var scenarioEvent = BlueprintAppFactory.ScenarioEvent(msel.Id);
        await Seed(field, scenarioEvent);

        var dataValue = BlueprintAppFactory.DataValue(field.Id, scenarioEvent.Id, "before");
        await Seed(dataValue);

        return new Row(msel, scenarioEvent, field, dataValue);
    }

    private Task<HttpResponseMessage> Post(HttpClient client, EventBody body) =>
        client.PostAsJsonAsync("/api/scenarioEvents", body, Ct);

    private Task<HttpResponseMessage> Put(HttpClient client, Guid id, EventBody body) =>
        client.PutAsJsonAsync($"/api/scenarioEvents/{id}", body, Ct);

    private Task<HttpResponseMessage> BatchDelete(HttpClient client, params Guid[] ids) =>
        client.PostAsJsonAsync("/api/scenarioEvents/batchDelete", ids, Ct);

    private async Task<List<ScenarioEvent>> GetEvents(HttpClient client, Guid mselId)
    {
        var response = await client.GetAsync($"/api/msels/{mselId}/scenarioEvents", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<List<ScenarioEvent>>(response);
    }

    private async Task<ScenarioEvent> GetEvent(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/scenarioEvents/{id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<ScenarioEvent>(response);
    }

    private async Task<ScenarioEventEntity> Stored(Guid id)
    {
        await using var context = NewContext();

        return await context.ScenarioEvents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, Ct);
    }

    private async Task<MselEntity> StoredMsel(Guid id)
    {
        await using var context = NewContext();

        return await context.Msels.AsNoTracking().SingleAsync(x => x.Id == id, Ct);
    }

    private async Task<List<DataValueEntity>> CellsOn(Guid scenarioEventId)
    {
        await using var context = NewContext();

        return await context.DataValues
            .AsNoTracking()
            .Where(x => x.ScenarioEventId == scenarioEventId)
            .ToListAsync(Ct);
    }

    private async Task<int> CountOn(Guid mselId)
    {
        await using var context = NewContext();

        return await context.ScenarioEvents.CountAsync(x => x.MselId == mselId, Ct);
    }

    private async Task<ApiError> ReadError(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions, Ct);

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }
}
