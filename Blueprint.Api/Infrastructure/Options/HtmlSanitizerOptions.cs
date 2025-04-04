// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;

namespace Blueprint.Api.Infrastructure.Options
{
    public class HtmlSanitizerOptions
    {
        public List<string> AllowedTags { get; set; } = [];
        public List<string> AllowedAttributes { get; set; } = [];
        public List<string> AllowedClasses { get; set; } = [];
        public List<string> AllowedCssProperties { get; set; } = [];
        public List<string> AllowedSchemes { get; set; } = [];
    }
}