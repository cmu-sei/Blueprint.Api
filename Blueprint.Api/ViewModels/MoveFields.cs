// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// The fields a Move's create and edit requests share. The owning MSEL is not one of them: only
    /// <see cref="MoveCreate"/> names it, so an edit cannot move a Move to another MSEL.
    /// </summary>
    public abstract class MoveFields
    {
        public int MoveNumber { get; set; }
        public string Description { get; set; }
        public int DeltaSeconds { get; set; }
        public DateTime? MoveStartTime { get; set; }
        public DateTime? SituationTime { get; set; }
        public string SituationDescription { get; set; }
    }

    public class MoveCreate : MoveFields
    {
        public Guid Id { get; set; }
        public Guid MselId { get; set; }
    }

    public class MoveEdit : MoveFields
    {
    }
}
