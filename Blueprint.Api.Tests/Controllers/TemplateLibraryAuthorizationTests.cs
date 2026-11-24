// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Tests.Support;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// The one gate the six template libraries share: <c>GET cards/templates</c>, <c>citeActions/templates</c>,
/// <c>citeDuties/templates</c>, <c>playerApplications/templates</c>, <c>organizations/templates</c> and
/// <c>dataFields/templates</c>.
/// </summary>
/// <remarks>
/// Each is open to the permissions that view or manage its library, to <c>ViewMsels</c>, and to anybody
/// who works on a MSEL (<c>AnyMselRequirement</c>), because a MSEL's tabs load the libraries whatever the
/// caller's role on it. <c>organizations/templates</c> also takes <c>CreateMsels</c>, for the new-MSEL
/// page. Each route takes several permissions, so none can hold a row in
/// <see cref="RouteAuthorizationTests"/>, which lifts a refusal with exactly one. What each route answers
/// is tested in its own file.
/// </remarks>
public class TemplateLibraryAuthorizationTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    private static readonly (string Route, SystemPermission[] Opens)[] Libraries =
    [
        ("api/cards/templates", [SystemPermission.ViewGalleryCards, SystemPermission.ManageGalleryCards, SystemPermission.ViewMsels]),
        ("api/citeActions/templates", [SystemPermission.ViewCiteActions, SystemPermission.ManageCiteActions, SystemPermission.ViewMsels]),
        ("api/citeDuties/templates", [SystemPermission.ViewCiteDuties, SystemPermission.ManageCiteDuties, SystemPermission.ViewMsels]),
        ("api/playerApplications/templates", [SystemPermission.ViewPlayerApplications, SystemPermission.ManagePlayerApplications, SystemPermission.ViewMsels]),
        ("api/organizations/templates", [SystemPermission.ViewOrganizations, SystemPermission.ManageOrganizations, SystemPermission.ViewMsels, SystemPermission.CreateMsels]),
        ("api/dataFields/templates", [SystemPermission.ViewDataFields, SystemPermission.ManageDataFields, SystemPermission.ViewMsels]),
    ];

    public static TheoryData<string> Routes => new(Libraries.Select(x => x.Route));

    public static TheoryData<string, SystemPermission> OpeningPermissions
    {
        get
        {
            var data = new TheoryData<string, SystemPermission>();
            foreach (var (route, opens) in Libraries)
                foreach (var permission in opens)
                    data.Add(route, permission);
            return data;
        }
    }

    public static TheoryData<string, MselRole> RolesOnEveryRoute
    {
        get
        {
            var data = new TheoryData<string, MselRole>();
            foreach (var (route, _) in Libraries)
                foreach (var role in Enum.GetValues<MselRole>())
                    data.Add(route, role);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryLibrary_is_forbidden_for_a_caller_with_no_permission_who_works_on_no_msel(string route)
    {
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync(route, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>A team membership is a participant's way into a MSEL, and the Exercise View loads no
    /// library, so it opens none.</summary>
    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryLibrary_is_forbidden_for_a_caller_only_on_a_msels_team(string route)
    {
        var msel = TestData.Msel();
        var team = TestData.Team(msel.Id);
        await Seed(msel, team);
        var actor = await Actor().OnTeam(team).SeedAsync();

        var response = await Client(actor).GetAsync(route, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryLibrary_is_forbidden_for_a_caller_holding_every_permission_but_those_that_open_it(string route)
    {
        var opens = Libraries.Single(x => x.Route == route).Opens;
        var actor = await Actor()
            .WithSystemPermissions(Enum.GetValues<SystemPermission>().Except(opens).ToArray())
            .SeedAsync();

        var response = await Client(actor).GetAsync(route, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(OpeningPermissions))]
    public async Task EveryLibrary_OpensToEachOfItsPermissions(string route, SystemPermission permission)
    {
        var actor = await Actor().WithSystemPermissions(permission).SeedAsync();

        var response = await Client(actor).GetAsync(route, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Every MSEL role opens the tabs that load the libraries, so every role opens them.</summary>
    [Theory]
    [MemberData(nameof(RolesOnEveryRoute))]
    public async Task EveryLibrary_OpensToAnyRoleOnAnyMsel(string route, MselRole role)
    {
        var actor = await Actor().OnNewMsel(role).SeedAsync();

        var response = await Client(actor).GetAsync(route, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryLibrary_OpensToTheCreatorOfAMsel(string route)
    {
        var actor = await Actor().SeedAsync();
        await Seed(TestData.Msel(createdBy: actor.Id));

        var response = await Client(actor).GetAsync(route, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
