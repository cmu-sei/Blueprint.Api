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
using Blueprint.Api.Tests.Infrastructure;
using Blueprint.Api.ViewModels;
using Cite.Api.Client;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

// Cite.Api.Client declares a SystemPermission of its own, so the name is ambiguous in any file that
// reaches for CITE as well as blueprint's own permissions - as the import tests do.
using SystemPermission = Blueprint.Api.Data.Enumerations.SystemPermission;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>GET api/msels/{id}/json</c> and <c>POST api/msels/json</c> - how a MSEL is moved between
/// installations.
/// </summary>
/// <remarks>
/// <para>
/// These two endpoints are the only supported way to carry an exercise from a development installation
/// to a production one, so the pair has to compose: what download writes, upload has to read. It does -
/// <see cref="Download_ThenUpload_BringsTheMselBackIn"/> - but on two accidents rather than on anything
/// stated. The halves are written in different JSON dialects (<c>ReferenceHandler.IgnoreCycles</c> out,
/// <c>ReferenceHandler.Preserve</c> in) and the export narrows the bytes to <c>Encoding.ASCII</c>; both
/// happen to be survivable today, and both are one ordinary edit away from not being. The two tests that
/// pin them say which edit.
/// </para>
/// <para>
/// Upload is a thin layer over the copy: after remapping the CITE ids it hands the deserialized entity
/// to the same <c>privateMselCopyAsync</c> that <c>POST msels/{id}/copy</c> uses. So an import inherits
/// every behaviour <see cref="MselServiceCopyTests"/> pins - the renaming, the cleared integration ids,
/// the <c>Pending</c> status, the silence over SignalR - and those are asserted here only where an
/// importer would be surprised by them.
/// </para>
/// <para>
/// The CITE remapping is the part written specifically for the import, and it is the reason moving a
/// MSEL between installations works at all: a scoring model or a team type is identified by a GUID that
/// is local to one CITE, so the import looks it up again by name. Note that this only runs when the MSEL
/// has <c>UseCite</c> set, and that the two remappings fail in opposite directions - see
/// <see cref="Upload_WhenCiteCannotBeReached_ClearsTheScoringModelButKeepsAStaleTeamType"/>.
/// </para>
/// <para>
/// One thing to know before mutation-checking the team-type block: two of its mutations mask each
/// other, and applying both at once will convince you a test is worthless when it is not. Dropping the
/// <c>"Standard"</c> preference from <c>defaultTeamType</c> leaves <c>teamTypes.FirstOrDefault()</c>,
/// and <see cref="Upload_OfACiteMselWithAStaleTeamTypeId_RemapsItByName"/> lists the team type it
/// expects first - so with the name remap <em>also</em> disabled the fallback lands on exactly the id
/// the remap would have produced, and the test stays green against two broken lines. Mutate that block
/// one line at a time. Likewise, do not flip the controller's <c>CreateMsels</c> check while testing
/// anything else: every upload test here holds that permission and nothing else, so the flip reddens
/// all twenty of them at once and tells you only what
/// <see cref="Upload_WithEditMselsOnly_Is403"/> already says.
/// </para>
/// </remarks>
public class MselServiceJsonTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // Download
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Download_ReturnsTheMselAsAFileNamedAfterIt()
    {
        var msel = BlueprintAppFactory.Msel();
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
        var msel = BlueprintAppFactory.Msel();
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
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        var team = BlueprintAppFactory.Team(msel.Id);
        var move = new MoveEntity { Id = Guid.NewGuid(), MselId = msel.Id, MoveNumber = 3, Description = "move three" };
        var organization = BlueprintAppFactory.Organization(msel.Id);
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

    /// <summary>
    /// Text outside ASCII survives the export - a curly apostrophe pasted from a document, an em dash,
    /// an accented name, a degree sign in a weather inject.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It survives by luck rather than by design, which is why this is worth a test of its own.
    /// <c>DownloadJsonAsync</c> turns the serialized MSEL into bytes with
    /// <c>Encoding.ASCII.GetBytes</c>, which on its own would replace every character above 127 with a
    /// question mark. Nothing is lost only because <c>JsonSerializer</c>'s default encoder has already
    /// escaped those characters to <c>\uXXXX</c>, so the string it is handed is pure ASCII - which the
    /// second assertion below states directly, since that invariant is the whole reason the first one
    /// holds.
    /// </para>
    /// <para>
    /// The fragility is the point. Setting <c>Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping</c>
    /// on the serializer options - the ordinary way to make exported JSON readable, and a change nobody
    /// would think to test - starts corrupting every non-English MSEL silently, with nothing logged.
    /// Changing that one call to <c>Encoding.UTF8</c> costs nothing and removes the trap:
    /// <c>UploadJsonAsync</c> reads the file back through a default <c>StreamReader</c>, which already
    /// expects UTF-8.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Download_KeepsTextOutsideAscii()
    {
        var msel = BlueprintAppFactory.Msel();
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

    [Fact]
    public async Task Download_OfAnUnknownMsel_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{Guid.NewGuid()}/json", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Characterizes a defect in the authorization. Exporting a MSEL requires the installation-wide
    /// <see cref="SystemPermission.EditMsels"/> and nothing else, so it asks neither whether the caller
    /// may view <em>this</em> MSEL nor whether they may edit it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves of that are wrong, in opposite directions. A caller holding <c>EditMsels</c> can
    /// export every MSEL in the installation, including exercises they have no role on - the export
    /// being the whole graph, that is a more complete read than <c>GET msels/{id}</c> would give them.
    /// And the MSEL's own owner cannot export their own exercise, which
    /// <see cref="Download_AsTheOwnerWithoutEditMsels_Is403"/> pins.
    /// </para>
    /// <para>
    /// Scoping this the way <c>GET msels/{id}</c> is scoped - the permission or a role on the MSEL -
    /// turns both tests red, and is the fix.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Download_OfAMselTheCallerHasNoRoleOn_Is200()
    {
        var msel = BlueprintAppFactory.Msel();
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

    /// <summary>
    /// Characterizes the other half of the defect above: the owner of a MSEL cannot export it.
    /// </summary>
    [Fact]
    public async Task Download_AsTheOwnerWithoutEditMsels_Is403()
    {
        var actor = await Actor().SeedAsync();
        var msel = BlueprintAppFactory.Msel(createdBy: actor.Id);
        await Seed(msel);

        var response = await Client(actor).GetAsync($"/api/msels/{msel.Id}/json", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Download_WithNoPermission_Is403()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        var actor = await Actor().SeedAsync();

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
        Assert.NotNull(await NewContext().Msels.SingleOrDefaultAsync(m => m.Id == imported.Id, Ct));
    }

    /// <summary>
    /// An import is a copy, so it is renamed after whoever imported it and gets a new id. The importer
    /// becomes the creator - which is what gives them any hold on the MSEL at all, since an import
    /// carries no roles.
    /// </summary>
    [Fact]
    public async Task Upload_RenamesTheMselAfterTheImporterAndGivesItANewId()
    {
        var actor = await Actor()
            .WithName("Importing User")
            .WithSystemPermissions(SystemPermission.CreateMsels)
            .SeedAsync();
        var originalId = Guid.NewGuid();
        var file = Exported(m => { m.Id = originalId; m.Name = "imported exercise"; });

        var imported = await Upload(Client(actor), file);

        Assert.Equal("imported exercise - Importing User", imported.Name);
        Assert.NotEqual(originalId, imported.Id);
        Assert.Equal(actor.Id, imported.CreatedBy);
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

        var team = await NewContext().Teams
            .Include(t => t.TeamUsers)
            .Include(t => t.UserTeamRoles)
            .AsSplitQuery()
            .SingleAsync(t => t.MselId == imported.Id, Ct);

        Assert.Equal("Blue", team.Name);
        Assert.Empty(team.TeamUsers);
        Assert.Empty(team.UserTeamRoles);
        Assert.Null(await NewContext().Users.SingleOrDefaultAsync(u => u.Id == strangerId, Ct));
    }

    /// <summary>
    /// The graph is renumbered on the way in, exactly as a copy is, so an import can never collide with
    /// something already here that shares its ids.
    /// </summary>
    [Fact]
    public async Task Upload_RenumbersTheGraph()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        var dataFieldId = Guid.NewGuid();
        var scenarioEventId = Guid.NewGuid();
        var file = Exported(m =>
        {
            m.DataFields.Add(new DataFieldEntity
            {
                Id = dataFieldId,
                MselId = m.Id,
                Name = "Headline",
                DataType = DataFieldType.String,
                DisplayOrder = 1
            });
            m.ScenarioEvents.Add(new ScenarioEventEntity
            {
                Id = scenarioEventId,
                MselId = m.Id,
                GroupOrder = 1,
                DataValues =
                [
                    new DataValueEntity
                    {
                        Id = Guid.NewGuid(),
                        ScenarioEventId = scenarioEventId,
                        DataFieldId = dataFieldId,
                        Value = "the headline"
                    }
                ]
            });
        });

        var imported = await Upload(Client(actor), file);

        var dataField = await NewContext().DataFields.SingleAsync(df => df.MselId == imported.Id, Ct);
        var dataValue = await NewContext().DataValues
            .SingleAsync(dv => dv.ScenarioEvent.MselId == imported.Id, Ct);

        Assert.NotEqual(dataFieldId, dataField.Id);
        Assert.Equal("the headline", dataValue.Value);
        // The renumbered data value still points at the renumbered data field.
        Assert.Equal(dataField.Id, dataValue.DataFieldId);
    }

    /// <summary>
    /// An import is saved with <c>SkipEventPublishing</c>, so nothing at all goes out over SignalR - not
    /// even that a new MSEL now exists. Characterized, not fixed; see
    /// <see cref="MselServiceCopyTests.Copy_BroadcastsNothing"/>, which pins the same silence on the copy
    /// this path shares.
    /// </summary>
    [Fact]
    public async Task Upload_BroadcastsNothing()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();
        Factory.Hub.Clear();

        await Upload(Client(actor), Exported());

        Assert.Empty(Factory.Hub.Sends);
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

        var response = await Post(Client(actor), "not json at all");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// Characterizes a defect. A file that explicitly nulls a collection - which is a legal way to write
    /// "this MSEL has no teams" - fails the import with a 500 rather than being read as empty, because
    /// the deserializer overwrites the property initializer and the import then iterates it.
    /// </summary>
    /// <remarks>
    /// Omitting the property entirely is fine, so this only bites a client that writes its export by
    /// hand or serializes with a policy that emits nulls. Guarding the two loops, or deserializing with
    /// <c>JsonIgnoreCondition.WhenWritingNull</c> honoured on read, turns this red.
    /// </remarks>
    [Fact]
    public async Task Upload_WithAnExplicitlyNullTeamsCollection_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Post(Client(actor), """{"Name":"no teams at all","Teams":null}""");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Upload_WithCreateMselsPermission_Is200()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Post(Client(actor), Exported());

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

        var response = await Post(Client(actor), Exported());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // The round trip
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The two halves compose: a file this API exports, this API imports. That is the whole point of the
    /// pair, and the only test here that proves it rather than assuming it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It composes despite the two halves being written in different dialects.
    /// <c>DownloadJsonAsync</c> serializes with <c>ReferenceHandler.IgnoreCycles</c>, which writes plain
    /// JSON and replaces a back-reference with <c>null</c>; <c>UploadJsonAsync</c> deserializes with
    /// <c>ReferenceHandler.Preserve</c>, whose own output shape is
    /// <c>{"$id":…,"$values":[…]}</c>. They interoperate because <c>Preserve</c> treats that metadata as
    /// optional when reading, so it accepts the plain arrays the exporter wrote.
    /// </para>
    /// <para>
    /// Worth knowing rather than fixing, but do not read the mismatch as harmless: it only works in this
    /// direction. A file written by anything that serializes with <c>Preserve</c> is accepted, and a file
    /// written with <c>IgnoreCycles</c> is accepted, so nothing pins the exporter to one dialect and a
    /// later change from <c>IgnoreCycles</c> to <c>Preserve</c> on the export side would go unnoticed
    /// here - <see cref="Exported"/>, which every other import test uses, deliberately writes the
    /// <c>Preserve</c> dialect for exactly that reason. Settling on one handler for both halves would
    /// leave this test green and make the pair say what it means.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Download_ThenUpload_BringsTheMselBackIn()
    {
        var msel = BlueprintAppFactory.Msel();
        msel.Description = "the exercise as exported";
        await Seed(msel);
        var team = BlueprintAppFactory.Team(msel.Id);
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
        Assert.Equal($"{msel.Name} - Round Tripper", reimported.Name);
        Assert.Equal("the exercise as exported", reimported.Description);

        var reimportedTeam = await NewContext().Teams.SingleAsync(t => t.MselId == reimported.Id, Ct);
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

    /// <summary>
    /// A name this CITE does not know clears the id, deliberately: the MSEL arrives needing a scoring
    /// model chosen by hand rather than pointing at nothing.
    /// </summary>
    /// <remarks>
    /// The name is kept on the entity so the UI can say what the exercise was scored with - except that
    /// <c>MselEntity.CiteScoringModelName</c> is not on the <c>Msel</c> view model, so no client can
    /// read it. See <see cref="MselServiceCopyTests.Copy_KeepsTheCiteScoringModel"/>; this test therefore
    /// reads the name off the database rather than the response.
    /// </remarks>
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

        var stored = await NewContext().Msels.SingleAsync(m => m.Id == imported.Id, Ct);
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

        var stored = await NewContext().Msels.SingleAsync(m => m.Id == imported.Id, Ct);
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

        var team = await NewContext().Teams.SingleAsync(t => t.MselId == imported.Id, Ct);
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

        var team = await NewContext().Teams.SingleAsync(t => t.MselId == imported.Id, Ct);
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

        var team = await NewContext().Teams.SingleAsync(t => t.MselId == imported.Id, Ct);
        Assert.Equal(localId, team.CiteTeamTypeId);
    }

    /// <summary>
    /// A stale id whose name this CITE does not know falls back to the default type rather than being
    /// cleared - a team must have a type for CITE to accept the exercise, so guessing beats nothing.
    /// </summary>
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

        var team = await NewContext().Teams.SingleAsync(t => t.MselId == imported.Id, Ct);
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
        Assert.NotNull(await NewContext().Msels.SingleOrDefaultAsync(m => m.Id == imported.Id, Ct));
    }

    /// <summary>
    /// Characterizes an inconsistency worth knowing before relying on either half. Neither remapping
    /// blocks the import when CITE cannot be reached - both swallow the failure and log a warning - but
    /// they leave the MSEL in opposite states: the scoring model id is cleared, and a stale team type id
    /// is kept.
    /// </summary>
    /// <remarks>
    /// So an import done while CITE is down produces a MSEL that needs its scoring model chosen by hand
    /// and whose teams silently point at team types belonging to another installation, which is the shape
    /// that fails later, at push time, a long way from here. Making the team-type catch clear or default
    /// the ids the way the scoring-model catch does turns the second half of this red.
    /// </remarks>
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

        var team = await NewContext().Teams.SingleAsync(t => t.MselId == imported.Id, Ct);
        Assert.Equal(staleTeamTypeId, team.CiteTeamTypeId);
    }

    /// <summary>
    /// CITE naming two scoring models the same way is not an error: the first wins, so the import
    /// resolves rather than throwing on a duplicate key.
    /// </summary>
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
        var msel = BlueprintAppFactory.Msel();
        arrange?.Invoke(msel);

        return JsonSerializer.Serialize(msel, new JsonSerializerOptions
        {
            ReferenceHandler = ReferenceHandler.Preserve
        });
    }

    private async Task<Msel> Upload(HttpClient client, string file)
    {
        var response = await Post(client, file);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<Msel>(response);
    }

    /// <remarks>
    /// The content must be awaited before the <c>using</c> falls out of scope - <c>TestServer</c> reads
    /// the request body inside <c>SendAsync</c>.
    /// </remarks>
    private async Task<HttpResponseMessage> Post(HttpClient client, string file)
    {
        using var content = new MultipartFormDataContent();

        var upload = new ByteArrayContent(Encoding.UTF8.GetBytes(file));
        upload.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(upload, "ToUpload", "msel.json");

        return await client.PostAsync("/api/msels/json", content, Ct);
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
