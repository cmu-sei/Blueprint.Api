// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blueprint.Api.ViewModels
{
    public class PlayerApplicationTeam
    {
        public PlayerApplicationTeam() {
        }

        public PlayerApplicationTeam(Guid playerApplicationId, Guid teamId)
        {
            PlayerApplicationId = playerApplicationId;
            TeamId = teamId;
        }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public Guid PlayerApplicationId { get; set; }
        public Guid TeamId { get; set; }
    }

}

