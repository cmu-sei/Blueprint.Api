// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Tests.Support;
using Xunit;

namespace Blueprint.Api.Tests.Infrastructure.Filters;

/// <summary>What an error actually looks like on the wire. Two shapes, not one: the framework's
/// <c>ValidationProblemDetails</c> for anything model binding refuses, and blueprint's own <c>ApiError</c>
/// for anything a service throws.</summary>
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

    /// <summary>A binding failure is answered by MVC as a <c>ValidationProblemDetails</c> document, not an
    /// <c>ApiError</c>.</summary>
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

    /// <summary>The problem document writes its status as a JSON string.</summary>
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

    /// <summary>An unparseable id is refused against the empty key with the generic message.</summary>
    [Fact]
    public async Task AnUnparseableGuid_IsRefusedAgainstNoFieldAtAll()
    {
        var response = await PostJson(Client(await Author()), """{"name":"probe","mselId":"nope"}""");
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("The supplied value is invalid.", body);
        Assert.DoesNotContain("$.mselId", body);
    }

    /// <summary>A null audit field is refused before the controller runs.</summary>
    [Fact]
    public async Task ANullAuditField_IsRefusedBeforeTheControllerRuns()
    {
        var response = await PostJson(
            Client(await Author()), """{"name":"probe","dateCreated":null}""");
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("$.dateCreated", body);
    }

    /// <summary>A multipart body without the file part is a problem document naming <c>ToUpload</c>.</summary>
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

    /// <summary>A thrown exception is blueprints API error not a problem document.</summary>
    [Fact]
    public async Task AThrownExceptionIsBlueprintsApiError_NotAProblemDocument()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var response = await Client(await Actor().OnNewMsel(MselRole.Owner).SeedAsync())
            .GetAsync($"/api/msels/{msel.Id}/organizations", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("""
            "title"
            """, body);
        Assert.DoesNotContain("errors", body);
    }

    /// <summary>An <c>ApiError</c> also carries its status as a JSON string.</summary>
    [Fact]
    public async Task TheApiError_WritesItsStatusAsAJsonStringAsWell()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var response = await Client(await Actor().SeedAsync())
            .GetAsync($"/api/msels/{msel.Id}/organizations", Ct);

        Assert.Contains("""
            "status":"403"
            """, await response.Content.ReadAsStringAsync(Ct));
    }
}
