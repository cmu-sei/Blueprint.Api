// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;

namespace Blueprint.Api.ViewModels
{
    public class FileForm
    {
        public Guid? MselId { get; set; }
        public Guid? MselTemplateId { get; set; }
        public Guid? TeamId { get; set; }
        public IFormFile ToUpload { get; set; }
    }
}