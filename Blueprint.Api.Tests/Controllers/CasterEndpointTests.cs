// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Tests.Support;
using Caster.Api.Client;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

using CasterDirectory = Caster.Api.Client.Directory;
using CasterFile = Caster.Api.Client.File;
using SystemPermission = Blueprint.Api.Data.Enumerations.SystemPermission;

namespace Blueprint.Api.Tests.Controllers;

/// <summary><c>GET api/caster/directories</c> and <c>GET api/caster/directories/{id}/team-variables</c>,
/// and through them <c>CasterService</c>, over the substituted Caster client.</summary>
/// <remarks>
/// Neither route asks Blueprint's authorization service anything: <c>CasterService</c> calls Caster with
/// the caller's own token, and what Caster refuses is what the caller cannot see. So the cases here are
/// Caster's answers: a list, a refusal (403), an unknown directory (404) and a rejected token (401). The
/// caller is a planner holding <see cref="SystemPermission.CreateMsels"/>, the one who picks a directory.
/// </remarks>
public class CasterEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET caster/directories
    // ---------------------------------------------------------------------------------------------

    /// <summary>Every project's directories, with the project's name, ordered by project and then by
    /// directory name.</summary>
    [Fact]
    public async Task GetDirectories_ListsEveryProjectsDirectoriesInNameOrder()
    {
        var zulu = Project("Zulu");
        var alpha = Project("Alpha");
        var parent = Guid.NewGuid();
        Factory.Caster.GetAllProjectsAsync(false, Arg.Any<CancellationToken>()).Returns([zulu, alpha]);
        DirectoriesOf(zulu, Directory(zulu, "Only"));
        DirectoriesOf(alpha, Directory(alpha, "Second"), Directory(alpha, "First", parent));

        var listed = await Read<List<ViewModels.CasterDirectory>>(
            await Client(await Planner()).GetAsync(Directories, Ct));

        Assert.Equal(
            [("Alpha", "First"), ("Alpha", "Second"), ("Zulu", "Only")],
            listed.Select(x => (x.ProjectName, x.Name)));
        Assert.Equal(alpha.Id, listed[0].ProjectId);
        Assert.Equal(parent, listed[0].ParentId);
    }

    /// <summary>A caller Caster will not show every project is shown the projects they are a member of.</summary>
    [Fact]
    public async Task GetDirectories_WhenCasterRefusesTheWholeProjectList_ListsTheCallersOwnProjects()
    {
        var mine = Project("Mine");
        Factory.Caster.GetAllProjectsAsync(false, Arg.Any<CancellationToken>()).Throws(CasterArrangements.Answering(HttpStatusCode.Forbidden));
        Factory.Caster.GetAllProjectsAsync(true, Arg.Any<CancellationToken>()).Returns([mine]);
        DirectoriesOf(mine, Directory(mine, "Range"));

        var listed = await Read<List<ViewModels.CasterDirectory>>(
            await Client(await Planner()).GetAsync(Directories, Ct));

        Assert.Equal(["Range"], listed.Select(x => x.Name));
    }

    [Fact]
    public async Task GetDirectories_ForACallerWithNoProjects_IsAnEmptyList()
    {
        Factory.Caster.GetAllProjectsAsync(false, Arg.Any<CancellationToken>()).Returns([]);

        var response = await Client(await Planner()).GetAsync(Directories, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await Read<List<ViewModels.CasterDirectory>>(response));
    }

    /// <summary>A token Caster rejects is answered with a 500 carrying Caster's status.</summary>
    [Fact]
    public async Task GetDirectories_WhenCasterRejectsTheToken_Is500()
    {
        Factory.Caster.GetAllProjectsAsync(false, Arg.Any<CancellationToken>()).Throws(CasterArrangements.Answering(HttpStatusCode.Unauthorized));

        var response = await Client(await Planner()).GetAsync(Directories, Ct);

        Assert.Equal(
            "Caster answered 401.\n\nStatus: 401\nResponse: \n",
            (await AssertJsonError<ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
    }

    // ---------------------------------------------------------------------------------------------
    // GET caster/directories/{id}/team-variables
    // ---------------------------------------------------------------------------------------------

    /// <summary>The variables a team can take, from the directory's own template files and those of every
    /// directory above it.</summary>
    [Fact]
    public async Task GetTeamVariables_ReadsTheDirectoryAndEveryDirectoryAboveIt()
    {
        var project = Project("Range");
        var root = Directory(project, "root");
        var child = Directory(project, "child", root.Id);
        Knows(child, File("variables.tf", """variable "blue" {}"""));
        Knows(root, File("variables.tf", """variable "admin" { default = null }"""));

        var variables = await Read<List<ViewModels.CasterTeamVariable>>(
            await Client(await Planner()).GetAsync(TeamVariables(child.Id), Ct));

        Assert.Equal([("admin", false), ("blue", true)], variables.Select(x => (x.Name, x.Required)));
    }

    /// <summary>A workspace's own files, such as a push's variables file, are not the template's.</summary>
    [Fact]
    public async Task GetTeamVariables_LeavesOutTheFilesOfAWorkspace()
    {
        var directory = Directory(Project("Range"), "range");
        Knows(
            directory,
            File("variables.tf", """variable "blue" {}"""),
            File("teams.tf", """variable "pushed" {}""", workspaceId: Guid.NewGuid()));

        var variables = await Read<List<ViewModels.CasterTeamVariable>>(
            await Client(await Planner()).GetAsync(TeamVariables(directory.Id), Ct));

        Assert.Equal(["blue"], variables.Select(x => x.Name));
    }

    /// <summary>A directory that names itself as its parent is read once.</summary>
    [Fact]
    public async Task GetTeamVariables_ForADirectoryThatIsItsOwnParent_ReadsItOnce()
    {
        var project = Project("Range");
        var id = Guid.NewGuid();
        var directory = Directory(project, "loop", id);
        directory.Id = id;
        Knows(directory, File("variables.tf", """variable "blue" {}"""));

        var variables = await Read<List<ViewModels.CasterTeamVariable>>(
            await Client(await Planner()).GetAsync(TeamVariables(id), Ct));

        Assert.Equal(["blue"], variables.Select(x => x.Name));
        await Factory.Caster.Received(1).GetDirectoryAsync(id, false, false, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task GetTeamVariables_WhenCasterWillNotShowTheDirectory_Is403(HttpStatusCode casterAnswer)
    {
        var id = Guid.NewGuid();
        Factory.Caster.GetDirectoryAsync(id, false, false, Arg.Any<CancellationToken>()).Throws(CasterArrangements.Answering(casterAnswer));

        var response = await Client(await Planner()).GetAsync(TeamVariables(id), Ct);

        Assert.Equal(
            $"Caster directory {id} does not exist or you cannot view it in Caster.",
            (await AssertJsonError<ViewModels.ApiError>(HttpStatusCode.Forbidden, response)).Title);
    }

    /// <summary>A parent directory Caster will not show refuses the whole read, naming the parent.</summary>
    [Fact]
    public async Task GetTeamVariables_WhenCasterWillNotShowAParent_Is403NamingTheParent()
    {
        var project = Project("Range");
        var hidden = Guid.NewGuid();
        var child = Directory(project, "child", hidden);
        Knows(child, File("variables.tf", """variable "blue" {}"""));
        Factory.Caster.GetDirectoryAsync(hidden, false, false, Arg.Any<CancellationToken>())
            .Throws(CasterArrangements.Answering(HttpStatusCode.Forbidden));

        var response = await Client(await Planner()).GetAsync(TeamVariables(child.Id), Ct);

        Assert.Equal(
            $"Caster directory {hidden} does not exist or you cannot view it in Caster.",
            (await AssertJsonError<ViewModels.ApiError>(HttpStatusCode.Forbidden, response)).Title);
    }

    /// <summary>Any other failure from Caster is answered with a 500.</summary>
    [Fact]
    public async Task GetTeamVariables_WhenCasterFails_Is500()
    {
        var id = Guid.NewGuid();
        Factory.Caster.GetDirectoryAsync(id, false, false, Arg.Any<CancellationToken>())
            .Throws(CasterArrangements.Answering(HttpStatusCode.InternalServerError));

        var response = await Client(await Planner()).GetAsync(TeamVariables(id), Ct);

        Assert.Equal(
            "Caster answered 500.\n\nStatus: 500\nResponse: \n",
            (await AssertJsonError<ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
    }

    // ---------------------------------------------------------------------------------------------

    private const string Directories = "/api/caster/directories";

    private static string TeamVariables(Guid id) => $"/api/caster/directories/{id}/team-variables";

    /// <summary>A planner, who picks the directory a MSEL deploys from.</summary>
    private Task<TestActor> Planner() => Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

    private static Project Project(string name) => new() { Id = Guid.NewGuid(), Name = name };

    private static CasterDirectory Directory(Project project, string name, Guid? parentId = null) =>
        new() { Id = Guid.NewGuid(), Name = name, ProjectId = project.Id, ParentId = parentId };

    private static CasterFile File(string name, string content, Guid? workspaceId = null) =>
        new() { Id = Guid.NewGuid(), Name = name, Content = content, WorkspaceId = workspaceId };

    private void DirectoriesOf(Project project, params CasterDirectory[] directories) =>
        Factory.Caster.GetDirectoriesByProjectAsync(project.Id, true, false, false, Arg.Any<CancellationToken>())
            .Returns(directories);

    /// <summary>Caster shows <paramref name="directory"/> and holds <paramref name="files"/> in it.</summary>
    private void Knows(CasterDirectory directory, params CasterFile[] files)
    {
        foreach (var file in files)
            file.DirectoryId = directory.Id;

        Factory.Caster.GetDirectoryAsync(directory.Id, false, false, Arg.Any<CancellationToken>()).Returns(directory);
        Factory.Caster.GetFilesByDirectoryAsync(directory.Id, true, false, Arg.Any<CancellationToken>()).Returns(files);
    }

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
    }
}
