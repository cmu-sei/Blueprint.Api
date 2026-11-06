// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Threading;
using STT = System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;

namespace Blueprint.Api.Infrastructure.Extensions
{
    /// <summary>
    /// The run row a push writes and a pull reads. A MSEL has at most one open run (any status but Removed):
    /// the one its own integration-id columns describe. The push writes each id to both; the pull reads the
    /// run's, clears the MSEL's as before and leaves the run's in place.
    /// </summary>
    public static class IntegrationRunExtensions
    {
        /// <summary>The MSEL's open run, tracked, or null.</summary>
        public static STT.Task<MselRunEntity> FindOpenRunAsync(BlueprintContext context, Guid mselId, CancellationToken ct) =>
            context.MselRuns
                .Where(r => r.MselId == mselId && r.Status != MselRunStatus.Removed)
                .OrderByDescending(r => r.DateCreated)
                .FirstOrDefaultAsync(ct);

        /// <summary>The MSEL's latest run of any status, untracked, or null.</summary>
        public static STT.Task<MselRunEntity> FindLatestRunAsync(BlueprintContext context, Guid mselId, CancellationToken ct) =>
            context.MselRuns
                .AsNoTracking()
                .Where(r => r.MselId == mselId)
                .OrderByDescending(r => r.DateCreated)
                .FirstOrDefaultAsync(ct);

        /// <summary>
        /// The run a push is starting: the MSEL's open run if it has one (a push that failed and is pushed
        /// again reuses its ids, so it is the same deployment), otherwise a new one added to the context.
        /// Either way it is Deploying and holds the ids the MSEL holds now. The caller saves.
        /// </summary>
        public static async STT.Task<MselRunEntity> StartRunAsync(BlueprintContext context, MselEntity msel, Guid createdBy, CancellationToken ct)
        {
            var run = await FindOpenRunAsync(context, msel.Id, ct);
            if (run == null)
            {
                run = new MselRunEntity
                {
                    Id = Guid.NewGuid(),
                    MselId = msel.Id,
                    CreatedBy = createdBy
                };
                context.MselRuns.Add(run);
            }
            run.Status = MselRunStatus.Deploying;
            CopyIds(msel, run);
            return run;
        }

        /// <summary>
        /// Writes the ids the MSEL holds now to its open run, through ExecuteUpdateAsync like the push's other
        /// progress writes. An id the MSEL does not hold is left as the run has it. Not cancelled, for the
        /// same reason the MSEL's own Caster ids are not: an id Blueprint never recorded is an orphan.
        /// </summary>
        public static STT.Task<int> RecordIdsAsync(BlueprintContext context, MselEntity msel) =>
            RecordIdsAsync(context, msel.Id, msel.PlayerViewId, msel.CiteEvaluationId, msel.CiteScoringModelId,
                msel.GalleryCollectionId, msel.GalleryExhibitId, msel.SteamfitterScenarioId,
                msel.CasterWorkspaceId, msel.CasterRunId);

        private static STT.Task<int> RecordIdsAsync(BlueprintContext context, Guid mselId,
            Guid? playerViewId, Guid? citeEvaluationId, Guid? citeScoringModelId,
            Guid? galleryCollectionId, Guid? galleryExhibitId, Guid? steamfitterScenarioId,
            Guid? casterWorkspaceId, Guid? casterRunId) =>
            context.MselRuns
                .Where(r => r.MselId == mselId && r.Status != MselRunStatus.Removed)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.PlayerViewId, r => playerViewId ?? r.PlayerViewId)
                    .SetProperty(r => r.CiteEvaluationId, r => citeEvaluationId ?? r.CiteEvaluationId)
                    .SetProperty(r => r.CiteScoringModelId, r => citeScoringModelId ?? r.CiteScoringModelId)
                    .SetProperty(r => r.GalleryCollectionId, r => galleryCollectionId ?? r.GalleryCollectionId)
                    .SetProperty(r => r.GalleryExhibitId, r => galleryExhibitId ?? r.GalleryExhibitId)
                    .SetProperty(r => r.SteamfitterScenarioId, r => steamfitterScenarioId ?? r.SteamfitterScenarioId)
                    .SetProperty(r => r.CasterWorkspaceId, r => casterWorkspaceId ?? r.CasterWorkspaceId)
                    .SetProperty(r => r.CasterRunId, r => casterRunId ?? r.CasterRunId),
                    CancellationToken.None);

        /// <summary>
        /// Marks the push complete on the MSEL's open run: Active, and started now unless it already had a
        /// start. A MSEL with no open run (a Caster retry of a push from before runs were recorded) gets one.
        /// The caller saves, with the MSEL's own Deployed status.
        /// </summary>
        public static async STT.Task CompleteRunAsync(BlueprintContext context, MselEntity msel, Guid createdBy, CancellationToken ct)
        {
            var run = await StartRunAsync(context, msel, createdBy, ct);
            run.Status = MselRunStatus.Active;
            run.ActualStartTime ??= DateTime.UtcNow;
        }

        /// <summary>
        /// The ids a pull removes: the open run's, with any the run lacks taken from the MSEL, or the MSEL's
        /// alone when it has no open run. Untracked; nothing written to it is saved.
        /// </summary>
        public static async STT.Task<MselRunEntity> GetIdsToPullAsync(BlueprintContext context, MselEntity msel, CancellationToken ct)
        {
            var run = await context.MselRuns
                .AsNoTracking()
                .Where(r => r.MselId == msel.Id && r.Status != MselRunStatus.Removed)
                .OrderByDescending(r => r.DateCreated)
                .FirstOrDefaultAsync(ct);
            return WithMselIds(run, msel);
        }

        /// <summary>
        /// <paramref name="run"/>'s ids, with any it lacks taken from <paramref name="msel"/>; the MSEL's alone
        /// when there is no run. A new object either way, so the run row is not changed.
        /// </summary>
        public static MselRunEntity WithMselIds(MselRunEntity run, MselEntity msel) => new()
        {
            Id = run?.Id ?? Guid.Empty,
            MselId = msel.Id,
            Status = run?.Status ?? MselRunStatus.Active,
            PlayerViewId = run?.PlayerViewId ?? msel.PlayerViewId,
            CiteEvaluationId = run?.CiteEvaluationId ?? msel.CiteEvaluationId,
            CiteScoringModelId = run?.CiteScoringModelId ?? msel.CiteScoringModelId,
            GalleryCollectionId = run?.GalleryCollectionId ?? msel.GalleryCollectionId,
            GalleryExhibitId = run?.GalleryExhibitId ?? msel.GalleryExhibitId,
            SteamfitterScenarioId = run?.SteamfitterScenarioId ?? msel.SteamfitterScenarioId,
            CasterWorkspaceId = run?.CasterWorkspaceId ?? msel.CasterWorkspaceId,
            CasterRunId = run?.CasterRunId ?? msel.CasterRunId,
            ActualStartTime = run?.ActualStartTime,
            ActualEndTime = run?.ActualEndTime
        };

        /// <summary>
        /// Marks the MSEL's open run Removed once its integrations are deleted, and ended now unless End Event
        /// already ended it. Its ids stay. The caller saves.
        /// </summary>
        public static async STT.Task RemoveRunAsync(BlueprintContext context, Guid mselId, CancellationToken ct)
        {
            var run = await FindOpenRunAsync(context, mselId, ct);
            if (run == null)
                return;
            run.Status = MselRunStatus.Removed;
            run.ActualEndTime ??= DateTime.UtcNow;
        }

        private static void CopyIds(MselEntity msel, MselRunEntity run)
        {
            run.PlayerViewId = msel.PlayerViewId ?? run.PlayerViewId;
            run.CiteEvaluationId = msel.CiteEvaluationId ?? run.CiteEvaluationId;
            run.CiteScoringModelId = msel.CiteScoringModelId ?? run.CiteScoringModelId;
            run.GalleryCollectionId = msel.GalleryCollectionId ?? run.GalleryCollectionId;
            run.GalleryExhibitId = msel.GalleryExhibitId ?? run.GalleryExhibitId;
            run.SteamfitterScenarioId = msel.SteamfitterScenarioId ?? run.SteamfitterScenarioId;
            run.CasterWorkspaceId = msel.CasterWorkspaceId ?? run.CasterWorkspaceId;
            run.CasterRunId = msel.CasterRunId ?? run.CasterRunId;
        }
    }
}
