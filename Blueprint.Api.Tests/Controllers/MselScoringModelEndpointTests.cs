// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Cite.Api.Client;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

// The generated CITE client declares a SystemPermission of its own.
using SystemPermission = Blueprint.Api.Data.Enumerations.SystemPermission;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// <c>MselScoringModelService</c> / <c>MselScoringModelController</c> - the CITE scoring model a MSEL authors
/// itself. Reading it wants <c>ViewMsels</c> or <c>MselContributorRequirement</c>, which leaves out the people who
/// are only on one of the MSEL's teams; every write wants <c>EditMsels</c> or the MSEL's <c>Owner</c> role, like the
/// MSEL's scoring model link, and is refused while the MSEL is pushed.
/// </summary>
/// <remarks>
/// 401 and 403 for these routes live in <see cref="RouteAuthorizationTests"/>.
/// </remarks>
public class MselScoringModelEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET msels/{id}/scoringmodel
    // ---------------------------------------------------------------------------------------------

    /// <summary>A MSEL with no model of its own answers 204, so a client can ask without handling an error.</summary>
    [Fact]
    public async Task Get_ForAMselWithNoScoringModel_Is204()
    {
        var msel = await SeedMsel();

        var response = await RootClient.GetAsync(Route(msel), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>Get answers the categories and their options in display order, whatever order they were stored in.</summary>
    [Fact]
    public async Task Get_AnswersTheCategoriesAndOptionsInDisplayOrder()
    {
        var msel = await SeedMsel();
        var model = TestData.MselScoringModel(msel.Id);
        var first = model.Categories.Single();
        first.DisplayOrder = 2;
        first.Options.First().DisplayOrder = 3;
        model.Categories.Add(new MselScoringCategoryEntity
        {
            Id = Guid.NewGuid(), MselScoringModelId = msel.Id, DisplayOrder = 1, Description = "After-action review",
            MoveNumberFirstDisplay = 2, MoveNumberLastDisplay = 2
        });
        await Seed(model);

        var answered = await ReadAsync<MselScoringModel>(await RootClient.GetAsync(Route(msel), Ct), JsonOptions);

        Assert.Equal(model.Description, answered.Description);
        Assert.Equal(["After-action review", first.Description], answered.Categories.Select(c => c.Description));
        Assert.Equal(["What do you tell the press?", "Who do you call first?"], answered.Categories[1].Options.Select(o => o.Description));
        Assert.Equal(CiteScoringOptionSelection.None, answered.Categories[1].ScoringOptionSelection);
        Assert.Equal(CiteRightSideDisplay.Scoresheet, answered.RightSideDisplay);
    }

    /// <summary>A weight or a value is a JSON number, as in CITE; the display orders are strings, like every int here.</summary>
    [Fact]
    public async Task Get_WritesTheWeightsAndValuesAsNumbers()
    {
        var msel = await SeedMsel();
        var model = TestData.MselScoringModel(msel.Id);
        model.Categories.Single().ScoringWeight = 2.5;
        await Seed(model);

        var json = JsonDocument.Parse(await (await RootClient.GetAsync(Route(msel), Ct)).Content.ReadAsStringAsync(Ct));

        var category = json.RootElement.GetProperty("categories")[0];
        Assert.Equal(2.5, category.GetProperty("scoringWeight").GetDouble());
        Assert.Equal(1, category.GetProperty("options")[0].GetProperty("value").GetDouble());
        Assert.Equal("1", category.GetProperty("displayOrder").GetString());
        Assert.Equal("None", category.GetProperty("scoringOptionSelection").GetString());
    }

    /// <summary>Every role on the Contributors tab reads the model.</summary>
    [Theory]
    [InlineData(MselRole.Owner)]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.MoveEditor)]
    [InlineData(MselRole.Evaluator)]
    [InlineData(MselRole.Viewer)]
    public async Task Get_ForAContributorOfTheMsel_Is200(MselRole role)
    {
        var msel = await SeedMsel(withModel: true);
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Client(actor).GetAsync(Route(msel), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_ForTheCreatorOfTheMsel_Is200()
    {
        var actor = await Actor().SeedAsync();
        var msel = await SeedMsel(withModel: true, createdBy: actor.Id);

        var response = await Client(actor).GetAsync(Route(msel), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_WithViewMsels_Is200()
    {
        var msel = await SeedMsel(withModel: true);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync(Route(msel), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>CreateMsels reads a template's model, as it reads the template it creates MSELs from.</summary>
    [Fact]
    public async Task Get_OfATemplate_WithCreateMsels_Is200()
    {
        var msel = await SeedMsel(withModel: true, isTemplate: true);
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(actor).GetAsync(Route(msel), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_OfAMselThatIsNotATemplate_WithCreateMsels_Is403()
    {
        var msel = await SeedMsel(withModel: true);
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(actor).GetAsync(Route(msel), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Being on one of the MSEL's teams makes a participant, who does not read the equations they are scored by.</summary>
    [Fact]
    public async Task Get_ForAMemberOfOneOfTheMselsTeams_Is403()
    {
        var msel = await SeedMsel(withModel: true);
        var team = TestData.Team(msel.Id);
        await Seed(team);
        var actor = await Actor().OnTeam(team).SeedAsync();

        var response = await Client(actor).GetAsync(Route(msel), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_is_forbidden_for_a_caller_holding_Viewer_only_in_another_msel()
    {
        var msel = await SeedMsel(withModel: true);
        var actor = await Actor().OnNewMsel(MselRole.Viewer).SeedAsync();

        var response = await Client(actor).GetAsync(Route(msel), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_is_forbidden_for_a_caller_holding_EditMsels_only()
    {
        var msel = await SeedMsel(withModel: true);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).GetAsync(Route(msel), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_ForAMselThatIsNotThere_Is404()
    {
        var response = await RootClient.GetAsync($"api/msels/{Guid.NewGuid()}/scoringmodel", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // The writes: PUT, POST presets/{preset}, POST cite/{id}, DELETE
    // ---------------------------------------------------------------------------------------------

    /// <summary>The four writes, each sent against a MSEL that has a model of its own.</summary>
    public static TheoryData<string> Writes => new() { "replace", "preset", "cite", "delete" };

    /// <summary>The MSEL's owner, its creator and an EditMsels holder may each write.</summary>
    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Write_ForAnOwnerOfTheMsel_IsAllowed(string write)
    {
        var msel = await SeedMsel(withModel: true);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Send(Client(actor), write, msel);

        Assert.True(response.IsSuccessStatusCode, $"{write} answered {response.StatusCode}");
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Write_ForTheCreatorOfTheMsel_IsAllowed(string write)
    {
        var actor = await Actor().SeedAsync();
        var msel = await SeedMsel(withModel: true, createdBy: actor.Id);

        var response = await Send(Client(actor), write, msel);

        Assert.True(response.IsSuccessStatusCode, $"{write} answered {response.StatusCode}");
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Write_WithEditMsels_IsAllowed(string write)
    {
        var msel = await SeedMsel(withModel: true);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Send(Client(actor), write, msel);

        Assert.True(response.IsSuccessStatusCode, $"{write} answered {response.StatusCode}");
    }

    /// <summary>An editor of the MSEL may not, as they may not change its scoring model link.</summary>
    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Write_is_forbidden_for_an_editor_of_the_msel(string write)
    {
        var msel = await SeedMsel(withModel: true);
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var response = await Send(Client(actor), write, msel);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(await StoredCategories(msel));
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Write_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel(string write)
    {
        var msel = await SeedMsel(withModel: true);
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        var response = await Send(Client(actor), write, msel);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Write_is_forbidden_for_a_caller_holding_ViewMsels_only(string write)
    {
        var msel = await SeedMsel(withModel: true);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Send(Client(actor), write, msel);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>The CITE evaluation was built from the model, so it is locked until the integrations are removed.</summary>
    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Write_WhileTheMselIsPushed_Is409AndChangesNothing(string write)
    {
        var msel = await SeedMsel(withModel: true, adjust: m =>
        {
            m.Status = MselItemStatus.Deployed;
            m.CiteEvaluationId = Guid.NewGuid();
        });

        var response = await Send(RootClient, write, msel);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Single(await StoredCategories(msel));
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Write_ForAMselThatIsNotThere_Is404(string write)
    {
        var response = await Send(RootClient, write, TestData.Msel());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT msels/{id}/scoringmodel
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A replace stores the model as sent, numbering the categories and options in the order they arrive, and the
    /// MSEL stops linking a CITE model.
    /// </summary>
    [Fact]
    public async Task Replace_StoresTheModelInTheOrderSent_AndClearsTheLinkedModel()
    {
        var msel = await SeedMsel(adjust: m =>
        {
            m.UseCite = true;
            m.CiteScoringModelId = Guid.NewGuid();
            m.CiteScoringModelName = "Linked";
        });
        var body = Body("Harbor Questions");
        body.Categories.Add(Category("Move 2", 2, 2, "Second question", "First question"));
        body.Categories.Add(Category("Move 1", 1, 1, "Only question"));

        var response = await RootClient.PutAsJsonAsync(Route(msel), body, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answered = await ReadAsync<MselScoringModel>(response, JsonOptions);
        Assert.Equal(msel.Id, answered.MselId);
        Assert.Equal(["Move 2", "Move 1"], answered.Categories.Select(c => c.Description));
        Assert.Equal([1, 2], answered.Categories.Select(c => c.DisplayOrder));
        Assert.Equal(["Second question", "First question"], answered.Categories[0].Options.Select(o => o.Description));
        Assert.Equal([1, 2], answered.Categories[0].Options.Select(o => o.DisplayOrder));
        var stored = await ReadBack(db => db.Msels.SingleAsync(m => m.Id == msel.Id, Ct));
        Assert.Null(stored.CiteScoringModelId);
        Assert.Null(stored.CiteScoringModelName);
    }

    /// <summary>A replace swaps the model's contents in place: one model row, none of the old categories or options.</summary>
    [Fact]
    public async Task Replace_OverTheMselsOwnModel_ReplacesItsSettingsCategoriesAndOptions()
    {
        var msel = await SeedMsel(withModel: true);
        var old = await StoredCategories(msel);
        var body = Body("Renamed");
        body.HideScoresOnScoreSheet = true;
        body.Categories.Add(Category("New", 1, 3, "New question"));

        var response = await RootClient.PutAsJsonAsync(Route(msel), body, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var model = await ReadBack(db => db.MselScoringModels.SingleAsync(m => m.MselId == msel.Id, Ct));
        Assert.Equal("Renamed", model.Description);
        Assert.True(model.HideScoresOnScoreSheet);
        var category = Assert.Single(await StoredCategories(msel));
        Assert.DoesNotContain(category.Id, old.Select(c => c.Id));
        Assert.Equal("New question", Assert.Single(category.Options).Description);
        var oldOptionIds = old.SelectMany(c => c.Options).Select(o => o.Id).ToList();
        Assert.False(await ReadBack(db => db.MselScoringOptions.AnyAsync(o => oldOptionIds.Contains(o.Id), Ct)));
    }

    /// <summary>The ids a client sends are not kept, so a body naming another MSEL's rows cannot move them.</summary>
    [Fact]
    public async Task Replace_GivesTheCategoriesAndOptionsNewIds()
    {
        var other = await SeedMsel(withModel: true);
        var otherCategory = Assert.Single(await StoredCategories(other));
        var msel = await SeedMsel();
        var body = Body("Mine");
        var category = Category("Taken", 1, 1, "Question");
        category.Id = otherCategory.Id;
        category.Options[0].Id = otherCategory.Options.First().Id;
        body.Categories.Add(category);

        var response = await RootClient.PutAsJsonAsync(Route(msel), body, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(otherCategory.Id, Assert.Single(await StoredCategories(msel)).Id);
        Assert.Equal(otherCategory.Description, Assert.Single(await StoredCategories(other)).Description);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task Replace_WithNoDescription_Is400(string description)
    {
        var msel = await SeedMsel();

        var response = await RootClient.PutAsJsonAsync(Route(msel), Body(description), JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await ReadBack(db => db.MselScoringModels.AnyAsync(m => m.MselId == msel.Id, Ct)));
    }

    [Fact]
    public async Task Replace_WithACategoryEndingBeforeItStarts_Is400()
    {
        var msel = await SeedMsel();
        var body = Body("Backwards");
        body.Categories.Add(Category("Backwards", 3, 2, "Question"));

        var response = await RootClient.PutAsJsonAsync(Route(msel), body, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>CITE shows the embedded URL in a frame, so only a web address is taken.</summary>
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("/relative/page")]
    [InlineData("ftp://files.example/scores")]
    public async Task Replace_WithAnEmbeddedUrlThatIsNotAWebAddress_Is400(string url)
    {
        var msel = await SeedMsel();
        var body = Body("Embedded");
        body.RightSideDisplay = CiteRightSideDisplay.EmbeddedUrl;
        body.RightSideEmbeddedUrl = url;

        var response = await RootClient.PutAsJsonAsync(Route(msel), body, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Replace_WithAnHttpsEmbeddedUrl_StoresIt()
    {
        var msel = await SeedMsel();
        var body = Body("Embedded");
        body.RightSideDisplay = CiteRightSideDisplay.EmbeddedUrl;
        body.RightSideEmbeddedUrl = " https://dashboards.example/scores ";

        var answered = await ReadAsync<MselScoringModel>(
            await RootClient.PutAsJsonAsync(Route(msel), body, JsonOptions, Ct), JsonOptions);

        Assert.Equal("https://dashboards.example/scores", answered.RightSideEmbeddedUrl);
    }

    /// <summary>The html shown beside the score sheet is sanitized like a move's situation description.</summary>
    [Fact]
    public async Task Replace_SanitizesTheHtmlBlock()
    {
        var msel = await SeedMsel();
        var body = Body("Html");
        body.RightSideDisplay = CiteRightSideDisplay.HtmlBlock;
        body.RightSideHtmlBlock = """<table border="1"><tr><td bgcolor="#FF0000">5</td></tr></table><script>alert(1)</script>""";

        await RootClient.PutAsJsonAsync(Route(msel), body, JsonOptions, Ct);

        var stored = await ReadBack(db => db.MselScoringModels.SingleAsync(m => m.MselId == msel.Id, Ct));
        Assert.DoesNotContain("script", stored.RightSideHtmlBlock);
        Assert.Contains("bgcolor=\"#FF0000\"", stored.RightSideHtmlBlock);
    }

    // ---------------------------------------------------------------------------------------------
    // POST msels/{id}/scoringmodel/presets/{preset}
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Questions by move gives each move a question category shown in that move only, with nothing to select and
    /// no questions yet, and the last move an after-action review.
    /// </summary>
    [Fact]
    public async Task Preset_QuestionsByMove_GivesEachMoveACategoryAndTheLastMoveAnAfterActionReview()
    {
        var msel = await SeedMsel();
        await Seed(TestData.Move(msel.Id, 2), TestData.Move(msel.Id, 0), TestData.Move(msel.Id, 1));

        var response = await RootClient.PostAsync($"{Route(msel)}/presets/QuestionsByMove", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answered = await ReadAsync<MselScoringModel>(response, JsonOptions);
        Assert.Equal($"{msel.Name} Questions by Move", answered.Description);
        Assert.True(answered.DisplayCommentTextBoxes);
        Assert.True(answered.HideScoresOnScoreSheet);
        Assert.True(answered.DisplayScoringModelByMoveNumber);
        Assert.Equal(
            [(0, 0), (1, 1), (2, 2), (2, 2)],
            answered.Categories.Select(c => (c.MoveNumberFirstDisplay, c.MoveNumberLastDisplay)));
        Assert.Equal(ScoringModelPresets.AarHeading, answered.Categories[^1].Description);
        Assert.All(answered.Categories, c =>
        {
            Assert.Equal(CiteScoringOptionSelection.None, c.ScoringOptionSelection);
            Assert.Empty(c.Options);
        });
    }

    [Fact]
    public async Task Preset_QuestionsByMove_ForAMselWithNoMoves_Is400()
    {
        var msel = await SeedMsel();

        var response = await RootClient.PostAsync($"{Route(msel)}/presets/QuestionsByMove", null, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await ReadBack(db => db.MselScoringModels.AnyAsync(m => m.MselId == msel.Id, Ct)));
    }

    [Theory]
    [InlineData("MissionImpact", ScoringModelPresets.MissionImpactName, 4)]
    [InlineData("Likelihood", ScoringModelPresets.LikelihoodName, 2)]
    [InlineData("Nciss", ScoringModelPresets.NcissName, 8)]
    public async Task Preset_StoresTheNamedModel(string preset, string name, int categories)
    {
        var msel = await SeedMsel();

        var answered = await ReadAsync<MselScoringModel>(
            await RootClient.PostAsync($"{Route(msel)}/presets/{preset}", null, Ct), JsonOptions);

        Assert.Equal(name, answered.Description);
        Assert.Equal(categories, answered.Categories.Count);
        Assert.All(answered.Categories, c => Assert.Equal(CiteScoringOptionSelection.Single, c.ScoringOptionSelection));
        Assert.All(answered.Categories, c => Assert.NotEmpty(c.Options));
    }

    /// <summary>The likelihood matrix survives the sanitizer with its colors, so CITE shows it as the guide draws it.</summary>
    [Fact]
    public async Task Preset_Likelihood_KeepsTheMatrixBesideTheScoreSheet()
    {
        var msel = await SeedMsel();

        var answered = await ReadAsync<MselScoringModel>(
            await RootClient.PostAsync($"{Route(msel)}/presets/Likelihood", null, Ct), JsonOptions);

        Assert.Equal(CiteRightSideDisplay.HtmlBlock, answered.RightSideDisplay);
        Assert.Contains("<table", answered.RightSideHtmlBlock);
        Assert.Contains("bgcolor=\"#FF0000\"", answered.RightSideHtmlBlock);
    }

    [Fact]
    public async Task Preset_ReplacesTheMselsOwnModelAndClearsTheLinkedModel()
    {
        var msel = await SeedMsel(withModel: true, adjust: m => m.CiteScoringModelId = Guid.NewGuid());

        await RootClient.PostAsync($"{Route(msel)}/presets/Nciss", null, Ct);

        var model = await ReadBack(db => db.MselScoringModels.SingleAsync(m => m.MselId == msel.Id, Ct));
        Assert.Equal(ScoringModelPresets.NcissName, model.Description);
        Assert.Equal(8, (await StoredCategories(msel)).Count);
        Assert.Null((await ReadBack(db => db.Msels.SingleAsync(m => m.Id == msel.Id, Ct))).CiteScoringModelId);
    }

    [Fact]
    public async Task Preset_ThatIsNotOne_Is400()
    {
        var msel = await SeedMsel();

        var response = await RootClient.PostAsync($"{Route(msel)}/presets/Bogus", null, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // POST msels/{id}/scoringmodel/cite/{scoringModelId}
    // ---------------------------------------------------------------------------------------------

    /// <summary>A CITE model is copied with its settings, categories and options, the enums by name.</summary>
    [Fact]
    public async Task CopyFromCite_StoresACopyOfTheCiteModel()
    {
        var msel = await SeedMsel();
        var citeId = Guid.NewGuid();
        Factory.Cite.GetScoringModelAsync(citeId, Arg.Any<CancellationToken>()).Returns(new ScoringModel
        {
            Id = citeId,
            Description = "Project Lagoon Questions by Move",
            CalculationEquation = "{average}",
            DisplayCommentTextBoxes = true,
            RightSideDisplay = RightSideDisplay.Scoresheet,
            ScoringCategories =
            [
                new ScoringCategory
                {
                    DisplayOrder = 2, Description = "Move 2", MoveNumberFirstDisplay = 2, MoveNumberLastDisplay = 2,
                    ScoringOptionSelection = ScoringOptionSelection.None,
                    ScoringOptions = [new ScoringOption { DisplayOrder = 1, Description = "Pay the ransom?", Value = 1 }]
                },
                new ScoringCategory
                {
                    DisplayOrder = 1, Description = "Move 1", MoveNumberFirstDisplay = 1, MoveNumberLastDisplay = 1,
                    ScoringOptionSelection = ScoringOptionSelection.Multiple, ScoringWeight = 2.5
                }
            ]
        });

        var response = await RootClient.PostAsync($"{Route(msel)}/cite/{citeId}", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answered = await ReadAsync<MselScoringModel>(response, JsonOptions);
        Assert.Equal("Project Lagoon Questions by Move", answered.Description);
        Assert.True(answered.DisplayCommentTextBoxes);
        Assert.Equal(CiteRightSideDisplay.Scoresheet, answered.RightSideDisplay);
        Assert.Equal(["Move 1", "Move 2"], answered.Categories.Select(c => c.Description));
        Assert.Equal(CiteScoringOptionSelection.Multiple, answered.Categories[0].ScoringOptionSelection);
        Assert.Equal(2.5, answered.Categories[0].ScoringWeight);
        Assert.Equal(CiteScoringOptionSelection.None, answered.Categories[1].ScoringOptionSelection);
        Assert.Equal("Pay the ransom?", Assert.Single(answered.Categories[1].Options).Description);
        var stored = await ReadBack(db => db.MselScoringCategories.Where(c => c.MselScoringModelId == msel.Id).ToListAsync(Ct));
        Assert.Contains(stored, c => c.ScoringOptionSelection == CiteScoringOptionSelection.None);
    }

    /// <summary>CITE decides what the caller may read; a model it refuses or does not have is a 403, as for Caster.</summary>
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task CopyFromCite_WhenCiteRefusesTheCaller_Is403(int status)
    {
        var msel = await SeedMsel();
        var citeId = Guid.NewGuid();
        Factory.Cite.GetScoringModelAsync(citeId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException("refused", status, "", new Dictionary<string, IEnumerable<string>>(), null));

        var response = await RootClient.PostAsync($"{Route(msel)}/cite/{citeId}", null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await ReadBack(db => db.MselScoringModels.AnyAsync(m => m.MselId == msel.Id, Ct)));
    }

    /// <summary>Reads the model with the caller's own token, which the request's cancellation follows.</summary>
    [Fact]
    public async Task CopyFromCite_AsksCiteWithTheRequestsCancellationToken()
    {
        var msel = await SeedMsel();
        var citeId = Guid.NewGuid();
        Factory.Cite.GetScoringModelAsync(citeId, Arg.Any<CancellationToken>()).Returns(new ScoringModel { Description = "m" });

        await RootClient.PostAsync($"{Route(msel)}/cite/{citeId}", null, Ct);

        await Factory.Cite.Received(1).GetScoringModelAsync(citeId, Arg.Is<CancellationToken>(x => x.CanBeCanceled));
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE msels/{id}/scoringmodel
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_RemovesTheModelWithItsCategoriesAndOptions()
    {
        var msel = await SeedMsel(withModel: true);

        var response = await RootClient.DeleteAsync(Route(msel), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await ReadBack(db => db.MselScoringModels.AnyAsync(m => m.MselId == msel.Id, Ct)));
        Assert.Empty(await StoredCategories(msel));
        Assert.True(await ReadBack(db => db.Msels.AnyAsync(m => m.Id == msel.Id, Ct)));
    }

    [Fact]
    public async Task Delete_ForAMselWithNoScoringModel_Is404()
    {
        var msel = await SeedMsel();

        var response = await RootClient.DeleteAsync(Route(msel), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Deleting the MSEL deletes its model, by cascade.</summary>
    [Fact]
    public async Task DeletingTheMsel_DeletesItsScoringModel()
    {
        var msel = await SeedMsel(withModel: true);

        var response = await RootClient.DeleteAsync($"api/msels/{msel.Id}", Ct);

        Assert.True(response.IsSuccessStatusCode, $"delete answered {response.StatusCode}");
        Assert.False(await ReadBack(db => db.MselScoringModels.AnyAsync(m => m.MselId == msel.Id, Ct)));
        Assert.Empty(await StoredCategories(msel));
    }

    // ---------------------------------------------------------------------------------------------
    // PUT msels/{id} - linking a CITE model
    // ---------------------------------------------------------------------------------------------

    /// <summary>A MSEL pushes either its own model or a linked one, so it cannot link one while it has its own.</summary>
    [Fact]
    public async Task UpdateMsel_LinkingACiteModelWhileTheMselHasItsOwn_Is409()
    {
        var msel = await SeedMsel(withModel: true, adjust: m => m.UseCite = true);
        var body = MselBody(msel);
        body.CiteScoringModelId = Guid.NewGuid();

        var response = await RootClient.PutAsJsonAsync($"api/msels/{msel.Id}", body, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Null((await ReadBack(db => db.Msels.SingleAsync(m => m.Id == msel.Id, Ct))).CiteScoringModelId);
    }

    [Fact]
    public async Task UpdateMsel_LinkingACiteModelWithoutOneOfItsOwn_LinksIt()
    {
        var msel = await SeedMsel(adjust: m => m.UseCite = true);
        var body = MselBody(msel);
        body.CiteScoringModelId = Guid.NewGuid();

        var response = await RootClient.PutAsJsonAsync($"api/msels/{msel.Id}", body, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(body.CiteScoringModelId, (await ReadBack(db => db.Msels.SingleAsync(m => m.Id == msel.Id, Ct))).CiteScoringModelId);
    }

    // ---------------------------------------------------------------------------------------------
    // POST msels/{id}/copy and the JSON file
    // ---------------------------------------------------------------------------------------------

    /// <summary>A copy carries the model under new ids, so a MSEL made from a template starts with its scoring model.</summary>
    [Fact]
    public async Task Copy_OfAMselWithItsOwnScoringModel_CopiesTheModel()
    {
        var msel = await SeedMsel(withModel: true);
        var original = Assert.Single(await StoredCategories(msel));

        var response = await RootClient.PostAsync($"api/msels/{msel.Id}/copy", null, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var copy = await response.Content.ReadFromJsonAsync<ViewModels.Msel>(JsonOptions, Ct);
        var model = await ReadBack(db => db.MselScoringModels.SingleAsync(m => m.MselId == copy.Id, Ct));
        Assert.Equal($"scoring-model-{msel.Id}", model.Description);
        var category = Assert.Single(await StoredCategories(copy.Id));
        Assert.NotEqual(original.Id, category.Id);
        Assert.Equal(original.Options.Select(o => o.Description).Order(), category.Options.Select(o => o.Description).Order());
        Assert.DoesNotContain(category.Options.Select(o => o.Id), id => original.Options.Any(o => o.Id == id));
        Assert.Single(await StoredCategories(msel));
    }

    /// <summary>The JSON file carries the model, and importing it gives the new MSEL a copy of its own.</summary>
    [Fact]
    public async Task DownloadThenImport_CarriesTheModel()
    {
        var msel = await SeedMsel(withModel: true);

        var download = await RootClient.GetAsync($"api/msels/{msel.Id}/json", Ct);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var file = await download.Content.ReadAsStringAsync(Ct);
        file = file.Replace(msel.Name, "Imported " + msel.Id);

        var imported = await Import(file);

        var model = await ReadBack(db => db.MselScoringModels.SingleAsync(m => m.MselId == imported.Id, Ct));
        Assert.Equal($"scoring-model-{msel.Id}", model.Description);
        var category = Assert.Single(await StoredCategories(imported.Id));
        Assert.Equal(2, category.Options.Count);
        Assert.Single(await StoredCategories(msel));
    }

    /// <summary>A file carrying the MSEL's own model and a linked one keeps its own and drops the link, unchecked.</summary>
    [Fact]
    public async Task Import_OfAFileWithItsOwnModelAndALinkedOne_DropsTheLink()
    {
        var file = TestData.Msel();
        file.Name = "Imported " + Guid.NewGuid();
        file.UseCite = true;
        file.CiteScoringModelId = Guid.NewGuid();
        file.CiteScoringModelName = "Linked";
        file.ScoringModel = TestData.MselScoringModel(file.Id);
        var json = JsonSerializer.Serialize(file, new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.IgnoreCycles });

        var imported = await Import(json);

        var stored = await ReadBack(db => db.Msels.SingleAsync(m => m.Id == imported.Id, Ct));
        Assert.Null(stored.CiteScoringModelId);
        Assert.Null(stored.CiteScoringModelName);
        Assert.True(await ReadBack(db => db.MselScoringModels.AnyAsync(m => m.MselId == imported.Id, Ct)));
        await Factory.Cite.DidNotReceive().GetScoringModelsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>());
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static string Route(MselEntity msel) => $"api/msels/{msel.Id}/scoringmodel";

    private async Task<HttpResponseMessage> Send(HttpClient client, string write, MselEntity msel)
    {
        switch (write)
        {
            case "replace":
                var body = Body("Replaced");
                body.Categories.Add(Category("Replaced", 1, 1, "Question"));
                return await client.PutAsJsonAsync(Route(msel), body, JsonOptions, Ct);
            case "preset":
                return await client.PostAsync($"{Route(msel)}/presets/MissionImpact", null, Ct);
            case "cite":
                var citeId = Guid.NewGuid();
                Factory.Cite.GetScoringModelAsync(citeId, Arg.Any<CancellationToken>()).Returns(new ScoringModel
                {
                    Description = "from CITE",
                    ScoringCategories = [new ScoringCategory { Description = "c", MoveNumberFirstDisplay = 1, MoveNumberLastDisplay = 1 }]
                });
                return await client.PostAsync($"{Route(msel)}/cite/{citeId}", null, Ct);
            default:
                return await client.DeleteAsync(Route(msel), Ct);
        }
    }

    private async Task<MselEntity> SeedMsel(bool withModel = false, Guid? createdBy = null, bool isTemplate = false, Action<MselEntity> adjust = null)
    {
        var msel = TestData.Msel(isTemplate: isTemplate);
        if (createdBy != null)
            msel.CreatedBy = (Guid)createdBy;
        adjust?.Invoke(msel);
        await Seed(msel);
        if (withModel)
            await Seed(TestData.MselScoringModel(msel.Id));

        return msel;
    }

    private Task<List<MselScoringCategoryEntity>> StoredCategories(MselEntity msel) => StoredCategories(msel.Id);

    private Task<List<MselScoringCategoryEntity>> StoredCategories(Guid mselId) =>
        ReadBack(db => db.MselScoringCategories.Include(c => c.Options).Where(c => c.MselScoringModelId == mselId).ToListAsync(Ct));

    private async Task<ViewModels.Msel> Import(string json)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(json, Encoding.UTF8, "application/json"), "ToUpload", "msel.json");

        var response = await RootClient.PostAsync("api/msels/json", form, Ct);

        Assert.True(response.IsSuccessStatusCode, $"import answered {response.StatusCode}");
        return await response.Content.ReadFromJsonAsync<ViewModels.Msel>(JsonOptions, Ct);
    }

    private static MselScoringModel Body(string description) => new()
    {
        Description = description,
        CalculationEquation = "{average}",
        DisplayCommentTextBoxes = true,
        RightSideDisplay = CiteRightSideDisplay.Scoresheet
    };

    private static MselScoringCategory Category(string description, int firstMove, int lastMove, params string[] questions) => new()
    {
        Description = description,
        CalculationEquation = "{max}",
        ScoringWeight = 1,
        MoveNumberFirstDisplay = firstMove,
        MoveNumberLastDisplay = lastMove,
        ScoringOptionSelection = CiteScoringOptionSelection.None,
        Options = questions.Select(q => new MselScoringOption { Description = q, Value = 1 }).ToList()
    };

    private static ViewModels.Msel MselBody(MselEntity entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        Description = entity.Description,
        Status = entity.Status,
        UseCite = entity.UseCite,
        CiteScoringModelId = entity.CiteScoringModelId
    };
}
