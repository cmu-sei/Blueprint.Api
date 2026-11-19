// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Blueprint.Api.Tests.Migrations;

/// <summary>
/// The two places a Steamfitter task becomes an automation task: the <c>ReplaceSteamfitterWithAutomation</c>
/// migration, for the tasks a database holds, and <see cref="MselJsonUpgrader"/>, for the tasks in an older MSEL
/// file. Both are given the same tasks and held to the same expectations, so they cannot drift apart.
/// </summary>
/// <remarks>
/// The migration test takes its own database back down to <c>AddAutomation</c>, the schema before the conversion,
/// seeds Steamfitter tasks there, and migrates up again.
/// </remarks>
public class SteamfitterTaskConversionTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    private const string BeforeTheConversion = "20261118231321_AddAutomation";

    /// <summary>
    /// One Steamfitter task each, on events of one MSEL. Three share a time, so they were one group, which the push
    /// ran in group order; <c>unknown</c> is a task type neither conversion knows.
    /// </summary>
    private static readonly LegacyCase[] Cases =
    [
        new("notify", 0, 1, "Gallery, Steamfitter", 1,
            new() { ["notificationText"] = "Move 2 begins", ["Url"] = "filled in by the push" }, "Message was sent"),
        new("post", 0, 2, "Steamfitter", 12,
            new() { ["Url"] = "http://localhost:4720/api", ["Body"] = "{}", ["Headers"] = "{\"X-Team\":\"blue\"}" }, "ok"),
        new("unknown", 0, 3, "Steamfitter", 99, new() { ["Url"] = "http://localhost/" }, null),
        new("email", 0, 4, "Steamfitter", 2,
            new() { ["EmailTo"] = "blue@example.com", ["Subject"] = "Status", ["Message"] = "Report in", ["Url"] = "filled in" }, null),
        new("situation", 60, 1, "Gallery", 3,
            new() { ["situationTime"] = "2026-05-25T18:00:00Z", ["situationDescription"] = "calm" }, null),
    ];

    [Fact]
    public async Task TheMigration_ConvertsEachStoredSteamfitterTask()
    {
        var migrator = Db.GetService<IMigrator>();
        await migrator.MigrateAsync(BeforeTheConversion, Ct);

        var msel = TestData.Msel();
        var events = Cases.ToDictionary(c => c.Key, c =>
        {
            var scenarioEvent = TestData.ScenarioEvent(msel.Id, deltaSeconds: c.Delta, groupOrder: c.GroupOrder);
            scenarioEvent.IntegrationTarget = c.Target;
            return scenarioEvent;
        });
        await Seed([msel, .. events.Values]);
        var taskIds = new Dictionary<string, Guid>();
        await using (var context = NewContext())
        {
            foreach (var c in Cases)
            {
                taskIds[c.Key] = Guid.NewGuid();
                var hstore = string.Join(", ", c.Parameters.Select(p => $"\"{Escape(p.Key)}\"=>\"{Escape(p.Value)}\""));
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO steamfitter_tasks (id, scenario_event_id, task_type, name, action, action_parameters,
                        api_url, expected_output, expiration_seconds, delay_seconds, interval_seconds, iterations,
                        trigger_condition, user_executable, repeatable, created_by, date_created)
                    VALUES ({taskIds[c.Key]}, {events[c.Key].Id}, {c.TaskType}, {c.Key}, 12, {hstore}::hstore,
                        'http', {c.Expected}, 120, 5, 0, 0, 6, {c.UserExecutable}, false, {Guid.NewGuid()}, now())
                    """, Ct);
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE scenario_events SET steamfitter_task_id = {taskIds[c.Key]} WHERE id = {events[c.Key].Id}", Ct);
            }
        }

        await migrator.MigrateAsync(cancellationToken: Ct);

        var stored = await ReadBack(db => db.AutomationTasks.AsNoTracking().ToListAsync(Ct));
        var keyOfEvent = events.ToDictionary(e => e.Value.Id, e => e.Key);
        var converted = stored.ToDictionary(t => keyOfEvent[t.ScenarioEventId], t => new Converted(
            t.Action, t.ActionParameters, t.ExpectedOutput, t.Enabled, t.TriggerCondition,
            t.TriggerTaskId == null ? null : keyOfEvent[stored.Single(x => x.Id == t.TriggerTaskId).ScenarioEventId],
            t.DelaySeconds, t.Iterations, t.Name, t.UserExecutable, t.Score));
        AssertConverted(converted);

        // the ids are kept, and an http task's headers wait, in the clear, for AutomationSecretUpgrader
        Assert.All(stored, t => Assert.Equal(taskIds[keyOfEvent[t.ScenarioEventId]], t.Id));
        var post = stored.Single(t => t.Id == taskIds["post"]);
        Assert.Equal("{\"X-Team\":\"blue\"}", JsonSerializer.Deserialize<Dictionary<string, string>>(post.SecretParameters)["Headers"]);
        Assert.All(stored.Where(t => t.Id != post.Id), t => Assert.Null(t.SecretParameters));

        var targets = await ReadBack(db => db.ScenarioEvents.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.IntegrationTarget, Ct));
        Assert.Equal("Gallery", targets[events["notify"].Id]);
        Assert.Null(targets[events["post"].Id]);
        Assert.Equal("Gallery", targets[events["situation"].Id]);
    }

    [Fact]
    public void TheJsonUpgrader_ConvertsEachSteamfitterTaskInAFile()
    {
        var events = Cases.ToDictionary(c => c.Key, c => new JsonObject
        {
            ["Id"] = Guid.NewGuid().ToString(),
            ["DeltaSeconds"] = c.Delta,
            ["GroupOrder"] = c.GroupOrder,
            ["IntegrationTarget"] = c.Target,
            ["SteamfitterTask"] = new JsonObject
            {
                ["Id"] = Guid.NewGuid().ToString(),
                ["TaskType"] = c.TaskType,
                ["Name"] = c.Key,
                ["Action"] = 12,
                ["ActionParameters"] = JsonNode.Parse(JsonSerializer.Serialize(c.Parameters)),
                ["ExpectedOutput"] = c.Expected,
                ["ExpirationSeconds"] = 120,
                ["DelaySeconds"] = 5,
                ["IntervalSeconds"] = 0,
                ["Iterations"] = 0,
                ["TriggerCondition"] = 6,
                ["UserExecutable"] = c.UserExecutable,
                ["Repeatable"] = false,
            },
        });
        var file = new JsonObject { ["Id"] = Guid.NewGuid().ToString(), ["ScenarioEvents"] = new JsonArray([.. events.Values]) };

        var (json, _) = MselJsonUpgrader.Upgrade(file.ToJsonString());

        var upgraded = JsonNode.Parse(json)!["ScenarioEvents"]!.AsArray().Select(e => e!.AsObject()).ToList();
        string KeyOf(JsonObject e) => events.Single(x => x.Value["Id"]!.GetValue<string>() == e["Id"]!.GetValue<string>()).Key;
        var tasks = upgraded
            .Where(e => e["AutomationTasks"] is JsonArray)
            .ToDictionary(KeyOf, e => Assert.Single(e["AutomationTasks"]!.AsArray())!.AsObject());
        var keyOfTask = tasks.ToDictionary(t => t.Value["Id"]!.GetValue<string>(), t => t.Key);
        var converted = tasks.ToDictionary(t => t.Key, t => new Converted(
            (AutomationAction)t.Value["Action"]!.GetValue<int>(),
            JsonSerializer.Deserialize<Dictionary<string, string>>(t.Value["ActionParameters"]!.ToJsonString()),
            t.Value["ExpectedOutput"]?.GetValue<string>(),
            t.Value["Enabled"]!.GetValue<bool>(),
            (AutomationTrigger)t.Value["TriggerCondition"]!.GetValue<int>(),
            t.Value["TriggerTaskId"] is JsonValue trigger ? keyOfTask[trigger.GetValue<string>()] : null,
            t.Value["DelaySeconds"]!.GetValue<int>(),
            t.Value["Iterations"]!.GetValue<int>(),
            t.Value["Name"]!.GetValue<string>(),
            t.Value["UserExecutable"]!.GetValue<bool>(),
            t.Value["Score"]!.GetValue<int>()));
        AssertConverted(converted);

        // a file carries no secrets, so the headers are gone, and the upgrader's notes say so
        Assert.All(upgraded, e => Assert.Null(e["SteamfitterTask"]));
        var target = upgraded.ToDictionary(KeyOf, e => e["IntegrationTarget"]?.GetValue<string>());
        Assert.Equal("Gallery", target["notify"]);
        Assert.Null(target["post"]);
        Assert.Equal("Gallery", target["situation"]);
    }

    /// <summary>What both conversions must make of <see cref="Cases"/>.</summary>
    private static void AssertConverted(Dictionary<string, Converted> converted)
    {
        Assert.Equal(["email", "notify", "post", "situation"], converted.Keys.Order());

        // only the parameters the author wrote, renamed to the action's own
        Assert.Equal(AutomationAction.player_notification, converted["notify"].Action);
        Assert.Equal(new Dictionary<string, string> { ["Text"] = "Move 2 begins" }, converted["notify"].Parameters);
        Assert.Null(converted["notify"].Expected);
        Assert.Equal(AutomationAction.http_post, converted["post"].Action);
        Assert.Equal(new Dictionary<string, string> { ["Url"] = "http://localhost:4720/api", ["Body"] = "{}" }, converted["post"].Parameters);
        Assert.Equal("ok", converted["post"].Expected);
        Assert.Equal(AutomationAction.send_email, converted["email"].Action);
        Assert.Equal(new Dictionary<string, string> { ["EmailTo"] = "blue@example.com", ["Subject"] = "Status", ["Message"] = "Report in" },
            converted["email"].Parameters);
        Assert.Equal(AutomationAction.cite_situation_update, converted["situation"].Action);
        Assert.Equal(new Dictionary<string, string> { ["SituationTime"] = "2026-05-25T18:00:00Z", ["SituationDescription"] = "calm" },
            converted["situation"].Parameters);

        // only the tasks of events that targeted Steamfitter were pushed, so only those run
        Assert.True(converted["notify"].Enabled);
        Assert.True(converted["post"].Enabled);
        Assert.True(converted["email"].Enabled);
        Assert.False(converted["situation"].Enabled);

        // the group ran in group order once it was started: the first by hand, each after the one before it
        Assert.Equal((AutomationTrigger.Manual, null), (converted["notify"].Trigger, converted["notify"].TriggerKey));
        Assert.Equal((AutomationTrigger.OnCompletion, "notify"), (converted["post"].Trigger, converted["post"].TriggerKey));
        Assert.Equal((AutomationTrigger.OnCompletion, "post"), (converted["email"].Trigger, converted["email"].TriggerKey));
        Assert.Equal((AutomationTrigger.Manual, null), (converted["situation"].Trigger, converted["situation"].TriggerKey));

        Assert.All(converted, c =>
        {
            Assert.Equal(c.Key, c.Value.Name);
            Assert.Equal(5, c.Value.Delay);
            // Steamfitter read zero iterations as one
            Assert.Equal(1, c.Value.Iterations);
            // Blueprint never pushed a score; a task a participant could run still can
            Assert.Equal(0, c.Value.Score);
            Assert.Equal(c.Key == "notify", c.Value.UserExecutable);
        });
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private sealed record LegacyCase(
        string Key, int Delta, int GroupOrder, string Target, int TaskType, Dictionary<string, string> Parameters, string Expected)
    {
        // only the notification was one a participant could run
        public bool UserExecutable => Key == "notify";
    }

    private sealed record Converted(
        AutomationAction Action, Dictionary<string, string> Parameters, string Expected, bool Enabled,
        AutomationTrigger Trigger, string TriggerKey, int Delay, int Iterations, string Name, bool UserExecutable, int Score);
}
