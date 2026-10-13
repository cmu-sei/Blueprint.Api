// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using Blueprint.Api.Data.Enumerations;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// The fields a Scenario Event's create and edit requests share. The owning MSEL is not one of them:
    /// only <see cref="ScenarioEventCreate"/> names it, so an edit cannot move a Scenario Event to another
    /// MSEL. The cells and the Steamfitter task are written by the service, field by field, for the
    /// event's own MSEL and the event itself.
    /// </summary>
    public abstract class ScenarioEventFields
    {
        public int GroupOrder { get; set; }
        public bool IsHidden { get; set; }
        public string RowMetadata { get; set; }
        public int DeltaSeconds { get; set; }     // time from the start of the MSEL when this event should be executed
        public EventType ScenarioEventType { get; set; }
        public string IntegrationTarget { get; set; }
        public string Information { get; set; }
        /// <summary>The event's cells. Only cells for the MSEL's own Data Fields are written.</summary>
        public ICollection<ScenarioEventCell> DataValues { get; set; } = new List<ScenarioEventCell>();
        /// <summary>The event's Steamfitter task. Its identity and event are the server's.</summary>
        public SteamfitterTaskFields SteamfitterTask { get; set; }
    }

    public class ScenarioEventCreate : ScenarioEventFields
    {
        public Guid Id { get; set; }
        public Guid MselId { get; set; }
        public Guid? InjectId { get; set; }
    }

    public class ScenarioEventEdit : ScenarioEventFields
    {
    }

    /// <summary>
    /// One cell of a Scenario Event in a create or edit request: the Data Field it is for and its value.
    /// </summary>
    public class ScenarioEventCell : DataValueFields
    {
        public Guid DataFieldId { get; set; }
        /// <summary>On an edit, a cell is written only when this names the event being edited.</summary>
        public Guid? ScenarioEventId { get; set; }
    }

    /// <summary>
    /// The editable fields of a Scenario Event's Steamfitter task.
    /// </summary>
    public class SteamfitterTaskFields
    {
        public SteamfitterIntegrationType TaskType { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public SteamfitterTaskAction Action { get; set; }
        public string VmMask { get; set; }
        public string ApiUrl { get; set; }
        public Dictionary<string, string> ActionParameters { get; set; }
        public string ExpectedOutput { get; set; }
        public int ExpirationSeconds { get; set; }
        public int DelaySeconds { get; set; }
        public int IntervalSeconds { get; set; }
        public int Iterations { get; set; }
        public SteamfitterTaskTrigger TriggerCondition { get; set; }
        public bool UserExecutable { get; set; }
        public bool Repeatable { get; set; }
    }
}
