// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using Blueprint.Api.Tests.Infrastructure;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// The MSEL read, create, update, delete and role endpoints, driven over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// An MSEL is the object the whole application is about - every other entity hangs off one - and
/// <c>MselService</c> is 2641 lines. This class covers the part of it a client touches on every page
/// load: the four list endpoints, the single read, create, update, delete, and the two role routes. The
/// xlsx and JSON import/export, the copy graph, the invitation joins and the integration pushes are
/// large enough to be worth a class each and are covered separately.
/// </para>
/// <para>
/// Authorization here is decided twice, and the two disagree in ways worth reading before adding a test:
/// the controller resolves a coarse <see cref="SystemPermission"/> and passes it down as a boolean, and
/// then the service falls back to <c>MselOwnerRequirement</c> - <em>owner</em>, not editor - for every
/// write. So an actor holding <see cref="MselRole.Editor"/> on an MSEL cannot update it, and the four
/// read endpoints each answer to a different rule.
/// </para>
/// <para>
/// Several tests characterize behaviour that is wrong rather than fixing it, per this branch's rule. Each
/// says so and says what fixing it does to the test. The worst of them is the pair of role routes, which
/// cannot work at all: the controller passes the user id and the MSEL id in the wrong order.
/// </para>
/// </remarks>
public class MselEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET msels
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_WithViewMselsPermission_ReturnsEveryMsel()
    {
        var mine = BlueprintAppFactory.Msel();
        var somebodyElses = BlueprintAppFactory.Msel();
        await Seed(mine, somebodyElses);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var returned = await GetMsels(Client(actor), "/api/msels");

        Assert.Equal(
            [.. new[] { mine.Id, somebodyElses.Id }.Order()],
            returned.Select(x => x.Id).Order());
    }

    /// <summary>
    /// Unlike <c>my-msels</c>, this one has no fallback: it is the administrative list, and a caller
    /// without <see cref="SystemPermission.ViewMsels"/> is refused whatever roles they hold.
    /// </summary>
    [Fact]
    public async Task Get_WithoutViewMselsPermission_Is403()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await Client(actor).GetAsync("/api/msels", Ct)).StatusCode);
    }

    [Fact]
    public async Task Get_ReturnsArchivedMselsToo()
    {
        var archived = BlueprintAppFactory.Msel(status: MselItemStatus.Archived);
        await Seed(archived);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Equal(archived.Id, Assert.Single(await GetMsels(Client(actor), "/api/msels")).Id);
    }

    [Fact]
    public async Task Get_FilteredByUserId_ReturnsOnlyThatUsersCreations()
    {
        var creator = Guid.NewGuid();
        var theirs = BlueprintAppFactory.Msel(createdBy: creator);
        await Seed(theirs, BlueprintAppFactory.Msel());

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var returned = await GetMsels(Client(actor), $"/api/msels?userId={creator}");

        Assert.Equal(theirs.Id, Assert.Single(returned).Id);
    }

    /// <summary>
    /// A <c>userId</c> that is not a guid is not rejected: it is silently read as
    /// <see cref="Guid.Empty"/> and the filter becomes "created by nobody".
    /// </summary>
    /// <remarks>
    /// Characterization. <c>MselService.GetAsync</c> discards the result of <c>Guid.TryParse</c> and uses
    /// the <c>out</c> value regardless, so <c>?userId=nonsense</c> is answered 200 with an unrelated set
    /// rather than 400. The query parameter is typed <c>string</c> for exactly this reason - typing it
    /// <c>Guid?</c> would make model binding answer 400 for free. Turns red when either happens.
    /// </remarks>
    [Fact]
    public async Task Get_FilteredByAnUnparseableUserId_FiltersOnAnEmptyGuid()
    {
        var orphan = BlueprintAppFactory.Msel();
        orphan.CreatedBy = Guid.Empty;
        await Seed(orphan, BlueprintAppFactory.Msel());

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var returned = await GetMsels(Client(actor), "/api/msels?userId=not-a-guid");

        Assert.Equal(orphan.Id, Assert.Single(returned).Id);
    }

    [Fact]
    public async Task Get_FilteredByDescription_MatchesASubstring()
    {
        var hurricane = BlueprintAppFactory.Msel();
        hurricane.Description = "Hurricane response, 2026";
        var wildfire = BlueprintAppFactory.Msel();
        wildfire.Description = "Wildfire response, 2026";
        await Seed(hurricane, wildfire);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var returned = await GetMsels(Client(actor), "/api/msels?description=Hurricane");

        Assert.Equal(hurricane.Id, Assert.Single(returned).Id);
    }

    /// <summary>
    /// The description filter is case-sensitive.
    /// </summary>
    /// <remarks>
    /// Characterization of a search a user would call broken. <c>string.Contains</c> translates to
    /// PostgreSQL's <c>strpos</c>, which respects case, so the UI's search box finds nothing unless the
    /// capitalisation matches. Turns red when the filter is made case-insensitive - with
    /// <c>EF.Functions.ILike</c> or a <c>StringComparison</c> overload.
    /// </remarks>
    [Fact]
    public async Task Get_FilteredByDescription_IsCaseSensitive()
    {
        var msel = BlueprintAppFactory.Msel();
        msel.Description = "Hurricane response";
        await Seed(msel);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Empty(await GetMsels(Client(actor), "/api/msels?description=hurricane"));
    }

    /// <summary>
    /// And case-sensitive along the other of the two code paths the description filter has.
    /// </summary>
    /// <remarks>
    /// Characterization of the duplication as much as of the case sensitivity: <c>GetAsync</c> writes the
    /// <c>Description.Contains</c> predicate twice - once for a description on its own and once for a
    /// description combined with another filter - so a fix applied to one copy leaves the other behind.
    /// This test and <see cref="Get_FilteredByDescription_IsCaseSensitive"/> pin one copy each, and the
    /// mutation check for this class found the pair by reddening only one of them. Turns red with its
    /// sibling, and only if both copies are fixed.
    /// </remarks>
    [Fact]
    public async Task Get_FilteredByUserIdAndDescription_IsAlsoCaseSensitive()
    {
        var creator = Guid.NewGuid();
        var msel = BlueprintAppFactory.Msel(createdBy: creator);
        msel.Description = "Hurricane response";
        await Seed(msel);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Empty(await GetMsels(
            Client(actor), $"/api/msels?userId={creator}&description=hurricane"));
    }

    [Fact]
    public async Task Get_FilteredByTeamId_ReturnsTheTeamsMsel()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel, BlueprintAppFactory.Msel());
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var returned = await GetMsels(Client(actor), $"/api/msels?teamId={team.Id}");

        Assert.Equal(msel.Id, Assert.Single(returned).Id);
    }

    /// <summary>
    /// An unknown team id is a 500.
    /// </summary>
    /// <remarks>
    /// Characterization. <c>MselService.GetAsync</c> dereferences the result of
    /// <c>SingleOrDefaultAsync</c> to read <c>MselId</c>, so a team that is not there is a
    /// <see cref="NullReferenceException"/> rather than an empty list or a 404. The same line accepts an
    /// unparseable <c>teamId</c> as <see cref="Guid.Empty"/> and reaches the same place. Turns red when
    /// the lookup is guarded.
    /// </remarks>
    [Fact]
    public async Task Get_FilteredByAnUnknownTeamId_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Equal(
            HttpStatusCode.InternalServerError,
            (await Client(actor).GetAsync($"/api/msels?teamId={Guid.NewGuid()}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Get_FilteredByUserIdAndDescription_AppliesBoth()
    {
        var creator = Guid.NewGuid();

        var wanted = BlueprintAppFactory.Msel(createdBy: creator);
        wanted.Description = "Hurricane response";
        var wrongDescription = BlueprintAppFactory.Msel(createdBy: creator);
        wrongDescription.Description = "Wildfire response";
        var wrongCreator = BlueprintAppFactory.Msel();
        wrongCreator.Description = "Hurricane response";

        await Seed(wanted, wrongDescription, wrongCreator);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var returned = await GetMsels(
            Client(actor), $"/api/msels?userId={creator}&description=Hurricane");

        Assert.Equal(wanted.Id, Assert.Single(returned).Id);
    }

    /// <summary>
    /// Enum values go out as names, which the checked-in <c>blueprint.ui</c> client depends on.
    /// </summary>
    /// <remarks>
    /// Asserted against the raw response rather than through <c>JsonOptions</c>: deserializing with the
    /// application's own options would follow the wire format wherever it went, and the UI would not.
    /// </remarks>
    [Fact]
    public async Task Get_SerializesTheStatusAsAName()
    {
        await Seed(BlueprintAppFactory.Msel(status: MselItemStatus.Approved));

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var body = await Client(actor).GetStringAsync("/api/msels", Ct);

        Assert.Contains("\"status\":\"Approved\"", body);
    }

    // ---------------------------------------------------------------------------------------------
    // GET my-msels
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task MyMsels_ReturnsAnMselTheCallersUnitIsAssignedTo()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel, BlueprintAppFactory.Msel());

        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var returned = await GetMsels(Client(actor), "/api/my-msels");

        Assert.Equal(msel.Id, Assert.Single(returned).Id);
    }

    [Fact]
    public async Task MyMsels_ReturnsAnMselTheCallerCreated()
    {
        var actorId = Guid.NewGuid();
        var msel = BlueprintAppFactory.Msel(createdBy: actorId);
        await Seed(msel);

        var actor = await Actor().WithId(actorId).SeedAsync();

        var returned = await GetMsels(Client(actor), "/api/my-msels");

        Assert.Equal(msel.Id, Assert.Single(returned).Id);
    }

    /// <summary>
    /// Team membership is not a route into this list - only a unit assignment or having created the MSEL
    /// is - even though <c>MselViewRequirement</c> takes a team member as able to view.
    /// </summary>
    [Fact]
    public async Task MyMsels_DoesNotReturnAnMselTheCallerIsOnlyOnATeamOf()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().OnTeam(team).SeedAsync();

        Assert.Empty(await GetMsels(Client(actor), "/api/my-msels"));
    }

    [Fact]
    public async Task MyMsels_ExcludesArchivedMsels()
    {
        var archived = BlueprintAppFactory.Msel(status: MselItemStatus.Archived);
        await Seed(archived);

        var actor = await Actor().OnMsel(archived, MselRole.Owner).SeedAsync();

        Assert.Empty(await GetMsels(Client(actor), "/api/my-msels"));
    }

    /// <summary>
    /// Somebody else's templates are included for a caller who could copy one.
    /// </summary>
    [Theory]
    [InlineData(SystemPermission.ViewMsels)]
    [InlineData(SystemPermission.CreateMsels)]
    public async Task MyMsels_WithAViewOrCreatePermission_IncludesOtherPeoplesTemplates(
        SystemPermission permission)
    {
        var template = BlueprintAppFactory.Msel(isTemplate: true);
        await Seed(template);

        var actor = await Actor().WithSystemPermissions(permission).SeedAsync();

        var returned = await GetMsels(Client(actor), "/api/my-msels");

        Assert.Equal(template.Id, Assert.Single(returned).Id);
    }

    [Fact]
    public async Task MyMsels_WithNoPermission_DoesNotIncludeOtherPeoplesTemplates()
    {
        await Seed(BlueprintAppFactory.Msel(isTemplate: true));

        var actor = await Actor().SeedAsync();

        Assert.Empty(await GetMsels(Client(actor), "/api/my-msels"));
    }

    /// <summary>
    /// A caller assigned to a template through a unit cannot see it, whatever role they hold on it.
    /// </summary>
    /// <remarks>
    /// Characterization, and the first half of the strangest rule in <c>GetUserMselsAsync</c>: after
    /// building the list it drops every template unless the caller holds a view or create permission
    /// <em>or</em> created a non-template MSEL of their own. An administrator who assigns somebody's unit
    /// to a template MSEL and gives them a role on it has done nothing they can observe. Turns red when
    /// the filter stops discarding MSELs the caller has an explicit role on - see
    /// <see cref="MyMsels_WithNoPermission_KeepsTheTemplateWhenTheCallerAlsoCreatedAnMsel"/> for the
    /// other half.
    /// </remarks>
    [Fact]
    public async Task MyMsels_WithNoPermission_DropsATemplateTheCallersUnitIsAssignedTo()
    {
        var template = BlueprintAppFactory.Msel(isTemplate: true);
        await Seed(template);

        var actor = await Actor().OnMsel(template, MselRole.Owner).SeedAsync();

        Assert.Empty(await GetMsels(Client(actor), "/api/my-msels"));
    }

    /// <summary>
    /// Creating an unrelated MSEL makes the template above visible.
    /// </summary>
    /// <remarks>
    /// Characterization, and the other half. The condition is
    /// <c>!myMselList.Any(m =&gt; !m.IsTemplate)</c>, so one non-template MSEL created by the caller -
    /// which has nothing to do with the template, or with the unit it was reached through - switches the
    /// whole filter off. Two callers with identical roles on the template therefore get different
    /// answers. Turns red with the test above.
    /// </remarks>
    [Fact]
    public async Task MyMsels_WithNoPermission_KeepsTheTemplateWhenTheCallerAlsoCreatedAnMsel()
    {
        var template = BlueprintAppFactory.Msel(isTemplate: true);
        await Seed(template);

        var actorId = Guid.NewGuid();
        var ownMsel = BlueprintAppFactory.Msel(createdBy: actorId);
        await Seed(ownMsel);

        var actor = await Actor().WithId(actorId).OnMsel(template, MselRole.Owner).SeedAsync();

        var returned = await GetMsels(Client(actor), "/api/my-msels");

        Assert.Equal(
            [.. new[] { ownMsel.Id, template.Id }.Order()],
            returned.Select(x => x.Id).Order());
    }

    /// <summary>
    /// The creator is reported as an owner without a row saying so, which is how the UI shows an owner
    /// nobody can accidentally delete.
    /// </summary>
    [Fact]
    public async Task MyMsels_ReportsTheCreatorAsAnOwnerWithoutARow()
    {
        var actorId = Guid.NewGuid();
        var msel = BlueprintAppFactory.Msel(createdBy: actorId);
        await Seed(msel);

        var actor = await Actor().WithId(actorId).SeedAsync();

        var returned = Assert.Single(await GetMsels(Client(actor), "/api/my-msels"));
        var role = Assert.Single(returned.UserMselRoles);

        Assert.Equal(MselRole.Owner, role.Role);
        Assert.Equal(actorId, role.UserId);
        Assert.Equal(Guid.Empty, role.Id);
        Assert.False(await NewContext().UserMselRoles.AnyAsync(x => x.MselId == msel.Id, Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // GET users/{userId}/msels
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task UserMsels_ForTheCallerThemselves_IsAllowedWithNoPermission()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var returned = await GetMsels(Client(actor), $"/api/users/{actor.Id}/msels");

        Assert.Equal(msel.Id, Assert.Single(returned).Id);
    }

    [Fact]
    public async Task UserMsels_ForSomebodyElse_WithoutManageUsers_Is403()
    {
        var subject = await Actor().SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await Client(actor).GetAsync($"/api/users/{subject.Id}/msels", Ct)).StatusCode);
    }

    [Fact]
    public async Task UserMsels_ForSomebodyElse_WithManageUsers_ReturnsTheirMsels()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var subject = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var returned = await GetMsels(Client(actor), $"/api/users/{subject.Id}/msels");

        Assert.Equal(msel.Id, Assert.Single(returned).Id);
    }

    /// <summary>
    /// The template part of the answer is decided by the <em>caller's</em> permissions, not the subject's.
    /// </summary>
    /// <remarks>
    /// Characterization. <c>MselController.GetUserMsels</c> resolves <c>ViewMsels</c> and
    /// <c>CreateMsels</c> for whoever is asking and passes them down as the subject's, so an
    /// administrator asking "what can this user see" is told something the user cannot see. Turns red
    /// when the flags are resolved for <c>userId</c>.
    /// </remarks>
    [Fact]
    public async Task UserMsels_ForSomebodyElse_AnswersWithTheCallersTemplateVisibility()
    {
        await Seed(BlueprintAppFactory.Msel(isTemplate: true));

        var subject = await Actor().SeedAsync();
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        Assert.Single(await GetMsels(Client(actor), $"/api/users/{subject.Id}/msels"));
        Assert.Empty(await GetMsels(Client(subject), "/api/my-msels"));
    }

    [Fact]
    public async Task UserMsels_ForAUserWithNoRow_IsAnEmptyArray()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        Assert.Empty(await GetMsels(Client(actor), $"/api/users/{Guid.NewGuid()}/msels"));
    }

    // ---------------------------------------------------------------------------------------------
    // GET msels/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetById_AsAViewer_ReturnsIt()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        Assert.Equal(msel.Id, (await GetMsel(Client(actor), msel.Id)).Id);
    }

    [Fact]
    public async Task GetById_WithViewMselsPermissionAndNoRole_ReturnsIt()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Equal(msel.Id, (await GetMsel(Client(actor), msel.Id)).Id);
    }

    [Fact]
    public async Task GetById_WithNoRoleOrPermission_Is403()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().SeedAsync();

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await Client(actor).GetAsync($"/api/msels/{msel.Id}", Ct)).StatusCode);
    }

    /// <summary>
    /// A template is readable by any authenticated caller, which is what makes the copy-from-template
    /// flow work without granting a permission first.
    /// </summary>
    [Fact]
    public async Task GetById_ForATemplate_WithNoRoleOrPermission_ReturnsIt()
    {
        var template = BlueprintAppFactory.Msel(isTemplate: true);
        await Seed(template);

        var actor = await Actor().SeedAsync();

        Assert.Equal(template.Id, (await GetMsel(Client(actor), template.Id)).Id);
    }

    /// <summary>
    /// An unknown id is a 500 for a caller with no permission.
    /// </summary>
    /// <remarks>
    /// Characterization. The refusal branch reads <c>mselCheck.IsTemplate</c> off an unguarded
    /// <c>FindAsync</c>, so a caller who cannot view an MSEL learns whether it exists from the status
    /// code - 403 when it does, 500 when it does not. Turns red when the lookup is guarded, which should
    /// make both cases 403 or both 404.
    /// </remarks>
    [Fact]
    public async Task GetById_ForAnUnknownMsel_WithNoPermission_Is500()
    {
        var actor = await Actor().SeedAsync();

        Assert.Equal(
            HttpStatusCode.InternalServerError,
            (await Client(actor).GetAsync($"/api/msels/{Guid.NewGuid()}", Ct)).StatusCode);
    }

    /// <summary>
    /// An unknown id is a 500 for a caller who holds <see cref="SystemPermission.ViewMsels"/> too - by a
    /// different route from the test above.
    /// </summary>
    /// <remarks>
    /// Characterization. This half of <c>GetAsync</c> maps the null entity happily and then reads
    /// <c>msel.UseGallery</c> off the result to decide whether to fill the Gallery lists, so the one
    /// endpoint the UI loads an MSEL through cannot say "gone" whoever asks. Every other read on this
    /// controller throws <c>EntityNotFoundException</c>. Turns red when this one does too - and note that
    /// fixing only the <c>UseGallery</c> dereference would leave it answering 200 with a null body, which
    /// is not the fix.
    /// </remarks>
    [Fact]
    public async Task GetById_ForAnUnknownMsel_WithViewMselsPermission_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task GetById_IncludesTheUnitsFromTheJoinRows()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var returned = await GetMsel(Client(actor), msel.Id);

        Assert.Equal(actor.MselRole.UnitId, Assert.Single(returned.Units).Id);
    }

    /// <summary>
    /// Only the caller's own roles come back, so one member of an MSEL cannot enumerate the others'
    /// roles from this endpoint.
    /// </summary>
    [Fact]
    public async Task GetById_IncludesOnlyTheCallersOwnRoles()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var other = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var returned = await GetMsel(Client(actor), msel.Id);

        // The MSEL's creator is added to the view model without a row, so they are here too.
        Assert.Equal(
            [.. new[] { (actor.Id, MselRole.Viewer), (msel.CreatedBy, MselRole.Owner) }
                .OrderBy(x => x.Item1)],
            returned.UserMselRoles.Select(x => (x.UserId, x.Role)).OrderBy(x => x.UserId));
        Assert.DoesNotContain(other.Id, returned.UserMselRoles.Select(x => x.UserId));
    }

    /// <summary>
    /// The two Gallery lists are filled in by the service after mapping, and only when the MSEL uses
    /// Gallery.
    /// </summary>
    /// <remarks>
    /// They have no source on the entity at all - see
    /// <c>MappingConfigurationTests.Map_MselEntityToMsel_LeavesTheGalleryListsEmpty</c> - so this
    /// endpoint and <c>MselHandler</c> each fill them from <c>Enum.GetNames</c> by hand. A third caller
    /// of the mapper would silently ship empty lists.
    /// </remarks>
    [Fact]
    public async Task GetById_ForAGalleryMsel_IncludesTheGalleryParameterNames()
    {
        var msel = BlueprintAppFactory.Msel();
        msel.UseGallery = true;
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var returned = await GetMsel(Client(actor), msel.Id);

        Assert.Equal(
            [.. Enum.GetNames<GalleryArticleParameter>()],
            returned.GalleryArticleParameters);
        Assert.Equal([.. Enum.GetNames<GallerySourceType>()], returned.GallerySourceTypes);
    }

    [Fact]
    public async Task GetById_ForANonGalleryMsel_LeavesTheGalleryListsEmpty()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var returned = await GetMsel(Client(actor), msel.Id);

        Assert.Empty(returned.GalleryArticleParameters);
        Assert.Empty(returned.GallerySourceTypes);
    }

    // ---------------------------------------------------------------------------------------------
    // GET msels/{id}/data
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The endpoint cannot succeed for any MSEL, for any caller: its return type cannot be serialized.
    /// </summary>
    /// <remarks>
    /// Characterization, and the endpoint is dead rather than merely wrong.
    /// <c>MselService.GetDataTableAsync</c> returns a <see cref="System.Data.DataTable"/>, whose
    /// <c>Columns[].DataType</c> is a <see cref="Type"/> - and System.Text.Json, which <c>Startup</c>
    /// configures and which has been the only serializer since this endpoint was written, refuses
    /// <see cref="Type"/> outright. So every call 500s while formatting the result, whatever the MSEL
    /// holds; the exact path is asserted because it is what says why. Two consequences worth reading: the
    /// failure happens after the action returned, so <c>JsonExceptionFilter</c> never sees it and the body
    /// is not an <c>ApiError</c> but a bare stack trace; and the 404 path below is the only branch of this
    /// endpoint that has ever worked. Turns red when the service returns something serializable - the
    /// row-and-column shape the xlsx export already builds would do.
    /// </remarks>
    [Fact]
    public async Task GetData_ForAnyMsel_Is500_BecauseADataTableCannotBeSerialized()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/data", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.StartsWith("System.NotSupportedException", body);
        Assert.Contains(
            "Serialization and deserialization of 'System.Type' instances is not supported. " +
            "Path: $.Columns.DataType.",
            body);
    }

    /// <summary>
    /// A caller with no role on the MSEL and no permission at all gets as far as the serializer.
    /// </summary>
    /// <remarks>
    /// Characterization of a missing authorization check, asserted through the defect above: reaching the
    /// 500 is proof the request was never refused. <c>MselController.GetData</c> asks
    /// <c>IBlueprintAuthorizationService</c> nothing and <c>GetDataTableAsync</c> checks no requirement,
    /// so were the serialization fixed, this endpoint would hand every scenario event and data value of
    /// any MSEL to any authenticated user who knows its id. Turns red when a check is added -
    /// <c>MselViewRequirement</c> is the one the sibling read uses - and then it should be a 403.
    /// </remarks>
    [Fact]
    public async Task GetData_WithNoRoleOrPermission_IsNotForbidden()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().SeedAsync();

        Assert.Equal(
            HttpStatusCode.InternalServerError,
            (await Client(actor).GetAsync($"/api/msels/{msel.Id}/data", Ct)).StatusCode);
    }

    /// <remarks>
    /// The one branch of this endpoint that answers correctly, and the second half of the evidence that
    /// it is ungated: an unknown MSEL is a 404 to a caller holding nothing, where a gated endpoint would
    /// refuse before looking.
    /// </remarks>
    [Fact]
    public async Task GetData_ForAnUnknownMsel_WithNoRoleOrPermission_Is404()
    {
        var actor = await Actor().SeedAsync();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await Client(actor).GetAsync($"/api/msels/{Guid.NewGuid()}/data", Ct)).StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // POST msels
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_WithCreateMselsPermission_Is201()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/msels", new Msel { Name = "Hurricane response" }, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await Read<Msel>(response);

        Assert.Equal("Hurricane response", created.Name);
        Assert.True(await NewContext().Msels.AnyAsync(x => x.Id == created.Id, Ct));
    }

    [Fact]
    public async Task Create_PutsTheNewMselsRouteInTheLocationHeader()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/msels", new Msel { Name = "Hurricane response" }, JsonOptions, Ct);

        var created = await Read<Msel>(response);

        Assert.EndsWith($"/api/msels/{created.Id}", response.Headers.Location.ToString());
    }

    [Theory]
    [InlineData(SystemPermission.ViewMsels)]
    [InlineData(SystemPermission.EditMsels)]
    [InlineData(SystemPermission.ManageMsels)]
    public async Task Create_WithoutCreateMselsPermission_Is403(SystemPermission permission)
    {
        var actor = await Actor().WithSystemPermissions(permission).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/msels", new Msel { Name = "Hurricane response" }, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await NewContext().Msels.ToListAsync(Ct));
    }

    [Fact]
    public async Task Create_WithoutAName_Is400()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/msels", new Msel(), JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The exercise window is a start time plus a duration, so an end time before the start is exactly a
    /// negative duration and the view model's <c>[Range]</c> rejects it.
    /// </summary>
    [Fact]
    public async Task Create_WithANegativeDuration_Is400()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/msels",
            new Msel { Name = "Hurricane response", DurationSeconds = -1 },
            JsonOptions,
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("must not precede the start time", await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>
    /// Audit fields are stamped by the server whatever the request body says.
    /// </summary>
    /// <remarks>
    /// <c>BlueprintContext.SaveEntries</c> sets them on every save, so the hostile values below cannot
    /// reach the database. Both the controller and <c>CreateAsync</c> also overwrite <c>CreatedBy</c>
    /// with the caller's id first, which is belt and braces for the same thing.
    /// </remarks>
    [Fact]
    public async Task Create_StampsTheAuditFieldsOnTheServer()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var before = DateTime.UtcNow;

        var response = await Client(actor).PostAsJsonAsync(
            "/api/msels",
            new Msel
            {
                Name = "Hurricane response",
                CreatedBy = Guid.NewGuid(),
                DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ModifiedBy = Guid.NewGuid(),
                DateModified = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            JsonOptions,
            Ct);

        var created = await Read<Msel>(response);

        await using var context = NewContext();
        var stored = await context.Msels.AsNoTracking().SingleAsync(x => x.Id == created.Id, Ct);

        Assert.Equal(actor.Id, stored.CreatedBy);
        AssertStampedBetween(stored.DateCreated, before, DateTime.UtcNow);
        Assert.Null(stored.ModifiedBy);
        Assert.Null(stored.DateModified);
    }

    /// <summary>
    /// The client chooses the primary key when it sends one.
    /// </summary>
    /// <remarks>
    /// Characterization. <c>CreateAsync</c> keeps a non-empty <c>Id</c> from the request body, so two
    /// callers can collide on it - and a caller can probe for an id that already exists, because the
    /// collision comes back as a 500 rather than a 409. Turns red when the id is always minted by the
    /// server.
    /// </remarks>
    [Fact]
    public async Task Create_WithAnIdInTheBody_UsesIt()
    {
        var id = Guid.NewGuid();
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/msels", new Msel { Id = id, Name = "Hurricane response" }, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(id, (await Read<Msel>(response)).Id);
    }

    /// <remarks>
    /// Characterization, the other half of <see cref="Create_WithAnIdInTheBody_UsesIt"/>: the duplicate
    /// key surfaces as a <c>DbUpdateException</c>, which is not an <c>IApiException</c>, so
    /// <c>JsonExceptionFilter</c> answers 500. Turns red when the id is server-minted, or when the
    /// conflict is caught and answered 409.
    /// </remarks>
    [Fact]
    public async Task Create_WithAnIdThatAlreadyExists_Is500()
    {
        var existing = BlueprintAppFactory.Msel();
        await Seed(existing);

        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/msels", new Msel { Id = existing.Id, Name = "Hurricane response" }, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// Creating an MSEL does not write a role row for the creator, and the response says they own it
    /// anyway.
    /// </summary>
    [Fact]
    public async Task Create_ReportsTheCallerAsAnOwnerWithoutWritingARole()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/msels", new Msel { Name = "Hurricane response" }, JsonOptions, Ct);

        var created = await Read<Msel>(response);
        var role = Assert.Single(created.UserMselRoles);

        Assert.Equal(actor.Id, role.UserId);
        Assert.Equal(MselRole.Owner, role.Role);
        Assert.Empty(await NewContext().UserMselRoles.ToListAsync(Ct));
    }

    [Fact]
    public async Task Create_NotifiesTheMselGroupAndTheAdminGroup()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/msels", new Msel { Name = "Hurricane response" }, JsonOptions, Ct);

        var created = await Read<Msel>(response);

        Assert.Equal(
            [created.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.MselCreated));
    }

    // ---------------------------------------------------------------------------------------------
    // PUT msels/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_AsTheOwner_Is200()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var returned = await Update(Client(actor), msel.Id, Body(msel, name: "Renamed"));

        Assert.Equal("Renamed", returned.Name);
        Assert.Equal(
            "Renamed",
            (await NewContext().Msels.SingleAsync(x => x.Id == msel.Id, Ct)).Name);
    }

    [Fact]
    public async Task Update_AsTheCreator_Is200()
    {
        var actorId = Guid.NewGuid();
        var msel = BlueprintAppFactory.Msel(createdBy: actorId);
        await Seed(msel);

        var actor = await Actor().WithId(actorId).SeedAsync();

        Assert.Equal("Renamed", (await Update(Client(actor), msel.Id, Body(msel, "Renamed"))).Name);
    }

    [Fact]
    public async Task Update_WithEditMselsPermissionAndNoRole_Is200()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        Assert.Equal("Renamed", (await Update(Client(actor), msel.Id, Body(msel, "Renamed"))).Name);
    }

    /// <summary>
    /// An editor cannot edit.
    /// </summary>
    /// <remarks>
    /// Characterization of the branch that reads most wrongly in the service: <c>UpdateAsync</c> falls
    /// back to <c>MselOwnerRequirement</c>, so of the six <see cref="MselRole"/> values only
    /// <see cref="MselRole.Owner"/> reaches it. <see cref="MselRole.Editor"/> exists, is granted through
    /// the UI, and buys nothing here - the holder needs the system-wide
    /// <see cref="SystemPermission.EditMsels"/> instead, which grants them every MSEL in the
    /// installation. Turns red when the fallback admits editors.
    /// </remarks>
    [Theory]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.MoveEditor)]
    [InlineData(MselRole.Viewer)]
    [InlineData(MselRole.Evaluator)]
    public async Task Update_AsAnythingButAnOwner_Is403(MselRole role)
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Client(actor).PutAsJsonAsync(
            $"/api/msels/{msel.Id}", Body(msel, "Renamed"), JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Update_ForAnUnknownMsel_WithEditMselsPermission_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var id = Guid.NewGuid();
        var response = await Client(actor).PutAsJsonAsync(
            $"/api/msels/{id}", new Msel { Id = id, Name = "Renamed" }, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// An unknown id is a 500 for a caller without the permission.
    /// </summary>
    /// <remarks>
    /// Characterization. The fallback runs first and <c>MselOwnerRequirement.IsMet</c> dereferences the
    /// MSEL it did not find, so this never reaches the 404 above. Turns red when that helper guards its
    /// lookup - <c>MselOwnerRequirementTests</c> pins the same defect one layer down.
    /// </remarks>
    [Fact]
    public async Task Update_ForAnUnknownMsel_WithNoPermission_Is500()
    {
        var actor = await Actor().SeedAsync();

        var id = Guid.NewGuid();
        var response = await Client(actor).PutAsJsonAsync(
            $"/api/msels/{id}", new Msel { Id = id, Name = "Renamed" }, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// The body's <c>id</c> is mapped onto the tracked entity, so a body that omits it is a 500.
    /// </summary>
    /// <remarks>
    /// Characterization. <c>MselProfile</c>'s <c>Msel -&gt; MselEntity</c> map does not ignore
    /// <c>Id</c> - only <c>MselEntity -&gt; MselEntity</c> does - so <c>UpdateAsync</c> assigns the
    /// primary key of a tracked entity and EF Core refuses at save. The route id is therefore not
    /// authoritative: it decides which row is loaded and which permission is checked, and the body
    /// decides what is written. Turns red when the map ignores <c>Id</c>.
    /// </remarks>
    [Fact]
    public async Task Update_WithNoIdInTheBody_Is500()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).PutAsJsonAsync(
            $"/api/msels/{msel.Id}", new Msel { Name = "Renamed" }, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(
            msel.Name,
            (await NewContext().Msels.SingleAsync(x => x.Id == msel.Id, Ct)).Name);
    }

    /// <remarks>
    /// Characterization, the same defect reached from the other side: the body names a different MSEL and
    /// the write is refused rather than applied to it. Turns red with
    /// <see cref="Update_WithNoIdInTheBody_Is500"/>.
    /// </remarks>
    [Fact]
    public async Task Update_WithAnotherMselsIdInTheBody_Is500()
    {
        var msel = BlueprintAppFactory.Msel();
        var other = BlueprintAppFactory.Msel();
        await Seed(msel, other);

        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).PutAsJsonAsync(
            $"/api/msels/{msel.Id}", Body(other, "Renamed"), JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// A PUT is a whole-object replace: a field the body leaves at its default is written as the default.
    /// </summary>
    /// <remarks>
    /// Not a defect - it is what PUT means - but it is the shape of the two tests above, and it is why
    /// the UI has to round-trip an entire MSEL to rename one. A client sending only the fields it changed
    /// resets <c>Status</c> to <c>Pending</c>, clears every integration flag, and moves
    /// <c>StartTime</c> to the epoch.
    /// </remarks>
    [Fact]
    public async Task Update_WithAPartialBody_ResetsTheFieldsItOmits()
    {
        var msel = BlueprintAppFactory.Msel(status: MselItemStatus.Approved);
        msel.UseGallery = true;
        await Seed(msel);

        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var returned = await Update(
            Client(actor), msel.Id, new Msel { Id = msel.Id, Name = msel.Name });

        Assert.Equal(MselItemStatus.Pending, returned.Status);
        Assert.False(returned.UseGallery);
    }

    [Fact]
    public async Task Update_StampsTheAuditFieldsAndPreservesCreation()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var createdBy = msel.CreatedBy;
        var dateCreated = (await NewContext().Msels.SingleAsync(x => x.Id == msel.Id, Ct)).DateCreated;

        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var before = DateTime.UtcNow;

        var body = Body(msel, "Renamed");
        body.CreatedBy = Guid.NewGuid();
        body.DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        body.ModifiedBy = Guid.NewGuid();
        body.DateModified = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await Update(Client(actor), msel.Id, body);

        await using var context = NewContext();
        var stored = await context.Msels.AsNoTracking().SingleAsync(x => x.Id == msel.Id, Ct);

        Assert.Equal(createdBy, stored.CreatedBy);
        Assert.Equal(dateCreated, stored.DateCreated);
        Assert.Equal(actor.Id, stored.ModifiedBy);
        AssertStampedBetween(stored.DateModified, before, DateTime.UtcNow);
    }

    [Fact]
    public async Task Update_NotifiesTheMselGroupWithTheModifiedPropertyNames()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        await Update(Client(actor), msel.Id, Body(msel, "Renamed"));

        var send = Hub.Of(MainHubMethods.MselUpdated).First();

        Assert.Equal(msel.Id.ToString(), send.Group);
        Assert.Contains("name", (string[])send.Args[1]);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT msels/{mselId}/user/{userId}/role/{mselRole}/add and .../remove
    //
    // Both routes are broken in the same way, so both are characterized rather than covered: the
    // controller passes the ids to the service in the wrong order.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Granting a real user a role on a real MSEL is answered 404, and writes nothing.
    /// </summary>
    /// <remarks>
    /// Characterization of a defect that makes the endpoint unusable.
    /// <c>MselService.AddUserMselRoleAsync</c> is declared <c>(Guid mselId, Guid userId, …)</c> and
    /// <c>MselController.AddUserMselRole</c> calls it <c>(userId, mselId, …)</c>, so the service looks
    /// for an MSEL whose id is the user's and throws <c>EntityNotFoundException</c>. The only way to get
    /// a 200 is to write the URL backwards - see
    /// <see cref="AddUserRole_WithTheIdsSwappedInTheRoute_WritesTheRole"/>. Turns red when the arguments
    /// are put in the right order, which is when this endpoint starts working.
    /// </remarks>
    [Fact]
    public async Task AddUserRole_ForARealUserAndMsel_Is404()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var subject = await Actor().SeedAsync();
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Client(actor).PutAsync(
            $"/api/msels/{msel.Id}/user/{subject.Id}/role/Owner/add", null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await NewContext().UserMselRoles.ToListAsync(Ct));
    }

    /// <summary>
    /// Writing the URL backwards - the user's id where the route asks for an MSEL and the MSEL's id where
    /// it asks for a user - is answered 200 and writes the role correctly.
    /// </summary>
    /// <remarks>
    /// Characterization, and the proof of the transposition above: the service itself is right, and what
    /// reaches it is <c>(mselId: {userId route value}, userId: {mselId route value})</c>. Both halves of
    /// the swap are needed to get here - the role row's <c>UserId</c> has a foreign key to
    /// <c>Users</c>, so naming a second MSEL in the user slot is a 500 rather than a swapped write. Turns
    /// red with <see cref="AddUserRole_ForARealUserAndMsel_Is404"/>.
    /// </remarks>
    [Fact]
    public async Task AddUserRole_WithTheIdsSwappedInTheRoute_WritesTheRole()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var subject = await Actor().SeedAsync();
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Client(actor).PutAsync(
            $"/api/msels/{subject.Id}/user/{msel.Id}/role/Owner/add", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(msel.Id, (await Read<Msel>(response)).Id);

        var role = Assert.Single(await NewContext().UserMselRoles.ToListAsync(Ct));

        Assert.Equal(subject.Id, role.UserId);
        Assert.Equal(msel.Id, role.MselId);
        Assert.Equal(MselRole.Owner, role.Role);
        Assert.Equal(actor.Id, role.CreatedBy);
    }

    /// <remarks>
    /// Characterization. Reached through the swap above, because there is no other way to reach it: a
    /// duplicate role throws <c>ArgumentException</c>, which is not an <c>IApiException</c>, so
    /// <c>JsonExceptionFilter</c> answers 500 where the service plainly meant 400 or 409. Turns red when
    /// the exception becomes an <c>IApiException</c>.
    /// </remarks>
    [Fact]
    public async Task AddUserRole_ForARoleThatAlreadyExists_Is500()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var subject = await Actor().SeedAsync();
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();
        var route = $"/api/msels/{subject.Id}/user/{msel.Id}/role/Owner/add";

        Assert.Equal(
            HttpStatusCode.OK,
            (await Client(actor).PutAsync(route, null, Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.InternalServerError,
            (await Client(actor).PutAsync(route, null, Ct)).StatusCode);
    }

    /// <summary>
    /// Reached through the swap, so that the authorization the endpoint would apply if it worked is
    /// pinned rather than left to the transposition to hide: granting a role needs ownership of the MSEL,
    /// and an editor does not have it.
    /// </summary>
    [Fact]
    public async Task AddUserRole_WithoutEditMselsPermissionOrOwnership_Is403()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var response = await Client(actor).PutAsync(
            $"/api/msels/{actor.Id}/user/{msel.Id}/role/Owner/add", null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(await NewContext().UserMselRoles.ToListAsync(Ct));
    }

    /// <remarks>
    /// Characterization, the same transposition in <c>RemoveUserMselRole</c>. Removing a role a user
    /// really holds is answered 404 and the row survives. Turns red when the arguments are ordered
    /// correctly.
    /// </remarks>
    [Fact]
    public async Task RemoveUserRole_ForARoleTheUserReallyHolds_Is404AndKeepsIt()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var subject = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Client(actor).PutAsync(
            $"/api/msels/{msel.Id}/user/{subject.Id}/role/Owner/remove", null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(await NewContext().UserMselRoles.AnyAsync(
            x => x.UserId == subject.Id && x.MselId == msel.Id, Ct));
    }

    [Fact]
    public async Task RemoveUserRole_WithTheIdsSwappedInTheRoute_RemovesTheRole()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var subject = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Client(actor).PutAsync(
            $"/api/msels/{subject.Id}/user/{msel.Id}/role/Owner/remove", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await NewContext().UserMselRoles.ToListAsync(Ct));
    }

    /// <summary>
    /// An unknown role name is a routing failure rather than a 400, because the value is bound from the
    /// path.
    /// </summary>
    [Fact]
    public async Task AddUserRole_WithARoleThatIsNotAnMselRole_Is400()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Client(actor).PutAsync(
            $"/api/msels/{msel.Id}/user/{Guid.NewGuid()}/role/Sovereign/add", null, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE msels/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_AsTheOwner_Is204AndRemovesIt()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/msels/{msel.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await NewContext().Msels.ToListAsync(Ct));
    }

    [Fact]
    public async Task Delete_WithEditMselsPermissionAndNoRole_Is204()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await Client(actor).DeleteAsync($"/api/msels/{msel.Id}", Ct)).StatusCode);
    }

    /// <remarks>
    /// The same owner-only fallback as <see cref="Update_AsAnythingButAnOwner_Is403"/>, and worth its own
    /// test because deleting an MSEL destroys an exercise: an editor cannot do it, and anybody holding
    /// <see cref="SystemPermission.EditMsels"/> can do it to every MSEL in the installation.
    /// </remarks>
    [Fact]
    public async Task Delete_AsAnEditor_Is403()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await Client(actor).DeleteAsync($"/api/msels/{msel.Id}", Ct)).StatusCode);
        Assert.Single(await NewContext().Msels.ToListAsync(Ct));
    }

    [Fact]
    public async Task Delete_ForAnUnknownMsel_WithEditMselsPermission_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await Client(actor).DeleteAsync($"/api/msels/{Guid.NewGuid()}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Delete_CascadesToTheMselsTeamsAndUnitAssignments()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        await Seed(BlueprintAppFactory.Team(msel.Id));

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Client(actor).DeleteAsync($"/api/msels/{msel.Id}", Ct);

        await using var context = NewContext();

        Assert.Empty(await context.Teams.ToListAsync(Ct));
        Assert.Empty(await context.MselUnits.ToListAsync(Ct));
        Assert.Empty(await context.UserMselRoles.ToListAsync(Ct));
        // The unit itself is not owned by the MSEL, so it stays.
        Assert.Single(await context.Units.ToListAsync(Ct));
    }

    [Fact]
    public async Task Delete_NotifiesTheMselGroupAndTheAdminGroupWithTheId()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Client(actor).DeleteAsync($"/api/msels/{msel.Id}", Ct);

        Assert.Equal(
            [msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.MselDeleted));
        Assert.Equal(msel.Id, Hub.Of(MainHubMethods.MselDeleted).First().Payload);
    }

    // ---------------------------------------------------------------------------------------------
    // Authentication
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Every route on this controller is behind the MVC-wide authorization filter, including the four
    /// that ask <c>IBlueprintAuthorizationService</c> nothing.
    /// </summary>
    [Theory]
    [InlineData("GET", "msels")]
    [InlineData("GET", "my-msels")]
    [InlineData("GET", "my-join-msels")]
    [InlineData("GET", "my-launch-msels")]
    [InlineData("GET", "users/00000000-0000-0000-0000-000000000001/msels")]
    [InlineData("GET", "msels/00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "msels/00000000-0000-0000-0000-000000000001/data")]
    [InlineData("GET", "msels/00000000-0000-0000-0000-000000000001/xlsx")]
    [InlineData("GET", "msels/00000000-0000-0000-0000-000000000001/json")]
    [InlineData("POST", "msels")]
    [InlineData("POST", "msels/00000000-0000-0000-0000-000000000001/copy")]
    [InlineData("PUT", "msels/00000000-0000-0000-0000-000000000001")]
    [InlineData("PUT", "msels/00000000-0000-0000-0000-000000000001/user/" +
        "00000000-0000-0000-0000-000000000002/role/Owner/add")]
    [InlineData("PUT", "msels/00000000-0000-0000-0000-000000000001/user/" +
        "00000000-0000-0000-0000-000000000002/role/Owner/remove")]
    [InlineData("DELETE", "msels/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "msels/00000000-0000-0000-0000-000000000001/archive")]
    public async Task EveryRoute_Unauthenticated_Is401(string method, string route)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/{route}");

        var response = await AnonymousClient.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A full update body for <paramref name="entity"/>, which is what the endpoint wants: the mapper
    /// writes every mapped member, so a field the body omits is written as its default.
    /// </summary>
    private static Msel Body(MselEntity entity, string name = null) => new()
    {
        Id = entity.Id,
        Name = name ?? entity.Name,
        Description = entity.Description,
        Status = entity.Status,
        IsTemplate = entity.IsTemplate,
        StartTime = entity.StartTime,
        DurationSeconds = entity.DurationSeconds
    };

    private async Task<List<Msel>> GetMsels(HttpClient client, string route)
    {
        var response = await client.GetAsync(route, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<List<Msel>>(response);
    }

    private async Task<Msel> GetMsel(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/msels/{id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<Msel>(response);
    }

    private async Task<Msel> Update(HttpClient client, Guid id, Msel body)
    {
        var response = await client.PutAsJsonAsync($"/api/msels/{id}", body, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<Msel>(response);
    }

    /// <summary>
    /// Asserts a server-stamped audit timestamp: present, and inside the window the test bracketed.
    /// </summary>
    private static void AssertStampedBetween(DateTime? actual, DateTime notBefore, DateTime notAfter)
    {
        Assert.NotNull(actual);
        Assert.InRange(actual.Value, notBefore, notAfter);
    }

    private async Task<T> Read<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
}
