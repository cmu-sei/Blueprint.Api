// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;

namespace Blueprint.Api.ViewModels
{
    public class CiteAction : Base
    {
        public Guid Id { get; set; }
        public Guid? MselId { get; set; }
        public virtual Msel Msel { get; set; }
        public Guid? TeamId { get; set; }
        public virtual Team Team { get; set; }
        public int MoveNumber { get; set; }
        public int InjectNumber { get; set; }
        public int ActionNumber { get; set; }
        public string Description { get; set; }
        public bool IsTemplate { get; set; }
    }

}

