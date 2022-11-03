// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Blueprint.Api.Infrastructure.Options
{
    public class ClientOptions
    {
        public string CiteApiUrl { get; set; }
        public string GalleryApiUrl { get; set; }
        public string SteamfitterApiUrl { get; set; }
    }
}

