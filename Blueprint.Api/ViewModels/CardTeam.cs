// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blueprint.Api.ViewModels
{
    public class CardTeam
    {
        public CardTeam() {
        }

        public CardTeam(Guid cardId, Guid teamId)
        {
            CardId = cardId;
            TeamId = teamId;
            IsShownOnWall = true;
            CanPostArticles = false;
        }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public Guid CardId { get; set; }
        public Guid TeamId { get; set; }
        public bool IsShownOnWall { get; set; }
        public bool CanPostArticles { get; set; }
    }

}

