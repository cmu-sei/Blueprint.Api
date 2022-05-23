// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;

namespace Blueprint.Api.Infrastructure.QueryParameters
{
    public class ScenarioEventGet
    {
        /// <summary>
        /// Whether or not to return records only for a designated MSEL
        /// </summary>
        public string MselId { get; set; }

        /// <summary>
        /// Whether or not to return records only for a designated team
        /// </summary>
        public string TeamId { get; set; }

        /// <summary>
        /// Whether or not to return records only for a designated move
        /// </summary>
        public string MoveId { get; set; }

    }
}

