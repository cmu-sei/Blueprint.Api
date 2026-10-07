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
using Blueprint.Api.Tests.Support;
using Crucible.Common.EntityEvents.Events;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Infrastructure.EventHandlers;

/// <summary>The 24 handler families under <c>Infrastructure/EventHandlers</c>: which groups each one
/// addresses, what it puts on the wire, and what it does when the row it broadcasts about is not
/// there.</summary>
public class EventHandlerTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    private readonly HubRecorder<MainHub> _hub = new();

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

        await Created(TestData.Organization(mselId));
        await Created(TestData.Card(mselId));
        await Created(TestData.Move(mselId));
        await Created(TestData.ScenarioEvent(mselId));
        await Created(TestData.PlayerApplication(mselId));
        await Created(TestData.UserMselRole(Guid.NewGuid(), mselId));

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
        var msel = TestData.Msel();
        var citeAction = TestData.CiteAction(msel.Id);
        var citeDuty = TestData.CiteDuty(msel.Id);
        var dataField = TestData.DataField(msel.Id);
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

    /// <summary>The three families whose entity has no MSEL id look the MSEL up and tell its group and the
    /// admin group.</summary>
    [Fact]
    public async Task TheFamiliesThatLookTheMselUp_TellTheGroupTheyFound()
    {
        var msel = TestData.Msel();
        var card = TestData.Card(msel.Id);
        var playerApplication = TestData.PlayerApplication(msel.Id);
        var scenarioEvent = TestData.ScenarioEvent(msel.Id);
        await Seed(msel, card, playerApplication, scenarioEvent);

        await Created(TestData.CardTeam(card.Id, Guid.NewGuid()));
        await Created(TestData.PlayerApplicationTeam(playerApplication.Id, Guid.NewGuid()));
        await Created(TestData.DataValue(Guid.NewGuid(), scenarioEvent.Id));

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
        var msel = TestData.Msel();
        var team = TestData.Team(msel.Id);
        var user = TestData.User();
        var teamUser = TestData.TeamUser(user.Id, team.Id);
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

    /// <summary>A unit is told to its own group, the admin group and every MSEL the unit is assigned
    /// to.</summary>
    [Fact]
    public async Task TheUnitFamilies_TellTheUnitTheAdminGroupAndEveryMselTheUnitIsOn()
    {
        var msel = TestData.Msel();
        var unit = TestData.Unit();
        var user = TestData.User();
        var unitUser = TestData.UnitUser(user.Id, unit.Id);
        await Seed(msel, unit, user, unitUser, TestData.MselUnit(unit.Id, msel.Id));

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

    /// <summary>A catalog is told to the units it is shared with and the admin group.</summary>
    [Fact]
    public async Task TheCatalogFamily_TellsTheUnitsItIsSharedWithAndNeverTheCatalog()
    {
        var injectType = TestData.InjectType();
        var unit = TestData.Unit();
        var catalog = TestData.Catalog(injectType.Id);
        await Seed(injectType, unit, catalog, TestData.CatalogUnit(unit.Id, catalog.Id));

        await Created(catalog);

        AssertTold(MainHubMethods.CatalogCreated, unit.Id.ToString(), MainHub.ADMIN_DATA_GROUP);
    }

    /// <summary>The inject families tell nobody but the admin group.</summary>
    [Fact]
    public async Task TheInjectFamilies_TellNobodyButTheAdminGroup()
    {
        var injectType = TestData.InjectType();

        await Created(injectType);
        await Created(TestData.Inject(injectType.Id));

        AssertTold(MainHubMethods.InjectTypeCreated, MainHub.ADMIN_DATA_GROUP);
        AssertTold(MainHubMethods.InjectCreated, MainHub.ADMIN_DATA_GROUP);
    }

    /// <summary>The user family tells the creators own group and not the user admin group.</summary>
    [Fact]
    public async Task TheUserFamily_TellsTheCreatorsOwnGroupAndNotTheUserAdminGroup()
    {
        var createdBy = Guid.NewGuid();

        await Created(TestData.User(createdBy));

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
        var group = TestData.Group();
        var user = TestData.User();
        await Seed(group, user);

        await Created(TestData.SystemRole());
        await Created(group);
        await Created(TestData.GroupMembership(group.Id, user.Id));

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
        var organization = TestData.Organization(Guid.NewGuid());

        await Created(organization);
        await Updated(organization, "Name", "MselId");

        Assert.Null(_hub.Of(MainHubMethods.OrganizationCreated)[0].Arguments[1]);
        Assert.Equal(
            new[] { "name", "mselId" },
            (string[])_hub.Of(MainHubMethods.OrganizationUpdated)[0].Arguments[1]);
    }

    /// <remarks>
    /// A delete carries the id alone, there being nothing left to send - except in the two families below.
    /// </remarks>
    [Fact]
    public async Task ADelete_SendsTheIdAlone()
    {
        var organization = TestData.Organization(Guid.NewGuid());

        await Deleted(organization);

        var send = _hub.Of(MainHubMethods.OrganizationDeleted)[0];
        Assert.Equal(organization.Id, send.Payload);
        Assert.Single(send.Arguments);
    }

    /// <summary>The membership families send the whole entity where every other family sends a view model.</summary>
    [Fact]
    public async Task TheMembershipFamilies_SendTheWholeEntityWhereEveryOtherFamilySendsAViewModel()
    {
        var msel = TestData.Msel();
        var team = TestData.Team(msel.Id);
        var unit = TestData.Unit();
        var user = TestData.User();
        var teamUser = TestData.TeamUser(user.Id, team.Id);
        var unitUser = TestData.UnitUser(user.Id, unit.Id);
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

    /// <summary>The installation wide families name no modified properties even on an update.</summary>
    [Fact]
    public async Task TheInstallationWideFamilies_NameNoModifiedPropertiesEvenOnAnUpdate()
    {
        var systemRole = TestData.SystemRole();

        await Updated(systemRole, "Name");

        Assert.Single(_hub.Of(MainHubMethods.SystemRoleUpdated)[0].Arguments);
    }

    // -------------------------------------------------------------------------------------------------
    // When the row is not there
    // -------------------------------------------------------------------------------------------------

    /// <summary>A row with no MSEL is broadcast to a group named by the empty string.</summary>
    [Fact]
    public async Task ARowWithNoMsel_IsBroadcastToAGroupNamedByTheEmptyString()
    {
        var dataField = TestData.DataField();
        await Seed(dataField);

        await Created(TestData.Organization());
        await Created(TestData.Card(null));
        await Created(dataField);
        await Created(TestData.CardTeam(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Contains(string.Empty, _hub.Recipients(MainHubMethods.OrganizationCreated, string.Empty));
        Assert.Contains(string.Empty, _hub.Recipients(MainHubMethods.CardCreated, string.Empty));
        Assert.Contains(string.Empty, _hub.Recipients(MainHubMethods.DataFieldCreated, string.Empty));
        Assert.Contains(string.Empty, _hub.Recipients(MainHubMethods.CardTeamCreated, string.Empty));
    }

    /// <summary>A row whose parent is gone is broadcast to the all zero guids group.</summary>
    [Fact]
    public async Task ARowWhoseParentIsGone_IsBroadcastToTheAllZeroGuidsGroup()
    {
        await Created(TestData.DataValue(Guid.NewGuid(), Guid.NewGuid()));
        await Created(TestData.PlayerApplicationTeam(Guid.NewGuid(), Guid.NewGuid()));

        AssertTold(
            MainHubMethods.DataValueCreated, Guid.Empty.ToString(), MainHub.ADMIN_DATA_GROUP);
        AssertTold(
            MainHubMethods.PlayerApplicationTeamCreated,
            Guid.Empty.ToString(),
            MainHub.ADMIN_DATA_GROUP);
    }

    /// <summary>The families that re read the row throw when it is gone.</summary>
    [Fact]
    public async Task TheFamiliesThatReReadTheRow_ThrowWhenItIsGone()
    {
        var mselId = Guid.NewGuid();

        await Assert.ThrowsAsync<NullReferenceException>(
            () => Created(TestData.CiteAction(mselId)));
        await Assert.ThrowsAsync<NullReferenceException>(
            () => Created(TestData.CiteDuty(mselId)));
        await Assert.ThrowsAsync<NullReferenceException>(
            () => Created(TestData.TeamUser(Guid.NewGuid(), Guid.NewGuid())));
        await Assert.ThrowsAsync<NullReferenceException>(
            () => Created(TestData.UnitUser(Guid.NewGuid(), Guid.NewGuid())));
    }

    /// <summary>A team user is only broadcastable while its team is tracked.</summary>
    [Fact]
    public async Task ATeamUser_IsOnlyBroadcastableWhileItsTeamIsTracked()
    {
        var msel = TestData.Msel();
        var team = TestData.Team(msel.Id);
        var user = TestData.User();
        var tracked = TestData.TeamUser(user.Id, team.Id);
        await Seed(msel, team, user, tracked);

        // A second member of the same team, written by somebody else's request.
        var stranger = TestData.User();
        var untracked = TestData.TeamUser(stranger.Id, team.Id);
        await SeedCold(stranger, untracked);

        await Created(tracked);
        await using (var cold = NewContext())
        {
            await Assert.ThrowsAsync<NullReferenceException>(() => Created(untracked, cold));
        }

        Assert.Equal(3, _hub.Of(MainHubMethods.TeamUserCreated, team.Id, user.Id).Count);
    }

    /// <summary>A MSEL gone by the time its event is handled is broadcast to nobody.</summary>
    [Fact]
    public async Task AMselThatIsGone_IsBroadcastToNobody()
    {
        var msel = TestData.Msel();

        await Created(msel);

        Assert.Empty(_hub.Sent(msel.Id));
    }

    /// <summary>A data field that is gone is broadcast as a null payload.</summary>
    [Fact]
    public async Task ADataFieldThatIsGone_IsBroadcastAsANullPayload()
    {
        var dataField = TestData.DataField(Guid.NewGuid());

        await Created(dataField);

        Assert.Equal(2, _hub.Of(MainHubMethods.DataFieldCreated, dataField.MselId).Count);
        Assert.Null(_hub.Of(MainHubMethods.DataFieldCreated, dataField.MselId)[0].Payload);
    }

    /// <summary>A MSEL is broadcast with the acting users roles alone.</summary>
    [Fact]
    public async Task AMselIsBroadcastWithTheActingUsersRolesAlone()
    {
        var actor = TestData.User();
        var participant = TestData.User();
        var msel = TestData.Msel(actor.Id);
        await SeedCold(
            actor,
            participant,
            msel,
            TestData.UserMselRole(actor.Id, msel.Id),
            TestData.UserMselRole(participant.Id, msel.Id));

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

    /// <summary>Nineteen of the forty three entities raise events no handler receives.</summary>
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

    /// <summary>Three broadcast method names are declared for an entity no handler serves.</summary>
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
                x.ParameterType == typeof(IMapper) ? TestMapper.Mapper :
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
        var actual = _hub.Recipients(method, groups).OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.Equal(expected, actual);
    }
}
