// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// The fields a Data Value's create and edit requests share. The cell a value belongs to - its Data
    /// Field and Scenario Event - is not one of them: only <see cref="DataValueCreate"/> names it, so an
    /// edit cannot move a value to another cell.
    /// </summary>
    public abstract class DataValueFields
    {
        public string Value { get; set; }
        public string CellMetadata { get; set; }
    }

    public class DataValueCreate : DataValueFields
    {
        public Guid Id { get; set; }
        /// <summary>A Data Field of a MSEL.</summary>
        public Guid DataFieldId { get; set; }
        /// <summary>A Scenario Event of the Data Field's MSEL.</summary>
        public Guid ScenarioEventId { get; set; }
    }

    public class DataValueEdit : DataValueFields
    {
    }
}
