// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using Blueprint.Api.Data.Enumerations;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// The fields a Data Field's create and edit requests share. What owns the field - a MSEL, an inject
    /// type or the template list - is not one of them: only <see cref="DataFieldCreate"/> names it, so an
    /// edit cannot move a field to another owner.
    /// </summary>
    public abstract class DataFieldFields
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public DataFieldType DataType { get; set; }
        public int DisplayOrder { get; set; }
        public bool OnScenarioEventList { get; set; }
        public bool OnExerciseView { get; set; }
        public bool IsChosenFromList { get; set; } // flag that this DataField value should be chosen from a dropdown selector
        public bool IsMultiSelect { get; set; } // flag that this DataField value should be chosen from a multi-select dropdown
        public ICollection<DataFieldOption> DataOptions { get; set; } = new List<DataFieldOption>(); // values to be included in the dropdown selector
        public string CellMetadata { get; set; } // spreadsheet metadata defining the column header cell attributes
        public string ColumnMetadata { get; set; } // spreadsheet metadata defining the column attributes
        public bool IsShownOnDefaultTab { get; set; } // determines if this data field is displayed on the default tab
        public bool IsOnlyShownToOwners { get; set; } // determines if this data field gets displayed for all users or just owners (i.e. spreadsheet metadata)
        public string GalleryArticleParameter { get; set; } // the Gallery Article parameter associated with this DataField
        public bool IsInformationField { get; set; }  // will be displayed for Information scenario event types
        public bool IsFacilitationField { get; set; }  // will be displayed for Facilitation scenario event types
        public bool IsAssessorVisible { get; set; }
    }

    /// <summary>
    /// A new Data Field, owned by at most one of a MSEL, an inject type or the template list.
    /// </summary>
    public class DataFieldCreate : DataFieldFields
    {
        public Guid Id { get; set; }
        public Guid? MselId { get; set; }
        public Guid? InjectTypeId { get; set; }
        public bool IsTemplate { get; set; }
    }

    public class DataFieldEdit : DataFieldFields
    {
    }
}
