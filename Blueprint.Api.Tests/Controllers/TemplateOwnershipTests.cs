// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// A template belongs to no MSEL. The template lists ask the caller for nothing, so a MSEL's card, CITE
/// action, CITE duty, organization or Player application created as a template would publish its contents to anyone signed
/// in; the create is refused instead.
/// </summary>
public class TemplateOwnershipTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    [Theory]
    [InlineData("cards", SystemPermission.ManageGalleryCards)]
    [InlineData("citeActions", SystemPermission.ManageCiteActions)]
    [InlineData("citeDuties", SystemPermission.ManageCiteDuties)]
    [InlineData("organizations", SystemPermission.ManageOrganizations)]
    [InlineData("playerApplications", SystemPermission.ManagePlayerApplications)]
    public async Task Create_ForAMselAsATemplate_Is409AndCreatesNothing(string route, SystemPermission templatePermission)
    {
        var msel = TestData.Msel();
        await Seed(msel);
        // both tiers, so the refusal is the template rule and not a missing permission
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels, templatePermission).SeedAsync();
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
        Assert.False(await db.PlayerApplications.AnyAsync(x => x.Id == id, Ct));
    }

    [Theory]
    [InlineData("cards", SystemPermission.ManageGalleryCards)]
    [InlineData("citeActions", SystemPermission.ManageCiteActions)]
    [InlineData("citeDuties", SystemPermission.ManageCiteDuties)]
    [InlineData("organizations", SystemPermission.ManageOrganizations)]
    [InlineData("playerApplications", SystemPermission.ManagePlayerApplications)]
    public async Task Create_AsATemplateWithNoMsel_Is201(string route, SystemPermission templatePermission)
    {
        var actor = await Actor().WithSystemPermissions(templatePermission).SeedAsync();

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
