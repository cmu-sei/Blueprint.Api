// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>ProficiencyLevelService</c> / <c>ProficiencyLevelController</c> - the five routes over the levels a
/// proficiency scale is made of. Reads want <c>ViewCompetencyFrameworks</c>, writes
/// <c>ManageCompetencyFrameworks</c>.
/// </summary>
/// <remarks>
/// 401 and 403 for these routes live in <see cref="RouteAuthorizationTests"/>; this file covers the happy
/// path and the not-found path per route, per the thin protocol.
/// <para />
/// BUG: the service checks no permission at all - the controller's five <c>ForbiddenException</c> throws
/// are the whole guard, where every MSEL-scoped service on the branch re-checks with a requirement
/// helper. Nothing here is MSEL-scoped, so there are no role actors in this file: a proficiency scale is
/// global reference data that <c>CompetencyFrameworkService</c> (<c>abb8fdd</c>) imports against.
/// </remarks>
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
        var scale = BlueprintAppFactory.ProficiencyScale();
        var otherScale = BlueprintAppFactory.ProficiencyScale();
        await Seed(scale, otherScale);
        await Seed(
            BlueprintAppFactory.ProficiencyLevel(scale.Id, "last", value: 3, displayOrder: 3),
            BlueprintAppFactory.ProficiencyLevel(scale.Id, "first", value: 1, displayOrder: 1),
            BlueprintAppFactory.ProficiencyLevel(otherScale.Id, "elsewhere"));

        var response = await Client(actor)
            .GetAsync($"api/proficiencyScales/{scale.Id}/proficiencyLevels", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content
            .ReadFromJsonAsync<List<ViewModels.ProficiencyLevel>>(JsonOptions, Ct);
        Assert.Equal(["first", "last"], list.Select(x => x.Name));
        Assert.All(list, x => Assert.Equal(scale.Id, x.ProficiencyScaleId));
    }

    /// <remarks>
    /// BUG: <c>GetByScaleAsync</c> validates no scale, so an unknown one is an empty 200 rather than a
    /// 404 - indistinguishable from a scale that exists and has no levels yet, which is the state a
    /// client building one is in. Every other list route on the branch that takes a parent id either
    /// answers a clean 404 (<c>MselCompetencyService</c>) or dereferences an unchecked lookup and answers
    /// a status that depends on the caller's permission (<c>PlayerApplicationService</c>); this is the
    /// third answer to the same question.
    /// </remarks>
    [Fact]
    public async Task GetByScale_ForAScaleThatIsNotThere_IsAnEmpty200()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ViewCompetencyFrameworks).SeedAsync();
        var scale = BlueprintAppFactory.ProficiencyScale();
        await Seed(scale);
        await Seed(BlueprintAppFactory.ProficiencyLevel(scale.Id));

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
        var scale = BlueprintAppFactory.ProficiencyScale();
        await Seed(scale);
        var level = BlueprintAppFactory.ProficiencyLevel(scale.Id, "competent", value: 2, displayOrder: 4);
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

    /// <remarks>
    /// The 404 is the controller's rather than the service's: <c>GetAsync</c> maps a null entity to a null
    /// view model and <c>ProficiencyLevelController.cs:59-60</c> checks it. Without that check
    /// <c>Ok(null)</c> would be a 204 through <c>HttpNoContentOutputFormatter</c> rather than an empty
    /// 200, as <c>CatalogEndpointTests.Get_ForAnIdThatIsNotThere_Is204</c> pins - so the check is what
    /// makes this route answerable at all.
    /// </remarks>
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

    /// <remarks>
    /// BUG: <c>ProficiencyLevelController.cs:76</c> assigns <c>CreatedBy</c> from the caller and
    /// <c>ProficiencyLevelService.cs:64</c> assigns it again from the same principal, so the controller's
    /// line is dead code. Harmless, and the same shape as <c>:92</c> on the update - noted because four
    /// services on the branch carry the same pair of dead assignments.
    /// </remarks>
    [Fact]
    public async Task Create_WithManageCompetencyFrameworks_Is201()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();
        var scale = BlueprintAppFactory.ProficiencyScale();
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

        var stored = await NewContext().ProficiencyLevels.SingleAsync(x => x.Id == created.Id, Ct);
        Assert.Equal("created", stored.Name);
    }

    /// <remarks>
    /// BUG: nothing validates the scale the body names, so an unknown one is a 500 from the foreign key
    /// where the caller deserves a 404 naming it. <c>MselCompetencyService.CreateAsync</c> validates both
    /// of its parents and is the model to copy.
    /// </remarks>
    [Fact]
    public async Task Create_ForAScaleThatIsNotThere_Is500()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "api/proficiencyLevels", Body(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(await NewContext().ProficiencyLevels.ToListAsync(Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // PUT proficiencyLevels/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_WithManageCompetencyFrameworks_Is200()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();
        var scale = BlueprintAppFactory.ProficiencyScale();
        await Seed(scale);
        var level = BlueprintAppFactory.ProficiencyLevel(scale.Id, "before");
        await Seed(level);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/proficiencyLevels/{level.Id}",
            Body(scale.Id) with { id = level.Id, name = "after", displayOrder = 7 },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await NewContext().ProficiencyLevels.SingleAsync(x => x.Id == level.Id, Ct);
        Assert.Equal("after", stored.Name);
        Assert.Equal(7, stored.DisplayOrder);
        Assert.Equal(actor.Id, stored.ModifiedBy);
    }

    /// <remarks>
    /// BUG: the 404 is <c>EntityNotFoundException&lt;ProficiencyLevel&gt;</c> - the <b>view model</b> -
    /// so it reads "Proficiency Level not found" where a sibling service naming the entity would read
    /// "Proficiency Level Entity not found". Cosmetic on its own; it is the marker for the same
    /// inconsistency <c>TeamCompetencyService</c> and <c>MselPageService</c> carry, and a client matching
    /// on the message cannot rely on either spelling.
    /// </remarks>
    [Fact]
    public async Task Update_ForAnIdThatIsNotThere_Is404_ThatNamesTheViewModel()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();
        var scale = BlueprintAppFactory.ProficiencyScale();
        await Seed(scale);
        var id = Guid.NewGuid();

        var response = await Client(actor).PutAsJsonAsync(
            $"api/proficiencyLevels/{id}", Body(scale.Id) with { id = id }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>(JsonOptions, Ct);
        Assert.Equal("Proficiency Level not found", error.title);
    }

    /// <remarks>
    /// BUG: the profile maps <c>ProficiencyScaleId</c>, and nothing compares the body's to the stored
    /// row's, so a PUT moves a level from one scale to another - the scale it left keeps no record and
    /// nothing broadcasts either way, there being no handler for this type among the 25. Milder than the
    /// same shape on an MSEL-scoped row (this is global reference data, so no permission boundary is
    /// crossed), which is why it is one assertion rather than a file of them.
    /// </remarks>
    [Fact]
    public async Task Update_MovesTheLevelToWhicheverScaleTheBodyNames()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();
        var scale = BlueprintAppFactory.ProficiencyScale();
        var otherScale = BlueprintAppFactory.ProficiencyScale();
        await Seed(scale, otherScale);
        var level = BlueprintAppFactory.ProficiencyLevel(scale.Id);
        await Seed(level);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/proficiencyLevels/{level.Id}", Body(otherScale.Id) with { id = level.Id }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await NewContext().ProficiencyLevels.SingleAsync(x => x.Id == level.Id, Ct);
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
        var scale = BlueprintAppFactory.ProficiencyScale();
        await Seed(scale);
        var level = BlueprintAppFactory.ProficiencyLevel(scale.Id);
        await Seed(level);

        var response = await Client(actor).DeleteAsync($"api/proficiencyLevels/{level.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await NewContext().ProficiencyLevels.ToListAsync(Ct));
        Assert.Single(await NewContext().ProficiencyScales.ToListAsync(Ct));
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
