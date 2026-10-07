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
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Sdk;

namespace Blueprint.Api.Tests.Services;

/// <summary><c>XApiBackgroundService</c> - the only thing in blueprint that ever sends a statement to a
/// Learning Record Store, and the only thing that ever deletes one.</summary>
public class XApiBackgroundServiceTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    // ---------------------------------------------------------------------------------------------
    // Whether it runs at all
    // ---------------------------------------------------------------------------------------------

    /// <summary>With no LRS username the service processes nothing; the username is read once, at
    /// start.</summary>
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

    /// <summary>Start with x API disabled but still credentialed sends anyway.</summary>
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

    /// <summary>Each statement is sent with basic credentials, <c>X-Experience-API-Version: 1.0.3</c> and the
    /// stored JSON verbatim.</summary>
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

    /// <summary>A 409 from the LRS marks the statement completed.</summary>
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

    /// <summary>A status the service has no opinion about is retried forever.</summary>
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

    /// <summary>An unreachable LRS returns the statement to <c>Pending</c> with the exception
    /// recorded.</summary>
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

    /// <summary>Each statement has its own <c>try</c>, so one refusal does not stop the rest of the
    /// batch.</summary>
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

    /// <summary>A pass sends at most ten statements, oldest first.</summary>
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

    /// <summary>Shutdown mid send strands the statement in processing.</summary>
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
        Assert.Contains("Error processing xAPI queue", Log.At(LogLevel.Error).Select(x => x.Message).ToList());
    }

    // ---------------------------------------------------------------------------------------------
    // Cleanup
    // ---------------------------------------------------------------------------------------------

    /// <summary>Cleanup deletes old completed failed and stranded statements alike.</summary>
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

    /// <summary>Cleanup reads <c>RetentionDays</c> and <c>ProcessingTimeoutMinutes</c> on every pass.</summary>
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

    /// <summary>A cleanup pass with nothing old enough logs that and deletes nothing.</summary>
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
    private SiblingApiHandler Handler { get; } = new();

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
