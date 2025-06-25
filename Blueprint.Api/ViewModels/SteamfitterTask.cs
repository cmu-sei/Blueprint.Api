// Copyright 2025 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using Blueprint.Api.Data.Enumerations;

namespace Blueprint.Api.ViewModels
{
    public class SteamfitterTask : Base
    {
        public Guid Id { get; set; }
        public Guid ScenarioEventId { get; set; }
        public virtual ScenarioEvent ScenarioEvent { get; set; }
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
