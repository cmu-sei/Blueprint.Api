// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Tests.Infrastructure;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// What an error actually looks like on the wire. Two shapes, not one: the framework's
/// <c>ValidationProblemDetails</c> for anything model binding refuses, and blueprint's own
/// <c>ApiError</c> for anything a service throws.
/// </summary>
/// <remarks>
/// <para>
/// This is the only file in the suite that asserts the body of a 400. Some 250 endpoint tests assert the
/// status alone, which is why it took until now to notice that the shape they were assumed to carry is not
/// the one they carry: <c>ValidateModelStateFilter</c> builds an <c>ApiError</c> titled "Invalid Data" and
/// no request ever reaches it, because <c>BaseController</c> carries <c>[ApiController]</c>
/// (<c>BaseController.cs:11</c>) and nothing configures <c>SuppressModelStateInvalidFilter</c> - so MVC's
/// own <c>ModelStateInvalidFilter</c>, at order -2000 against the global filter's 0, answers first. See
/// <see cref="FilterTests"/>, which describes the filter that does not run.
/// </para>
/// <para>
/// Everything here is measured rather than derived. The two dialects differ in status, media type, member
/// names and the treatment of an <c>int</c>, and a client has to handle both - which nothing in
/// <c>blueprint.ui</c> or <c>Blueprint.Api.Client</c> is told, neither shape appearing in the OpenAPI
/// document.
/// </para>
/// </remarks>
public class ErrorResponseTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    /// <summary>An actor who may create an organization, so a refusal is the body's fault and not theirs.</summary>
    private async Task<TestActor> Author() =>
        await Actor().WithSystemPermissions(SystemPermission.ManageOrganizations).SeedAsync();

    /// <summary>
    /// Posts <paramref name="json"/> verbatim, rather than through <c>PostAsJsonAsync</c>, because every
    /// case here is a document the application's own serializer would refuse to write.
    /// </summary>
    private async Task<HttpResponseMessage> PostJson(HttpClient client, string json) =>
        await client.PostAsync(
            "/api/organizations", new StringContent(json, Encoding.UTF8, "application/json"), Ct);

    // -------------------------------------------------------------------------------------------------
    // A binding failure: the framework's shape, not blueprint's
    // -------------------------------------------------------------------------------------------------

    /// <remarks>
    /// The correction this file exists for. <c>3c31930</c> and <c>b5d2d86</c> both recorded a 400 as
    /// "the <c>ApiError</c> shape"; it is <c>ValidationProblemDetails</c>, served as
    /// <c>application/problem+json</c>, with a per-member <c>errors</c> dictionary keyed by the JSON path
    /// and none of <c>ApiError</c>'s <c>detail</c>. Which is the better of the two answers - a client can
    /// show a message against the field it belongs to - so the fix for the dead filter is to delete it
    /// rather than to make it reachable.
    /// </remarks>
    [Fact]
    public async Task ABindingFailure_IsAProblemDocumentAndNotBlueprintsApiError()
    {
        var response = await PostJson(Client(await Author()), """{"name":"probe","mselId":3}""");
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("One or more validation errors occurred.", body);
        Assert.Contains("$.mselId", body);
        Assert.DoesNotContain("Invalid Data", body);
    }

    /// <remarks>
    /// BUG: <c>JsonIntegerConverter</c> is registered on the MVC serializer for every <c>int</c>, and
    /// <c>ProblemDetails.Status</c> is an <c>int?</c> - so every problem document blueprint answers
    /// reports its status as a JSON *string*, where RFC 9457 section 3.1 says the member is a number. A
    /// client validating the document against the spec rejects it, and one reading
    /// <c>problem.status === 400</c> in TypeScript is quietly wrong. The converter's blast radius is wider
    /// than the view models it was written for; the fix is to scope it with a
    /// <c>JsonConverterAttribute</c> on the properties that need it rather than to register it globally.
    /// </remarks>
    [Fact]
    public async Task TheProblemDocument_WritesItsStatusAsAJsonString()
    {
        var response = await PostJson(Client(await Author()), """{"name":"probe","mselId":3}""");
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Contains("""
            "status":"400"
            """, body);
        Assert.DoesNotContain("""
            "status":400
            """, body);
    }

    /// <remarks>
    /// BUG, and the one the converters cause rather than the framework: an unparseable id is refused
    /// against the empty key with the generic "The supplied value is invalid.", so a document with a bad
    /// id in it is rejected without saying which field was bad - where the case above names
    /// <c>$.mselId</c>. The cause is in
    /// <see cref="JsonConverterTests.ANullableGuidThatIsNotAGuid_IsAnUnwrappedFormatExceptionNamingNothing"/>:
    /// <c>Guid.Parse</c> is the converter's own call, so System.Text.Json does not wrap its
    /// <c>FormatException</c> and there is no path to attribute. One <c>Guid.TryParse</c> and a
    /// <c>throw new JsonException()</c> puts this case in the shape of the one above.
    /// </remarks>
    [Fact]
    public async Task AnUnparseableGuid_IsRefusedAgainstNoFieldAtAll()
    {
        var response = await PostJson(Client(await Author()), """{"name":"probe","mselId":"nope"}""");
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("The supplied value is invalid.", body);
        Assert.DoesNotContain("$.mselId", body);
    }

    /// <remarks>
    /// <c>ViewModels.Base</c> declares <c>DateCreated</c> and <c>CreatedBy</c> non-nullable, so a client
    /// echoing back a row it read - with the nulls the API itself answered for an unmodified entity -
    /// cannot send <c>dateCreated: null</c> either. Recorded because it cost 36 tests at once earlier in
    /// this branch, all failing with one cause: the request is a 400 that never reaches the controller,
    /// so no amount of reading the service explains it.
    /// </remarks>
    [Fact]
    public async Task ANullAuditField_IsRefusedBeforeTheControllerRuns()
    {
        var response = await PostJson(
            Client(await Author()), """{"name":"probe","dateCreated":null}""");
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("$.dateCreated", body);
    }

    /// <remarks>
    /// The upload routes' refusal is MVC's too. <c>FileForm.ToUpload</c> carries <c>[Required]</c>, and
    /// what answers is <c>ModelStateInvalidFilter</c> - so the message is the framework's generated
    /// "The ToUpload field is required." keyed by the property name rather than a JSON path, this being a
    /// form rather than a body. Note the part has to be well-formed and merely misnamed: a
    /// <c>MultipartFormDataContent</c> with no parts at all is refused earlier still, by the form reader,
    /// and asserting the status alone cannot tell the two apart.
    /// </remarks>
    [Fact]
    public async Task AMissingFilePart_IsAProblemDocumentNamingTheProperty()
    {
        using var content = new MultipartFormDataContent
        {
            { new StringContent("not the file part"), "SomethingElse" },
        };

        var response = await Client(await Author())
            .PostAsync("/api/organizations/json", content, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("ToUpload", body);
        Assert.DoesNotContain("Invalid Data", body);
    }

    // -------------------------------------------------------------------------------------------------
    // A thrown exception: blueprint's shape
    // -------------------------------------------------------------------------------------------------

    /// <remarks>
    /// The contrast, and the reason both shapes matter. Everything a service throws is answered by
    /// <see cref="Blueprint.Api.Infrastructure.Filters.JsonExceptionFilter"/> as an <c>ApiError</c>:
    /// <c>application/json</c> rather than <c>application/problem+json</c>, a <c>title</c> and a
    /// <c>detail</c> rather than an <c>errors</c> dictionary, and no <c>type</c> or <c>instance</c>. So a
    /// client cannot parse blueprint's errors with one reader - and cannot tell the shapes apart by status
    /// either, a 400 arriving in both dialects (<c>ApiError</c> for a <c>BadRequestException</c>, a
    /// problem document for a bad body). The media type is the only reliable discriminator.
    /// <c>GET organizations/templates</c> cannot serve as this case, incidentally: it asks for no
    /// permission at all and answers 200 <c>[]</c> to a caller holding nothing.
    /// </remarks>
    [Fact]
    public async Task AThrownExceptionIsBlueprintsApiError_NotAProblemDocument()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var response = await Client(await Actor().SeedAsync())
            .GetAsync($"/api/msels/{msel.Id}/organizations", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("""
            "title"
            """, body);
        Assert.DoesNotContain("errors", body);
    }

    /// <remarks>
    /// And the converter reaches this shape too - <c>ApiError.Status</c> being an <c>int</c> - so the one
    /// thing both dialects agree on is the thing both get wrong. Worth pinning separately from the
    /// problem-document case because the two are written by different code, so a fix applied to one leaves
    /// the other.
    /// </remarks>
    [Fact]
    public async Task TheApiError_WritesItsStatusAsAJsonStringAsWell()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var response = await Client(await Actor().SeedAsync())
            .GetAsync($"/api/msels/{msel.Id}/organizations", Ct);

        Assert.Contains("""
            "status":"403"
            """, await response.Content.ReadAsStringAsync(Ct));
    }
}
