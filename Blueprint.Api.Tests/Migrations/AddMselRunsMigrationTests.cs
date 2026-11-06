// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Blueprint.Api.Tests.Migrations;

/// <summary>
/// The <c>AddMselRuns</c> migration's backfill: one run for every MSEL that already holds integration ids.
/// </summary>
/// <remarks>
/// Each test takes its own database back to the migration before, seeds MSELs into the <c>msels</c> table
/// (which that migration does not change), then applies <c>AddMselRuns</c> again, so the backfill runs over
/// exactly the rows the test seeded.
/// </remarks>
public class AddMselRunsMigrationTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    [Fact]
    public async Task Backfill_GivesADeployedMselAnActiveRunWithEveryIdItHolds()
    {
        var msel = Pushed(MselItemStatus.Deployed);
        msel.CasterWorkspaceId = Guid.NewGuid();
        msel.CasterRunId = Guid.NewGuid();
        msel.CiteScoringModelId = Guid.NewGuid();

        var run = Assert.Single(await Backfill(msel));

        Assert.Equal(msel.Id, run.MselId);
        Assert.NotEqual(Guid.Empty, run.Id);
        Assert.Equal(MselRunStatus.Active, run.Status);
        Assert.Equal(msel.PlayerViewId, run.PlayerViewId);
        Assert.Equal(msel.GalleryCollectionId, run.GalleryCollectionId);
        Assert.Equal(msel.GalleryExhibitId, run.GalleryExhibitId);
        Assert.Equal(msel.CiteEvaluationId, run.CiteEvaluationId);
        Assert.Equal(msel.CiteScoringModelId, run.CiteScoringModelId);
        Assert.Equal(msel.SteamfitterScenarioId, run.SteamfitterScenarioId);
        Assert.Equal(msel.CasterWorkspaceId, run.CasterWorkspaceId);
        Assert.Equal(msel.CasterRunId, run.CasterRunId);
        Assert.Equal(msel.CreatedBy, run.CreatedBy);
        Assert.Null(run.ActualStartTime);
        Assert.Null(run.ActualEndTime);
    }

    /// <summary>A MSEL still Pushing, which is also where a failed push stays, gets a Deploying run.</summary>
    [Fact]
    public async Task Backfill_GivesAMselStillPushingADeployingRun()
    {
        var run = Assert.Single(await Backfill(Pushed(MselItemStatus.Pushing)));

        Assert.Equal(MselRunStatus.Deploying, run.Status);
    }

    /// <summary>Any one id is a push; a Gallery-only MSEL has no Player view, and a Caster-only one only a
    /// workspace.</summary>
    [Fact]
    public async Task Backfill_CountsAnyOneIdAsAPush()
    {
        var gallery = TestData.Msel(status: MselItemStatus.Deployed);
        gallery.GalleryCollectionId = Guid.NewGuid();
        gallery.GalleryExhibitId = Guid.NewGuid();
        var caster = TestData.Msel(status: MselItemStatus.Deployed);
        caster.CasterWorkspaceId = Guid.NewGuid();

        var runs = await Backfill(gallery, caster);

        Assert.Equal(
            new[] { gallery.Id, caster.Id }.OrderBy(x => x),
            runs.Select(x => x.MselId).OrderBy(x => x));
    }

    /// <summary>
    /// A MSEL never pushed gets no run, even with a CITE scoring model chosen: that id names the template
    /// the push copies, not something a push made.
    /// </summary>
    [Fact]
    public async Task Backfill_GivesNoRunToAMselNeverPushed()
    {
        var pushed = Pushed(MselItemStatus.Deployed);
        var never = TestData.Msel(status: MselItemStatus.Approved);
        never.CiteScoringModelId = Guid.NewGuid();
        var template = TestData.Msel(isTemplate: true);

        var runs = await Backfill(pushed, never, template);

        Assert.Equal([pushed.Id], runs.Select(x => x.MselId).ToList());
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static MselEntity Pushed(MselItemStatus status)
    {
        var msel = TestData.Msel(status: status);
        msel.PlayerViewId = Guid.NewGuid();
        msel.GalleryCollectionId = Guid.NewGuid();
        msel.GalleryExhibitId = Guid.NewGuid();
        msel.CiteEvaluationId = Guid.NewGuid();
        msel.SteamfitterScenarioId = Guid.NewGuid();
        return msel;
    }

    /// <summary>Re-applies <c>AddMselRuns</c> over <paramref name="msels"/> and returns the runs it made.</summary>
    private async Task<List<MselRunEntity>> Backfill(params MselEntity[] msels)
    {
        var migrations = Db.Database.GetMigrations().ToList();
        var addMselRuns = migrations.Single(x => x.EndsWith("_AddMselRuns"));
        var before = migrations[migrations.IndexOf(addMselRuns) - 1];
        var migrator = Db.GetService<IMigrator>();

        await migrator.MigrateAsync(before, Ct);
        await Seed(msels);
        await migrator.MigrateAsync(addMselRuns, Ct);

        return await ReadBack(db => db.MselRuns.AsNoTracking().ToListAsync(Ct));
    }
}
