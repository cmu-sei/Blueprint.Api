// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Blueprint.Api.Tests.Infrastructure;

/// <summary>
/// One message the application broadcast: the group it was addressed to, the method name, and the
/// arguments.
/// </summary>
public sealed record HubSend(string Group, string Method, object[] Args)
{
    /// <summary>The first argument, which for every blueprint notification is the payload.</summary>
    public object Payload => Args.Length > 0 ? Args[0] : null;
}

/// <summary>
/// Records what the application broadcasts through <see cref="IHubContext{MainHub}"/>. In blueprint that
/// is the single seam for every notification: a save publishes an entity event, the
/// <c>EntityEventInterceptor</c> hands it to MediatR, and one of the 25 handlers under
/// <c>Infrastructure/EventHandlers</c> sends it to a group.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than an NSubstitute double, which is where this parts company with vm.api's
/// <c>HubContextHarness</c>. NSubstitute keeps its call and assertion state per thread, and
/// <c>TestServer</c> serves a request on the same pool the tests run on: a pending <c>Received()</c> in
/// one test can consume a call made by another. That flake arrives with load rather than with a change,
/// which is the worst way for it to arrive. Writing the recorder out also sidesteps the
/// "Could not find a call to return from" problem vm.api documents, where re-stubbing one group name
/// from a test consumes the pending-call state inside the stubbing lambda.
/// </para>
/// <para>
/// Only <see cref="IHubClients.Group"/> and <see cref="IHubClients{T}.All"/> are implemented. The other
/// seven members throw and say so: a handler that starts addressing clients by connection or by user
/// should fail loudly here rather than quietly record nothing.
/// </para>
/// </remarks>
public sealed class HubRecorder : IHubContext<MainHub>
{
    /// <summary>
    /// The recorded group name for a send to <c>Clients.All</c>. Not a valid group name - every real one
    /// is a guid or one of <c>MainHub</c>'s four admin constants - so it cannot collide with one a
    /// caller computes.
    /// </summary>
    public const string Everyone = "<all clients>";

    private readonly ConcurrentQueue<HubSend> _sends = new();

    public HubRecorder() => Clients = new RecordingClients(this);

    public IHubClients Clients { get; }

    public IGroupManager Groups => throw new NotSupportedException(
        "Nothing outside a hub adds connections to groups - MainHub.Join does it from inside, where it " +
        "has Context.ConnectionId. A caller reaching for this is doing something new; test it against a " +
        "real connection instead.");

    /// <summary>Every message sent, in order.</summary>
    public IReadOnlyList<HubSend> Sends => [.. _sends];

    /// <summary>The messages sent as <paramref name="method"/>, in order.</summary>
    public IReadOnlyList<HubSend> Of(string method) => [.. _sends.Where(x => x.Method == method)];

    /// <summary>
    /// Every group name that received <paramref name="method"/>, in the order first addressed.
    /// </summary>
    /// <remarks>
    /// Deduplicated, because a handler sending the same message to one group twice is a different
    /// question from which groups were told - <see cref="Sends"/> is where that one is asked.
    /// </remarks>
    public IReadOnlyList<string> Recipients(string method) =>
        [.. Of(method).Select(x => x.Group).Distinct()];

    /// <summary>
    /// Forgets everything recorded so far. <see cref="ApiTestBase"/> calls it per test, because the host
    /// - and so this recorder - is shared by every test in the class.
    /// </summary>
    public void Clear() => _sends.Clear();

    private void Record(string group, string method, object[] args) =>
        _sends.Enqueue(new HubSend(group, method, args));

    private sealed class RecordingClients(HubRecorder recorder) : IHubClients
    {
        public IClientProxy All => new RecordingProxy(recorder, Everyone);

        public IClientProxy Group(string groupName) => new RecordingProxy(recorder, groupName);

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Unsupported();

        public IClientProxy Client(string connectionId) => Unsupported();

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Unsupported();

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Unsupported();

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) =>
            Unsupported();

        public IClientProxy User(string userId) => Unsupported();

        public IClientProxy Users(IReadOnlyList<string> userIds) => Unsupported();

        private static IClientProxy Unsupported() => throw new NotSupportedException(
            "HubRecorder records sends to a named group and to All, which is everything blueprint's " +
            "event handlers do. Addressing clients any other way needs a recorder that models it.");
    }

    private sealed class RecordingProxy(HubRecorder recorder, string group) : IClientProxy
    {
        public Task SendCoreAsync(
            string method, object[] args, CancellationToken cancellationToken = default)
        {
            recorder.Record(group, method, args);
            return Task.CompletedTask;
        }
    }
}
