// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;

namespace Blueprint.Api.ViewModels
{
    public class CompetencyAssertion
    {
        public Guid MselId { get; set; }
        public Guid CompetencyId { get; set; }
        public Guid? ScenarioEventId { get; set; }
        public Guid? TeamId { get; set; }

        /// <summary>
        /// The members of the team being assessed. When null or empty, every eligible member of the team
        /// is assessed: everyone on it except observers and the assessor.
        /// </summary>
        public List<Guid> UserIds { get; set; }
        public Guid ProficiencyLevelId { get; set; }
        public string Comment { get; set; }
        public int? MoveNumber { get; set; }
        public int? GroupNumber { get; set; }
    }
}
