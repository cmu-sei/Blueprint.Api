// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System.Collections.Generic;

namespace Blueprint.Api.ViewModels
{
    public class DataOptionImportPreview
    {
        public List<DataOptionImportPreviewItem> Items { get; set; } = new List<DataOptionImportPreviewItem>();
        public string Error { get; set; }
    }

    public class DataOptionImportPreviewItem
    {
        public string OptionName { get; set; }
        public string OptionValue { get; set; }
        public string OptionDescription { get; set; }
        public bool Exists { get; set; }
    }
}
