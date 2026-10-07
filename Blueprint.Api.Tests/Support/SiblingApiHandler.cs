// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

// App extra: the shared StubHttpMessageHandler, refusing anything unarranged, behind a DelegatingHandler
// that adds what only blueprint's concurrency tests need: Yields, Holds and HoldsUntil, and MaxInFlight.
// The route rules (Answers, AnswersOnce, AnswersJson, Throws) and the request record are the shared
// handler's; this class forwards them so the tests keep one object to arrange and read.

namespace Blueprint.Api.Tests.Support;

/// <summary>
/// The four sibling Crucible APIs and the identity provider, at the transport. Everything above it is
/// production code: IdentityModel's discovery and token requests, the four generated API clients and the
/// <c>HttpClient</c> pipeline run for real, and only the socket is replaced.
/// </summary>
/// <remarks>
/// <para>
/// Blueprint's integration code does not use the injected sibling clients: each of the four
/// <c>Integration*Extensions</c> classes builds its own client from <c>IHttpClientFactory</c> through
/// <c>ApiClientsExtensions.GetHttpClient</c>, and <c>ApiClientsExtensions.GetToken</c> resolves the factory
/// the same way. So the factory's primary handler is the one seam that reaches all five.
/// </para>
/// <para>
/// A request nothing arranged throws rather than answering 404 (the shared handler's
/// <c>RefusesUnmatched</c>), because an unexpected request is an arrangement that has drifted from the route
/// the client builds, and a 404 would be swallowed by the error handling several of these tests are about.
/// </para>
/// </remarks>
public sealed class SiblingApiHandler : DelegatingHandler
{
    private readonly StubHttpMessageHandler _stub;
    private bool _yields;
    private TaskCompletionSource _gate;
    private int _gateCount;
    private int _arrived;
    private int _inFlight;
    private int _maxInFlight;

    public SiblingApiHandler()
        : this(new StubHttpMessageHandler().RefusesUnmatched())
    {
    }

    private SiblingApiHandler(StubHttpMessageHandler stub)
        : base(stub)
    {
        _stub = stub;
    }

    /// <summary>Every request that reached the transport, in order, with what it carried.</summary>
    public IReadOnlyList<SentRequest> Sent => _stub.Sent;

    /// <summary>The path of each request, in order.</summary>
    public IEnumerable<string> Paths => _stub.Paths;

    /// <summary>The most requests that were ever in flight at once.</summary>
    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    public SiblingApiHandler Answers(string route, object body) => Forward(() => _stub.Answers(route, body));

    public SiblingApiHandler Answers(string route, HttpStatusCode status) => Forward(() => _stub.Answers(route, status));

    public SiblingApiHandler AnswersOnce(string route, HttpStatusCode status) => Forward(() => _stub.AnswersOnce(route, status));

    public SiblingApiHandler AnswersJson(
        string route, string json, HttpStatusCode status = HttpStatusCode.OK, bool once = false) =>
        Forward(() => _stub.AnswersJson(route, json, status, once));

    public SiblingApiHandler AnswersJson(string route, Func<string> json) => Forward(() => _stub.AnswersJson(route, json));

    public SiblingApiHandler Throws(string route) => Forward(() => _stub.Throws(route));

    /// <summary>
    /// Makes every response complete asynchronously, so work started in parallel really is in flight at
    /// once. Off by default: without it a fan-out written as <c>items.Select(async x => ...)</c> runs one
    /// item at a time, which hides whether code meaning to limit its own concurrency does so.
    /// </summary>
    public SiblingApiHandler Yields()
    {
        _yields = true;

        return this;
    }

    /// <summary>
    /// Holds every response until <paramref name="count"/> requests have arrived, then releases them all
    /// together, so a test asserts concurrency by completing at all. Give the call under test a token that
    /// cancels after a few seconds.
    /// </summary>
    public SiblingApiHandler HoldsUntil(int count)
    {
        _gateCount = count;
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        return this;
    }

    /// <summary>
    /// Holds every response for as long as the caller's token allows, so <see cref="MaxInFlight"/> is the
    /// high-water mark of requests the code under test had open at once.
    /// </summary>
    public SiblingApiHandler Holds() => HoldsUntil(int.MaxValue);

    /// <summary>
    /// The transport as an <see cref="IHttpClientFactory"/>, which is how production reaches it. The clients
    /// do not own the handler, so one handler serves every client a test builds.
    /// </summary>
    public IHttpClientFactory AsFactory() => new StubHttpClientFactory(this);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var inFlight = Interlocked.Increment(ref _inFlight);
        int seen;
        while ((seen = Volatile.Read(ref _maxInFlight)) < inFlight &&
            Interlocked.CompareExchange(ref _maxInFlight, inFlight, seen) != seen)
        {
        }

        try
        {
            // The shared handler records the request before anything is held, as the tests read it.
            var answer = base.SendAsync(request, cancellationToken);

            if (_gate is not null)
            {
                if (Interlocked.Increment(ref _arrived) >= _gateCount)
                {
                    _gate.TrySetResult();
                }

                await _gate.Task.WaitAsync(cancellationToken);
            }
            else if (_yields)
            {
                await Task.Yield();
            }

            return await answer;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private SiblingApiHandler Forward(Func<StubHttpMessageHandler> arrange)
    {
        arrange();

        return this;
    }
}
