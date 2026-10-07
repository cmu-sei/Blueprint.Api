// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary><c>ProficiencyLevelService</c> / <c>ProficiencyLevelController</c> - the five routes over the
/// levels a proficiency scale is made of. Reads want <c>ViewCompetencyFrameworks</c>, writes
/// <c>ManageCompetencyFrameworks</c>.</summary>
public class ProficiencyLevelEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET proficiencyScales/{scaleId}/proficiencyLevels
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByScale_WithViewCompetencyFrameworks_ReturnsThatScalesLevelsInDisplayOrder()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ViewCompetencyFrameworks).SeedAsync();
        var scale = TestData.ProficiencyScale();
        var otherScale = TestData.ProficiencyScale();
        await Seed(scale, otherScale);
        await Seed(
            TestData.ProficiencyLevel(scale.Id, "last", value: 3, displayOrder: 3),
            TestData.ProficiencyLevel(scale.Id, "first", value: 1, displayOrder: 1),
            TestData.ProficiencyLevel(otherScale.Id, "elsewhere"));

        var response = await Client(actor)
            .GetAsync($"api/proficiencyScales/{scale.Id}/proficiencyLevels", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content
            .ReadFromJsonAsync<List<ViewModels.ProficiencyLevel>>(JsonOptions, Ct);
        Assert.Equal(["first", "last"], list.Select(x => x.Name));
        Assert.All(list, x => Assert.Equal(scale.Id, x.ProficiencyScaleId));
    }

    /// <summary>Get by scale for a scale that is not there is an empty 200.</summary>
    [Fact]
    public async Task GetByScale_ForAScaleThatIsNotThere_IsAnEmpty200()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ViewCompetencyFrameworks).SeedAsync();
        var scale = TestData.ProficiencyScale();
        await Seed(scale);
        await Seed(TestData.ProficiencyLevel(scale.Id));

        var response = await Client(actor)
            .GetAsync($"api/proficiencyScales/{Guid.NewGuid()}/proficiencyLevels", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content
            .ReadFromJsonAsync<List<ViewModels.ProficiencyLevel>>(JsonOptions, Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // GET proficiencyLevels/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_WithViewCompetencyFrameworks_Is200_AndWritesTheIntegersAsStrings()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ViewCompetencyFrameworks).SeedAsync();
        var scale = TestData.ProficiencyScale();
        await Seed(scale);
        var level = TestData.ProficiencyLevel(scale.Id, "competent", value: 2, displayOrder: 4);
        await Seed(level);

        var response = await Client(actor).GetAsync($"api/proficiencyLevels/{level.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("\"value\":\"2\"", body);
        Assert.Contains("\"displayOrder\":\"4\"", body);
        var answered = await response.Content
            .ReadFromJsonAsync<ViewModels.ProficiencyLevel>(JsonOptions, Ct);
        Assert.Equal(level.Id, answered.Id);
        Assert.Equal(scale.Id, answered.ProficiencyScaleId);
        Assert.Equal("competent", answered.Name);
    }

    /// <summary>An unknown level is a 404 from the controller's own null check.</summary>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ViewCompetencyFrameworks).SeedAsync();

        var response = await Client(actor).GetAsync($"api/proficiencyLevels/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>(JsonOptions, Ct);
        Assert.Equal("Proficiency Level not found", error.title);
    }

    // ---------------------------------------------------------------------------------------------
    // POST proficiencyLevels
    // ---------------------------------------------------------------------------------------------

    /// <summary>Create with manage competency frameworks is answered with a 201.</summary>
    [Fact]
    public async Task Create_WithManageCompetencyFrameworks_Is201()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();
        var scale = TestData.ProficiencyScale();
        await Seed(scale);

        var response = await Client(actor).PostAsJsonAsync(
            "api/proficiencyLevels", Body(scale.Id) with { name = "created", value = 5 }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content
            .ReadFromJsonAsync<ViewModels.ProficiencyLevel>(JsonOptions, Ct);
        Assert.Equal(scale.Id, created.ProficiencyScaleId);
        Assert.Equal(5, created.Value);
        Assert.Equal(actor.Id, created.CreatedBy);
        Assert.EndsWith($"/api/proficiencylevels/{created.Id}", response.Headers.Location.ToString());

        var stored = await ReadBack(rb => rb.ProficiencyLevels.SingleAsync(x => x.Id == created.Id, Ct));
        Assert.Equal("created", stored.Name);
    }

    /// <summary>Create for a scale that is not there is answered with a 500.</summary>
    [Fact]
    public async Task Create_ForAScaleThatIsNotThere_Is500()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "api/proficiencyLevels", Body(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(await ReadBack(rb => rb.ProficiencyLevels.ToListAsync(Ct)));
    }

    // ---------------------------------------------------------------------------------------------
    // PUT proficiencyLevels/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_WithManageCompetencyFrameworks_Is200()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();
        var scale = TestData.ProficiencyScale();
        await Seed(scale);
        var level = TestData.ProficiencyLevel(scale.Id, "before");
        await Seed(level);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/proficiencyLevels/{level.Id}",
            Body(scale.Id) with { id = level.Id, name = "after", displayOrder = 7 },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await ReadBack(rb => rb.ProficiencyLevels.SingleAsync(x => x.Id == level.Id, Ct));
        Assert.Equal("after", stored.Name);
        Assert.Equal(7, stored.DisplayOrder);
        Assert.Equal(actor.Id, stored.ModifiedBy);
    }

    /// <summary>An unknown level on update is a 404 naming <c>ProficiencyLevel</c>.</summary>
    [Fact]
    public async Task Update_ForAnIdThatIsNotThere_Is404_ThatNamesTheViewModel()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();
        var scale = TestData.ProficiencyScale();
        await Seed(scale);
        var id = Guid.NewGuid();

        var response = await Client(actor).PutAsJsonAsync(
            $"api/proficiencyLevels/{id}", Body(scale.Id) with { id = id }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>(JsonOptions, Ct);
        Assert.Equal("Proficiency Level not found", error.title);
    }

    /// <summary>Update moves the level to whichever scale the body names.</summary>
    [Fact]
    public async Task Update_MovesTheLevelToWhicheverScaleTheBodyNames()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();
        var scale = TestData.ProficiencyScale();
        var otherScale = TestData.ProficiencyScale();
        await Seed(scale, otherScale);
        var level = TestData.ProficiencyLevel(scale.Id);
        await Seed(level);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/proficiencyLevels/{level.Id}", Body(otherScale.Id) with { id = level.Id }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await ReadBack(rb => rb.ProficiencyLevels.SingleAsync(x => x.Id == level.Id, Ct));
        Assert.Equal(otherScale.Id, stored.ProficiencyScaleId);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE proficiencyLevels/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_WithManageCompetencyFrameworks_Is204()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();
        var scale = TestData.ProficiencyScale();
        await Seed(scale);
        var level = TestData.ProficiencyLevel(scale.Id);
        await Seed(level);

        var response = await Client(actor).DeleteAsync($"api/proficiencyLevels/{level.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await ReadBack(rb => rb.ProficiencyLevels.ToListAsync(Ct)));
        Assert.Single(await ReadBack(rb => rb.ProficiencyScales.ToListAsync(Ct)));
    }

    [Fact]
    public async Task Delete_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();

        var response = await Client(actor).DeleteAsync($"api/proficiencyLevels/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>(JsonOptions, Ct);
        Assert.Equal("Proficiency Level not found", error.title);
    }

    /// <remarks>
    /// <c>ViewModels.ProficiencyLevel</c> derives from <c>Base</c>, whose <c>DateCreated</c> and
    /// <c>CreatedBy</c> are non-nullable, so this record omits them rather than sending nulls.
    /// </remarks>
    private static ProficiencyLevelBody Body(Guid proficiencyScaleId) =>
        new() { proficiencyScaleId = proficiencyScaleId, name = "seeded", value = 1, displayOrder = 1 };

    private record ProficiencyLevelBody
    {
        public Guid id { get; init; }
        public Guid proficiencyScaleId { get; init; }
        public string name { get; init; }
        public int value { get; init; }
        public string description { get; init; }
        public int displayOrder { get; init; }
    }

    /// <remarks>
    /// The shape <c>JsonExceptionFilter</c> answers with - <c>ViewModels.ApiError</c>, read here as a
    /// record so a test can assert which name a 404 carries.
    /// </remarks>
    private record ApiErrorBody
    {
        public int status { get; init; }
        public string title { get; init; }
        public string detail { get; init; }
    }
}
