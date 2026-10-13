// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// A variable in a Caster template that a MSEL team's Player team ID can be written to.
    /// </summary>
    public class CasterTeamVariable
    {
        public string Name { get; set; }

        /// <summary>
        /// The template gives the variable no default, so terraform fails the plan unless a team is mapped to it or Caster supplies it.
        /// </summary>
        public bool Required { get; set; }
    }
}
