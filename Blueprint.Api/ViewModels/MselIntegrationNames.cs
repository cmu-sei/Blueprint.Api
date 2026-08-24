// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// The display names of the things a MSEL is integrated with.
    /// </summary>
    /// <remarks>
    /// The ids of those things are on the Msel itself; only their names live here, because a name has
    /// to be read from the application that owns it. A name is empty when the MSEL has no association
    /// of that kind, or when that application could not be reached — the association is still valid in
    /// that case, so the name is best-effort and never fails the request.
    /// </remarks>
    public class MselIntegrationNames
    {
        public string PlayerViewName { get; set; } = "";
        public string GalleryCollectionName { get; set; } = "";
        public string GalleryExhibitName { get; set; } = "";
        public string CiteEvaluationName { get; set; } = "";
        public string CiteScoringModelName { get; set; } = "";
        public string SteamfitterScenarioName { get; set; } = "";
    }
}
