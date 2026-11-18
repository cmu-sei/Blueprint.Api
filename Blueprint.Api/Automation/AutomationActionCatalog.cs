// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Blueprint.Api.Data.Enumerations;

namespace Blueprint.Api.Automation
{
    /// <summary>One action an automation task can perform, and the parameters it takes.</summary>
    public class AutomationActionDefinition
    {
        public AutomationAction Action { get; set; }
        /// <summary>Blueprint, VM, SSH, Email or HTTP.</summary>
        public string Category { get; set; }
        public string Display { get; set; }
        public string Description { get; set; }
        /// <summary>Whether the action runs on the VMs its task's mask matches in a Player view.</summary>
        public bool RequiresVm { get; set; }
        /// <summary>What the MSEL must have for the action to run: playerView, cite or gallery.</summary>
        public List<string> Requires { get; set; } = new();
        /// <summary>live, or dryRun: a test reports the request the action would make instead of making it.</summary>
        public string TestMode { get; set; } = "live";
        public List<AutomationParameterDefinition> Parameters { get; set; } = new();

        public bool IsDryRunWhenTested => string.Equals(TestMode, "dryRun", StringComparison.OrdinalIgnoreCase);
    }

    public class AutomationParameterDefinition
    {
        public string Key { get; set; }
        public string Display { get; set; }
        public string Hint { get; set; }
        /// <summary>text, textArea, password, checkbox, select, number, datetime, html or file.</summary>
        public string InputType { get; set; } = "text";
        public string Default { get; set; }
        public bool Required { get; set; }
        /// <summary>Stored encrypted, and never returned by the API.</summary>
        public bool Secret { get; set; }
        public List<AutomationParameterChoice> Choices { get; set; }
    }

    public class AutomationParameterChoice
    {
        public string Key { get; set; }
        public string Display { get; set; }
    }

    public interface IAutomationActionCatalog
    {
        IReadOnlyList<AutomationActionDefinition> Actions { get; }
        AutomationActionDefinition Get(AutomationAction action);
        /// <summary>The keys of the action's secret parameters.</summary>
        IReadOnlySet<string> SecretKeys(AutomationAction action);
        /// <summary>
        /// The parameters of a task as it is saved: only the action's own keys, under the catalog's spelling, without
        /// its secrets (which are stored apart, encrypted) and without empty values.
        /// </summary>
        Dictionary<string, string> Normalize(AutomationAction action, IDictionary<string, string> parameters);
        /// <summary>What is wrong with a task's parameters, or nothing.</summary>
        IReadOnlyList<string> Validate(AutomationAction action, IDictionary<string, string> parameters, IEnumerable<string> secretKeysSet);
    }

    /// <summary>The action catalog, read once from the embedded automationActions.json.</summary>
    public class AutomationActionCatalog : IAutomationActionCatalog
    {
        private readonly Dictionary<AutomationAction, AutomationActionDefinition> _byAction;

        public AutomationActionCatalog()
        {
            using var stream = typeof(AutomationActionCatalog).Assembly
                .GetManifestResourceStream("Blueprint.Api.Automation.automationActions.json")
                ?? throw new InvalidOperationException("The automation action catalog is not embedded in the assembly.");
            var file = JsonSerializer.Deserialize<CatalogFile>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() },
            });
            Actions = file.Actions;
            _byAction = file.Actions.ToDictionary(a => a.Action);
            var missing = Enum.GetValues<AutomationAction>().Where(a => !_byAction.ContainsKey(a)).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException("The automation action catalog has no entry for " + string.Join(", ", missing));
        }

        public IReadOnlyList<AutomationActionDefinition> Actions { get; }

        public AutomationActionDefinition Get(AutomationAction action) =>
            _byAction.TryGetValue(action, out var definition) ? definition : null;

        public IReadOnlySet<string> SecretKeys(AutomationAction action) =>
            (Get(action)?.Parameters ?? new())
                .Where(p => p.Secret)
                .Select(p => p.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> Normalize(AutomationAction action, IDictionary<string, string> parameters)
        {
            var definition = Get(action);
            var normalized = new Dictionary<string, string>();
            if (definition == null || parameters == null)
                return normalized;
            foreach (var parameter in definition.Parameters.Where(p => !p.Secret))
            {
                var value = parameters.FirstOrDefault(kv => string.Equals(kv.Key, parameter.Key, StringComparison.OrdinalIgnoreCase)).Value;
                if (!string.IsNullOrEmpty(value))
                    normalized[parameter.Key] = value;
            }
            return normalized;
        }

        public IReadOnlyList<string> Validate(AutomationAction action, IDictionary<string, string> parameters, IEnumerable<string> secretKeysSet)
        {
            var definition = Get(action);
            if (definition == null)
                return new[] { $"{action} is not an automation action." };
            var errors = new List<string>();
            var secrets = new HashSet<string>(secretKeysSet ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var parameter in definition.Parameters)
            {
                var value = parameters?.FirstOrDefault(kv => string.Equals(kv.Key, parameter.Key, StringComparison.OrdinalIgnoreCase)).Value;
                var isSet = parameter.Secret ? secrets.Contains(parameter.Key) : !string.IsNullOrWhiteSpace(value);
                if (parameter.Required && !isSet)
                {
                    errors.Add($"{parameter.Display} is required.");
                    continue;
                }
                if (!isSet || parameter.Secret)
                    continue;
                switch (parameter.InputType)
                {
                    case "number" when !long.TryParse(value, out _):
                        errors.Add($"{parameter.Display} must be a whole number.");
                        break;
                    case "datetime" when !DateTimeOffset.TryParse(value, out _):
                        errors.Add($"{parameter.Display} must be a date and time.");
                        break;
                    case "checkbox" when !bool.TryParse(value, out _):
                        errors.Add($"{parameter.Display} must be true or false.");
                        break;
                    case "select" when parameter.Choices != null && !parameter.Choices.Any(c => c.Key == value):
                        errors.Add($"{parameter.Display} must be one of {string.Join(", ", parameter.Choices.Select(c => c.Key))}.");
                        break;
                }
            }
            return errors;
        }

        private class CatalogFile
        {
            public List<AutomationActionDefinition> Actions { get; set; } = new();
        }
    }
}
