// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using Blueprint.Api.Data.Enumerations;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// An automation task of a scenario event. Its secret parameters are never returned:
    /// <see cref="SecretParameterKeys"/> names those it stores.
    /// </summary>
    public class AutomationTask : Base
    {
        public Guid Id { get; set; }
        public Guid MselId { get; set; }
        public Guid ScenarioEventId { get; set; }
        public int DisplayOrder { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public AutomationAction Action { get; set; }
        public Dictionary<string, string> ActionParameters { get; set; } = new();
        public List<string> SecretParameterKeys { get; set; } = new();
        public string VmMask { get; set; }
        public string ExpectedOutput { get; set; }
        public int ExpirationSeconds { get; set; }
        public int DelaySeconds { get; set; }
        public int IntervalSeconds { get; set; }
        public int Iterations { get; set; }
        public AutomationIterationTermination IterationTermination { get; set; }
        public AutomationTrigger TriggerCondition { get; set; }
        public Guid? TriggerTaskId { get; set; }
        public bool Enabled { get; set; }
        public bool Repeatable { get; set; }
    }

    /// <summary>One run of an automation task, or a group start's record.</summary>
    public class AutomationRun : Base
    {
        public Guid Id { get; set; }
        public Guid MselId { get; set; }
        public Guid? AutomationTaskId { get; set; }
        public Guid? ScenarioEventId { get; set; }
        public string TaskName { get; set; }
        public AutomationAction Action { get; set; }
        public AutomationRunKind Kind { get; set; }
        public AutomationRunSource Source { get; set; }
        public Guid? ParentRunId { get; set; }
        public Guid? RerunOfRunId { get; set; }
        public Guid? DeploymentId { get; set; }
        public Guid? PlayerViewId { get; set; }
        public Guid? RequestedBy { get; set; }
        public bool TriggerChain { get; set; }
        public AutomationStatus Status { get; set; }
        public string StatusDetail { get; set; }
        public int CurrentIteration { get; set; }
        public DateTime DueAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public bool CancelRequested { get; set; }
        /// <summary>The run's results, newest iteration first; empty where only the run is asked for.</summary>
        public List<AutomationResult> Results { get; set; } = new();
    }

    /// <summary>One iteration of a run on one VM, or of the run itself for an action without VMs.</summary>
    public class AutomationResult : Base
    {
        public Guid Id { get; set; }
        public Guid RunId { get; set; }
        public int Iteration { get; set; }
        public Guid? VmId { get; set; }
        public string VmName { get; set; }
        public AutomationStatus Status { get; set; }
        /// <summary>The parameters the action ran with, as JSON, every secret redacted.</summary>
        public string InputString { get; set; }
        public string ExpectedOutput { get; set; }
        public string ActualOutput { get; set; }
        public bool OutputTruncated { get; set; }
        public int ExpirationSeconds { get; set; }
        public DateTime? SentAt { get; set; }
        public DateTime? StatusAt { get; set; }
    }
}
