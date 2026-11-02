// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// One move's situation briefing to write with AI. The move's values come from the edit dialog, so unsaved edits count.
    /// Nothing is saved.
    /// </summary>
    public class MoveDraftRequest
    {
        /// <summary>The move being edited; empty for one that is not saved yet</summary>
        public Guid? MoveId { get; set; }
        public int MoveNumber { get; set; }
        /// <summary>The move's title</summary>
        public string Description { get; set; }
        /// <summary>The scenario date and time the move opens</summary>
        public DateTime? SituationTime { get; set; }
        /// <summary>The situation briefing as the dialog holds it, in HTML; empty to write it from nothing</summary>
        public string SituationDescription { get; set; }
        /// <summary>What the planner wants, such as "say the plant is on manual control" or "shorten it"</summary>
        public string Instruction { get; set; }
    }

    public class MoveDraftResult
    {
        /// <summary>The proposed situation briefing, in HTML; the planner decides whether to use it</summary>
        public string Html { get; set; }
        /// <summary>Assumptions and conflicts the planner should check</summary>
        public List<string> Notes { get; set; } = new List<string>();
    }
}
