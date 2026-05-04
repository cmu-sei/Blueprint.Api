// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using Blueprint.Api.Data.Enumerations;

namespace Blueprint.Api.ViewModels
{
    public class UserMselRole : Base
    {
        public Guid Id { get; set; }
        public Guid MselId { get; set; }
        public Guid UserId { get; set; }
        public MselRole Role { get; set; }
        public string CiteEvaluationRole { get; set; }
        public string GalleryExhibitRole { get; set; }
        public string SteamfitterScenarioRole { get; set; }
    }

}

