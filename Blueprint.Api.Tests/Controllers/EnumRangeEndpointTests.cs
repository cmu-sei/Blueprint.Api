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
        var stored = await ReadBack(rb => rb.ScenarioEvents.SingleAsync(x => x.Id == id, Ct));
        Assert.Equal(Undefined, EventMembers(stored)[member]);
    }

    /// <remarks>
    /// Unlike the event's own enums, an automation task's are refused: the engine would have nothing to run, or no
    /// rule to start or stop it by.
    /// </remarks>
    [Theory]
    [InlineData("action")]
    [InlineData("triggerCondition")]
    [InlineData("iterationTermination")]
    public async Task Write_refuses_an_undefined_automation_task_enum(string member)
    {
        var msel = TestData.Msel();
        var scenarioEvent = TestData.ScenarioEvent(msel.Id);
        await Seed(msel, scenarioEvent);
        var task = new JsonObject
        {
            ["name"] = "numeric task enum",
            ["action"] = 100,
            ["actionParameters"] = new JsonObject { ["Text"] = "hello" },
            ["triggerCondition"] = 0,
            ["iterationTermination"] = 0,
            ["iterations"] = 1,
            ["enabled"] = true
        };
        task[member] = Undefined;
        var body = new JsonObject
        {
            ["id"] = scenarioEvent.Id, ["mselId"] = msel.Id, ["scenarioEventType"] = 10,
            ["automationTasks"] = new JsonArray(task)
        };
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/scenarioEvents/{scenarioEvent.Id}")
        {
            Content = JsonContent.Create(body)
        };

        var response = await RootClient.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await ReadBack(rb => rb.AutomationTasks.AnyAsync(x => x.ScenarioEventId == scenarioEvent.Id, Ct)));
    }

    private static Dictionary<string, int> MselMembers(Data.Models.MselEntity row) => new()
    {
        ["status"] = (int)row.Status,
        ["playerIntegrationType"] = (int)row.PlayerIntegrationType,
        ["galleryIntegrationType"] = (int)row.GalleryIntegrationType,
        ["citeIntegrationType"] = (int)row.CiteIntegrationType,
        ["casterIntegrationType"] = (int)row.CasterIntegrationType
    };

    private static HttpRequestMessage EventRequest(string method, string member, Guid id, Guid mselId, string route)
    {
        var body = new JsonObject { ["id"] = id, ["mselId"] = mselId, ["scenarioEventType"] = 10 };
        body[member] = Undefined;
        return new HttpRequestMessage(new HttpMethod(method),
            string.Format(route, id)) { Content = JsonContent.Create(body) };
    }

    private static Dictionary<string, int> EventMembers(ViewModels.ScenarioEvent row) => new()
    {
        ["scenarioEventType"] = (int)row.ScenarioEventType
    };

    private static Dictionary<string, int> EventMembers(Data.Models.ScenarioEventEntity row) => new()
    {
        ["scenarioEventType"] = (int)row.ScenarioEventType
    };
}
