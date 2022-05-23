// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

namespace Blueprint.Api.Infrastructure.Options
{
    public class ClaimsTransformationOptions
    {
        public bool EnableCaching { get; set; }
        public double CacheExpirationSeconds { get; set; }
    }
}

