// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// The fields a Card Team's create and edit requests share. The Card and the Team are not among them:
    /// only <see cref="CardTeamCreate"/> names the pair, and both must belong to the same MSEL.
    /// </summary>
    public abstract class CardTeamFields
    {
        public bool IsShownOnWall { get; set; }
        public bool CanPostArticles { get; set; }
    }

    public class CardTeamCreate : CardTeamFields
    {
        public Guid Id { get; set; }
        public Guid CardId { get; set; }
        public Guid TeamId { get; set; }
    }

    public class CardTeamEdit : CardTeamFields
    {
    }
}
