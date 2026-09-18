// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>XApiQueueService</c> - the <c>xapi_queued_statements</c> table and the nine methods that move a
/// statement through it. Blueprint records what an exercise did as xAPI statements, and this is the
/// durable buffer between the request that produced one and the Learning Record Store that eventually
/// stores it.
/// </summary>
/// <remarks>
/// <para>
/// The state machine is <c>Pending</c> → <c>Processing</c> → <c>Completed</c> or <c>Failed</c>, driven
/// entirely by <c>XApiBackgroundService</c>; nothing else calls anything here but
/// <c>EnqueueAsync</c>. It takes a <c>BlueprintContext</c> and a logger and no authorization of any
/// kind, so this file needs no host - the service is constructed directly over the test's own database
/// and a <see cref="RecordingLogger{T}"/>, which is how the two missing-row branches are read.
/// </para>
/// <para>
/// <strong>A transient failure returns the statement to <c>Pending</c> without touching its
/// <c>QueuedAt</c>, and <c>DequeueAsync</c> orders by <c>QueuedAt</c>.</strong> So a statement the LRS
/// keeps rejecting with a retryable status is handed back at the head of the queue on every pass,
/// ahead of everything queued since, for as long as the host runs - there is no retry ceiling, by
/// design (<c>XApiQueueService.cs:56</c>) and no dead-letter state a permanent failure can be promoted
/// to. Ten such statements fill a batch and the newer ones never leave. See
/// <see cref="MarkFailed_WithATransientError_ReturnsTheStatementToTheHeadOfTheQueue"/>.
/// </para>
/// <para>
/// <strong><c>RetryCount</c> counts attempts, not retries.</strong> <c>DequeueAsync</c> increments it
/// while taking the statement, so a statement that reaches the LRS the first time it is offered is
/// recorded as having been retried once. See
/// <see cref="Dequeue_CountsTheFirstAttemptAsARetry"/>.
/// </para>
/// <para>
/// <strong>Retention is measured from when a statement was queued, not from when it finished.</strong>
/// Both cleanup queries filter on <c>QueuedAt</c>, so a statement that sat in the queue for longer than
/// <c>XApiOptions.RetentionDays</c> - which is what happens whenever the LRS has been unreachable -
/// is eligible for deletion the moment it completes. See
/// <see cref="OldCompleted_MeasuresRetentionFromWhenTheStatementWasQueued"/>. What the background
/// service then does with the rows it is handed is characterized in
/// <c>XApiBackgroundServiceTests</c>; it deletes them.
/// </para>
/// <para>
/// One thing here is right and worth recording because its neighbour is not: both
/// <c>Mark*Async</c> methods call <c>FindAsync(new object[] { statementId }, ct)</c>, the overload that
/// separates the key values from the token. <c>XApiService.cs:322</c> calls
/// <c>FindAsync(id, ct)</c> on the same shape of single-key entity, which binds the token as a second
/// key value - characterized in <c>XApiServiceTests</c>.
/// </para>
/// <para>
/// <strong>Nothing here locks a row.</strong> The table has no owner column and <c>DequeueAsync</c>
/// issues a plain <c>SELECT</c>, so two hosts polling one database can take the same batch and send
/// every statement in it twice. No test reproduces that - it would need two dequeues interleaved
/// mid-transaction, which no deterministic arrangement can arrange through this API - but the absence
/// is the point, and the LRS's own de-duplication by statement id is the only thing standing in for it.
/// </para>
/// <para>
/// Per this branch's rule, every test characterizes rather than fixes, and says what fixing it will do
/// to the test.
/// </para>
/// </remarks>
public class XApiQueueServiceTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    // ---------------------------------------------------------------------------------------------
    // EnqueueAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Enqueue_StoresTheStatementAndItsMetadata()
    {
        var mselId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var statement = Statement(verb: "initialized");
        statement.MselId = mselId;
        statement.TeamId = teamId;

        await Service().EnqueueAsync(statement, Ct);

        var stored = await Stored(statement.Id);
        Assert.Equal("{\"verb\":\"initialized\"}", stored.StatementJson);
        Assert.Equal("initialized", stored.Verb);
        Assert.Equal("https://blueprint.test/msels/1", stored.ActivityId);
        Assert.Equal(mselId, stored.MselId);
        Assert.Equal(teamId, stored.TeamId);
    }

    /// <remarks>
    /// The three fields the queue owns are assigned rather than defaulted, so a caller cannot queue a
    /// statement as anything but a fresh <c>Pending</c> one. That is the right call and it is also the
    /// reason a failed statement cannot be re-queued by handing it back: the method would reset the
    /// record of everything that had already been tried.
    /// </remarks>
    [Fact]
    public async Task Enqueue_OverwritesTheStatusRetryCountAndQueuedAt()
    {
        var statement = Statement(status: XApiQueueStatus.Failed, queuedAt: Ancient);
        statement.RetryCount = 9;

        var before = DateTime.UtcNow;
        await Service().EnqueueAsync(statement, Ct);

        Assert.InRange(statement.QueuedAt, before, DateTime.UtcNow);

        var stored = await Stored(statement.Id);
        Assert.Equal(XApiQueueStatus.Pending, stored.Status);
        Assert.Equal(0, stored.RetryCount);
        Assert.NotEqual(Ancient, stored.QueuedAt);
    }

    /// <remarks>
    /// <c>XApiQueuedStatementEntity</c> is a <c>BaseEntity</c>, so <c>SaveEntries</c> stamps
    /// <c>DateCreated</c> - but nothing sets <c>CreatedBy</c>, and <c>EnqueueAsync</c> is called from a
    /// request that knows exactly who the actor is. So the row that exists to be replayed to a Learning
    /// Record Store carries no record of which blueprint user produced it; the actor survives only as
    /// an <c>mbox</c> inside <c>StatementJson</c>, built from
    /// <c>XApiOptions.EmailDomain</c> and the <c>sub</c> claim. Setting <c>CreatedBy</c> turns this red.
    /// </remarks>
    [Fact]
    public async Task Enqueue_RecordsNobodyAsTheAuthorOfTheStatement()
    {
        var statement = Statement();

        var before = DateTime.UtcNow;
        await Service().EnqueueAsync(statement, Ct);

        var stored = await Stored(statement.Id);
        Assert.Equal(Guid.Empty, stored.CreatedBy);
        Assert.InRange(stored.DateCreated, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
    }

    // ---------------------------------------------------------------------------------------------
    // DequeueAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Dequeue_TakesOnlyPendingStatements()
    {
        var pending = Statement(verb: "wanted");
        await Seed(
            pending,
            Statement(status: XApiQueueStatus.Processing),
            Statement(status: XApiQueueStatus.Completed),
            Statement(status: XApiQueueStatus.Failed));

        var taken = await Service().DequeueAsync(ct: Ct);

        Assert.Equal([pending.Id], taken.Select(x => x.Id).ToList());
    }

    [Fact]
    public async Task Dequeue_TakesTheOldestFirst()
    {
        var newest = Statement(verb: "newest", queuedAt: Now);
        var oldest = Statement(verb: "oldest", queuedAt: Now.AddMinutes(-10));
        var middle = Statement(verb: "middle", queuedAt: Now.AddMinutes(-5));
        await Seed(newest, oldest, middle);

        var taken = await Service().DequeueAsync(ct: Ct);

        Assert.Equal(["oldest", "middle", "newest"], taken.Select(x => x.Verb).ToList());
    }

    [Fact]
    public async Task Dequeue_TakesAtMostTheBatchSize()
    {
        var last = Statement(verb: "last", queuedAt: Now.AddMinutes(-1));
        await Seed(
            Statement(queuedAt: Now.AddMinutes(-3)),
            Statement(queuedAt: Now.AddMinutes(-2)),
            last);

        var taken = await Service().DequeueAsync(2, Ct);

        Assert.Equal(2, taken.Count);
        Assert.Equal(XApiQueueStatus.Pending, (await Stored(last.Id)).Status);
    }

    [Fact]
    public async Task Dequeue_MarksWhatItTookAsProcessingAndStampsTheAttempt()
    {
        var statement = Statement();
        await Seed(statement);

        var before = DateTime.UtcNow;
        await Service().DequeueAsync(ct: Ct);

        var stored = await Stored(statement.Id);
        Assert.Equal(XApiQueueStatus.Processing, stored.Status);
        Assert.NotNull(stored.LastAttemptAt);
        Assert.InRange(stored.LastAttemptAt.Value, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
    }

    /// <remarks>
    /// The increment happens on the way out, before anything has been attempted, so the field is an
    /// attempt counter wearing a retry counter's name. It reads correctly in
    /// <c>MarkFailedAsync</c>'s log line - "encountered transient error on attempt 1" - and
    /// incorrectly everywhere a human reads the column, where a statement that worked immediately
    /// claims to have needed a retry. Counting after the failure instead turns this red.
    /// </remarks>
    [Fact]
    public async Task Dequeue_CountsTheFirstAttemptAsARetry()
    {
        var statement = Statement();
        await Seed(statement);

        await Service().DequeueAsync(ct: Ct);
        await Service().MarkCompletedAsync(statement.Id, Ct);

        Assert.Equal(1, (await Stored(statement.Id)).RetryCount);
    }

    /// <remarks>
    /// <c>XApiQueueService.cs:56</c> says so in a comment - "no retry count limit - transient errors
    /// retry indefinitely" - so this is deliberate rather than an oversight, and it is recorded because
    /// it is what makes the head-of-queue behaviour below permanent rather than temporary. A ceiling
    /// would turn this red, and would need somewhere to put the statement that exceeded it.
    /// </remarks>
    [Fact]
    public async Task Dequeue_HasNoRetryCeiling()
    {
        var statement = Statement();
        statement.RetryCount = 5000;
        await Seed(statement);

        var taken = await Service().DequeueAsync(ct: Ct);

        Assert.Equal([statement.Id], taken.Select(x => x.Id).ToList());
        Assert.Equal(5001, (await Stored(statement.Id)).RetryCount);
    }

    /// <remarks>
    /// The save and the log line are both inside <c>if (statements.Any())</c>, so a poll against an
    /// empty queue is one <c>SELECT</c> and nothing else. With <c>ProcessingDelaySeconds</c> at its
    /// 30-second minimum that is 2880 queries a day on an installation not using xAPI at all, which is
    /// cheap but not free, and is why <c>XApiBackgroundService</c>'s early return on a blank username
    /// matters.
    /// </remarks>
    [Fact]
    public async Task Dequeue_WithNothingPending_SaysNothing()
    {
        await Seed(Statement(status: XApiQueueStatus.Completed));
        var logger = new RecordingLogger<XApiQueueService>();

        var taken = await Service(logger).DequeueAsync(ct: Ct);

        Assert.Empty(taken);
        Assert.Empty(logger.Entries.Where(x => x.Level == LogLevel.Information).ToList());
    }

    /// <remarks>
    /// Which is correct, and is the only thing stopping the background service from sending a statement
    /// it is already sending. It is also why a statement whose host died mid-send is stranded: nothing
    /// returns a <c>Processing</c> row to <c>Pending</c>, and the sweep that finds it deletes it. See
    /// <see cref="StuckProcessing_ReturnsProcessingStatementsLastAttemptedBeforeTheThreshold"/>.
    /// </remarks>
    [Fact]
    public async Task Dequeue_IgnoresAStatementAlreadyBeingProcessed()
    {
        await Seed(Statement(status: XApiQueueStatus.Processing));

        Assert.Empty(await Service().DequeueAsync(ct: Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // MarkCompletedAsync and MarkFailedAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task MarkCompleted_SetsTheStatus()
    {
        var statement = Statement(status: XApiQueueStatus.Processing);
        await Seed(statement);

        await Service().MarkCompletedAsync(statement.Id, Ct);

        Assert.Equal(XApiQueueStatus.Completed, (await Stored(statement.Id)).Status);
    }

    /// <remarks>
    /// The missing row is a warning and a return rather than a throw, which is right - the background
    /// service has no better answer than to carry on - and the warning is the only trace. Reaching it
    /// needs the row to have been deleted between the dequeue and the mark, which
    /// <c>CleanupOldStatementsAsync</c> can do to a <c>Processing</c> row on the very pass that is
    /// sending it.
    /// </remarks>
    [Fact]
    public async Task MarkCompleted_ForAStatementThatIsNoLongerThere_WarnsAndCarriesOn()
    {
        var logger = new RecordingLogger<XApiQueueService>();
        var unknown = Guid.NewGuid();

        await Service(logger).MarkCompletedAsync(unknown, Ct);

        var warning = Assert.Single(logger.Entries.Where(x => x.Level == LogLevel.Warning).ToList());
        Assert.Contains(unknown.ToString(), warning.Message);
        Assert.Contains("non-existent", warning.Message);
    }

    /// <remarks>
    /// <c>MarkCompletedAsync</c> writes only the status, so the text of the last failure outlives the
    /// failure. A statement that went out on its fourth attempt is stored <c>Completed</c> with
    /// <c>[TRANSIENT] 503 …</c> still in its <c>ErrorMessage</c>, which reads - to anybody querying the
    /// table for what went wrong - as a statement that both failed and succeeded. Clearing the message
    /// on success turns this red.
    /// </remarks>
    [Fact]
    public async Task MarkCompleted_KeepsTheErrorFromTheAttemptThatFailed()
    {
        var statement = Statement(status: XApiQueueStatus.Processing);
        await Seed(statement);

        await Service().MarkFailedAsync(statement.Id, "503 from the LRS", true, Ct);
        await Service().MarkCompletedAsync(statement.Id, Ct);

        var stored = await Stored(statement.Id);
        Assert.Equal(XApiQueueStatus.Completed, stored.Status);
        Assert.Equal("[TRANSIENT] 503 from the LRS", stored.ErrorMessage);
    }

    [Fact]
    public async Task MarkFailed_WithATransientError_ReturnsTheStatementToPending()
    {
        var statement = Statement(status: XApiQueueStatus.Processing);
        await Seed(statement);
        var logger = new RecordingLogger<XApiQueueService>();

        await Service(logger).MarkFailedAsync(statement.Id, "429 Too Many Requests", true, Ct);

        var stored = await Stored(statement.Id);
        Assert.Equal(XApiQueueStatus.Pending, stored.Status);
        Assert.Equal("[TRANSIENT] 429 Too Many Requests", stored.ErrorMessage);
        var warning = Assert.Single(logger.Entries.Where(x => x.Level == LogLevel.Warning).ToList());
        Assert.Contains("will retry", warning.Message);
    }

    [Fact]
    public async Task MarkFailed_WithAPermanentError_MarksTheStatementFailed()
    {
        var statement = Statement(status: XApiQueueStatus.Processing);
        await Seed(statement);
        var logger = new RecordingLogger<XApiQueueService>();

        await Service(logger).MarkFailedAsync(statement.Id, "400 Bad Request", false, Ct);

        var stored = await Stored(statement.Id);
        Assert.Equal(XApiQueueStatus.Failed, stored.Status);
        Assert.Equal("[PERMANENT] 400 Bad Request", stored.ErrorMessage);
        var error = Assert.Single(logger.Errors.ToList());
        Assert.Contains("permanent error after", error.Message);
    }

    [Fact]
    public async Task MarkFailed_ForAStatementThatIsNoLongerThere_WarnsAndCarriesOn()
    {
        var logger = new RecordingLogger<XApiQueueService>();
        var unknown = Guid.NewGuid();

        await Service(logger).MarkFailedAsync(unknown, "anything", false, Ct);

        var warning = Assert.Single(logger.Entries.Where(x => x.Level == LogLevel.Warning).ToList());
        Assert.Contains(unknown.ToString(), warning.Message);
        Assert.Contains("non-existent", warning.Message);
        Assert.Empty(logger.Errors.ToList());
    }

    /// <remarks>
    /// One column, assigned rather than appended, so a statement that has failed forty times records
    /// only the fortieth - and because the prefix is part of the same string, a statement that failed
    /// permanently after a run of transient errors keeps no evidence of them. Appending, or a separate
    /// attempt-history table, turns this red.
    /// </remarks>
    [Fact]
    public async Task MarkFailed_KeepsOnlyTheMostRecentError()
    {
        var statement = Statement(status: XApiQueueStatus.Processing);
        await Seed(statement);

        await Service().MarkFailedAsync(statement.Id, "503 Service Unavailable", true, Ct);
        await Service().MarkFailedAsync(statement.Id, "401 Unauthorized", false, Ct);

        Assert.Equal("[PERMANENT] 401 Unauthorized", (await Stored(statement.Id)).ErrorMessage);
    }

    /// <remarks>
    /// <para>
    /// The transient branch writes the status and the message and nothing else, so the statement keeps
    /// the <c>QueuedAt</c> it was first queued with - and <c>DequeueAsync</c> orders by <c>QueuedAt</c>.
    /// A statement the LRS will never accept but always answers retryably (any 5xx, a 429, a timeout,
    /// or - per <c>XApiBackgroundService</c> - any exception it does not recognize) therefore comes back
    /// first on every pass, indefinitely, and ten of them fill the batch.
    /// </para>
    /// <para>
    /// Re-stamping <c>QueuedAt</c> on a retry turns this red, and would make the queue fair; ordering
    /// by <c>LastAttemptAt</c> with <c>QueuedAt</c> as a tie-break would do it without losing the
    /// record of when the statement was produced, which matters because the cleanup queries measure
    /// retention from that field.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task MarkFailed_WithATransientError_ReturnsTheStatementToTheHeadOfTheQueue()
    {
        var poison = Statement(verb: "poison", status: XApiQueueStatus.Processing, queuedAt: Now.AddDays(-2));
        var fresh = Statement(verb: "fresh", queuedAt: Now);
        await Seed(poison, fresh);

        await Service().MarkFailedAsync(poison.Id, "504 Gateway Timeout", true, Ct);
        var taken = await Service().DequeueAsync(1, Ct);

        Assert.Equal(["poison"], taken.Select(x => x.Verb).ToList());
        var stored = await Stored(poison.Id);
        Assert.InRange(stored.QueuedAt, Now.AddDays(-2).AddSeconds(-1), Now.AddDays(-2).AddSeconds(1));
    }

    // ---------------------------------------------------------------------------------------------
    // GetQueueDepthAsync
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// Depth is work outstanding, so <c>Processing</c> counts - which also means a statement stranded
    /// in <c>Processing</c> by a host that died is counted as outstanding until the cleanup sweep
    /// deletes it, up to <c>ProcessingTimeoutMinutes</c> later. Nothing calls this method: it is
    /// reachable through no controller, no worker and no health check, so the queue has no observable
    /// depth at all.
    /// </remarks>
    [Fact]
    public async Task QueueDepth_CountsPendingAndProcessingAndNothingElse()
    {
        await Seed(
            Statement(),
            Statement(),
            Statement(status: XApiQueueStatus.Processing),
            Statement(status: XApiQueueStatus.Completed),
            Statement(status: XApiQueueStatus.Failed));

        Assert.Equal(3, await Service().GetQueueDepthAsync(Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // The three cleanup queries
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task OldCompleted_ReturnsCompletedStatementsQueuedBeforeTheCutoff()
    {
        var old = Statement(verb: "old", status: XApiQueueStatus.Completed, queuedAt: Now.AddDays(-8));
        await Seed(
            old,
            Statement(status: XApiQueueStatus.Completed, queuedAt: Now),
            Statement(status: XApiQueueStatus.Failed, queuedAt: Now.AddDays(-8)),
            Statement(queuedAt: Now.AddDays(-8)));

        var found = await Service().GetOldCompletedStatementsAsync(Now.AddDays(-7), Ct);

        Assert.Equal([old.Id], found.Select(x => x.Id).ToList());
    }

    /// <remarks>
    /// The mirror of the query above, one status along, and the reason it matters is what the caller
    /// does with the answer: <c>XApiBackgroundService.CleanupOldStatementsAsync</c> deletes these rows.
    /// A permanent failure is therefore destroyed <c>RetentionDays</c> after it was queued, taking the
    /// only copy of the statement and the reason it was rejected with it - so a misconfigured LRS
    /// endpoint loses a week of exercise telemetry silently, and there is nothing left to replay.
    /// </remarks>
    [Fact]
    public async Task OldFailed_ReturnsFailedStatementsQueuedBeforeTheCutoff()
    {
        var old = Statement(verb: "old", status: XApiQueueStatus.Failed, queuedAt: Now.AddDays(-8));
        await Seed(
            old,
            Statement(status: XApiQueueStatus.Failed, queuedAt: Now),
            Statement(status: XApiQueueStatus.Completed, queuedAt: Now.AddDays(-8)));

        var found = await Service().GetOldFailedStatementsAsync(Now.AddDays(-7), Ct);

        Assert.Equal([old.Id], found.Select(x => x.Id).ToList());
    }

    /// <remarks>
    /// Both cleanup queries filter on <c>QueuedAt</c>, and nothing in the table records when a
    /// statement reached the LRS. So a statement queued nine days ago and completed a second ago is
    /// already older than the seven-day retention and goes on the next pass, while
    /// <c>ErrorMessage</c> - the one field that would explain the nine-day wait - goes with it.
    /// Filtering on a completion timestamp turns this red, and would need the column to exist;
    /// <c>LastAttemptAt</c> is the closest thing the table has.
    /// </remarks>
    [Fact]
    public async Task OldCompleted_MeasuresRetentionFromWhenTheStatementWasQueued()
    {
        var statement = Statement(status: XApiQueueStatus.Processing, queuedAt: Now.AddDays(-9));
        await Seed(statement);

        await Service().MarkCompletedAsync(statement.Id, Ct);
        var found = await Service().GetOldCompletedStatementsAsync(Now.AddDays(-7), Ct);

        Assert.Equal([statement.Id], found.Select(x => x.Id).ToList());
    }

    /// <remarks>
    /// The statement a host was sending when it stopped. This query is how the background service finds
    /// it - and it <em>deletes</em> what it finds rather than returning it to <c>Pending</c>, so a
    /// restart during a send loses the statement. Requeueing instead turns
    /// <c>XApiBackgroundServiceTests</c> red, not this test; what this one pins is that the sweep looks
    /// at <c>LastAttemptAt</c> and not at <c>QueuedAt</c> like its two neighbours, which is the right
    /// field for the question and is why a freshly-dequeued statement is safe from it.
    /// </remarks>
    [Fact]
    public async Task StuckProcessing_ReturnsProcessingStatementsLastAttemptedBeforeTheThreshold()
    {
        var stuck = Statement(verb: "stuck", status: XApiQueueStatus.Processing, queuedAt: Now.AddDays(-1));
        stuck.LastAttemptAt = Now.AddMinutes(-30);
        var inFlight = Statement(status: XApiQueueStatus.Processing, queuedAt: Now.AddDays(-1));
        inFlight.LastAttemptAt = Now;
        var failedLongAgo = Statement(status: XApiQueueStatus.Failed, queuedAt: Now.AddDays(-1));
        failedLongAgo.LastAttemptAt = Now.AddMinutes(-30);
        await Seed(stuck, inFlight, failedLongAgo);

        var found = await Service().GetStuckProcessingStatementsAsync(Now.AddMinutes(-10), Ct);

        Assert.Equal([stuck.Id], found.Select(x => x.Id).ToList());
    }

    /// <remarks>
    /// Defensive rather than reachable: <c>EnqueueAsync</c> forces <c>Pending</c> and only
    /// <c>DequeueAsync</c> writes <c>Processing</c>, and it always stamps <c>LastAttemptAt</c> in the
    /// same breath. The branch is pinned because it is a branch, and because a row in this state would
    /// be invisible to every query in the file - not dequeued, not swept, and counted in a depth
    /// nobody reads.
    /// </remarks>
    [Fact]
    public async Task StuckProcessing_IgnoresAStatementThatRecordsNoAttempt()
    {
        var statement = Statement(status: XApiQueueStatus.Processing, queuedAt: Now.AddDays(-1));
        statement.LastAttemptAt = null;
        await Seed(statement);

        Assert.Empty(await Service().GetStuckProcessingStatementsAsync(Now, Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // DeleteStatementsAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task DeleteStatements_RemovesOnlyTheStatementsItWasNamed()
    {
        var doomed = Statement(status: XApiQueueStatus.Completed);
        var kept = Statement();
        await Seed(doomed, kept);
        var logger = new RecordingLogger<XApiQueueService>();

        await Service(logger).DeleteStatementsAsync([doomed.Id], Ct);

        await using var cold = NewContext();
        var remaining = await cold.XApiQueuedStatements.Select(x => x.Id).ToListAsync(Ct);
        Assert.Equal([kept.Id], remaining);
        Assert.Contains("Deleted 1 xAPI statements from queue", logger.Entries.Select(x => x.Message).ToList());
    }

    /// <remarks>
    /// An unknown id is neither an error nor a log line of its own - the count in the
    /// <c>Deleted {Count}</c> line is the number of rows found, not the number asked for, so the only
    /// evidence that a caller asked for something absent is the discrepancy between the two, and no
    /// caller checks. Both callers are <c>CleanupOldStatementsAsync</c> passing ids it read a moment
    /// earlier, so in practice the difference is a row another host deleted first.
    /// </remarks>
    [Fact]
    public async Task DeleteStatements_IgnoresAnIdThatIsNotThere()
    {
        var kept = Statement();
        await Seed(kept);
        var logger = new RecordingLogger<XApiQueueService>();

        await Service(logger).DeleteStatementsAsync([Guid.NewGuid()], Ct);

        Assert.Contains("Deleted 0 xAPI statements from queue", logger.Entries.Select(x => x.Message).ToList());
        Assert.NotNull(await Stored(kept.Id));
    }

    [Fact]
    public async Task DeleteStatements_WithAnEmptyList_RemovesNothing()
    {
        var kept = Statement();
        await Seed(kept);

        await Service().DeleteStatementsAsync([], Ct);

        Assert.NotNull(await Stored(kept.Id));
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// One base instant for the whole test, so a seeded <c>QueuedAt</c> and the cutoff a query is given
    /// are offsets from the same point rather than from two readings of the clock.
    /// </summary>
    private static readonly DateTime Now = DateTime.UtcNow;

    /// <summary>
    /// A hostile <c>QueuedAt</c>, for the assertion that <c>EnqueueAsync</c> overwrites what it is
    /// handed. UTC by kind, because the column is <c>timestamp with time zone</c> and Npgsql refuses
    /// anything else.
    /// </summary>
    private static readonly DateTime Ancient = new(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The service under test, over the test's own database. A fresh logger unless the test wants to
    /// read what was logged.
    /// </summary>
    private XApiQueueService Service(RecordingLogger<XApiQueueService> logger = null) =>
        new(Db, logger ?? new RecordingLogger<XApiQueueService>());

    /// <summary>
    /// A queued statement. <c>Id</c> is assigned rather than left to the column's database default, so
    /// a test can name the row it seeded before saving it.
    /// </summary>
    private static XApiQueuedStatementEntity Statement(
        XApiQueueStatus status = XApiQueueStatus.Pending,
        string verb = "viewed",
        DateTime? queuedAt = null) => new()
        {
            Id = Guid.NewGuid(),
            StatementJson = $"{{\"verb\":\"{verb}\"}}",
            Verb = verb,
            ActivityId = "https://blueprint.test/msels/1",
            Status = status,
            QueuedAt = queuedAt ?? Now,
        };

    /// <summary>
    /// The row as the database holds it, read through a cold change tracker so a test cannot pass on a
    /// value that only ever existed in the entity the service was handed.
    /// </summary>
    private async Task<XApiQueuedStatementEntity> Stored(Guid id)
    {
        await using var cold = NewContext();

        return await cold.XApiQueuedStatements.SingleOrDefaultAsync(x => x.Id == id, Ct);
    }
}
