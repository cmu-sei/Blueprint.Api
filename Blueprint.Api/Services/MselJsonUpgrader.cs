// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Blueprint.Api.Data.Enumerations;

namespace Blueprint.Api.Services
{
    /// <summary>
    /// Brings a MSEL JSON file from before Blueprint ran automation tasks up to date, before it is read: each scenario
    /// event's Steamfitter task becomes an automation task, as the ReplaceSteamfitterWithAutomation migration converts
    /// the stored ones, and the MSEL's Steamfitter settings are dropped.
    /// </summary>
    /// <remarks>
    /// The push ran a group's Steamfitter tasks one after another once a facilitator started the group, whatever
    /// trigger each task said. So within each group (the events sharing a time), each converted task is triggered on
    /// completion of the one before it, and the first is run by hand; starting the group on the Facilitator View
    /// offers to run it. A task was pushed only when its event targeted Steamfitter, so only those are enabled.
    /// </remarks>
    public static class MselJsonUpgrader
    {
        public static (string Json, List<string> Notes) Upgrade(string json)
        {
            var notes = new List<string>();
            JsonNode root;
            try
            {
                // files are written PascalCase, but other casing is tolerated
                root = JsonNode.Parse(json, new JsonNodeOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException)
            {
                // not JSON: the import says so, as before
                return (json, notes);
            }
            if (root is not JsonObject msel)
                return (json, notes);

            var changed = false;
            foreach (var key in new[] { "UseSteamfitter", "SteamfitterScenarioId", "SteamfitterIntegrationType", "SteamfitterScenarioStartMode" })
                changed |= msel.Remove(key);
            foreach (var role in Items(msel["UserMselRoles"]).OfType<JsonObject>())
                changed |= role.Remove("SteamfitterScenarioRole");

            var events = Items(msel["ScenarioEvents"]).OfType<JsonObject>().ToList();
            var converted = new List<(JsonObject Event, JsonObject Task, int Delta, int Order, string Id)>();
            var droppedHeaders = 0;
            foreach (var scenarioEvent in events)
            {
                var target = scenarioEvent["IntegrationTarget"]?.GetValue<string>();
                var targetsSteamfitter = target != null && target.Contains("Steamfitter", StringComparison.OrdinalIgnoreCase);
                if (target != null)
                {
                    var rest = target.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(t => !t.Equals("Steamfitter", StringComparison.OrdinalIgnoreCase));
                    var newTarget = string.Join(", ", rest);
                    if (newTarget != target)
                    {
                        scenarioEvent["IntegrationTarget"] = string.IsNullOrEmpty(newTarget) ? null : JsonValue.Create(newTarget);
                        changed = true;
                    }
                }
                var legacy = scenarioEvent["SteamfitterTask"] as JsonObject;
                changed |= scenarioEvent.Remove("SteamfitterTask");
                changed |= scenarioEvent.Remove("SteamfitterTaskId");
                if (legacy == null)
                    continue;
                var task = Convert(legacy, targetsSteamfitter, out var hadHeaders);
                if (task == null)
                    continue;
                droppedHeaders += hadHeaders ? 1 : 0;
                converted.Add((scenarioEvent, task, Int(scenarioEvent["DeltaSeconds"]), Int(scenarioEvent["GroupOrder"]),
                    scenarioEvent["Id"]?.ToString() ?? string.Empty));
                changed = true;
            }

            // a group's tasks ran one after another
            foreach (var group in converted.Where(c => c.Task["Enabled"]!.GetValue<bool>()).GroupBy(c => c.Delta))
            {
                string previous = null;
                foreach (var item in group.OrderBy(c => c.Order).ThenBy(c => c.Id, StringComparer.Ordinal))
                {
                    if (previous != null)
                    {
                        item.Task["TriggerCondition"] = (int)AutomationTrigger.OnCompletion;
                        item.Task["TriggerTaskId"] = previous;
                    }
                    previous = item.Task["Id"]!.GetValue<string>();
                }
            }
            foreach (var item in converted)
            {
                var list = item.Event["AutomationTasks"] as JsonArray ?? new JsonArray();
                list.Add(item.Task);
                item.Event["AutomationTasks"] = list;
            }

            if (converted.Count > 0)
            {
                notes.Add($"{converted.Count} Steamfitter task(s) became automation tasks. Blueprint now runs them itself: start each move or group on the Facilitator View, which also moves CITE and Gallery, as the Move Change tasks did.");
                if (droppedHeaders > 0)
                    notes.Add($"{droppedHeaders} task(s) had http headers, which were dropped: enter them again, since Blueprint stores them encrypted.");
            }
            return changed ? (msel.ToJsonString(), notes) : (json, notes);
        }

        /// <summary>A Steamfitter task as an automation task, by the migration's mapping; null for one of no known kind.</summary>
        private static JsonObject Convert(JsonObject legacy, bool enabled, out bool hadHeaders)
        {
            hadHeaders = false;
            var parameters = legacy["ActionParameters"] as JsonObject ?? new JsonObject();
            string P(string key) => parameters[key]?.ToString();
            JsonObject Slice(params (string To, string From)[] keys)
            {
                var sliced = new JsonObject();
                foreach (var (to, from) in keys)
                    if (!string.IsNullOrEmpty(P(from)))
                        sliced[to] = P(from);
                return sliced;
            }

            var type = Int(legacy["TaskType"]);
            AutomationAction action;
            JsonObject actionParameters;
            var expected = legacy["ExpectedOutput"]?.ToString();
            switch (type)
            {
                case 1:
                    action = AutomationAction.player_notification;
                    actionParameters = Slice(("Text", "notificationText"));
                    expected = null;
                    break;
                case 2:
                    action = AutomationAction.send_email;
                    actionParameters = Slice(("EmailFrom", "EmailFrom"), ("EmailTo", "EmailTo"), ("EmailCC", "EmailCC"),
                        ("Subject", "Subject"), ("Message", "Message"), ("Mime", "Mime"));
                    expected = null;
                    break;
                case 3:
                    action = AutomationAction.cite_situation_update;
                    actionParameters = Slice(("SituationTime", "situationTime"), ("SituationDescription", "situationDescription"));
                    expected = null;
                    break;
                case 11 or 12 or 13 or 14:
                    action = (AutomationAction)type;
                    actionParameters = Slice(("Url", "Url"), ("Body", "Body"));
                    break;
                default:
                    return null;
            }
            var task = new JsonObject
            {
                ["Id"] = Guid.NewGuid().ToString(),
                ["Name"] = legacy["Name"]?.ToString() ?? "Steamfitter task",
                ["Description"] = legacy["Description"]?.ToString(),
                ["Action"] = (int)action,
                ["ActionParameters"] = actionParameters,
                ["ExpectedOutput"] = string.IsNullOrEmpty(expected) ? null : expected,
                ["ExpirationSeconds"] = Math.Max(0, Int(legacy["ExpirationSeconds"])),
                ["DelaySeconds"] = Math.Max(0, Int(legacy["DelaySeconds"])),
                ["IntervalSeconds"] = Math.Max(0, Int(legacy["IntervalSeconds"])),
                ["Iterations"] = Math.Max(1, Int(legacy["Iterations"])),
                ["IterationTermination"] = (int)AutomationIterationTermination.IterationCount,
                ["TriggerCondition"] = (int)AutomationTrigger.Manual,
                ["TriggerTaskId"] = null,
                ["Enabled"] = enabled,
                ["Repeatable"] = legacy["Repeatable"]?.GetValue<bool>() ?? false,
                ["Score"] = 0,
                // a task a participant could run in Steamfitter still can
                ["UserExecutable"] = legacy["UserExecutable"]?.GetValue<bool>() ?? false,
                ["DisplayOrder"] = 0,
            };
            // headers are a secret now, and a file carries none
            hadHeaders = !string.IsNullOrEmpty(P("Headers")) && type is 11 or 12 or 13 or 14;
            return task;
        }

        /// <summary>The items of a collection, written as an array or, by a reference-preserving writer, as {"$values": [...]}.</summary>
        private static IEnumerable<JsonNode> Items(JsonNode node) => node switch
        {
            JsonArray array => array,
            JsonObject o when o["$values"] is JsonArray values => values,
            _ => Enumerable.Empty<JsonNode>(),
        };

        private static int Int(JsonNode node)
        {
            if (node is not JsonValue value)
                return 0;
            if (value.TryGetValue<int>(out var i))
                return i;
            if (value.TryGetValue<long>(out var l))
                return (int)l;
            return int.TryParse(value.ToString(), out var parsed) ? parsed : 0;
        }
    }
}
