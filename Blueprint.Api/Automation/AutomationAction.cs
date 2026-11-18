// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;

namespace Blueprint.Api.Automation
{
    /// <summary>What one action is run with: the task's parameters, bound to one VM, and where the MSEL's integrations are.</summary>
    public sealed class AutomationActionContext
    {
        public Guid MselId { get; init; }
        public AutomationAction Action { get; init; }
        /// <summary>The parameters, secrets decrypted, the VM's id and name filled in and the substitutions made.</summary>
        public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
        /// <summary>The Player view the run uses: the MSEL's own, or the one a test was run against.</summary>
        public Guid? PlayerViewId { get; init; }
        public Guid? CiteEvaluationId { get; init; }
        public Guid? GalleryExhibitId { get; init; }
        /// <summary>The VM this result is for; null for an action that runs without VMs.</summary>
        public Guid? VmId { get; init; }
        public string VmName { get; init; }
        public bool IsTest { get; init; }
        /// <summary>Report the request the action would make, rather than make it (a test of a dry run action).</summary>
        public bool DryRun { get; init; }

        public string Get(string key) =>
            Parameters.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

        public bool GetBool(string key) => bool.TryParse(Get(key), out var value) && value;

        public int? GetInt(string key) => int.TryParse(Get(key), out var value) ? value : null;
    }

    /// <summary>
    /// What an action did. <see cref="Succeeded"/> is the action's own verdict; when it is null, the run succeeds when
    /// <see cref="Output"/> contains the task's expected output, ignoring case, as Steamfitter decided.
    /// </summary>
    public sealed record ActionOutcome(string Output, bool? Succeeded = null);

    /// <summary>An action that ran and failed, with what it reported. The result is Failed with the message as its output.</summary>
    public class AutomationActionFailedException : Exception
    {
        public AutomationActionFailedException(string message) : base(message) { }
    }

    /// <summary>A task that cannot run as it is configured: no Player view, an address that is not allowed, ...</summary>
    public class AutomationConfigurationException : Exception
    {
        public AutomationConfigurationException(string message) : base(message) { }
    }

    /// <summary>Performs some of the automation actions.</summary>
    public interface IAutomationActionHandler
    {
        IReadOnlyCollection<AutomationAction> Actions { get; }
        Task<ActionOutcome> ExecuteAsync(AutomationActionContext context, CancellationToken ct);
    }

    /// <summary>Finds the handler of an action.</summary>
    public interface IAutomationActionRunner
    {
        Task<ActionOutcome> ExecuteAsync(AutomationActionContext context, CancellationToken ct);
    }

    public class AutomationActionRunner : IAutomationActionRunner
    {
        private readonly Dictionary<AutomationAction, IAutomationActionHandler> _handlers;

        public AutomationActionRunner(IEnumerable<IAutomationActionHandler> handlers)
        {
            _handlers = new();
            foreach (var handler in handlers)
                foreach (var action in handler.Actions)
                    _handlers[action] = handler;
        }

        public Task<ActionOutcome> ExecuteAsync(AutomationActionContext context, CancellationToken ct)
        {
            if (!_handlers.TryGetValue(context.Action, out var handler))
                throw new AutomationConfigurationException($"Blueprint has no handler for the {context.Action} action.");
            return handler.ExecuteAsync(context, ct);
        }
    }
}
