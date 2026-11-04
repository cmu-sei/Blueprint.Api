// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using Blueprint.Api.Services;

namespace Blueprint.Api.ViewModels
{
    /// <summary>The network map Blueprint draws from a MSEL's Caster workspace topology.</summary>
    public class TopologyMapPreview
    {
        public Guid MselId { get; set; }
        public Guid CasterWorkspaceId { get; set; }

        /// <summary>Plan while the workspace's latest plan is still to be applied, State after.</summary>
        public string Source { get; set; }

        /// <summary>The drawing, an SVG document.</summary>
        public string Svg { get; set; }

        /// <summary>The drawing as the data URL Player is given as the map's image.</summary>
        public string ImageUrl { get; set; }

        /// <summary>One per machine with a Player name, on Player's 0 to 100 scale.</summary>
        public List<TopologyMapClickPoint> ClickPoints { get; set; } = new();

        public int MachineCount { get; set; }
        public int NetworkCount { get; set; }

        /// <summary>Machines drawn without a click point, because they have no Player record.</summary>
        public List<string> MachinesWithoutConsole { get; set; } = new();
    }

    /// <summary>What Update map did in Player.</summary>
    public class TopologyMapResult
    {
        public TopologyMapPreview Map { get; set; }

        /// <summary>The Player VM map's id. The same for every update of one view's map.</summary>
        public Guid PlayerMapId { get; set; }

        /// <summary>True when the map was made now, false when an existing one was replaced.</summary>
        public bool Created { get; set; }

        /// <summary>The Player teams the map is for.</summary>
        public List<Guid> TeamIds { get; set; } = new();

        /// <summary>True when the Network Map application was added to the view now.</summary>
        public bool ApplicationAdded { get; set; }

        /// <summary>The teams the Network Map application was added to now.</summary>
        public List<Guid> ApplicationTeamIds { get; set; } = new();
    }
}
