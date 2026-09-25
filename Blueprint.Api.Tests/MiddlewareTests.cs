// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// The three things <c>Startup.Configure</c> puts in the pipeline that no controller can see: the
/// middleware promoting a <c>?bearer=…</c> query parameter into the <c>Authorization</c> header, response
/// compression, and the two health probes.
/// </summary>
/// <remarks>
/// <para>
/// Identity is observed through <c>GET api/me/systemPermissions</c>, as <see cref="ClaimsPipelineTests"/>
/// does, because it is the one route whose whole answer is the caller's own claims - so "who did the
/// pipeline think this was" is legible in the body rather than inferred from a status. The actor holds
/// exactly one permission, making the answer the literal <c>["ViewMsels"]</c>.
/// </para>
/// <para>
/// Every query-string test sends from <see cref="ApiTestBase.AnonymousClient"/>, and has to:
/// <c>Client(actor)</c> stamps <c>X-Test-User</c> on every request, which
/// <see cref="TestAuthHandler"/> reads first, so a test using it would authenticate whether the promotion
/// worked or not. The <c>Authorization: Bearer &lt;user id&gt;</c> fallback in that handler exists for
/// this file.
/// </para>
/// <para>
/// Not tested, because this harness cannot isolate it: the promoted token is never URL-decoded, so a
/// percent-encoded token arrives mangled. Here any token that is not a bare guid fails in
/// <c>Guid.Parse</c> inside the claims transformer and answers 500 - the same status the decoding defect
/// would produce - so a test for it would pass for the wrong reason.
/// <see cref="ATokenWithATrailingEqualsSign_IsSilentlyTruncated"/> carries the weight instead, the
/// truncation being observable as a *successful* authentication as somebody else.
/// </para>
/// </remarks>
public class MiddlewareTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    private const string Route = "api/me/systemPermissions";
    private const string OnePermission = "[\"ViewMsels\"]";

    /// <summary>What both health probes answer - the framework's default writer, not a configured one.</summary>
    private const string Healthy = """{"status":"Healthy"}""";

    /// <summary>An actor holding one permission, so the response body names them unambiguously.</summary>
    private async Task<TestActor> Viewer() =>
        await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

    // -------------------------------------------------------------------------------------------------
    // The ?bearer= promotion (Startup.cs:315-331)
    // -------------------------------------------------------------------------------------------------

    /// <remarks>
    /// The middleware's reason for existing: a browser opening a download or an <c>EventSource</c> cannot
    /// set a header, so the token travels in the query string. It sits after <c>UseRouting</c> and before
    /// <c>UseAuthentication</c>, which is what makes the promotion effective rather than decorative.
    /// </remarks>
    [Fact]
    public async Task ABearerTokenInTheQueryString_Authenticates()
    {
        var actor = await Viewer();

        var response = await AnonymousClient.GetAsync($"{Route}?bearer={actor.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OnePermission, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <remarks>
    /// The control for the two 401s below: an anonymous request to this route is refused whatever the
    /// query string says, so a test asserting 401 has to be read against this one to mean anything.
    /// </remarks>
    [Fact]
    public async Task AnAnonymousRequest_WithNoQueryStringAtAll_Is401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await AnonymousClient.GetAsync(Route, Ct)).StatusCode);
    }

    /// <remarks>
    /// The guard is on the header, not on the query string, so a request carrying both is not an error -
    /// the header simply wins and the query parameter is ignored. That is the right precedence, and worth
    /// pinning because the middleware could as easily have appended a second <c>Authorization</c> value.
    /// </remarks>
    [Fact]
    public async Task AnAuthorizationHeader_WinsOverTheQueryString()
    {
        var header = await Viewer();
        var query = await Actor().WithSystemPermissions(SystemPermission.ViewCatalogs).SeedAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Route}?bearer={query.Id}");
        request.Headers.Add("Authorization", $"Bearer {header.Id}");

        var response = await AnonymousClient.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OnePermission, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <remarks>
    /// BUG: the parameter is found with <c>SingleOrDefault</c>, which throws when there are two - so a
    /// request naming <c>bearer</c> twice is a 500 from inside the middleware, before routing has decided
    /// anything and before the MVC filters that turn an exception into an <c>ApiError</c>. A client that
    /// appends the token to a url which already carries one gets an unexplained server error on every
    /// request. <c>FirstOrDefault</c> is the whole fix.
    /// </remarks>
    [Fact]
    public async Task TwoBearerParameters_AreA500()
    {
        var actor = await Viewer();

        var response = await AnonymousClient.GetAsync(
            $"{Route}?bearer={actor.Id}&bearer={actor.Id}", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <remarks>
    /// BUG: the match is <c>StartsWith("bearer=")</c> with no comparison argument, so it is
    /// case-sensitive - <c>?Bearer=</c> is not promoted and the request is refused. Query parameter names
    /// are case-sensitive by convention, so this is defensible; what is not is that the failure is
    /// indistinguishable from an expired token, since nothing logs that a parameter was nearly matched.
    /// </remarks>
    [Fact]
    public async Task ACapitalisedBearerParameter_IsNotPromoted()
    {
        var actor = await Viewer();

        var response = await AnonymousClient.GetAsync($"{Route}?Bearer={actor.Id}", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <remarks>
    /// BUG, and the sharp one: the value is taken with <c>Split('=')[1]</c>, so everything from the
    /// second <c>=</c> onwards is discarded. A real JWT is three base64url segments joined by dots and
    /// carries no <c>=</c>, which is why this has never bitten - but base64 *padding* is <c>=</c>, so any
    /// token or token-like id with a padded segment is silently truncated and presented as a different
    /// credential. Here the truncation is visible as a success: the request authenticates as the
    /// un-suffixed actor rather than being refused. <c>Split('=', 2)[1]</c> is the fix.
    /// </remarks>
    [Fact]
    public async Task ATokenWithATrailingEqualsSign_IsSilentlyTruncated()
    {
        var actor = await Viewer();

        var response = await AnonymousClient.GetAsync($"{Route}?bearer={actor.Id}=discarded", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OnePermission, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <remarks>
    /// The one guard that works: an empty value is whitespace, so nothing is appended and the request is
    /// refused as anonymous rather than presenting an <c>Authorization: Bearer</c> with nothing after it,
    /// which the JWT handler would report as a malformed token.
    /// </remarks>
    [Fact]
    public async Task AnEmptyBearerParameter_IsNotPromoted()
    {
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await AnonymousClient.GetAsync($"{Route}?bearer=", Ct)).StatusCode);
    }

    /// <remarks>
    /// The split is on <c>&amp;</c> and the <c>Substring(1)</c> drops the <c>?</c>, so the parameter is
    /// found wherever it sits. Worth pinning because the one real caller - a download url built by the UI
    /// - always has other parameters, so this is the arrangement production actually uses.
    /// </remarks>
    [Fact]
    public async Task OtherQueryParametersEitherSide_DoNotBreakThePromotion()
    {
        var actor = await Viewer();

        var response = await AnonymousClient.GetAsync($"{Route}?a=1&bearer={actor.Id}&b=2", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OnePermission, await response.Content.ReadAsStringAsync(Ct));
    }

    // -------------------------------------------------------------------------------------------------
    // Response compression (Startup.cs:122-141, app.UseResponseCompression() at :310)
    // -------------------------------------------------------------------------------------------------

    /// <remarks>
    /// There is no minimum size, so every JSON response is compressed - including this three-word one,
    /// where the encoding costs more than it saves. That is the trade the configuration makes deliberately
    /// for the competency-framework payloads its comment names.
    /// </remarks>
    [Fact]
    public async Task AJsonResponse_IsGzipped()
    {
        var actor = await Viewer();

        var response = await SendAccepting("gzip", Client(actor));

        Assert.Equal("gzip", Assert.Single(response.Content.Headers.ContentEncoding));
        Assert.Equal(OnePermission, await Decompress(response));
    }

    /// <remarks>
    /// Brotli stays registered for the client that accepts nothing else, which is the only case it is
    /// reached in - see below.
    /// </remarks>
    [Fact]
    public async Task AJsonResponse_IsBrotliedWhenThatIsAllTheClientAccepts()
    {
        var actor = await Viewer();

        var response = await SendAccepting("br", Client(actor));

        Assert.Equal("br", Assert.Single(response.Content.Headers.ContentEncoding));
        Assert.Equal(OnePermission, await Decompress(response));
    }

    /// <remarks>
    /// gzip wins, and not because the client listed it: <c>ResponseCompressionProvider</c> orders
    /// candidates by the quality the request gave them and then by the provider's registration index, so
    /// with no explicit qualities the winner is whichever <c>Startup</c> added first. This is the
    /// assertion that pins the comment at <c>Startup.cs:127-131</c> - Brotli at <c>Fastest</c> is quality
    /// 1, which on a megabyte of framework JSON is both bigger and slower than gzip. Adding Brotli before
    /// gzip would reverse it silently, with no test but this one to say so.
    /// </remarks>
    [Fact]
    public async Task AClientAcceptingBoth_IsSentGzipWhateverOrderItListedThem()
    {
        var actor = await Viewer();

        var response = await SendAccepting("br, gzip", Client(actor));

        Assert.Equal("gzip", Assert.Single(response.Content.Headers.ContentEncoding));
    }

    /// <remarks>
    /// <para>
    /// <c>MimeTypes</c> is set explicitly rather than left at the framework's defaults, and the difference
    /// is <c>text/plain</c>: SignalR's long-polling transport answers <c>text/plain</c>, and compressing a
    /// response that is deliberately held open buffers it, so a client waiting on a notification would not
    /// receive it until the poll timed out. That exclusion is the reason the list is written out, and
    /// nothing else in the suite can observe it - a long-polling connection is not reachable from here, so
    /// the configured list is the subject.
    /// </para>
    /// <para>
    /// The exclusion is also why deleting an entry from this list is safe and adding <c>text/plain</c> to
    /// it is not, which no comment on <c>Startup.cs:133</c> would survive as well as an assertion.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheCompressionMimeTypes_AreJsonOnlyAndDeliberatelyExcludeTextPlain()
    {
        var mimeTypes = Factory.Services
            .GetRequiredService<IOptions<ResponseCompressionOptions>>().Value.MimeTypes
            .ToList();

        Assert.Equal(["application/json", "text/json", "application/problem+json"], mimeTypes);
        Assert.DoesNotContain("text/plain", mimeTypes);
    }

    /// <remarks>
    /// So the health probes are compressed, which is worth pinning because it is the opposite of what the
    /// <c>text/plain</c> exclusion above suggests: <c>MapHealthChecks</c> writes
    /// <c>application/json</c> by default in .NET 8 and later, putting a 24-byte body inside the list and
    /// spending a gzip stream on it several times a minute for as long as the deployment lives. Kubernetes
    /// does not send <c>Accept-Encoding</c>, so in practice nothing collects it - which is the only reason
    /// this costs nothing. A custom <c>ResponseWriter</c> answering <c>text/plain</c>, as the framework
    /// did before .NET 8, would exclude them properly.
    /// </remarks>
    [Fact]
    public async Task AHealthProbe_IsGzippedBecauseItAnswersJson()
    {
        var response = await SendAccepting("gzip", AnonymousClient, "api/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("gzip", Assert.Single(response.Content.Headers.ContentEncoding));
        Assert.Equal(Healthy, await Decompress(response));
    }

    // -------------------------------------------------------------------------------------------------
    // The health probes (Startup.cs:354-361)
    // -------------------------------------------------------------------------------------------------

    /// <remarks>
    /// <para>
    /// Anonymous, and that is a property of where they are mapped rather than of any attribute:
    /// <c>MapHealthChecks</c> creates an endpoint carrying none of MVC's metadata, so the application-wide
    /// <c>AuthorizeFilter</c> at <c>Startup.cs:160</c> - which every controller action is subject to -
    /// does not apply. Kubernetes could not authenticate if it did.
    /// </para>
    /// <para>
    /// The body is the framework's default minimal JSON writer's, not a custom one -
    /// <c>MapHealthChecks</c> is called with no <c>HealthCheckOptions</c> at all - so it is a document
    /// rather than the bare word the older default wrote. It carries the aggregate status and nothing
    /// about the check that produced it, which for blueprint is exactly the same thing: there is one
    /// check, and <see cref="BothProbes_AnswerTheSameSingleCheck"/> is why that matters.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("api/health/ready")]
    [InlineData("api/health/live")]
    public async Task AHealthProbe_IsAnonymousAndHealthy(string route)
    {
        var response = await AnonymousClient.GetAsync(route, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Healthy, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <remarks>
    /// One check answers both probes - <c>AddNpgSql(connectionString, tags: ["ready", "live"])</c>, inside
    /// the provider switch - so blueprint's liveness and readiness are the same question, and a database
    /// outage makes Kubernetes restart the pod rather than take it out of the load balancer. The two
    /// <c>Predicate</c>s therefore select the same single check, which is what this pins: the same body,
    /// not merely two healthy answers.
    /// </remarks>
    [Fact]
    public async Task BothProbes_AnswerTheSameSingleCheck()
    {
        var ready = await AnonymousClient.GetStringAsync("api/health/ready", Ct);
        var live = await AnonymousClient.GetStringAsync("api/health/live", Ct);

        Assert.Equal(ready, live);
    }

    /// <summary>
    /// A request carrying an <c>Accept-Encoding</c>, which has to be per-request: no client in the suite
    /// sets one by default, and <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{T}"/>'s
    /// handler chain does no automatic decompression, so the header survives to be asserted on.
    /// </summary>
    private async Task<HttpResponseMessage> SendAccepting(
        string encoding, HttpClient client, string route = Route)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.Add("Accept-Encoding", encoding);

        return await client.SendAsync(request, Ct);
    }

    /// <summary>The response body, decoded according to the <c>Content-Encoding</c> it arrived with.</summary>
    private async Task<string> Decompress(HttpResponseMessage response)
    {
        var encoding = response.Content.Headers.ContentEncoding.Single();

        await using var body = await response.Content.ReadAsStreamAsync(Ct);
        await using Stream decoded = encoding switch
        {
            "gzip" => new GZipStream(body, CompressionMode.Decompress),
            "br" => new BrotliStream(body, CompressionMode.Decompress),
            _ => throw new InvalidOperationException($"Unexpected Content-Encoding '{encoding}'."),
        };

        using var reader = new StreamReader(decoded);

        return await reader.ReadToEndAsync(Ct);
    }
}
