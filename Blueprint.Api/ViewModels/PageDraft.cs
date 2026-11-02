// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// One MSEL page to write with AI, such as the read-in or the facilitator guide. Its name and content come from the editor,
    /// so unsaved edits count. Nothing is saved.
    /// </summary>
    public class PageDraftRequest
    {
        /// <summary>The page being edited; empty for one that is not saved yet</summary>
        public Guid? PageId { get; set; }
        public string Name { get; set; }
        /// <summary>The page as the editor holds it, in HTML; empty to write it from nothing</summary>
        public string Content { get; set; }
        /// <summary>What the planner wants, such as "add the two new teams" or "cut it to one screen"</summary>
        public string Instruction { get; set; }
    }

    public class PageDraftResult
    {
        /// <summary>The proposed page, in HTML; the planner decides whether to use it</summary>
        public string Html { get; set; }
        /// <summary>Assumptions and conflicts the planner should check</summary>
        public List<string> Notes { get; set; } = new List<string>();
    }
}
