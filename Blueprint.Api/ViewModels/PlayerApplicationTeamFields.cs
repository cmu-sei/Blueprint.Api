// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// The fields a Player Application Team's create and edit requests share. The application and the Team
    /// are not among them: only <see cref="PlayerApplicationTeamCreate"/> names the pair, and both must
    /// belong to the same MSEL.
    /// </summary>
    public abstract class PlayerApplicationTeamFields
    {
        public int DisplayOrder { get; set; }
    }

    public class PlayerApplicationTeamCreate : PlayerApplicationTeamFields
    {
        public Guid Id { get; set; }
        public Guid PlayerApplicationId { get; set; }
        public Guid TeamId { get; set; }
    }

    public class PlayerApplicationTeamEdit : PlayerApplicationTeamFields
    {
    }
}
