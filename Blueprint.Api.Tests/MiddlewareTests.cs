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
using Blueprint.Api.Tests.Support;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>The three things <c>Startup.Configure</c> puts in the pipeline that no controller can see: the
/// middleware promoting a <c>?bearer=…</c> query parameter into the <c>Authorization</c> header, response
/// compression, and the two health probes.</summary>
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

        var response = await Client().GetAsync($"{Route}?bearer={actor.Id}", Ct);

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
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client().GetAsync(Route, Ct)).StatusCode);
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

        var response = await Client().SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OnePermission, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>Two bearer parameters are answered with a 500.</summary>
    [Fact]
    public async Task TwoBearerParameters_AreA500()
    {
        var actor = await Viewer();

        var response = await Client().GetAsync(
            $"{Route}?bearer={actor.Id}&bearer={actor.Id}", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>The parameter name is matched case-sensitively, so <c>?Bearer=</c> is not promoted.</summary>
    [Fact]
    public async Task ACapitalisedBearerParameter_IsNotPromoted()
    {
        var actor = await Viewer();

        var response = await Client().GetAsync($"{Route}?Bearer={actor.Id}", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>A token with a trailing equals sign is silently truncated.</summary>
    [Fact]
    public async Task ATokenWithATrailingEqualsSign_IsSilentlyTruncated()
    {
        var actor = await Viewer();

        var response = await Client().GetAsync($"{Route}?bearer={actor.Id}=discarded", Ct);

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
            (await Client().GetAsync($"{Route}?bearer=", Ct)).StatusCode);
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

        var response = await Client().GetAsync($"{Route}?a=1&bearer={actor.Id}&b=2", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OnePermission, await response.Content.ReadAsStringAsync(Ct));
    }

    // -------------------------------------------------------------------------------------------------
    // Response compression (Startup.cs:122-141, app.UseResponseCompression() at :310)
    // -------------------------------------------------------------------------------------------------

    /// <summary>Every JSON response is gzipped, however small.</summary>
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

    /// <summary>The compressed MIME types are JSON only; <c>text/plain</c>, which long polling answers, is
    /// excluded.</summary>
    [Fact]
    public void TheCompressionMimeTypes_AreJsonOnlyAndDeliberatelyExcludeTextPlain()
    {
        var mimeTypes = Factory.Services
            .GetRequiredService<IOptions<ResponseCompressionOptions>>().Value.MimeTypes
            .ToList();

        Assert.Equal(["application/json", "text/json", "application/problem+json"], mimeTypes);
        Assert.DoesNotContain("text/plain", mimeTypes);
    }

    /// <summary>The health probes answer <c>application/json</c> and so are gzipped.</summary>
    [Fact]
    public async Task AHealthProbe_IsGzippedBecauseItAnswersJson()
    {
        var response = await SendAccepting("gzip", Client(), "api/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("gzip", Assert.Single(response.Content.Headers.ContentEncoding));
        Assert.Equal(Healthy, await Decompress(response));
    }

    // -------------------------------------------------------------------------------------------------
    // The health probes (Startup.cs:354-361)
    // -------------------------------------------------------------------------------------------------

    /// <summary>The health probes are anonymous and answer the default minimal JSON body.</summary>
    [Theory]
    [InlineData("api/health/ready")]
    [InlineData("api/health/live")]
    public async Task AHealthProbe_IsAnonymousAndHealthy(string route)
    {
        var response = await Client().GetAsync(route, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Healthy, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>Both probes answer the same single check.</summary>
    [Fact]
    public async Task BothProbes_AnswerTheSameSingleCheck()
    {
        var ready = await Client().GetStringAsync("api/health/ready", Ct);
        var live = await Client().GetStringAsync("api/health/live", Ct);

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
