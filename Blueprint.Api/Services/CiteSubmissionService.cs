// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.ViewModels;
using Cite.Api.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ResponseErrorType = IdentityModel.Client.ResponseErrorType;
using TokenResponse = IdentityModel.Client.TokenResponse;

namespace Blueprint.Api.Services
{
    public interface ICiteSubmissionService
    {
        Task<CiteTeamResponses> GetTeamResponsesAsync(Guid mselId, Guid teamId, int moveNumber, bool hasSystemPermission, CancellationToken ct);
    }

    /// <summary>
    /// Reads a team's answers on its CITE scoring model, for the Assessor View.
    /// </summary>
    /// <remarks>
    /// CITE is read as Blueprint's service account, the one that pushed the evaluation and so is its Owner,
    /// not with the caller's token: an assessor is rarely a member of the CITE evaluation. The caller is
    /// authorized here instead, with the same MSEL roles that may read the xAPI evidence beside it.
    /// <para />
    /// CITE's team submissions route (<c>evaluations/{id}/teams/{teamId}/submissions</c>) answers an Owner
    /// as an observer: the team's submission, every member's own, the official one and the averages the
    /// scoring model turns on. Only the team, official and team-average submissions are returned; a member's
    /// own submission is skipped, so nothing here names or singles out a participant.
    /// </remarks>
    public class CiteSubmissionService : ICiteSubmissionService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ClientOptions _clientOptions;
        private readonly ResourceOwnerAuthorizationOptions _resourceOwnerAuthorizationOptions;
        private readonly ILogger<CiteSubmissionService> _logger;

        public CiteSubmissionService(
            BlueprintContext context,
            IPrincipal user,
            IHttpClientFactory httpClientFactory,
            ClientOptions clientOptions,
            ResourceOwnerAuthorizationOptions resourceOwnerAuthorizationOptions,
            ILogger<CiteSubmissionService> logger)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _httpClientFactory = httpClientFactory;
            _clientOptions = clientOptions;
            _resourceOwnerAuthorizationOptions = resourceOwnerAuthorizationOptions;
            _logger = logger;
        }

        public async Task<CiteTeamResponses> GetTeamResponsesAsync(Guid mselId, Guid teamId, int moveNumber, bool hasSystemPermission, CancellationToken ct)
        {
            var msel = await _context.Msels.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();

            // the same MSEL roles that can open the Assessor View and read its xAPI evidence
            if (!hasSystemPermission &&
                !await MselOwnerRequirement.IsMet(_user.GetId(), msel.Id, _context) &&
                !await MselEditorRequirement.IsMet(_user.GetId(), msel.Id, _context) &&
                !await MselApproverRequirement.IsMet(_user.GetId(), msel.Id, _context) &&
                !await EvaluatorRequirement.IsMet(_user.GetId(), msel.Id, _context))
                throw new ForbiddenException();

            var team = await _context.Teams.AsNoTracking().FirstOrDefaultAsync(t => t.Id == teamId && t.MselId == mselId, ct);
            if (team == null)
                throw new EntityNotFoundException<TeamEntity>("Team not found on this MSEL");

            var responses = new CiteTeamResponses
            {
                MselId = mselId,
                TeamId = teamId,
                MoveNumber = moveNumber
            };

            if (!msel.UseCite || msel.CiteEvaluationId == null)
                return WithStatus(responses, CiteTeamResponsesStatus.NotPushed, "This MSEL has not been pushed to CITE.");

            // the push creates a CITE team, under the Blueprint team's id, only for a team with a CITE team type
            if (team.CiteTeamTypeId == null)
                return WithStatus(responses, CiteTeamResponsesStatus.TeamNotInCite, "This team has no CITE team type, so it is not in CITE.");

            // Each failure names the system that failed: Blueprint's own configuration, the identity provider
            // the service account signs in with, or CITE. A timeout is a failure like any other; only the
            // caller giving up (ct) is let through.
            var citeApiUrl = _clientOptions.CiteApiUrl;
            if (!Uri.TryCreate(citeApiUrl, UriKind.Absolute, out var citeApiUri) ||
                (citeApiUri.Scheme != Uri.UriSchemeHttp && citeApiUri.Scheme != Uri.UriSchemeHttps))
            {
                _logger.LogWarning("CiteApiUrl {CiteApiUrl} is not an http or https url, so team {TeamId} submissions on MSEL {MselId} cannot be read", citeApiUrl, teamId, mselId);
                return WithStatus(responses, CiteTeamResponsesStatus.Unavailable, "Blueprint has no valid CITE API URL configured.");
            }

            var (tokenResponse, identityProviderProblem) = await RequestServiceAccountTokenAsync(teamId, mselId, ct);
            if (identityProviderProblem != null)
                return WithStatus(responses, CiteTeamResponsesStatus.Unavailable, identityProviderProblem);

            var evaluationId = (Guid)msel.CiteEvaluationId;
            try
            {
                var citeApiClient = IntegrationCiteExtensions.GetCiteApiClient(_httpClientFactory, citeApiUrl, tokenResponse);

                Evaluation evaluation;
                try
                {
                    evaluation = await citeApiClient.GetEvaluationAsync(evaluationId, ct);
                }
                catch (ApiException ex) when (ex.StatusCode == (int)HttpStatusCode.NotFound)
                {
                    return WithStatus(responses, CiteTeamResponsesStatus.NotPushed, "CITE has no evaluation for this MSEL.");
                }

                responses.CurrentMoveNumber = evaluation.CurrentMoveNumber;
                // the push creates one CITE move per Blueprint move, with the same number
                if (evaluation.Moves == null || !evaluation.Moves.Any(m => m.MoveNumber == moveNumber))
                    return WithStatus(responses, CiteTeamResponsesStatus.MoveNotInCite, $"No CITE move {moveNumber}. The move was added after the push.");

                if (moveNumber > evaluation.CurrentMoveNumber)
                    return WithStatus(responses, CiteTeamResponsesStatus.MoveNotStarted, $"CITE is on move {evaluation.CurrentMoveNumber}, so move {moveNumber} has not started.");

                var scoringModel = await citeApiClient.GetScoringModelAsync(evaluation.ScoringModelId, ct);
                var submissions = await citeApiClient.GetByEvaluationTeamAsync(evaluationId, teamId, ct) ?? [];
                var moveSubmissions = submissions.Where(s => s.MoveNumber == moveNumber && s.UserId == null).ToList();

                responses.ScoringModelDescription = scoringModel?.Description;
                responses.TeamSubmission = ToResponse(
                    moveSubmissions.FirstOrDefault(s => s.TeamId == teamId && !s.ScoreIsAnAverage), scoringModel, moveNumber);
                responses.OfficialSubmission = ToResponse(
                    moveSubmissions.FirstOrDefault(s => s.TeamId == null && !s.ScoreIsAnAverage), scoringModel, moveNumber);
                responses.TeamAverageSubmission = ToResponse(
                    moveSubmissions.FirstOrDefault(s => s.TeamId == teamId && s.ScoreIsAnAverage), scoringModel, moveNumber);

                var answered = responses.TeamSubmission != null && responses.TeamSubmission.Categories
                    .Any(c => c.SelectedOptions.Count > 0 || c.Comments.Count > 0);
                responses.Status = answered ? CiteTeamResponsesStatus.Answered : CiteTeamResponsesStatus.NotAnswered;
                return responses;
            }
            catch (ApiException ex)
            {
                _logger.LogWarning(ex, "CITE answered {StatusCode} reading team {TeamId} submissions on MSEL {MselId}", ex.StatusCode, teamId, mselId);
                string message;
                if (ex.StatusCode == (int)HttpStatusCode.Forbidden || ex.StatusCode == (int)HttpStatusCode.Unauthorized)
                    message = "CITE refused Blueprint's service account.";
                // the generated client throws with the success status when the body is empty or not the expected JSON
                else if (ex.StatusCode >= 200 && ex.StatusCode < 300)
                    message = "CITE sent an answer Blueprint could not read.";
                else
                    message = $"CITE answered {ex.StatusCode}.";
                return WithStatus(responses, CiteTeamResponsesStatus.Unavailable, message);
            }
            // HttpClient's own timeout, which cancels without the caller having asked
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "CITE did not answer in time reading team {TeamId} submissions on MSEL {MselId}", teamId, mselId);
                return WithStatus(responses, CiteTeamResponsesStatus.Unavailable, "CITE did not answer in time.");
            }
            // a name that does not resolve, a refused connection, or a connection dropped mid-answer
            catch (System.Exception ex) when (ex is HttpRequestException or IOException)
            {
                _logger.LogWarning(ex, "CITE could not be reached reading team {TeamId} submissions on MSEL {MselId}", teamId, mselId);
                return WithStatus(responses, CiteTeamResponsesStatus.Unavailable, "CITE could not be reached.");
            }
        }

        /// <summary>
        /// The service account's token, or what the identity provider did wrong instead; never both.
        /// </summary>
        private async Task<(TokenResponse Token, string Problem)> RequestServiceAccountTokenAsync(Guid teamId, Guid mselId, CancellationToken ct)
        {
            TokenResponse tokenResponse;
            try
            {
                using var tokenHttpClient = _httpClientFactory.CreateClient();
                tokenResponse = await ApiClientsExtensions.RequestTokenAsync(_resourceOwnerAuthorizationOptions, tokenHttpClient, ct);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "The identity provider did not answer in time signing in to read team {TeamId} submissions on MSEL {MselId}", teamId, mselId);
                return (null, "The identity provider did not answer in time.");
            }
            // RequestTokenAsync throws when the discovery document is not read, with IdentityModel's own
            // exception inside when the transport failed, a timeout or the caller giving up included
            catch (System.Exception ex) when (ex is not OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
                if (ex.InnerException is OperationCanceledException)
                {
                    _logger.LogWarning(ex, "The identity provider did not answer in time signing in to read team {TeamId} submissions on MSEL {MselId}", teamId, mselId);
                    return (null, "The identity provider did not answer in time.");
                }
                _logger.LogWarning(ex, "The identity provider's discovery document could not be read signing in to read team {TeamId} submissions on MSEL {MselId}", teamId, mselId);
                return (null, "The identity provider's discovery document could not be read.");
            }

            if (!tokenResponse.IsError)
                return (tokenResponse, null);

            // IdentityModel answers a failed token request with an error response rather than throwing,
            // including the transport failing, which it marks as an exception
            if (tokenResponse.ErrorType == ResponseErrorType.Exception)
            {
                ct.ThrowIfCancellationRequested();
                _logger.LogWarning(tokenResponse.Exception, "The identity provider's token endpoint failed signing in to read team {TeamId} submissions on MSEL {MselId}", teamId, mselId);
                return (null, tokenResponse.Exception is OperationCanceledException
                    ? "The identity provider did not answer in time."
                    : "The identity provider could not be reached.");
            }

            _logger.LogWarning("The identity provider refused the service account ({Error}, HTTP {StatusCode}) signing in to read team {TeamId} submissions on MSEL {MselId}",
                tokenResponse.Error, (int)tokenResponse.HttpStatusCode, teamId, mselId);
            return (null, "The identity provider refused Blueprint's service account.");
        }

        private static CiteTeamResponses WithStatus(CiteTeamResponses responses, CiteTeamResponsesStatus status, string message)
        {
            responses.Status = status;
            responses.Message = message;
            return responses;
        }

        private static CiteSubmissionResponse ToResponse(Submission submission, ScoringModel scoringModel, int moveNumber)
        {
            if (submission == null)
                return null;

            var submissionCategories = (submission.SubmissionCategories ?? [])
                .GroupBy(sc => sc.ScoringCategoryId)
                .ToDictionary(g => g.Key, g => g.First());
            var scoringCategories = (scoringModel?.ScoringCategories ?? [])
                .Where(c => scoringModel?.DisplayScoringModelByMoveNumber != true ||
                    (moveNumber >= c.MoveNumberFirstDisplay && moveNumber <= c.MoveNumberLastDisplay))
                .OrderBy(c => c.DisplayOrder);

            var response = new CiteSubmissionResponse
            {
                Score = submission.Score,
                Status = submission.Status.ToString(),
                DateModified = submission.DateModified ?? submission.DateCreated,
                IsAverage = submission.ScoreIsAnAverage
            };
            foreach (var scoringCategory in scoringCategories)
            {
                if (!submissionCategories.TryGetValue(scoringCategory.Id, out var submissionCategory))
                    continue;

                var scoringOptions = (scoringCategory.ScoringOptions ?? []).ToDictionary(o => o.Id);
                var submissionOptions = (submissionCategory.SubmissionOptions ?? [])
                    .Where(so => scoringOptions.ContainsKey(so.ScoringOptionId))
                    .OrderBy(so => scoringOptions[so.ScoringOptionId].DisplayOrder)
                    .ToList();
                var category = new CiteCategoryResponse
                {
                    ScoringCategoryId = scoringCategory.Id,
                    Description = scoringCategory.Description,
                    DisplayOrder = scoringCategory.DisplayOrder,
                    Score = submissionCategory.Score
                };
                foreach (var submissionOption in submissionOptions)
                {
                    var scoringOption = scoringOptions[submissionOption.ScoringOptionId];
                    // an average marks an option by how many chose it, rather than by selecting it
                    var selected = submission.ScoreIsAnAverage ? submissionOption.SelectedCount > 0 : submissionOption.IsSelected;
                    if (selected)
                    {
                        category.SelectedOptions.Add(new CiteOptionResponse
                        {
                            Description = scoringOption.Description,
                            Value = scoringOption.Value,
                            IsModifier = scoringOption.IsModifier,
                            SelectedCount = submissionOption.SelectedCount
                        });
                    }
                    // an average's comments are one member's, copied from the first submission averaged
                    if (submission.ScoreIsAnAverage)
                        continue;
                    foreach (var comment in submissionOption.SubmissionComments ?? [])
                    {
                        if (string.IsNullOrWhiteSpace(comment.Comment))
                            continue;
                        category.Comments.Add(new CiteCommentResponse
                        {
                            OptionDescription = scoringOption.Description,
                            Text = comment.Comment
                        });
                    }
                }
                response.Categories.Add(category);
            }

            return response;
        }
    }
}
