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
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// <c>POST msels/ai-draft</c> and <c>msels/ai-draft/import</c> - the competencies a draft assigns come from
/// a fixed list, and land on the injects, the teams and the MSEL.
/// </summary>
public class MselDraftCompetencyEndpointTests(DatabaseFixture fixture, AiEnabledFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<AiEnabledFactory>
{
    private readonly IAiModelClient _model = factory.Model;
    private string _prompt;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _model.ClearSubstitute();
    }

    /// <summary>A chosen framework's leaf competencies are the list; its categories and other frameworks are not.</summary>
    [Fact]
    public async Task Draft_WithAFramework_OffersOnlyItsLeafCompetencies()
    {
        var (template, _) = await SeedTemplate();
        var (framework, _, _) = await SeedFramework("T0161", "T0175");
        await SeedFramework("K0001");
        Answer(Draft(mselCompetencies: ["T0161"]));
        var actor = await Planner();

        var result = await Read<MselDraftResult>(await PostDraft(actor, template.Id, framework.Id));

        Assert.Contains("T0161: Analyze network traffic", _prompt);
        Assert.Contains("T0175: Respond to incidents", _prompt);
        Assert.DoesNotContain("K0001", _prompt);
        Assert.DoesNotContain("CAT: ", _prompt);
        Assert.Equal(["T0161"], result.Draft.Competencies);
        Assert.Equal("Analyze network traffic", Assert.Single(result.Competencies).ShortName);
    }

    [Fact]
    public async Task Draft_DropsCompetenciesNotInTheListAndCollectsTheRestOnTheMsel()
    {
        var (template, _) = await SeedTemplate();
        var (framework, _, _) = await SeedFramework("T0161", "T0175");
        Answer(Draft(injectCompetencies: ["t0161", "K9999"], teamCompetencies: ["T0175", "CAT"]));
        var actor = await Planner();

        var result = await Read<MselDraftResult>(await PostDraft(actor, template.Id, framework.Id));

        Assert.Equal(["T0161"], result.Draft.Injects[0].Competencies);
        Assert.Equal(["T0175"], result.Draft.Teams[0].Competencies);
        Assert.Equal(["T0161", "T0175"], result.Draft.Competencies);
        Assert.Contains(result.Warnings, w => w.Contains("CAT, K9999"));
    }

    /// <summary>With no framework chosen, the template's own MSEL competencies are the list.</summary>
    [Fact]
    public async Task Draft_WithoutAFramework_OffersTheTemplatesCompetencies()
    {
        var (template, _) = await SeedTemplate();
        var (_, leaves, _) = await SeedFramework("T0161", "T0175");
        await Seed(TestData.MselCompetency(template.Id, leaves[0].Id));
        Answer(Draft());
        var actor = await Planner();

        await PostDraft(actor, template.Id);

        Assert.Contains("T0161: Analyze network traffic", _prompt);
        Assert.DoesNotContain("T0175", _prompt);
    }

    [Fact]
    public async Task Draft_WithMoreCompetenciesThanTheLimit_IsRefusedWithoutAskingTheModel()
    {
        var (template, _) = await SeedTemplate();
        var ids = Enumerable.Range(0, 2501).Select(i => $"X{i:0000}").ToArray();
        var (framework, _, _) = await SeedFramework(ids);
        Answer(Draft());
        var actor = await Planner();

        var response = await PostDraft(actor, template.Id, framework.Id);

        Assert.False(response.IsSuccessStatusCode);
        Assert.Null(_prompt);
    }

    /// <summary>Import fills the Competency column and its options, the team's and the MSEL's competencies.</summary>
    [Fact]
    public async Task Import_PutsTheCompetenciesOnTheInjectsTeamsAndMsel()
    {
        var (template, competencyField) = await SeedTemplate();
        var (framework, leaves, _) = await SeedFramework("T0161", "T0175");
        var draft = JsonSerializer.Deserialize<MselDraft>(Draft(injectCompetencies: ["T0161", "K9999"], teamCompetencies: ["T0175"]), JsonOptions);
        var actor = await Planner();

        var response = await Client(actor).PostAsJsonAsync("/api/msels/ai-draft/import",
            new MselDraftImport { TemplateMselId = template.Id, Draft = draft, CompetencyFrameworkIds = [framework.Id] }, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Read<MselImportResult>(response);
        var mselId = result.Msel.Id;
        Assert.Contains(result.Changes, c => c.Message.Contains("K9999"));

        var field = await ReadBack(rb => rb.DataFields.Include(f => f.DataOptions)
            .SingleAsync(f => f.MselId == mselId && f.DataType == DataFieldType.Competency, Ct));
        Assert.Equal("T0161", Assert.Single(field.DataOptions).OptionName);
        Assert.Equal("Analyze network traffic", field.DataOptions.Single().OptionValue);
        var value = await ReadBack(rb => rb.DataValues.SingleAsync(v => v.DataFieldId == field.Id, Ct));
        Assert.Equal("T0161", value.Value);

        var teamCompetencies = await ReadBack(rb => rb.TeamCompetencies
            .Where(tc => tc.Team.MselId == mselId && tc.Team.ShortName == "BLUE").Select(tc => tc.CompetencyId).ToListAsync(Ct));
        Assert.Equal([leaves[1].Id], teamCompetencies);
        var mselCompetencies = await ReadBack(rb => rb.MselCompetencies
            .Where(mc => mc.MselId == mselId).Select(mc => mc.CompetencyId).ToListAsync(Ct));
        Assert.Equal(new[] { leaves[0].Id, leaves[1].Id }.OrderBy(i => i), mselCompetencies.OrderBy(i => i));
        // the template's column is untouched
        Assert.False(await ReadBack(rb => rb.DataOptions.AnyAsync(o => o.DataFieldId == competencyField.Id, Ct)));
    }

    // ---------------------------------------------------------------------------------------------

    private Task<TestActor> Planner() =>
        Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

    private void Answer(string json) =>
        _model.InvokeToolAsync(default, default, default, default, default, default)
            .ReturnsForAnyArgs(callInfo =>
            {
                _prompt = callInfo.ArgAt<string>(1);
                return JsonDocument.Parse(json).RootElement.Clone();
            });

    private static string Draft(string[] injectCompetencies = null, string[] teamCompetencies = null, string[] mselCompetencies = null) =>
        JsonSerializer.Serialize(new
        {
            name = "Port closure",
            description = "A hurricane closes the port.",
            durationMinutes = 120,
            moves = new[] { new { moveNumber = 0, description = "Landfall", startMinutes = 0 } },
            teams = new[] { new { name = "Blue Team", shortName = "BLUE", description = "Defenders", competencies = teamCompetencies ?? [] } },
            organizations = Array.Empty<object>(),
            injects = new[]
            {
                new { minutes = 10, eventType = "Inject", values = new Dictionary<string, string> { ["Title"] = "Port closed" }, competencies = injectCompetencies ?? [] }
            },
            competencies = mselCompetencies ?? []
        });

    private async Task<T> Read<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);

    private Task<HttpResponseMessage> PostDraft(TestActor actor, Guid templateId, params Guid[] frameworkIds) =>
        Client(actor).PostAsJsonAsync("/api/msels/ai-draft",
            new MselDraftRequest { TemplateMselId = templateId, Prompt = "A hurricane closes the port.", CompetencyFrameworkIds = [.. frameworkIds] },
            JsonOptions, Ct);

    /// <summary>A template with a Title column and a Competency column.</summary>
    private async Task<(MselEntity, DataFieldEntity Competency)> SeedTemplate()
    {
        var template = TestData.Msel(isTemplate: true);
        var title = TestData.DataField(template.Id, name: "Title", displayOrder: 1);
        var competency = TestData.DataField(template.Id, dataType: DataFieldType.Competency, name: "Competencies", displayOrder: 2);
        await Seed(template, title, competency);
        return (template, competency);
    }

    /// <summary>A framework with a category "CAT" over one leaf competency per ID number.</summary>
    private async Task<(CompetencyFrameworkEntity, List<CompetencyEntity> Leaves, CompetencyEntity Category)> SeedFramework(params string[] ids)
    {
        var names = new Dictionary<string, string> { ["T0161"] = "Analyze network traffic", ["T0175"] = "Respond to incidents" };
        var framework = TestData.CompetencyFramework();
        var category = TestData.Competency(framework.Id, "CAT");
        category.ShortName = "Tasks";
        var leaves = ids.Select((id, i) =>
        {
            var leaf = TestData.Competency(framework.Id, id, parentId: category.Id);
            leaf.ShortName = names.GetValueOrDefault(id, id);
            leaf.SortOrder = i;
            return leaf;
        }).ToList();
        await Seed([framework, category, .. leaves]);
        return (framework, leaves, category);
    }
}
