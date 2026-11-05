// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
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

            var evaluationId = (Guid)msel.CiteEvaluationId;
            try
            {
                var citeApiClient = await GetServiceAccountClientAsync(ct);

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
                var message = ex.StatusCode == (int)HttpStatusCode.Forbidden || ex.StatusCode == (int)HttpStatusCode.Unauthorized
                    ? "CITE refused Blueprint's service account."
                    : $"CITE answered {ex.StatusCode}.";
                return WithStatus(responses, CiteTeamResponsesStatus.Unavailable, message);
            }
            catch (System.Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "CITE could not be read for team {TeamId} submissions on MSEL {MselId}", teamId, mselId);
                return WithStatus(responses, CiteTeamResponsesStatus.Unavailable, "CITE could not be reached.");
            }
        }

        private async Task<CiteApiClient> GetServiceAccountClientAsync(CancellationToken ct)
        {
            using var tokenHttpClient = _httpClientFactory.CreateClient();
            var tokenResponse = await ApiClientsExtensions.RequestTokenAsync(_resourceOwnerAuthorizationOptions, tokenHttpClient);
            if (tokenResponse.IsError)
                throw new InvalidOperationException($"The service account token request failed: {tokenResponse.Error}");

            return IntegrationCiteExtensions.GetCiteApiClient(_httpClientFactory, _clientOptions.CiteApiUrl, tokenResponse);
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
