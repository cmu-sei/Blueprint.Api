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
        public int Score { get; set; }
        public bool UserExecutable { get; set; }
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

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// One automation task of a scenario event, as an edit of the event saves it. The list replaces the event's tasks:
    /// a task the list leaves out is deleted, and the list's order is theirs. A task named by <see cref="Id"/> is that
    /// task; one with no id is new, and a client may give a new task an id so that another task of the same save can
    /// be triggered by it.
    /// </summary>
    public class AutomationTaskFields
    {
        public Guid? Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public AutomationAction Action { get; set; }
        /// <summary>The action's parameters other than its secrets; keys the action does not take are dropped.</summary>
        public Dictionary<string, string> ActionParameters { get; set; } = new();
        /// <summary>
        /// Secrets to set, by key: a value replaces the stored one, an empty value removes it, and a secret this leaves
        /// out keeps its stored value. The API never returns a secret.
        /// </summary>
        public Dictionary<string, string> SecretParameters { get; set; } = new();
        public string VmMask { get; set; }
        public string ExpectedOutput { get; set; }
        public int ExpirationSeconds { get; set; }
        public int DelaySeconds { get; set; }
        public int IntervalSeconds { get; set; }
        public int Iterations { get; set; } = 1;
        public AutomationIterationTermination IterationTermination { get; set; }
        public AutomationTrigger TriggerCondition { get; set; }
        public Guid? TriggerTaskId { get; set; }
        public bool Enabled { get; set; } = true;
        public bool Repeatable { get; set; }
        /// <summary>The points the exercise earns when the task's newest run succeeds; 0 is not scored.</summary>
        public int Score { get; set; }
        /// <summary>Whether a participant can run it on the MSEL's Tasks page.</summary>
        public bool UserExecutable { get; set; }
    }

    /// <summary>
    /// A test of a task while the MSEL is written: the task as the form has it, saved or not, run once against a Player
    /// view the caller can see. A test is not exercise history.
    /// </summary>
    public class AutomationTestForm
    {
        public Guid ScenarioEventId { get; set; }
        /// <summary>The saved task the form edits, whose stored secrets the test uses where the form sets none; null for a new task.</summary>
        public Guid? TaskId { get; set; }
        public AutomationTaskFields Task { get; set; }
        /// <summary>The Player view whose VMs the test uses, and that a Player notification goes to.</summary>
        public Guid? PlayerViewId { get; set; }
        /// <summary>Also run, as tests, the saved tasks this one triggers.</summary>
        public bool IncludeChain { get; set; }
    }

    public class AutomationVmPreviewForm
    {
        public Guid PlayerViewId { get; set; }
        public string VmMask { get; set; }
    }

    public class AutomationVmPreview
    {
        /// <summary>ids when the mask is a list of VM ids, name otherwise.</summary>
        public string MatchMode { get; set; }
        public List<AutomationVmSummary> Vms { get; set; } = new();
        /// <summary>Why the VMs could not be listed, if they could not.</summary>
        public string Error { get; set; }
    }

    public class AutomationVmSummary
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
    }

    /// <summary>How to start a task by hand.</summary>
    public class AutomationRunForm
    {
        /// <summary>Whether the tasks it triggers run after it.</summary>
        public bool TriggerChain { get; set; } = true;
    }

    /// <summary>Where a deployed MSEL is, as the Facilitator View shows it.</summary>
    public class MselExecution
    {
        public Guid MselId { get; set; }
        public Data.Enumerations.MselItemStatus Status { get; set; }
        public Guid? DeploymentId { get; set; }
        public DateTime StartTime { get; set; }
        public int? CurrentMoveNumber { get; set; }
        public int? CurrentGroupNumber { get; set; }
        public DateTime? CurrentGroupStartedAt { get; set; }
        public bool AutomationPaused { get; set; }
        /// <summary>Each scenario event's move and group: the numbers Gallery articles are pushed with.</summary>
        public List<ScenarioEventPosition> Positions { get; set; } = new();
    }

    public class ScenarioEventPosition
    {
        public Guid ScenarioEventId { get; set; }
        public int MoveNumber { get; set; }
        public int GroupNumber { get; set; }
    }

    /// <summary>Starting a move or a group: the manual tasks to run, one after another, in this order.</summary>
    public class MselAdvanceForm
    {
        public List<Guid> RunTaskIds { get; set; } = new();
    }

    /// <summary>What starting a move or a group did.</summary>
    public class MselAdvanceResult
    {
        public MselExecution Execution { get; set; }
        /// <summary>One line per integration moved: "CITE evaluation is at move 2", or why it is not.</summary>
        public List<string> Messages { get; set; } = new();
        public bool Succeeded { get; set; }
        /// <summary>The run of the first manual task, which starts the rest; null when none was asked for.</summary>
        public AutomationRun FirstRun { get; set; }
    }

    public class AutomationPausedForm
    {
        public bool Paused { get; set; }
    }

    /// <summary>
    /// A task a participant can run, as the MSEL's Tasks page shows it: what it is called, how its newest run in this
    /// deployment went, and its points. A participant is not shown its action, parameters or output.
    /// </summary>
    public class ParticipantTask
    {
        public Guid Id { get; set; }
        public Guid ScenarioEventId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        /// <summary>The newest run's status; none when the task has not run in this deployment.</summary>
        public AutomationStatus? Status { get; set; }
        public DateTime? StatusAt { get; set; }
        /// <summary>Whether the participant can run it now; when not, why.</summary>
        public bool CanRun { get; set; }
        public string CannotRunReason { get; set; }
        public int TotalScore { get; set; }
        public int TotalScoreEarned { get; set; }
    }

    /// <summary>A deployed MSEL's Tasks page: the tasks participants can run, and the exercise's score.</summary>
    public class ParticipantTasks
    {
        public Guid MselId { get; set; }
        public string MselName { get; set; }
        public bool IsDeployed { get; set; }
        public bool AutomationPaused { get; set; }
        /// <summary>The most every task of the MSEL can earn together, participants' or not.</summary>
        public int Score { get; set; }
        public int ScoreEarned { get; set; }
        public List<ParticipantTask> Tasks { get; set; } = new();
    }
}
