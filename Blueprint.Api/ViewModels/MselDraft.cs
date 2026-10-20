// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;

namespace Blueprint.Api.ViewModels
{
    /// <summary>What the planner asks for. Nothing is saved when a draft is generated.</summary>
    public class MselDraftRequest
    {
        /// <summary>The template whose columns, teams and integrations the draft uses</summary>
        public Guid TemplateMselId { get; set; }
        /// <summary>The planner's description of the exercise</summary>
        public string Prompt { get; set; }
    }

    /// <summary>A drafted MSEL in a flat form the model can fill and the planner can edit before creating it</summary>
    public class MselDraft
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public int DurationMinutes { get; set; }
        public List<MselDraftMove> Moves { get; set; } = new List<MselDraftMove>();
        public List<MselDraftTeam> Teams { get; set; } = new List<MselDraftTeam>();
        public List<MselDraftOrganization> Organizations { get; set; } = new List<MselDraftOrganization>();
        public List<MselDraftInject> Injects { get; set; } = new List<MselDraftInject>();
        /// <summary>Competency ID numbers (DCWF or NICE) the exercise exercises; unknown ones are dropped at import</summary>
        public List<string> Competencies { get; set; } = new List<string>();
        /// <summary>Assumptions and open questions the model wants the planner to check</summary>
        public List<string> PlannerNotes { get; set; } = new List<string>();
    }

    public class MselDraftMove
    {
        public int MoveNumber { get; set; }
        public string Description { get; set; }
        public string SituationDescription { get; set; }
        /// <summary>Minutes from the start of the exercise</summary>
        public int StartMinutes { get; set; }
    }

    public class MselDraftTeam
    {
        public string Name { get; set; }
        public string ShortName { get; set; }
        public string Description { get; set; }
    }

    public class MselDraftOrganization
    {
        public string Name { get; set; }
        public string ShortName { get; set; }
        public string Description { get; set; }
    }

    public class MselDraftInject
    {
        /// <summary>Minutes from the start of the exercise; the move follows from the time</summary>
        public int Minutes { get; set; }
        /// <summary>Inject, Information or Facilitation</summary>
        public string EventType { get; set; } = "Inject";
        /// <summary>Values keyed by the template's column (data field) name</summary>
        public Dictionary<string, string> Values { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>A template column the draft fills</summary>
    public class MselDraftField
    {
        public string Name { get; set; }
        public string DataType { get; set; }
        public List<string> Options { get; set; } = new List<string>();
    }

    public class MselDraftResult
    {
        public Guid TemplateMselId { get; set; }
        public string TemplateName { get; set; }
        public MselDraft Draft { get; set; }
        /// <summary>The template columns the model filled, in display order</summary>
        public List<MselDraftField> Fields { get; set; } = new List<MselDraftField>();
        /// <summary>Things the server corrected or dropped from the model's answer</summary>
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>A reviewed draft to create as a new MSEL</summary>
    public class MselDraftImport
    {
        public Guid TemplateMselId { get; set; }
        public MselDraft Draft { get; set; }
    }
}
