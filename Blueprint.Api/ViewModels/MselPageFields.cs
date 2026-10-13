// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// The fields a MSEL Page's create and edit requests share. The owning MSEL is not one of them: only
    /// <see cref="MselPageCreate"/> names it, so an edit cannot move a page to another MSEL.
    /// </summary>
    public abstract class MselPageFields
    {
        public string Name { get; set; }
        public string Content { get; set; }
        public bool AllCanView { get; set; }
        public bool IncludeInPlaybook { get; set; }
    }

    public class MselPageCreate : MselPageFields
    {
        public Guid Id { get; set; }
        public Guid MselId { get; set; }
    }

    public class MselPageEdit : MselPageFields
    {
    }
}
