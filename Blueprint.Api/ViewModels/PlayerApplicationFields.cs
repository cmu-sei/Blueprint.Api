// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// The fields a Player Application's create and edit requests share. The owning MSEL is not one of
    /// them: only <see cref="PlayerApplicationCreate"/> names it, so an edit cannot move an application to
    /// another MSEL.
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
        public Guid MselId { get; set; }
    }

    public class PlayerApplicationEdit : PlayerApplicationFields
    {
    }
}
