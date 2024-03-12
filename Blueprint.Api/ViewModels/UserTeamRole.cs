// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using Blueprint.Api.Data.Enumerations;

namespace Blueprint.Api.ViewModels
{
    public class UserTeamRole : Base
    {
        public Guid Id { get; set; }
        public Guid TeamId { get; set; }
        public Guid UserId { get; set; }
        public TeamRole Role { get; set; }
    }

}

