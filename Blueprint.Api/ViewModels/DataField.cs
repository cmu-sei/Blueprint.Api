// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using Blueprint.Api.Data.Enumerations;

namespace Blueprint.Api.ViewModels
{
    public class DataField : Base
    {
        public Guid Id { get; set; }
        public Guid? MselId { get; set; }
        public Guid? InjectTypeId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public DataFieldType DataType { get; set; }
        public int DisplayOrder { get; set; }
        public bool OnScenarioEventList { get; set; }
        public bool OnExerciseView { get; set; }
        public bool IsChosenFromList { get; set; } // flag that this DataField value should be chosen from a dropdown selector
        public bool IsMultiSelect { get; set; } // flag that this DataField value should be chosen from a multi-select dropdown
        public virtual ICollection<DataOption> DataOptions { get; set; } = new HashSet<DataOption>(); // values to be included in the dropdown selector
        public string CellMetadata { get; set; } // spreadsheet metadata defining the column header cell attributes
        public string ColumnMetadata { get; set; } // spreadsheet metadata defining the column attributes
        public bool IsShownOnDefaultTab { get; set; } // determines if this data field is displayed on the default tab
        public bool IsOnlyShownToOwners { get; set;} // determines if this data field gets displayed for all users or just owners (i.e. spreadsheet metadata)
        public string GalleryArticleParameter { get; set; } // the Gallery Article parameter associated with this DataField
        public bool IsTemplate { get; set; }
        public bool IsInformationField { get; set; }  // will be displayed for Information scenario event types
        public bool IsFacilitationField { get; set; }  // will be displayed for Facilitation scenario event types
    }

}
