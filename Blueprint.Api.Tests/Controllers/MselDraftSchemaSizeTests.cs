// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using NSubstitute;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// Bedrock's strict tool use refuses a schema whose "compiled grammar is too large", and without strict mode Sonnet writes the
/// nested parts of the design in its own call markup. Probed against Bedrock (October 2026), free-text string fields are what
/// count: a flat object takes 28, integers and enums far more, and the design's schema at its fullest (Gallery and CITE, so cards
/// and duties too) was accepted with 40 leaf fields and refused with 41. This keeps it two under. Anything added to the design
/// must take a field out, or be written in a later step, as the move briefings and the catalog metadata are.
/// </summary>
public class MselDraftSchemaSizeTests(DatabaseFixture fixture, AiEnabledFactory factory) : ApiTestBase(fixture, factory), IClassFixture<AiEnabledFactory>
{
    private const int LeafFieldsBedrockAccepts = 38;

    [Fact]
    public async Task DesignSchema_StaysWithinWhatBedrockStrictModeTakes()
    {
        factory.Cite.GetTeamTypesAsync(TestContext.Current.CancellationToken).Returns(new List<Cite.Api.Client.TeamType>());
        JsonObject schema = null;
        factory.Model.InvokeToolAsync(default, default, default, default, default, TestContext.Current.CancellationToken)
            .ReturnsForAnyArgs(call => { schema = call.ArgAt<JsonObject>(4); return JsonDocument.Parse("{}").RootElement; });
        var template = TestData.Msel(isTemplate: true);
        template.UseGallery = true;
        template.UseCite = true;
        var excon = TestData.Team(template.Id);
        excon.ShortName = "EXCON";
        await Seed(template, excon, TestData.DataField(template.Id, name: "Title", displayOrder: 1));
        var framework = TestData.CompetencyFramework();
        await Seed([framework, .. Enumerable.Range(1, 3).Select(i => TestData.Competency(framework.Id, idNumber: $"T{i:0000}"))]);
        var planner = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(planner).PostAsJsonAsync("/api/msels/ai-draft/design", new MselDraftRequest
        {
            TemplateMselId = template.Id,
            Prompt = "x",
            Design = new MselDraftDesign { Format = "Discussion", CiteAssessment = "QuestionsByMove", Moves = 4, DurationMinutes = 120, ProfilePages = true },
            CompetencyFrameworkIds = [framework.Id]
        }, JsonOptions, Ct);

        Assert.True(schema != null, await response.Content.ReadAsStringAsync(Ct));
        var strict = BedrockAiModelClient.StrictSchema(schema);
        // the schema as Bedrock sees it, for probing its limit by hand
        if (System.Environment.GetEnvironmentVariable("BLUEPRINT_SCHEMA_OUT") is string path && path.Length > 0)
            await System.IO.File.WriteAllTextAsync(path, strict.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Ct);
        Assert.True(LeafFields(strict) <= LeafFieldsBedrockAccepts, $"The design schema has {LeafFields(strict)} leaf fields:\n{strict.ToJsonString(new JsonSerializerOptions { WriteIndented = true })}");
    }

    private static int LeafFields(JsonNode node) => node["type"]?.GetValue<string>() switch
    {
        "object" => node["properties"]?.AsObject().Sum(p => LeafFields(p.Value)) ?? 0,
        "array" => LeafFields(node["items"]),
        _ => 1
    };
}
