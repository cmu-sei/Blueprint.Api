// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;

namespace Blueprint.Api.ViewModels
{
    public class Organization : Base
    {
         public Guid Id { get; set; }
        public string Name { get; set;}
        public string Description { get; set; }
        public string Summary { get; set; }
        public bool IsTemplate { get; set; }
        public Guid? MselId { get; set; }
   }
}

