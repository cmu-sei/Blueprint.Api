// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

public class EnumRangeEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    /// <summary>A value outside every member of the enum is sent numerically.</summary>
    private const int Undefined = 999;

    [Theory]
    [InlineData("POST", "status", HttpStatusCode.Created, "api/msels")]
    [InlineData("PUT", "status", HttpStatusCode.OK, "api/msels/{0}")]
    [InlineData("POST", "playerIntegrationType", HttpStatusCode.Created, "api/msels")]
    [InlineData("PUT", "playerIntegrationType", HttpStatusCode.OK, "api/msels/{0}")]
    [InlineData("POST", "galleryIntegrationType", HttpStatusCode.Created, "api/msels")]
    [InlineData("PUT", "galleryIntegrationType", HttpStatusCode.OK, "api/msels/{0}")]
    [InlineData("POST", "citeIntegrationType", HttpStatusCode.Created, "api/msels")]
    [InlineData("PUT", "citeIntegrationType", HttpStatusCode.OK, "api/msels/{0}")]
    [InlineData("POST", "steamfitterIntegrationType", HttpStatusCode.Created, "api/msels")]
    [InlineData("PUT", "steamfitterIntegrationType", HttpStatusCode.OK, "api/msels/{0}")]
    [InlineData("POST", "casterIntegrationType", HttpStatusCode.Created, "api/msels")]
    [InlineData("PUT", "casterIntegrationType", HttpStatusCode.OK, "api/msels/{0}")]
    public async Task Write_stores_and_returns_an_undefined_Msel_enum(string method, string member, HttpStatusCode status, string route)
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var id = new Dictionary<string, Guid> { ["POST"] = Guid.NewGuid(), ["PUT"] = msel.Id }[method];
        var body = new JsonObject { ["id"] = id, ["name"] = "numeric enum", [member] = Undefined };
        using var request = new HttpRequestMessage(new HttpMethod(method),
            string.Format(route, id)) { Content = JsonContent.Create(body) };

        var response = await RootClient.SendAsync(request, Ct);

        Assert.Equal(status, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(Undefined, document.RootElement.GetProperty(member).GetInt32());
        var stored = await ReadBack(rb => rb.Msels.SingleAsync(x => x.Id == id, Ct));
        Assert.Equal(Undefined, MselMembers(stored)[member]);
    }

    [Theory]
    [InlineData("POST", "scenarioEventType", "api/scenarioEvents")]
    [InlineData("PUT", "scenarioEventType", "api/scenarioEvents/{0}")]
    [InlineData("POST", "taskType", "api/scenarioEvents")]
    [InlineData("PUT", "taskType", "api/scenarioEvents/{0}")]
    [InlineData("POST", "action", "api/scenarioEvents")]
    [InlineData("PUT", "action", "api/scenarioEvents/{0}")]
    [InlineData("POST", "triggerCondition", "api/scenarioEvents")]
    [InlineData("PUT", "triggerCondition", "api/scenarioEvents/{0}")]
    public async Task Write_stores_and_returns_an_undefined_scenario_event_enum(string method, string member, string route)
    {
        var msel = TestData.Msel();
        var scenarioEvent = TestData.ScenarioEvent(msel.Id);
        await Seed(msel, scenarioEvent);
        var id = new Dictionary<string, Guid> { ["POST"] = Guid.NewGuid(), ["PUT"] = scenarioEvent.Id }[method];
        using var request = EventRequest(method, member, id, msel.Id, route);

        var response = await RootClient.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answered = (await response.Content.ReadFromJsonAsync<List<ViewModels.ScenarioEvent>>(JsonOptions, Ct)).Single(x => x.Id == id);
        Assert.Equal(Undefined, EventMembers(answered)[member]);
        var stored = await ReadBack(rb => rb.ScenarioEvents.Include(x => x.SteamfitterTask).SingleAsync(x => x.Id == id, Ct));
        Assert.Equal(Undefined, EventMembers(stored)[member]);
    }

    private static Dictionary<string, int> MselMembers(Data.Models.MselEntity row) => new()
    {
        ["status"] = (int)row.Status,
        ["playerIntegrationType"] = (int)row.PlayerIntegrationType,
        ["galleryIntegrationType"] = (int)row.GalleryIntegrationType,
        ["citeIntegrationType"] = (int)row.CiteIntegrationType,
        ["steamfitterIntegrationType"] = (int)row.SteamfitterIntegrationType,
        ["casterIntegrationType"] = (int)row.CasterIntegrationType
    };

    private static HttpRequestMessage EventRequest(string method, string member, Guid id, Guid mselId, string route)
    {
        var task = new JsonObject { ["name"] = "numeric task enum", ["taskType"] = 1, ["action"] = 11, ["triggerCondition"] = 1 };
        var body = new JsonObject { ["id"] = id, ["mselId"] = mselId, ["scenarioEventType"] = 10, ["steamfitterTask"] = task };
        var targets = new Dictionary<string, JsonObject>
        {
            ["scenarioEventType"] = body, ["taskType"] = task, ["action"] = task, ["triggerCondition"] = task
        };
        targets[member][member] = Undefined;
        return new HttpRequestMessage(new HttpMethod(method),
            string.Format(route, id)) { Content = JsonContent.Create(body) };
    }

    private static Dictionary<string, int> EventMembers(ViewModels.ScenarioEvent row) => new()
    {
        ["scenarioEventType"] = (int)row.ScenarioEventType,
        ["taskType"] = (int)row.SteamfitterTask.TaskType,
        ["action"] = (int)row.SteamfitterTask.Action,
        ["triggerCondition"] = (int)row.SteamfitterTask.TriggerCondition
    };

    private static Dictionary<string, int> EventMembers(Data.Models.ScenarioEventEntity row) => new()
    {
        ["scenarioEventType"] = (int)row.ScenarioEventType,
        ["taskType"] = (int)row.SteamfitterTask.TaskType,
        ["action"] = (int)row.SteamfitterTask.Action,
        ["triggerCondition"] = (int)row.SteamfitterTask.TriggerCondition
    };
}
