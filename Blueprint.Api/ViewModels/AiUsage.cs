// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;

namespace Blueprint.Api.ViewModels
{
    /// <summary>Calls to AI models and what they used</summary>
    public class AiUsageTotals
    {
        /// <summary>The MSEL's name, or the operation: draft, article, profile, value, assist or photo</summary>
        public string Name { get; set; }
        public int Calls { get; set; }
        /// <summary>Calls a safety classifier declined; they are billed too</summary>
        public int Refused { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheWriteTokens { get; set; }
        public int Images { get; set; }
        /// <summary>
        /// The estimated cost in millionths of a US dollar (1,000,000 is $1), from the configured prices; null when a model
        /// used has no price. A whole number, as every number the API sends is.
        /// </summary>
        public long? EstimatedCostMicros { get; set; }
    }

    /// <summary>What building one MSEL with AI used, in all and by operation</summary>
    public class AiUsageSummary : AiUsageTotals
    {
        public Guid MselId { get; set; }
        public string MselName { get; set; }
        /// <summary>Models used that have no configured price</summary>
        public List<string> Unpriced { get; set; } = new List<string>();
        public DateTime? FirstCall { get; set; }
        public DateTime? LastCall { get; set; }
        public List<AiUsageTotals> ByOperation { get; set; } = new List<AiUsageTotals>();
    }
}
