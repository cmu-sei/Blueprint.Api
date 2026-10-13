// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// The fields a Card's create and edit requests share. The owning MSEL and the template flag are not
    /// among them: only <see cref="CardCreate"/> names them, so an edit cannot move a Card between MSELs
    /// or between a MSEL and the templates.
    /// </summary>
    public abstract class CardFields
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public int Move { get; set; }
        public int Inject { get; set; }
        public Guid? GalleryId { get; set; }
    }

    public class CardCreate : CardFields
    {
        public Guid Id { get; set; }
        public Guid? MselId { get; set; }
        public bool IsTemplate { get; set; }
    }

    public class CardEdit : CardFields
    {
    }
}
