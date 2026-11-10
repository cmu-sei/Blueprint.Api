// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// The fields a Player Application's create and edit requests share. The owning MSEL and the template
    /// flag are not among them: only <see cref="PlayerApplicationCreate"/> names them, so an edit cannot move
    /// an application to another MSEL or between a MSEL and the templates.
    /// </summary>
    public abstract class PlayerApplicationFields
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public string Icon { get; set; }
        public bool? Embeddable { get; set; }
        public bool? LoadInBackground { get; set; }
    }

    public class PlayerApplicationCreate : PlayerApplicationFields
    {
        public Guid Id { get; set; }
        /// <summary>The MSEL the application belongs to, or null for a template.</summary>
        public Guid? MselId { get; set; }
        public bool IsTemplate { get; set; }
    }

    public class PlayerApplicationEdit : PlayerApplicationFields
    {
    }
}
