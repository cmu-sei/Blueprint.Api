// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blueprint.Api.ViewModels
{
    public class UnitUser : Base
    {
        public UnitUser() { }

        public UnitUser(Guid userId, Guid unitId)
        {
            UserId = userId;
            UnitId = unitId;
        }

        public Guid Id { get; set; }

        public Guid UserId { get; set; }
        public User User { get; set; }

        public Guid UnitId { get; set; }
        public Unit Unit { get; set; }
    }

}

