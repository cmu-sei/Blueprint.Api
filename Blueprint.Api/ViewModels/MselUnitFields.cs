// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// The fields a MSEL Unit's create and edit requests share. A MSEL Unit is only the pairing of a MSEL
    /// and a Unit, so there are none: only <see cref="MselUnitCreate"/> names the pair, and an edit cannot
    /// move the pairing to another MSEL or Unit, nor change the Unit itself.
    /// </summary>
    public abstract class MselUnitFields
    {
    }

    public class MselUnitCreate : MselUnitFields
    {
        public Guid Id { get; set; }
        public Guid MselId { get; set; }
        public Guid UnitId { get; set; }
    }

    public class MselUnitEdit : MselUnitFields
    {
    }
}
