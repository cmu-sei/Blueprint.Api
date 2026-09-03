// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// The in-memory ledger behind <c>GET competencyframeworks/imports/{importId}</c>: what a client sees
/// while a framework import is running.
/// </summary>
/// <remarks>
/// <para>
/// Tested without a host because it has no dependencies at all - no database, no clock, no logger - so
/// every branch is reachable from a fresh instance. The endpoint that reads it is covered in
/// <see cref="CompetencyFrameworkImportTests"/>, where the phases arrive from a real import instead of
/// from these calls.
/// </para>
/// <para>
/// Two properties carry the design. The percentage only ever increases, because a bar that goes backwards
/// reads as a bug in the import rather than in the reporting; and a terminal status is final, because the
/// importers report phases from inside a transaction that may still fail, so a phase report can arrive
/// after the request has already answered. Both are asserted below.
/// </para>
/// <para>
/// One thing is deliberately not tested: <c>Prune</c>'s removal half. Retention is a 30-minute constant
/// and the timestamps are stamped from <c>DateTime.UtcNow</c> with no seam, so an entry old enough to be
/// pruned cannot be constructed. <see cref="Begin_DoesNotPruneAnEntryThatIsStillFresh"/> covers the half
/// that is reachable. Note also that pruning only happens in <c>Begin</c>, so on an installation where
/// nobody starts an import the dictionary never shrinks.
/// </para>
/// </remarks>
public class CompetencyFrameworkImportProgressServiceTests
{
    [Fact]
    public void Get_ForAnUnknownId_IsNull()
    {
        Assert.Null(Service().Get(Guid.NewGuid()));
    }

    [Fact]
    public void Begin_ReportsARunningImportAtZero()
    {
        var service = Service();
        var id = Guid.NewGuid();
        var before = DateTime.UtcNow;

        var returned = service.Begin(id);

        var status = service.Get(id);
        Assert.Equal(id, status.Id);
        Assert.Equal(CompetencyFrameworkImportState.Running, status.State);
        Assert.Equal("Starting", status.Phase);
        Assert.Equal(0, status.PhaseNumber);
        Assert.Equal(0, status.PhaseCount);
        Assert.Equal(0, status.PercentComplete);
        Assert.Null(status.CompletedAt);
        Assert.Null(status.Error);
        Assert.Null(status.FrameworkId);
        Assert.InRange(status.StartedAt, before, DateTime.UtcNow);
        Assert.Equal(status.StartedAt, status.UpdatedAt);

        // Begin hands back the status it stored, so a caller that wants it need not poll for it.
        Assert.Equal(id, returned.Id);
    }

    /// <summary>
    /// A second import under the same id starts over rather than being refused, which is what makes a
    /// client-generated id safe to reuse after a failure.
    /// </summary>
    [Fact]
    public void Begin_ForAnIdAlreadyUsed_ReplacesTheEarlierImport()
    {
        var service = Service();
        var id = Guid.NewGuid();
        service.Begin(id);
        service.Fail(id, "the first attempt");

        service.Begin(id);

        var status = service.Get(id);
        Assert.Equal(CompetencyFrameworkImportState.Running, status.State);
        Assert.Null(status.Error);
        Assert.Null(status.CompletedAt);
    }

    [Fact]
    public void Begin_DoesNotPruneAnEntryThatIsStillFresh()
    {
        var service = Service();
        var first = Guid.NewGuid();
        service.Begin(first);
        service.Succeed(first, Guid.NewGuid(), "done");

        service.Begin(Guid.NewGuid());

        Assert.NotNull(service.Get(first));
    }

    [Fact]
    public void ReportPhase_RecordsThePhaseAndItsCounts()
    {
        var service = Service();
        var id = Guid.NewGuid();
        service.Begin(id);
        var before = DateTime.UtcNow;

        service.ReportPhase(id, "Saving competencies", 3, 6, 250, 1000);

        var status = service.Get(id);
        Assert.Equal("Saving competencies", status.Phase);
        Assert.Equal(3, status.PhaseNumber);
        Assert.Equal(6, status.PhaseCount);
        Assert.Equal(250, status.Processed);
        Assert.Equal(1000, status.Total);
        Assert.InRange(status.UpdatedAt, before, DateTime.UtcNow);
        Assert.Null(status.CompletedAt);
    }

    [Fact]
    public void ReportPhase_ForAnUnknownId_DoesNothing()
    {
        var service = Service();
        var id = Guid.NewGuid();

        service.ReportPhase(id, "Saving competencies", 3, 6);

        // Not created on the way past: only Begin registers an import.
        Assert.Null(service.Get(id));
    }

    /// <summary>
    /// Phases are weighted equally, so phase n of m has completed (n-1)/m of the work on entry and the
    /// processed/total fraction fills that phase's own share.
    /// </summary>
    /// <remarks>
    /// The last case pins <c>Math.Round</c>'s default: a midpoint goes to the even value, so 12.5 reports
    /// as 12 rather than 13. Nothing depends on which way it goes - it is one percent on a progress bar -
    /// but a test asserting the arithmetic has to agree with the rounding mode, and switching to
    /// <c>MidpointRounding.AwayFromZero</c> should be a deliberate change rather than a silent one.
    /// </remarks>
    [Theory]
    [InlineData(1, 6, 0, 0, 0)]
    [InlineData(2, 6, 0, 0, 17)]
    [InlineData(3, 6, 0, 0, 33)]
    [InlineData(6, 6, 0, 0, 83)]
    [InlineData(3, 6, 500, 1000, 42)]
    [InlineData(3, 6, 1000, 1000, 50)]
    [InlineData(1, 2, 1, 4, 12)]
    public void ReportPhase_WeightsThePhasesEqually(
        int phaseNumber, int phaseCount, int processed, int total, int expected)
    {
        var service = Service();
        var id = Guid.NewGuid();
        service.Begin(id);

        service.ReportPhase(id, "phase", phaseNumber, phaseCount, processed, total);

        Assert.Equal(expected, service.Get(id).PercentComplete);
    }

    /// <summary>
    /// A phase whose work cannot be counted passes total = 0, which contributes nothing rather than
    /// dividing by zero.
    /// </summary>
    [Fact]
    public void ReportPhase_WithAnUncountablePhase_ReportsOnlyTheCompletedPhases()
    {
        var service = Service();
        var id = Guid.NewGuid();
        service.Begin(id);

        service.ReportPhase(id, "Building hierarchy", 4, 6, processed: 900, total: 0);

        Assert.Equal(50, service.Get(id).PercentComplete);
    }

    [Theory]
    [InlineData(0, 6)]
    [InlineData(-1, 6)]
    [InlineData(3, 0)]
    [InlineData(3, -6)]
    public void ReportPhase_WithANonsensePhase_ReportsZero(int phaseNumber, int phaseCount)
    {
        var service = Service();
        var id = Guid.NewGuid();
        service.Begin(id);

        service.ReportPhase(id, "phase", phaseNumber, phaseCount);

        Assert.Equal(0, service.Get(id).PercentComplete);
    }

    /// <summary>
    /// A phase change does not move the bar backwards: the phase the import is entering starts exactly
    /// where the phase it is leaving finished.
    /// </summary>
    /// <remarks>
    /// That is arithmetic rather than the <c>Math.Max</c> guard - phase n at total/total computes
    /// (n-1+1)/m, which is phase n+1's floor - so this test still passes with the guard removed. The
    /// guard covers reports arriving out of order, and
    /// <see cref="ReportPhase_GoingBackwards_LowersEverythingButThePercentage"/> is what witnesses it.
    /// Both are kept: this one pins the sequence the importers actually produce.
    /// </remarks>
    [Fact]
    public void ReportPhase_NeverLowersThePercentage()
    {
        var service = Service();
        var id = Guid.NewGuid();
        service.Begin(id);
        service.ReportPhase(id, "Saving competencies", 3, 6, 1000, 1000);

        service.ReportPhase(id, "Building hierarchy", 4, 6, 0, 0);

        // Phase 4 on its own computes 50, which is what phase 3 already reached.
        Assert.Equal(50, service.Get(id).PercentComplete);
        Assert.Equal("Building hierarchy", service.Get(id).Phase);
        Assert.Equal(4, service.Get(id).PhaseNumber);
    }

    /// <summary>
    /// Only the percentage is monotonic. The phase, its number and its counts are whatever was reported
    /// last, so a report that goes backwards is visible in every field except the bar.
    /// </summary>
    [Fact]
    public void ReportPhase_GoingBackwards_LowersEverythingButThePercentage()
    {
        var service = Service();
        var id = Guid.NewGuid();
        service.Begin(id);
        service.ReportPhase(id, "Saving relationships", 5, 6, 10, 10);

        service.ReportPhase(id, "Reading file", 1, 6);

        var status = service.Get(id);
        Assert.Equal(83, status.PercentComplete);
        Assert.Equal("Reading file", status.Phase);
        Assert.Equal(1, status.PhaseNumber);
        Assert.Equal(0, status.Processed);
    }

    /// <summary>
    /// A running import never reports 100 however far through it is, because the last phase is loading
    /// the framework to return it - work the client is still waiting on. Only <c>Succeed</c> reports 100.
    /// </summary>
    [Fact]
    public void ReportPhase_ForTheLastPhase_StopsShortOf100()
    {
        var service = Service();
        var id = Guid.NewGuid();
        service.Begin(id);

        service.ReportPhase(id, "Loading framework", 6, 6, 1000, 1000);

        Assert.Equal(99, service.Get(id).PercentComplete);
    }

    [Fact]
    public void ReportPhase_ClampsAProcessedCountAboveItsTotal()
    {
        var service = Service();
        var id = Guid.NewGuid();
        service.Begin(id);

        service.ReportPhase(id, "Saving competencies", 3, 6, processed: 5000, total: 1000);

        Assert.Equal(50, service.Get(id).PercentComplete);
    }

    [Fact]
    public void Succeed_ReportsTheFrameworkAt100()
    {
        var service = Service();
        var id = Guid.NewGuid();
        var frameworkId = Guid.NewGuid();
        service.Begin(id);
        service.ReportPhase(id, "Saving competencies", 3, 6);
        var before = DateTime.UtcNow;

        service.Succeed(id, frameworkId, "NICE Framework");

        var status = service.Get(id);
        Assert.Equal(CompetencyFrameworkImportState.Succeeded, status.State);
        Assert.Equal("Complete", status.Phase);
        Assert.Equal(100, status.PercentComplete);
        Assert.Equal(frameworkId, status.FrameworkId);
        Assert.Equal("NICE Framework", status.FrameworkName);
        Assert.Null(status.Error);
        Assert.NotNull(status.CompletedAt);
        Assert.InRange(status.CompletedAt.Value, before, DateTime.UtcNow);
        Assert.Equal(status.CompletedAt, status.UpdatedAt);

        // The phase number is pushed to the end so "step n of m" reads as finished rather than stalled
        // at whatever phase happened to report last.
        Assert.Equal(status.PhaseCount, status.PhaseNumber);
    }

    /// <summary>
    /// Characterizes a rough edge. <c>Succeed</c> sets the phase number from <c>PhaseCount</c>, which is
    /// only ever written by <c>ReportPhase</c> - so an import that succeeds without reporting a single
    /// phase reports "step 0 of 0" alongside 100%. No importer does that (all six phases are reported
    /// before the framework is returned), which is why this is characterized rather than fixed.
    /// </summary>
    [Fact]
    public void Succeed_WithoutAnyPhaseReported_LeavesThePhaseCountAtZero()
    {
        var service = Service();
        var id = Guid.NewGuid();
        service.Begin(id);

        service.Succeed(id, Guid.NewGuid(), "framework");

        var status = service.Get(id);
        Assert.Equal(0, status.PhaseCount);
        Assert.Equal(0, status.PhaseNumber);
        Assert.Equal(100, status.PercentComplete);
    }

    [Fact]
    public void Succeed_ForAnUnknownId_DoesNothing()
    {
        var service = Service();
        var id = Guid.NewGuid();

        service.Succeed(id, Guid.NewGuid(), "framework");

        Assert.Null(service.Get(id));
    }

    [Fact]
    public void Fail_ReportsTheError()
    {
        var service = Service();
        var id = Guid.NewGuid();
        service.Begin(id);
        var before = DateTime.UtcNow;

        service.Fail(id, "CSV file is empty or has no data rows.");

        var status = service.Get(id);
        Assert.Equal(CompetencyFrameworkImportState.Failed, status.State);
        Assert.Equal("CSV file is empty or has no data rows.", status.Error);
        Assert.NotNull(status.CompletedAt);
        Assert.InRange(status.CompletedAt.Value, before, DateTime.UtcNow);
        Assert.Equal(status.CompletedAt, status.UpdatedAt);
    }

    /// <summary>
    /// A failure leaves the phase and the percentage where they were, so a client can show how far the
    /// import got before it failed. It reads oddly - "Saving competencies, 33%, Failed" - but the
    /// alternative is discarding the only information about where the file went wrong.
    /// </summary>
    [Fact]
    public void Fail_KeepsThePhaseTheImportFailedIn()
    {
        var service = Service();
        var id = Guid.NewGuid();
        service.Begin(id);
        service.ReportPhase(id, "Saving competencies", 3, 6, 250, 1000);

        service.Fail(id, "Database error importing framework");

        var status = service.Get(id);
        Assert.Equal("Saving competencies", status.Phase);
        Assert.Equal(3, status.PhaseNumber);
        Assert.Equal(38, status.PercentComplete);
    }

    [Fact]
    public void Fail_ForAnUnknownId_DoesNothing()
    {
        var service = Service();
        var id = Guid.NewGuid();

        service.Fail(id, "went wrong");

        Assert.Null(service.Get(id));
    }

    /// <summary>
    /// A phase report arriving after the import has finished is discarded. The importers report from
    /// inside a transaction, so this is not hypothetical: a phase can be reported and the transaction can
    /// then fail, and a status that had already answered "Failed" must not go back to "Running".
    /// </summary>
    [Theory]
    [InlineData(CompetencyFrameworkImportState.Succeeded)]
    [InlineData(CompetencyFrameworkImportState.Failed)]
    public void ReportPhase_AfterTheImportFinished_IsIgnored(CompetencyFrameworkImportState state)
    {
        var service = Service();
        var id = Guid.NewGuid();
        service.Begin(id);
        service.ReportPhase(id, "Saving competencies", 3, 6);

        if (state == CompetencyFrameworkImportState.Succeeded)
            service.Succeed(id, Guid.NewGuid(), "framework");
        else
            service.Fail(id, "went wrong");

        service.ReportPhase(id, "Saving relationships", 5, 6);

        var status = service.Get(id);
        Assert.Equal(state, status.State);
        Assert.NotEqual("Saving relationships", status.Phase);
    }

    /// <summary>
    /// Characterizes a gap. The terminal-state guard is in <c>ReportPhase</c> only, so <c>Fail</c> will
    /// overwrite a succeeded import and <c>Succeed</c> will overwrite a failed one. Unreachable through
    /// the controller - <c>RunImportAsync</c> calls exactly one of the two per import - which is why it is
    /// characterized rather than fixed. It turns red if the guard is hoisted into all three methods.
    /// </summary>
    [Fact]
    public void Fail_AfterSucceeding_OverwritesTheSuccess()
    {
        var service = Service();
        var id = Guid.NewGuid();
        service.Begin(id);
        service.Succeed(id, Guid.NewGuid(), "framework");

        service.Fail(id, "too late");

        var status = service.Get(id);
        Assert.Equal(CompetencyFrameworkImportState.Failed, status.State);
        Assert.Equal("too late", status.Error);

        // And the success it overwrote is still there beside the failure, so the status now says both.
        Assert.Equal(100, status.PercentComplete);
        Assert.NotNull(status.FrameworkId);
    }

    /// <summary>
    /// Every read is a copy. The import mutates the stored status while MVC is serializing the answer to
    /// a poll, so handing out the live instance would let a phase change land halfway through a response.
    /// </summary>
    [Fact]
    public void Get_ReturnsACopyThatLaterProgressDoesNotChange()
    {
        var service = Service();
        var id = Guid.NewGuid();
        service.Begin(id);
        service.ReportPhase(id, "Saving competencies", 3, 6);

        var polled = service.Get(id);
        service.ReportPhase(id, "Saving relationships", 5, 6);

        Assert.Equal("Saving competencies", polled.Phase);
        Assert.Equal(3, polled.PhaseNumber);
        Assert.Equal("Saving relationships", service.Get(id).Phase);
    }

    [Fact]
    public void Get_ReturnsADifferentInstanceEachTime()
    {
        var service = Service();
        var id = Guid.NewGuid();
        service.Begin(id);

        Assert.NotSame(service.Get(id), service.Get(id));
    }

    /// <summary>
    /// Imports do not interfere with one another, which is the whole reason the id is client-generated.
    /// </summary>
    [Fact]
    public void ReportPhase_OnlyTouchesItsOwnImport()
    {
        var service = Service();
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        service.Begin(mine);
        service.Begin(theirs);

        service.ReportPhase(mine, "Saving competencies", 3, 6);
        service.Succeed(theirs, Guid.NewGuid(), "theirs");

        Assert.Equal(CompetencyFrameworkImportState.Running, service.Get(mine).State);
        Assert.Equal("Saving competencies", service.Get(mine).Phase);
        Assert.Equal(CompetencyFrameworkImportState.Succeeded, service.Get(theirs).State);
        Assert.Equal("theirs", service.Get(theirs).FrameworkName);
    }

    private static ICompetencyFrameworkImportProgressService Service() =>
        new CompetencyFrameworkImportProgressService();
}
