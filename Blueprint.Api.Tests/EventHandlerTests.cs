// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using AutoMapper;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using Blueprint.Api.Tests.Infrastructure;
using Crucible.Common.EntityEvents.Events;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// The 24 handler families under <c>Infrastructure/EventHandlers</c>: which groups each one addresses,
/// what it puts on the wire, and what it does when the row it broadcasts about is not there.
/// </summary>
/// <remarks>
/// Every handler takes only a <see cref="BlueprintContext"/>, an <see cref="IMapper"/>, a service it never
/// reads and an <c>IHubContext&lt;MainHub&gt;</c>, so no host is needed: the handler is constructed
/// directly over this test's database with <see cref="HubRecorder"/> in the hub's place, and the
/// notification is handed to it by reflection. Passing <c>null</c> for the service argument is itself the
/// assertion that no handler reads it.
/// <para />
/// Group names are the whole contract with a subscribing client - <c>MainHub.Join</c> puts a connection in
/// a group per MSEL, a group per unit and one named by its own user id, and <c>JoinAdmin</c> adds the four
/// constants - so "who is told" is what these tests are about. <see cref="MainHubTests"/> covers the other
/// half, which groups a caller is put into.
/// </remarks>
public class EventHandlerTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    private readonly HubRecorder _hub = new();

    // -------------------------------------------------------------------------------------------------
    // Who is told
    // -------------------------------------------------------------------------------------------------

    /// <remarks>
    /// The six families whose entity carries the MSEL id and that re-read nothing. This is the shape the
    /// whole set is a variation on: the MSEL's own group, plus the admin group, which gets everything.
    /// </remarks>
    [Fact]
    public async Task TheMselScopedFamilies_TellTheMselsGroupAndTheAdminGroup()
    {
        var mselId = Guid.NewGuid();

        await Created(BlueprintAppFactory.Organization(mselId));
        await Created(BlueprintAppFactory.Card(mselId));
        await Created(BlueprintAppFactory.Move(mselId));
        await Created(BlueprintAppFactory.ScenarioEvent(mselId));
        await Created(BlueprintAppFactory.PlayerApplication(mselId));
        await Created(BlueprintAppFactory.UserMselRole(Guid.NewGuid(), mselId));

        AssertTold(MainHubMethods.OrganizationCreated, mselId.ToString(), MainHub.ADMIN_DATA_GROUP);
        AssertTold(MainHubMethods.CardCreated, mselId.ToString(), MainHub.ADMIN_DATA_GROUP);
        AssertTold(MainHubMethods.MoveCreated, mselId.ToString(), MainHub.ADMIN_DATA_GROUP);
        AssertTold(MainHubMethods.ScenarioEventCreated, mselId.ToString(), MainHub.ADMIN_DATA_GROUP);
        AssertTold(MainHubMethods.PlayerApplicationCreated, mselId.ToString(), MainHub.ADMIN_DATA_GROUP);
        AssertTold(MainHubMethods.UserMselRoleCreated, mselId.ToString(), MainHub.ADMIN_DATA_GROUP);
    }

    /// <remarks>
    /// The four families that re-read the row before broadcasting it, to pick up navigations the saving
    /// request did not load. They address the same two groups, chosen from the entity they were handed
    /// rather than from what the re-read found.
    /// </remarks>
    [Fact]
    public async Task TheFamiliesThatReReadTheRow_TellTheMselsGroupAndTheAdminGroup()
    {
        var msel = BlueprintAppFactory.Msel();
        var citeAction = BlueprintAppFactory.CiteAction(msel.Id);
        var citeDuty = BlueprintAppFactory.CiteDuty(msel.Id);
        var dataField = BlueprintAppFactory.DataField(msel.Id);
        await Seed(msel, citeAction, citeDuty, dataField);

        await Created(msel);
        await Created(citeAction);
        await Created(citeDuty);
        await Created(dataField);

        AssertTold(MainHubMethods.MselCreated, msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP);
        AssertTold(MainHubMethods.CiteActionCreated, msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP);
        AssertTold(MainHubMethods.CiteDutyCreated, msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP);
        AssertTold(MainHubMethods.DataFieldCreated, msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP);
    }

    /// <remarks>
    /// The three families whose entity has no MSEL id of its own, so the handler looks one up. All three
    /// queries drop the <c>CancellationToken</c>, and <c>DataValueHandler</c>'s is synchronous - a blocking
    /// database call on whichever thread the save completed on.
    /// </remarks>
    [Fact]
    public async Task TheFamiliesThatLookTheMselUp_TellTheGroupTheyFound()
    {
        var msel = BlueprintAppFactory.Msel();
        var card = BlueprintAppFactory.Card(msel.Id);
        var playerApplication = BlueprintAppFactory.PlayerApplication(msel.Id);
        var scenarioEvent = BlueprintAppFactory.ScenarioEvent(msel.Id);
        await Seed(msel, card, playerApplication, scenarioEvent);

        await Created(BlueprintAppFactory.CardTeam(card.Id, Guid.NewGuid()));
        await Created(BlueprintAppFactory.PlayerApplicationTeam(playerApplication.Id, Guid.NewGuid()));
        await Created(BlueprintAppFactory.DataValue(Guid.NewGuid(), scenarioEvent.Id));

        AssertTold(MainHubMethods.CardTeamCreated, msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP);
        AssertTold(
            MainHubMethods.PlayerApplicationTeamCreated, msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP);
        AssertTold(MainHubMethods.DataValueCreated, msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP);
    }

    /// <remarks>
    /// A team is addressed by its own id as well, because <c>MainHub</c> puts nobody in a team group - so
    /// that name reaches no connection and the MSEL group is what carries the message.
    /// </remarks>
    [Fact]
    public async Task TheTeamFamilies_TellTheTeamTheMselAndTheAdminGroup()
    {
        var msel = BlueprintAppFactory.Msel();
        var team = BlueprintAppFactory.Team(msel.Id);
        var user = BlueprintAppFactory.User();
        var teamUser = BlueprintAppFactory.TeamUser(user.Id, team.Id);
        await Seed(msel, team, user, teamUser);

        await Created(team);
        await Created(teamUser);

        AssertTold(
            MainHubMethods.TeamCreated,
            team.Id.ToString(),
            msel.Id.ToString(),
            MainHub.ADMIN_DATA_GROUP);
        AssertTold(
            MainHubMethods.TeamUserCreated,
            team.Id.ToString(),
            user.Id.ToString(),
            MainHub.ADMIN_DATA_GROUP);
    }

    /// <remarks>
    /// The unit families are the only ones that fan out: a unit is reusable across MSELs, so the handler
    /// queries <c>MselUnits</c> and names every MSEL the unit is assigned to. Both delete handlers pass
    /// <c>CancellationToken.None</c> to that query rather than the token they were given.
    /// </remarks>
    [Fact]
    public async Task TheUnitFamilies_TellTheUnitTheAdminGroupAndEveryMselTheUnitIsOn()
    {
        var msel = BlueprintAppFactory.Msel();
        var unit = BlueprintAppFactory.Unit();
        var user = BlueprintAppFactory.User();
        var unitUser = BlueprintAppFactory.UnitUser(user.Id, unit.Id);
        await Seed(msel, unit, user, unitUser, BlueprintAppFactory.MselUnit(unit.Id, msel.Id));

        await Created(unit);
        await Created(unitUser);

        AssertTold(
            MainHubMethods.UnitCreated,
            unit.Id.ToString(),
            MainHub.ADMIN_DATA_GROUP,
            msel.Id.ToString());
        AssertTold(
            MainHubMethods.UnitUserCreated,
            unit.Id.ToString(),
            user.Id.ToString(),
            MainHub.ADMIN_DATA_GROUP,
            msel.Id.ToString());
    }

    /// <remarks>
    /// BUG: a catalog is told to the units it is shared with and to nobody else - not to a group named by
    /// the catalog, which no handler ever addresses, so a client watching a catalog it is not shared
    /// through learns nothing about it. A catalog shared with no unit is broadcast to the admin group
    /// alone.
    /// </remarks>
    [Fact]
    public async Task TheCatalogFamily_TellsTheUnitsItIsSharedWithAndNeverTheCatalog()
    {
        var injectType = BlueprintAppFactory.InjectType();
        var unit = BlueprintAppFactory.Unit();
        var catalog = BlueprintAppFactory.Catalog(injectType.Id);
        await Seed(injectType, unit, catalog, BlueprintAppFactory.CatalogUnit(unit.Id, catalog.Id));

        await Created(catalog);

        AssertTold(MainHubMethods.CatalogCreated, unit.Id.ToString(), MainHub.ADMIN_DATA_GROUP);
    }

    /// <remarks>
    /// BUG: an inject and an inject type are told to the admin group alone, so the content-developer
    /// screens are the only ones that ever hear about either. An inject is the reusable unit a catalog is
    /// built from, and its own catalog's units are not told - unlike a catalog's, which are.
    /// </remarks>
    [Fact]
    public async Task TheInjectFamilies_TellNobodyButTheAdminGroup()
    {
        var injectType = BlueprintAppFactory.InjectType();

        await Created(injectType);
        await Created(BlueprintAppFactory.Inject(injectType.Id));

        AssertTold(MainHubMethods.InjectTypeCreated, MainHub.ADMIN_DATA_GROUP);
        AssertTold(MainHubMethods.InjectCreated, MainHub.ADMIN_DATA_GROUP);
    }

    /// <remarks>
    /// BUG: the group named is the *creator's* own id, not the user's, and never
    /// <c>MainHub.USER_GROUP</c> - which exists for exactly this and is joined by every holder of
    /// <c>ViewUsers</c>. So the administration screen listing users is not told when one is added, renamed
    /// or deleted, while whoever happened to create the row is told about it on their personal group.
    /// Auto-provisioned users are worse still: <c>UserClaimsService</c> saves them with no creator, so the
    /// broadcast goes to a group named by all-zeros.
    /// </remarks>
    [Fact]
    public async Task TheUserFamily_TellsTheCreatorsOwnGroupAndNotTheUserAdminGroup()
    {
        var createdBy = Guid.NewGuid();

        await Created(BlueprintAppFactory.User(createdBy));

        AssertTold(MainHubMethods.UserCreated, createdBy.ToString(), MainHub.ADMIN_DATA_GROUP);
        Assert.DoesNotContain(MainHub.USER_GROUP, _hub.Recipients(MainHubMethods.UserCreated));
    }

    /// <remarks>
    /// The three families that govern the whole installation rather than one exercise, and the only three
    /// with no base class: each addresses one of <c>MainHub</c>'s constants and not the admin data group.
    /// </remarks>
    [Fact]
    public async Task TheInstallationWideFamilies_TellTheirOwnAdminGroup()
    {
        var group = BlueprintAppFactory.Group();
        var user = BlueprintAppFactory.User();
        await Seed(group, user);

        await Created(BlueprintAppFactory.SystemRole());
        await Created(group);
        await Created(BlueprintAppFactory.GroupMembership(group.Id, user.Id));

        AssertTold(MainHubMethods.SystemRoleCreated, MainHub.ROLE_GROUP);
        AssertTold(MainHubMethods.GroupCreated, MainHub.GROUP_GROUP);
        AssertTold(MainHubMethods.GroupMembershipCreated, MainHub.GROUP_GROUP);
    }

    // -------------------------------------------------------------------------------------------------
    // What goes on the wire
    // -------------------------------------------------------------------------------------------------

    /// <remarks>
    /// Three arguments: the view model, the property names that changed, and nothing else. A create passes
    /// <c>null</c> for the second, which is how blueprint.ui tells a create from an update arriving on a
    /// group it has just joined.
    /// </remarks>
    [Fact]
    public async Task ACreate_NamesNoModifiedProperties_AndAnUpdateCamelCasesThem()
    {
        var organization = BlueprintAppFactory.Organization(Guid.NewGuid());

        await Created(organization);
        await Updated(organization, "Name", "MselId");

        Assert.Null(_hub.Of(MainHubMethods.OrganizationCreated)[0].Args[1]);
        Assert.Equal(
            new[] { "name", "mselId" },
            (string[])_hub.Of(MainHubMethods.OrganizationUpdated)[0].Args[1]);
    }

    /// <remarks>
    /// A delete carries the id alone, there being nothing left to send - except in the two families below.
    /// </remarks>
    [Fact]
    public async Task ADelete_SendsTheIdAlone()
    {
        var organization = BlueprintAppFactory.Organization(Guid.NewGuid());

        await Deleted(organization);

        var send = _hub.Of(MainHubMethods.OrganizationDeleted)[0];
        Assert.Equal(organization.Id, send.Payload);
        Assert.Single(send.Args);
    }

    /// <remarks>
    /// BUG: 22 of the 24 families send an id on delete and these two send the whole entity, so a client
    /// cannot read the two messages the same way. Both also broadcast the *entity* on create and update
    /// where every other family maps a view model first - which is why both files inject an
    /// <see cref="IMapper"/> they never use.
    /// </remarks>
    [Fact]
    public async Task TheMembershipFamilies_SendTheWholeEntityWhereEveryOtherFamilySendsAViewModel()
    {
        var msel = BlueprintAppFactory.Msel();
        var team = BlueprintAppFactory.Team(msel.Id);
        var unit = BlueprintAppFactory.Unit();
        var user = BlueprintAppFactory.User();
        var teamUser = BlueprintAppFactory.TeamUser(user.Id, team.Id);
        var unitUser = BlueprintAppFactory.UnitUser(user.Id, unit.Id);
        await Seed(msel, team, unit, user, teamUser, unitUser);

        await Created(teamUser);
        await Created(unitUser);
        await Deleted(teamUser);
        await Deleted(unitUser);

        Assert.IsType<TeamUserEntity>(_hub.Of(MainHubMethods.TeamUserCreated)[0].Payload);
        Assert.IsType<UnitUserEntity>(_hub.Of(MainHubMethods.UnitUserCreated)[0].Payload);
        Assert.IsType<TeamUserEntity>(_hub.Of(MainHubMethods.TeamUserDeleted)[0].Payload);
        Assert.IsType<UnitUserEntity>(_hub.Of(MainHubMethods.UnitUserDeleted)[0].Payload);
    }

    /// <remarks>
    /// BUG: the three installation-wide families send two arguments where the other 21 send three, so a
    /// client handler written against the common shape reads the cancellation-token slot for the modified
    /// property names - and a role or group update cannot say what changed about it.
    /// </remarks>
    [Fact]
    public async Task TheInstallationWideFamilies_NameNoModifiedPropertiesEvenOnAnUpdate()
    {
        var systemRole = BlueprintAppFactory.SystemRole();

        await Updated(systemRole, "Name");

        Assert.Single(_hub.Of(MainHubMethods.SystemRoleUpdated)[0].Args);
    }

    // -------------------------------------------------------------------------------------------------
    // When the row is not there
    // -------------------------------------------------------------------------------------------------

    /// <remarks>
    /// BUG: five entities carry a nullable <c>MselId</c>, and a row without one is broadcast to a group
    /// named by the empty string - a name no connection can be in, so the message is dropped and only the
    /// admin group hears about a template. <c>CardTeamHandler</c> reaches the same state by another route:
    /// its lookup selects a <c>Guid?</c>, so a card that is gone yields null rather than all-zeros.
    /// </remarks>
    [Fact]
    public async Task ARowWithNoMsel_IsBroadcastToAGroupNamedByTheEmptyString()
    {
        var dataField = BlueprintAppFactory.DataField();
        await Seed(dataField);

        await Created(BlueprintAppFactory.Organization());
        await Created(BlueprintAppFactory.Card(null));
        await Created(dataField);
        await Created(BlueprintAppFactory.CardTeam(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Contains(string.Empty, _hub.Recipients(MainHubMethods.OrganizationCreated));
        Assert.Contains(string.Empty, _hub.Recipients(MainHubMethods.CardCreated));
        Assert.Contains(string.Empty, _hub.Recipients(MainHubMethods.DataFieldCreated));
        Assert.Contains(string.Empty, _hub.Recipients(MainHubMethods.CardTeamCreated));
    }

    /// <remarks>
    /// BUG: the other two lookups select a non-nullable <c>Guid</c>, so a missing parent yields all-zeros
    /// and the broadcast is addressed to a group named by it. Two ways of spelling "nobody", neither
    /// logged.
    /// </remarks>
    [Fact]
    public async Task ARowWhoseParentIsGone_IsBroadcastToTheAllZeroGuidsGroup()
    {
        await Created(BlueprintAppFactory.DataValue(Guid.NewGuid(), Guid.NewGuid()));
        await Created(BlueprintAppFactory.PlayerApplicationTeam(Guid.NewGuid(), Guid.NewGuid()));

        AssertTold(
            MainHubMethods.DataValueCreated, Guid.Empty.ToString(), MainHub.ADMIN_DATA_GROUP);
        AssertTold(
            MainHubMethods.PlayerApplicationTeamCreated,
            Guid.Empty.ToString(),
            MainHub.ADMIN_DATA_GROUP);
    }

    /// <remarks>
    /// BUG: four of the families that re-read the row dereference the result without checking it, so a row
    /// deleted between the save and the broadcast is a <see cref="NullReferenceException"/> raised inside
    /// MediatR - which means inside <c>SaveChanges</c>, so the request that saved the row is answered 500
    /// although its write committed. <c>MselHandler</c> is the one that guards (it broadcasts nothing
    /// instead) and <c>DataFieldHandler</c> the one that maps the null (it broadcasts nothing *useful*
    /// instead), both below.
    /// </remarks>
    [Fact]
    public async Task TheFamiliesThatReReadTheRow_ThrowWhenItIsGone()
    {
        var mselId = Guid.NewGuid();

        await Assert.ThrowsAsync<NullReferenceException>(
            () => Created(BlueprintAppFactory.CiteAction(mselId)));
        await Assert.ThrowsAsync<NullReferenceException>(
            () => Created(BlueprintAppFactory.CiteDuty(mselId)));
        await Assert.ThrowsAsync<NullReferenceException>(
            () => Created(BlueprintAppFactory.TeamUser(Guid.NewGuid(), Guid.NewGuid())));
        await Assert.ThrowsAsync<NullReferenceException>(
            () => Created(BlueprintAppFactory.UnitUser(Guid.NewGuid(), Guid.NewGuid())));
    }

    /// <remarks>
    /// BUG: <c>TeamUserHandler</c> re-reads the row including only <c>User</c>, then dereferences five
    /// navigations off <c>Team</c> - so whether a team-user broadcast succeeds depends on whether the
    /// request that saved the row also happened to load the team into the same change tracker. It does
    /// today, which is why nothing has noticed. <c>UnitUserHandler</c> is the same shape with the guards
    /// written, so its broadcast carries a null <c>Unit</c> rather than throwing.
    /// </remarks>
    [Fact]
    public async Task ATeamUser_IsOnlyBroadcastableWhileItsTeamIsTracked()
    {
        var msel = BlueprintAppFactory.Msel();
        var team = BlueprintAppFactory.Team(msel.Id);
        var user = BlueprintAppFactory.User();
        var tracked = BlueprintAppFactory.TeamUser(user.Id, team.Id);
        await Seed(msel, team, user, tracked);

        // A second member of the same team, written by somebody else's request.
        var stranger = BlueprintAppFactory.User();
        var untracked = BlueprintAppFactory.TeamUser(stranger.Id, team.Id);
        await SeedCold(stranger, untracked);

        await Created(tracked);
        await using (var cold = NewContext())
        {
            await Assert.ThrowsAsync<NullReferenceException>(() => Created(untracked, cold));
        }

        Assert.Equal(3, _hub.Of(MainHubMethods.TeamUserCreated).Count);
    }

    /// <remarks>
    /// The one guarded re-read in the set. A MSEL deleted between the save and the broadcast is silence
    /// rather than a 500 - which is the right half of the trade and leaves every connected client showing
    /// an exercise that is gone until they reload.
    /// </remarks>
    [Fact]
    public async Task AMselThatIsGone_IsBroadcastToNobody()
    {
        await Created(BlueprintAppFactory.Msel());

        Assert.Empty(_hub.Sends);
    }

    /// <remarks>
    /// BUG: <c>DataFieldHandler</c> maps whatever the re-read found, so a column deleted between the save
    /// and the broadcast is a message with a <c>null</c> payload - which blueprint.ui reads as a data field
    /// with no id.
    /// </remarks>
    [Fact]
    public async Task ADataFieldThatIsGone_IsBroadcastAsANullPayload()
    {
        await Created(BlueprintAppFactory.DataField(Guid.NewGuid()));

        Assert.Equal(2, _hub.Of(MainHubMethods.DataFieldCreated).Count);
        Assert.Null(_hub.Of(MainHubMethods.DataFieldCreated)[0].Payload);
    }

    /// <remarks>
    /// BUG: the MSEL re-read filters <c>UserMselRoles</c> to the acting user, so the roles a client is
    /// told about depend on who saved the MSEL. The UI's list of who may do what on an exercise is
    /// therefore replaced, on every update, with a list of one - and a client that renders it loses every
    /// other participant's role until it reloads the MSEL by id.
    /// </remarks>
    [Fact]
    public async Task AMselIsBroadcastWithTheActingUsersRolesAlone()
    {
        var actor = BlueprintAppFactory.User();
        var participant = BlueprintAppFactory.User();
        var msel = BlueprintAppFactory.Msel(actor.Id);
        await SeedCold(
            actor,
            participant,
            msel,
            BlueprintAppFactory.UserMselRole(actor.Id, msel.Id),
            BlueprintAppFactory.UserMselRole(participant.Id, msel.Id));

        await using var cold = NewContext();
        await Created(msel, cold);

        var broadcast = Assert.IsType<ViewModels.Msel>(_hub.Of(MainHubMethods.MselCreated)[0].Payload);
        Assert.Equal(actor.Id, Assert.Single(broadcast.UserMselRoles).UserId);
    }

    // -------------------------------------------------------------------------------------------------
    // The set of handlers itself
    // -------------------------------------------------------------------------------------------------

    /// <remarks>
    /// Every family implements all three events. A handler added for one and not the others would be the
    /// easiest kind of gap to ship, and the hardest to see from a client: the entity would simply stop
    /// appearing or stop disappearing.
    /// </remarks>
    [Fact]
    public void EveryHandledEntity_HasAHandlerForAllThreeEvents()
    {
        var created = HandledEntities(typeof(EntityCreated<>));

        Assert.Equal(24, created.Count);
        Assert.Equal(created, HandledEntities(typeof(EntityUpdated<>)));
        Assert.Equal(created, HandledEntities(typeof(EntityDeleted<>)));
    }

    /// <remarks>
    /// BUG: <c>BlueprintContext</c> carries <c>[GenerateEntityEventInterfaces]</c>, so all 43 of its
    /// entities raise created, updated and deleted events on every save - and 19 of them are handled by
    /// nothing, so a client is never told they changed. Several matter: an <c>InvitationEntity</c> is how a
    /// unit joins an exercise, a <c>MselPageEntity</c> is exercise content with its own editor, and a
    /// <c>UserTeamRoleEntity</c> is what CITE is pushed from. The pinned list is the inventory, not an
    /// endorsement - deleting a name from it is the test for adding a handler.
    /// </remarks>
    [Fact]
    public void NineteenOfTheFortyThreeEntities_RaiseEventsNoHandlerReceives()
    {
        var tracked = typeof(BlueprintContext).GetProperties()
            .Where(x => x.PropertyType.IsGenericType
                && x.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(x => x.PropertyType.GetGenericArguments()[0].Name)
            .ToList();

        var unhandled = tracked.Except(HandledEntities(typeof(EntityCreated<>)))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(43, tracked.Count);
        Assert.Equal(
            new[]
            {
                "CatalogInjectEntity",
                "CatalogUnitEntity",
                "CompetencyEntity",
                "CompetencyFrameworkEntity",
                "CompetencyRelationshipEntity",
                "DataOptionEntity",
                "InvitationEntity",
                "MselCompetencyEntity",
                "MselPageEntity",
                "MselTeamEntity",
                "MselUnitEntity",
                "PermissionEntity",
                "ProficiencyLevelEntity",
                "ProficiencyScaleEntity",
                "SteamfitterTaskEntity",
                "TeamCompetencyEntity",
                "UserPermissionEntity",
                "UserTeamRoleEntity",
                "XApiQueuedStatementEntity"
            },
            unhandled);
    }

    /// <remarks>
    /// BUG: <c>MainHubMethods</c> declares three method names for an entity no handler serves, so
    /// blueprint.ui may subscribe to <c>MselTeamCreated</c> and wait for a message nothing sends. It is
    /// the inverse of the gap above and the two are unrelated: <c>MselUnitEntity</c>, which is what
    /// actually assigns a unit to an exercise, has neither a handler nor a method name.
    /// </remarks>
    [Fact]
    public void ThreeBroadcastMethodNames_AreDeclaredForAnEntityNoHandlerServes()
    {
        var handled = HandledEntities(typeof(EntityCreated<>));

        Assert.DoesNotContain("MselTeamEntity", handled);
        Assert.Equal("MselTeamCreated", MainHubMethods.MselTeamCreated);
        Assert.Equal("MselTeamUpdated", MainHubMethods.MselTeamUpdated);
        Assert.Equal("MselTeamDeleted", MainHubMethods.MselTeamDeleted);
    }

    // -------------------------------------------------------------------------------------------------
    // Driving a handler
    // -------------------------------------------------------------------------------------------------

    private Task Created(object entity, BlueprintContext db = null) =>
        Publish(typeof(EntityCreated<>), entity, null, db);

    private Task Updated(object entity, params string[] modifiedProperties) =>
        Publish(typeof(EntityUpdated<>), entity, modifiedProperties, null);

    private Task Deleted(object entity) => Publish(typeof(EntityDeleted<>), entity, null, null);

    /// <summary>
    /// Constructs the one handler registered for this entity and event and invokes it.
    /// </summary>
    /// <remarks>
    /// Reflection rather than a generic method, because a handler's entity type is what varies from case to
    /// case and a type parameter would have to be spelt at every call site. The notification types are
    /// concrete classes with primary constructors, so <see cref="Activator"/> can build them.
    /// </remarks>
    private async Task Publish(
        Type openNotificationType, object entity, string[] modifiedProperties, BlueprintContext db)
    {
        var notificationType = openNotificationType.MakeGenericType(entity.GetType());
        var notification = openNotificationType == typeof(EntityUpdated<>)
            ? Activator.CreateInstance(notificationType, entity, modifiedProperties)
            : Activator.CreateInstance(notificationType, entity);
        var handlerInterface = typeof(INotificationHandler<>).MakeGenericType(notificationType);
        var handler = Construct(HandlerFor(handlerInterface), db ?? Db);

        Task handling;
        try
        {
            handling = (Task)handlerInterface.GetMethod("Handle")
                .Invoke(handler, [notification, Ct]);
        }
        catch (TargetInvocationException ex)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();

            throw;
        }

        await handling;
    }

    private static Type HandlerFor(Type handlerInterface) =>
        typeof(Startup).Assembly.GetTypes()
            .Single(x => !x.IsAbstract && handlerInterface.IsAssignableFrom(x));

    /// <remarks>
    /// The service argument is <c>null</c> deliberately: every handler in the set injects one and not one
    /// of them reads it, so a handler that starts to fails here with a
    /// <see cref="NullReferenceException"/> naming the member it reached for.
    /// </remarks>
    private object Construct(Type handlerType, BlueprintContext db)
    {
        var constructor = handlerType.GetConstructors().Single();
        var arguments = constructor.GetParameters()
            .Select(x =>
                x.ParameterType == typeof(BlueprintContext) ? db :
                x.ParameterType == typeof(IMapper) ? TestMapper.Value :
                x.ParameterType == typeof(IHubContext<MainHub>) ? _hub :
                (object)null)
            .ToArray();

        return constructor.Invoke(arguments);
    }

    /// <summary>The entity type names handled for one of the three events, sorted.</summary>
    private static List<string> HandledEntities(Type openNotificationType) =>
        typeof(Startup).Assembly.GetTypes()
            .SelectMany(x => x.GetInterfaces())
            .Where(x => x.IsGenericType
                && x.GetGenericTypeDefinition() == typeof(INotificationHandler<>))
            .Select(x => x.GetGenericArguments()[0])
            .Where(x => x.IsGenericType && x.GetGenericTypeDefinition() == openNotificationType)
            .Select(x => x.GetGenericArguments()[0].Name)
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

    /// <summary>Seeds through a context of its own, so <see cref="Db"/> is not tracking the rows.</summary>
    /// <remarks>
    /// Which matters to the two handlers that depend on what the saving request happened to load: the MSEL
    /// re-read's role filter, and <c>TeamUserHandler</c>'s dereferences off a team it never included.
    /// </remarks>
    private async Task SeedCold(params object[] entities)
    {
        await using var context = NewContext();
        context.AddRange(entities);
        await context.SaveChangesAsync(Ct);
    }

    /// <summary>Asserts the exact set of groups addressed under one method name.</summary>
    private void AssertTold(string method, params string[] groups)
    {
        var expected = groups.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var actual = _hub.Recipients(method).OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.Equal(expected, actual);
    }
}
