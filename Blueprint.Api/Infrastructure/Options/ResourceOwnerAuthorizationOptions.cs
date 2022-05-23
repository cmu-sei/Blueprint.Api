// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Blueprint.Api.Infrastructure.Options
{
    public class ResourceOwnerAuthorizationOptions
    {
        public string Authority { get; set; }
        public string ClientId { get; set; }
        public string ClientSecret { get; set; }
        public string UserName { get; set; }
        public string Password { get; set; }
        public string Scope { get; set; }
        public int TokenExpirationBufferSeconds { get; set; }
        public bool ValidateDiscoveryDocument { get; set; }
    }
}

