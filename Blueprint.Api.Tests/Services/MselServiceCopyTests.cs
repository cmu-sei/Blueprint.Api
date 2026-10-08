// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary><c>POST api/msels/{id}/copy</c>, and through it <c>MselService.privateMselCopyAsync</c> - the
/// deepest graph clone in the codebase, and the one the launch-by-invitation path reuses.</summary>
public class MselServiceCopyTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // Who may copy
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Copy_WithCreateMselsPermission_Is201AndPointsAtTheNewMsel()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var actor = await Copier().SeedAsync();

        var response = await Client(actor).PostAsync($"/api/msels/{msel.Id}/copy", null, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var copy = await Read<Msel>(response);
        Assert.NotEqual(msel.Id, copy.Id);
        Assert.Equal($"/api/msels/{copy.Id}", response.Headers.Location?.AbsolutePath);
    }

    /// <summary>Copy of a MSEL the caller cannot see is answered with a 201 and hands over its contents.</summary>
    [Fact]
    public async Task Copy_OfAMselTheCallerCannotSee_Is201AndHandsOverItsContents()
    {
        var graph = await SeedGraph();
        // CreateMsels alone, which is the point: this actor has no way to view the MSEL.
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        // Proof the caller cannot read it the honest way.
        var read = await Client(actor).GetAsync($"/api/msels/{graph.Msel.Id}", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);

        var copy = await Copy(Client(actor), graph.Msel.Id);

        var events = await ReadBack(rb => rb.ScenarioEvents
            .Where(x => x.MselId == copy.Id)
            .Include(x => x.DataValues)
            .SelectMany(x => x.DataValues)
            .Select(x => x.Value)
            .ToListAsync(Ct));

        Assert.Contains(GraphSeed.SecretValue, events);
    }

    /// <summary>The owner of a MSEL who lacks CreateMsels is refused the copy.</summary>
    [Fact]
    public async Task Copy_AsTheOwnerWithoutCreateMsels_Is403()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).PostAsync($"/api/msels/{msel.Id}/copy", null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Copy of an unknown MSEL is answered with a 404.</summary>
    [Fact]
    public async Task Copy_OfAnUnknownMsel_Is404()
    {
        var actor = await Copier().SeedAsync();

        var response = await Client(actor).PostAsync($"/api/msels/{Guid.NewGuid()}/copy", null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // What the copy is
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Copy_NamesTheCopyAfterTheCallerAndMakesThemTheCreator()
    {
        var msel = TestData.Msel();
        msel.Name = "Exercise Vigilant Badger";
        await Seed(msel);
        var actor = await Copier().WithName("Dana Okonkwo").SeedAsync();

        var copy = await Copy(Client(actor), msel.Id);

        Assert.Equal("Exercise Vigilant Badger - Dana Okonkwo", copy.Name);
        Assert.Equal(actor.Id, copy.CreatedBy);
    }

    /// <summary>
    /// A copy starts over: pending, and not a template however it was made.
    /// </summary>
    [Theory]
    [InlineData(MselItemStatus.Deployed, false)]
    [InlineData(MselItemStatus.Archived, false)]
    [InlineData(MselItemStatus.Approved, true)]
    public async Task Copy_IsAPendingNonTemplate(MselItemStatus status, bool isTemplate)
    {
        var msel = TestData.Msel(isTemplate: isTemplate, status: status);
        await Seed(msel);
        var actor = await Copier().SeedAsync();

        var copy = await Copy(Client(actor), msel.Id);

        Assert.Equal(MselItemStatus.Pending, copy.Status);
        Assert.False(copy.IsTemplate);
    }

    /// <summary>
    /// The copy is not deployed anywhere, so every pointer at a thing in another application is dropped.
    /// Keeping one would make two MSELs claim the same Player view, and tearing either down would take
    /// the other's environment with it.
    /// </summary>
    [Fact]
    public async Task Copy_ClearsTheIntegrationIds()
    {
        var msel = TestData.Msel(status: MselItemStatus.Deployed);
        msel.PlayerViewId = Guid.NewGuid();
        msel.GalleryCollectionId = Guid.NewGuid();
        msel.GalleryExhibitId = Guid.NewGuid();
        msel.CiteEvaluationId = Guid.NewGuid();
        msel.SteamfitterScenarioId = Guid.NewGuid();
        await Seed(msel);
        var actor = await Copier().SeedAsync();

        var copy = await Copy(Client(actor), msel.Id);

        Assert.Null(copy.PlayerViewId);
        Assert.Null(copy.GalleryCollectionId);
        Assert.Null(copy.GalleryExhibitId);
        Assert.Null(copy.CiteEvaluationId);
        Assert.Null(copy.SteamfitterScenarioId);
    }

    /// <summary>The copy keeps the CITE scoring model, the one integration pointer it does not clear.</summary>
    [Fact]
    public async Task Copy_KeepsTheCiteScoringModel()
    {
        var msel = TestData.Msel();
        msel.UseCite = true;
        msel.CiteScoringModelId = Guid.NewGuid();
        msel.CiteScoringModelName = "Vigilance";
        await Seed(msel);
        var actor = await Copier().SeedAsync();

        var copy = await Copy(Client(actor), msel.Id);

        Assert.Equal(msel.CiteScoringModelId, copy.CiteScoringModelId);

        var stored = await ReadBack(rb => rb.Msels.SingleAsync(x => x.Id == copy.Id, Ct));
        Assert.Equal("Vigilance", stored.CiteScoringModelName);
    }

    /// <summary>
    /// The service copies the source entity's audit fields along with everything else and never resets
    /// them; <c>BlueprintContext.SaveEntries</c> is what makes the copy's creation date its own.
    /// </summary>
    [Fact]
    public async Task Copy_StampsTheCopysAuditFieldsOnTheServer()
    {
        var msel = TestData.Msel();
        msel.DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        msel.DateModified = new DateTime(1999, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        msel.ModifiedBy = Guid.NewGuid();
        await Seed(msel);
        var actor = await Copier().SeedAsync();
        var before = DateTime.UtcNow;

        var copy = await Copy(Client(actor), msel.Id);

        var stored = await ReadBack(rb => rb.Msels.SingleAsync(x => x.Id == copy.Id, Ct));
        Assert.InRange(stored.DateCreated, before, DateTime.UtcNow);
        Assert.Null(stored.DateModified);
        Assert.Null(stored.ModifiedBy);
    }

    /// <summary>
    /// The service mutates the entity it read in place - new id, new name, cleared integration ids - and
    /// relies on <c>AsNoTracking</c> to keep that off the original. Worth pinning explicitly: dropping
    /// <c>AsNoTracking</c> would turn a copy into a rename of the source.
    /// </summary>
    [Fact]
    public async Task Copy_LeavesTheOriginalAlone()
    {
        var msel = TestData.Msel(status: MselItemStatus.Deployed);
        msel.Name = "Exercise Vigilant Badger";
        msel.IsTemplate = true;
        msel.PlayerViewId = Guid.NewGuid();
        await Seed(msel);
        var actor = await Copier().SeedAsync();

        await Copy(Client(actor), msel.Id);

        var original = await ReadBack(rb => rb.Msels.SingleAsync(x => x.Id == msel.Id, Ct));
        Assert.Equal("Exercise Vigilant Badger", original.Name);
        Assert.True(original.IsTemplate);
        Assert.Equal(MselItemStatus.Deployed, original.Status);
        Assert.Equal(msel.PlayerViewId, original.PlayerViewId);
        Assert.Equal(msel.CreatedBy, original.CreatedBy);
    }

    /// <summary>
    /// The Gallery parameter name lists are computed from the enums on the way out, exactly as
    /// <c>GetAsync</c> does it - a third copy of the same three lines, which is why this is asserted here
    /// as well as on the read.
    /// </summary>
    [Fact]
    public async Task Copy_OfAGalleryMsel_IncludesTheGalleryParameterNames()
    {
        var msel = TestData.Msel();
        msel.UseGallery = true;
        await Seed(msel);
        var actor = await Copier().SeedAsync();

        var copy = await Copy(Client(actor), msel.Id);

        Assert.NotEmpty(copy.GalleryArticleParameters);
        Assert.NotEmpty(copy.GallerySourceTypes);
    }

    [Fact]
    public async Task Copy_OfANonGalleryMsel_LeavesTheGalleryListsEmpty()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var actor = await Copier().SeedAsync();

        var copy = await Copy(Client(actor), msel.Id);

        Assert.Empty(copy.GalleryArticleParameters);
        Assert.Empty(copy.GallerySourceTypes);
    }

    // ---------------------------------------------------------------------------------------------
    // The graph
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Copy_CopiesTheDataFieldsAndTheirOptions()
    {
        var graph = await SeedGraph();
        var actor = await Copier().SeedAsync();

        var copy = await Copy(Client(actor), graph.Msel.Id);

        await using var db = NewContext();
        var field = await db.DataFields
            .Include(x => x.DataOptions)
            .SingleAsync(x => x.MselId == copy.Id, Ct);

        Assert.NotEqual(graph.DataField.Id, field.Id);
        Assert.Equal(graph.DataField.Name, field.Name);
        Assert.Equal(actor.Id, field.CreatedBy);

        var option = Assert.Single(field.DataOptions);
        Assert.NotEqual(graph.DataOption.Id, option.Id);
        Assert.Equal(field.Id, option.DataFieldId);
        Assert.Equal(actor.Id, option.CreatedBy);
    }

    [Fact]
    public async Task Copy_CopiesTheMovesOrganizationsAndPages()
    {
        var graph = await SeedGraph();
        var actor = await Copier().SeedAsync();

        var copy = await Copy(Client(actor), graph.Msel.Id);

        await using var db = NewContext();
        var move = await db.Moves.SingleAsync(x => x.MselId == copy.Id, Ct);
        Assert.NotEqual(graph.Move.Id, move.Id);
        Assert.Equal(graph.Move.MoveNumber, move.MoveNumber);
        Assert.Equal(actor.Id, move.CreatedBy);

        var organization = await db.Organizations.SingleAsync(x => x.MselId == copy.Id, Ct);
        Assert.NotEqual(graph.Organization.Id, organization.Id);
        Assert.Equal(graph.Organization.Name, organization.Name);
        Assert.Equal(actor.Id, organization.CreatedBy);

        var page = await db.MselPages.SingleAsync(x => x.MselId == copy.Id, Ct);
        Assert.NotEqual(graph.Page.Id, page.Id);
        Assert.Equal(graph.Page.Content, page.Content);
    }

    /// <summary>
    /// The scenario events come last on purpose: their data values point at the copied data fields, whose
    /// new ids only exist because the data field loop ran first.
    /// </summary>
    [Fact]
    public async Task Copy_CopiesTheScenarioEventsAndPointsTheirDataValuesAtTheNewDataFields()
    {
        var graph = await SeedGraph();
        var actor = await Copier().SeedAsync();

        var copy = await Copy(Client(actor), graph.Msel.Id);

        await using var db = NewContext();
        var newFieldId = (await db.DataFields.SingleAsync(x => x.MselId == copy.Id, Ct)).Id;
        var scenarioEvent = await db.ScenarioEvents
            .Include(x => x.DataValues)
            .SingleAsync(x => x.MselId == copy.Id, Ct);

        Assert.NotEqual(graph.ScenarioEvent.Id, scenarioEvent.Id);
        Assert.Equal(graph.ScenarioEvent.DeltaSeconds, scenarioEvent.DeltaSeconds);
        Assert.Equal(actor.Id, scenarioEvent.CreatedBy);

        Assert.All(scenarioEvent.DataValues, x => Assert.Equal(newFieldId, x.DataFieldId));
        Assert.All(scenarioEvent.DataValues, x => Assert.Equal(scenarioEvent.Id, x.ScenarioEventId));
        Assert.All(scenarioEvent.DataValues, x => Assert.Equal(actor.Id, x.CreatedBy));
    }

    /// <summary>
    /// A data value is text, but a scenario event that targets a team stores that team's id in one - so a
    /// value that parses as a GUID is run through the same crosswalk the foreign keys are, and a copied
    /// event ends up naming the copied team.
    /// </summary>
    [Fact]
    public async Task Copy_RepointsADataValueThatHoldsTheIdOfSomethingElseItCopied()
    {
        var graph = await SeedGraph();
        var actor = await Copier().SeedAsync();

        var copy = await Copy(Client(actor), graph.Msel.Id);

        await using var db = NewContext();
        var newTeamId = (await db.Teams.SingleAsync(x => x.MselId == copy.Id, Ct)).Id;
        var values = await db.ScenarioEvents
            .Where(x => x.MselId == copy.Id)
            .SelectMany(x => x.DataValues)
            .Select(x => x.Value)
            .ToListAsync(Ct);

        Assert.Contains(newTeamId.ToString(), values);
        Assert.DoesNotContain(graph.Team.Id.ToString(), values);
    }

    /// <summary>Copy empties a data value that looks like an id but is not.</summary>
    [Fact]
    public async Task Copy_EmptiesADataValueThatLooksLikeAnIdButIsNot()
    {
        var graph = await SeedGraph();
        var actor = await Copier().SeedAsync();

        var copy = await Copy(Client(actor), graph.Msel.Id);

        var values = await ReadBack(rb => rb.ScenarioEvents
            .Where(x => x.MselId == copy.Id)
            .SelectMany(x => x.DataValues)
            .Select(x => new { x.CellMetadata, x.Value })
            .ToListAsync(Ct));

        var stranger = Assert.Single(values, x => x.CellMetadata == GraphSeed.StrangerMarker);
        Assert.Null(stranger.Value);
    }

    [Fact]
    public async Task Copy_CopiesTheScenarioEventsSteamfitterTask()
    {
        var graph = await SeedGraph();
        var actor = await Copier().SeedAsync();

        var copy = await Copy(Client(actor), graph.Msel.Id);

        var scenarioEvent = await ReadBack(rb => rb.ScenarioEvents
            .Include(x => x.SteamfitterTask)
            .SingleAsync(x => x.MselId == copy.Id, Ct));

        Assert.NotNull(scenarioEvent.SteamfitterTask);
        Assert.NotEqual(graph.SteamfitterTask.Id, scenarioEvent.SteamfitterTask.Id);
        Assert.Equal(scenarioEvent.SteamfitterTaskId, scenarioEvent.SteamfitterTask.Id);
        Assert.Equal(scenarioEvent.Id, scenarioEvent.SteamfitterTask.ScenarioEventId);
        Assert.Equal(graph.SteamfitterTask.Name, scenarioEvent.SteamfitterTask.Name);
        Assert.Equal(actor.Id, scenarioEvent.SteamfitterTask.CreatedBy);
    }

    /// <summary>
    /// A scenario event built from a catalog inject keeps a link back to it. The copy drops the link, so
    /// the copied event is a plain event that no longer tracks the inject it came from.
    /// </summary>
    [Fact]
    public async Task Copy_DropsTheLinkBackToTheInject()
    {
        var graph = await SeedGraph();
        var actor = await Copier().SeedAsync();

        var copy = await Copy(Client(actor), graph.Msel.Id);

        var scenarioEvent = await ReadBack(rb => rb.ScenarioEvents
            .SingleAsync(x => x.MselId == copy.Id, Ct));

        Assert.NotNull(graph.ScenarioEvent.InjectId);
        Assert.Null(scenarioEvent.InjectId);
    }

    [Fact]
    public async Task Copy_CopiesTheTeamsWithTheirMembersAndRoles()
    {
        var graph = await SeedGraph();
        var actor = await Copier().SeedAsync();

        var copy = await Copy(Client(actor), graph.Msel.Id);

        // Two collection includes, and the context throws on MultipleCollectionIncludeWarning exactly as
        // production's does.
        var team = await ReadBack(rb => rb.Teams
            .Include(x => x.TeamUsers)
            .Include(x => x.UserTeamRoles)
            .AsSplitQuery()
            .SingleAsync(x => x.MselId == copy.Id, Ct));

        Assert.NotEqual(graph.Team.Id, team.Id);
        Assert.Equal(graph.Team.ShortName, team.ShortName);
        Assert.Equal(actor.Id, team.CreatedBy);

        var member = Assert.Single(team.TeamUsers);
        Assert.Equal(graph.Member.Id, member.UserId);
        Assert.Equal(team.Id, member.TeamId);

        var role = Assert.Single(team.UserTeamRoles);
        Assert.Equal(graph.Member.Id, role.UserId);
        Assert.Equal("Submitter", role.Role);
        Assert.Equal(team.Id, role.TeamId);
    }

    /// <summary>
    /// A card's audience is a set of teams. Every one of those rows has to be renumbered twice - its own
    /// key and the team it points at - and the second is the one that needs the crosswalk.
    /// </summary>
    [Fact]
    public async Task Copy_CopiesTheCardsAndPointsThemAtTheNewTeams()
    {
        var graph = await SeedGraph();
        var actor = await Copier().SeedAsync();

        var copy = await Copy(Client(actor), graph.Msel.Id);

        await using var db = NewContext();
        var newTeamId = (await db.Teams.SingleAsync(x => x.MselId == copy.Id, Ct)).Id;
        var card = await db.Cards
            .Include(x => x.CardTeams)
            .SingleAsync(x => x.MselId == copy.Id, Ct);

        Assert.NotEqual(graph.Card.Id, card.Id);
        Assert.Equal(graph.Card.Name, card.Name);
        Assert.Equal(actor.Id, card.CreatedBy);

        var cardTeam = Assert.Single(card.CardTeams);
        Assert.Equal(newTeamId, cardTeam.TeamId);
        Assert.Equal(card.Id, cardTeam.CardId);
        Assert.True(cardTeam.IsShownOnWall);
    }

    /// <summary>
    /// A card that was pushed to Gallery carries the id it was given there. The copy has not been pushed,
    /// so it must not claim one.
    /// </summary>
    [Fact]
    public async Task Copy_ClearsACardsGalleryId()
    {
        var graph = await SeedGraph();
        var actor = await Copier().SeedAsync();

        var copy = await Copy(Client(actor), graph.Msel.Id);

        var card = await ReadBack(rb => rb.Cards.SingleAsync(x => x.MselId == copy.Id, Ct));

        Assert.NotNull(graph.Card.GalleryId);
        Assert.Null(card.GalleryId);
    }

    [Fact]
    public async Task Copy_CopiesThePlayerApplicationsAndPointsThemAtTheNewTeams()
    {
        var graph = await SeedGraph();
        var actor = await Copier().SeedAsync();

        var copy = await Copy(Client(actor), graph.Msel.Id);

        await using var db = NewContext();
        var newTeamId = (await db.Teams.SingleAsync(x => x.MselId == copy.Id, Ct)).Id;
        var application = await db.PlayerApplications
            .Include(x => x.PlayerApplicationTeams)
            .SingleAsync(x => x.MselId == copy.Id, Ct);

        Assert.NotEqual(graph.PlayerApplication.Id, application.Id);
        Assert.Equal(graph.PlayerApplication.Url, application.Url);
        Assert.Equal(actor.Id, application.CreatedBy);

        var applicationTeam = Assert.Single(application.PlayerApplicationTeams);
        Assert.Equal(newTeamId, applicationTeam.TeamId);
        Assert.Equal(application.Id, applicationTeam.PlayerApplicationId);
    }

    [Fact]
    public async Task Copy_CopiesTheCiteActionsAndDutiesAndPointsThemAtTheNewTeams()
    {
        var graph = await SeedGraph();
        var actor = await Copier().SeedAsync();

        var copy = await Copy(Client(actor), graph.Msel.Id);

        await using var db = NewContext();
        var newTeamId = (await db.Teams.SingleAsync(x => x.MselId == copy.Id, Ct)).Id;

        var action = await db.CiteActions.SingleAsync(x => x.MselId == copy.Id, Ct);
        Assert.NotEqual(graph.CiteAction.Id, action.Id);
        Assert.Equal(graph.CiteAction.Description, action.Description);
        Assert.Equal(newTeamId, action.TeamId);
        Assert.Equal(actor.Id, action.CreatedBy);

        var duty = await db.CiteDuties.SingleAsync(x => x.MselId == copy.Id, Ct);
        Assert.NotEqual(graph.CiteDuty.Id, duty.Id);
        Assert.Equal(graph.CiteDuty.Name, duty.Name);
        Assert.Equal(newTeamId, duty.TeamId);
        Assert.Equal(actor.Id, duty.CreatedBy);
    }

    [Fact]
    public async Task Copy_CopiesTheCompetencyAssignmentsOnTheMselAndItsTeams()
    {
        var graph = await SeedGraph();
        var actor = await Copier().SeedAsync();

        var copy = await Copy(Client(actor), graph.Msel.Id);

        await using var db = NewContext();
        var newTeamId = (await db.Teams.SingleAsync(x => x.MselId == copy.Id, Ct)).Id;

        var mselCompetency = await db.MselCompetencies.SingleAsync(x => x.MselId == copy.Id, Ct);
        Assert.NotEqual(graph.MselCompetency.Id, mselCompetency.Id);
        Assert.Equal(graph.Competency.Id, mselCompetency.CompetencyId);

        var teamCompetency = await db.TeamCompetencies.SingleAsync(x => x.TeamId == newTeamId, Ct);
        Assert.NotEqual(graph.TeamCompetency.Id, teamCompetency.Id);
        Assert.Equal(graph.Competency.Id, teamCompetency.CompetencyId);
    }

    // ---------------------------------------------------------------------------------------------
    // What the copy leaves behind
    // ---------------------------------------------------------------------------------------------

    /// <summary>Copy does not copy the MSEL roles.</summary>
    [Fact]
    public async Task Copy_DoesNotCopyTheMselRoles()
    {
        var graph = await SeedGraph();
        await Db.AddMselRoleAsync(graph.Member.Id, graph.Msel.Id, MselRole.Editor, Ct);
        var actor = await Copier().SeedAsync();

        var copy = await Copy(Client(actor), graph.Msel.Id);

        await using var db = NewContext();
        Assert.NotEmpty(await db.UserMselRoles.Where(x => x.MselId == graph.Msel.Id).ToListAsync(Ct));
        Assert.Empty(await db.UserMselRoles.Where(x => x.MselId == copy.Id).ToListAsync(Ct));
    }

    /// <summary>The units assigned to the source MSEL are not assigned to the copy.</summary>
    [Fact]
    public async Task Copy_DoesNotCopyTheUnitAssignments()
    {
        var graph = await SeedGraph();
        await Db.AddUnitMembershipAsync(graph.Member.Id, graph.Msel.Id, Ct);
        var actor = await Copier().SeedAsync();

        var copy = await Copy(Client(actor), graph.Msel.Id);

        await using var db = NewContext();
        Assert.NotEmpty(await db.MselUnits.Where(x => x.MselId == graph.Msel.Id).ToListAsync(Ct));
        Assert.Empty(await db.MselUnits.Where(x => x.MselId == copy.Id).ToListAsync(Ct));
    }

    /// <summary>
    /// Invitations are deliberately not copied, and this is the one omission that is clearly right: an
    /// invitation carries an expiry, a seat count and a running total of who has used it, none of which
    /// mean anything on a different MSEL.
    /// </summary>
    [Fact]
    public async Task Copy_DoesNotCopyTheInvitations()
    {
        var graph = await SeedGraph();
        await Seed(new InvitationEntity
        {
            Id = Guid.NewGuid(),
            MselId = graph.Msel.Id,
            TeamId = graph.Team.Id,
            EmailDomain = "example.test",
            MaxUsersAllowed = 5,
            UserCount = 2
        });
        var actor = await Copier().SeedAsync();

        var copy = await Copy(Client(actor), graph.Msel.Id);

        await using var db = NewContext();
        Assert.NotEmpty(await db.Invitations.Where(x => x.MselId == graph.Msel.Id).ToListAsync(Ct));
        Assert.Empty(await db.Invitations.Where(x => x.MselId == copy.Id).ToListAsync(Ct));
    }

    /// <summary>Copy broadcasts nothing.</summary>
    [Fact]
    public async Task Copy_BroadcastsNothing()
    {
        var graph = await SeedGraph();
        var actor = await Copier().SeedAsync();

        await Copy(Client(actor), graph.Msel.Id);

        Assert.Empty(Hub.Sent(graph.Msel.Id));
    }

    // ---------------------------------------------------------------------------------------------
    // Where the crosswalk runs out
    // ---------------------------------------------------------------------------------------------

    /// <summary>Copy of a MSEL with a teamless CITE duty is answered with a 500.</summary>
    [Fact]
    public async Task Copy_OfAMselWithATeamlessCiteDuty_Is500()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(new CiteDutyEntity
        {
            Id = Guid.NewGuid(),
            MselId = msel.Id,
            TeamId = null,
            Name = "unassigned duty",
            CreatedBy = msel.CreatedBy
        });
        var actor = await Copier().SeedAsync();

        var response = await Client(actor).PostAsync($"/api/msels/{msel.Id}/copy", null, Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Nullable object must have a value.", failure.Title);
        Assert.Contains("MselService.privateMselCopyAsync", failure.Detail);
    }

    /// <summary>Copy of a MSEL whose card is shown to another MSELs team is answered with a 500.</summary>
    [Fact]
    public async Task Copy_OfAMselWhoseCardIsShownToAnotherMselsTeam_Is500()
    {
        var msel = TestData.Msel();
        var other = TestData.Msel();
        await Seed(msel, other);
        var otherTeam = TestData.Team(other.Id);
        await Seed(otherTeam);
        var card = new CardEntity
        {
            Id = Guid.NewGuid(),
            MselId = msel.Id,
            Name = "card",
            CreatedBy = msel.CreatedBy
        };
        await Seed(card);
        await Seed(new CardTeamEntity
        {
            Id = Guid.NewGuid(),
            CardId = card.Id,
            TeamId = otherTeam.Id
        });
        var actor = await Copier().SeedAsync();

        var response = await Client(actor).PostAsync($"/api/msels/{msel.Id}/copy", null, Ct);

        Assert.Matches(@"^The\ given\ key\ '[0-9a-f-]{36}'\ was\ not\ present\ in\ the\ dictionary\.$", (await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>An actor holding <c>CreateMsels</c> and <c>ViewMsels</c>.</summary>
    private TestActorBuilder Copier() =>
        Actor().WithSystemPermissions(SystemPermission.CreateMsels, SystemPermission.ViewMsels);

    /// <summary>
    /// One of everything the copy walks, so a test can name the source row a copied row came from.
    /// </summary>
    private sealed class GraphSeed
    {
        /// <summary>
        /// Text on a data value of the source MSEL. Nothing but the copy could put it on the copy, which
        /// is what makes it evidence in <c>Copy_OfAMselTheCallerCannotSee_...</c>.
        /// </summary>
        public const string SecretValue = "the injected phishing email, in draft";

        /// <summary>
        /// Marks the data value holding a GUID the crosswalk knows nothing about, so the test that pins
        /// the wipe can find it again afterwards.
        /// </summary>
        public const string StrangerMarker = "stranger";

        public MselEntity Msel { get; init; }
        public UserEntity Member { get; init; }
        public TeamEntity Team { get; init; }
        public DataFieldEntity DataField { get; init; }
        public DataOptionEntity DataOption { get; init; }
        public ScenarioEventEntity ScenarioEvent { get; init; }
        public SteamfitterTaskEntity SteamfitterTask { get; init; }
        public MoveEntity Move { get; init; }
        public OrganizationEntity Organization { get; init; }
        public MselPageEntity Page { get; init; }
        public CardEntity Card { get; init; }
        public CiteActionEntity CiteAction { get; init; }
        public CiteDutyEntity CiteDuty { get; init; }
        public PlayerApplicationEntity PlayerApplication { get; init; }
        public CompetencyEntity Competency { get; init; }
        public MselCompetencyEntity MselCompetency { get; init; }
        public TeamCompetencyEntity TeamCompetency { get; init; }
    }

    /// <summary>
    /// Seeds an MSEL with one row in every collection the copy walks, wired together the way a real one
    /// is: the card, the CITE action, the CITE duty and the Player application all point at the team, and
    /// the scenario event's data values include the team's own id and a GUID belonging to nothing.
    /// </summary>
    private async Task<GraphSeed> SeedGraph()
    {
        var creator = Guid.NewGuid();
        var msel = TestData.Msel(createdBy: creator);
        await Seed(msel);

        var member = new UserEntity { Id = Guid.NewGuid(), Name = "team member" };
        var team = TestData.Team(msel.Id, creator);
        team.CiteTeamTypeId = Guid.NewGuid();
        await Seed(member, team);
        await Seed(
            new TeamUserEntity { Id = Guid.NewGuid(), TeamId = team.Id, UserId = member.Id },
            new UserTeamRoleEntity
            {
                Id = Guid.NewGuid(),
                TeamId = team.Id,
                UserId = member.Id,
                Role = "Submitter",
                CreatedBy = creator
            });

        var dataField = new DataFieldEntity
        {
            Id = Guid.NewGuid(),
            MselId = msel.Id,
            Name = "Target",
            DataType = DataFieldType.String,
            DisplayOrder = 1,
            CreatedBy = creator
        };
        await Seed(dataField);
        var dataOption = new DataOptionEntity
        {
            Id = Guid.NewGuid(),
            DataFieldId = dataField.Id,
            OptionName = "an option",
            OptionValue = "an option",
            DisplayOrder = 1,
            CreatedBy = creator
        };
        await Seed(dataOption);

        var inject = TestData.InjectType(creator);
        await Seed(inject);
        var catalog = TestData.Catalog(inject.Id, creator);
        await Seed(catalog);
        var injectEntity = new InjectEntity
        {
            Id = Guid.NewGuid(),
            InjectTypeId = inject.Id,
            CreatedBy = creator
        };
        await Seed(injectEntity);

        var scenarioEvent = new ScenarioEventEntity
        {
            Id = Guid.NewGuid(),
            MselId = msel.Id,
            DeltaSeconds = 600,
            InjectId = injectEntity.Id,
            CreatedBy = creator
        };
        await Seed(scenarioEvent);
        await Seed(
            // Plain text: carried across unchanged.
            new DataValueEntity
            {
                Id = Guid.NewGuid(),
                ScenarioEventId = scenarioEvent.Id,
                DataFieldId = dataField.Id,
                Value = GraphSeed.SecretValue,
                CreatedBy = creator
            },
            // The team's own id: rewritten to the copied team's.
            new DataValueEntity
            {
                Id = Guid.NewGuid(),
                ScenarioEventId = scenarioEvent.Id,
                DataFieldId = dataField.Id,
                Value = team.Id.ToString(),
                CreatedBy = creator
            },
            // A GUID the crosswalk has never heard of.
            new DataValueEntity
            {
                Id = Guid.NewGuid(),
                ScenarioEventId = scenarioEvent.Id,
                DataFieldId = dataField.Id,
                Value = Guid.NewGuid().ToString(),
                CellMetadata = GraphSeed.StrangerMarker,
                CreatedBy = creator
            });

        var steamfitterTask = new SteamfitterTaskEntity
        {
            Id = Guid.NewGuid(),
            ScenarioEventId = scenarioEvent.Id,
            Name = "restart the mail server",
            CreatedBy = creator
        };
        await Seed(steamfitterTask);
        scenarioEvent.SteamfitterTaskId = steamfitterTask.Id;
        await Db.SaveChangesAsync(Ct);

        var move = new MoveEntity
        {
            Id = Guid.NewGuid(),
            MselId = msel.Id,
            MoveNumber = 1,
            Description = "move one",
            CreatedBy = creator
        };
        var organization = TestData.Organization(msel.Id, creator);
        var page = new MselPageEntity
        {
            Id = Guid.NewGuid(),
            MselId = msel.Id,
            Name = "briefing",
            Content = "<p>read this first</p>"
        };
        await Seed(move, organization, page);

        var card = new CardEntity
        {
            Id = Guid.NewGuid(),
            MselId = msel.Id,
            Name = "front page",
            Move = 1,
            GalleryId = Guid.NewGuid(),
            CreatedBy = creator
        };
        var citeAction = new CiteActionEntity
        {
            Id = Guid.NewGuid(),
            MselId = msel.Id,
            TeamId = team.Id,
            MoveNumber = 1,
            Description = "brief the board",
            CreatedBy = creator
        };
        var citeDuty = new CiteDutyEntity
        {
            Id = Guid.NewGuid(),
            MselId = msel.Id,
            TeamId = team.Id,
            Name = "incident lead",
            CreatedBy = creator
        };
        var playerApplication = new PlayerApplicationEntity
        {
            Id = Guid.NewGuid(),
            MselId = msel.Id,
            Name = "the SIEM",
            Url = "https://siem.example.test",
            CreatedBy = creator
        };
        await Seed(card, citeAction, citeDuty, playerApplication);
        await Seed(
            new CardTeamEntity
            {
                Id = Guid.NewGuid(),
                CardId = card.Id,
                TeamId = team.Id,
                IsShownOnWall = true
            },
            new PlayerApplicationTeamEntity
            {
                Id = Guid.NewGuid(),
                PlayerApplicationId = playerApplication.Id,
                TeamId = team.Id,
                DisplayOrder = 1
            });

        var framework = new CompetencyFrameworkEntity
        {
            Id = Guid.NewGuid(),
            Name = "a framework",
            IdNumber = $"framework-{Guid.NewGuid()}",
            CreatedBy = creator
        };
        await Seed(framework);
        var competency = new CompetencyEntity
        {
            Id = Guid.NewGuid(),
            CompetencyFrameworkId = framework.Id,
            IdNumber = "1",
            ShortName = "triage",
            CreatedBy = creator
        };
        await Seed(competency);
        var mselCompetency = new MselCompetencyEntity
        {
            Id = Guid.NewGuid(),
            MselId = msel.Id,
            CompetencyId = competency.Id
        };
        var teamCompetency = new TeamCompetencyEntity
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            CompetencyId = competency.Id
        };
        await Seed(mselCompetency, teamCompetency);

        return new GraphSeed
        {
            Msel = msel,
            Member = member,
            Team = team,
            DataField = dataField,
            DataOption = dataOption,
            ScenarioEvent = scenarioEvent,
            SteamfitterTask = steamfitterTask,
            Move = move,
            Organization = organization,
            Page = page,
            Card = card,
            CiteAction = citeAction,
            CiteDuty = citeDuty,
            PlayerApplication = playerApplication,
            Competency = competency,
            MselCompetency = mselCompetency,
            TeamCompetency = teamCompetency
        };
    }

    private async Task<Msel> Copy(HttpClient client, Guid mselId)
    {
        var response = await client.PostAsync($"/api/msels/{mselId}/copy", null, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await Read<Msel>(response);
    }

    private async Task<T> Read<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
}
