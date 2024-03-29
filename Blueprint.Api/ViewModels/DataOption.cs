// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;

namespace Blueprint.Api.ViewModels
{
    public class DataOption : Base
    {
        public Guid Id { get; set; }
        public Guid DataFieldId { get; set; }
        public string OptionName {get; set; }
        public string OptionValue {get; set; }
        public int DisplayOrder { get; set; }
    }

}

