// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

using ClientOptions = Blueprint.Api.Infrastructure.Options.ClientOptions;

namespace Blueprint.Api.Tests.Infrastructure;

/// <summary>
/// <c>IntegrationService</c> wired up outside the application, so a test can drive the worker that pushes
/// and pulls a MSEL's integrations.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BlueprintAppFactory"/> removes every <c>IHostedService</c>, so the worker never runs inside
/// the host and cannot be reached through it. This builds one by hand instead: a null logger, a scope
/// factory whose every scope hands out a <c>BlueprintContext</c> over the test's own database, the real
/// <c>IntegrationQueue</c>, <see cref="TestHttpHandler"/> as the transport for all four sibling APIs and
/// the identity provider, and <see cref="HubRecorder"/> for the broadcasts.
/// </para>
/// <para>
/// <strong>The four sibling urls are given distinct path prefixes</strong> - <c>http://siblings.test/player/</c>
/// and so on - because <see cref="TestHttpHandler"/> matches on the path alone, and all four generated
/// clients build <c>api/users</c>. Without a prefix, a rule for one application's user list would answer
/// another's. The trailing slash is what makes a prefix survive; see <c>ApiClientsExtensionsTests</c> for
/// what happens without one. A deployment behind a path-based reverse proxy is configured exactly this way,
/// so nothing here is more artificial than the alternative.
/// </para>
/// <para>
/// <strong>Nothing stops the worker.</strong> <c>Run()</c> is a <c>while (true)</c> over a blocking
/// <c>Take()</c> and <c>StopAsync</c> returns a completed task without cancelling it, so a started harness
/// leaks one blocked thread-pool task for the rest of the run. That is the production shape rather than a
/// harness compromise, and it is why <see cref="StartAsync"/> is separate from construction: a test that
/// only needs <c>CancelPush</c> should not start the loop at all.
/// </para>
/// </remarks>
public sealed class IntegrationServiceHarness
{
    /// <summary>The base url of every sibling API, distinguished by path rather than by host.</summary>
    public const string PlayerApiUrl = "http://siblings.test/player/";
    public const string GalleryApiUrl = "http://siblings.test/gallery/";
    public const string CiteApiUrl = "http://siblings.test/cite/";
    public const string SteamfitterApiUrl = "http://siblings.test/steamfitter/";

    /// <summary>The realm path the identity provider stubs sit under.</summary>
    public const string Realm = "realms/crucible";
    public const string DiscoveryPath = $"{Realm}/.well-known/openid-configuration";
    public const string JwksPath = $"{Realm}/protocol/openid-connect/certs";
    public const string TokenPath = $"{Realm}/protocol/openid-connect/token";

    private readonly TestDatabaseSession _session;

    public IntegrationServiceHarness(
        TestDatabaseSession session,
        TestHttpHandler handler,
        IScenarioEventService scenarioEvents = null)
    {
        _session = session;
        Handler = handler;
        Hub = new HubRecorder();
        Queue = new IntegrationQueue();

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
        services.AddScoped(_ => scenarioEvents ?? Substitute.For<IScenarioEventService>());

        var provider = services.BuildServiceProvider();
        var options = Substitute.For<IOptionsMonitor<ClientOptions>>();

        options.CurrentValue.Returns(new ClientOptions
        {
            PlayerApiUrl = PlayerApiUrl,
            GalleryApiUrl = GalleryApiUrl,
            CiteApiUrl = CiteApiUrl,
            SteamfitterApiUrl = SteamfitterApiUrl,
            PlayerMaxConcurrentRequests = 5,
            GalleryMaxConcurrentRequests = 5,
            CiteMaxConcurrentRequests = 5
        });

        Service = new IntegrationService(
            NullLogger<IntegrationService>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            Queue,
            handler.AsFactory(),
            options,
            Hub);
    }

    /// <summary>The transport every request goes through, and the record of what went out.</summary>
    public TestHttpHandler Handler { get; }

    /// <summary>The broadcasts the worker made.</summary>
    public HubRecorder Hub { get; }

    /// <summary>The real queue the worker takes its work from.</summary>
    public IIntegrationQueue Queue { get; }

    public IIntegrationService Service { get; }

    /// <summary>
    /// Starts the worker's queue loop. Call this only in a test that puts something on the queue - it
    /// occupies a thread-pool task for the rest of the run, because nothing stops it.
    /// </summary>
    public Task StartAsync() => Service.StartAsync(CancellationToken.None);

    /// <summary>Queues a push and starts the worker.</summary>
    public async Task PushAsync(Guid mselId, Guid? playerViewId = null)
    {
        await StartAsync();

        Queue.Add(new IntegrationInformation
        {
            MselId = mselId,
            PlayerViewId = playerViewId,
            IsPush = true
        });
    }

    /// <summary>Queues a pull and starts the worker.</summary>
    public async Task PullAsync(
        Guid mselId, Data.Enumerations.MselItemStatus finalStatus = Data.Enumerations.MselItemStatus.Approved)
    {
        await StartAsync();

        Queue.Add(new IntegrationInformation
        {
            MselId = mselId,
            IsPush = false,
            FinalStatus = finalStatus
        });
    }

    /// <summary>
    /// Waits for the worker to reach a state, which is the only signal it gives: the work runs on a thread
    /// whose delegate is <c>private async void</c>, so there is nothing to await.
    /// </summary>
    /// <remarks>
    /// Ten seconds, which is twenty times what a full push against this transport takes. The deadline is
    /// deliberately tight: during a mutation check most of the tests in a file are expected to time out, and
    /// a generous deadline turns one batch into several minutes of waiting.
    /// </remarks>
    public async Task<MselEntity> WaitFor(
        Guid mselId, Func<MselEntity, bool> done, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            await using var context = _session.CreateContext();
            var msel = await context.Msels.AsNoTracking().SingleOrDefaultAsync(x => x.Id == mselId, ct);

            if (msel is not null && done(msel))
            {
                return msel;
            }

            await Task.Delay(50, ct);
        }

        throw new TimeoutException(
            $"The integration worker did not bring MSEL {mselId} to the expected state within ten " +
            $"seconds. Its last integration status was " +
            $"'{(await LastStatus(mselId, ct)) ?? "<null>"}'. Requests made: " +
            $"{string.Join(", ", Handler.Paths)}.");
    }

    /// <summary>Waits for the worker to finish a push, which is the MSEL reaching <c>Deployed</c>.</summary>
    public Task<MselEntity> WaitForDeployment(Guid mselId, CancellationToken ct = default) =>
        WaitFor(mselId, x => x.Status == Data.Enumerations.MselItemStatus.Deployed, ct);

    /// <summary>Waits for the worker to give up, which it announces by writing an <c>ERROR:</c> status.</summary>
    public Task<MselEntity> WaitForError(Guid mselId, CancellationToken ct = default) =>
        WaitFor(mselId, x => x.IntegrationStatus is not null && x.IntegrationStatus.StartsWith("ERROR"), ct);

    private async Task<string> LastStatus(Guid mselId, CancellationToken ct)
    {
        await using var context = _session.CreateContext();

        return (await context.Msels.AsNoTracking().SingleOrDefaultAsync(x => x.Id == mselId, ct))
            ?.IntegrationStatus;
    }
}
