// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

using ClientOptions = Blueprint.Api.Infrastructure.Options.ClientOptions;

namespace Blueprint.Api.Tests.Infrastructure;

/// <summary>
/// <c>JoinService</c> and <c>AddApplicationService</c> wired up outside the application, so a test can
/// drive the two smaller queue workers.
/// </summary>
/// <remarks>
/// <para>
/// The same arrangement as <see cref="IntegrationServiceHarness"/> - the host removes every
/// <c>IHostedService</c>, so a worker cannot be reached through it - with one difference that shapes
/// every test built on this. Neither worker writes to the database, broadcasts to the hub or returns
/// anything to anybody: a join is three POSTs to three sibling APIs and nothing else. So there is no
/// row to poll for completion, and the signals are the requests that arrived
/// (<see cref="WaitForRequests"/>) and the line the outermost <c>catch</c> logged
/// (<see cref="WaitForLog"/>). That is also the finding - see <c>JoinServiceTests</c>.
/// </para>
/// <para>
/// Both workers are constructed, because they differ only in which queue they read; starting one costs
/// nothing for the other, and <see cref="StartJoinAsync"/> is separate from construction for the same
/// reason it is in the other harness. Each <c>Run()</c> is a <c>while (true)</c> over a blocking
/// <c>Take()</c> and each <c>StopAsync</c> cancels nothing, so a started worker leaks one blocked
/// thread-pool task for the rest of the run.
/// </para>
/// <para>
/// The sibling urls carry the same distinct path prefixes as <see cref="IntegrationServiceHarness"/>,
/// and for the same reason: <see cref="TestHttpHandler"/> matches on the path alone and all four
/// generated clients build <c>api/users</c>. The constants are reused rather than restated.
/// </para>
/// </remarks>
public sealed class QueueWorkerHarness
{
    public QueueWorkerHarness(TestDatabaseSession session, TestHttpHandler handler)
    {
        Handler = handler;
        Hub = new HubRecorder();
        JoinQueue = new JoinQueue();
        AddApplicationQueue = new AddApplicationQueue();
        JoinLog = new RecordingLogger<JoinService>();
        AddApplicationLog = new RecordingLogger<AddApplicationService>();

        var services = new ServiceCollection();

        services.AddScoped(_ => session.CreateContext());
        services.AddSingleton(handler.AsFactory());
        services.AddSingleton(new ResourceOwnerAuthorizationOptions
        {
            Authority = "http://localhost:8080/realms/crucible",
            ClientId = "blueprint-admin",
            UserName = "blueprint-admin",
            Password = string.Empty,
            Scope = "player player-vm cite gallery steamfitter"
        });

        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var options = Substitute.For<IOptionsMonitor<ClientOptions>>();

        options.CurrentValue.Returns(new ClientOptions
        {
            PlayerApiUrl = IntegrationServiceHarness.PlayerApiUrl,
            GalleryApiUrl = IntegrationServiceHarness.GalleryApiUrl,
            CiteApiUrl = IntegrationServiceHarness.CiteApiUrl,
            SteamfitterApiUrl = IntegrationServiceHarness.SteamfitterApiUrl
        });

        Join = new JoinService(JoinLog, scopes, JoinQueue, Hub, handler.AsFactory(), options);
        AddApplication = new AddApplicationService(
            AddApplicationLog, scopes, AddApplicationQueue, Hub, handler.AsFactory(), options);
    }

    /// <summary>The transport every request goes through, and the record of what went out.</summary>
    public TestHttpHandler Handler { get; }

    /// <summary>
    /// The hub context both workers are given. Neither sends anything through it, which is what
    /// <c>JoinServiceTests</c> pins: the user who asked to join is never told whether they did.
    /// </summary>
    public HubRecorder Hub { get; }

    public IJoinQueue JoinQueue { get; }

    public IAddApplicationQueue AddApplicationQueue { get; }

    /// <summary>The only place a failed join leaves a trace.</summary>
    public RecordingLogger<JoinService> JoinLog { get; }

    /// <summary>The only place a failed application add leaves a trace.</summary>
    public RecordingLogger<AddApplicationService> AddApplicationLog { get; }

    public IJoinService Join { get; }

    public IAddApplicationService AddApplication { get; }

    /// <summary>Queues a join and starts the worker.</summary>
    public async Task JoinAsync(JoinInformation information)
    {
        await Join.StartAsync(CancellationToken.None);

        JoinQueue.Add(information);
    }

    /// <summary>Queues an application add and starts the worker.</summary>
    public async Task AddApplicationAsync(AddApplicationInformation information)
    {
        await AddApplication.StartAsync(CancellationToken.None);

        AddApplicationQueue.Add(information);
    }

    /// <summary>
    /// Waits until <paramref name="count"/> requests have reached the transport, and returns the ones
    /// that are not the identity provider's three.
    /// </summary>
    /// <remarks>
    /// <paramref name="count"/> counts <em>every</em> request including discovery, JWKS and the token,
    /// because that is what the handler can see as it happens; the return value drops them, because no
    /// test is about them. Ten seconds, for the reason
    /// <see cref="IntegrationServiceHarness.WaitFor"/> gives.
    /// </remarks>
    public async Task<IReadOnlyList<TestHttpHandler.SentRequest>> WaitForRequests(
        int count, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            if (Handler.Sent.Count >= count)
            {
                return Siblings;
            }

            await Task.Delay(50, ct);
        }

        throw new TimeoutException(
            $"Only {Handler.Sent.Count} of {count} requests arrived within ten seconds. " +
            $"Requests made: {string.Join(", ", Handler.Paths)}. Logged: {Logged}");
    }

    /// <summary>
    /// Both workers' log lines, for a timeout message. A worker that stalled almost always swallowed
    /// something first - a route nothing stubbed is an <c>InvalidOperationException</c> the outermost
    /// <c>catch</c> absorbs - and without this the failure names a count and nothing else.
    /// </summary>
    private string Logged =>
        string.Join(
            " | ",
            JoinLog.Errors.Select(x => $"join: {x.Message} - {x.Exception?.Message}")
                .Concat(AddApplicationLog.Errors
                    .Select(x => $"application: {x.Message} - {x.Exception?.Message}")));

    /// <summary>Waits for a log line matching <paramref name="done"/>, which is how a failure surfaces.</summary>
    public async Task<RecordingLogger<JoinService>.LogEntry> WaitForLog(
        Func<RecordingLogger<JoinService>.LogEntry, bool> done, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            var entry = JoinLog.Entries.FirstOrDefault(done);

            if (entry is not null)
            {
                return entry;
            }

            await Task.Delay(50, ct);
        }

        throw new TimeoutException(
            $"The join worker logged nothing matching within ten seconds. It logged: " +
            $"{string.Join(" | ", JoinLog.Entries.Select(x => x.Message))}.");
    }

    /// <summary>The same, for the other worker's log.</summary>
    public async Task<RecordingLogger<AddApplicationService>.LogEntry> WaitForApplicationLog(
        Func<RecordingLogger<AddApplicationService>.LogEntry, bool> done, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            var entry = AddApplicationLog.Entries.FirstOrDefault(done);

            if (entry is not null)
            {
                return entry;
            }

            await Task.Delay(50, ct);
        }

        throw new TimeoutException(
            $"The add-application worker logged nothing matching within ten seconds. It logged: " +
            $"{string.Join(" | ", AddApplicationLog.Entries.Select(x => x.Message))}.");
    }

    /// <summary>The requests that went to a sibling API, with the identity provider's dropped.</summary>
    public IReadOnlyList<TestHttpHandler.SentRequest> Siblings =>
        [.. Handler.Sent.Where(x => !x.Path.StartsWith(IntegrationServiceHarness.Realm, StringComparison.Ordinal))];

    /// <summary>The paths of <see cref="Siblings"/>, which is the usual assertion.</summary>
    public IReadOnlyList<string> SiblingPaths => [.. Siblings.Select(x => x.Path)];

    /// <summary>
    /// The identity provider, answering the three requests a token costs. Every test needs these,
    /// because both workers fetch a token before doing anything.
    /// </summary>
    public static TestHttpHandler IdentityProvider(TestHttpHandler handler) => handler
        .AnswersJson(IntegrationServiceHarness.DiscoveryPath, """
            {
              "issuer": "http://localhost:8080/realms/crucible",
              "authorization_endpoint": "http://localhost:8080/realms/crucible/protocol/openid-connect/auth",
              "token_endpoint": "http://localhost:8080/realms/crucible/protocol/openid-connect/token",
              "jwks_uri": "http://localhost:8080/realms/crucible/protocol/openid-connect/certs",
              "response_types_supported": ["code"],
              "subject_types_supported": ["public"],
              "id_token_signing_alg_values_supported": ["RS256"]
            }
            """)
        .AnswersJson(IntegrationServiceHarness.JwksPath, """{"keys":[]}""")
        .AnswersJson(IntegrationServiceHarness.TokenPath,
            """{"access_token":"abc123","token_type":"Bearer","expires_in":300}""");
}
