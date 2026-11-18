// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
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
/// The MSEL assistant on techniques: it is shown the releases and the MSEL's techniques, adds techniques through the
/// MSEL technique service, tags injects with them, and turns a column of technique names into a technique column.
/// </summary>
public class MselAssistTechniqueEndpointTests(DatabaseFixture fixture, AiEnabledFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<AiEnabledFactory>
{
    private readonly IAiModelClient _model = factory.Model;
    private string _prompt;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _model.ClearSubstitute();
    }

    /// <summary>The model is told which releases Blueprint has, which one the MSEL takes a domain from, and its techniques.</summary>
    [Fact]
    public async Task Ask_ShowsTheModelTheReleasesAndTheMselsTechniques()
    {
        var (msel, _, release, _) = await SeedMsel(pool: "T1003");
        Answer(new { reply = "Nothing to do.", changes = Array.Empty<object>() });
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Ask(owner, msel.Id);

        Assert.Contains($"- {release.Name} 18.0, domain {release.Domain}, attack (this MSEL's {release.Domain} techniques come from it)", _prompt);
        Assert.Contains("- T1003 OS Credential Dumping (" + release.Domain + ", attack)", _prompt);
        Assert.Contains("- Techniques, AttackTechnique: Ids of the MSEL's attack techniques, separated by \", \"", _prompt);
        Assert.Contains("The planner may change: ", _prompt);
        Assert.Contains("techniques (adding them)", _prompt);
    }

    /// <summary>An editor may tag injects but not add techniques, so the add is left out with the reason.</summary>
    [Fact]
    public async Task Ask_ByAnEditor_LeavesOutTheTechniqueAdd()
    {
        var (msel, _, release, _) = await SeedMsel();
        Answer(new { reply = "Here you go.", changes = new object[] { new { op = "technique.add", technique = "T1059", domain = release.Domain } } });
        var editor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var result = await Read<MselAssistResult>(await Ask(editor, msel.Id));

        Assert.Empty(result.Changes);
        Assert.Contains(result.Warnings, w => w.Contains("only the MSEL's owner can change its techniques"));
    }

    /// <summary>Applying adds the technique to the MSEL's techniques and writes its id on the inject.</summary>
    [Fact]
    public async Task Apply_AddsATechniqueAndTagsAnInjectWithIt()
    {
        var (msel, fields, release, events) = await SeedMsel();
        Answer(new
        {
            reply = "Here you go.",
            changes = new object[]
            {
                new { op = "technique.add", technique = "t1059", domain = release.Domain },
                new { op = "inject.update", target = "E1", values = new Dictionary<string, string> { ["Techniques"] = "T1059 Command and Scripting Interpreter" } }
            }
        });
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var proposal = await Read<MselAssistResult>(await Ask(owner, msel.Id));

        var result = await Read<MselAssistApplyResult>(await Apply(owner, msel.Id, proposal.Changes.ToArray()));

        Assert.All(result.Results, r => Assert.True(r.Applied, $"{r.Label}: {r.Error}"));
        await using var db = NewContext();
        var pooled = await db.MselTechniques.Include(m => m.Technique).Where(m => m.MselId == msel.Id).Select(m => m.Technique.ExternalId).ToListAsync(Ct);
        Assert.Equal(["T1059"], pooled);
        var value = await db.DataValues.SingleAsync(v => v.DataFieldId == fields["Techniques"].Id && v.ScenarioEventId == events[0].Id, Ct);
        Assert.Equal("T1059", value.Value);
    }

    /// <summary>
    /// Turning a text column of technique names into a technique column keeps only the ids, adds those techniques to the
    /// MSEL's techniques, and reports the ones Blueprint does not have.
    /// </summary>
    [Fact]
    public async Task Apply_TurnsAColumnOfTechniqueNamesIntoATechniqueColumn()
    {
        var (msel, fields, _, events) = await SeedMsel(pool: "T1003");
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var result = await Read<MselAssistApplyResult>(await Apply(owner, msel.Id,
            new MselChange { Op = "column.update", Target = "Mapped", DataType = "AttackTechnique" }));

        var outcome = Assert.Single(result.Results);
        Assert.True(outcome.Applied, outcome.Error);
        Assert.Contains("2 techniques, 1 of them added to the MSEL's techniques; not found, so dropped: T9999", outcome.Label);
        await using var db = NewContext();
        var column = await db.DataFields.SingleAsync(f => f.Id == fields["Mapped"].Id, Ct);
        Assert.Equal(DataFieldType.AttackTechnique, column.DataType);
        Assert.True(column.IsMultiSelect);
        var values = await db.DataValues.Where(v => v.DataFieldId == column.Id).ToDictionaryAsync(v => v.ScenarioEventId.Value, v => v.Value, Ct);
        Assert.Equal("T1059, T1003", values[events[0].Id]);
        Assert.Null(values[events[1].Id]);
        var pooled = await db.MselTechniques.Include(m => m.Technique).Where(m => m.MselId == msel.Id).Select(m => m.Technique.ExternalId).OrderBy(x => x).ToListAsync(Ct);
        Assert.Equal(["T1003", "T1059"], pooled);
    }

    private void Answer(object answer) =>
        _model.InvokeToolAsync(default, default, default, default, default, default)
            .ReturnsForAnyArgs(callInfo =>
            {
                _prompt = callInfo.ArgAt<string>(1);
                return JsonSerializer.SerializeToElement(answer);
            });

    private Task<HttpResponseMessage> Ask(TestActor actor, Guid mselId) =>
        Client(actor).PostAsJsonAsync($"/api/msels/{mselId}/ai-assist", new MselAssistRequest { Prompt = "Map the techniques" }, Ct);

    private Task<HttpResponseMessage> Apply(TestActor actor, Guid mselId, params MselChange[] changes) =>
        Client(actor).PostAsJsonAsync($"/api/msels/{mselId}/ai-assist/apply", new MselAssistApply { Changes = changes.ToList() }, Ct);

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
    }

    // a release (domain unique to the test) with T1059 and T1003; a MSEL with a Title column, a technique column, and a text
    // column Mapped that names techniques on E1 and only an unknown one on E2; the pool optionally holds one technique
    private async Task<(MselEntity, Dictionary<string, DataFieldEntity>, TechniqueReleaseEntity, List<ScenarioEventEntity>)> SeedMsel(string pool = null)
    {
        var release = new TechniqueReleaseEntity
        {
            Id = Guid.NewGuid(), Domain = "attacks-" + Guid.NewGuid().ToString("N")[..8], Name = "Attack Catalog", Version = "18.0",
            ReleaseDate = new DateTime(2025, 10, 28, 0, 0, 0, DateTimeKind.Utc), IsDefensive = false
        };
        var t1059 = new TechniqueEntity { Id = Guid.NewGuid(), TechniqueReleaseId = release.Id, ExternalId = "T1059", Name = "Command and Scripting Interpreter" };
        var t1003 = new TechniqueEntity { Id = Guid.NewGuid(), TechniqueReleaseId = release.Id, ExternalId = "T1003", Name = "OS Credential Dumping" };
        var msel = TestData.Msel();
        var fields = new Dictionary<string, DataFieldEntity>
        {
            ["Title"] = TestData.DataField(msel.Id, dataType: DataFieldType.String, displayOrder: 1, name: "Title"),
            ["Techniques"] = TestData.DataField(msel.Id, dataType: DataFieldType.AttackTechnique, displayOrder: 2, name: "Techniques"),
            ["Mapped"] = TestData.DataField(msel.Id, dataType: DataFieldType.String, displayOrder: 3, name: "Mapped")
        };
        foreach (var field in fields.Values)
            field.Description = null;
        var first = TestData.ScenarioEvent(msel.Id, deltaSeconds: 10 * 60);
        var second = TestData.ScenarioEvent(msel.Id, deltaSeconds: 20 * 60);
        List<object> seed = [release, t1059, t1003, msel, .. fields.Values, first, second,
            TestData.DataValue(fields["Title"].Id, first.Id, "Beaconing"),
            TestData.DataValue(fields["Techniques"].Id, first.Id, null),
            TestData.DataValue(fields["Mapped"].Id, first.Id, "T1059 Command and Scripting Interpreter; T1003 OS Credential Dumping"),
            TestData.DataValue(fields["Title"].Id, second.Id, "Wire transfer"),
            TestData.DataValue(fields["Mapped"].Id, second.Id, "T9999 Made up")];
        if (pool != null)
            seed.Add(new MselTechniqueEntity(msel.Id, pool == "T1059" ? t1059.Id : t1003.Id) { Id = Guid.NewGuid() });
        await Seed([.. seed]);
        release.Techniques.Add(t1059);
        release.Techniques.Add(t1003);
        return (msel, fields, release, [first, second]);
    }
}
