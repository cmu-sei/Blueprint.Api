// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Sdk;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>XApiBackgroundService</c> - the only thing in blueprint that ever sends a statement to a Learning
/// Record Store, and the only thing that ever deletes one.
/// </summary>
/// <remarks>
/// <para>
/// A hosted service, so it is unreachable through the host: <c>BlueprintAppFactory</c> does
/// <c>RemoveAll&lt;IHostedService&gt;()</c>, which is what stops this loop dialling an LRS during every
/// other test in the suite. It is constructed here over a mini-container holding the test's own
/// database, a real <c>XApiQueueService</c>, a test-owned <c>XApiOptions</c> and
/// <see cref="TestHttpHandler.AsFactory"/> - which reaches the POSTs because this is the one xAPI method
/// that asks for an <c>IHttpClientFactory</c> rather than doing <c>new HttpClient()</c>. Its sibling
/// <c>XApiService.GetStatementsAsync</c> does the latter and cannot be driven at all; see
/// <c>XApiServiceTests</c>.
/// </para>
/// <para>
/// One pass of the loop is <c>ProcessQueueAsync</c> then, because <c>lastCleanup</c> starts at
/// <c>DateTime.MinValue</c>, <c>CleanupOldStatementsAsync</c>. Every test here runs exactly one pass by
/// setting <c>ProcessingDelaySeconds</c> to five minutes and stopping the service once the pass has
/// left its mark.
/// </para>
/// <para>
/// <strong>The switch that turns xAPI off is not the switch this service reads.</strong>
/// <c>ExecuteAsync</c> returns early on a blank <c>Username</c> and never looks at
/// <c>XApiOptions.Enabled</c>, which is the flag <c>XApiService.IsConfigured()</c> gates every statement
/// on. So an installation that sets <c>Enabled: false</c> while leaving its LRS credentials in place
/// queues nothing new and goes on flushing whatever was queued before, to an LRS an administrator
/// believes has been disconnected. See
/// <see cref="Start_WithXApiDisabledButStillCredentialed_SendsAnyway"/>.
/// </para>
/// <para>
/// <strong>Any status the service has no opinion about is retried forever.</strong>
/// <c>IsTransientError</c> names five retryable statuses and four permanent ones and calls everything
/// else transient - "conservative approach" - so a typo in <c>XApiOptions.Endpoint</c> answering 404
/// produces a queue that grows without bound, ten futile POSTs every <c>ProcessingDelaySeconds</c>, and
/// a warning per statement per pass. There is no retry ceiling and no dead-letter state. See
/// <see cref="AStatusTheServiceHasNoOpinionAbout_IsRetriedForever"/>.
/// </para>
/// <para>
/// <strong>Cleanup deletes; it never requeues.</strong> A statement stranded in <c>Processing</c> by a
/// host that stopped mid-send is found by the stuck sweep and destroyed, and so is every permanently
/// failed statement once it is older than <c>RetentionDays</c>. Nothing is written anywhere else first,
/// so the row and the reason it was rejected go together. See
/// <see cref="Cleanup_DeletesOldCompletedFailedAndStrandedStatementsAlike"/> and
/// <see cref="Shutdown_MidSend_StrandsTheStatementInProcessing"/>.
/// </para>
/// <para>
/// <strong>Nobody is ever told.</strong> The service holds a logger and nothing else - no hub context,
/// no MSEL status, no counter - so an LRS that has been refusing every statement for a week is visible
/// only to whoever reads the container's logs, and the evidence is deleted on schedule. It is the same
/// shape as <c>JoinService</c> and <c>AddApplicationService</c>, recorded in <c>JoinServiceTests</c>.
/// </para>
/// <para>
/// Per this branch's rule, every test characterizes rather than fixes, and says what fixing it will do
/// to the test.
/// </para>
/// </remarks>
public class XApiBackgroundServiceTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    // ---------------------------------------------------------------------------------------------
    // Whether it runs at all
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// The gate is the username, and it is checked once at startup - so an installation that configures
    /// its LRS after the host is running does not start sending until the host is restarted. Nothing
    /// re-reads the options; <c>XApiOptions</c> is registered as the <c>CurrentValue</c> of an
    /// <c>IOptionsMonitor</c> (<c>Startup.cs:114</c>), so a scope resolves whatever it was at
    /// composition.
    /// </remarks>
    [Fact]
    public async Task Start_WithNoLrsUsername_ProcessesNothing()
    {
        var statement = Statement();
        await Seed(statement);

        await Start(Options(username: string.Empty));
        await WaitForLog(x => x.Message.Contains("not configured"));

        Assert.Empty(Handler.Sent);
        Assert.Equal(XApiQueueStatus.Pending, (await Stored(statement.Id)).Status);
    }

    /// <remarks>
    /// <c>Enabled</c> is read by <c>XApiService.IsConfigured()</c>, which every statement-producing
    /// method consults, and by nothing here. Turning it off therefore stops new statements being queued
    /// and does not stop queued ones being sent. Gating <c>ExecuteAsync</c> on <c>Enabled</c> as well as
    /// on <c>Username</c> turns this red, and is the fix.
    /// </remarks>
    [Fact]
    public async Task Start_WithXApiDisabledButStillCredentialed_SendsAnyway()
    {
        var statement = Statement();
        await Seed(statement);
        Handler.AnswersJson(Statements, "{}");

        var options = Options();
        options.Enabled = false;
        await Start(options);
        await WaitForStatus(statement.Id, XApiQueueStatus.Completed);

        Assert.Equal([Statements], Handler.Paths.ToList());
    }

    // ---------------------------------------------------------------------------------------------
    // The happy path and the wire contract
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AStatementTheLrsAccepts_IsMarkedCompleted()
    {
        var statement = Statement(verb: "initialized");
        await Seed(statement);
        Handler.AnswersJson(Statements, "{}");

        await Start(Options());
        await WaitForStatus(statement.Id, XApiQueueStatus.Completed);

        var sent = Assert.Single(Handler.Sent);
        Assert.Equal(System.Net.Http.HttpMethod.Post, sent.Method);
        Assert.Equal(Statements, sent.Path);
        Assert.Equal("{\"verb\":\"initialized\"}", sent.Body);
    }

    /// <remarks>
    /// Basic authentication and <c>X-Experience-API-Version: 1.0.3</c>, which is xAPI 1.0.3's own
    /// requirement rather than blueprint's choice - an LRS rejects a statement without the version
    /// header. The credentials go out in plain base64, so <c>XApiOptions.Endpoint</c> is one of the
    /// urls in blueprint's configuration where <c>http</c> would hand the LRS password to the network;
    /// nothing validates the scheme. The statement JSON itself is sent verbatim from the column, so
    /// whatever <c>XApiService</c> built is exactly what the LRS is told.
    /// </remarks>
    [Fact]
    public async Task ItSendsTheLrsCredentialsAndTheXApiVersionHeader()
    {
        var statement = Statement();
        await Seed(statement);
        Handler.AnswersJson(Statements, "{}");

        await Start(Options());
        await WaitForStatus(statement.Id, XApiQueueStatus.Completed);

        var sent = Assert.Single(Handler.Sent);
        var expected = Convert.ToBase64String(Encoding.ASCII.GetBytes("lrs-user:lrs-secret"));
        Assert.Equal($"Basic {expected}", sent.Authorization);
        Assert.Equal("1.0.3", sent.Headers["X-Experience-API-Version"]);
    }

    /// <remarks>
    /// A 409 means the LRS already holds a statement with this id, which is what a statement sent twice
    /// looks like - and since nothing here locks the row it is taking, two hosts polling one database is
    /// how that happens. Treating it as success is correct and is the only thing standing in for the
    /// missing lock.
    /// </remarks>
    [Fact]
    public async Task AStatementTheLrsAlreadyHas_IsMarkedCompleted()
    {
        var statement = Statement();
        await Seed(statement);
        Handler.Answers(Statements, HttpStatusCode.Conflict);

        await Start(Options());
        await WaitForStatus(statement.Id, XApiQueueStatus.Completed);

        Assert.Null((await Stored(statement.Id)).ErrorMessage);
    }

    // ---------------------------------------------------------------------------------------------
    // What it makes of a refusal
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task AStatementTheLrsRefusesRetryably_ReturnsToPending(HttpStatusCode status)
    {
        var statement = Statement();
        await Seed(statement);
        Handler.Answers(Statements, status);

        await Start(Options());
        await WaitForStored(statement.Id, x => x.ErrorMessage is not null, "an error message");

        var stored = await Stored(statement.Id);
        Assert.Equal(XApiQueueStatus.Pending, stored.Status);
        Assert.Equal($"[TRANSIENT] HTTP {status}: ", stored.ErrorMessage);
        Assert.Equal(1, stored.RetryCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task AStatementTheLrsRejects_IsMarkedFailed(HttpStatusCode status)
    {
        var statement = Statement();
        await Seed(statement);
        Handler.Answers(Statements, status);

        await Start(Options());
        await WaitForStatus(statement.Id, XApiQueueStatus.Failed);

        Assert.Equal($"[PERMANENT] HTTP {status}: ", (await Stored(statement.Id)).ErrorMessage);
    }

    /// <remarks>
    /// A 404 is not in either list, so it falls through to <c>IsTransientError</c>'s "any other
    /// non-success status: treat as transient" and the statement is queued again - and a 404 is what an
    /// LRS answers for a wrong path, which is the likeliest thing to be wrong about
    /// <c>XApiOptions.Endpoint</c>. So a misconfigured endpoint is indistinguishable from an overloaded
    /// one: every statement is retried forever, the queue only grows, and the sole trace is a warning
    /// per statement per pass. The same goes for a 405, a 410 and a 415. Treating an unrecognized 4xx as
    /// permanent turns this red, and a retry ceiling would make either choice survivable.
    /// </remarks>
    [Fact]
    public async Task AStatusTheServiceHasNoOpinionAbout_IsRetriedForever()
    {
        var statement = Statement();
        await Seed(statement);
        Handler.Answers(Statements, HttpStatusCode.NotFound);

        await Start(Options());
        await WaitForStored(statement.Id, x => x.ErrorMessage is not null, "an error message");

        var stored = await Stored(statement.Id);
        Assert.Equal(XApiQueueStatus.Pending, stored.Status);
        Assert.Equal("[TRANSIENT] HTTP NotFound: ", stored.ErrorMessage);
    }

    /// <remarks>
    /// An unreachable LRS is not a status code at all, and this is the case the whole queue exists for:
    /// blueprint keeps the statement and tries again. The message records the exception's type name and
    /// message, which is the most any of these paths says.
    /// </remarks>
    [Fact]
    public async Task AnLrsThatCannotBeReached_ReturnsTheStatementToPending()
    {
        var statement = Statement();
        await Seed(statement);
        Handler.Throws(Statements);

        await Start(Options());
        await WaitForStored(statement.Id, x => x.ErrorMessage is not null, "an error message");

        var stored = await Stored(statement.Id);
        Assert.Equal(XApiQueueStatus.Pending, stored.Status);
        Assert.StartsWith("[TRANSIENT] HttpRequestException: ", stored.ErrorMessage);
    }

    /// <remarks>
    /// Each statement has its own <c>try</c>, so one refusal costs one statement rather than the batch -
    /// the shape <c>JoinService</c> does not have and <c>IntegrationService.PullIntegrations</c> does.
    /// The loop is sequential, though, so a batch of ten against a slow LRS is ten round trips one after
    /// another inside one pass.
    /// </remarks>
    [Fact]
    public async Task AStatementTheLrsRejects_DoesNotStopTheOnesBehindIt()
    {
        var rejected = Statement(verb: "rejected", queuedAt: Now.AddMinutes(-2));
        var accepted = Statement(verb: "accepted", queuedAt: Now.AddMinutes(-1));
        await Seed(rejected, accepted);
        Handler
            .AnswersJson(Statements, string.Empty, HttpStatusCode.BadRequest, once: true)
            .AnswersJson(Statements, "{}");

        await Start(Options());
        await WaitForStatus(accepted.Id, XApiQueueStatus.Completed);

        Assert.Equal(XApiQueueStatus.Failed, (await Stored(rejected.Id)).Status);
    }

    /// <remarks>
    /// <c>BatchSize</c> is a private <c>const 10</c>, so the drain rate is ten statements per
    /// <c>ProcessingDelaySeconds</c> - 1200 an hour at the shipped thirty-second delay - and nothing in
    /// the configuration can change it. An exercise producing statements faster than that never catches
    /// up, and because a transiently failed statement keeps its place at the head of the queue (see
    /// <c>XApiQueueServiceTests</c>), ten poison statements fill the batch and the drain rate becomes
    /// zero. Making the batch size an option turns this red.
    /// </remarks>
    [Fact]
    public async Task ItSendsTenStatementsAPassAndTheOldestFirst()
    {
        var statements = Enumerable.Range(0, 12)
            .Select(x => Statement(verb: $"verb{x:00}", queuedAt: Now.AddMinutes(x - 20)))
            .ToArray();
        await Seed(statements);
        Handler.AnswersJson(Statements, "{}");

        await Start(Options());
        await WaitForStatus(statements[9].Id, XApiQueueStatus.Completed);

        Assert.Equal(10, Handler.Sent.Count);
        Assert.Equal(
            statements.Take(10).Select(x => $"{{\"verb\":\"{x.Verb}\"}}").ToList(),
            Handler.Sent.Select(x => x.Body).ToList());
        Assert.Equal(XApiQueueStatus.Pending, (await Stored(statements[10].Id)).Status);
    }

    /// <remarks>
    /// <para>
    /// The statement that was in flight when the host was told to stop. The POST is cancelled, the
    /// <c>catch</c> calls <c>MarkFailedAsync</c> with the same cancelled token, and the save that would
    /// have returned the statement to <c>Pending</c> throws instead - so the failure handling is itself
    /// cancelled and the row is left saying <c>Processing</c>, with no error message and nothing to
    /// distinguish it from a statement being sent right now. The outer <c>catch</c> logs
    /// <c>Error processing xAPI queue</c> and the loop exits.
    /// </para>
    /// <para>
    /// Nothing ever returns a <c>Processing</c> row to <c>Pending</c>: the sweep that finds it
    /// <em>deletes</em> it, <c>ProcessingTimeoutMinutes</c> later, so an orderly restart during a send
    /// loses the statement. Passing <c>CancellationToken.None</c> to the two <c>Mark*</c> calls would
    /// turn this red and would leave the statement recoverable - which is the fix, along with requeueing
    /// rather than deleting in <see cref="Cleanup_DeletesOldCompletedFailedAndStrandedStatementsAlike"/>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Shutdown_MidSend_StrandsTheStatementInProcessing()
    {
        var statement = Statement();
        await Seed(statement);
        Handler.Holds().AnswersJson(Statements, "{}");

        var worker = await Start(Options());
        await WaitForRequests(1);
        await worker.StopAsync(CancellationToken.None);

        var stored = await Stored(statement.Id);
        Assert.Equal(XApiQueueStatus.Processing, stored.Status);
        Assert.Null(stored.ErrorMessage);
        Assert.Contains("Error processing xAPI queue", Log.Errors.Select(x => x.Message).ToList());
    }

    // ---------------------------------------------------------------------------------------------
    // Cleanup
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// <para>
    /// Three queries, one delete, and no distinction between the three kinds of row it found. Deleting
    /// old completed statements is housekeeping; the other two are data loss. A permanently failed
    /// statement is the only record that something an exercise did was never accounted for, and its
    /// <c>ErrorMessage</c> is the only explanation - both go a week after the statement was queued, and
    /// retention is measured from queueing rather than from failure, so a statement that failed
    /// yesterday after a long wait goes today. A statement stranded in <c>Processing</c> by a restart
    /// has not been sent at all, and it goes ten minutes later.
    /// </para>
    /// <para>
    /// Requeueing the stranded rows and leaving the failed ones alone - or archiving them anywhere at
    /// all - turns this red.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Cleanup_DeletesOldCompletedFailedAndStrandedStatementsAlike()
    {
        var oldCompleted = Statement(status: XApiQueueStatus.Completed, queuedAt: Now.AddDays(-8));
        var oldFailed = Statement(status: XApiQueueStatus.Failed, queuedAt: Now.AddDays(-8));
        var stranded = Statement(status: XApiQueueStatus.Processing, queuedAt: Now.AddDays(-8));
        stranded.LastAttemptAt = Now.AddHours(-1);
        var recentCompleted = Statement(status: XApiQueueStatus.Completed, queuedAt: Now);
        await Seed(oldCompleted, oldFailed, stranded, recentCompleted);

        await Start(Options());
        await WaitForLog(x => x.Message.Contains("Deleted 3 old xAPI statements"));

        await using var cold = NewContext();
        var remaining = await cold.XApiQueuedStatements.Select(x => x.Id).ToListAsync(Ct);
        Assert.Equal([recentCompleted.Id], remaining);
    }

    /// <remarks>
    /// <c>RetentionDays</c> and <c>ProcessingTimeoutMinutes</c> are both read on every pass, so both are
    /// live configuration rather than the constants <c>BatchSize</c> and <c>CleanupDelayHours</c> are.
    /// A one-day retention takes a two-day-old completed statement and leaves a twelve-hour-old one; a
    /// one-minute processing timeout takes a statement last attempted two minutes ago, which is short
    /// enough to delete a statement the same pass is still sending.
    /// </remarks>
    [Fact]
    public async Task Cleanup_UsesTheConfiguredRetentionAndProcessingTimeout()
    {
        var beyondRetention = Statement(status: XApiQueueStatus.Completed, queuedAt: Now.AddDays(-2));
        var withinRetention = Statement(status: XApiQueueStatus.Completed, queuedAt: Now.AddHours(-12));
        var beyondTimeout = Statement(status: XApiQueueStatus.Processing, queuedAt: Now.AddHours(-12));
        beyondTimeout.LastAttemptAt = Now.AddMinutes(-2);
        await Seed(beyondRetention, withinRetention, beyondTimeout);

        var options = Options();
        options.RetentionDays = 1;
        options.ProcessingTimeoutMinutes = 1;
        await Start(options);
        await WaitForLog(x => x.Message.Contains("Deleted 2 old xAPI statements"));

        await using var cold = NewContext();
        var remaining = await cold.XApiQueuedStatements.Select(x => x.Id).ToListAsync(Ct);
        Assert.Equal([withinRetention.Id], remaining);
    }

    /// <remarks>
    /// The early return is the reason a cleanup pass on a healthy installation is three queries and one
    /// log line rather than a delete of nothing, and the <c>Cleanup summary</c> warning below it is the
    /// only place the three counts are ever reported. It is logged at warning level whenever anything at
    /// all is deleted, including the ordinary case of old completed statements, so it is not a signal
    /// that anything is wrong.
    /// </remarks>
    [Fact]
    public async Task Cleanup_WithNothingOldEnough_SaysSo()
    {
        await Seed(Statement(status: XApiQueueStatus.Completed, queuedAt: Now));

        await Start(Options());
        await WaitForLog(x => x.Message == "No statements to cleanup");

        Assert.DoesNotContain(Log.Entries, x => x.Level == LogLevel.Warning);
    }

    // ---------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------

    /// <summary>The path the LRS's statement resource sits at, under <see cref="Endpoint"/>.</summary>
    private const string Statements = "xapi/statements";

    private const string Endpoint = "https://lrs.test/xapi";

    /// <summary>One base instant, so a seeded date and a cutoff are offsets from the same point.</summary>
    private static readonly DateTime Now = DateTime.UtcNow;

    /// <summary>The transport. The service's POSTs are the only requests anything here makes.</summary>
    private TestHttpHandler Handler { get; } = new();

    /// <summary>
    /// The service's own log, which is the whole of what it reports: it has no hub context, writes to no
    /// table of its own and answers nobody.
    /// </summary>
    private RecordingLogger<XApiBackgroundService> Log { get; } = new();

    private readonly List<XApiBackgroundService> _started = [];
    private readonly List<ServiceProvider> _providers = [];

    /// <summary>
    /// Options an LRS would accept, with a delay long enough that the loop runs exactly one pass and
    /// then waits. Named parameters rather than a record so a test can vary one field by assignment,
    /// which is what <c>Enabled</c> and the two cleanup windows need.
    /// </summary>
    private static XApiOptions Options(string username = "lrs-user") => new()
    {
        Enabled = true,
        Endpoint = Endpoint,
        Username = username,
        Password = "lrs-secret",
        ProcessingDelaySeconds = 300,
    };

    /// <summary>
    /// Builds the service over the test's own database and starts it. <c>BackgroundService.StartAsync</c>
    /// returns as soon as <c>ExecuteAsync</c> reaches its first await, so the caller waits for a signal
    /// rather than for this.
    /// </summary>
    /// <remarks>
    /// The mini-container is what the service resolves per pass: <c>ProcessQueueAsync</c> and
    /// <c>CleanupOldStatementsAsync</c> each open their own scope and each takes a fresh
    /// <c>BlueprintContext</c> with it, exactly as they do in the application. The real
    /// <c>XApiQueueService</c> is registered rather than a substitute, because what these tests assert
    /// is the state of the queue and not the calls made against it.
    /// </remarks>
    private async Task<XApiBackgroundService> Start(XApiOptions options)
    {
        var services = new ServiceCollection();

        services.AddScoped(_ => Session.CreateContext());
        services.AddScoped<IXApiQueueService, XApiQueueService>();
        services.AddSingleton(options);
        services.AddSingleton(Handler.AsFactory());
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        var worker = new XApiBackgroundService(provider, Log);
        _started.Add(worker);

        await worker.StartAsync(CancellationToken.None);

        return worker;
    }

    /// <summary>
    /// A queued statement. <c>Id</c> is assigned rather than left to the column's database default, so a
    /// test can name the row it seeded before saving it.
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

    /// <summary>The row as the database holds it, through a cold change tracker.</summary>
    private async Task<XApiQueuedStatementEntity> Stored(Guid id)
    {
        await using var cold = NewContext();

        return await cold.XApiQueuedStatements.SingleOrDefaultAsync(x => x.Id == id, Ct);
    }

    /// <summary>
    /// Waits until a statement reaches a status, which is how a pass of the loop signals that it has
    /// dealt with one.
    /// </summary>
    /// <remarks>
    /// Only for a status the statement did not start in. A statement that fails transiently ends where it
    /// began - <c>Pending</c> - so waiting for that status returns before the service has touched it, and
    /// the assertion that follows reads the row as it was seeded. Five tests failed that way, all with a
    /// null <c>ErrorMessage</c>; they wait on <see cref="WaitForStored"/> for the error instead.
    /// </remarks>
    private Task WaitForStatus(Guid id, XApiQueueStatus status) =>
        WaitForStored(id, x => x.Status == status, status.ToString());

    /// <summary>Waits until the stored row satisfies <paramref name="done"/>.</summary>
    /// <remarks>
    /// Ten seconds, for the reason <c>IntegrationServiceHarness.WaitFor</c> gives: long enough that a
    /// container under load does not fail a passing test, short enough that a mutation which stops the
    /// service working costs a batch run seconds rather than minutes. The message names what was logged,
    /// because a service that stalled has almost always swallowed something first.
    /// </remarks>
    private async Task WaitForStored(
        Guid id, Func<XApiQueuedStatementEntity, bool> done, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            var stored = await Stored(id);

            if (stored is not null && done(stored))
            {
                return;
            }

            await Task.Delay(50, Ct);
        }

        var last = await Stored(id);

        throw new XunitException(
            $"Statement {id} did not reach {expected} within ten seconds. It was " +
            $"{last?.Status.ToString() ?? "deleted"}, error {last?.ErrorMessage ?? "none"}. " +
            $"Requests made: {string.Join(", ", Handler.Paths)}. Logged: {Logged}");
    }

    /// <summary>Waits until the service logs something matching, which is how cleanup signals.</summary>
    private async Task WaitForLog(Func<RecordingLogger<XApiBackgroundService>.LogEntry, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            if (Log.Entries.Any(done))
            {
                return;
            }

            await Task.Delay(50, Ct);
        }

        throw new XunitException(
            $"The service logged nothing matching within ten seconds. Logged: {Logged}");
    }

    /// <summary>Waits until requests have reached the transport, for a test that stops the service.</summary>
    private async Task WaitForRequests(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            if (Handler.Sent.Count >= count)
            {
                return;
            }

            await Task.Delay(50, Ct);
        }

        throw new XunitException(
            $"Only {Handler.Sent.Count} of {count} requests arrived within ten seconds. " +
            $"Logged: {Logged}");
    }

    private string Logged =>
        Log.Entries.Count == 0
            ? "nothing"
            : string.Join(" | ", Log.Entries.Select(x => $"{x.Level}: {x.Message}"));

    /// <summary>
    /// Stops every service this test started before the database goes away, so a pass in flight cannot
    /// query a dropped database and fail the next test with somebody else's exception.
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        foreach (var worker in _started)
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }

        foreach (var provider in _providers)
        {
            await provider.DisposeAsync();
        }

        await base.DisposeAsync();
    }
}
