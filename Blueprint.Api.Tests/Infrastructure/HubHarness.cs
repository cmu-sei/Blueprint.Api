// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;

namespace Blueprint.Api.Tests.Infrastructure;

/// <summary>
/// Drives a <see cref="Hub"/> by direct invocation: stands in for the connection, the group manager and
/// the client proxies, and records every group the hub joined or left and every message it addressed.
/// </summary>
/// <remarks>
/// Ported from vm.api's <c>HubHarness</c>, with two changes. It implements <c>OthersInGroup</c>, which is
/// the only way <c>MainHub</c> ever addresses a client and which vm.api's version does not have; and it
/// records an addressing <em>form</em> alongside the group name, because "everybody in the group" and
/// "everybody in the group but me" are different contracts and <c>MainHub</c> relies on the second one to
/// keep a caller from being told about their own arrival.
/// <para />
/// Group names are the whole contract with a subscribing client, and nothing else can see them: a real
/// <c>HubConnection</c> is told messages, never which group carried them, and
/// <see cref="HubRecorder"/> - the seam for what the 25 event handlers broadcast - sees the names a
/// handler chose but not the ones a caller was joined to. So a test that cares which group a user ends up
/// in has to run the hub through this harness. <see cref="MainHubConnectionTests"/> covers what only a
/// real connection can prove.
/// <para />
/// Hand-written rather than substituted, for the reason recorded on <see cref="HubRecorder"/>:
/// NSubstitute keeps assertion state per thread, and an unconsumed <c>Received()</c> in one test can
/// swallow another's call once classes run in parallel.
/// </remarks>
internal sealed class HubHarness
{
    public const string DefaultConnectionId = "test-connection";

    /// <summary>"everybody in the group", i.e. <c>Clients.Group(name)</c>.</summary>
    public const string ToGroup = "Group";

    /// <summary>"everybody in the group but the caller", i.e. <c>Clients.OthersInGroup(name)</c>.</summary>
    public const string ToOthersInGroup = "OthersInGroup";

    private readonly List<string> _added = [];
    private readonly List<string> _removed = [];
    private readonly List<HubAddressing> _sends = [];

    public HubHarness(Guid userId, string userName = "test-user", string connectionId = DefaultConnectionId)
        : this(
            Claims(userId, userName),
            connectionId)
    {
    }

    /// <summary>
    /// The claims-explicit form, for the cases the convenience constructor cannot express: a principal
    /// with no <c>sub</c> at all, or one whose <c>sub</c> is not a <see cref="Guid"/>.
    /// </summary>
    public HubHarness(IEnumerable<Claim> claims, string connectionId = DefaultConnectionId)
    {
        ConnectionId = connectionId;
        Context = new HarnessCallerContext(
            connectionId, new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")));
        Clients = new HarnessClients(this);
        Groups = new HarnessGroupManager(this);
    }

    public string ConnectionId { get; }

    public HubCallerContext Context { get; }

    public IHubCallerClients Clients { get; }

    public IGroupManager Groups { get; }

    /// <summary>Group names the hub joined this connection to, in order, duplicates included.</summary>
    public IReadOnlyList<string> Added => _added;

    /// <summary>Group names the hub removed this connection from, in order.</summary>
    public IReadOnlyList<string> Removed => _removed;

    /// <summary>Every message the hub addressed, in order.</summary>
    public IReadOnlyList<HubAddressing> Sends => _sends;

    /// <summary>The messages sent under one method name.</summary>
    public IReadOnlyList<HubAddressing> Of(string method) =>
        _sends.Where(x => x.Method == method).ToList();

    public void Clear()
    {
        _added.Clear();
        _removed.Clear();
        _sends.Clear();
    }

    /// <summary>Points <paramref name="hub"/> at this harness and hands it back, for use inline.</summary>
    public T Attach<T>(T hub) where T : Hub
    {
        hub.Context = Context;
        hub.Clients = Clients;
        hub.Groups = Groups;

        return hub;
    }

    /// <summary>
    /// Reads the <c>id</c> and <c>name</c> of one of the anonymous presence payloads
    /// <c>MainHub</c> builds - both as broadcast arguments and as <c>GetPresence</c> list entries.
    /// </summary>
    public static (string Id, string Name) Identity(object payload) =>
        (Property(payload, "id"), Property(payload, "name"));

    private static string Property(object payload, string name) =>
        payload?.GetType().GetProperty(name)?.GetValue(payload)?.ToString();

    private static IEnumerable<Claim> Claims(Guid userId, string userName)
    {
        yield return new Claim("sub", userId.ToString());

        // TestAuthHandler mints "name" only when the request carries X-Test-Name, so a nameless caller
        // is a real state over HTTP as well - which is what reaches MainHub's "Unknown" fallback.
        if (userName is not null)
        {
            yield return new Claim("name", userName);
        }
    }

    private sealed class HarnessGroupManager(HubHarness harness) : IGroupManager
    {
        public Task AddToGroupAsync(
            string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            harness._added.Add(groupName);

            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(
            string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            harness._removed.Add(groupName);

            return Task.CompletedTask;
        }
    }

    /// <remarks>
    /// Only the three members <c>MainHub</c> uses answer; the rest throw, so a hub method that starts
    /// addressing clients some other way fails with a sentence naming what it did rather than silently
    /// recording nothing.
    /// </remarks>
    private sealed class HarnessClients(HubHarness harness) : IHubCallerClients
    {
        public IClientProxy OthersInGroup(string groupName) => Proxy(ToOthersInGroup, groupName);

        public IClientProxy Group(string groupName) => Proxy(ToGroup, groupName);

        public IClientProxy Groups(IReadOnlyList<string> groupNames) =>
            new FanOutProxy(groupNames.Select(x => Proxy(ToGroup, x)).ToList());

        public IClientProxy All => throw Unsupported(nameof(All));

        public IClientProxy Caller => throw Unsupported(nameof(Caller));

        public IClientProxy Others => throw Unsupported(nameof(Others));

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) =>
            throw Unsupported(nameof(AllExcept));

        public IClientProxy Client(string connectionId) => throw Unsupported(nameof(Client));

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) =>
            throw Unsupported(nameof(Clients));

        public IClientProxy GroupExcept(
            string groupName, IReadOnlyList<string> excludedConnectionIds) =>
            throw Unsupported(nameof(GroupExcept));

        public IClientProxy User(string userId) => throw Unsupported(nameof(User));

        public IClientProxy Users(IReadOnlyList<string> userIds) => throw Unsupported(nameof(Users));

        private RecordingProxy Proxy(string form, string groupName) =>
            new(harness, form, groupName);

        private static NotSupportedException Unsupported(string member) =>
            new($"HubHarness does not record IHubCallerClients.{member}, because no MainHub method " +
                "addresses clients that way. Implement it here if one starts to.");
    }

    private sealed class RecordingProxy(HubHarness harness, string form, string group) : IClientProxy
    {
        public Task SendCoreAsync(
            string method, object[] args, CancellationToken cancellationToken = default)
        {
            harness._sends.Add(new HubAddressing(form, group, method, args));

            return Task.CompletedTask;
        }
    }

    /// <remarks>
    /// <c>Groups(IReadOnlyList&lt;string&gt;)</c> is one call that a real hub turns into one message per
    /// group, so this records one <see cref="HubAddressing"/> per name - the same choice
    /// <see cref="HubRecorder"/> makes, and what keeps the two overloads indistinguishable to a test.
    /// </remarks>
    private sealed class FanOutProxy(IReadOnlyList<RecordingProxy> proxies) : IClientProxy
    {
        public async Task SendCoreAsync(
            string method, object[] args, CancellationToken cancellationToken = default)
        {
            foreach (var proxy in proxies)
            {
                await proxy.SendCoreAsync(method, args, cancellationToken);
            }
        }
    }

    private sealed class HarnessCallerContext(string connectionId, ClaimsPrincipal user)
        : HubCallerContext
    {
        public override string ConnectionId => connectionId;

        public override string UserIdentifier =>
            user.Claims.FirstOrDefault(x => x.Type == "sub")?.Value;

        public override ClaimsPrincipal User => user;

        public override IDictionary<object, object> Items { get; } = new Dictionary<object, object>();

        public override IFeatureCollection Features =>
            throw new NotSupportedException(
                "HubHarness has no connection features. A hub method reading them has to be driven " +
                "over a real HubConnection instead - see MainHubConnectionTests.");

        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort() =>
            throw new NotSupportedException(
                "HubHarness cannot abort a connection there is none of. A hub method calling Abort " +
                "has to be driven over a real HubConnection instead - see MainHubConnectionTests.");
    }
}

/// <summary>
/// One message a hub addressed: the form it used, the group it named, the method and the arguments.
/// </summary>
/// <remarks>
/// Deliberately not <see cref="HubSend"/>, which records what an <c>IHubContext</c> broadcast. That seam
/// has only one addressing form, and this one has three - so folding them together would lose the
/// distinction between <c>Group</c> and <c>OthersInGroup</c> that half of <c>MainHub</c>'s presence
/// behaviour turns on.
/// </remarks>
internal sealed record HubAddressing(string Form, string Group, string Method, object[] Args)
{
    public object Payload => Args.Length > 0 ? Args[0] : null;
}
