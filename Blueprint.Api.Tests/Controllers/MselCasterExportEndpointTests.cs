// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Caster.Api.Client;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

using CasterDirectory = Caster.Api.Client.Directory;
using CasterFile = Caster.Api.Client.File;
using SystemPermission = Blueprint.Api.Data.Enumerations.SystemPermission;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// <c>CasterExportService</c> / <c>MselCasterExportController</c> - the copy of its Caster directory a MSEL carries
/// to another Caster, and the MSEL JSON download and import that move it. Reading the copy wants <c>ViewMsels</c> or
/// <c>MselContributorRequirement</c>; copying, importing and deleting want <c>EditMsels</c> or the MSEL's
/// <c>Owner</c> or <c>Editor</c> role, and are refused while the MSEL is pushed.
/// </summary>
/// <remarks>
/// Caster is <see cref="BlueprintAppFactory.Caster"/>, the client called with the caller's own token. 401 and 403
/// for these routes live in <see cref="RouteAuthorizationTests"/>.
/// </remarks>
public class MselCasterExportEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET msels/{id}/caster/export
    // ---------------------------------------------------------------------------------------------

    /// <summary>A MSEL with no copy answers 204, so a client can ask without handling an error.</summary>
    [Fact]
    public async Task Get_ForAMselWithNoCopy_Is204()
    {
        var msel = await SeedMsel();

        var response = await RootClient.GetAsync(Route(msel), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>
    /// The copy answers the directories from the root down, the shared files of the ones above, and the exported
    /// directory's contents by path, with the workspace each belongs to and without Caster's manifest.
    /// </summary>
    [Fact]
    public async Task Get_AnswersTheChainAndTheExportedDirectorysContents()
    {
        var msel = await SeedMsel(withCopy: true);

        var copy = await ReadAsync<MselCasterExport>(await RootClient.GetAsync(Route(msel), Ct), JsonOptions);

        Assert.Equal("Topology", copy.ProjectName);
        Assert.Equal([("Root", 0), ("Range", 1)], copy.Directories.Select(d => (d.Name, d.Level)));
        Assert.Equal("providers.tf", Assert.Single(copy.Directories[0].Files).Name);
        Assert.Empty(copy.Directories[1].Files);
        Assert.Equal(5, copy.Directories[1].Parallelism);
        Assert.Equal(
            [("Child/child.tf", null), ("__Workspaces__/topology/topology.auto.tfvars", "topology"), ("main.tf", null)],
            copy.Entries.Select(e => (e.Path, e.Workspace)));
        Assert.Contains("variable \"blue\"", copy.Entries.Single(e => e.Path == "main.tf").Content);
    }

    /// <summary>
    /// A workspace's files can hold credentials, so a contributor who may not change the copy gets its paths without
    /// their contents.
    /// </summary>
    [Theory]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.MoveEditor)]
    [InlineData(MselRole.Evaluator)]
    [InlineData(MselRole.Viewer)]
    public async Task Get_ForAContributorWhoIsNotAnOwnerOrEditor_AnswersThePathsWithoutContents(MselRole role)
    {
        var msel = await SeedMsel(withCopy: true);
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var copy = await ReadAsync<MselCasterExport>(await Client(actor).GetAsync(Route(msel), Ct), JsonOptions);

        Assert.False(copy.ContentsIncluded);
        Assert.Equal(3, copy.Entries.Count);
        Assert.All(copy.Entries, e => Assert.Null(e.Content));
        Assert.Null(Assert.Single(copy.Directories[0].Files).Content);
    }

    [Theory]
    [InlineData(MselRole.Owner)]
    [InlineData(MselRole.Editor)]
    public async Task Get_ForAnOwnerOrEditorOfTheMsel_AnswersTheContents(MselRole role)
    {
        var msel = await SeedMsel(withCopy: true);
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var copy = await ReadAsync<MselCasterExport>(await Client(actor).GetAsync(Route(msel), Ct), JsonOptions);

        Assert.True(copy.ContentsIncluded);
        Assert.Equal("terraform {}\n", Assert.Single(copy.Directories[0].Files).Content);
    }

    [Fact]
    public async Task Get_WithViewMselsAndEditMsels_AnswersTheContents()
    {
        var msel = await SeedMsel(withCopy: true);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels, SystemPermission.EditMsels).SeedAsync();

        var copy = await ReadAsync<MselCasterExport>(await Client(actor).GetAsync(Route(msel), Ct), JsonOptions);

        Assert.True(copy.ContentsIncluded);
    }

    [Fact]
    public async Task Get_WithViewMselsOnly_AnswersThePathsWithoutContents()
    {
        var msel = await SeedMsel(withCopy: true);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var copy = await ReadAsync<MselCasterExport>(await Client(actor).GetAsync(Route(msel), Ct), JsonOptions);

        Assert.False(copy.ContentsIncluded);
    }

    [Fact]
    public async Task Get_ForAMselThatIsNotThere_Is404()
    {
        var response = await RootClient.GetAsync($"api/msels/{Guid.NewGuid()}/caster/export", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // GET msels/{id}/caster/team-variables
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A MSEL with only its copy maps its teams to the variables the copy's template declares: the exported
    /// directory's and those above it, not a child directory's.
    /// </summary>
    [Fact]
    public async Task TeamVariables_OfAMselWithOnlyItsCopy_AreTheCopysTemplates()
    {
        var msel = await SeedMsel(withCopy: true);

        var variables = await ReadAsync<List<CasterTeamVariable>>(await RootClient.GetAsync(TeamVariablesRoute(msel), Ct), JsonOptions);

        Assert.Equal("blue", Assert.Single(variables).Name);
    }

    /// <summary>A MSEL that links a directory reads the variables from Caster, as the directory's own route does.</summary>
    [Fact]
    public async Task TeamVariables_OfAMselThatLinksADirectory_AreReadFromCaster()
    {
        var directoryId = Guid.NewGuid();
        var msel = await SeedMsel(withCopy: true, directoryId: directoryId);
        Factory.Caster.ShowsDirectory(directoryId);
        Factory.Caster.GetFilesByDirectoryAsync(directoryId, true, false, Arg.Any<CancellationToken>())
            .Returns([new CasterFile { Name = "vars.tf", Content = "variable \"green\" {\n  type = string\n}\n" }]);

        var variables = await ReadAsync<List<CasterTeamVariable>>(await RootClient.GetAsync(TeamVariablesRoute(msel), Ct), JsonOptions);

        Assert.Equal("green", Assert.Single(variables).Name);
    }

    [Fact]
    public async Task TeamVariables_OfAMselWithNeither_AreEmpty()
    {
        var msel = await SeedMsel();

        var variables = await ReadAsync<List<CasterTeamVariable>>(await RootClient.GetAsync(TeamVariablesRoute(msel), Ct), JsonOptions);

        Assert.Empty(variables);
    }

    // ---------------------------------------------------------------------------------------------
    // The reads: GET caster/export and GET caster/team-variables
    // ---------------------------------------------------------------------------------------------

    /// <summary>The two reads, the copy and the team variables, which share their gate.</summary>
    public static TheoryData<string> Reads => new() { "caster/export", "caster/team-variables" };

    private static readonly MselRole[] ContributorRoles =
        [MselRole.Owner, MselRole.Editor, MselRole.Approver, MselRole.MoveEditor, MselRole.Evaluator, MselRole.Viewer];

    public static TheoryData<string, MselRole> ReadsByContributor()
    {
        var data = new TheoryData<string, MselRole>();
        foreach (var read in new[] { "caster/export", "caster/team-variables" })
            foreach (var role in ContributorRoles)
                data.Add(read, role);
        return data;
    }

    /// <summary>Every role on the Contributors tab reads the copy and its team variables.</summary>
    [Theory]
    [MemberData(nameof(ReadsByContributor))]
    public async Task Read_ForAContributorOfTheMsel_Is200(string read, MselRole role)
    {
        var msel = await SeedMsel(withCopy: true);
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Client(actor).GetAsync(ReadRoute(msel, read), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Reads))]
    public async Task Read_ForTheCreatorOfTheMsel_Is200(string read)
    {
        var actor = await Actor().SeedAsync();
        var msel = await SeedMsel(withCopy: true, createdBy: actor.Id);

        var response = await Client(actor).GetAsync(ReadRoute(msel, read), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Reads))]
    public async Task Read_WithViewMsels_Is200(string read)
    {
        var msel = await SeedMsel(withCopy: true);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync(ReadRoute(msel, read), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>CreateMsels reads a template's copy, as it reads the template it creates MSELs from.</summary>
    [Theory]
    [MemberData(nameof(Reads))]
    public async Task Read_OfATemplate_WithCreateMsels_Is200(string read)
    {
        var msel = await SeedMsel(withCopy: true, isTemplate: true);
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(actor).GetAsync(ReadRoute(msel, read), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Reads))]
    public async Task Read_OfAMselThatIsNotATemplate_WithCreateMsels_Is403(string read)
    {
        var msel = await SeedMsel(withCopy: true);
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(actor).GetAsync(ReadRoute(msel, read), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Being on one of the MSEL's teams makes a participant, who does not read the planners' topology.</summary>
    [Theory]
    [MemberData(nameof(Reads))]
    public async Task Read_ForAMemberOfOneOfTheMselsTeams_Is403(string read)
    {
        var msel = await SeedMsel(withCopy: true);
        var team = TestData.Team(msel.Id);
        await Seed(team);
        var actor = await Actor().OnTeam(team).SeedAsync();

        var response = await Client(actor).GetAsync(ReadRoute(msel, read), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Reads))]
    public async Task Read_is_forbidden_for_a_caller_holding_Viewer_only_in_another_msel(string read)
    {
        var msel = await SeedMsel(withCopy: true);
        var actor = await Actor().OnNewMsel(MselRole.Viewer).SeedAsync();

        var response = await Client(actor).GetAsync(ReadRoute(msel, read), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Reads))]
    public async Task Read_is_forbidden_for_a_caller_holding_EditMsels_only(string read)
    {
        var msel = await SeedMsel(withCopy: true);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).GetAsync(ReadRoute(msel, read), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // The writes: POST (copy from Caster), POST import, DELETE
    // ---------------------------------------------------------------------------------------------

    /// <summary>The three writes, each sent against a MSEL it can succeed on.</summary>
    public static TheoryData<string> Writes => new() { "capture", "import", "delete" };

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Write_ForAnOwnerOfTheMsel_IsAllowed(string write)
    {
        var msel = await SeedFor(write);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Send(Client(actor), write, msel);

        Assert.True(response.IsSuccessStatusCode, $"{write} answered {response.StatusCode}");
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Write_ForTheCreatorOfTheMsel_IsAllowed(string write)
    {
        var actor = await Actor().SeedAsync();
        var msel = await SeedFor(write, createdBy: actor.Id);

        var response = await Send(Client(actor), write, msel);

        Assert.True(response.IsSuccessStatusCode, $"{write} answered {response.StatusCode}");
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Write_WithEditMsels_IsAllowed(string write)
    {
        var msel = await SeedFor(write);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Send(Client(actor), write, msel);

        Assert.True(response.IsSuccessStatusCode, $"{write} answered {response.StatusCode}");
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Write_ForAnEditorOfTheMsel_IsAllowed(string write)
    {
        var msel = await SeedFor(write);
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var response = await Send(Client(actor), write, msel);

        Assert.True(response.IsSuccessStatusCode, $"{write} answered {response.StatusCode}");
    }

    /// <summary>The MSEL's other contributors may only read the copy.</summary>
    [Theory]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.MoveEditor)]
    [InlineData(MselRole.Evaluator)]
    [InlineData(MselRole.Viewer)]
    public async Task Write_is_forbidden_for_a_contributor_who_is_not_an_owner_or_editor(MselRole role)
    {
        var msel = await SeedFor("delete");
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Send(Client(actor), "delete", msel);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await HasCopy(msel.Id));
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Write_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel(string write)
    {
        var msel = await SeedFor(write);
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        var response = await Send(Client(actor), write, msel);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Write_is_forbidden_for_a_caller_holding_ViewMsels_only(string write)
    {
        var msel = await SeedFor(write);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Send(Client(actor), write, msel);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>The pushed topology was built from the directory, so the copy is locked until the integrations are removed.</summary>
    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Write_WhileTheMselIsPushed_Is409AndChangesNothing(string write)
    {
        var msel = await SeedFor(write, adjust: m =>
        {
            m.Status = MselItemStatus.Deployed;
            m.CasterWorkspaceId = Guid.NewGuid();
        });
        var before = await ReadBack(db => db.MselCasterExports.Where(e => e.MselId == msel.Id).Select(e => e.DateExported).ToListAsync(Ct));

        var response = await Send(RootClient, write, msel);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(before, await ReadBack(db => db.MselCasterExports.Where(e => e.MselId == msel.Id).Select(e => e.DateExported).ToListAsync(Ct)));
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Write_ForAMselThatIsNotThere_Is404(string write)
    {
        var response = await Send(RootClient, write, TestData.Msel());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // POST msels/{id}/caster/export
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The copy holds the chain from the project's root: each directory above with its settings and its shared
    /// files only, not its workspaces' or its deleted ones, and the exported directory as Caster exported it.
    /// </summary>
    [Fact]
    public async Task Capture_CopiesTheChainAboveTheDirectoryAndItsExport()
    {
        var chain = ArrangeChain();
        var msel = await SeedMsel(directoryId: chain.Target);

        var copy = await ReadAsync<MselCasterExport>(await RootClient.PostAsync(Route(msel), null, Ct), JsonOptions);

        Assert.Equal("Topology", copy.ProjectName);
        Assert.Equal(chain.Project, copy.SourceProjectId);
        Assert.Equal(chain.Target, copy.SourceDirectoryId);
        Assert.Equal([("Root", 0), ("Range", 1)], copy.Directories.Select(d => (d.Name, d.Level)));
        Assert.Equal("1.5.0", copy.Directories[0].TerraformVersion);
        Assert.Equal(7, copy.Directories[1].Parallelism);
        Assert.Equal(["providers.tf"], copy.Directories[0].Files.Select(f => f.Name));
        Assert.Equal(["main.tf"], copy.Entries.Select(e => e.Path));
        Assert.Empty(copy.Warnings);
        Assert.Equal(Root.Id, copy.ExportedBy);
        var stored = await ReadBack(db => db.MselCasterExports.Include(e => e.Directories).SingleAsync(e => e.MselId == msel.Id, Ct));
        Assert.Equal(2, stored.Directories.Count);
    }

    /// <summary>
    /// The workspaces a push made, blueprint-{msel id}, hold a past push's variables rather than the topology, so
    /// the copy drops them and their settings; the directory's other workspaces stay.
    /// </summary>
    [Fact]
    public async Task Capture_DropsTheWorkspacesAPushMade()
    {
        var pushed = $"blueprint-{Guid.NewGuid()}";
        var chain = ArrangeChain(TestData.CasterArchive(
            ("main.tf", "terraform {}"),
            ($"__Workspaces__/{pushed}/", null),
            ($"__Workspaces__/{pushed}/{pushed}.auto.tfvars", "view_id = \"x\""),
            ("__Workspaces__/kept/kept.tfvars", "a = 1"),
            ($"Child/__Workspaces__/{pushed}/vars.tfvars", "b = 2"),
            ("__Caster__/manifest.json", "{\"schemaVersion\":1,\"workspaces\":{\"__Workspaces__/" + pushed + "\":{\"parallelism\":2},\"__Workspaces__/kept\":{\"parallelism\":3}}}")));
        var msel = await SeedMsel(directoryId: chain.Target);

        await RootClient.PostAsync(Route(msel), null, Ct);

        var stored = await ReadBack(db => db.MselCasterExports.SingleAsync(e => e.MselId == msel.Id, Ct));
        using var zip = new ZipArchive(new MemoryStream(stored.Archive));
        Assert.Equal(["__Caster__/manifest.json", "__Workspaces__/kept/kept.tfvars", "main.tf"], zip.Entries.Select(e => e.FullName).Order());
        using var manifest = JsonDocument.Parse(new StreamReader(zip.GetEntry("__Caster__/manifest.json").Open()).ReadToEnd());
        Assert.Equal(["__Workspaces__/kept"], manifest.RootElement.GetProperty("workspaces").EnumerateObject().Select(p => p.Name));
    }

    /// <summary>A new copy replaces the MSEL's old one.</summary>
    [Fact]
    public async Task Capture_ReplacesTheMselsCopy()
    {
        var chain = ArrangeChain();
        var msel = await SeedMsel(withCopy: true, directoryId: chain.Target);

        await RootClient.PostAsync(Route(msel), null, Ct);

        var stored = await ReadBack(db => db.MselCasterExports.Include(e => e.Directories).Where(e => e.MselId == msel.Id).ToListAsync(Ct));
        Assert.Equal("Range", Assert.Single(stored).Directories.Single(d => d.Level == 1).Name);
        Assert.Equal(chain.Project, stored[0].SourceProjectId);
        Assert.Equal(2, await ReadBack(db => db.MselCasterExportDirectories.CountAsync(d => d.MselCasterExportId == msel.Id, Ct)));
    }

    /// <summary>An archive carries no designs, so an enabled design on the chain is a warning.</summary>
    [Fact]
    public async Task Capture_OfAChainWithAnEnabledDesign_WarnsThatTheCopyLacksIt()
    {
        var chain = ArrangeChain();
        Factory.Caster.GetDesignsByDirectoryAsync(chain.Root, Arg.Any<CancellationToken>())
            .Returns([new Design { Name = "Network", Enabled = true }, new Design { Name = "Off", Enabled = false }]);
        var msel = await SeedMsel(directoryId: chain.Target);

        var copy = await ReadAsync<MselCasterExport>(await RootClient.PostAsync(Route(msel), null, Ct), JsonOptions);

        Assert.Equal(
            "The design \"Network\" in the directory \"Root\" is not part of the copy, so an import does not have it.",
            Assert.Single(copy.Warnings));
    }

    /// <summary>The copy is read as the caller, so a directory Caster does not show them is a 403, and the old copy stays.</summary>
    [Fact]
    public async Task Capture_OfADirectoryCasterRefuses_Is403AndKeepsTheOldCopy()
    {
        var directoryId = Guid.NewGuid();
        Factory.Caster.RefusesDirectory(directoryId);
        var msel = await SeedMsel(withCopy: true, directoryId: directoryId);

        var response = await RootClient.PostAsync(Route(msel), null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Topology", await ReadBack(db => db.MselCasterExports.Where(e => e.MselId == msel.Id).Select(e => e.ProjectName).SingleAsync(Ct)));
    }

    [Fact]
    public async Task Capture_OfAMselWithNoDirectory_Is409()
    {
        var msel = await SeedMsel();

        var response = await RootClient.PostAsync(Route(msel), null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // POST msels/{id}/caster/export/import
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// An import creates a project by the copy's project name, the chain under it with each directory's settings
    /// and the shared files of the ones above, imports Caster's export into the last, and links the MSEL to it.
    /// </summary>
    [Fact]
    public async Task Import_RecreatesTheChainAsANewProjectAndLinksTheMsel()
    {
        var msel = await SeedMsel(withCopy: true);
        var created = ArrangeImport();

        var result = await ReadAsync<MselCasterImportResult>(await RootClient.PostAsync($"{Route(msel)}/import", null, Ct), JsonOptions);

        Assert.Equal(created.Target, result.CasterDirectoryId);
        Assert.Equal(created.Target, result.Msel.CasterDirectoryId);
        Assert.Equal(created.Target, await ReadBack(db => db.Msels.Where(m => m.Id == msel.Id).Select(m => m.CasterDirectoryId).SingleAsync(Ct)));
        await Factory.Caster.Received(1).CreateProjectAsync(Arg.Is<CreateProjectCommand>(c => c.Name == "Topology"), Arg.Any<CancellationToken>());
        await Factory.Caster.Received(1).CreateDirectoryAsync(
            Arg.Is<CreateDirectoryCommand>(c => c.ProjectId == created.Project && c.ParentId == null && c.Name == "Root" && c.TerraformVersion == "1.5.0"),
            Arg.Any<CancellationToken>());
        await Factory.Caster.Received(1).CreateDirectoryAsync(
            Arg.Is<CreateDirectoryCommand>(c => c.ProjectId == created.Project && c.ParentId == created.Root && c.Name == "Range" && c.Parallelism == 5),
            Arg.Any<CancellationToken>());
        await Factory.Caster.Received(1).CreateFileAsync(
            Arg.Is<CreateFileCommand>(c => c.DirectoryId == created.Root && c.Name == "providers.tf" && c.WorkspaceId == null),
            Arg.Any<CancellationToken>());
        await Factory.Caster.Received(1).ImportDirectoryAsync(
            created.Target, false, Arg.Is<FileParameter>(f => f.FileName == "export.zip"), Arg.Any<CancellationToken>());
        // an open MSEL page learns of the new directory
        Assert.Contains(msel.Id.ToString(), Hub.Recipients(MainHubMethods.MselUpdated, msel.Id));
    }

    /// <summary>A failed import deletes the project it created, so nothing is left half imported and the MSEL stays unlinked.</summary>
    [Fact]
    public async Task Import_ThatCasterRefusesPartWay_DeletesTheProjectAndIs409()
    {
        var msel = await SeedMsel(withCopy: true);
        var created = ArrangeImport();
        Factory.Caster.ImportDirectoryAsync(created.Target, Arg.Any<bool?>(), Arg.Any<FileParameter>(), Arg.Any<CancellationToken>())
            .Throws(CasterArrangements.Answering(HttpStatusCode.BadRequest));

        var response = await RootClient.PostAsync($"{Route(msel)}/import", null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await Factory.Caster.Received(1).DeleteProjectAsync(created.Project, Arg.Any<CancellationToken>());
        Assert.Null(await ReadBack(db => db.Msels.Where(m => m.Id == msel.Id).Select(m => m.CasterDirectoryId).SingleAsync(Ct)));
    }

    /// <summary>
    /// Two imports at once each make a project, and only one may link the MSEL; the other deletes the project it made.
    /// </summary>
    [Fact]
    public async Task Import_WhenTheMselIsLinkedWhileItsCopyImports_DeletesItsProjectAndIs409()
    {
        var msel = await SeedMsel(withCopy: true);
        var created = ArrangeImport();
        var winner = Guid.NewGuid();
        Factory.Caster.ImportDirectoryAsync(created.Target, Arg.Any<bool?>(), Arg.Any<FileParameter>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                // the other import links the MSEL first
                using var other = NewContext();
                other.Msels.Where(m => m.Id == msel.Id).ExecuteUpdate(s => s.SetProperty(m => m.CasterDirectoryId, winner));
                return new ImportDirectoryResult { LockedFiles = [] };
            });

        var response = await RootClient.PostAsync($"{Route(msel)}/import", null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await Factory.Caster.Received(1).DeleteProjectAsync(created.Project, Arg.Any<CancellationToken>());
        Assert.Equal(winner, await ReadBack(db => db.Msels.Where(m => m.Id == msel.Id).Select(m => m.CasterDirectoryId).SingleAsync(Ct)));
    }

    /// <summary>
    /// Caster has held a new directory's name to letters, digits, '-', '_' and '.' since 3.7.0, while one made before
    /// keeps a name like "Basic Topology". The import gives such a directory a name Caster takes, and says so.
    /// </summary>
    [Theory]
    [InlineData("Basic Topology", "Basic_Topology")]
    [InlineData("Range (v2)", "Range__v2_")]
    [InlineData("..", "_directory")]
    [InlineData("__Workspaces__", "__Workspaces___directory")]
    public async Task Import_OfADirectoryNamedAsCasterNoLongerTakes_RenamesItToFitAndWarns(string name, string imported)
    {
        var msel = await SeedMsel();
        var copy = TestData.MselCasterExport(msel.Id);
        copy.Directories.Single(d => d.Level == 1).Name = name;
        await Seed(copy);
        var created = ArrangeImport();

        var result = await ReadAsync<MselCasterImportResult>(await RootClient.PostAsync($"{Route(msel)}/import", null, Ct), JsonOptions);

        await Factory.Caster.Received(1).CreateDirectoryAsync(
            Arg.Is<CreateDirectoryCommand>(c => c.ParentId == created.Root && c.Name == imported), Arg.Any<CancellationToken>());
        await Factory.Caster.Received(1).CreateDirectoryAsync(
            Arg.Is<CreateDirectoryCommand>(c => c.ParentId == null && c.Name == "Root"), Arg.Any<CancellationToken>());
        Assert.Equal($"Caster no longer takes the directory name \"{name}\", so it was imported as \"{imported}\".", Assert.Single(result.Warnings));
    }

    /// <summary>A refusal passes on what Caster said was wrong, field by field, not only its status.</summary>
    [Fact]
    public async Task Import_ThatCasterRefusesAsInvalid_SaysWhatCasterRefused()
    {
        var msel = await SeedMsel(withCopy: true);
        var created = ArrangeImport();
        Factory.Caster.ImportDirectoryAsync(created.Target, Arg.Any<bool?>(), Arg.Any<FileParameter>(), Arg.Any<CancellationToken>())
            .Throws(new ApiException("Caster answered 400.", 400,
                """{"title":"One or more validation errors occurred.","status":400,"errors":{"Archive":["Archive must be a .zip, .tar.gz or .tgz file."]}}""",
                new Dictionary<string, IEnumerable<string>>(), null));

        var response = await RootClient.PostAsync($"{Route(msel)}/import", null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("One or more validation errors occurred. Archive: Archive must be a .zip, .tar.gz or .tgz file.", await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>A caller Caster will not let create a project is a 403 that says which permission it takes.</summary>
    [Fact]
    public async Task Import_ByACallerCasterWillNotLetCreateAProject_Is403()
    {
        var msel = await SeedMsel(withCopy: true);
        Factory.Caster.CreateProjectAsync(Arg.Any<CreateProjectCommand>(), Arg.Any<CancellationToken>())
            .Throws(CasterArrangements.Answering(HttpStatusCode.Forbidden));

        var response = await RootClient.PostAsync($"{Route(msel)}/import", null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("Create Projects", await response.Content.ReadAsStringAsync(Ct));
        await Factory.Caster.DidNotReceive().DeleteProjectAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Import_OfAMselThatLinksADirectory_Is409()
    {
        var msel = await SeedMsel(withCopy: true, directoryId: Guid.NewGuid());

        var response = await RootClient.PostAsync($"{Route(msel)}/import", null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Import_OfAMselWithNoCopy_Is404()
    {
        var msel = await SeedMsel();

        var response = await RootClient.PostAsync($"{Route(msel)}/import", null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE msels/{id}/caster/export
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_RemovesTheCopyWithItsDirectoriesAndFiles()
    {
        var msel = await SeedMsel(withCopy: true);

        var response = await RootClient.DeleteAsync(Route(msel), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await HasCopy(msel.Id));
        Assert.Equal(0, await ReadBack(db => db.MselCasterExportDirectories.CountAsync(d => d.MselCasterExportId == msel.Id, Ct)));
    }

    [Fact]
    public async Task Delete_OfAMselWithNoCopy_Is404()
    {
        var msel = await SeedMsel();

        var response = await RootClient.DeleteAsync(Route(msel), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // The MSEL file: download, import, copy
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Downloading a MSEL that links a directory copies the directory first, so the file carries the topology as
    /// it is now; the archive travels as base64.
    /// </summary>
    [Fact]
    public async Task Download_OfAMselThatLinksADirectory_CarriesAFreshCopy()
    {
        var chain = ArrangeChain();
        var msel = await SeedMsel(withCopy: true, directoryId: chain.Target);

        var file = await Download(msel);

        var copy = file.GetProperty("CasterExport");
        Assert.Equal(chain.Project, copy.GetProperty("SourceProjectId").GetGuid());
        Assert.Equal(["Root", "Range"], copy.GetProperty("Directories").EnumerateArray()
            .OrderBy(d => d.GetProperty("Level").GetInt32()).Select(d => d.GetProperty("Name").GetString()));
        using var zip = new ZipArchive(new MemoryStream(copy.GetProperty("Archive").GetBytesFromBase64()));
        Assert.Equal("main.tf", Assert.Single(zip.Entries).FullName);
        Assert.Equal(chain.Project, await ReadBack(db => db.MselCasterExports.Where(e => e.MselId == msel.Id).Select(e => e.SourceProjectId).SingleAsync(Ct)));
    }

    /// <summary>
    /// A download whose directory Caster cannot export (Caster down, the directory gone, or a downloader Caster does
    /// not show it to) still downloads, with the stored copy: moving a MSEL never depends on Caster.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Download_WhenCasterCannotExportTheDirectory_CarriesTheStoredCopy(HttpStatusCode status)
    {
        var chain = ArrangeChain();
        Factory.Caster.ExportDirectoryAsync(chain.Target, Arg.Any<ArchiveType?>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>())
            .Throws(CasterArrangements.Answering(status));
        var msel = await SeedMsel(withCopy: true, directoryId: chain.Target);

        var file = await Download(msel);

        Assert.Equal("Topology", file.GetProperty("CasterExport").GetProperty("ProjectName").GetString());
        Assert.Equal("Topology", await ReadBack(db => db.MselCasterExports.Where(e => e.MselId == msel.Id).Select(e => e.ProjectName).SingleAsync(Ct)));
    }

    /// <summary>The copy follows the linked directory whether or not Caster is on, so turning Caster on elsewhere deploys it as it is.</summary>
    [Fact]
    public async Task Download_OfAMselThatLinksADirectoryWithCasterOff_StillCarriesAFreshCopy()
    {
        var chain = ArrangeChain();
        var msel = await SeedMsel(directoryId: chain.Target, adjust: m => m.UseCaster = false);

        var file = await Download(msel);

        Assert.Equal(chain.Project, file.GetProperty("CasterExport").GetProperty("SourceProjectId").GetGuid());
    }

    /// <summary>A MSEL with only its copy downloads the copy as it is, and does not call Caster.</summary>
    [Fact]
    public async Task Download_OfAMselWithOnlyItsCopy_CarriesTheCopyUnchanged()
    {
        var msel = await SeedMsel(withCopy: true);

        var file = await Download(msel);

        Assert.Equal("Topology", file.GetProperty("CasterExport").GetProperty("ProjectName").GetString());
    }

    /// <summary>
    /// In a Caster without the directory, the import drops the directory, keeps the copy under the new MSEL's id,
    /// and says the push will import it.
    /// </summary>
    [Fact]
    public async Task ImportFile_IntoACasterWithoutTheDirectory_KeepsTheCopyAndSaysThePushImportsIt()
    {
        var directoryId = Guid.NewGuid();
        Factory.Caster.RefusesDirectory(directoryId);
        var file = FileOf(m =>
        {
            m.UsePlayer = true;
            m.UseCaster = true;
            m.CasterDirectoryId = directoryId;
            m.CasterExport = TestData.MselCasterExport(m.Id);
        });

        var result = await ImportFile(file);

        Assert.Null(result.Msel.CasterDirectoryId);
        Assert.Contains(result.Changes, c => c.Field == "casterDirectoryId" &&
            c.Message == "The Caster directory is not in this Caster, or you cannot view it, so pushing imports the MSEL's copy of it (from the Caster project \"Topology\") as a new Caster project.");
        var stored = await ReadBack(db => db.MselCasterExports.Include(e => e.Directories).ThenInclude(d => d.Files).AsSplitQuery().SingleAsync(e => e.MselId == result.Msel.Id, Ct));
        Assert.Equal(["Root", "Range"], stored.Directories.OrderBy(d => d.Level).Select(d => d.Name));
        Assert.Equal("providers.tf", stored.Directories.Single(d => d.Level == 0).Files.Single().Name);
    }

    /// <summary>In the Caster the file came from, the import keeps the directory as well as the copy.</summary>
    [Fact]
    public async Task ImportFile_IntoTheCasterWithTheDirectory_KeepsTheDirectoryAndTheCopy()
    {
        var directoryId = Guid.NewGuid();
        Factory.Caster.ShowsDirectory(directoryId);
        var file = FileOf(m =>
        {
            m.UseCaster = true;
            m.CasterDirectoryId = directoryId;
            m.CasterExport = TestData.MselCasterExport(m.Id);
        });

        var result = await ImportFile(file);

        Assert.Equal(directoryId, result.Msel.CasterDirectoryId);
        Assert.True(await HasCopy(result.Msel.Id));
        Assert.DoesNotContain(result.Changes, c => c.Field == "casterDirectoryId");
    }

    /// <summary>A Caster MSEL with neither a directory it can use nor a copy tells the planner to pick one.</summary>
    [Fact]
    public async Task ImportFile_OfACasterMselWhoseDirectoryIsNotHereAndNoCopy_SaysToSelectADirectory()
    {
        var directoryId = Guid.NewGuid();
        Factory.Caster.RefusesDirectory(directoryId);
        var file = FileOf(m =>
        {
            m.UseCaster = true;
            m.CasterDirectoryId = directoryId;
        });

        var result = await ImportFile(file);

        Assert.Contains(result.Changes, c => c.Field == "casterDirectoryId" &&
            c.Message == "The Caster directory is not in this Caster, or you cannot view it. Select the MSEL's Caster directory before pushing.");
    }

    /// <summary>A copy whose archive is not a zip could never be imported into Caster, so the file is refused.</summary>
    [Fact]
    public async Task ImportFile_WithACopyWhoseArchiveIsNotAZip_Is400()
    {
        var file = FileOf(m =>
        {
            m.CasterExport = TestData.MselCasterExport(m.Id);
            m.CasterExport.Archive = Encoding.UTF8.GetBytes("not a zip");
        });

        var response = await ImportFileResponse(file);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// An archive is read whole when it is shown, so one that expands past the limits is refused, however small it
    /// is: a file of zeros compresses a thousandfold.
    /// </summary>
    [Fact]
    public async Task ImportFile_WithACopyHoldingAFileTooBigOnceExpanded_Is400()
    {
        var file = FileOf(m =>
        {
            m.CasterExport = TestData.MselCasterExport(m.Id);
            m.CasterExport.Archive = TestData.CasterArchive(("main.tf", new string('0', CasterExportService.MaxEntryBytes + 1)));
        });

        var response = await ImportFileResponse(file);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("main.tf", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task ImportFile_WithACopyThatExpandsPastTheLimitInAll_Is400()
    {
        var count = (int)(CasterExportService.MaxExpandedBytes / CasterExportService.MaxEntryBytes) + 1;
        var file = FileOf(m =>
        {
            m.CasterExport = TestData.MselCasterExport(m.Id);
            m.CasterExport.Archive = TestData.CasterArchive(
                [.. Enumerable.Range(0, count).Select(i => ($"f{i}.tf", new string('0', CasterExportService.MaxEntryBytes)))]);
        });

        var response = await ImportFileResponse(file);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("expands to more than", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task ImportFile_WithACopyHoldingTooManyFiles_Is400()
    {
        var file = FileOf(m =>
        {
            m.CasterExport = TestData.MselCasterExport(m.Id);
            m.CasterExport.Archive = TestData.CasterArchive(
                [.. Enumerable.Range(0, CasterExportService.MaxArchiveEntries + 1).Select(i => ($"f{i}.tf", ""))]);
        });

        var response = await ImportFileResponse(file);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>A copy of the MSEL carries the copy of its directory under new ids.</summary>
    [Fact]
    public async Task Copy_OfAMselWithACopy_CopiesItUnderNewIds()
    {
        var msel = await SeedMsel(withCopy: true);
        var original = await ReadBack(db => db.MselCasterExportDirectories.Where(d => d.MselCasterExportId == msel.Id).Select(d => d.Id).ToListAsync(Ct));

        var response = await RootClient.PostAsync($"api/msels/{msel.Id}/copy", null, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var copy = await response.Content.ReadFromJsonAsync<Msel>(JsonOptions, Ct);
        var copied = await ReadBack(db => db.MselCasterExportDirectories.Include(d => d.Files).Where(d => d.MselCasterExportId == copy.Id).ToListAsync(Ct));
        Assert.Equal(2, copied.Count);
        Assert.DoesNotContain(copied, d => original.Contains(d.Id));
        Assert.Equal("providers.tf", copied.Single(d => d.Level == 0).Files.Single().Name);
        Assert.Equal(2, original.Count);
    }

    /// <summary>
    /// The caller owns a copy of the MSEL, so it carries the copy of the directory only for a caller who may read the
    /// directory's files on this MSEL.
    /// </summary>
    [Theory]
    [InlineData(MselRole.Viewer)]
    [InlineData(MselRole.Approver)]
    public async Task Copy_ByAContributorWhoMayNotReadTheFiles_LeavesTheCopyOut(MselRole role)
    {
        var msel = await SeedMsel(withCopy: true);
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).OnMsel(msel, role).SeedAsync();

        var response = await Client(actor).PostAsync($"api/msels/{msel.Id}/copy", null, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var copy = await response.Content.ReadFromJsonAsync<Msel>(JsonOptions, Ct);
        Assert.False(await HasCopy(copy.Id));
        Assert.True(await HasCopy(msel.Id));
    }

    [Fact]
    public async Task Copy_ByAnEditorOfTheMsel_CarriesTheCopy()
    {
        var msel = await SeedMsel(withCopy: true);
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).OnMsel(msel, MselRole.Editor).SeedAsync();

        var response = await Client(actor).PostAsync($"api/msels/{msel.Id}/copy", null, Ct);

        var copy = await response.Content.ReadFromJsonAsync<Msel>(JsonOptions, Ct);
        Assert.True(await HasCopy(copy.Id));
    }

    /// <summary>A copy of the directory the MSEL linked before is not the topology of the one it links now, so it goes.</summary>
    [Fact]
    public async Task Update_ThatLinksAnotherDirectory_DeletesTheCopyOfTheOldOne()
    {
        var msel = await SeedMsel(withCopy: true, directoryId: Guid.NewGuid());
        var other = Guid.NewGuid();
        Factory.Caster.ShowsDirectory(other);

        var response = await RootClient.PutAsJsonAsync($"api/msels/{msel.Id}", Edit(msel, other), JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(await HasCopy(msel.Id));
    }

    /// <summary>Clearing the directory, or saving other settings, keeps the copy: a push imports it.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Update_ThatKeepsOrClearsTheDirectory_KeepsTheCopy(bool clear)
    {
        var directoryId = Guid.NewGuid();
        var msel = await SeedMsel(withCopy: true, directoryId: directoryId);

        var response = await RootClient.PutAsJsonAsync($"api/msels/{msel.Id}", Edit(msel, clear ? null : directoryId), JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await HasCopy(msel.Id));
    }

    // ---------------------------------------------------------------------------------------------

    private static Msel Edit(MselEntity entity, Guid? casterDirectoryId) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        Description = entity.Description,
        Status = entity.Status,
        UseCaster = entity.UseCaster,
        CasterDirectoryId = casterDirectoryId
    };

    private static string Route(MselEntity msel) => $"api/msels/{msel.Id}/caster/export";

    private static string TeamVariablesRoute(MselEntity msel) => $"api/msels/{msel.Id}/caster/team-variables";

    private static string ReadRoute(MselEntity msel, string read) => $"api/msels/{msel.Id}/{read}";

    private sealed record Chain(Guid Project, Guid Root, Guid Target);

    /// <summary>
    /// Caster shows the caller the project "Topology", its root directory "Root" holding a shared file, a
    /// workspace's file and a deleted file, and the directory "Range" below it, which exports as
    /// <paramref name="archive"/> (by default, one main.tf).
    /// </summary>
    private Chain ArrangeChain(byte[] archive = null)
    {
        var chain = new Chain(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Factory.Caster.GetDirectoryAsync(chain.Root, false, false, Arg.Any<CancellationToken>())
            .Returns(new CasterDirectory { Id = chain.Root, Name = "Root", ProjectId = chain.Project, TerraformVersion = "1.5.0" });
        Factory.Caster.GetDirectoryAsync(chain.Target, false, false, Arg.Any<CancellationToken>())
            .Returns(new CasterDirectory { Id = chain.Target, Name = "Range", ProjectId = chain.Project, ParentId = chain.Root, Parallelism = 7 });
        Factory.Caster.GetProjectAsync(chain.Project, Arg.Any<CancellationToken>())
            .Returns(new Project { Id = chain.Project, Name = "Topology" });
        Factory.Caster.GetFilesByDirectoryAsync(chain.Root, true, false, Arg.Any<CancellationToken>())
            .Returns(
            [
                new CasterFile { Name = "providers.tf", Content = "terraform {}", DirectoryId = chain.Root },
                new CasterFile { Name = "ws.auto.tfvars", Content = "a = 1", DirectoryId = chain.Root, WorkspaceId = Guid.NewGuid() },
                new CasterFile { Name = "old.tf", Content = "", DirectoryId = chain.Root, IsDeleted = true }
            ]);
        Factory.Caster.GetDesignsByDirectoryAsync(Arg.Is<Guid>(id => id == chain.Root || id == chain.Target), Arg.Any<CancellationToken>())
            .Returns([]);
        Factory.Caster.ExportDirectoryAsync(chain.Target, ArchiveType.Zip, false, Arg.Any<CancellationToken>())
            .Returns(_ => new FileResponse(200, new Dictionary<string, IEnumerable<string>>(),
                new MemoryStream(archive ?? TestData.CasterArchive(("main.tf", "terraform {}"))), null, null));
        return chain;
    }

    /// <summary>Caster creates a project, a root directory and the directory below it, and imports the archive.</summary>
    private Chain ArrangeImport()
    {
        var created = new Chain(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Factory.Caster.CreateProjectAsync(Arg.Any<CreateProjectCommand>(), Arg.Any<CancellationToken>())
            .Returns(new Project { Id = created.Project, Name = "Topology" });
        Factory.Caster.CreateDirectoryAsync(Arg.Is<CreateDirectoryCommand>(c => c.ProjectId == created.Project && c.ParentId == null), Arg.Any<CancellationToken>())
            .Returns(new CasterDirectory { Id = created.Root, Name = "Root", ProjectId = created.Project });
        Factory.Caster.CreateDirectoryAsync(Arg.Is<CreateDirectoryCommand>(c => c.ProjectId == created.Project && c.ParentId == created.Root), Arg.Any<CancellationToken>())
            .Returns(new CasterDirectory { Id = created.Target, Name = "Range", ProjectId = created.Project, ParentId = created.Root });
        Factory.Caster.CreateFileAsync(Arg.Any<CreateFileCommand>(), Arg.Any<CancellationToken>())
            .Returns(new CasterFile());
        Factory.Caster.ImportDirectoryAsync(created.Target, Arg.Any<bool?>(), Arg.Any<FileParameter>(), Arg.Any<CancellationToken>())
            .Returns(new ImportDirectoryResult { LockedFiles = [] });
        return created;
    }

    /// <summary>A MSEL each write can succeed on: one that links a directory Caster exports, or one with a copy.</summary>
    private async Task<MselEntity> SeedFor(string write, Guid? createdBy = null, Action<MselEntity> adjust = null)
    {
        if (write == "capture")
        {
            var chain = ArrangeChain();
            return await SeedMsel(directoryId: chain.Target, createdBy: createdBy, adjust: adjust);
        }
        if (write == "import")
            ArrangeImport();
        return await SeedMsel(withCopy: true, createdBy: createdBy, adjust: adjust);
    }

    private async Task<HttpResponseMessage> Send(HttpClient client, string write, MselEntity msel) => write switch
    {
        "capture" => await client.PostAsync(Route(msel), null, Ct),
        "import" => await client.PostAsync($"{Route(msel)}/import", null, Ct),
        _ => await client.DeleteAsync(Route(msel), Ct)
    };

    private async Task<MselEntity> SeedMsel(bool withCopy = false, Guid? directoryId = null, Guid? createdBy = null, bool isTemplate = false, Action<MselEntity> adjust = null)
    {
        var msel = TestData.Msel(isTemplate: isTemplate);
        msel.UseCaster = true;
        msel.CasterDirectoryId = directoryId;
        if (createdBy != null)
            msel.CreatedBy = (Guid)createdBy;
        adjust?.Invoke(msel);
        await Seed(msel);
        if (withCopy)
            await Seed(TestData.MselCasterExport(msel.Id));

        return msel;
    }

    private Task<bool> HasCopy(Guid mselId) => ReadBack(db => db.MselCasterExports.AnyAsync(e => e.MselId == mselId, Ct));

    private async Task<JsonElement> Download(MselEntity msel)
    {
        var response = await RootClient.GetAsync($"api/msels/{msel.Id}/json", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(Ct)).RootElement;
    }

    private static string FileOf(Action<MselEntity> arrange)
    {
        var msel = TestData.Msel();
        msel.Name = "Imported " + Guid.NewGuid();
        arrange(msel);
        return JsonSerializer.Serialize(msel, new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.Preserve });
    }

    private async Task<MselImportResult> ImportFile(string file)
    {
        var response = await ImportFileResponse(file);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<MselImportResult>(JsonOptions, Ct);
    }

    private async Task<HttpResponseMessage> ImportFileResponse(string file)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(file, Encoding.UTF8, "application/json"), "ToUpload", "msel.json");
        return await RootClient.PostAsync("api/msels/json/import", form, Ct);
    }
}
