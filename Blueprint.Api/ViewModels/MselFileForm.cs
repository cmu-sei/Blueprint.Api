// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// A file upload that replaces an existing MSEL. MselId is optional and only guards
    /// against a mismatch with the MSEL id in the route.
    /// </summary>
    public class MselFileForm
    {
        public Guid? MselId { get; set; }

        [Required]
        public IFormFile ToUpload { get; set; }
    }
}
