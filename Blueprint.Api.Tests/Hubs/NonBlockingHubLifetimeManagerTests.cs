// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Blueprint.Api.Tests.Hubs;

/// <summary>
/// <c>NonBlockingHubLifetimeManager</c>: a send returns without waiting for the clients it goes to, and
/// everything else is passed straight to SignalR's own manager.
/// </summary>
/// <remarks>
/// The manager is driven directly, over a stand-in for SignalR's manager whose sends can be made to hang
/// the way a send to a client with a full replay buffer does. What the stand-in cannot show, a real
/// client that stops acknowledging, was measured against the running API.
/// </remarks>
public class NonBlockingHubLifetimeManagerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void ASend_IsStartedAtOnce_AndReturnsBeforeAnyClientHasAcceptedIt()
    {
        var inner = new StandInLifetimeManager { Send = (_, ct) => Task.Delay(Timeout.Infinite, ct) };
        var manager = Create(inner, new StandInLifetime());

        var send = manager.SendGroupAsync("frozen", "UserCreated", ["payload"], Ct);

        Assert.True(send.IsCompletedSuccessfully);
        Assert.Equal([new SendRecord("group", "frozen", "UserCreated")], inner.Started.ToList());
    }

    [Fact]
    public async Task TheCallersToken_IsNotPassedOn_TheApplicationsStoppingTokenIs()
    {
        var lifetime = new StandInLifetime();
        var inner = new StandInLifetimeManager();
        var manager = Create(inner, lifetime);
        using var request = new CancellationTokenSource();
        request.Cancel();

        await manager.SendGroupAsync("msel", "MselDeleted", [], request.Token);

        var token = Assert.Single(inner.Tokens);
        Assert.Equal(lifetime.ApplicationStopping, token);
        Assert.False(token.IsCancellationRequested);
    }

    [Fact]
    public async Task Sends_StartInTheOrderTheyWereMade_EvenWhileEarlierOnesAreStillWaiting()
    {
        // Every third send hangs, as one to a client with a full buffer does. SignalR writes to each
        // connection in the order sends reach it, so start order is what keeps a client's messages in
        // order.
        var inner = new StandInLifetimeManager
        {
            Send = (record, ct) => int.Parse(record.Target) % 3 == 0 ? Task.Delay(Timeout.Infinite, ct) : Task.CompletedTask,
        };
        var manager = Create(inner, new StandInLifetime());
        var expected = Enumerable.Range(0, 60).Select(i => i.ToString()).ToList();

        foreach (var group in expected)
        {
            await manager.SendGroupAsync(group, "ScenarioEventUpdated", [], Ct);
        }

        Assert.Equal(expected, inner.Started.Select(x => x.Target).ToList());
    }

    [Fact]
    public async Task ASendThatFails_IsLogged_NotThrownAtTheCaller()
    {
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new StandInLifetimeManager { Send = (_, _) => failed.Task };
        var logger = new CountingLogger();
        var manager = new NonBlockingHubLifetimeManager<TestHub>(inner, new StandInLifetime(), logger);

        await manager.SendGroupAsync("msel", "MselUpdated", [], Ct);
        failed.SetException(new InvalidOperationException("transport gone"));

        await WaitUntil(() => logger.Warnings > 0);
    }

    [Fact]
    public async Task ASendCancelledByShutdown_IsNotLogged()
    {
        var lifetime = new StandInLifetime();
        var inner = new StandInLifetimeManager { Send = (_, ct) => Task.Delay(Timeout.Infinite, ct) };
        var logger = new CountingLogger();
        var manager = new NonBlockingHubLifetimeManager<TestHub>(inner, lifetime, logger);

        await manager.SendGroupAsync("msel", "MselUpdated", [], Ct);
        lifetime.Stop();

        await Task.Delay(100, Ct);
        Assert.Equal(0, logger.Warnings);
    }

    [Fact]
    public async Task ArgumentErrors_StillReachTheCaller()
    {
        var inner = new StandInLifetimeManager { Send = (_, _) => throw new ArgumentNullException("groupName") };
        var manager = Create(inner, new StandInLifetime());

        await Assert.ThrowsAsync<ArgumentNullException>(() => manager.SendGroupAsync(null, "M", [], Ct));
    }

    [Fact]
    public async Task EveryKindOfSend_GoesToSignalR()
    {
        var inner = new StandInLifetimeManager();
        var manager = Create(inner, new StandInLifetime());

        await manager.SendAllAsync("M", [], Ct);
        await manager.SendAllExceptAsync("M", [], ["x"], Ct);
        await manager.SendConnectionAsync("c1", "M", [], Ct);
        await manager.SendConnectionsAsync(["c1", "c2"], "M", [], Ct);
        await manager.SendGroupAsync("g", "M", [], Ct);
        await manager.SendGroupsAsync(["a", "b"], "M", [], Ct);
        await manager.SendGroupExceptAsync("g", "M", [], ["x"], Ct);
        await manager.SendUserAsync("u1", "M", [], Ct);
        await manager.SendUsersAsync(["u1", "u2"], "M", [], Ct);

        Assert.Equal(
            [
                new("all", "", "M"),
                new("all-except", "x", "M"),
                new("connection", "c1", "M"),
                new("connections", "c1,c2", "M"),
                new("group", "g", "M"),
                new("groups", "a,b", "M"),
                new("group-except", "g/x", "M"),
                new("user", "u1", "M"),
                new("users", "u1,u2", "M"),
            ],
            inner.Started.ToList());
    }

    [Fact]
    public async Task GroupMembership_GoesStraightToSignalR()
    {
        var inner = new StandInLifetimeManager();
        var manager = Create(inner, new StandInLifetime());

        await manager.AddToGroupAsync("c1", "msel", Ct);
        await manager.RemoveFromGroupAsync("c1", "msel", Ct);

        Assert.Equal(["add c1 msel", "remove c1 msel"], inner.Membership.ToList());
    }

    /// <remarks>
    /// A member SignalR adds as abstract breaks the build. One added as virtual, with a default body, would
    /// compile and silently skip the real manager; that is how the three client-result members are
    /// declared today. This fails instead.
    /// </remarks>
    [Fact]
    public void EveryOverridableMember_IsForwarded()
    {
        var wrapper = typeof(NonBlockingHubLifetimeManager<MainHub>);
        var notForwarded = wrapper.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.IsVirtual)
            .Where(m => m.GetBaseDefinition().DeclaringType is { IsGenericType: true } t
                        && t.GetGenericTypeDefinition() == typeof(HubLifetimeManager<>))
            .Where(m => m.DeclaringType != wrapper)
            .Select(m => m.Name);

        Assert.Empty(notForwarded);
    }

    // ---------------------------------------------------------------------------------------------

    private static NonBlockingHubLifetimeManager<TestHub> Create(StandInLifetimeManager inner, StandInLifetime lifetime) =>
        new(inner, lifetime, NullLogger<NonBlockingHubLifetimeManager<TestHub>>.Instance);

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(Patience);
        while (!condition())
        {
            await Task.Delay(10, deadline.Token);
        }
    }

    public sealed class TestHub : Hub;

    private sealed record SendRecord(string Kind, string Target, string Method);

    private sealed class StandInLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => Stop();

        public void Stop() => _stopping.Cancel();
    }

    private sealed class CountingLogger : Microsoft.Extensions.Logging.ILogger<NonBlockingHubLifetimeManager<TestHub>>
    {
        private int _warnings;

        public int Warnings => Volatile.Read(ref _warnings);

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception exception,
            Func<TState, Exception, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                Interlocked.Increment(ref _warnings);
            }
        }
    }

    /// <summary>
    /// Stands in for SignalR's manager. Records each send as it starts, then answers it with
    /// <see cref="Send"/>, which by default completes at once.
    /// </summary>
    private sealed class StandInLifetimeManager : HubLifetimeManager<TestHub>
    {
        private readonly ConcurrentQueue<SendRecord> _started = new();

        public Func<SendRecord, CancellationToken, Task> Send { get; init; } = (_, _) => Task.CompletedTask;

        public IReadOnlyCollection<SendRecord> Started => _started;

        public ConcurrentQueue<CancellationToken> Tokens { get; } = new();

        public ConcurrentQueue<string> Membership { get; } = new();

        public override Task OnConnectedAsync(HubConnectionContext connection) => Task.CompletedTask;

        public override Task OnDisconnectedAsync(HubConnectionContext connection) => Task.CompletedTask;

        public override Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            Membership.Enqueue($"add {connectionId} {groupName}");
            return Task.CompletedTask;
        }

        public override Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            Membership.Enqueue($"remove {connectionId} {groupName}");
            return Task.CompletedTask;
        }

        public override Task SendAllAsync(string methodName, object[] args, CancellationToken cancellationToken = default) =>
            Record(new("all", "", methodName), cancellationToken);

        public override Task SendAllExceptAsync(string methodName, object[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default) =>
            Record(new("all-except", string.Join(',', excludedConnectionIds), methodName), cancellationToken);

        public override Task SendConnectionAsync(string connectionId, string methodName, object[] args, CancellationToken cancellationToken = default) =>
            Record(new("connection", connectionId, methodName), cancellationToken);

        public override Task SendConnectionsAsync(IReadOnlyList<string> connectionIds, string methodName, object[] args, CancellationToken cancellationToken = default) =>
            Record(new("connections", string.Join(',', connectionIds), methodName), cancellationToken);

        public override Task SendGroupAsync(string groupName, string methodName, object[] args, CancellationToken cancellationToken = default) =>
            Record(new("group", groupName, methodName), cancellationToken);

        public override Task SendGroupsAsync(IReadOnlyList<string> groupNames, string methodName, object[] args, CancellationToken cancellationToken = default) =>
            Record(new("groups", string.Join(',', groupNames), methodName), cancellationToken);

        public override Task SendGroupExceptAsync(string groupName, string methodName, object[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default) =>
            Record(new("group-except", $"{groupName}/{string.Join(',', excludedConnectionIds)}", methodName), cancellationToken);

        public override Task SendUserAsync(string userId, string methodName, object[] args, CancellationToken cancellationToken = default) =>
            Record(new("user", userId, methodName), cancellationToken);

        public override Task SendUsersAsync(IReadOnlyList<string> userIds, string methodName, object[] args, CancellationToken cancellationToken = default) =>
            Record(new("users", string.Join(',', userIds), methodName), cancellationToken);

        private Task Record(SendRecord record, CancellationToken cancellationToken)
        {
            _started.Enqueue(record);
            Tokens.Enqueue(cancellationToken);
            return Send(record, cancellationToken);
        }
    }
}
