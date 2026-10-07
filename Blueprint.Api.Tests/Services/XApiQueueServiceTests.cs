// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary><c>XApiQueueService</c> - the <c>xapi_queued_statements</c> table and the nine methods that
/// move a statement through it. Blueprint records what an exercise did as xAPI statements, and this is the
/// durable buffer between the request that produced one and the Learning Record Store that eventually
/// stores it.</summary>
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

    /// <summary>Enqueue assigns the status, retry count and queued time, whatever the caller handed
    /// it.</summary>
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

    /// <summary>Enqueue records nobody as the author of the statement.</summary>
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

    /// <summary>Dequeue counts the first attempt as a retry.</summary>
    [Fact]
    public async Task Dequeue_CountsTheFirstAttemptAsARetry()
    {
        var statement = Statement();
        await Seed(statement);

        await Service().DequeueAsync(ct: Ct);
        await Service().MarkCompletedAsync(statement.Id, Ct);

        Assert.Equal(1, (await Stored(statement.Id)).RetryCount);
    }

    /// <summary>Dequeue takes a pending statement however many times it has been tried.</summary>
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

    /// <summary>A poll of an empty queue saves nothing and logs nothing.</summary>
    [Fact]
    public async Task Dequeue_WithNothingPending_SaysNothing()
    {
        await Seed(Statement(status: XApiQueueStatus.Completed));
        var logger = new RecordingLogger<XApiQueueService>();

        var taken = await Service(logger).DequeueAsync(ct: Ct);

        Assert.Empty(taken);
        Assert.Empty(logger.Entries.Where(x => x.Level == LogLevel.Information).ToList());
    }

    /// <summary>Dequeue does not take a statement already in <c>Processing</c>.</summary>
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

    /// <summary>Marking a missing statement completed logs a warning and returns.</summary>
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

    /// <summary>Mark completed keeps the error from the attempt that failed.</summary>
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
        var error = Assert.Single(logger.At(LogLevel.Error).ToList());
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
        Assert.Empty(logger.At(LogLevel.Error).ToList());
    }

    /// <summary>Each failure replaces the stored error message.</summary>
    [Fact]
    public async Task MarkFailed_KeepsOnlyTheMostRecentError()
    {
        var statement = Statement(status: XApiQueueStatus.Processing);
        await Seed(statement);

        await Service().MarkFailedAsync(statement.Id, "503 Service Unavailable", true, Ct);
        await Service().MarkFailedAsync(statement.Id, "401 Unauthorized", false, Ct);

        Assert.Equal("[PERMANENT] 401 Unauthorized", (await Stored(statement.Id)).ErrorMessage);
    }

    /// <summary>Mark failed with a transient error returns the statement to the head of the queue.</summary>
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

    /// <summary>Queue depth counts <c>Pending</c> and <c>Processing</c> rows.</summary>
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

    /// <summary>The old-failed query returns <c>Failed</c> rows queued before the cutoff.</summary>
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

    /// <summary>The old-completed query measures retention from <c>QueuedAt</c>.</summary>
    [Fact]
    public async Task OldCompleted_MeasuresRetentionFromWhenTheStatementWasQueued()
    {
        var statement = Statement(status: XApiQueueStatus.Processing, queuedAt: Now.AddDays(-9));
        await Seed(statement);

        await Service().MarkCompletedAsync(statement.Id, Ct);
        var found = await Service().GetOldCompletedStatementsAsync(Now.AddDays(-7), Ct);

        Assert.Equal([statement.Id], found.Select(x => x.Id).ToList());
    }

    /// <summary>The stuck query returns <c>Processing</c> rows last attempted before the threshold.</summary>
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

    /// <summary>A <c>Processing</c> row with no <c>LastAttemptAt</c> is not returned.</summary>
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

    /// <summary>An id that is not there is skipped without an error.</summary>
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
