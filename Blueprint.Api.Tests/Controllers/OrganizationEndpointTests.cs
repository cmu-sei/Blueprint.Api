// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>The eight organization endpoints, driven over HTTP.</summary>
public class OrganizationEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET organizations/templates
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Templates_ReturnsOnlyOrganizationsWithNoMsel()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var template = TestData.Organization();
        await Seed(template, TestData.Organization(msel.Id));

        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var returned = await GetOrganizations(Client(actor), "/api/organizations/templates");

        Assert.Equal(template.Id, Assert.Single(returned).Id);
    }

    [Fact]
    public async Task Templates_WithNoneSeeded_IsAnEmptyArray()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        Assert.Empty(await GetOrganizations(Client(actor), "/api/organizations/templates"));
    }

    /// <summary>Templates with no system role is answered with a 200.</summary>
    [Fact]
    public async Task Templates_WithNoSystemRole_Is200()
    {
        await Seed(TestData.Organization());

        var actor = await Actor().SeedAsync();

        Assert.Single(await GetOrganizations(Client(actor), "/api/organizations/templates"));
    }

    /// <summary>A user with no row is provisioned by <c>UserClaimsService.ValidateUser</c> on the first
    /// request.</summary>
    [Fact]
    public async Task Templates_AsAUserWithNoRow_ProvisionsTheUser()
    {
        var userId = Guid.NewGuid();

        var response = await ClientFor(userId, "Provisioned Once").GetAsync(
            "/api/organizations/templates", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = NewContext();
        var user = await context.Users.AsNoTracking().SingleAsync(x => x.Id == userId, Ct);

        Assert.Equal("Provisioned Once", user.Name);
        Assert.Null(user.RoleId);
    }

    // ---------------------------------------------------------------------------------------------
    // GET msels/{mselId}/organizations
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByMsel_AsAViewer_ReturnsTheMselsOrganizations()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var organization = TestData.Organization(msel.Id);
        await Seed(organization);

        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var returned = await GetOrganizations(Client(actor), $"/api/msels/{msel.Id}/organizations");

        Assert.Equal(organization.Id, Assert.Single(returned).Id);
    }

    [Fact]
    public async Task GetByMsel_WithViewMselsPermission_ReturnsThemWithNoRole()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(TestData.Organization(msel.Id));

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Single(await GetOrganizations(Client(actor), $"/api/msels/{msel.Id}/organizations"));
    }

    [Fact]
    public async Task GetByMsel_AsTheMselsCreator_ReturnsThem()
    {
        var creatorId = Guid.NewGuid();
        var msel = TestData.Msel(createdBy: creatorId);
        await Seed(msel);
        await Seed(TestData.Organization(msel.Id));

        var actor = await Actor().WithId(creatorId).SeedAsync();

        Assert.Single(await GetOrganizations(Client(actor), $"/api/msels/{msel.Id}/organizations"));
    }

    [Fact]
    public async Task GetByMsel_AsAMemberOfATeamOnTheMsel_ReturnsThem()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var team = TestData.Team(msel.Id);
        await Seed(team, TestData.Organization(msel.Id));

        var actor = await Actor().OnTeam(team).SeedAsync();

        Assert.Single(await GetOrganizations(Client(actor), $"/api/msels/{msel.Id}/organizations"));
    }

    [Fact]
    public async Task GetByMsel_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(TestData.Organization(msel.Id));

        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/organizations", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// A template MSEL's organizations are readable by anyone: the service falls through the
    /// <c>ForbiddenException</c> when <c>msel.IsTemplate</c>.
    /// </summary>
    [Fact]
    public async Task GetByMsel_WithNoRoleOnATemplateMsel_ReturnsThem()
    {
        var msel = TestData.Msel(isTemplate: true);
        await Seed(msel);
        await Seed(TestData.Organization(msel.Id));

        var actor = await Actor().SeedAsync();

        Assert.Single(await GetOrganizations(Client(actor), $"/api/msels/{msel.Id}/organizations"));
    }

    [Fact]
    public async Task GetByMsel_DoesNotReturnAnotherMselsOrganizations()
    {
        var mine = TestData.Msel();
        var theirs = TestData.Msel();
        await Seed(mine, theirs);

        var organization = TestData.Organization(mine.Id);
        await Seed(organization, TestData.Organization(theirs.Id));

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var returned = await GetOrganizations(Client(actor), $"/api/msels/{mine.Id}/organizations");

        Assert.Equal(organization.Id, Assert.Single(returned).Id);
    }

    /// <summary>Get by MSEL for an unknown MSEL is answered with a 500.</summary>
    [Fact]
    public async Task GetByMsel_ForAnUnknownMsel_Is500()
    {
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{Guid.NewGuid()}/organizations", Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Object reference not set to an instance of an object.", failure.Title);
        Assert.Contains("OrganizationService.GetByMselAsync", failure.Detail);
    }

    /// <summary>Get by MSEL for an unknown MSEL with view MSELs permission is an empty array.</summary>
    [Fact]
    public async Task GetByMsel_ForAnUnknownMsel_WithViewMselsPermission_IsAnEmptyArray()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Empty(await GetOrganizations(
            Client(actor), $"/api/msels/{Guid.NewGuid()}/organizations"));
    }

    // ---------------------------------------------------------------------------------------------
    // GET organizations/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ATemplate_NeedsNoPermission()
    {
        var organization = TestData.Organization();
        await Seed(organization);

        var actor = await Actor().SeedAsync();

        var returned = await GetOrganization(Client(actor), organization.Id);

        Assert.Equal(organization.Name, returned.Name);
        Assert.Null(returned.MselId);
    }

    [Fact]
    public async Task Get_AMselOrganization_AsAViewer_ReturnsIt()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var organization = TestData.Organization(msel.Id);
        await Seed(organization);

        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        Assert.Equal(msel.Id, (await GetOrganization(Client(actor), organization.Id)).MselId);
    }

    [Fact]
    public async Task Get_AMselOrganization_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var organization = TestData.Organization(msel.Id);
        await Seed(organization);

        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/organizations/{organization.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_AMselOrganization_WithViewMselsPermission_ReturnsIt()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var organization = TestData.Organization(msel.Id);
        await Seed(organization);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Equal(organization.Id, (await GetOrganization(Client(actor), organization.Id)).Id);
    }

    /// <summary>Get an unknown id is answered with a 500.</summary>
    [Fact]
    public async Task Get_AnUnknownId_Is500()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Client(actor).GetAsync($"/api/organizations/{Guid.NewGuid()}", Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Sequence contains no elements.", failure.Title);
        Assert.Contains("OrganizationService.GetAsync", failure.Detail);
    }

    /// <summary>Property names go out in camelCase, read from the raw JSON.</summary>
    [Fact]
    public async Task Get_SerializesPropertyNamesInCamelCase()
    {
        var organization = TestData.Organization();
        await Seed(organization);

        var actor = await Actor().SeedAsync();

        var json = await Client(actor).GetStringAsync($"/api/organizations/{organization.Id}", Ct);

        using var document = JsonDocument.Parse(json);
        var names = document.RootElement.EnumerateObject().Select(x => x.Name).ToList();

        Assert.Contains("shortName", names);
        Assert.Contains("isTemplate", names);
        Assert.Contains("mselId", names);
        Assert.Contains("dateCreated", names);
        Assert.DoesNotContain("ShortName", names);
    }

    // ---------------------------------------------------------------------------------------------
    // POST organizations
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_ATemplate_WithManageOrganizations_Is201()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageOrganizations)
            .SeedAsync();

        var response = await Client(actor).PostAsJsonAsync("/api/organizations", NewBody(), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await Read<Organization>(response);

        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal("Created Organization", created.Name);
    }

    [Fact]
    public async Task Create_ATemplate_WithoutManageOrganizations_Is403()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync("/api/organizations", NewBody(), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_ReturnsALocationHeaderThatResolves()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageOrganizations)
            .SeedAsync();

        var response = await Client(actor).PostAsJsonAsync("/api/organizations", NewBody(), Ct);
        var created = await Read<Organization>(response);

        Assert.NotNull(response.Headers.Location);
        Assert.EndsWith($"/api/organizations/{created.Id}", response.Headers.Location.ToString());

        var followed = await Client(actor).GetAsync(response.Headers.Location, Ct);

        Assert.Equal(HttpStatusCode.OK, followed.StatusCode);
    }

    [Fact]
    public async Task Create_SetsCreatedByToTheCaller()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageOrganizations)
            .SeedAsync();

        var response = await Client(actor).PostAsJsonAsync("/api/organizations", NewBody(), Ct);
        var created = await Read<Organization>(response);

        Assert.Equal(actor.Id, created.CreatedBy);

        await using var context = NewContext();
        var stored = await context.Organizations.AsNoTracking().SingleAsync(x => x.Id == created.Id, Ct);

        Assert.Equal(actor.Id, stored.CreatedBy);
    }

    [Fact]
    public async Task Create_IgnoresACreatedByInTheBody()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageOrganizations)
            .SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/organizations",
            new { Name = "Spoofed", ShortName = "spoof", CreatedBy = Guid.NewGuid() },
            Ct);

        Assert.Equal(actor.Id, (await Read<Organization>(response)).CreatedBy);
    }

    [Fact]
    public async Task Create_WithAnExplicitId_UsesIt()
    {
        var id = Guid.NewGuid();

        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageOrganizations)
            .SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/organizations", new { Id = id, Name = "Explicit", ShortName = "exp" }, Ct);

        Assert.Equal(id, (await Read<Organization>(response)).Id);
    }

    /// <summary>A create's audit fields are stamped by <c>BlueprintContext.SaveEntries</c>, whatever the body
    /// says.</summary>
    [Fact]
    public async Task Create_StampsTheAuditFieldsOnTheServer()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageOrganizations)
            .SeedAsync();

        var before = DateTime.UtcNow;

        var response = await Client(actor).PostAsJsonAsync(
            "/api/organizations",
            new
            {
                Name = "Audited",
                ShortName = "aud",
                DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                DateModified = new DateTime(1999, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                ModifiedBy = Guid.NewGuid()
            },
            Ct);

        var created = await Read<Organization>(response);

        AssertStampedBetween(created.DateCreated, before, DateTime.UtcNow);
        Assert.Null(created.DateModified);
        Assert.Null(created.ModifiedBy);
    }

    [Fact]
    public async Task Create_StripsScriptFromTheDescription()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageOrganizations)
            .SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/organizations",
            new
            {
                Name = "Sanitized",
                ShortName = "san",
                Description = "<p>keep</p><script>alert('no')</script>"
            },
            Ct);

        var created = await Read<Organization>(response);

        Assert.Contains("keep", created.Description);
        Assert.DoesNotContain("script", created.Description);

        await using var context = NewContext();
        var stored = await context.Organizations.AsNoTracking().SingleAsync(x => x.Id == created.Id, Ct);

        Assert.DoesNotContain("script", stored.Description);
    }

    [Fact]
    public async Task Create_ForAMsel_WithEditMselsPermission_Is201()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/organizations", NewBody(mselId: msel.Id), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(msel.Id, (await Read<Organization>(response)).MselId);
    }

    [Theory]
    [InlineData(MselRole.Owner)]
    [InlineData(MselRole.Editor)]
    public async Task Create_ForAMsel_AsAnOwnerOrEditor_Is201(MselRole role)
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/organizations", NewBody(mselId: msel.Id), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData(MselRole.Viewer)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.MoveEditor)]
    public async Task Create_ForAMsel_WithAReadOnlyRole_Is403(MselRole role)
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/organizations", NewBody(mselId: msel.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// Which permission a create needs is decided by the request body's <c>MselId</c>, not by the
    /// caller: <see cref="SystemPermission.ManageOrganizations"/> is enough for a template and no help at
    /// all for an MSEL.
    /// </summary>
    [Fact]
    public async Task Create_ForAMsel_WithManageOrganizationsOnly_Is403()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageOrganizations)
            .SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/organizations", NewBody(mselId: msel.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Create for an unknown MSEL is answered with a 500.</summary>
    [Fact]
    public async Task Create_ForAnUnknownMsel_Is500()
    {
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/organizations", NewBody(mselId: Guid.NewGuid()), Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Object reference not set to an instance of an object.", failure.Title);
        Assert.Contains("MselOwnerRequirement.IsMet", failure.Detail);
    }

    /// <summary>A create against a MSEL marks that MSEL modified.</summary>
    [Fact]
    public async Task Create_ForAMsel_MarksTheMselModified()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var before = DateTime.UtcNow;

        await Client(actor).PostAsJsonAsync("/api/organizations", NewBody(mselId: msel.Id), Ct);

        await using var context = NewContext();
        var stored = await context.Msels.AsNoTracking().SingleAsync(x => x.Id == msel.Id, Ct);

        Assert.Equal(actor.Id, stored.ModifiedBy);
        AssertStampedBetween(stored.DateModified, before, DateTime.UtcNow);
    }

    [Fact]
    public async Task Create_BroadcastsOrganizationCreatedToTheAdminDataGroup()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageOrganizations)
            .SeedAsync();

        await Client(actor).PostAsJsonAsync("/api/organizations", NewBody(), Ct);

        Assert.Contains(MainHub.ADMIN_DATA_GROUP, Hub.Recipients(MainHubMethods.OrganizationCreated));
    }

    [Fact]
    public async Task Create_ForAMsel_BroadcastsToTheMselGroup()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Client(actor).PostAsJsonAsync("/api/organizations", NewBody(mselId: msel.Id), Ct);

        Assert.Equal(
            [msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.OrganizationCreated, msel.Id));
    }

    /// <summary>Create a template also broadcasts to an empty group name.</summary>
    [Fact]
    public async Task Create_ATemplate_AlsoBroadcastsToAnEmptyGroupName()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageOrganizations)
            .SeedAsync();

        await Client(actor).PostAsJsonAsync("/api/organizations", NewBody(), Ct);

        Assert.Equal(
            [string.Empty, MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.OrganizationCreated, string.Empty));
    }

    // ---------------------------------------------------------------------------------------------
    // PUT organizations/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_ATemplate_WithManageOrganizations_Is200()
    {
        var organization = TestData.Organization();
        await Seed(organization);

        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageOrganizations)
            .SeedAsync();

        var response = await Client(actor).PutAsJsonAsync(
            $"/api/organizations/{organization.Id}", Body(organization, name: "Renamed"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Renamed", (await Read<Organization>(response)).Name);

        await using var context = NewContext();
        var stored = await context.Organizations.AsNoTracking()
            .SingleAsync(x => x.Id == organization.Id, Ct);

        Assert.Equal("Renamed", stored.Name);
    }

    [Fact]
    public async Task Update_ATemplate_WithoutManageOrganizations_Is403()
    {
        var organization = TestData.Organization();
        await Seed(organization);

        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).PutAsJsonAsync(
            $"/api/organizations/{organization.Id}", Body(organization, name: "Renamed"), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Update_AnUnknownId_Is404()
    {
        var id = Guid.NewGuid();

        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageOrganizations)
            .SeedAsync();

        var response = await Client(actor).PutAsJsonAsync(
            $"/api/organizations/{id}", new { Id = id, Name = "Missing", ShortName = "mis" }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// An update stamps <c>ModifiedBy</c> and <c>DateModified</c> on the server, and restores
    /// <c>CreatedBy</c> and <c>DateCreated</c> from the row's original values - so neither the mapper nor
    /// a hostile body can rewrite who created a record or when.
    /// </summary>
    /// <remarks>
    /// The restore is the second loop of <c>BlueprintContext.SaveEntries</c>, reading
    /// <c>entry.OriginalValues</c>. Without it, <c>_mapper.Map(organization, organizationToUpdate)</c>
    /// would let any caller who may edit a record also claim to have authored it.
    /// </remarks>
    [Fact]
    public async Task Update_StampsTheAuditFieldsAndPreservesCreation()
    {
        var creatorId = Guid.NewGuid();
        var organization = TestData.Organization(createdBy: creatorId);
        await Seed(organization);

        var createdAt = organization.DateCreated;

        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageOrganizations)
            .SeedAsync();

        var before = DateTime.UtcNow;

        var response = await Client(actor).PutAsJsonAsync(
            $"/api/organizations/{organization.Id}",
            Body(organization, name: "Renamed") with
            {
                DateModified = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            Ct);

        var updated = await Read<Organization>(response);

        Assert.Equal(actor.Id, updated.ModifiedBy);
        AssertStampedBetween(updated.DateModified, before, DateTime.UtcNow);
        Assert.Equal(creatorId, updated.CreatedBy);
        Assert.Equal(createdAt, updated.DateCreated, TimeSpan.FromMilliseconds(1));
    }

    /// <summary>Update with a mismatched body id is answered with a 500.</summary>
    [Fact]
    public async Task Update_WithAMismatchedBodyId_Is500()
    {
        var organization = TestData.Organization();
        await Seed(organization);

        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageOrganizations)
            .SeedAsync();

        var response = await Client(actor).PutAsJsonAsync(
            $"/api/organizations/{organization.Id}",
            Body(organization) with { Id = Guid.NewGuid() },
            Ct);

        Assert.Equal("The property 'OrganizationEntity.Id' is part of a key and so cannot be modified or marked as modified. To change the principal of an existing entity with an identifying foreign key, first delete the dependent and invoke 'SaveChanges', and then associate the dependent with the new principal.", (await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
    }

    [Fact]
    public async Task Update_ForAMsel_AsAnOwner_Is200()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var organization = TestData.Organization(msel.Id);
        await Seed(organization);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).PutAsJsonAsync(
            $"/api/organizations/{organization.Id}", Body(organization, name: "Renamed"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Update_ForAMsel_with_EditMsels_and_no_role_is_200()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var organization = TestData.Organization(msel.Id);
        await Seed(organization);

        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).PutAsJsonAsync(
            $"/api/organizations/{organization.Id}", Body(organization, name: "Renamed"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Renamed", (await ReadBack(rb => rb.Organizations.SingleAsync(x => x.Id == organization.Id, Ct))).Name);
    }

    [Fact]
    public async Task Update_ForAMsel_AsAViewer_Is403()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var organization = TestData.Organization(msel.Id);
        await Seed(organization);

        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var response = await Client(actor).PutAsJsonAsync(
            $"/api/organizations/{organization.Id}", Body(organization, name: "Renamed"), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Update_ForAMsel_MarksTheMselModified()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var organization = TestData.Organization(msel.Id);
        await Seed(organization);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var before = DateTime.UtcNow;

        await Client(actor).PutAsJsonAsync(
            $"/api/organizations/{organization.Id}", Body(organization, name: "Renamed"), Ct);

        await using var context = NewContext();
        var stored = await context.Msels.AsNoTracking().SingleAsync(x => x.Id == msel.Id, Ct);

        Assert.Equal(actor.Id, stored.ModifiedBy);
        AssertStampedBetween(stored.DateModified, before, DateTime.UtcNow);
    }

    /// <summary>Update chooses its permission branch from the request body.</summary>
    [Fact]
    public async Task Update_ChoosesItsPermissionBranchFromTheRequestBody()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var organization = TestData.Organization(msel.Id);
        await Seed(organization);

        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageOrganizations)
            .SeedAsync();

        var response = await Client(actor).PutAsJsonAsync(
            $"/api/organizations/{organization.Id}",
            Body(organization, name: "Escalated") with { MselId = null },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = NewContext();
        var stored = await context.Organizations.AsNoTracking()
            .SingleAsync(x => x.Id == organization.Id, Ct);

        Assert.Equal("Escalated", stored.Name);
        Assert.Null(stored.MselId);
    }

    [Fact]
    public async Task Update_BroadcastsOrganizationUpdatedWithCamelCaseModifiedProperties()
    {
        var organization = TestData.Organization();
        await Seed(organization);

        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageOrganizations)
            .SeedAsync();

        await Client(actor).PutAsJsonAsync(
            $"/api/organizations/{organization.Id}", Body(organization, name: "Renamed"), Ct);

        var send = Hub.ToGroup(MainHub.ADMIN_DATA_GROUP)
            .Single(x => x.Method == MainHubMethods.OrganizationUpdated);

        var modifiedProperties = Assert.IsType<string[]>(send.Arguments[1]);

        Assert.Contains("name", modifiedProperties);
        Assert.DoesNotContain("Name", modifiedProperties);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE organizations/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_ATemplate_WithManageOrganizations_Is204()
    {
        var organization = TestData.Organization();
        await Seed(organization);

        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageOrganizations)
            .SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/organizations/{organization.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var context = NewContext();

        Assert.Empty(await context.Organizations.ToListAsync(Ct));
    }

    [Fact]
    public async Task Delete_ATemplate_WithoutManageOrganizations_Is403()
    {
        var organization = TestData.Organization();
        await Seed(organization);

        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/organizations/{organization.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var context = NewContext();

        Assert.Single(await context.Organizations.ToListAsync(Ct));
    }

    [Fact]
    public async Task Delete_ForAMsel_AsAnOwner_Is204()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var organization = TestData.Organization(msel.Id);
        await Seed(organization);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/organizations/{organization.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Delete_ForAMsel_with_EditMsels_and_no_role_is_204()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var organization = TestData.Organization(msel.Id);
        await Seed(organization);

        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/organizations/{organization.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await ReadBack(rb => rb.Organizations.Where(x => x.Id == organization.Id).ToListAsync(Ct)));
    }

    [Fact]
    public async Task Delete_ForAMsel_AsAViewer_Is403()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var organization = TestData.Organization(msel.Id);
        await Seed(organization);

        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/organizations/{organization.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Delete_ForAMsel_MarksTheMselModified()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var organization = TestData.Organization(msel.Id);
        await Seed(organization);

        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Client(actor).DeleteAsync($"/api/organizations/{organization.Id}", Ct);

        await using var context = NewContext();
        var stored = await context.Msels.AsNoTracking().SingleAsync(x => x.Id == msel.Id, Ct);

        Assert.Equal(actor.Id, stored.ModifiedBy);
        Assert.NotNull(stored.DateModified);
        Assert.NotEqual(default, stored.DateModified);
    }

    /// <summary>Delete checks existence before permission.</summary>
    [Fact]
    public async Task Delete_ChecksExistenceBeforePermission()
    {
        var organization = TestData.Organization();
        await Seed(organization);

        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewOrganizations).SeedAsync();

        var missing = await Client(actor).DeleteAsync($"/api/organizations/{Guid.NewGuid()}", Ct);
        var existing = await Client(actor).DeleteAsync($"/api/organizations/{organization.Id}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, existing.StatusCode);
    }

    [Fact]
    public async Task Delete_BroadcastsOrganizationDeletedWithTheId()
    {
        var organization = TestData.Organization();
        await Seed(organization);

        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageOrganizations)
            .SeedAsync();

        await Client(actor).DeleteAsync($"/api/organizations/{organization.Id}", Ct);

        var send = Hub.ToGroup(MainHub.ADMIN_DATA_GROUP)
            .Single(x => x.Method == MainHubMethods.OrganizationDeleted);

        Assert.Equal(organization.Id, send.Payload);
    }

    // ---------------------------------------------------------------------------------------------
    // POST organizations/json
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task UploadJson_WithManageOrganizations_CreatesTheOrganizations()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageOrganizations)
            .SeedAsync();

        var response = await UploadJson(Client(actor), """
            [
              { "name": "First", "shortName": "one", "email": "one@organization.test" },
              { "name": "Second", "shortName": "two", "email": "two@organization.test" }
            ]
            """);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var created = await Read<List<Organization>>(response);

        Assert.Equal(["First", "Second"], created.Select(x => x.Name));

        await using var context = NewContext();

        Assert.Equal(2, await context.Organizations.CountAsync(Ct));
    }

    [Fact]
    public async Task UploadJson_WithoutManageOrganizations_Is403()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await UploadJson(Client(actor), """[ { "name": "Refused" } ]""");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var context = NewContext();

        Assert.Empty(await context.Organizations.ToListAsync(Ct));
    }

    /// <summary>
    /// An upload is always a set of templates, whatever the file claims.
    /// </summary>
    [Fact]
    public async Task UploadJson_ForcesEveryOrganizationToBeATemplate()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageOrganizations)
            .SeedAsync();

        var response = await UploadJson(Client(actor), $$"""
            [ { "name": "Claims A Msel", "isTemplate": false, "mselId": "{{msel.Id}}" } ]
            """);

        var created = Assert.Single(await Read<List<Organization>>(response));

        Assert.True(created.IsTemplate);
        Assert.Null(created.MselId);
    }

    [Fact]
    public async Task UploadJson_AssignsFreshIdsAndStampsTheCaller()
    {
        var uploadedId = Guid.NewGuid();

        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageOrganizations)
            .SeedAsync();

        var response = await UploadJson(Client(actor), $$"""
            [ { "id": "{{uploadedId}}", "name": "Fresh", "createdBy": "{{Guid.NewGuid()}}" } ]
            """);

        var created = Assert.Single(await Read<List<Organization>>(response));

        Assert.NotEqual(uploadedId, created.Id);
        Assert.Equal(actor.Id, created.CreatedBy);
        Assert.Null(created.ModifiedBy);
        Assert.Null(created.DateModified);
    }

    [Fact]
    public async Task UploadJson_WithAnEmptyArray_CreatesNothing()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageOrganizations)
            .SeedAsync();

        var response = await UploadJson(Client(actor), "[]");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await Read<List<Organization>>(response));
    }

    [Fact]
    public async Task UploadJson_WithNoFile_Is400()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageOrganizations)
            .SeedAsync();

        using var content = new MultipartFormDataContent();

        var response = await Client(actor).PostAsync("/api/organizations/json", content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UploadJson_WithMalformedJson_Is500()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageOrganizations)
            .SeedAsync();

        var response = await UploadJson(Client(actor), "not json at all");

        Assert.Equal("'not json at all' is an invalid JSON literal. Expected the literal 'null'. Path: $ | LineNumber: 0 | BytePositionInLine: 1.", (await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
    }

    /// <summary>An upload broadcasts one created event per organization.</summary>
    [Fact]
    public async Task UploadJson_BroadcastsOneCreatedEventPerOrganization()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageOrganizations)
            .SeedAsync();

        await UploadJson(Client(actor), """
            [ { "name": "First" }, { "name": "Second" }, { "name": "Third" } ]
            """);

        var sends = Hub.ToGroup(MainHub.ADMIN_DATA_GROUP)
            .Where(x => x.Method == MainHubMethods.OrganizationCreated)
            .ToList();

        Assert.Equal(3, sends.Count);
    }

    // ---------------------------------------------------------------------------------------------
    // POST organizations/json/download
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task DownloadJson_ReturnsAnAttachmentNamedOrganizationTemplates()
    {
        var organization = TestData.Organization();
        await Seed(organization);

        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageOrganizations)
            .SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/organizations/json/download", new[] { organization.Id }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType.MediaType);
        Assert.Equal(
            "organization-templates.json",
            response.Content.Headers.ContentDisposition.FileName.Trim('"'));
    }

    [Fact]
    public async Task DownloadJson_WithoutManageOrganizations_Is403()
    {
        var organization = TestData.Organization();
        await Seed(organization);

        var actor = await Actor().WithAllSystemPermissions().SeedAsync();
        var refused = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var allowed = await Client(actor).PostAsJsonAsync(
            "/api/organizations/json/download", new[] { organization.Id }, Ct);
        var response = await Client(refused).PostAsJsonAsync(
            "/api/organizations/json/download", new[] { organization.Id }, Ct);

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DownloadJson_ReturnsOnlyTheRequestedOrganizations()
    {
        var wanted = TestData.Organization(name: "Wanted");
        await Seed(wanted, TestData.Organization(name: "Unwanted"));

        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageOrganizations)
            .SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/organizations/json/download", new[] { wanted.Id, Guid.NewGuid() }, Ct);

        var json = await response.Content.ReadAsStringAsync(Ct);

        Assert.Contains("Wanted", json);
        Assert.DoesNotContain("Unwanted", json);
    }

    /// <summary>The download is reference-preserving JSON (<c>$id</c> and <c>$values</c>).</summary>
    [Fact]
    public async Task DownloadJson_WrapsTheListForReferencePreservation()
    {
        var organization = TestData.Organization();
        await Seed(organization);

        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageOrganizations)
            .SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "/api/organizations/json/download", new[] { organization.Id }, Ct);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        Assert.True(document.RootElement.TryGetProperty("$id", out _));
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("$values").ValueKind);
    }

    [Fact]
    public async Task DownloadJson_RoundTripsThroughUploadJson()
    {
        var organization = TestData.Organization(name: "Round Tripped");
        organization.Description = "<p>kept</p>";
        await Seed(organization);

        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageOrganizations)
            .SeedAsync();

        var downloaded = await Client(actor).PostAsJsonAsync(
            "/api/organizations/json/download", new[] { organization.Id }, Ct);

        var response = await UploadJson(
            Client(actor), await downloaded.Content.ReadAsStringAsync(Ct));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var created = Assert.Single(await Read<List<Organization>>(response));

        Assert.Equal("Round Tripped", created.Name);
        Assert.Equal("<p>kept</p>", created.Description);
        Assert.NotEqual(organization.Id, created.Id);
    }

    // ---------------------------------------------------------------------------------------------
    // Authentication
    // ---------------------------------------------------------------------------------------------

    /// <summary>Every route answers 401 to a request with no identity; the anonymous client is the case under
    /// test.</summary>
    [Theory]
    [InlineData("GET", "organizations/templates")]
    [InlineData("GET", "msels/00000000-0000-0000-0000-000000000001/organizations")]
    [InlineData("GET", "organizations/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "organizations")]
    [InlineData("PUT", "organizations/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "organizations/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "organizations/json")]
    [InlineData("POST", "organizations/json/download")]
    public async Task EveryRouteRefusesAnUnauthenticatedRequest(string method, string route)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/{route}")
        {
            Content = JsonContent.Create(new { })
        };

        var response = await Client().SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The wire shape of a create. An anonymous object rather than a <see cref="Organization"/>, so a
    /// property the tests never send stays unsent.
    /// </summary>
    private static object NewBody(Guid? mselId = null) => new
    {
        Name = "Created Organization",
        ShortName = "created",
        Description = "<p>Created</p>",
        Summary = "Created by a test",
        Email = "created@organization.test",
        MselId = mselId
    };

    /// <summary>
    /// The wire shape of an update, echoing the seeded row. The id is included because
    /// <see cref="Update_WithAMismatchedBodyId_Is500"/> is what happens when it is not.
    /// </summary>
    private static UpdateBody Body(OrganizationEntity entity, string name = null) => new()
    {
        Id = entity.Id,
        Name = name ?? entity.Name,
        ShortName = entity.ShortName,
        Description = entity.Description,
        Summary = entity.Summary,
        Email = entity.Email,
        IsTemplate = entity.IsTemplate,
        MselId = entity.MselId,
        DateCreated = entity.DateCreated
    };

    /// <summary>
    /// A record rather than an anonymous type so that tests can vary one field with a <c>with</c>
    /// expression.
    /// </summary>
    private sealed record UpdateBody
    {
        public Guid Id { get; init; }
        public string Name { get; init; }
        public string ShortName { get; init; }
        public string Description { get; init; }
        public string Summary { get; init; }
        public string Email { get; init; }
        public bool IsTemplate { get; init; }
        public Guid? MselId { get; init; }
        public DateTime DateCreated { get; init; }
        public DateTime? DateModified { get; init; }
    }

    private async Task<List<Organization>> GetOrganizations(HttpClient client, string route)
    {
        var response = await client.GetAsync(route, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<List<Organization>>(response);
    }

    private async Task<Organization> GetOrganization(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/organizations/{id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<Organization>(response);
    }

    /// <summary>
    /// Posts <paramref name="json"/> as the <c>ToUpload</c> part of a multipart form, which is what
    /// <c>FileForm</c> binds.
    /// </summary>
    /// <remarks>
    /// The <c>await</c> before the <c>using</c> falls out of scope is load-bearing: <c>TestServer</c>
    /// reads the request body inside <c>SendAsync</c>, so returning the task unawaited disposes the
    /// content first and every upload test fails with <c>ObjectDisposedException</c> rather than
    /// whatever it was asserting.
    /// </remarks>
    private async Task<HttpResponseMessage> UploadJson(HttpClient client, string json)
    {
        using var content = new MultipartFormDataContent();

        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "ToUpload", "organizations.json");

        return await client.PostAsync("/api/organizations/json", content, Ct);
    }

    /// <summary>
    /// Asserts a server-stamped audit timestamp: present, UTC, and inside the window the test bracketed.
    /// </summary>
    /// <remarks>
    /// Blueprint stamps these in <c>BlueprintContext.SaveEntries</c> from <c>DateTime.UtcNow</c>, so a
    /// test can bound the value but never predict it.
    /// </remarks>
    private static void AssertStampedBetween(DateTime? actual, DateTime notBefore, DateTime notAfter)
    {
        Assert.NotNull(actual);
        Assert.InRange(actual.Value, notBefore, notAfter);
    }

    private async Task<T> Read<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
}
