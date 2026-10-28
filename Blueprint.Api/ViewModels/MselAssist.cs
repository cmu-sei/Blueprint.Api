// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System.Collections.Generic;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// A change to an existing MSEL, asked for in the planner's words. Nothing is saved; the planner reviews the
    /// proposed changes and applies the ones they keep.
    /// </summary>
    public class MselAssistRequest
    {
        public string Prompt { get; set; }

        /// <summary>The conversation so far, oldest first, so a follow-up can refer to earlier answers</summary>
        public List<MselAssistMessage> History { get; set; } = new List<MselAssistMessage>();
    }

    public class MselAssistMessage
    {
        /// <summary>planner or assistant</summary>
        public string Role { get; set; }
        public string Text { get; set; }
    }

    public class MselAssistResult
    {
        /// <summary>What the assistant says back to the planner</summary>
        public string Reply { get; set; }

        /// <summary>A question the assistant needs answered before it can propose changes; empty when it proposed them</summary>
        public string Question { get; set; }

        public List<MselChange> Changes { get; set; } = new List<MselChange>();

        /// <summary>What was dropped from the model's answer, and why</summary>
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>
    /// One proposed change. Op says what it does; Target names the existing team, organization or card it changes,
    /// or for a competency the team it belongs to (empty for the MSEL itself).
    /// </summary>
    public class MselChange
    {
        /// <summary>team.add, team.update, organization.add, organization.update, card.add, card.update, competency.add or competency.remove</summary>
        public string Op { get; set; }
        public string Target { get; set; }
        public string ShortName { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Summary { get; set; }
        public int? Move { get; set; }
        public int? Inject { get; set; }
        public string IdNumber { get; set; }

        /// <summary>The change as the planner reads it, written by the server</summary>
        public string Label { get; set; }
    }

    public class MselAssistApply
    {
        public List<MselChange> Changes { get; set; } = new List<MselChange>();
    }

    public class MselAssistApplyResult
    {
        public List<MselChangeResult> Results { get; set; } = new List<MselChangeResult>();
    }

    public class MselChangeResult
    {
        public string Label { get; set; }
        public bool Applied { get; set; }
        public string Error { get; set; }
    }
}
