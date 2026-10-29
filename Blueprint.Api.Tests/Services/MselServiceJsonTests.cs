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
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Cite.Api.Client;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

// Cite.Api.Client declares a SystemPermission of its own, so the name is ambiguous in any file that
// reaches for CITE as well as blueprint's own permissions - as the import tests do.
using SystemPermission = Blueprint.Api.Data.Enumerations.SystemPermission;

namespace Blueprint.Api.Tests.Services;

/// <summary><c>GET api/msels/{id}/json</c>, <c>POST api/msels/json</c> and <c>POST api/msels/json/import</c> - how a MSEL is
/// moved between installations, and the report of what an import changed.</summary>
public class MselServiceJsonTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // Download
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Download_ReturnsTheMselAsAFileNamedAfterIt()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/json", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"{msel.Name}.json", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));

        var exported = await ReadExport(response);
        Assert.Equal(msel.Id, exported.GetProperty("Id").GetGuid());
        Assert.Equal(msel.Name, exported.GetProperty("Name").GetString());
    }

    /// <summary>
    /// A MSEL whose name already ends in <c>.json</c> is not given a second suffix.
    /// </summary>
    [Fact]
    public async Task Download_OfAMselAlreadyNamedDotJson_DoesNotAppendASecondSuffix()
    {
        var msel = TestData.Msel();
        msel.Name = "already-named.json";
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/json", Ct);

        Assert.Equal("already-named.json", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
    }

    /// <summary>
    /// The export carries the graph, not just the MSEL row - that is what makes it a transfer format.
    /// </summary>
    [Fact]
    public async Task Download_CarriesTheWholeGraph()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var team = TestData.Team(msel.Id);
        var move = new MoveEntity { Id = Guid.NewGuid(), MselId = msel.Id, MoveNumber = 3, Description = "move three" };
        var organization = TestData.Organization(msel.Id);
        var dataField = new DataFieldEntity
        {
            Id = Guid.NewGuid(),
            MselId = msel.Id,
            Name = "Headline",
            DataType = DataFieldType.String,
            DisplayOrder = 1
        };
        await Seed(team, move, organization, dataField);
        var scenarioEvent = new ScenarioEventEntity { Id = Guid.NewGuid(), MselId = msel.Id, GroupOrder = 1 };
        await Seed(scenarioEvent);
        var dataValue = new DataValueEntity
        {
            Id = Guid.NewGuid(),
            ScenarioEventId = scenarioEvent.Id,
            DataFieldId = dataField.Id,
            Value = "the headline"
        };
        await Seed(dataValue);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var exported = await ReadExport(await Client(actor).GetAsync($"/api/msels/{msel.Id}/json", Ct));

        Assert.Equal(team.Id, Single(exported, "Teams").GetProperty("Id").GetGuid());
        Assert.Equal(3, Single(exported, "Moves").GetProperty("MoveNumber").GetInt32());
        Assert.Equal(organization.Id, Single(exported, "Organizations").GetProperty("Id").GetGuid());
        Assert.Equal("Headline", Single(exported, "DataFields").GetProperty("Name").GetString());

        var exportedEvent = Single(exported, "ScenarioEvents");
        Assert.Equal("the headline", Single(exportedEvent, "DataValues").GetProperty("Value").GetString());
    }

    [Fact]
    public async Task Download_ThenUpload_KeepsWhenTheSteamfitterScenarioStarts()
    {
        var msel = TestData.Msel();
        msel.UseSteamfitter = true;
        msel.SteamfitterScenarioStartMode = SteamfitterScenarioStartMode.AtStartTime;
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels, SystemPermission.CreateMsels).SeedAsync();
        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/json", Ct);
        var file = (await ReadExport(response)).GetRawText();

        var imported = await Upload(Client(actor), file);

        Assert.Equal((int)SteamfitterScenarioStartMode.AtStartTime,
            JsonDocument.Parse(file).RootElement.GetProperty("SteamfitterScenarioStartMode").GetInt32());
        Assert.Equal(SteamfitterScenarioStartMode.AtStartTime, imported.SteamfitterScenarioStartMode);
    }

    /// <summary>A file from before the setting existed imports as a scenario someone starts by hand.</summary>
    [Fact]
    public async Task Upload_OfAFileWithoutTheSteamfitterStartMode_StartsTheScenarioManually()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var file = System.Text.Json.Nodes.JsonNode.Parse(Exported(m => m.UseSteamfitter = true)).AsObject();
        file.Remove("SteamfitterScenarioStartMode");

        var imported = await Upload(Client(actor), file.ToJsonString());

        Assert.Equal(SteamfitterScenarioStartMode.Manual, imported.SteamfitterScenarioStartMode);
    }

    /// <summary>Text outside ASCII survives the export, as <c>\uXXXX</c> escapes in an ASCII file.</summary>
    [Fact]
    public async Task Download_KeepsTextOutsideAscii()
    {
        var msel = TestData.Msel();
        msel.Description = "Rüdiger’s brief — 20°C";
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/json", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);

        Assert.Equal(
            "Rüdiger’s brief — 20°C",
            JsonDocument.Parse(bytes).RootElement.GetProperty("Description").GetString());

        // The file is pure ASCII on the wire: every one of those characters was escaped, which is the
        // only reason Encoding.ASCII did not eat them.
        Assert.DoesNotContain(bytes, b => b > 127);
    }

    /// <summary>
    /// The export records the scoring model's name as CITE has it now, because that name is what an
    /// import on another installation looks the model up by - the id is local to this CITE.
    /// </summary>
    /// <remarks>
    /// A MSEL whose model was picked in the UI has no stored name, so without this the importing side
    /// would have nothing to remap with. A stored name is replaced too: the model may have been renamed in
    /// CITE since.
    /// </remarks>
    [Fact]
    public async Task Download_OfACiteMsel_RecordsTheScoringModelNameFromCite()
    {
        var scoringModelId = Guid.NewGuid();
        var msel = TestData.Msel();
        msel.UseCite = true;
        msel.CiteScoringModelId = scoringModelId;
        msel.CiteScoringModelName = "Old Name";
        await Seed(msel);
        Factory.Cite.GetScoringModelAsync(scoringModelId, Arg.Any<CancellationToken>())
            .Returns(new ScoringModel { Id = scoringModelId, Description = "Incident Triage" });
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var exported = await ReadExport(await Client(actor).GetAsync($"/api/msels/{msel.Id}/json", Ct));

        Assert.Equal("Incident Triage", exported.GetProperty("CiteScoringModelName").GetString());
        // The name is written to the file, not to the MSEL.
        var stored = await ReadBack(rb => rb.Msels.SingleAsync(m => m.Id == msel.Id, Ct));
        Assert.Equal("Old Name", stored.CiteScoringModelName);
    }

    /// <summary>
    /// CITE being down doesn't block an export: the file carries whatever name is already stored.
    /// </summary>
    [Fact]
    public async Task Download_WhenCiteCannotBeReached_KeepsTheStoredScoringModelName()
    {
        var scoringModelId = Guid.NewGuid();
        var msel = TestData.Msel();
        msel.UseCite = true;
        msel.CiteScoringModelId = scoringModelId;
        msel.CiteScoringModelName = "Incident Triage";
        await Seed(msel);
        Factory.Cite.GetScoringModelAsync(scoringModelId, Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Connection refused"));
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/json", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Incident Triage", (await ReadExport(response)).GetProperty("CiteScoringModelName").GetString());
    }

    /// <summary>
    /// Exporting a MSEL that doesn't use CITE asks CITE nothing, the same as importing one.
    /// </summary>
    [Fact]
    public async Task Download_OfANonCiteMsel_AsksCiteNothing()
    {
        var msel = TestData.Msel();
        msel.UseCite = false;
        msel.CiteScoringModelId = Guid.NewGuid();
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/json", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(Factory.Cite.ReceivedCalls());
    }

    /// <summary>A model CITE answers with a blank description leaves the stored name in the file.</summary>
    [Fact]
    public async Task Download_WhenCiteNamesTheModelWithNothing_KeepsTheStoredScoringModelName()
    {
        var scoringModelId = Guid.NewGuid();
        var msel = TestData.Msel();
        msel.UseCite = true;
        msel.CiteScoringModelId = scoringModelId;
        msel.CiteScoringModelName = "Incident Triage";
        await Seed(msel);
        Factory.Cite.GetScoringModelAsync(scoringModelId, Arg.Any<CancellationToken>())
            .Returns(new ScoringModel { Id = scoringModelId, Description = "  " });
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var exported = await ReadExport(await Client(actor).GetAsync($"/api/msels/{msel.Id}/json", Ct));

        Assert.Equal("Incident Triage", exported.GetProperty("CiteScoringModelName").GetString());
    }

    /// <summary>A CITE MSEL with no scoring model asks CITE nothing on export.</summary>
    [Fact]
    public async Task Download_OfACiteMselWithNoScoringModel_AsksCiteNothing()
    {
        var msel = TestData.Msel();
        msel.UseCite = true;
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/json", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(Factory.Cite.ReceivedCalls());
    }

    [Fact]
    public async Task Download_OfAnUnknownMsel_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{Guid.NewGuid()}/json", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Download of a MSEL the caller has no role on is answered with a 200.</summary>
    [Fact]
    public async Task Download_OfAMselTheCallerHasNoRoleOn_Is200()
    {
        var msel = TestData.Msel();
        msel.Description = "somebody else's unpublished exercise";
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        // Proof this caller has no role on it: they cannot even list it as one of theirs.
        var mine = await Client(actor).GetAsync("/api/my-msels", Ct);
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.DoesNotContain(msel.Id, (await Read<List<Msel>>(mine)).Select(x => x.Id));

        var exported = await ReadExport(await Client(actor).GetAsync($"/api/msels/{msel.Id}/json", Ct));

        Assert.Equal("somebody else's unpublished exercise", exported.GetProperty("Description").GetString());
    }

    /// <summary>The owner of a MSEL who lacks EditMsels is refused the export.</summary>
    [Fact]
    public async Task Download_AsTheOwnerWithoutEditMsels_Is403()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/json", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Download_is_forbidden_for_a_caller_holding_only_ViewMsels()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/json", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Upload
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Upload_CreatesTheMselFromTheFile()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var file = Exported(m =>
        {
            m.Name = "imported exercise";
            m.Description = "carried over from another installation";
            m.DurationSeconds = 7200;
        });

        var imported = await Upload(Client(actor), file);

        Assert.Equal("carried over from another installation", imported.Description);
        Assert.Equal(7200, imported.DurationSeconds);
        Assert.NotNull(await ReadBack(rb => rb.Msels.SingleOrDefaultAsync(m => m.Id == imported.Id, Ct)));
    }

    /// <summary>
    /// An import keeps the file's name when no MSEL here has it, gets a new id, and makes the importer its
    /// creator - which is what gives them any hold on the MSEL at all, since an import carries no roles.
    /// </summary>
    [Fact]
    public async Task Upload_KeepsTheFileNameAndGivesItANewId()
    {
        var actor = await Actor()
            .WithName("Importing User")
            .WithSystemPermissions(SystemPermission.CreateMsels)
            .SeedAsync();
        var originalId = Guid.NewGuid();
        var name = $"imported exercise {Guid.NewGuid()}";
        var file = Exported(m => { m.Id = originalId; m.Name = name; });

        var imported = await Upload(Client(actor), file);

        Assert.Equal(name, imported.Name);
        Assert.NotEqual(originalId, imported.Id);
        Assert.Equal(actor.Id, imported.CreatedBy);
    }

    /// <summary>
    /// Only a name already in use here gets the importer's name added, so the two can be told apart
    /// in the MSEL list. The check ignores case, the way a planner reading the list would.
    /// </summary>
    /// <remarks>
    /// The name matters beyond Blueprint: every pushed app inherits it, so players would otherwise see
    /// "Exercise X - Admin User" as the CITE evaluation and Gallery exhibit. A copy
    /// (<c>POST msels/{id}/copy</c>) always collides with its source and keeps the suffix; see
    /// <see cref="MselServiceCopyTests"/>.
    /// </remarks>
    [Fact]
    public async Task Upload_OfANameAlreadyInUse_AddsTheImportersName()
    {
        var existing = TestData.Msel();
        existing.Name = $"Exercise {Guid.NewGuid()}";
        await Seed(existing);
        var actor = await Actor()
            .WithName("Importing User")
            .WithSystemPermissions(SystemPermission.CreateMsels)
            .SeedAsync();
        var file = Exported(m => m.Name = existing.Name.ToUpperInvariant());

        var imported = await Upload(Client(actor), file);

        Assert.Equal($"{existing.Name.ToUpperInvariant()} - Importing User", imported.Name);
    }

    /// <summary>
    /// An imported MSEL arrives disconnected from whatever it was deployed to at the other installation:
    /// the ids of the Player view, Gallery collection and exhibit, CITE evaluation and Steamfitter
    /// scenario are all cleared, and the status is reset. The integration <em>settings</em> survive, so
    /// the MSEL can be pushed again here.
    /// </summary>
    [Fact]
    public async Task Upload_ClearsTheIntegrationIdsAndResetsTheStatus()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var file = Exported(m =>
        {
            m.Status = MselItemStatus.Deployed;
            m.IsTemplate = true;
            m.UsePlayer = true;
            m.PlayerViewId = Guid.NewGuid();
            m.UseGallery = true;
            m.GalleryCollectionId = Guid.NewGuid();
            m.GalleryExhibitId = Guid.NewGuid();
            m.CiteEvaluationId = Guid.NewGuid();
            m.UseSteamfitter = true;
            m.SteamfitterScenarioId = Guid.NewGuid();
            m.UseCaster = true;
            m.CasterWorkspaceId = Guid.NewGuid();
            m.CasterRunId = Guid.NewGuid();
        });

        var imported = await Upload(Client(actor), file);

        Assert.Null(imported.PlayerViewId);
        Assert.Null(imported.GalleryCollectionId);
        Assert.Null(imported.GalleryExhibitId);
        Assert.Null(imported.CiteEvaluationId);
        Assert.Null(imported.SteamfitterScenarioId);
        Assert.Equal(MselItemStatus.Pending, imported.Status);
        Assert.False(imported.IsTemplate);
        // The settings themselves are what the MSEL is for, and they come across.
        Assert.True(imported.UsePlayer);
        Assert.True(imported.UseGallery);
        Assert.True(imported.UseSteamfitter);
        Assert.True(imported.UseCaster);

        var stored = await ReadBack(rb => rb.Msels.SingleAsync(x => x.Id == imported.Id, Ct));
        Assert.Null(stored.CasterWorkspaceId);
        Assert.Null(stored.CasterRunId);
    }

    /// <summary>
    /// Team membership is deliberately not imported: the users named in the file are users of the
    /// installation it came from. The teams themselves come across empty, ready to be filled here.
    /// </summary>
    [Fact]
    public async Task Upload_CarriesTheTeamsButNotTheirMembership()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var strangerId = Guid.NewGuid();
        var file = Exported(m =>
        {
            var team = new TeamEntity { Id = Guid.NewGuid(), Name = "Blue", ShortName = "B", MselId = m.Id };
            team.TeamUsers.Add(new TeamUserEntity { Id = Guid.NewGuid(), TeamId = team.Id, UserId = strangerId });
            team.UserTeamRoles.Add(new UserTeamRoleEntity
            {
                Id = Guid.NewGuid(),
                TeamId = team.Id,
                UserId = strangerId,
                Role = "Submitter"
            });
            m.Teams.Add(team);
        });

        var imported = await Upload(Client(actor), file);

        var team = await ReadBack(rb => rb.Teams
            .Include(t => t.TeamUsers)
            .Include(t => t.UserTeamRoles)
            .AsSplitQuery()
            .SingleAsync(t => t.MselId == imported.Id, Ct));

        Assert.Equal("Blue", team.Name);
        Assert.Empty(team.TeamUsers);
        Assert.Empty(team.UserTeamRoles);
        Assert.Null(await ReadBack(rb => rb.Users.SingleOrDefaultAsync(u => u.Id == strangerId, Ct)));
    }

    /// <summary>
    /// The graph is renumbered on the way in, exactly as a copy is, so an import can never collide with
    /// something already here that shares its ids.
    /// </summary>
    [Fact]
    public async Task Upload_RenumbersTheGraph()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var (file, dataFieldId) = ExportedWithOneCell(DataFieldType.String, "the headline");

        var imported = await Upload(Client(actor), file);

        var dataField = await ReadBack(rb => rb.DataFields.SingleAsync(df => df.MselId == imported.Id, Ct));
        var dataValue = await ReadBack(rb => rb.DataValues
            .SingleAsync(dv => dv.ScenarioEvent.MselId == imported.Id, Ct));

        Assert.NotEqual(dataFieldId, dataField.Id);
        Assert.Equal("the headline", dataValue.Value);
        // The renumbered data value still points at the renumbered data field.
        Assert.Equal(dataField.Id, dataValue.DataFieldId);
    }

    /// <summary>An IP Address or Network field comes across with its type and its value.</summary>
    [Theory]
    [InlineData(DataFieldType.IpAddress, "10.1.0.5")]
    [InlineData(DataFieldType.Network, "10.1.0.0/24")]
    public async Task Upload_KeepsTheTypeAndValueOfAnAddressField(DataFieldType dataType, string value)
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var (file, _) = ExportedWithOneCell(dataType, value);

        var imported = await Upload(Client(actor), file);

        var dataField = await ReadBack(rb => rb.DataFields.SingleAsync(df => df.MselId == imported.Id, Ct));
        var dataValue = await ReadBack(rb => rb.DataValues.SingleAsync(dv => dv.ScenarioEvent.MselId == imported.Id, Ct));
        Assert.Equal(dataType, dataField.DataType);
        Assert.Equal(value, dataValue.Value);
    }

    /// <summary>An import is saved with event publishing off, so nothing goes out over SignalR.</summary>
    [Fact]
    public async Task Upload_BroadcastsNothing()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        await Upload(Client(actor), Exported());

        Assert.Empty(Hub.Sent());
    }

    [Fact]
    public async Task Upload_WithNoFile_Is400()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        using var content = new MultipartFormDataContent();

        var response = await Client(actor).PostAsync("/api/msels/json", content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upload_WithMalformedJson_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Post(Client(actor), "not json at all", UploadRoute);

        Assert.Equal("'not json at all' is an invalid JSON literal. Expected the literal 'null'. Path: $ | LineNumber: 0 | BytePositionInLine: 1.", (await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
    }

    /// <summary>Upload with an explicitly null teams collection is answered with a 500.</summary>
    [Fact]
    public async Task Upload_WithAnExplicitlyNullTeamsCollection_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Post(Client(actor), """{"Name":"no teams at all","Teams":null}""", UploadRoute);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Value cannot be null. (Parameter 'source')", failure.Title);
        Assert.Contains("MselService.ImportJsonAsync", failure.Detail);
    }

    [Fact]
    public async Task Upload_WithCreateMselsPermission_Is200()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Post(Client(actor), Exported(), UploadRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Importing creates a MSEL, so it asks for <see cref="SystemPermission.CreateMsels"/> - and
    /// <see cref="SystemPermission.EditMsels"/>, which is what exporting asks for, is not enough. The two
    /// halves of the transfer are therefore gated on different permissions, which is defensible for the
    /// import and not for the export.
    /// </summary>
    [Fact]
    public async Task Upload_WithEditMselsOnly_Is403()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(Client(actor), Exported(), UploadRoute);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // The round trip
    // ---------------------------------------------------------------------------------------------

    /// <summary>A file this API exports, this API imports.</summary>
    [Fact]
    public async Task Download_ThenUpload_BringsTheMselBackIn()
    {
        var msel = TestData.Msel();
        msel.Description = "the exercise as exported";
        await Seed(msel);
        var team = TestData.Team(msel.Id);
        await Seed(team);
        var actor = await Actor()
            .WithName("Round Tripper")
            .WithSystemPermissions(SystemPermission.EditMsels, SystemPermission.CreateMsels)
            .SeedAsync();

        var exported = await Client(actor).GetAsync($"/api/msels/{msel.Id}/json", Ct);
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        var file = await exported.Content.ReadAsStringAsync(Ct);

        var reimported = await Upload(Client(actor), file);

        Assert.NotEqual(msel.Id, reimported.Id);
        // The source is still here, so the name is in use.
        Assert.Equal($"{msel.Name} - Round Tripper", reimported.Name);
        Assert.Equal("the exercise as exported", reimported.Description);

        var reimportedTeam = await ReadBack(rb => rb.Teams.SingleAsync(t => t.MselId == reimported.Id, Ct));
        Assert.Equal(team.Name, reimportedTeam.Name);
        Assert.NotEqual(team.Id, reimportedTeam.Id);
    }

    // ---------------------------------------------------------------------------------------------
    // Remapping the CITE ids, which is what makes an import portable
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A scoring model id is local to one CITE, so an import looks the model up again by the name the
    /// export recorded and repoints the MSEL at the local one.
    /// </summary>
    [Fact]
    public async Task Upload_OfACiteMselWithAStaleScoringModelId_RemapsItByName()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var localId = Guid.NewGuid();
        CiteKnows(scoringModels: [new ScoringModel { Id = localId, Description = "Incident Triage" }]);
        var file = Exported(m =>
        {
            m.UseCite = true;
            m.CiteScoringModelId = Guid.NewGuid();
            m.CiteScoringModelName = "Incident Triage";
        });

        var imported = await Upload(Client(actor), file);

        Assert.Equal(localId, imported.CiteScoringModelId);
    }

    /// <summary>
    /// A file with a name but no id - which is what an export from an installation that never deployed
    /// the MSEL looks like - is resolved by name too.
    /// </summary>
    [Fact]
    public async Task Upload_OfACiteMselWithOnlyAScoringModelName_ResolvesTheId()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var localId = Guid.NewGuid();
        // The lookup is case-insensitive, which is what lets a hand-edited export still resolve.
        CiteKnows(scoringModels: [new ScoringModel { Id = localId, Description = "Incident Triage" }]);
        var file = Exported(m =>
        {
            m.UseCite = true;
            m.CiteScoringModelId = null;
            m.CiteScoringModelName = "incident triage";
        });

        var imported = await Upload(Client(actor), file);

        Assert.Equal(localId, imported.CiteScoringModelId);
    }

    /// <summary>A scoring model name this CITE does not know clears the id and keeps the name on the stored
    /// row.</summary>
    [Fact]
    public async Task Upload_OfACiteMselWithAnUnknownScoringModelName_ClearsTheIdAndKeepsTheName()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        CiteKnows(scoringModels: [new ScoringModel { Id = Guid.NewGuid(), Description = "Something Else" }]);
        var file = Exported(m =>
        {
            m.UseCite = true;
            m.CiteScoringModelId = Guid.NewGuid();
            m.CiteScoringModelName = "Incident Triage";
        });

        var imported = await Upload(Client(actor), file);

        Assert.Null(imported.CiteScoringModelId);

        var stored = await ReadBack(rb => rb.Msels.SingleAsync(m => m.Id == imported.Id, Ct));
        Assert.Equal("Incident Triage", stored.CiteScoringModelName);
    }

    /// <summary>
    /// An id CITE still knows is left alone, and the name is filled in from CITE so a MSEL exported
    /// before the name column existed gains one.
    /// </summary>
    [Fact]
    public async Task Upload_OfACiteMselWithAValidScoringModelId_KeepsItAndFillsInTheName()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var localId = Guid.NewGuid();
        CiteKnows(scoringModels: [new ScoringModel { Id = localId, Description = "Incident Triage" }]);
        var file = Exported(m =>
        {
            m.UseCite = true;
            m.CiteScoringModelId = localId;
            m.CiteScoringModelName = null;
        });

        var imported = await Upload(Client(actor), file);

        Assert.Equal(localId, imported.CiteScoringModelId);

        var stored = await ReadBack(rb => rb.Msels.SingleAsync(m => m.Id == imported.Id, Ct));
        Assert.Equal("Incident Triage", stored.CiteScoringModelName);
    }

    /// <summary>
    /// A team without a type is given the installation's <c>Standard</c> type, so an import is usable
    /// in CITE without visiting every team.
    /// </summary>
    [Fact]
    public async Task Upload_OfACiteMselWithATypelessTeam_DefaultsItToStandard()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var standardId = Guid.NewGuid();
        CiteKnows(teamTypes:
        [
            new TeamType { Id = Guid.NewGuid(), Name = "Observer" },
            new TeamType { Id = standardId, Name = "Standard" }
        ]);
        var file = Exported(m =>
        {
            m.UseCite = true;
            m.Teams.Add(new TeamEntity { Id = Guid.NewGuid(), Name = "Blue", MselId = m.Id, CiteTeamTypeId = null });
        });

        var imported = await Upload(Client(actor), file);

        var team = await ReadBack(rb => rb.Teams.SingleAsync(t => t.MselId == imported.Id, Ct));
        Assert.Equal(standardId, team.CiteTeamTypeId);
        Assert.Equal("Standard", team.CiteTeamTypeName);
    }

    /// <summary>
    /// <c>Standard</c> is only a preference. An installation that renamed its types still gets a usable
    /// import, from whichever type CITE happens to list first.
    /// </summary>
    [Fact]
    public async Task Upload_WhenCiteHasNoStandardTeamType_UsesTheFirstOneItHas()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var firstId = Guid.NewGuid();
        CiteKnows(teamTypes:
        [
            new TeamType { Id = firstId, Name = "Participant" },
            new TeamType { Id = Guid.NewGuid(), Name = "Observer" }
        ]);
        var file = Exported(m =>
        {
            m.UseCite = true;
            m.Teams.Add(new TeamEntity { Id = Guid.NewGuid(), Name = "Blue", MselId = m.Id, CiteTeamTypeId = null });
        });

        var imported = await Upload(Client(actor), file);

        var team = await ReadBack(rb => rb.Teams.SingleAsync(t => t.MselId == imported.Id, Ct));
        Assert.Equal(firstId, team.CiteTeamTypeId);
    }

    /// <summary>
    /// A stale team type id is remapped by name, the same way a scoring model is.
    /// </summary>
    [Fact]
    public async Task Upload_OfACiteMselWithAStaleTeamTypeId_RemapsItByName()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var localId = Guid.NewGuid();
        CiteKnows(teamTypes:
        [
            new TeamType { Id = localId, Name = "Observer" },
            new TeamType { Id = Guid.NewGuid(), Name = "Standard" }
        ]);
        var file = Exported(m =>
        {
            m.UseCite = true;
            m.Teams.Add(new TeamEntity
            {
                Id = Guid.NewGuid(),
                Name = "White",
                MselId = m.Id,
                CiteTeamTypeId = Guid.NewGuid(),
                CiteTeamTypeName = "Observer"
            });
        });

        var imported = await Upload(Client(actor), file);

        var team = await ReadBack(rb => rb.Teams.SingleAsync(t => t.MselId == imported.Id, Ct));
        Assert.Equal(localId, team.CiteTeamTypeId);
    }

    /// <summary>A stale team type id whose name this CITE does not know falls back to the default type.</summary>
    [Fact]
    public async Task Upload_OfACiteMselWithAnUnknownTeamTypeName_FallsBackToTheDefault()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var standardId = Guid.NewGuid();
        CiteKnows(teamTypes: [new TeamType { Id = standardId, Name = "Standard" }]);
        var file = Exported(m =>
        {
            m.UseCite = true;
            m.Teams.Add(new TeamEntity
            {
                Id = Guid.NewGuid(),
                Name = "White",
                MselId = m.Id,
                CiteTeamTypeId = Guid.NewGuid(),
                CiteTeamTypeName = "A Type This Cite Never Had"
            });
        });

        var imported = await Upload(Client(actor), file);

        var team = await ReadBack(rb => rb.Teams.SingleAsync(t => t.MselId == imported.Id, Ct));
        Assert.Equal(standardId, team.CiteTeamTypeId);
        Assert.Equal("Standard", team.CiteTeamTypeName);
    }

    /// <summary>
    /// The remapping only runs for a MSEL that uses CITE, so importing anything else costs no call at
    /// all - which is what makes an import work while CITE is down.
    /// </summary>
    [Fact]
    public async Task Upload_OfANonCiteMsel_AsksCiteNothing()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var file = Exported(m =>
        {
            m.UseCite = false;
            m.CiteScoringModelId = Guid.NewGuid();
            m.Teams.Add(new TeamEntity { Id = Guid.NewGuid(), Name = "Blue", MselId = m.Id });
        });

        var imported = await Upload(Client(actor), file);

        Assert.Empty(Factory.Cite.ReceivedCalls());
        // And the stale id it carried is cleared only because the copy clears it, not by any remapping.
        Assert.NotNull(await ReadBack(rb => rb.Msels.SingleOrDefaultAsync(m => m.Id == imported.Id, Ct)));
    }

    /// <summary>Upload when CITE cannot be reached clears the scoring model but keeps a stale team type.</summary>
    [Fact]
    public async Task Upload_WhenCiteCannotBeReached_ClearsTheScoringModelButKeepsAStaleTeamType()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        Factory.Cite
            .GetScoringModelsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Connection refused"));
        Factory.Cite.GetTeamTypesAsync(Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Connection refused"));

        var staleTeamTypeId = Guid.NewGuid();
        var file = Exported(m =>
        {
            m.UseCite = true;
            m.CiteScoringModelId = Guid.NewGuid();
            m.CiteScoringModelName = "Incident Triage";
            m.Teams.Add(new TeamEntity
            {
                Id = Guid.NewGuid(),
                Name = "Blue",
                MselId = m.Id,
                CiteTeamTypeId = staleTeamTypeId
            });
        });

        var imported = await Upload(Client(actor), file);

        Assert.Null(imported.CiteScoringModelId);

        var team = await ReadBack(rb => rb.Teams.SingleAsync(t => t.MselId == imported.Id, Ct));
        Assert.Equal(staleTeamTypeId, team.CiteTeamTypeId);
    }

    /// <summary>When CITE names two scoring models alike, the first one wins.</summary>
    [Fact]
    public async Task Upload_WhenCiteHasTwoScoringModelsWithOneName_TakesTheFirst()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var firstId = Guid.NewGuid();
        CiteKnows(scoringModels:
        [
            new ScoringModel { Id = firstId, Description = "Incident Triage" },
            new ScoringModel { Id = Guid.NewGuid(), Description = "Incident Triage" }
        ]);
        var file = Exported(m =>
        {
            m.UseCite = true;
            m.CiteScoringModelId = null;
            m.CiteScoringModelName = "Incident Triage";
        });

        var imported = await Upload(Client(actor), file);

        Assert.Equal(firstId, imported.CiteScoringModelId);
    }

    /// <summary>A Caster directory the importer can view in Caster comes across.</summary>
    [Fact]
    public async Task Upload_OfACasterMselWithADirectoryTheImporterCanView_KeepsIt()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var directory = Guid.NewGuid();
        Factory.Caster.ShowsDirectory(directory);
        var file = Exported(m =>
        {
            m.UseCaster = true;
            m.CasterDirectoryId = directory;
        });

        var imported = await Upload(Client(actor), file);

        Assert.Equal(directory, imported.CasterDirectoryId);
        Assert.Equal(
            directory,
            (await ReadBack(rb => rb.Msels.SingleAsync(x => x.Id == imported.Id, Ct))).CasterDirectoryId);
    }

    /// <summary>A directory from another installation, or one Caster will not show the importer, is dropped
    /// and the import goes on.</summary>
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Upload_OfACasterMselWithADirectoryCasterWillNotShow_DropsTheDirectory(HttpStatusCode casterAnswer)
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var directory = Guid.NewGuid();
        Factory.Caster.RefusesDirectory(directory, casterAnswer);
        var file = Exported(m =>
        {
            m.UseCaster = true;
            m.CasterDirectoryId = directory;
        });

        var imported = await Upload(Client(actor), file);

        var stored = await ReadBack(rb => rb.Msels.SingleAsync(x => x.Id == imported.Id, Ct));
        Assert.Null(stored.CasterDirectoryId);
        Assert.True(stored.UseCaster);
    }

    [Fact]
    public async Task Upload_WhenCasterCannotBeReached_DropsTheDirectory()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var directory = Guid.NewGuid();
        Factory.Caster.GetDirectoryAsync(directory, false, false, Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Connection refused"));
        var file = Exported(m => m.CasterDirectoryId = directory);

        var imported = await Upload(Client(actor), file);

        Assert.Null(imported.CasterDirectoryId);
    }

    [Fact]
    public async Task Upload_OfAMselWithNoCasterDirectory_AsksCasterNothing()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        await Upload(Client(actor), Exported());

        Assert.Empty(Factory.Caster.ReceivedCalls());
    }

    // ---------------------------------------------------------------------------------------------
    // The import report (POST msels/json/import)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The report counts what was saved, not what the file held, so a planner can check the import
    /// against the spreadsheet they started from.
    /// </summary>
    [Fact]
    public async Task Import_CountsWhatWasImported()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var file = Exported(m =>
        {
            m.Moves.Add(TestData.Move(m.Id, moveNumber: 0));
            m.Moves.Add(TestData.Move(m.Id, moveNumber: 1));
            m.Cards.Add(TestData.Card(m.Id));
            m.Teams.Add(TestData.Team(m.Id));
            m.DataFields.Add(TestData.DataField(mselId: m.Id));
            m.ScenarioEvents.Add(TestData.ScenarioEvent(m.Id));
            m.Pages.Add(TestData.MselPage(m.Id, name: "Briefing"));
        });

        var result = await Import(Client(actor), file);

        Assert.Equal(2, result.Imported.Moves);
        Assert.Equal(1, result.Imported.Cards);
        Assert.Equal(1, result.Imported.Teams);
        Assert.Equal(1, result.Imported.DataFields);
        Assert.Equal(1, result.Imported.ScenarioEvents);
        Assert.Equal(1, result.Imported.Pages);
        Assert.NotNull(await ReadBack(rb => rb.Msels.SingleOrDefaultAsync(m => m.Id == result.Msel.Id, Ct)));
    }

    [Fact]
    public async Task Import_OfAPlainMsel_ReportsNoChangesOrFailures()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var result = await Import(Client(actor), Exported(m => m.Name = $"plain {Guid.NewGuid()}"));

        Assert.Empty(result.Changes);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task Import_OfANameAlreadyInUse_ReportsTheRename()
    {
        var existing = TestData.Msel();
        existing.Name = $"Exercise {Guid.NewGuid()}";
        await Seed(existing);
        var actor = await Actor()
            .WithName("Importing User")
            .WithSystemPermissions(SystemPermission.CreateMsels)
            .SeedAsync();

        var result = await Import(Client(actor), Exported(m => m.Name = existing.Name));

        var change = Assert.Single(result.Changes, c => c.Field == "name");
        Assert.Contains($"{existing.Name} - Importing User", change.Message);
        Assert.Equal($"{existing.Name} - Importing User", result.Msel.Name);
    }

    /// <summary>
    /// Team members are never imported (see <see cref="Upload_CarriesTheTeamsButNotTheirMembership"/>);
    /// the report says how many were left behind so the planner knows to add them before pushing.
    /// </summary>
    [Fact]
    public async Task Import_ReportsTheTeamMembersItLeftBehind()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var file = Exported(m =>
        {
            var team = TestData.Team(m.Id);
            team.TeamUsers.Add(TestData.TeamUser(Guid.NewGuid(), team.Id));
            team.TeamUsers.Add(TestData.TeamUser(Guid.NewGuid(), team.Id));
            m.Teams.Add(team);
        });

        var result = await Import(Client(actor), file);

        var change = Assert.Single(result.Changes, c => c.Field == "teams");
        Assert.StartsWith("2 team members", change.Message);
    }

    /// <summary>
    /// The integration settings come across on, so a push would create content in each of those apps.
    /// The report lists them so that is a decision rather than a surprise.
    /// </summary>
    [Fact]
    public async Task Import_ReportsTheIntegrationsThatAreOn()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var file = Exported(m =>
        {
            m.UsePlayer = true;
            m.UseSteamfitter = true;
            m.UseGallery = false;
            m.UseCite = false;
        });

        var result = await Import(Client(actor), file);

        Assert.Equal(["usePlayer", "useSteamfitter"], result.Changes.Select(c => c.Field).Order());
    }

    /// <summary>
    /// A scenario that starts itself runs its tasks as soon as a facilitator clicks them, so the report says so.
    /// </summary>
    [Theory]
    [InlineData(SteamfitterScenarioStartMode.OnPush, "The Steamfitter scenario starts when the push finishes.")]
    [InlineData(SteamfitterScenarioStartMode.AtStartTime, "The Steamfitter scenario starts at the MSEL start time.")]
    public async Task Import_ReportsAScenarioThatStartsItself(SteamfitterScenarioStartMode mode, string message)
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var file = Exported(m =>
        {
            m.UseSteamfitter = true;
            m.SteamfitterScenarioStartMode = mode;
        });

        var result = await Import(Client(actor), file);

        Assert.Equal(message, Assert.Single(result.Changes, c => c.Field == "steamfitterScenarioStartMode").Message);
        Assert.Equal(mode, result.Msel.SteamfitterScenarioStartMode);
    }

    [Fact]
    public async Task Import_OfAManuallyStartedScenario_ReportsNothingAboutIt()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var result = await Import(Client(actor), Exported(m => m.UseSteamfitter = true));

        Assert.DoesNotContain(result.Changes, c => c.Field == "steamfitterScenarioStartMode");
        Assert.Equal(SteamfitterScenarioStartMode.Manual, result.Msel.SteamfitterScenarioStartMode);
    }

    [Fact]
    public async Task Import_OfAnUnknownScoringModel_ReportsThatItWasNotLinked()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        CiteKnows(scoringModels: [new ScoringModel { Id = Guid.NewGuid(), Description = "Something Else" }]);
        var file = Exported(m =>
        {
            m.UseCite = true;
            m.CiteScoringModelId = Guid.NewGuid();
            m.CiteScoringModelName = "Incident Triage";
        });

        var result = await Import(Client(actor), file);

        var change = Assert.Single(result.Changes, c => c.Field == "citeScoringModel");
        Assert.Contains("\"Incident Triage\" was not found in CITE", change.Message);
        Assert.Null(result.Msel.CiteScoringModelId);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task Import_OfAScoringModelFoundByName_ReportsTheLink()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var localId = Guid.NewGuid();
        CiteKnows(scoringModels: [new ScoringModel { Id = localId, Description = "Incident Triage" }]);
        var file = Exported(m =>
        {
            m.UseCite = true;
            m.CiteScoringModelId = Guid.NewGuid();
            m.CiteScoringModelName = "Incident Triage";
        });

        var result = await Import(Client(actor), file);

        var change = Assert.Single(result.Changes, c => c.Field == "citeScoringModel");
        Assert.Contains("\"Incident Triage\" by name", change.Message);
        Assert.Equal(localId, result.Msel.CiteScoringModelId);
    }

    [Fact]
    public async Task Import_OfATypelessTeam_ReportsTheDefaultTeamType()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        CiteKnows(teamTypes: [new TeamType { Id = Guid.NewGuid(), Name = "Standard" }]);
        var file = Exported(m =>
        {
            m.UseCite = true;
            var team = TestData.Team(m.Id);
            team.ShortName = "BLUE";
            m.Teams.Add(team);
        });

        var result = await Import(Client(actor), file);

        var change = Assert.Single(result.Changes, c => c.Field == "teams");
        Assert.Equal("Team BLUE had no CITE team type; set to \"Standard\".", change.Message);
    }

    /// <summary>
    /// CITE being down still imports the MSEL, but both CITE steps are reported as failed, without the
    /// raw exception text.
    /// </summary>
    [Fact]
    public async Task Import_WhenCiteCannotBeReached_ReportsBothCiteStepsAsFailed()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        Factory.Cite
            .GetScoringModelsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Connection refused"));
        Factory.Cite.GetTeamTypesAsync(Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Connection refused"));
        var file = Exported(m =>
        {
            m.UseCite = true;
            m.CiteScoringModelId = Guid.NewGuid();
            m.CiteScoringModelName = "Incident Triage";
            m.Teams.Add(TestData.Team(m.Id));
        });

        var result = await Import(Client(actor), file);

        Assert.Equal(["citeScoringModel", "citeTeamTypes"], result.Failures.Select(f => f.Step).Order());
        Assert.DoesNotContain(result.Failures, f => f.Message.Contains("Connection refused"));
        Assert.NotNull(await ReadBack(rb => rb.Msels.SingleOrDefaultAsync(m => m.Id == result.Msel.Id, Ct)));
    }

    [Fact]
    public async Task Import_WithEditMselsOnly_Is403()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(Client(actor), Exported(), ImportRoute);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>A file naming its scoring model only by name is linked to the model CITE has under it.</summary>
    [Fact]
    public async Task Import_OfAScoringModelNamedOnly_ReportsTheLinkByName()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        CiteKnows(scoringModels: [new ScoringModel { Id = Guid.NewGuid(), Description = "Incident Triage" }]);

        var result = await Import(Client(actor), Exported(CiteScoringModel(null, "Incident Triage")));

        Assert.Equal(
            "Linked to the CITE scoring model \"Incident Triage\" by name.",
            Assert.Single(result.Changes, c => c.Field == "citeScoringModel").Message);
    }

    [Fact]
    public async Task Import_OfAScoringModelNamedOnlyThatCiteDoesNotHave_ReportsItWasNotFound()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        CiteKnows(scoringModels: [new ScoringModel { Id = Guid.NewGuid(), Description = "Something Else" }]);

        var result = await Import(Client(actor), Exported(CiteScoringModel(null, "Incident Triage")));

        Assert.Equal(
            "Scoring model \"Incident Triage\" was not found in CITE. Choose one before pushing.",
            Assert.Single(result.Changes, c => c.Field == "citeScoringModel").Message);
        Assert.Null(result.Msel.CiteScoringModelId);
    }

    /// <summary>A stale id relinked by name gets its own message, saying the file's id was not in CITE, which
    /// <see cref="Import_OfAScoringModelFoundByName_ReportsTheLink"/> only matches in part.</summary>
    [Fact]
    public async Task Import_OfAStaleScoringModelIdFoundByName_ReportsTheRelink()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        CiteKnows(scoringModels: [new ScoringModel { Id = Guid.NewGuid(), Description = "Incident Triage" }]);

        var result = await Import(Client(actor), Exported(CiteScoringModel(Guid.NewGuid(), "Incident Triage")));

        Assert.Equal(
            "The file's scoring model id is not in CITE; linked to \"Incident Triage\" by name.",
            Assert.Single(result.Changes, c => c.Field == "citeScoringModel").Message);
    }

    [Fact]
    public async Task Import_OfAStaleScoringModelIdWithNoName_ReportsItWasNotFound()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        CiteKnows(scoringModels: [new ScoringModel { Id = Guid.NewGuid(), Description = "Incident Triage" }]);

        var result = await Import(Client(actor), Exported(CiteScoringModel(Guid.NewGuid(), null)));

        Assert.Equal(
            "The file's scoring model was not found in CITE. Choose one before pushing.",
            Assert.Single(result.Changes, c => c.Field == "citeScoringModel").Message);
        Assert.Null(result.Msel.CiteScoringModelId);
    }

    [Fact]
    public async Task Import_OfAScoringModelIdCiteHas_ReportsNoScoringModelChange()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var localId = Guid.NewGuid();
        CiteKnows(scoringModels: [new ScoringModel { Id = localId, Description = "Incident Triage" }]);

        var result = await Import(Client(actor), Exported(CiteScoringModel(localId, "Incident Triage")));

        Assert.DoesNotContain(result.Changes, c => c.Field == "citeScoringModel");
        Assert.Equal(localId, result.Msel.CiteScoringModelId);
    }

    /// <summary>A team type id this CITE does not have falls back to the default, and the report names the
    /// type the file gave.</summary>
    [Fact]
    public async Task Import_OfATeamTypeCiteDoesNotHave_ReportsTheDefaultItWasGiven()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        CiteKnows(teamTypes: [new TeamType { Id = Guid.NewGuid(), Name = "Standard" }]);
        var file = Exported(m =>
        {
            m.UseCite = true;
            m.Teams.Add(CiteTeam(m.Id, "BLUE", Guid.NewGuid(), "Gold Cell"));
        });

        var result = await Import(Client(actor), file);

        Assert.Equal(
            "Team BLUE's CITE team type \"Gold Cell\" was not found; set to \"Standard\".",
            Assert.Single(result.Changes, c => c.Field == "teams").Message);
    }

    [Fact]
    public async Task Import_OfAnUnnamedTeamTypeCiteDoesNotHave_ReportsTheDefaultItWasGiven()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        CiteKnows(teamTypes: [new TeamType { Id = Guid.NewGuid(), Name = "Standard" }]);
        var file = Exported(m =>
        {
            m.UseCite = true;
            m.Teams.Add(CiteTeam(m.Id, "BLUE", Guid.NewGuid(), null));
        });

        var result = await Import(Client(actor), file);

        Assert.Equal(
            "Team BLUE's CITE team type was not found; set to \"Standard\".",
            Assert.Single(result.Changes, c => c.Field == "teams").Message);
    }

    [Fact]
    public async Task Import_OfATeamTypeWhenCiteHasNoTeamTypes_ReportsThatNoneCouldBeUsed()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        CiteKnows(teamTypes: []);
        var file = Exported(m =>
        {
            m.UseCite = true;
            m.Teams.Add(CiteTeam(m.Id, "BLUE", Guid.NewGuid(), "Gold Cell"));
        });

        var result = await Import(Client(actor), file);

        Assert.Equal(
            "Team BLUE's CITE team type was not found, and CITE has no team types to use instead.",
            Assert.Single(result.Changes, c => c.Field == "teams").Message);
    }

    /// <summary>A team with no short name is named in the report by its name.</summary>
    [Fact]
    public async Task Import_OfATypelessTeamWithNoShortName_NamesTheTeamByItsName()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        CiteKnows(teamTypes: [new TeamType { Id = Guid.NewGuid(), Name = "Standard" }]);
        var file = Exported(m =>
        {
            m.UseCite = true;
            m.Teams.Add(CiteTeam(m.Id, "", null, null, name: "Blue Team"));
        });

        var result = await Import(Client(actor), file);

        Assert.Equal(
            "Team Blue Team had no CITE team type; set to \"Standard\".",
            Assert.Single(result.Changes, c => c.Field == "teams").Message);
    }

    /// <summary>A team type found by the file's name is relinked without a line in the report.</summary>
    [Fact]
    public async Task Import_OfATeamTypeFoundByName_ReportsNothing()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var localId = Guid.NewGuid();
        CiteKnows(teamTypes: [new TeamType { Id = localId, Name = "Gold Cell" }]);
        var file = Exported(m =>
        {
            m.UseCite = true;
            m.Teams.Add(CiteTeam(m.Id, "BLUE", Guid.NewGuid(), "Gold Cell"));
        });

        var result = await Import(Client(actor), file);

        Assert.DoesNotContain(result.Changes, c => c.Field == "teams");
        Assert.Equal(localId, (await ReadBack(rb => rb.Teams.SingleAsync(t => t.MselId == result.Msel.Id, Ct))).CiteTeamTypeId);
    }

    [Fact]
    public async Task Import_WhenCiteCannotBeReached_ExplainsEachFailure()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        Factory.Cite
            .GetScoringModelsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Connection refused"));
        Factory.Cite.GetTeamTypesAsync(Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Connection refused"));
        var file = Exported(m =>
        {
            CiteScoringModel(Guid.NewGuid(), "Incident Triage")(m);
            m.Teams.Add(TestData.Team(m.Id));
        });

        var result = await Import(Client(actor), file);

        Assert.Equal(
            "Could not check the scoring model in CITE, so it was not linked. Choose one before pushing.",
            Assert.Single(result.Failures, f => f.Step == "citeScoringModel").Message);
        Assert.Equal(
            "Could not check the teams' CITE team types, so they were imported unchecked.",
            Assert.Single(result.Failures, f => f.Step == "citeTeamTypes").Message);
    }

    [Fact]
    public async Task Import_ReportsGalleryAndCiteWhenTheyAreOn()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        CiteKnows();
        var file = Exported(m =>
        {
            m.UsePlayer = false;
            m.UseGallery = true;
            m.UseCite = true;
            m.UseSteamfitter = false;
        });

        var result = await Import(Client(actor), file);

        Assert.Equal(
            [("useCite", "CITE integration is on."), ("useGallery", "Gallery integration is on.")],
            result.Changes.Select(c => (c.Field, c.Message)).Order());
    }

    /// <summary>A Caster MSEL is imported with Caster on, and the report does not list Caster.</summary>
    [Fact]
    public async Task Import_OfACasterMsel_DoesNotReportThatCasterIsOn()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var file = Exported(m => m.UseCaster = true);

        var result = await Import(Client(actor), file);

        Assert.True(result.Msel.UseCaster);
        Assert.Empty(result.Changes);
    }

    /// <summary>A Caster directory the importer cannot view is dropped, and the report says nothing of it.</summary>
    [Fact]
    public async Task Import_OfACasterDirectoryCasterWillNotShow_DropsItWithoutReportingIt()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var directory = Guid.NewGuid();
        Factory.Caster.RefusesDirectory(directory);
        var file = Exported(m => m.CasterDirectoryId = directory);

        var result = await Import(Client(actor), file);

        Assert.Null(result.Msel.CasterDirectoryId);
        Assert.Empty(result.Changes);
        Assert.Empty(result.Failures);
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Tells the substituted CITE client what this installation's CITE contains. Both lists default to
    /// empty rather than being left unstubbed, because an unstubbed substitute answers null and the
    /// import's own <c>catch</c> would then hide which call the test meant to arrange.
    /// </summary>
    private void CiteKnows(
        ICollection<ScoringModel> scoringModels = null,
        ICollection<TeamType> teamTypes = null)
    {
        Factory.Cite
            .GetScoringModelsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>())
            .Returns(scoringModels ?? []);
        Factory.Cite.GetTeamTypesAsync(Arg.Any<CancellationToken>())
            .Returns(teamTypes ?? []);
    }

    /// <summary>
    /// A file holding one data field of <paramref name="dataType"/> and one scenario event whose value in
    /// it is <paramref name="value"/>, and the id the field was exported under.
    /// </summary>
    private static (string File, Guid DataFieldId) ExportedWithOneCell(DataFieldType dataType, string value)
    {
        var dataFieldId = Guid.Empty;
        var file = Exported(m =>
        {
            var dataField = TestData.DataField(mselId: m.Id, dataType: dataType);
            var scenarioEvent = TestData.ScenarioEvent(m.Id);
            scenarioEvent.DataValues.Add(TestData.DataValue(dataField.Id, scenarioEvent.Id, value));
            m.DataFields.Add(dataField);
            m.ScenarioEvents.Add(scenarioEvent);
            dataFieldId = dataField.Id;
        });

        return (file, dataFieldId);
    }

    /// <summary>A CITE MSEL whose file names the scoring model <paramref name="id"/> under <paramref name="name"/>.</summary>
    private static Action<MselEntity> CiteScoringModel(Guid? id, string name) => m =>
    {
        m.UseCite = true;
        m.CiteScoringModelId = id;
        m.CiteScoringModelName = name;
    };

    /// <summary>A team of <paramref name="mselId"/> carrying the CITE team type the file gives it.</summary>
    private static TeamEntity CiteTeam(Guid mselId, string shortName, Guid? typeId, string typeName, string name = null)
    {
        var team = TestData.Team(mselId);
        team.ShortName = shortName;
        team.Name = name ?? team.Name;
        team.CiteTeamTypeId = typeId;
        team.CiteTeamTypeName = typeName;

        return team;
    }

    /// <summary>
    /// Builds the file an export would produce, in the dialect the importer actually reads.
    /// </summary>
    /// <remarks>
    /// Deliberately serialized with <c>ReferenceHandler.Preserve</c> - the handler
    /// <c>UploadJsonAsync</c> deserializes with - rather than with the exporter's <c>IgnoreCycles</c>,
    /// so that these tests are about what the import does with a MSEL and not about the dialect
    /// mismatch. That mismatch is the subject of exactly one test,
    /// <see cref="Download_ThenUpload_FailsBecauseTheTwoHalvesDisagreeAboutTheDialect"/>, which uses a
    /// real export instead.
    /// </remarks>
    private static string Exported(Action<MselEntity> arrange = null)
    {
        var msel = TestData.Msel();
        arrange?.Invoke(msel);

        return JsonSerializer.Serialize(msel, new JsonSerializerOptions
        {
            ReferenceHandler = ReferenceHandler.Preserve
        });
    }

    private async Task<Msel> Upload(HttpClient client, string file)
    {
        var response = await Post(client, file, UploadRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<Msel>(response);
    }

    /// <remarks>
    /// The content must be awaited before the <c>using</c> falls out of scope - <c>TestServer</c> reads
    /// the request body inside <c>SendAsync</c>.
    /// </remarks>
    private async Task<MselImportResult> Import(HttpClient client, string file)
    {
        var response = await Post(client, file, ImportRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<MselImportResult>(response);
    }

    private const string UploadRoute = "/api/msels/json";

    private const string ImportRoute = "/api/msels/json/import";

    private async Task<HttpResponseMessage> Post(HttpClient client, string file, string path)
    {
        using var content = new MultipartFormDataContent();

        var upload = new ByteArrayContent(Encoding.UTF8.GetBytes(file));
        upload.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(upload, "ToUpload", "msel.json");

        return await client.PostAsync(path, content, Ct);
    }

    /// <summary>
    /// Reads a download as raw JSON rather than through a view model, because the export is the entity
    /// graph - it has no view model, and half of what it carries is not on <c>Msel</c>.
    /// </summary>
    private async Task<JsonElement> ReadExport(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(Ct)).RootElement;
    }

    private static JsonElement Single(JsonElement parent, string collection)
    {
        var items = parent.GetProperty(collection).EnumerateArray().ToList();

        return Assert.Single(items);
    }

    private async Task<T> Read<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
}
