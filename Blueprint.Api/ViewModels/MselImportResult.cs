// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System.Collections.Generic;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// What a JSON MSEL import did. The import never stops for a missing CITE scoring model,
    /// an unknown team type or an unreachable CITE, so this is how the importer finds out
    /// what was changed or skipped instead of discovering it at push time.
    /// </summary>
    public class MselImportResult
    {
        public Msel Msel { get; set; }
        public MselImportCounts Imported { get; set; } = new MselImportCounts();

        /// <summary>Values the import changed or dropped, and settings the importer should check before pushing.</summary>
        public List<MselImportMessage> Changes { get; set; } = new List<MselImportMessage>();

        /// <summary>Steps that could not run, e.g. CITE was unreachable. The MSEL was still imported.</summary>
        public List<MselImportFailure> Failures { get; set; } = new List<MselImportFailure>();
    }

    public class MselImportCounts
    {
        public int Moves { get; set; }
        public int ScenarioEvents { get; set; }
        public int Cards { get; set; }
        public int Teams { get; set; }
        public int DataFields { get; set; }
        public int Pages { get; set; }
    }

    public class MselImportMessage
    {
        /// <summary>The MSEL field concerned, e.g. "name" or "citeScoringModel".</summary>
        public string Field { get; set; }
        public string Message { get; set; }
    }

    public class MselImportFailure
    {
        /// <summary>The import step that failed, e.g. "citeScoringModel" or "citeTeamTypes".</summary>
        public string Step { get; set; }
        public string Message { get; set; }
    }
}
