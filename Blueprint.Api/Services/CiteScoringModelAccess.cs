// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Infrastructure.Exceptions;
using Cite.Api.Client;

namespace Blueprint.Api.Services
{
    /// <summary>
    /// Reads a CITE scoring model with the caller's own token, for a MSEL to copy or link. The push builds the
    /// evaluation from it with Blueprint's service account, so Blueprint accepts only a model the caller can read in
    /// full in CITE: not one CITE refuses them, and not one it shows them as a participant, equations redacted.
    /// </summary>
    public static class CiteScoringModelAccess
    {
        // what CITE's ScoringModelService puts in place of each category's equation for a participant or observer
        internal const string RedactedEquation = "Redacted";

        public static async Task<ScoringModel> GetViewableAsync(ICiteApiClient citeApiClient, Guid scoringModelId, CancellationToken ct)
        {
            ScoringModel scoringModel;
            try
            {
                scoringModel = await citeApiClient.GetScoringModelAsync(scoringModelId, ct);
            }
            catch (ApiException ex) when (
                ex.StatusCode == (int)HttpStatusCode.Unauthorized ||
                ex.StatusCode == (int)HttpStatusCode.Forbidden ||
                ex.StatusCode == (int)HttpStatusCode.NotFound)
            {
                scoringModel = null;
            }
            if (scoringModel == null)
                throw new CiteAccessDeniedException($"CITE scoring model {scoringModelId} does not exist or you cannot view it in CITE.");
            if ((scoringModel.ScoringCategories ?? []).Any(c => c.CalculationEquation == RedactedEquation))
                throw new CiteAccessDeniedException($"You can view the CITE scoring model \"{scoringModel.Description}\" only as a participant, without its equations and weights. Ask a CITE administrator for view access to it.");

            return scoringModel;
        }
    }
}
