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

    /// <summary>Every request that reached the transport, in order, with what it carried.</summary>
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
    /// For the identity provider, whose discovery document and token response are not types this
    /// repository has: what IdentityModel parses is the OAuth JSON, so that is what a test of it should be
    /// handing over.
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

        Sent.Add(new SentRequest(
            request.Method,
            path,
            request.RequestUri.Query,
            request.Headers.TryGetValues("authorization", out var authorization)
                ? string.Join(", ", authorization)
                : null,
            request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));

        var rule = _rules.FirstOrDefault(x => !x.Used && x.Matches(request.Method, path));
        rule?.Use();

        if (rule is null)
        {
            throw new InvalidOperationException(
                $"TestHttpHandler was asked for {request.Method} {path}, which nothing stubbed. " +
                $"Stubbed: {(_rules.Count == 0 ? "nothing" : string.Join(", ", _rules.Select(x => x.Pattern)))}.");
        }

        // Read once: a lazily computed body may not answer the same way twice.
        var body = rule.Body;

        if (body is null)
        {
            throw new HttpRequestException($"TestHttpHandler was told to fail {path}.");
        }

        return new HttpResponseMessage(rule.Status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
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
    /// <c>Authorization</c> is read out of the raw header collection rather than
    /// <c>request.Headers.Authorization</c>, because <c>ApiClientsExtensions.GetHttpClient</c> writes the
    /// header by name and can write a value that is not a well-formed credential - which is a thing these
    /// tests assert, and which the typed property would refuse to hand back.
    /// </remarks>
    public sealed record SentRequest(
        HttpMethod Method, string Path, string Query, string Authorization, string Body);

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
        /// The path, or a prefix of it when the rule ends in <c>*</c> - which is what an arrangement that
        /// does not care about the id in the route wants - and the method when the rule named one.
        /// </summary>
        public bool Matches(HttpMethod method, string requested) =>
            (Method is null || Method == method) &&
            (Path.EndsWith('*')
                ? requested.StartsWith(Path.TrimEnd('*'), StringComparison.Ordinal)
                : string.Equals(requested, Path.TrimStart('/'), StringComparison.Ordinal));
    }
}
