// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Blueprint.Api.Tests.Infrastructure;

/// <summary>
/// A substituted transport. Everything above it is production code: IdentityModel's discovery and token
/// requests, the four generated API clients, the <c>HttpClient</c> pipeline and whatever
/// <c>DelegatingHandler</c>s are wrapped around it all run for real, and only the socket is replaced.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam the rest of the suite reaches by substituting <c>ICiteApiClient</c> and its three
/// siblings outright. Those substitutes are right for a test about a service that consults CITE; this is
/// what is left over. Blueprint's integration code does not use the injected clients at all - every one of
/// the four <c>Integration*Extensions</c> classes builds its own client from <c>IHttpClientFactory</c>
/// through <c>ApiClientsExtensions.GetHttpClient</c>, and <c>ApiClientsExtensions.GetToken</c> resolves the
/// factory the same way. So the factory's primary handler is the one seam that reaches all five, and the
/// only place a test can see the request that went out and what was made of what came back.
/// </para>
/// <para>
/// A request nothing stubbed throws rather than answering 404, because in this suite an unexpected request
/// is an arrangement that has drifted from the route the client builds - and a 404 would be swallowed by
/// the very error handling several of these tests are about. Blueprint swallows a great deal: every
/// integration step is wrapped in its own <c>try</c>/<c>catch</c> that logs and carries on.
/// </para>
/// <para>
/// Every builder takes a path pattern, which may be prefixed with a method -
/// <c>"POST connect/token"</c> - to answer only that method. Without a prefix a rule answers whatever
/// method arrives, which is what the generated clients' one-route-per-path calls want. A pattern ending in
/// <c>*</c> matches on prefix, for a route whose id an arrangement does not care about.
/// </para>
/// </remarks>
public sealed class TestHttpHandler : HttpMessageHandler
{
    private readonly List<Rule> _rules = [];
    private readonly Lock _sent = new();
    private bool _yields;
    private TaskCompletionSource _gate;
    private int _gateCount;
    private int _arrived;
    private int _inFlight;

    /// <summary>Every request that reached the transport, in order, with what it carried.</summary>
    /// <remarks>
    /// Appended under a lock. Several of the things this handler stands under fan out with
    /// <c>Task.WhenAll</c>, and a <c>List&lt;T&gt;</c> torn by two concurrent adds would surface as an
    /// assertion failure somewhere else entirely.
    /// </remarks>
    public List<SentRequest> Sent { get; } = [];

    /// <summary>The path of each request, in order. The usual assertion about how often a token is fetched.</summary>
    public IEnumerable<string> Paths => Sent.Select(x => x.Path);

    /// <summary>200 with <paramref name="body"/> serialized as the client's DTOs declare themselves.</summary>
    /// <remarks>
    /// System.Text.Json, and deliberately with no options: the four generated client libraries carry
    /// <c>[JsonPropertyName]</c> on every property, so serializing one produces the names its own
    /// deserializer looks for. Hand-written JSON here would be a second guess at that contract.
    /// </remarks>
    public TestHttpHandler Answers(string path, object body) =>
        Add(path, HttpStatusCode.OK, JsonSerializer.Serialize(body), once: false);

    /// <summary>A body written out as it arrives on the wire, with the status the sender gave it.</summary>
    /// <remarks>
    /// <para>
    /// For the identity provider, whose discovery document and token response are not types this
    /// repository has: what IdentityModel parses is the OAuth JSON, so that is what a test of it should be
    /// handing over.
    /// </para>
    /// <para>
    /// Rules are matched in the order they were added and a rule with <paramref name="once"/> false answers
    /// <em>every</em> request for its path, so registering two of them does not make a queue - the first
    /// answers both. To give two calls to one route different answers, mark the earlier one
    /// <paramref name="once"/>.
    /// </para>
    /// </remarks>
    public TestHttpHandler AnswersJson(
        string path, string json, HttpStatusCode status = HttpStatusCode.OK, bool once = false) =>
        Add(path, status, json, once);

    /// <summary>
    /// A body computed when the request arrives rather than when the rule is written, for a resource whose
    /// content a test goes on to change.
    /// </summary>
    public TestHttpHandler AnswersJson(string path, Func<string> json)
    {
        _rules.Add(new Rule(path, HttpStatusCode.OK, json, once: false));

        return this;
    }

    /// <summary>
    /// Makes every response complete asynchronously, so work started in parallel really is in flight at
    /// once.
    /// </summary>
    /// <remarks>
    /// Off by default, and worth understanding before reaching for it. A real socket never answers
    /// synchronously, but this handler does: the whole of <see cref="SendAsync"/> can run to completion
    /// without yielding, so a fan-out written as <c>items.Select(async x => ...)</c> executes one item at a
    /// time and looks orderly. That hides any defect in code meaning to limit its own concurrency - which is
    /// exactly what blueprint's <c>batchSize</c> parameters claim to do - so a test about concurrency has to
    /// ask for this and a test about anything else should not, since it is the cheaper and more predictable
    /// arrangement.
    /// </remarks>
    public TestHttpHandler Yields()
    {
        _yields = true;

        return this;
    }

    /// <summary>
    /// Holds every response until <paramref name="count"/> requests have arrived, then releases them all
    /// together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The deterministic way to ask whether code that claims to limit its own concurrency does. If it does,
    /// fewer than <paramref name="count"/> requests are ever in flight, the gate never opens and the work
    /// never finishes - so a test built on this asserts concurrency by <em>completing at all</em>, which no
    /// interleaving can fake. <see cref="Yields"/> alone is not enough: it makes responses asynchronous but
    /// does not make the requests overlap, so the order they are recorded in is up to the thread pool.
    /// </para>
    /// <para>
    /// Give the call under test a token that cancels after a few seconds. Without one, code that batches
    /// correctly hangs here rather than failing, which is a worse way to learn the same thing.
    /// </para>
    /// </remarks>
    public TestHttpHandler HoldsUntil(int count)
    {
        _gateCount = count;
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        return this;
    }

    /// <summary>
    /// Holds every response for as long as the caller's token allows, so <see cref="MaxInFlight"/> ends up
    /// being the high-water mark of requests the code under test was willing to have open at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the primitive for asking "how many at a time", where <see cref="HoldsUntil"/> answers "were
    /// there ever this many together". The difference matters: once <see cref="HoldsUntil"/>'s gate opens,
    /// every later request completes freely, so a caller that starts four requests two at a time and one
    /// that starts all four look identical after the second arrives. With nothing ever released, they do
    /// not.
    /// </para>
    /// <para>
    /// The call under test never finishes, so a test using this expects an
    /// <c>OperationCanceledException</c> and passes a token that cancels after a second or two. That is not
    /// a shortcoming - the assertion is about the requests, and a batched implementation would deadlock on a
    /// real server too.
    /// </para>
    /// </remarks>
    public TestHttpHandler Holds() => HoldsUntil(int.MaxValue);

    /// <summary>The most requests that were ever in flight at once.</summary>
    public int MaxInFlight { get; private set; }

    /// <summary>A status and nothing else, for the refusals.</summary>
    public TestHttpHandler Answers(string path, HttpStatusCode status) =>
        Add(path, status, string.Empty, once: false);

    /// <summary>
    /// A status the first time the path is asked for and nothing after that, so a later rule for the same
    /// path answers the retry. Rules are matched in the order they were added.
    /// </summary>
    public TestHttpHandler AnswersOnce(string path, HttpStatusCode status) =>
        Add(path, status, string.Empty, once: true);

    /// <summary>
    /// Fails the way a name that does not resolve or a refused connection fails, which is not a status code
    /// at all. This is what an unreachable sibling API looks like, and blueprint's integration code has to
    /// answer for it far more often than it has to answer for a 500.
    /// </summary>
    public TestHttpHandler Throws(string path)
    {
        _rules.Add(new Rule(path, HttpStatusCode.OK, body: (Func<string>)null, once: false));

        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = Path(request);
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        var inFlight = Interlocked.Increment(ref _inFlight);

        lock (_sent)
        {
            Sent.Add(new SentRequest(
                request.Method,
                path,
                request.RequestUri.Query,
                request.Headers.TryGetValues("authorization", out var authorization)
                    ? string.Join(", ", authorization)
                    : null,
                body,
                request.Headers.ToDictionary(x => x.Key, x => string.Join(", ", x.Value))));

            MaxInFlight = Math.Max(MaxInFlight, inFlight);
        }

        try
        {
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
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }

        var rule = _rules.FirstOrDefault(x => !x.Used && x.Matches(request.Method, path));
        rule?.Use();

        if (rule is null)
        {
            throw new InvalidOperationException(
                $"TestHttpHandler was asked for {request.Method} {path}, which nothing stubbed. " +
                $"Stubbed: {(_rules.Count == 0 ? "nothing" : string.Join(", ", _rules.Select(x => x.Pattern)))}.");
        }

        // Read once: a lazily computed body may not answer the same way twice.
        var answer = rule.Body;

        if (answer is null)
        {
            throw new HttpRequestException($"TestHttpHandler was told to fail {path}.");
        }

        return new HttpResponseMessage(rule.Status)
        {
            Content = new StringContent(answer, Encoding.UTF8, "application/json"),
            RequestMessage = request,
        };
    }

    /// <summary>
    /// The transport as an <see cref="IHttpClientFactory"/>, which is how production reaches it: every
    /// client blueprint builds for a sibling API comes from <c>ApiClientsExtensions.GetHttpClient</c>, and
    /// the token request comes from <c>CreateClient()</c> directly.
    /// </summary>
    /// <remarks>
    /// The clients do not own the handler. <c>ApiClientsExtensions.GetToken</c> wraps its client in a
    /// <c>using</c>, so a handler disposed with the client would be dead for the next call - and one handler
    /// has to serve every client a test builds, since it is where the arrangement and the record both live.
    /// </remarks>
    public IHttpClientFactory AsFactory() => new HandlerFactory(this);

    private sealed class HandlerFactory(TestHttpHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private TestHttpHandler Add(string path, HttpStatusCode status, string body, bool once)
    {
        _rules.Add(new Rule(path, status, () => body, once));

        return this;
    }

    /// <summary>The path as a rule spells it: no leading slash, no query.</summary>
    private static string Path(HttpRequestMessage request) =>
        request.RequestUri.AbsolutePath.TrimStart('/');

    /// <summary>What one request carried. The body is read here because the content is disposed later.</summary>
    /// <remarks>
    /// <para>
    /// <c>Authorization</c> is read out of the raw header collection rather than
    /// <c>request.Headers.Authorization</c>, because <c>ApiClientsExtensions.GetHttpClient</c> writes the
    /// header by name and can write a value that is not a well-formed credential - which is a thing these
    /// tests assert, and which the typed property would refuse to hand back. It is also in
    /// <paramref name="Headers"/>; it keeps its own property because nearly every test wants it and none of
    /// the others did until the LRS's <c>X-Experience-API-Version</c> had to be pinned.
    /// </para>
    /// <para>
    /// <paramref name="Headers"/> holds the request headers only, not the content's - a
    /// <c>Content-Type</c> belongs to the body and is not on this collection.
    /// </para>
    /// </remarks>
    public sealed record SentRequest(
        HttpMethod Method,
        string Path,
        string Query,
        string Authorization,
        string Body,
        IReadOnlyDictionary<string, string> Headers = null);

    private sealed class Rule
    {
        public Rule(string pattern, HttpStatusCode status, Func<string> body, bool once)
        {
            Pattern = pattern;
            Status = status;
            _body = body;
            _once = once;

            // "GET some/path" scopes the rule to one method; a bare path answers any of them.
            var verb = pattern.IndexOf(' ');

            if (verb > 0)
            {
                Method = HttpMethod.Parse(pattern.AsSpan(0, verb));
                Path = pattern[(verb + 1)..];
            }
            else
            {
                Path = pattern;
            }
        }

        private readonly bool _once;
        private readonly Func<string> _body;

        /// <summary>As the test wrote it, method prefix and all. What an unmatched request is told about.</summary>
        public string Pattern { get; }

        public string Path { get; }

        /// <summary>Null means "any method", which is every rule that did not ask for one.</summary>
        public HttpMethod Method { get; }

        public HttpStatusCode Status { get; }

        /// <summary>Null means "throw instead of answering". Read once per request that matches.</summary>
        public string Body => _body?.Invoke();

        public bool Used { get; private set; }

        public void Use() => Used = _once;

        /// <summary>
        /// The method, when the rule named one, and the path - where <c>*</c> stands for any run of
        /// characters, anywhere in the pattern.
        /// </summary>
        /// <remarks>
        /// A wildcard in the middle is the case that matters: the sibling APIs' routes are mostly
        /// <c>api/views/{id}/teams</c> shaped, so <c>player/api/views/*/teams</c> has to match. A pattern
        /// with no <c>*</c> is an exact match, and one ending in <c>*</c> is a prefix match, which is what
        /// they meant before this understood the middle case.
        /// </remarks>
        public bool Matches(HttpMethod method, string requested)
        {
            if (Method is not null && Method != method)
            {
                return false;
            }

            var pattern = Path.TrimStart('/');

            if (!pattern.Contains('*'))
            {
                return string.Equals(requested, pattern, StringComparison.Ordinal);
            }

            var parts = pattern.Split('*');

            if (!requested.StartsWith(parts[0], StringComparison.Ordinal) ||
                !requested.EndsWith(parts[^1], StringComparison.Ordinal) ||
                requested.Length < parts.Sum(x => x.Length))
            {
                return false;
            }

            // Walk the middle parts in order, so a/*/b/*/c cannot be satisfied out of sequence.
            var at = parts[0].Length;

            for (var i = 1; i < parts.Length - 1; i++)
            {
                var found = requested.IndexOf(parts[i], at, StringComparison.Ordinal);

                if (found < 0)
                {
                    return false;
                }

                at = found + parts[i].Length;
            }

            return true;
        }
    }
}
