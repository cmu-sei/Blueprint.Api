// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Blueprint.Api.ViewModels
{
    public class MselPage
    {
        public MselPage() {
        }

        public Guid Id { get; set; }
        public Guid MselId { get; set; }
        public string Name { get; set; }
        public string Content { get; set; }
        public bool AllCanView { get; set; }
        public bool IncludeInPlaybook { get; set; }
        /// <summary>
        /// The MSEL's marking and name, so a reader of the page can draw its banner without reading the MSEL, which a
        /// page reader may not be allowed to; null when the MSEL is not loaded.
        /// </summary>
        public string ArticleMarking { get; set; }
        public string ArticleMarkingText { get; set; }
        public string MselName { get; set; }
    }

}

