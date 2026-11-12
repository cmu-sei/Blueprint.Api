// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Mappings;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Xunit;

namespace Blueprint.Api.Tests.Infrastructure.Mappings;

/// <summary>
/// The Mapperly mappers called directly, for the rules no endpoint reaches on its own. The build checks that
/// every member is mapped (see .editorconfig); these check what the mapping does with it.
/// </summary>
public class MapperTests
{
    /// <summary>
    /// <c>TeamEntity.MselId</c> is a required foreign key and <c>TeamCreate.MselId</c> is nullable, so a create
    /// request that omits it leaves the entity's empty rather than writing a null it could not save
    /// (MapperDefaults).
    /// </summary>
    [Fact]
    public void A_team_create_without_an_msel_id_leaves_the_entitys_empty()
    {
        var mselId = Guid.NewGuid();

        Assert.Equal(Guid.Empty, new TeamCreate { Name = "Red", MselId = null }.ToEntity().MselId);
        Assert.Equal(mselId, new TeamCreate { Name = "Red", MselId = mselId }.ToEntity().MselId);
    }

    /// <summary>
    /// An MSEL's units come from its join rows, and its pages are mapped whenever they are loaded: the
    /// ExplicitExpansion AutoMapper declared on them only ever applied to projections, and an MSEL is never
    /// projected.
    /// </summary>
    [Fact]
    public void An_msel_takes_its_units_from_the_join_rows_and_maps_its_pages()
    {
        var unit = new UnitEntity { Id = Guid.NewGuid(), Name = "Alpha", ShortName = "A" };
        var entity = TestData.Msel();
        entity.MselUnits.Add(new MselUnitEntity { UnitId = unit.Id, MselId = entity.Id, Unit = unit });
        entity.MselUnits.Add(new MselUnitEntity { UnitId = Guid.NewGuid(), MselId = entity.Id });
        entity.Pages.Add(new MselPageEntity { Id = Guid.NewGuid(), MselId = entity.Id, Name = "Brief" });

        var msel = entity.ToMsel();

        Assert.Equal("Alpha", msel.Units.First().Name);
        Assert.Null(msel.Units.Last());
        Assert.Equal("Brief", Assert.Single(msel.Pages).Name);
    }

    /// <summary>A membership whose user is not loaded is a null user, not a missing one.</summary>
    [Fact]
    public void A_membership_without_a_loaded_user_is_a_null_user()
    {
        var user = new UserEntity { Id = Guid.NewGuid(), Name = "Loaded" };
        var team = new TeamEntity { Id = Guid.NewGuid() };
        team.TeamUsers.Add(new TeamUserEntity(user.Id, team.Id) { User = user });
        team.TeamUsers.Add(new TeamUserEntity(Guid.NewGuid(), team.Id));
        var unit = new UnitEntity { Id = Guid.NewGuid() };
        unit.UnitUsers.Add(new UnitUserEntity(user.Id, unit.Id) { User = user });
        unit.UnitUsers.Add(new UnitUserEntity(Guid.NewGuid(), unit.Id));

        var teamUsers = team.ToTeam().Users;
        var unitUsers = unit.ToUnit().Users;

        Assert.Equal(["Loaded", null], teamUsers.Select(x => x?.Name));
        Assert.Equal(["Loaded", null], unitUsers.Select(x => x?.Name));
    }

    /// <summary>Lists and dictionaries missing on an entity are empty on the view model, as AutoMapper left them.</summary>
    [Fact]
    public void Missing_lists_on_an_entity_are_mapped_to_empty_ones()
    {
        var team = new TeamEntity { TeamUsers = null, CardTeams = null, PlayerApplicationTeams = null, Invitations = null, UserTeamRoles = null }.ToTeam();
        var unit = new UnitEntity { UnitUsers = null }.ToUnit();
        var msel = new MselEntity
        {
            Moves = null, DataFields = null, ScenarioEvents = null, Teams = null, MselUnits = null, Organizations = null,
            UserMselRoles = null, Cards = null, CiteDuties = null, CiteActions = null, PlayerApplications = null, Pages = null,
            Invitations = null, MselCompetencies = null,
        }.ToMsel();
        var field = new DataFieldEntity { DataOptions = null }.ToDataField();
        var injectType = new InjectTypeEntity { DataFields = null }.ToInjectType();
        var inject = new InjectEntity { DataValues = null }.ToInjectm();
        var competency = new CompetencyEntity { Children = null }.ToCompetency();
        var framework = new CompetencyFrameworkEntity { Competencies = null }.ToCompetencyFramework();
        var scale = new ProficiencyScaleEntity { ProficiencyLevels = null }.ToProficiencyScale();
        var scenarioEvent = new ScenarioEventEntity { DataValues = null }.ToScenarioEvent();
        var role = new SystemRoleEntity { Permissions = null }.ToSystemRole();
        var task = new SteamfitterTaskEntity { ActionParameters = null }.ToSteamfitterTask();

        Assert.Equal(0, team.Users.Length + team.CardTeams.Length + team.PlayerApplicationTeams.Length + team.Invitations.Length + team.UserTeamRoles.Count);
        Assert.Empty(unit.Users);
        Assert.Equal(0, msel.Moves.Count + msel.DataFields.Count + msel.ScenarioEvents.Count + msel.Teams.Count + msel.Units.Count
            + msel.Organizations.Count + msel.UserMselRoles.Count + msel.Cards.Count + msel.CiteDuties.Count + msel.CiteActions.Count
            + msel.PlayerApplications.Count + msel.Pages.Count + msel.Invitations.Count + msel.MselCompetencies.Count);
        Assert.Equal(0, field.DataOptions.Count + injectType.DataFields.Count + inject.DataValues.Count + competency.Children.Count
            + framework.Competencies.Count + scale.ProficiencyLevels.Count + scenarioEvent.DataValues.Count + role.Permissions.Count
            + task.ActionParameters.Count);
    }

    /// <summary>Lists and dictionaries missing on a request are empty on the entity, as AutoMapper left them.</summary>
    [Fact]
    public void Missing_lists_on_a_request_are_mapped_to_empty_ones()
    {
        var inject = new Injectm { DataValues = null }.ToEntity();
        var injectType = new ViewModels.InjectType { DataFields = null }.ToEntity();
        var competency = new Competency { Children = null }.ToEntity();
        var framework = new CompetencyFramework { Competencies = null }.ToEntity();
        var scale = new ProficiencyScale { ProficiencyLevels = null }.ToEntity();
        var role = new SystemRole { Permissions = null }.ToEntity();
        var task = new SteamfitterTaskFields { ActionParameters = null }.ToEntity();
        var stored = new SteamfitterTaskEntity { ActionParameters = new() { ["old"] = "value" } };
        new SteamfitterTaskFields { ActionParameters = null }.ApplyTo(stored);
        var storedRole = new SystemRoleEntity { Permissions = [SystemPermission.ViewUsers] };
        new SystemRole { Permissions = null }.ApplyTo(storedRole);

        Assert.Equal(0, inject.DataValues.Count + injectType.DataFields.Count + competency.Children.Count + framework.Competencies.Count
            + scale.ProficiencyLevels.Count + role.Permissions.Count + task.ActionParameters.Count);
        Assert.Empty(stored.ActionParameters);
        Assert.Empty(storedRole.Permissions);
    }

    /// <summary>The entity gets copies of a request's permission list and action parameters, never the request's own.</summary>
    [Fact]
    public void Permissions_and_action_parameters_are_copied()
    {
        var permissions = new List<SystemPermission> { SystemPermission.ViewUsers };
        var parameters = new Dictionary<string, string> { ["key"] = "value" };

        var role = new SystemRole { Permissions = permissions }.ToEntity();
        var task = new SteamfitterTaskFields { ActionParameters = parameters }.ToEntity();

        Assert.NotSame(permissions, role.Permissions);
        Assert.Equal(permissions, role.Permissions);
        Assert.NotSame(parameters, task.ActionParameters);
        Assert.Equal(parameters, task.ActionParameters);
    }

    /// <summary>
    /// A self-referencing graph maps each entity once, so a cycle ends at the view model already made, as
    /// AutoMapper's inferred reference preservation did, rather than overflowing the stack.
    /// </summary>
    [Fact]
    public void Self_referencing_graphs_map_each_entity_once()
    {
        var competency = new CompetencyEntity { Id = Guid.NewGuid() };
        competency.Children.Add(competency);
        var catalog = new CatalogEntity { Id = Guid.NewGuid() };
        catalog.Parent = catalog;
        var listed = new CompetencyEntity { Id = Guid.NewGuid() };
        var parent = new CompetencyEntity { Id = Guid.NewGuid() };
        parent.Children.Add(listed);
        var framework = new CompetencyFrameworkEntity { Competencies = [parent, listed] };

        var mappedCompetency = competency.ToCompetency();
        var mappedCatalog = catalog.ToCatalog();
        var mappedFramework = framework.ToCompetencyFramework();

        Assert.Same(mappedCompetency, Assert.Single(mappedCompetency.Children));
        Assert.Same(mappedCatalog, mappedCatalog.Parent);
        Assert.Same(mappedFramework.Competencies.Last(), Assert.Single(mappedFramework.Competencies.First().Children));
    }

    /// <summary>
    /// An update writes a nested parent or unit onto the loaded one, as AutoMapper mapped onto an existing
    /// nested object, makes a new one when none is loaded, and clears it when the body has none.
    /// </summary>
    [Fact]
    public void An_update_writes_a_nested_object_onto_the_loaded_one()
    {
        var parent = new CatalogEntity { Id = Guid.NewGuid(), Name = "Stored parent" };
        var catalog = new CatalogEntity { Id = Guid.NewGuid(), Parent = parent };
        var unit = new UnitEntity { Id = Guid.NewGuid(), Name = "Stored unit" };
        var catalogUnit = new CatalogUnitEntity { Id = Guid.NewGuid(), Unit = unit };
        var unloaded = new CatalogEntity { Id = Guid.NewGuid() };
        var cleared = new CatalogEntity { Id = Guid.NewGuid(), Parent = new CatalogEntity() };
        var clearedUnit = new CatalogUnitEntity { Id = Guid.NewGuid(), Unit = new UnitEntity { Id = Guid.NewGuid() } };

        new Catalog { Id = catalog.Id, Parent = new Catalog { Id = parent.Id, Name = "Renamed parent" } }.ApplyTo(catalog);
        new CatalogUnit { Id = catalogUnit.Id, Unit = new Unit { Id = unit.Id, Name = "Renamed unit" } }.ApplyTo(catalogUnit);
        new Catalog { Id = unloaded.Id, Parent = new Catalog { Id = parent.Id, Name = "New parent" } }.ApplyTo(unloaded);
        new Catalog { Id = cleared.Id }.ApplyTo(cleared);
        new CatalogUnit { Id = clearedUnit.Id }.ApplyTo(clearedUnit);

        Assert.Same(parent, catalog.Parent);
        Assert.Equal("Renamed parent", parent.Name);
        Assert.Same(unit, catalogUnit.Unit);
        Assert.Equal("Renamed unit", unit.Name);
        Assert.Equal("New parent", unloaded.Parent.Name);
        Assert.NotSame(parent, unloaded.Parent);
        Assert.Null(cleared.Parent);
        Assert.Null(clearedUnit.Unit);
    }

    /// <summary>A null entity maps to a null view model, as AutoMapper's <c>Map</c> answered.</summary>
    [Fact]
    public void A_null_entity_maps_to_null()
    {
        Assert.Null(((MselEntity)null).ToMsel());
        Assert.Null(((ProficiencyScaleEntity)null).ToProficiencyScale());
        Assert.Null(((InjectTypeEntity)null).ToInjectType());
    }
}
