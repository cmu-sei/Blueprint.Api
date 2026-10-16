// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// A template belongs to no MSEL. The template lists ask the caller for nothing, so a MSEL's card, CITE
/// action, CITE duty or organization created as a template would publish its contents to anyone signed
/// in; the create is refused instead.
/// </summary>
public class TemplateOwnershipTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    [Theory]
    [InlineData("cards")]
    [InlineData("citeActions")]
    [InlineData("citeDuties")]
    [InlineData("organizations")]
    public async Task Create_ForAMselAsATemplate_Is409AndCreatesNothing(string route)
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();
        var id = Guid.NewGuid();

        var response = await Client(actor).PostAsJsonAsync($"/api/{route}", new
        {
            id,
            mselId = msel.Id,
            isTemplate = true,
            name = "Published",
            shortName = "P",
            description = "A MSEL's row"
        }, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await using var db = NewContext();
        Assert.False(await db.Cards.AnyAsync(x => x.Id == id, Ct));
        Assert.False(await db.CiteActions.AnyAsync(x => x.Id == id, Ct));
        Assert.False(await db.CiteDuties.AnyAsync(x => x.Id == id, Ct));
        Assert.False(await db.Organizations.AnyAsync(x => x.Id == id, Ct));
    }

    [Theory]
    [InlineData("cards")]
    [InlineData("citeActions")]
    [InlineData("citeDuties")]
    [InlineData("organizations")]
    public async Task Create_AsATemplateWithNoMsel_Is201(string route)
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Client(actor).PostAsJsonAsync($"/api/{route}", new
        {
            isTemplate = true,
            name = "Template",
            shortName = "T",
            description = "A template"
        }, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
