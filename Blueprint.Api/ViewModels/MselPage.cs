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
    }

}

