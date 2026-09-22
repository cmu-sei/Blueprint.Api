// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>MselPageService</c> / <c>MselPageController</c> - the five routes over the free-text pages a MSEL
/// carries alongside its timeline. Reads resolve <c>ViewMsels</c> and <c>CreateMsels</c>; writes want
/// <c>EditMsels</c> or the MSEL's <c>Owner</c> role.
/// </summary>
/// <remarks>
/// 401 and 403 for these routes live in <see cref="RouteAuthorizationTests"/>; this file covers the happy
/// path and the not-found path per route, per the thin protocol.
/// <para />
/// Each of the three requirement helpers in the Phase 2 table is asked by one method here, and they
/// disagree: listing a MSEL's pages demands <c>MselOwnerRequirement</c>, reading one page demands
/// <c>MselViewRequirement</c>, and reading an <c>AllCanView</c> page demands <c>MselUserRequirement</c>
/// <b>and ignores every system permission</b>. So "all can view" is the narrowest of the three.
/// </remarks>
public class MselPageEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET msels/{mselId}/mselpages
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByMsel_ForAnOwnerOfTheMsel_ReturnsOnlyThatMselsPages()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var otherMsel = await SeedMsel();
        await Seed(
            BlueprintAppFactory.MselPage(msel.Id, "mine"),
            BlueprintAppFactory.MselPage(otherMsel.Id, "theirs"));

        var response = await Client(actor).GetAsync($"api/msels/{msel.Id}/mselpages", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<List<ViewModels.MselPage>>(JsonOptions, Ct);
        Assert.Single(list);
        Assert.Equal("mine", list[0].Name);
    }

    /// <remarks>
    /// BUG: the list route demands <c>MselOwnerRequirement</c> (<c>MselPageService.cs:52</c>) where the
    /// single read demands only <c>MselViewRequirement</c>, so an ordinary viewer of the MSEL cannot
    /// discover that it has any pages and can read every one of them by id. Both halves are asserted
    /// here.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_ForAViewerOfTheMsel_Is403_ThoughTheyMayReadTheSamePageById()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();
        var page = BlueprintAppFactory.MselPage(msel.Id);
        await Seed(page);

        var listed = await Client(actor).GetAsync($"api/msels/{msel.Id}/mselpages", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, listed.StatusCode);

        var read = await Client(actor).GetAsync($"api/mselpages/{page.Id}", Ct);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor)
            .GetAsync($"api/msels/{Guid.NewGuid()}/mselpages", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // GET mselpages/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ForAViewerOfTheMsel_Is200()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();
        var page = BlueprintAppFactory.MselPage(msel.Id, "briefing", "<p>read me</p>");
        await Seed(page);

        var response = await Client(actor).GetAsync($"api/mselpages/{page.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answered = await response.Content.ReadFromJsonAsync<ViewModels.MselPage>(JsonOptions, Ct);
        Assert.Equal(page.Id, answered.Id);
        Assert.Equal(msel.Id, answered.MselId);
        Assert.Equal("briefing", answered.Name);
        Assert.Equal("<p>read me</p>", answered.Content);
        Assert.False(answered.AllCanView);
    }

    /// <remarks>
    /// BUG: the <c>AllCanView</c> branch (<c>MselPageService.cs:70-74</c>) asks
    /// <c>MselUserRequirement</c> and <b>never looks at <c>hasSystemPermission</c></b>, so marking a page
    /// "all can view" makes it <i>unreadable</i> by a <c>ViewMsels</c> holder who is not on one of the
    /// MSEL's teams or units - the opposite of what the flag says. The ordinary page beside it is a 200
    /// for the same caller.
    /// </remarks>
    [Fact]
    public async Task Get_ForAnAllCanViewPage_Is403ForAViewMselsHolderWhoIsNotOnTheMsel()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();
        var msel = await SeedMsel();
        var open = BlueprintAppFactory.MselPage(msel.Id, "open", allCanView: true);
        var ordinary = BlueprintAppFactory.MselPage(msel.Id, "ordinary");
        await Seed(open, ordinary);

        var refused = await Client(actor).GetAsync($"api/mselpages/{open.Id}", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var allowed = await Client(actor).GetAsync($"api/mselpages/{ordinary.Id}", Ct);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    /// <remarks>
    /// BUG: both reads report a missing <i>page</i> as <c>EntityNotFoundException&lt;MselEntity&gt;</c>
    /// (<c>MselPageService.cs:48</c> and <c>:66</c>), so the answer is indistinguishable from the MSEL
    /// being gone. The service's own update and delete name <c>MselPage</c> correctly.
    /// </remarks>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is404_ThatNamesTheMselRatherThanThePage()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"api/mselpages/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>(JsonOptions, Ct);
        Assert.Equal("Msel Entity not found", error.title);
    }

    // ---------------------------------------------------------------------------------------------
    // POST mselpages
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_ForAnOwnerOfTheMsel_Is201()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "api/mselpages", Body(msel.Id) with { name = "created" }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ViewModels.MselPage>(JsonOptions, Ct);
        Assert.Equal(msel.Id, created.MselId);
        Assert.EndsWith($"/api/mselpages/{created.Id}", response.Headers.Location.ToString());

        var stored = await NewContext().MselPages.SingleAsync(x => x.Id == created.Id, Ct);
        Assert.Equal("created", stored.Name);
    }

    /// <remarks>
    /// BUG: <c>CreateAsync</c> returns through <c>GetAsync</c>, which applies the read rules to what was
    /// just written - and the <c>AllCanView</c> branch ignores the <c>true</c> it is handed. So an
    /// <c>EditMsels</c> holder creating an "all can view" page is answered 403 <b>with the row already
    /// saved and already broadcast as created</b>, and has no id for it. <c>UpdateAsync</c> returns
    /// through the same read, so editing one is the same 403 after the write.
    /// </remarks>
    [Fact]
    public async Task Create_OfAnAllCanViewPage_WithEditMsels_Is403_WithTheRowAlreadySaved()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var msel = await SeedMsel();

        var response = await Client(actor).PostAsJsonAsync(
            "api/mselpages", Body(msel.Id) with { name = "open", allCanView = true }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var stored = Assert.Single(await NewContext().MselPages.ToListAsync(Ct));
        Assert.Equal("open", stored.Name);
        Assert.True(stored.AllCanView);
    }

    [Fact]
    public async Task Create_ForAMselThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "api/mselpages", Body(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT mselpages/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_ForAnOwnerOfTheMsel_Is200()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var page = BlueprintAppFactory.MselPage(msel.Id, "before");
        await Seed(page);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/mselpages/{page.Id}",
            Body(msel.Id) with { id = page.Id, name = "after", includeInPlaybook = true },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await NewContext().MselPages.SingleAsync(x => x.Id == page.Id, Ct);
        Assert.Equal("after", stored.Name);
        Assert.True(stored.IncludeInPlaybook);
    }

    [Fact]
    public async Task Update_ForAnIdThatIsNotThere_Is404()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var id = Guid.NewGuid();

        var response = await Client(actor).PutAsJsonAsync(
            $"api/mselpages/{id}", Body(msel.Id) with { id = id }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <remarks>
    /// BUG: <c>UpdateAsync</c> decides from the request body's <c>MselId</c> before the lookup
    /// (<c>MselPageService.cs:108</c>) and the profile maps that id both ways, so an owner of any MSEL may
    /// edit and steal every other MSEL's pages. Twelfth instance of the shape; <c>DeleteAsync</c>
    /// twenty lines below reads the stored row first, which is half the fix.
    /// </remarks>
    [Fact]
    public async Task Update_DecidesFromTheRequestBodyAndStealsTheRow()
    {
        var mine = await SeedMsel();
        var actor = await Actor().OnMsel(mine, MselRole.Owner).SeedAsync();
        var theirs = await SeedMsel();
        var page = BlueprintAppFactory.MselPage(theirs.Id);
        await Seed(page);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/mselpages/{page.Id}",
            Body(mine.Id) with { id = page.Id, name = "stolen" },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await NewContext().MselPages.SingleAsync(x => x.Id == page.Id, Ct);
        Assert.Equal(mine.Id, stored.MselId);
        Assert.Equal("stolen", stored.Name);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE mselpages/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_ForAnOwnerOfTheMsel_Is204()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var page = BlueprintAppFactory.MselPage(msel.Id);
        await Seed(page);

        var response = await Client(actor).DeleteAsync($"api/mselpages/{page.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await NewContext().MselPages.ToListAsync(Ct));
    }

    [Fact]
    public async Task Delete_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync($"api/mselpages/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<MselEntity> SeedMsel()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        return msel;
    }

    /// <remarks>
    /// <c>ViewModels.MselPage</c> does not derive from <c>Base</c> and <c>MselPageEntity</c> has no audit
    /// columns, so nothing records who wrote a MSEL's briefing pages or when.
    /// </remarks>
    private static MselPageBody Body(Guid mselId) =>
        new() { mselId = mselId, name = "seeded", content = "<p>seeded</p>" };

    private record MselPageBody
    {
        public Guid id { get; init; }
        public Guid mselId { get; init; }
        public string name { get; init; }
        public string content { get; init; }
        public bool allCanView { get; init; }
        public bool includeInPlaybook { get; init; }
    }

    /// <remarks>
    /// The shape <c>JsonExceptionFilter</c> answers with - <c>ViewModels.ApiError</c>, read here as a
    /// record so a test can assert which entity a 404 names.
    /// </remarks>
    private record ApiErrorBody
    {
        public int status { get; init; }
        public string title { get; init; }
        public string detail { get; init; }
    }
}
