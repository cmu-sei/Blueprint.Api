// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;

namespace Blueprint.Api.ViewModels
{
    /// <summary>What the model is asked to do to one field of an inject</summary>
    public enum DataValueDraftAction
    {
        /// <summary>Write the field from the inject's other values and the scenario</summary>
        Draft,
        /// <summary>Rewrite the field's current value in another style</summary>
        Rewrite,
        /// <summary>Fix anything in the field that conflicts with the scenario or earlier injects</summary>
        Consistent,
        Shorten,
        Expand
    }

    /// <summary>
    /// One field of an inject to write with AI. The inject's values come from the dialog, so unsaved
    /// edits count. Nothing is saved.
    /// </summary>
    public class DataValueDraftRequest
    {
        /// <summary>The inject being edited; empty for one that is not saved yet</summary>
        public Guid? ScenarioEventId { get; set; }
        /// <summary>The field to write</summary>
        public Guid DataFieldId { get; set; }
        /// <summary>The inject's delivery time, which decides which injects come before it</summary>
        public int DeltaSeconds { get; set; }
        /// <summary>The inject's current values, keyed by data field id</summary>
        public Dictionary<Guid, string> Values { get; set; } = new Dictionary<Guid, string>();
        public DataValueDraftAction Action { get; set; }
        /// <summary>For Rewrite: what to write it as, e.g. News or Email</summary>
        public string Style { get; set; }
        /// <summary>Anything else the planner wants</summary>
        public string Instruction { get; set; }
    }

    public class DataValueDraftResult
    {
        /// <summary>The proposed value; the planner decides whether to use it</summary>
        public string Value { get; set; }
        /// <summary>Assumptions and conflicts the planner should check</summary>
        public List<string> Notes { get; set; } = new List<string>();
    }
}
