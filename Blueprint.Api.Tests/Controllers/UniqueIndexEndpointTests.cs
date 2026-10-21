// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

public class UniqueIndexEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    [Theory]
    [InlineData("api/groups", "GroupService.UpdateAsync")]
    [InlineData("api/catalogs", "CatalogService.UpdateAsync")]
    [InlineData("api/injectTypes", "InjectTypeService.UpdateAsync")]
    [InlineData("api/proficiencyScales", "ProficiencyScaleService.UpdateAsync")]
    [InlineData("api/system-roles", "SystemRoleService.UpdateAsync")]
    public async Task Update_answers_a_taken_name_with_a_server_error_and_keeps_both_names(string route, string service)
    {
        var (original, taken, body) = await NamedRows(route);

        var response = await RootClient.PutAsJsonAsync($"{route}/{original}", body, Ct);

        await SaveError(response, service);
        Assert.Equal("original", await Name(route, original));
        Assert.Equal("taken", await Name(route, taken));
    }

    [Fact]
    public async Task Create_answers_a_taken_catalog_name_with_a_server_error_and_creates_nothing()
    {
        var type = TestData.InjectType();
        var catalog = TestData.Catalog(type.Id);
        await Seed(type, catalog);

        var response = await RootClient.PostAsJsonAsync("api/catalogs", new { name = catalog.Name, injectTypeId = type.Id }, Ct);

        await SaveError(response, "CatalogService.CreateAsync");
        Assert.Equal(1, await ReadBack(rb => rb.Catalogs.CountAsync(Ct)));
    }

    [Fact]
    public async Task Copy_answers_a_taken_generated_catalog_name_with_a_server_error_and_keeps_the_catalogs()
    {
        var type = TestData.InjectType();
        var catalog = TestData.Catalog(type.Id);
        var taken = TestData.Catalog(type.Id);
        taken.Name = $"{catalog.Name} - {Root.Name}";
        await Seed(type, catalog, taken);

        var response = await RootClient.PostAsync($"api/catalogs/{catalog.Id}/copy", null, Ct);

        await SaveError(response, "CatalogService.privateCatalogCopyAsync");
        Assert.Equal(2, await ReadBack(rb => rb.Catalogs.CountAsync(Ct)));
        Assert.Equal(catalog.Name, await ReadBack(rb => rb.Catalogs.Where(x => x.Id == catalog.Id).Select(x => x.Name).SingleAsync(Ct)));
    }

    [Theory]
    [InlineData("api/teamcards", "CardTeamService.CreateAsync")]
    [InlineData("api/cataloginjects", "CatalogInjectService.CreateAsync")]
    [InlineData("api/cataloginjects/multiple", "CatalogInjectService.CreateMultipleAsync")]
    [InlineData("api/groups/{0}/memberships", "GroupService.CreateMembershipAsync")]
    [InlineData("api/teamplayerApplications", "PlayerApplicationTeamService.CreateAsync")]
    public async Task Create_answers_an_existing_join_pair_with_a_server_error_and_keeps_one_membership(string route, string service)
    {
        var request = await JoinPair(route);

        var response = await RootClient.PostAsJsonAsync(request.Route, request.Body, Ct);

        await SaveError(response, service);
        Assert.Equal(1, await CountRows(request.Type));
    }

    [Fact]
    public async Task CreateMultiple_keeps_the_earlier_catalog_assignment_when_a_later_pair_collides()
    {
        var type = TestData.InjectType();
        var catalog = TestData.Catalog(type.Id);
        var first = TestData.Inject(type.Id);
        var taken = TestData.Inject(type.Id);
        var existing = TestData.CatalogInject(catalog.Id, taken.Id);
        await Seed(type, catalog, first, taken, existing);

        var response = await RootClient.PostAsJsonAsync("api/cataloginjects/multiple", new[]
        {
            new { catalogId = catalog.Id, injectId = first.Id },
            new { catalogId = catalog.Id, injectId = taken.Id }
        }, Ct);

        await SaveError(response, "CatalogInjectService.CreateMultipleAsync");
        var stored = await ReadBack(rb => rb.CatalogInjects.OrderBy(x => x.InjectId).Select(x => x.InjectId).ToListAsync(Ct));
        Assert.Equal(new[] { first.Id, taken.Id }.Order(), stored);
    }

    [Fact]
    public async Task Update_answers_an_existing_catalog_unit_pair_with_a_server_error_and_keeps_the_assignment()
    {
        var type = TestData.InjectType();
        var catalog = TestData.Catalog(type.Id);
        var unit = TestData.Unit();
        var other = TestData.Unit();
        var original = TestData.CatalogUnit(unit.Id, catalog.Id);
        var taken = TestData.CatalogUnit(other.Id, catalog.Id);
        await Seed(type, catalog, unit, other, original, taken);

        var response = await RootClient.PutAsJsonAsync($"api/catalogunits/{original.Id}",
            new { id = original.Id, unitId = other.Id, catalogId = catalog.Id }, Ct);

        await SaveError(response, "CatalogUnitService.UpdateAsync");
        Assert.Equal(unit.Id, await ReadBack(rb => rb.CatalogUnits.Where(x => x.Id == original.Id).Select(x => x.UnitId).SingleAsync(Ct)));
        Assert.Equal(2, await ReadBack(rb => rb.CatalogUnits.CountAsync(Ct)));
    }

    [Fact]
    public async Task Update_answers_an_existing_move_number_with_a_server_error_and_keeps_the_move()
    {
        var msel = TestData.Msel();
        var original = TestData.Move(msel.Id, 1);
        var taken = TestData.Move(msel.Id, 2);
        await Seed(msel, original, taken);

        var response = await RootClient.PutAsJsonAsync($"api/moves/{original.Id}", new { moveNumber = 2 }, Ct);

        await SaveError(response, "MoveService.UpdateAsync");
        Assert.Equal(1, await ReadBack(rb => rb.Moves.Where(x => x.Id == original.Id).Select(x => x.MoveNumber).SingleAsync(Ct)));
        Assert.Equal(2, await ReadBack(rb => rb.Moves.CountAsync(Ct)));
    }

    private async Task SaveError(System.Net.Http.HttpResponseMessage response, string service)
    {
        var error = await AssertJsonError<ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("An error occurred while saving the entity changes. See the inner exception for details.", error.Title);
        Assert.Contains(service, error.Detail);
    }

    private async Task<(Guid Original, Guid Taken, object Body)> NamedRows(string route)
    {
        var type = TestData.InjectType();
        await Seed(type);
        (object original, object taken) = route switch
        {
            "api/groups" => ((object)TestData.Group("original"), (object)TestData.Group("taken")),
            "api/catalogs" => ((object)TestData.Catalog(type.Id), (object)TestData.Catalog(type.Id)),
            "api/injectTypes" => ((object)TestData.InjectType(), (object)TestData.InjectType()),
            "api/proficiencyScales" => ((object)TestData.ProficiencyScale(name: "original"), (object)TestData.ProficiencyScale(name: "taken")),
            "api/system-roles" => ((object)TestData.SystemRole("original"), (object)TestData.SystemRole("taken")),
            _ => throw new ArgumentOutOfRangeException(nameof(route))
        };
        original.GetType().GetProperty("Name").SetValue(original, "original");
        taken.GetType().GetProperty("Name").SetValue(taken, "taken");
        await Seed(original, taken);
        var id = (Guid)original.GetType().GetProperty("Id").GetValue(original);
        var takenId = (Guid)taken.GetType().GetProperty("Id").GetValue(taken);
        return (id, takenId, new { id, name = "taken", injectTypeId = type.Id });
    }

    private async Task<string> Name(string route, Guid id)
    {
        await using var db = NewContext();
        var type = route switch
        {
            "api/groups" => typeof(Data.Models.GroupEntity),
            "api/catalogs" => typeof(Data.Models.CatalogEntity),
            "api/injectTypes" => typeof(Data.Models.InjectTypeEntity),
            "api/proficiencyScales" => typeof(Data.Models.ProficiencyScaleEntity),
            "api/system-roles" => typeof(Data.Models.SystemRoleEntity),
            _ => throw new ArgumentOutOfRangeException(nameof(route))
        };
        var row = await db.FindAsync(type, [id], Ct);
        return (string)db.Entry(row).Property("Name").CurrentValue;
    }

    private Task<int> CountRows(Type type) => type.Name switch
    {
        nameof(Data.Models.CardTeamEntity) => ReadBack(db => db.CardTeams.CountAsync(Ct)),
        nameof(Data.Models.CatalogInjectEntity) => ReadBack(db => db.CatalogInjects.CountAsync(Ct)),
        nameof(Data.Models.GroupMembershipEntity) => ReadBack(db => db.GroupMemberships.CountAsync(Ct)),
        nameof(Data.Models.PlayerApplicationTeamEntity) => ReadBack(db => db.PlayerApplicationTeams.CountAsync(Ct)),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private async Task<(string Route, object Body, Type Type)> JoinPair(string route)
    {
        var msel = TestData.Msel();
        var team = TestData.Team(msel.Id);
        var card = TestData.Card(msel.Id);
        var application = TestData.PlayerApplication(msel.Id);
        var type = TestData.InjectType();
        var catalog = TestData.Catalog(type.Id);
        var inject = TestData.Inject(type.Id);
        var group = TestData.Group();
        var user = TestData.User();
        await Seed(msel, team, card, application, type, catalog, inject, group, user);
        var (row, body) = route switch
        {
            "api/teamcards" => ((object)TestData.CardTeam(card.Id, team.Id), (object)new { cardId = card.Id, teamId = team.Id }),
            "api/cataloginjects" or "api/cataloginjects/multiple" => ((object)TestData.CatalogInject(catalog.Id, inject.Id), new { catalogId = catalog.Id, injectId = inject.Id }),
            "api/groups/{0}/memberships" => ((object)TestData.GroupMembership(group.Id, user.Id), new { groupId = group.Id, userId = user.Id }),
            "api/teamplayerApplications" => ((object)TestData.PlayerApplicationTeam(application.Id, team.Id), new { playerApplicationId = application.Id, teamId = team.Id }),
            _ => throw new ArgumentOutOfRangeException(nameof(route))
        };
        await Seed(row);
        return (string.Format(route, group.Id), route == "api/cataloginjects/multiple" ? new[] { body } : body, row.GetType());
    }
}
