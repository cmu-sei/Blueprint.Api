// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// The fields a CITE Duty's create and edit requests share. The owning MSEL and the template flag are
    /// not among them: only <see cref="CiteDutyCreate"/> names them, so an edit cannot move a duty
    /// between MSELs or between a MSEL and the templates. The team is named by id only; its own
    /// endpoints edit it.
    /// </summary>
    public abstract class CiteDutyFields
    {
        /// <summary>A team of the duty's MSEL.</summary>
        public Guid? TeamId { get; set; }
        public string Name { get; set; }
    }

    public class CiteDutyCreate : CiteDutyFields
    {
        public Guid Id { get; set; }
        public Guid? MselId { get; set; }
        public bool IsTemplate { get; set; }
    }

    public class CiteDutyEdit : CiteDutyFields
    {
    }
}
