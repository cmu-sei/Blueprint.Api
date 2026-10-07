// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Blueprint.Api.Tests.Support;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// <c>LmtService</c> / <c>LmtController</c> - the one route, <c>GET lmt/resource/{mselId}</c>, which
/// publishes a MSEL's IEEE 2881 learning metadata for LMS and PCTE catalog discovery.
/// </summary>
/// <remarks>
/// The only <c>[AllowAnonymous]</c> controller in the API, which is deliberate - and the only one with no
/// published-or-template check, which is not. It is therefore absent from
/// <see cref="RouteAuthorizationTests"/>: there is no authorization here to assert.
/// </remarks>
public class LmtEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    [Fact]
    public async Task Get_ForAMsel_AnswersItsMetadata()
    {
        var msel = TestData.Msel();
        msel.Name = "Cyber Shield 26";
        msel.Description = "A defensive exercise";
        msel.EducationalLevel = "Intermediate";
        msel.Subject = "incident response, forensics";
        msel.Keywords = " blue team , SOC ";
        await Seed(msel);

        var response = await Client().GetAsync($"api/lmt/resource/{msel.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/ld+json", response.Content.Headers.ContentType.MediaType);

        var root = await Document(response);
        Assert.Equal("Cyber Shield 26", root.GetProperty("name").GetString());
        Assert.Equal("A defensive exercise", root.GetProperty("description").GetString());
        Assert.Equal("Intermediate", root.GetProperty("educationalLevel").GetString());
        Assert.Equal(
            ["incident response", "forensics"],
            root.GetProperty("subject").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(
            ["blue team", "SOC"],
            root.GetProperty("keywords").EnumerateArray().Select(x => x.GetString()));
        Assert.Empty(root.GetProperty("assesses").EnumerateArray());
    }

    /// <remarks>
    /// The four defaults are the service's own, not the column's: an unset <c>EducationalUse</c> is
    /// published as <c>Assessment</c>, <c>CourseMode</c> as <c>Online</c> and <c>Language</c> as
    /// <c>en-US</c>, while <c>EducationalLevel</c> has no default and is published empty.
    /// </remarks>
    [Fact]
    public async Task Get_ForAMselWithNoEducationalMetadata_PublishesTheDefaults()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var root = await Document(await Client().GetAsync($"api/lmt/resource/{msel.Id}", Ct));

        Assert.Equal("Assessment", root.GetProperty("educationalUse").GetString());
        Assert.Equal("Online", root.GetProperty("courseMode").GetString());
        Assert.Equal("en-US", root.GetProperty("inLanguage").GetString());
        Assert.Equal(string.Empty, root.GetProperty("educationalLevel").GetString());
        Assert.Empty(root.GetProperty("subject").EnumerateArray());
        Assert.Empty(root.GetProperty("keywords").EnumerateArray());
    }

    [Fact]
    public async Task Get_PublishesTheMselsCompetenciesAsAssesses()
    {
        var msel = TestData.Msel();
        var framework = TestData.CompetencyFramework(idNumber: "https://example.test/framework");
        await Seed(msel, framework);
        var competency = TestData.Competency(framework.Id, "https://example.test/c/1");
        competency.ShortName = "Contain an incident";
        var other = TestData.Competency(framework.Id, "LOCAL-2");
        other.ShortName = "Write a report";
        await Seed(competency, other);
        await Seed(
            TestData.MselCompetency(msel.Id, competency.Id),
            TestData.MselCompetency(msel.Id, other.Id));

        var root = await Document(await Client().GetAsync($"api/lmt/resource/{msel.Id}", Ct));

        var assesses = root.GetProperty("assesses").EnumerateArray().ToList();
        Assert.Equal(2, assesses.Count);
        Assert.All(assesses, x => Assert.Equal("DefinedTerm", x.GetProperty("type").GetString()));
        Assert.Contains("Contain an incident", assesses.Select(x => x.GetProperty("name").GetString()));
        Assert.Contains("Write a report", assesses.Select(x => x.GetProperty("name").GetString()));

        // An ID number that is already an IRI is published as-is; one that is not becomes an API url.
        Assert.Contains("https://example.test/c/1", assesses.Select(x => x.GetProperty("id").GetString()));
        Assert.Contains(
            $"/competencies/{other.Id}",
            assesses.Select(x => x.GetProperty("id").GetString()).Single(x => x.Contains("competencies")));
        Assert.Contains(
            "https://example.test/framework",
            assesses.Select(x => x.GetProperty("inDefinedTermSet").GetString()));
    }

    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is404()
    {
        var response = await Client().GetAsync($"api/lmt/resource/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Get answers the JSON ld keywords without their at prefix.</summary>
    [Fact]
    public async Task Get_AnswersTheJsonLdKeywordsWithoutTheirAtPrefix()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var body = await Client().GetStringAsync($"api/lmt/resource/{msel.Id}", Ct);

        Assert.DoesNotContain("@context", body);
        Assert.DoesNotContain("@type", body);
        Assert.DoesNotContain("@id", body);
        Assert.Contains("\"context\": \"https://schema.org/\"", body);
        Assert.Contains("\"type\": \"LearningResource\"", body);
    }

    /// <summary>Get for a pending MSEL that is not a template publishes it anyway.</summary>
    [Fact]
    public async Task Get_ForAPendingMselThatIsNotATemplate_PublishesItAnyway()
    {
        var msel = TestData.Msel(isTemplate: false);
        msel.Name = "Not for publication";
        await Seed(msel);

        var root = await Document(await Client().GetAsync($"api/lmt/resource/{msel.Id}", Ct));

        Assert.Equal("Not for publication", root.GetProperty("name").GetString());
    }

    /// <summary>Get answers an id that does not include the API path base.</summary>
    [Fact]
    public async Task Get_AnswersAnIdThatDoesNotIncludeTheApiPathBase()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var root = await Document(await Client().GetAsync($"api/lmt/resource/{msel.Id}", Ct));

        Assert.Equal($"http://localhost:4724/lmt/resource/{msel.Id}", root.GetProperty("id").GetString());
        Assert.Equal($"http://localhost:4725/#/msel/{msel.Id}", root.GetProperty("url").GetString());
    }

    private async Task<JsonElement> Document(System.Net.Http.HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
    }
}
