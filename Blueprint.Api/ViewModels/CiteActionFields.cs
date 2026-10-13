// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// The fields a CITE Action's create and edit requests share. The owning MSEL and the template flag
    /// are not among them: only <see cref="CiteActionCreate"/> names them, so an edit cannot move an
    /// action between MSELs or between a MSEL and the templates.
    /// </summary>
    public abstract class CiteActionFields
    {
        /// <summary>A team of the action's MSEL.</summary>
        public Guid? TeamId { get; set; }
        public int MoveNumber { get; set; }
        public int InjectNumber { get; set; }
        public int ActionNumber { get; set; }
        public string Description { get; set; }
    }

    public class CiteActionCreate : CiteActionFields
    {
        public Guid Id { get; set; }
        public Guid? MselId { get; set; }
        public bool IsTemplate { get; set; }
    }

    public class CiteActionEdit : CiteActionFields
    {
    }
}
