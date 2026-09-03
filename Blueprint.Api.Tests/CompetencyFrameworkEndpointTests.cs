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
using Blueprint.Api.Tests.Infrastructure;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// The six competency framework endpoints that are not imports: the two reads, create, update,
/// <c>can-delete</c> and delete.
/// </summary>
/// <remarks>
/// <para>
/// A framework is a tree of competencies - NICE, DCWF - that exercise authors draw on when they say what a
/// MSEL trains. It is reference data rather than exercise data: one installation-wide copy of each
/// published framework, no MSEL scoping and no MSEL role anywhere in the area, which is why nothing here
/// looks like the rest of the API's two-stage authorization. The whole surface is gated on one coarse
/// permission, <c>ManageCompetencyFrameworks</c>, and only on the writes.
/// </para>
/// <para>
/// So the reads are open to <em>any</em> account that can sign in - see
/// <see cref="Get_WithNoSystemPermission_Is200"/>, <see cref="GetById_WithNoSystemPermission_Is200"/> and
/// <see cref="CanDelete_WithNoSystemPermission_Is200"/>. That is characterized rather than fixed, and the
/// sharp end of it is that <c>SystemPermission.ViewCompetencyFrameworks</c> exists, is assignable, and is
/// read by <em>nothing</em> in this controller: the only actions that demand it are
/// <c>ProficiencyScaleController</c>'s and <c>ProficiencyLevelController</c>'s reads. An administrator
/// granting it to let somebody browse frameworks has changed nothing, and an administrator withholding it
/// has not stopped them.
/// </para>
/// <para>
/// A competency's related competencies are stored one way round and reported both ways.
/// <c>CompetencyRelationshipEntity</c> is a directed row, but <c>GetAsync</c> reports each competency's
/// <c>RelatedIdNumbers</c> as the union of its outbound and its inbound rows - so a client cannot tell
/// which way a link was written, and does not need to. Relationships are matched by <em>ID number</em>
/// throughout, never by id, which is what makes a framework exported from one installation importable into
/// another; it is also why a competency with no ID number can never be related to anything
/// (<see cref="Create_IgnoresRelatedIdNumbersOnACompetencyWithNoIdNumber"/>).
/// </para>
/// <para>
/// Two things about <c>can-delete</c> and <c>delete</c> disagree with their own documentation. The
/// controller's <c>&lt;remarks&gt;</c> say the check reports "which MSELs, data fields, and teams are
/// using competencies from this framework"; it only looks at <c>MselCompetencies</c>, so a framework whose
/// competencies are assigned to a <em>team</em> reports as deletable and the delete cascades the team's
/// assignments away without warning (<see cref="Delete_WhenACompetencyIsOnlyOnATeam_TakesTheTeamsWithIt"/>).
/// And the delete "will fail with BadRequest if the framework is in use" - it throws a bare
/// <c>ArgumentException</c>, which is not an <c>IApiException</c>, so <c>JsonExceptionFilter</c> answers
/// <em>500</em> (<see cref="Delete_WhenACompetencyIsOnAMsel_Is500_NotBadRequest"/>).
/// </para>
/// <para>
/// The importers and the preview endpoints are covered separately, in
/// <c>CompetencyFrameworkImportTests</c> and <c>CompetencyFrameworkPreviewTests</c>; the nested competency
/// endpoints are in <c>CompetencyEndpointTests</c>. What lives here is the shape the importers all end at,
/// because every one of them finishes by calling the same <c>GetAsync(id)</c> this file pins.
/// </para>
/// </remarks>
public class CompetencyFrameworkEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET api/competencyframeworks
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ReturnsEveryFramework()
    {
        var first = await SeedFramework();
        var second = await SeedFramework();
        var actor = await Manager();

        var frameworks = await List(Client(actor));

        // Asserted as a set: nothing behind this endpoint orders the rows.
        Assert.Contains(first.Id, frameworks.Select(f => f.Id));
        Assert.Contains(second.Id, frameworks.Select(f => f.Id));
    }

    [Fact]
    public async Task Get_WhenThereAreNoFrameworks_IsAnEmptyList()
    {
        var actor = await Manager();

        var frameworks = await List(Client(actor));

        Assert.Empty(frameworks);
    }

    [Fact]
    public async Task Get_DoesNotIncludeCompetencies()
    {
        var framework = await SeedFramework();
        await SeedCompetency(framework, "A");
        await SeedCompetency(framework, "B");
        var actor = await Manager();

        var frameworks = await List(Client(actor));

        Assert.Empty(Assert.Single(frameworks).Competencies);
    }

    [Fact]
    public async Task Get_ReturnsTheFrameworksOwnFields()
    {
        var framework = await SeedFramework(f =>
        {
            f.Name = "NICE Framework";
            f.Source = "NICE";
            f.Version = "2.0";
            f.Taxonomies = "Category,Work Role";
        });
        var actor = await Manager();

        var returned = Assert.Single(await List(Client(actor)));

        Assert.Equal("NICE Framework", returned.Name);
        Assert.Equal(framework.IdNumber, returned.IdNumber);
        Assert.Equal("NICE", returned.Source);
        Assert.Equal("2.0", returned.Version);
        Assert.Equal("Category,Work Role", returned.Taxonomies);
    }

    /// <summary>
    /// Characterization. <c>GET competencyframeworks</c> has no authorization check at all.
    /// </summary>
    /// <remarks>
    /// Turns red when the endpoint starts demanding <c>ViewCompetencyFrameworks</c>, which is the
    /// permission that exists for it and is currently read only by the proficiency scale controllers.
    /// </remarks>
    [Fact]
    public async Task Get_WithNoSystemPermission_Is200()
    {
        await SeedFramework();
        var actor = await Nobody();

        var response = await Client(actor).GetAsync("api/competencyframeworks", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(await Read<List<CompetencyFramework>>(response));
    }

    // ---------------------------------------------------------------------------------------------
    // GET api/competencyframeworks/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetById_ReturnsTheFrameworkAndItsCompetencies()
    {
        var framework = await SeedFramework();
        await SeedCompetency(framework, "A");
        await SeedCompetency(framework, "B");
        var actor = await Manager();

        var returned = await Single(Client(actor), framework.Id);

        Assert.Equal(framework.Id, returned.Id);
        Assert.Equal(["A", "B"], returned.Competencies.Select(c => c.IdNumber).Order());
    }

    [Fact]
    public async Task GetById_OfAnUnknownFramework_Is404()
    {
        var actor = await Manager();

        var response = await Client(actor).GetAsync($"api/competencyframeworks/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetById_DoesNotReturnAnotherFrameworksCompetencies()
    {
        var framework = await SeedFramework();
        await SeedCompetency(framework, "MINE");
        var other = await SeedFramework();
        await SeedCompetency(other, "THEIRS");
        var actor = await Manager();

        var returned = await Single(Client(actor), framework.Id);

        Assert.Equal("MINE", Assert.Single(returned.Competencies).IdNumber);
    }

    [Fact]
    public async Task GetById_ForAFrameworkWithNoCompetencies_ReturnsAnEmptyCollection()
    {
        var framework = await SeedFramework();
        var actor = await Manager();

        var returned = await Single(Client(actor), framework.Id);

        Assert.Empty(returned.Competencies);
    }

    [Fact]
    public async Task GetById_ReturnsEachCompetencysParentAndPath()
    {
        var framework = await SeedFramework();
        var parent = await SeedCompetency(framework, "PARENT");
        var child = await SeedCompetency(framework, "CHILD", c =>
        {
            c.ParentId = parent.Id;
            c.Path = $"/{parent.Id}/{c.Id}";
        });
        var actor = await Manager();

        var returned = await Single(Client(actor), framework.Id);
        var returnedChild = returned.Competencies.Single(c => c.IdNumber == "CHILD");

        Assert.Equal(parent.Id, returnedChild.ParentId);
        Assert.Equal($"/{parent.Id}/{child.Id}", returnedChild.Path);
    }

    /// <summary>
    /// Characterization. A competency's <c>children</c> collection comes back empty even when the framework
    /// plainly has a hierarchy, so a client has to rebuild the tree from <c>parentId</c> or <c>path</c>.
    /// </summary>
    /// <remarks>
    /// The query includes <c>Competencies</c> and their <c>Relationships</c> but not <c>Children</c>, and
    /// it is <c>AsNoTracking</c> without identity resolution, so no navigation fix-up can populate it
    /// either. The property is on the view model regardless, which is the trap: it is not absent, it is
    /// empty. Turns red if the include is added or the query switches to identity resolution.
    /// </remarks>
    [Fact]
    public async Task GetById_DoesNotPopulateChildren()
    {
        var framework = await SeedFramework();
        var parent = await SeedCompetency(framework, "PARENT");
        await SeedCompetency(framework, "CHILD", c => c.ParentId = parent.Id);
        var actor = await Manager();

        var returned = await Single(Client(actor), framework.Id);

        Assert.All(returned.Competencies, c => Assert.Empty(c.Children));
    }

    [Fact]
    public async Task GetById_ReportsAnOutboundRelationshipOnBothCompetencies()
    {
        var framework = await SeedFramework();
        var from = await SeedCompetency(framework, "FROM");
        var to = await SeedCompetency(framework, "TO");
        await SeedRelationship(from, to);
        var actor = await Manager();

        var returned = await Single(Client(actor), framework.Id);

        Assert.Equal(["TO"], Competency(returned, "FROM").RelatedIdNumbers);
        Assert.Equal(["FROM"], Competency(returned, "TO").RelatedIdNumbers);
    }

    [Fact]
    public async Task GetById_WhenBothDirectionsAreStored_ReportsTheRelatedIdNumberOnce()
    {
        var framework = await SeedFramework();
        var first = await SeedCompetency(framework, "FIRST");
        var second = await SeedCompetency(framework, "SECOND");
        await SeedRelationship(first, second);
        await SeedRelationship(second, first);
        var actor = await Manager();

        var returned = await Single(Client(actor), framework.Id);

        Assert.Equal(["SECOND"], Competency(returned, "FIRST").RelatedIdNumbers);
        Assert.Equal(["FIRST"], Competency(returned, "SECOND").RelatedIdNumbers);
    }

    [Fact]
    public async Task GetById_WhenARelatedCompetencyHasNoIdNumber_OmitsItFromTheRelatedList()
    {
        var framework = await SeedFramework();
        var named = await SeedCompetency(framework, "NAMED");
        var anonymous = await SeedCompetency(framework, null);
        await SeedRelationship(named, anonymous);
        var actor = await Manager();

        var returned = await Single(Client(actor), framework.Id);

        Assert.Empty(Competency(returned, "NAMED").RelatedIdNumbers);
    }

    [Fact]
    public async Task GetById_ReportsEveryRelatedCompetency()
    {
        var framework = await SeedFramework();
        var role = await SeedCompetency(framework, "ROLE");
        var task = await SeedCompetency(framework, "T-1");
        var knowledge = await SeedCompetency(framework, "K-1");
        await SeedRelationship(role, task);
        await SeedRelationship(role, knowledge);
        var actor = await Manager();

        var returned = await Single(Client(actor), framework.Id);

        Assert.Equal(["K-1", "T-1"], Competency(returned, "ROLE").RelatedIdNumbers.Order());
    }

    /// <summary>
    /// Characterization: <c>GET competencyframeworks/{id}</c> has no authorization check either, and this
    /// one returns the whole framework rather than a summary.
    /// </summary>
    [Fact]
    public async Task GetById_WithNoSystemPermission_Is200()
    {
        var framework = await SeedFramework();
        await SeedCompetency(framework, "A");
        var actor = await Nobody();

        var response = await Client(actor).GetAsync($"api/competencyframeworks/{framework.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single((await Read<CompetencyFramework>(response)).Competencies);
    }

    // ---------------------------------------------------------------------------------------------
    // POST api/competencyframeworks
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_Is201_WithALocationHeaderPointingAtTheFramework()
    {
        var actor = await Manager();

        var response = await Create(Client(actor), new CompetencyFramework
        {
            Name = "Hand built",
            IdNumber = "HAND-1"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await Read<CompetencyFramework>(response);
        Assert.EndsWith($"/api/competencyframeworks/{created.Id}", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Create_StoresTheFramework()
    {
        var actor = await Manager();

        var response = await Create(Client(actor), new CompetencyFramework
        {
            Name = "Hand built",
            IdNumber = "HAND-1",
            Description = "Typed in by an author",
            Source = "LOCAL",
            Version = "0.1",
            Taxonomies = "Category,Task"
        });

        var created = await Read<CompetencyFramework>(response);
        await using var db = NewContext();
        var stored = await db.CompetencyFrameworks.SingleAsync(f => f.Id == created.Id, Ct);
        Assert.Equal("Hand built", stored.Name);
        Assert.Equal("HAND-1", stored.IdNumber);
        Assert.Equal("Typed in by an author", stored.Description);
        Assert.Equal("LOCAL", stored.Source);
        Assert.Equal("0.1", stored.Version);
        Assert.Equal("Category,Task", stored.Taxonomies);
    }

    [Fact]
    public async Task Create_StoresTheCallerAsTheCreator()
    {
        var actor = await Manager();

        var response = await Create(Client(actor), new CompetencyFramework { Name = "n", IdNumber = "N-1" });

        Assert.Equal(actor.Id, (await Read<CompetencyFramework>(response)).CreatedBy);
    }

    [Fact]
    public async Task Create_StampsTheAuditFieldsOnTheServer()
    {
        var actor = await Manager();
        var before = DateTime.UtcNow;

        var response = await Create(Client(actor), new CompetencyFramework
        {
            Name = "n",
            IdNumber = "N-1",
            // Hostile: none of these may reach the row.
            DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            DateModified = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedBy = Guid.NewGuid(),
            ModifiedBy = Guid.NewGuid()
        });

        var created = await Read<CompetencyFramework>(response);
        AssertStampedBetween(created.DateCreated, before, DateTime.UtcNow);
        Assert.Null(created.DateModified);
        Assert.Null(created.ModifiedBy);
        Assert.Equal(actor.Id, created.CreatedBy);
    }

    [Theory]
    [InlineData(SystemPermission.ViewCompetencyFrameworks)]
    [InlineData(SystemPermission.ManageMsels)]
    [InlineData(SystemPermission.ManageCatalogs)]
    public async Task Create_WithoutManageCompetencyFrameworks_Is403(SystemPermission permission)
    {
        var actor = await Actor().WithSystemPermissions(permission).SeedAsync();

        var response = await Create(Client(actor), new CompetencyFramework { Name = "n", IdNumber = "N-1" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_TrimsTheIdNumber()
    {
        var actor = await Manager();

        var response = await Create(Client(actor), new CompetencyFramework { Name = "n", IdNumber = "  N-1  " });

        Assert.Equal("N-1", (await Read<CompetencyFramework>(response)).IdNumber);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_WithABlankIdNumber_StoresNull(string idNumber)
    {
        var actor = await Manager();

        var response = await Create(Client(actor), new CompetencyFramework { Name = "n", IdNumber = idNumber });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null((await Read<CompetencyFramework>(response)).IdNumber);
    }

    [Fact]
    public async Task Create_WithAnIdNumberAlreadyInUse_Is409_NamingTheFrameworkThatHasIt()
    {
        await SeedFramework(f =>
        {
            f.IdNumber = "TAKEN";
            f.Name = "The first one";
            f.Version = "3.1";
        });
        var actor = await Manager();

        var response = await Create(Client(actor), new CompetencyFramework { Name = "n", IdNumber = "TAKEN" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await ReadError(response);
        Assert.Contains("'TAKEN'", error.Title);
        Assert.Contains("'The first one' (version 3.1)", error.Title);
    }

    /// <summary>
    /// A blank ID number is stored as null, and Postgres treats nulls in a unique index as distinct, so any
    /// number of frameworks may have none.
    /// </summary>
    [Fact]
    public async Task Create_WhenTwoFrameworksHaveNoIdNumber_AllowsBoth()
    {
        var actor = await Manager();

        var first = await Create(Client(actor), new CompetencyFramework { Name = "first" });
        var second = await Create(Client(actor), new CompetencyFramework { Name = "second" });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
    }

    /// <summary>
    /// Characterization. The importers refuse a source and version that has already been imported;
    /// <c>POST competencyframeworks</c> does not check, so the same framework can be added twice by hand.
    /// </summary>
    /// <remarks>
    /// Only the ID number is unique, and it is caller-supplied here rather than derived from source and
    /// version as the importers derive it. Turns red if <c>CreateAsync</c> starts calling
    /// <c>EnsureSourceAndVersionAvailableAsync</c> as the importers do.
    /// </remarks>
    [Fact]
    public async Task Create_WithTheSameSourceAndVersionAsAnExistingFramework_Is201()
    {
        await SeedFramework(f =>
        {
            f.Source = "NICE";
            f.Version = "1.0";
        });
        var actor = await Manager();

        var response = await Create(Client(actor), new CompetencyFramework
        {
            Name = "n",
            IdNumber = "SOMETHING-ELSE",
            Source = "NICE",
            Version = "1.0"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Create_StoresTheCompetenciesInThePayload()
    {
        var actor = await Manager();

        var response = await Create(Client(actor), Framework(
            Vm("A", "Alpha"),
            Vm("B", "Bravo")));

        var created = await Read<CompetencyFramework>(response);
        await using var db = NewContext();
        var stored = await db.Competencies.Where(c => c.CompetencyFrameworkId == created.Id).ToListAsync(Ct);
        Assert.Equal(["A", "B"], stored.Select(c => c.IdNumber).Order());
        Assert.Equal("Alpha", stored.Single(c => c.IdNumber == "A").ShortName);
    }

    [Fact]
    public async Task Create_WithNoCompetencies_Is201()
    {
        var actor = await Manager();

        var response = await Create(Client(actor), Framework());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Empty((await Read<CompetencyFramework>(response)).Competencies);
    }

    /// <summary>
    /// Every competency gets a fresh id, so an export can be re-imported while the original still exists.
    /// </summary>
    [Fact]
    public async Task Create_GivesEveryCompetencyAFreshId()
    {
        var actor = await Manager();
        var payloadId = Guid.NewGuid();

        var response = await Create(Client(actor), Framework(Vm("A", id: payloadId)));

        var created = await Read<CompetencyFramework>(response);
        Assert.NotEqual(payloadId, Assert.Single(created.Competencies).Id);
    }

    [Fact]
    public async Task Create_RemapsParentReferencesOntoTheNewIds()
    {
        var actor = await Manager();
        var parentId = Guid.NewGuid();

        var response = await Create(Client(actor), Framework(
            Vm("PARENT", id: parentId),
            Vm("CHILD", parentId: parentId)));

        var created = await Read<CompetencyFramework>(response);
        var parent = Competency(created, "PARENT");
        var child = Competency(created, "CHILD");
        Assert.Equal(parent.Id, child.ParentId);
        Assert.NotEqual(parentId, parent.Id);
    }

    [Fact]
    public async Task Create_BuildsThePathFromTheNewIds()
    {
        var actor = await Manager();
        var parentId = Guid.NewGuid();

        var response = await Create(Client(actor), Framework(
            Vm("PARENT", id: parentId),
            Vm("CHILD", parentId: parentId)));

        var created = await Read<CompetencyFramework>(response);
        var parent = Competency(created, "PARENT");
        var child = Competency(created, "CHILD");
        Assert.Equal($"/{parent.Id}", parent.Path);
        Assert.Equal($"/{parent.Id}/{child.Id}", child.Path);
    }

    [Fact]
    public async Task Create_WhenAParentIsNotInThePayload_DropsTheParentReference()
    {
        var actor = await Manager();

        var response = await Create(Client(actor), Framework(Vm("ORPHAN", parentId: Guid.NewGuid())));

        var created = await Read<CompetencyFramework>(response);
        var orphan = Assert.Single(created.Competencies);
        Assert.Null(orphan.ParentId);
        Assert.Equal($"/{orphan.Id}", orphan.Path);
    }

    /// <summary>
    /// An uploaded file can name a parent that names it back. <c>BuildPath</c> stops at the first repeat
    /// rather than walking forever, so the framework still saves.
    /// </summary>
    [Fact]
    public async Task Create_WithAParentCycle_StopsWalkingAndStillSaves()
    {
        var actor = await Manager();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();

        var response = await Create(Client(actor), Framework(
            Vm("FIRST", id: firstId, parentId: secondId),
            Vm("SECOND", id: secondId, parentId: firstId)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await Read<CompetencyFramework>(response);
        var first = Competency(created, "FIRST");
        var second = Competency(created, "SECOND");
        Assert.Equal($"/{second.Id}/{first.Id}", first.Path);
        Assert.Equal($"/{first.Id}/{second.Id}", second.Path);
    }

    /// <summary>
    /// Characterization. A second competency with an ID number already used in the same payload is dropped
    /// silently - no error, no mention in the response, and the framework is created without it.
    /// </summary>
    /// <remarks>
    /// The unique index would have refused the insert, so something has to give; skipping matches what the
    /// three importers do with a duplicate row. Turns red if the payload is rejected with a 409 instead,
    /// which is what a hand-authored framework arguably deserves.
    /// </remarks>
    [Fact]
    public async Task Create_SkipsASecondCompetencyWithTheSameIdNumber()
    {
        var actor = await Manager();

        var response = await Create(Client(actor), Framework(
            Vm("DUP", "The one that is kept"),
            Vm("DUP", "The one that is dropped")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await Read<CompetencyFramework>(response);
        Assert.Equal("The one that is kept", Assert.Single(created.Competencies).ShortName);
    }

    /// <summary>
    /// Blank ID numbers become null and nulls do not collide, so any number of competencies may have none.
    /// </summary>
    [Fact]
    public async Task Create_AllowsSeveralCompetenciesWithNoIdNumber()
    {
        var actor = await Manager();

        var response = await Create(Client(actor), Framework(
            Vm(null, "first"),
            Vm("   ", "second")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await Read<CompetencyFramework>(response);
        Assert.Equal(2, created.Competencies.Count);
        Assert.All(created.Competencies, c => Assert.Null(c.IdNumber));
    }

    [Fact]
    public async Task Create_ResolvesRelatedIdNumbersIntoRelationships()
    {
        var actor = await Manager();

        var response = await Create(Client(actor), Framework(
            Vm("ROLE", related: ["T-1"]),
            Vm("T-1")));

        var created = await Read<CompetencyFramework>(response);
        Assert.Equal(["T-1"], Competency(created, "ROLE").RelatedIdNumbers);
        Assert.Equal(["ROLE"], Competency(created, "T-1").RelatedIdNumbers);
        await using var db = NewContext();
        var stored = Assert.Single(await db.CompetencyRelationships.ToListAsync(Ct));
        Assert.Equal(Competency(created, "ROLE").Id, stored.CompetencyId);
        Assert.Equal(Competency(created, "T-1").Id, stored.RelatedCompetencyId);
    }

    [Fact]
    public async Task Create_WhenTheSameRelatedIdNumberIsListedTwice_StoresOneRelationship()
    {
        var actor = await Manager();

        var response = await Create(Client(actor), Framework(
            Vm("ROLE", related: ["T-1", "T-1"]),
            Vm("T-1")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var db = NewContext();
        Assert.Single(await db.CompetencyRelationships.ToListAsync(Ct));
    }

    [Fact]
    public async Task Create_WhenTwoCompetenciesNameEachOther_StoresBothRowsAndReportsOneLink()
    {
        var actor = await Manager();

        var response = await Create(Client(actor), Framework(
            Vm("FIRST", related: ["SECOND"]),
            Vm("SECOND", related: ["FIRST"])));

        var created = await Read<CompetencyFramework>(response);
        await using var db = NewContext();
        Assert.Equal(2, await db.CompetencyRelationships.CountAsync(Ct));
        Assert.Equal(["SECOND"], Competency(created, "FIRST").RelatedIdNumbers);
        Assert.Equal(["FIRST"], Competency(created, "SECOND").RelatedIdNumbers);
    }

    [Fact]
    public async Task Create_IgnoresARelatedIdNumberThatIsNotInThePayload()
    {
        var actor = await Manager();

        var response = await Create(Client(actor), Framework(Vm("ROLE", related: ["NOT-HERE"])));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Empty(Assert.Single((await Read<CompetencyFramework>(response)).Competencies).RelatedIdNumbers);
    }

    /// <summary>
    /// Characterization. Relationships are resolved by ID number in both directions, so a competency with
    /// no ID number cannot be related to anything however its <c>relatedIdNumbers</c> read.
    /// </summary>
    /// <remarks>
    /// Turns red if the resolver falls back to the payload's competency ids for the source side. Note the
    /// asymmetry it produces today: naming a blank-ID-number competency as a <em>target</em> is also a
    /// no-op, so the link simply does not exist in either direction.
    /// </remarks>
    [Fact]
    public async Task Create_IgnoresRelatedIdNumbersOnACompetencyWithNoIdNumber()
    {
        var actor = await Manager();

        var response = await Create(Client(actor), Framework(
            Vm(null, "no id number", related: ["T-1"]),
            Vm("T-1")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var db = NewContext();
        Assert.Empty(await db.CompetencyRelationships.ToListAsync(Ct));
    }

    [Fact]
    public async Task Create_IgnoresABlankRelatedIdNumber()
    {
        var actor = await Manager();

        var response = await Create(Client(actor), Framework(Vm("ROLE", related: ["", "   "])));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var db = NewContext();
        Assert.Empty(await db.CompetencyRelationships.ToListAsync(Ct));
    }

    [Fact]
    public async Task Create_StoresTheCallerAsTheCreatorOfEveryCompetency()
    {
        var actor = await Manager();

        var response = await Create(Client(actor), Framework(Vm("A", related: ["B"]), Vm("B")));

        var created = await Read<CompetencyFramework>(response);
        Assert.All(created.Competencies, c => Assert.Equal(actor.Id, c.CreatedBy));
        await using var db = NewContext();
        Assert.All(await db.CompetencyRelationships.ToListAsync(Ct), r => Assert.Equal(actor.Id, r.CreatedBy));
    }

    // ---------------------------------------------------------------------------------------------
    // PUT api/competencyframeworks/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_StoresTheNameDescriptionSourceAndVersion()
    {
        var framework = await SeedFramework();
        var actor = await Manager();

        var response = await Update(Client(actor), framework.Id, new CompetencyFramework
        {
            Name = "Renamed",
            Description = "Rewritten",
            Source = "DCWF",
            Version = "2.5"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = NewContext();
        var stored = await db.CompetencyFrameworks.SingleAsync(f => f.Id == framework.Id, Ct);
        Assert.Equal("Renamed", stored.Name);
        Assert.Equal("Rewritten", stored.Description);
        Assert.Equal("DCWF", stored.Source);
        Assert.Equal("2.5", stored.Version);
    }

    [Fact]
    public async Task Update_OfAnUnknownFramework_Is404()
    {
        var actor = await Manager();

        var response = await Update(Client(actor), Guid.NewGuid(), new CompetencyFramework { Name = "n" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(SystemPermission.ViewCompetencyFrameworks)]
    [InlineData(SystemPermission.ManageMsels)]
    public async Task Update_WithoutManageCompetencyFrameworks_Is403(SystemPermission permission)
    {
        var framework = await SeedFramework();
        var actor = await Actor().WithSystemPermissions(permission).SeedAsync();

        var response = await Update(Client(actor), framework.Id, new CompetencyFramework { Name = "Renamed" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var db = NewContext();
        Assert.Equal(framework.Name, (await db.CompetencyFrameworks.SingleAsync(f => f.Id == framework.Id, Ct)).Name);
    }

    /// <summary>
    /// Characterization. <c>PUT competencyframeworks/{id}</c> ignores the body's <c>idNumber</c>, so the one
    /// field the whole area's uniqueness and cross-installation matching depend on cannot be corrected
    /// after an import got it wrong.
    /// </summary>
    /// <remarks>
    /// The update writes five fields and the ID number is not among them; the response then re-reads the
    /// row, so the caller is told their new value was not kept. Turns red once the field is assignable -
    /// which will also need the 409 check <c>CreateAsync</c> already has.
    /// </remarks>
    [Fact]
    public async Task Update_IgnoresTheIdNumberInTheBody()
    {
        var framework = await SeedFramework(f => f.IdNumber = "ORIGINAL");
        var actor = await Manager();

        var response = await Update(Client(actor), framework.Id, new CompetencyFramework
        {
            Name = framework.Name,
            IdNumber = "CORRECTED"
        });

        Assert.Equal("ORIGINAL", (await Read<CompetencyFramework>(response)).IdNumber);
    }

    /// <summary>
    /// Characterization. The scale, taxonomy and description-format fields are also ignored, silently.
    /// </summary>
    /// <remarks>
    /// These are all set by the importers and never editable afterwards. Nothing tells the caller: the
    /// request is a 200 carrying the unchanged values.
    /// </remarks>
    [Fact]
    public async Task Update_IgnoresTheScaleTaxonomyAndFormatFields()
    {
        var framework = await SeedFramework(f =>
        {
            f.ScaleValues = "original scale";
            f.ScaleConfiguration = "original config";
            f.Taxonomies = "original taxonomy";
            f.DescriptionFormat = 1;
        });
        var actor = await Manager();

        var response = await Update(Client(actor), framework.Id, new CompetencyFramework
        {
            Name = framework.Name,
            ScaleValues = "new scale",
            ScaleConfiguration = "new config",
            Taxonomies = "new taxonomy",
            DescriptionFormat = 2
        });

        var updated = await Read<CompetencyFramework>(response);
        Assert.Equal("original scale", updated.ScaleValues);
        Assert.Equal("original config", updated.ScaleConfiguration);
        Assert.Equal("original taxonomy", updated.Taxonomies);
        Assert.Equal(1, updated.DescriptionFormat);
    }

    /// <summary>
    /// Characterization. The body's <c>competencies</c> are ignored, so this endpoint cannot add, remove or
    /// edit one - that is what the competency endpoints are for.
    /// </summary>
    [Fact]
    public async Task Update_IgnoresTheCompetenciesInTheBody()
    {
        var framework = await SeedFramework();
        await SeedCompetency(framework, "EXISTING");
        var actor = await Manager();

        var response = await Update(Client(actor), framework.Id, new CompetencyFramework
        {
            Name = framework.Name,
            Competencies = [Vm("SMUGGLED-IN")]
        });

        var updated = await Read<CompetencyFramework>(response);
        Assert.Equal("EXISTING", Assert.Single(updated.Competencies).IdNumber);
    }

    [Fact]
    public async Task Update_ReturnsTheFrameworkWithItsCompetenciesAndRelationships()
    {
        var framework = await SeedFramework();
        var from = await SeedCompetency(framework, "FROM");
        var to = await SeedCompetency(framework, "TO");
        await SeedRelationship(from, to);
        var actor = await Manager();

        var response = await Update(Client(actor), framework.Id, new CompetencyFramework { Name = "Renamed" });

        var updated = await Read<CompetencyFramework>(response);
        Assert.Equal("Renamed", updated.Name);
        Assert.Equal(["TO"], Competency(updated, "FROM").RelatedIdNumbers);
    }

    [Fact]
    public async Task Update_StampsTheAuditFieldsAndPreservesCreation()
    {
        var creator = Guid.NewGuid();
        var framework = await SeedFramework(f => f.CreatedBy = creator);

        // Not the value the arrangement asked for: SaveEntries stamps DateCreated on insert, so the seed's
        // own date is the one the update has to preserve.
        var created = framework.DateCreated;

        var actor = await Manager();
        var before = DateTime.UtcNow;

        var response = await Update(Client(actor), framework.Id, new CompetencyFramework
        {
            Name = "Renamed",
            // Hostile: the audit fields are the server's, not the caller's.
            CreatedBy = Guid.NewGuid(),
            DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ModifiedBy = Guid.NewGuid()
        });

        var updated = await Read<CompetencyFramework>(response);
        Assert.Equal(creator, updated.CreatedBy);
        Assert.Equal(created, updated.DateCreated, TimeSpan.FromMilliseconds(1));
        Assert.Equal(actor.Id, updated.ModifiedBy);
        AssertStampedBetween(updated.DateModified, before, DateTime.UtcNow);
    }

    /// <summary>
    /// Characterization. <c>defaultProficiencyScaleId</c> is written straight through with no existence
    /// check, so an id that names no scale is a 500 from the foreign key rather than a 400.
    /// </summary>
    /// <remarks>
    /// This is the one field on the update that can fail, and <c>UpdateAsync</c> has no <c>try</c> around
    /// its save - unlike the create and the competency writes, which translate a
    /// <c>DbUpdateException</c>. Turns red once the id is validated, or once the save is wrapped.
    /// </remarks>
    [Fact]
    public async Task Update_WithAProficiencyScaleThatDoesNotExist_Is500()
    {
        var framework = await SeedFramework();
        var actor = await Manager();

        var response = await Update(Client(actor), framework.Id, new CompetencyFramework
        {
            Name = framework.Name,
            DefaultProficiencyScaleId = Guid.NewGuid()
        });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // GET api/competencyframeworks/{id}/can-delete
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CanDelete_ForAFrameworkWithNoCompetencies_IsTrue()
    {
        var framework = await SeedFramework();
        var actor = await Manager();

        var check = await CanDelete(Client(actor), framework.Id);

        Assert.True(check.CanDelete);
        Assert.Empty(check.AffectedMsels);
    }

    [Fact]
    public async Task CanDelete_ForAFrameworkWhoseCompetenciesAreUnused_IsTrue()
    {
        var framework = await SeedFramework();
        await SeedCompetency(framework, "A");
        var actor = await Manager();

        var check = await CanDelete(Client(actor), framework.Id);

        Assert.True(check.CanDelete);
        Assert.Empty(check.AffectedMsels);
    }

    [Fact]
    public async Task CanDelete_WhenACompetencyIsOnAMsel_IsFalse_AndNamesTheMsel()
    {
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "A");
        var msel = await SeedMselUsing(competency);
        var actor = await Manager();

        var check = await CanDelete(Client(actor), framework.Id);

        Assert.False(check.CanDelete);
        var affected = Assert.Single(check.AffectedMsels);
        Assert.Equal(msel.Id, affected.Id);
        Assert.Equal(msel.Name, affected.Name);
    }

    [Fact]
    public async Task CanDelete_WhenTwoCompetenciesAreOnOneMsel_NamesItOnce()
    {
        var framework = await SeedFramework();
        var first = await SeedCompetency(framework, "A");
        var second = await SeedCompetency(framework, "B");
        var msel = await SeedMselUsing(first);
        await Seed(new MselCompetencyEntity(msel.Id, second.Id));
        var actor = await Manager();

        var check = await CanDelete(Client(actor), framework.Id);

        Assert.False(check.CanDelete);
        Assert.Single(check.AffectedMsels);
    }

    [Fact]
    public async Task CanDelete_IgnoresAnotherFrameworksUse()
    {
        var framework = await SeedFramework();
        await SeedCompetency(framework, "MINE");
        var other = await SeedFramework();
        var theirs = await SeedCompetency(other, "THEIRS");
        await SeedMselUsing(theirs);
        var actor = await Manager();

        var check = await CanDelete(Client(actor), framework.Id);

        Assert.True(check.CanDelete);
    }

    [Fact]
    public async Task CanDelete_OfAnUnknownFramework_Is404()
    {
        var actor = await Manager();

        var response = await Client(actor).GetAsync($"api/competencyframeworks/{Guid.NewGuid()}/can-delete", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Characterization: <c>can-delete</c> has no authorization check, so any signed-in account can
    /// enumerate which MSELs draw on a framework - MSEL names included.
    /// </summary>
    [Fact]
    public async Task CanDelete_WithNoSystemPermission_Is200()
    {
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "A");
        var msel = await SeedMselUsing(competency);
        var actor = await Nobody();

        var response = await Client(actor).GetAsync($"api/competencyframeworks/{framework.Id}/can-delete", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(msel.Name, Assert.Single((await Read<FrameworkDeleteCheck>(response)).AffectedMsels).Name);
    }

    /// <summary>
    /// Characterization. The check only looks at <c>MselCompetencies</c>, so a framework whose competencies
    /// are assigned to a <em>team</em> reports as deletable.
    /// </summary>
    /// <remarks>
    /// The controller's own <c>&lt;remarks&gt;</c> promise "which MSELs, data fields, and teams are using
    /// competencies from this framework". <c>TeamCompetencyEntity</c> is the second and only other thing
    /// that references a competency; there is no data-field reference at all, so that third of the sentence
    /// describes nothing. Turns red once team assignments are counted -
    /// see <see cref="Delete_WhenACompetencyIsOnlyOnATeam_TakesTheTeamsWithIt"/> for what it costs today.
    /// </remarks>
    [Fact]
    public async Task CanDelete_WhenACompetencyIsOnlyOnATeam_IsTrue()
    {
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "A");
        await SeedTeamUsing(competency);
        var actor = await Manager();

        var check = await CanDelete(Client(actor), framework.Id);

        Assert.True(check.CanDelete);
        Assert.Empty(check.AffectedMsels);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE api/competencyframeworks/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_Is204_AndRemovesTheFramework()
    {
        var framework = await SeedFramework();
        var actor = await Manager();

        var response = await Client(actor).DeleteAsync($"api/competencyframeworks/{framework.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var db = NewContext();
        Assert.False(await db.CompetencyFrameworks.AnyAsync(f => f.Id == framework.Id, Ct));
    }

    [Fact]
    public async Task Delete_CascadesToCompetenciesAndRelationships()
    {
        var framework = await SeedFramework();
        var from = await SeedCompetency(framework, "FROM");
        var to = await SeedCompetency(framework, "TO");
        await SeedRelationship(from, to);
        var actor = await Manager();

        var response = await Client(actor).DeleteAsync($"api/competencyframeworks/{framework.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var db = NewContext();
        Assert.Empty(await db.Competencies.ToListAsync(Ct));
        Assert.Empty(await db.CompetencyRelationships.ToListAsync(Ct));
    }

    [Fact]
    public async Task Delete_LeavesAnotherFrameworkAlone()
    {
        var framework = await SeedFramework();
        await SeedCompetency(framework, "MINE");
        var other = await SeedFramework();
        await SeedCompetency(other, "THEIRS");
        var actor = await Manager();

        await Client(actor).DeleteAsync($"api/competencyframeworks/{framework.Id}", Ct);

        await using var db = NewContext();
        Assert.Equal("THEIRS", (await db.Competencies.SingleAsync(Ct)).IdNumber);
    }

    [Fact]
    public async Task Delete_OfAnUnknownFramework_Is404()
    {
        var actor = await Manager();

        var response = await Client(actor).DeleteAsync($"api/competencyframeworks/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(SystemPermission.ViewCompetencyFrameworks)]
    [InlineData(SystemPermission.ManageMsels)]
    public async Task Delete_WithoutManageCompetencyFrameworks_Is403(SystemPermission permission)
    {
        var framework = await SeedFramework();
        var actor = await Actor().WithSystemPermissions(permission).SeedAsync();

        var response = await Client(actor).DeleteAsync($"api/competencyframeworks/{framework.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var db = NewContext();
        Assert.True(await db.CompetencyFrameworks.AnyAsync(f => f.Id == framework.Id, Ct));
    }

    /// <summary>
    /// Characterization. Refusing to delete a framework in use is right; answering 500 is not.
    /// </summary>
    /// <remarks>
    /// The controller documents "will fail with BadRequest if the framework is in use", but the service
    /// throws a plain <c>ArgumentException</c>, which is not an <c>IApiException</c>, so
    /// <c>JsonExceptionFilter</c> maps it to 500. The message does reach the caller - as
    /// <c>Detail</c> in Production and as <c>Title</c> in Development, which is the environment this
    /// harness runs the host in. Turns red once the service throws something that carries 400.
    /// </remarks>
    [Fact]
    public async Task Delete_WhenACompetencyIsOnAMsel_Is500_NotBadRequest()
    {
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "A");
        await SeedMselUsing(competency);
        var actor = await Manager();

        var response = await Client(actor).DeleteAsync($"api/competencyframeworks/{framework.Id}", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("Cannot delete framework", (await ReadError(response)).Title);
    }

    [Fact]
    public async Task Delete_WhenACompetencyIsOnAMsel_LeavesTheFrameworkInPlace()
    {
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "A");
        await SeedMselUsing(competency);
        var actor = await Manager();

        await Client(actor).DeleteAsync($"api/competencyframeworks/{framework.Id}", Ct);

        await using var db = NewContext();
        Assert.True(await db.CompetencyFrameworks.AnyAsync(f => f.Id == framework.Id, Ct));
        Assert.True(await db.Competencies.AnyAsync(c => c.Id == competency.Id, Ct));
    }

    /// <summary>
    /// Characterization, and the cost of <see cref="CanDelete_WhenACompetencyIsOnlyOnATeam_IsTrue"/>: the
    /// delete goes through and takes the team's competency assignments with it, because
    /// <c>TeamCompetency</c>'s foreign key cascades.
    /// </summary>
    /// <remarks>
    /// Nothing warns and nothing records what was removed - the team's competency list is simply shorter
    /// afterwards. Turns red once team use blocks the delete, which is the fix.
    /// </remarks>
    [Fact]
    public async Task Delete_WhenACompetencyIsOnlyOnATeam_TakesTheTeamsWithIt()
    {
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "A");
        var team = await SeedTeamUsing(competency);
        var actor = await Manager();

        var response = await Client(actor).DeleteAsync($"api/competencyframeworks/{framework.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var db = NewContext();
        Assert.Empty(await db.TeamCompetencies.Where(tc => tc.TeamId == team.Id).ToListAsync(Ct));
        Assert.True(await db.Teams.AnyAsync(t => t.Id == team.Id, Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // Authentication
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Every route in this file behind the inherited <c>[Authorize]</c> on <c>BaseController</c>. The ids
    /// name nothing, deliberately: a 401 has to arrive before the framework is looked up.
    /// </summary>
    [Theory]
    [InlineData("GET", "api/competencyframeworks")]
    [InlineData("GET", "api/competencyframeworks/6f1f0d9e-0000-4000-8000-000000000001")]
    [InlineData("POST", "api/competencyframeworks")]
    [InlineData("PUT", "api/competencyframeworks/6f1f0d9e-0000-4000-8000-000000000001")]
    [InlineData("GET", "api/competencyframeworks/6f1f0d9e-0000-4000-8000-000000000001/can-delete")]
    [InlineData("DELETE", "api/competencyframeworks/6f1f0d9e-0000-4000-8000-000000000001")]
    public async Task EveryRoute_Anonymously_Is401(string method, string route)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), route);

        var response = await AnonymousClient.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// An actor holding <c>ManageCompetencyFrameworks</c> and nothing else, which is everything this
    /// controller ever asks for.
    /// </summary>
    private Task<TestActor> Manager() =>
        Actor().WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();

    /// <summary>
    /// An actor holding no system permission at all, for the reads that turn out not to need one.
    /// </summary>
    private Task<TestActor> Nobody() => Actor().SeedAsync();

    private async Task<CompetencyFrameworkEntity> SeedFramework(Action<CompetencyFrameworkEntity> arrange = null)
    {
        var framework = BlueprintAppFactory.CompetencyFramework();
        arrange?.Invoke(framework);
        await Seed(framework);

        return framework;
    }

    private async Task<CompetencyEntity> SeedCompetency(
        CompetencyFrameworkEntity framework, string idNumber, Action<CompetencyEntity> arrange = null)
    {
        var competency = BlueprintAppFactory.Competency(framework.Id, idNumber);

        // BlueprintAppFactory.Competency defaults a missing ID number to a fresh one; here a null means
        // null, because a competency without an ID number is a case this area gets wrong in several ways.
        competency.IdNumber = idNumber;
        arrange?.Invoke(competency);
        await Seed(competency);

        return competency;
    }

    private async Task SeedRelationship(CompetencyEntity from, CompetencyEntity to) =>
        await Seed(new CompetencyRelationshipEntity
        {
            CompetencyId = from.Id,
            RelatedCompetencyId = to.Id,
            CreatedBy = Guid.NewGuid()
        });

    /// <summary>A MSEL with <paramref name="competency"/> in its pool, which is what blocks a delete.</summary>
    private async Task<MselEntity> SeedMselUsing(CompetencyEntity competency)
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        await Seed(new MselCompetencyEntity(msel.Id, competency.Id));

        return msel;
    }

    /// <summary>
    /// A team with <paramref name="competency"/> assigned, which is the reference <c>can-delete</c> does
    /// not look for. A team needs a MSEL - <c>TeamEntity.MselId</c> is required - so one is seeded too, but
    /// it deliberately does not use the competency itself.
    /// </summary>
    private async Task<TeamEntity> SeedTeamUsing(CompetencyEntity competency)
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);
        await Seed(new TeamCompetencyEntity(team.Id, competency.Id));

        return team;
    }

    private static CompetencyFramework Framework(params Competency[] competencies) =>
        new()
        {
            Name = "Posted framework",
            IdNumber = $"POSTED-{Guid.NewGuid()}",
            Competencies = competencies
        };

    /// <summary>
    /// A competency as a caller sends it. <paramref name="id"/> matters only as the value a sibling's
    /// <paramref name="parentId"/> points at - the service discards it and mints a new one.
    /// </summary>
    private static Competency Vm(
        string idNumber,
        string shortName = null,
        Guid? id = null,
        Guid? parentId = null,
        string[] related = null) =>
        new()
        {
            Id = id ?? Guid.NewGuid(),
            IdNumber = idNumber,
            ShortName = shortName ?? $"competency {idNumber}",
            Description = "Posted",
            ParentId = parentId,
            RelatedIdNumbers = related ?? []
        };

    private static Competency Competency(CompetencyFramework framework, string idNumber) =>
        framework.Competencies.Single(c => c.IdNumber == idNumber);

    private async Task<List<CompetencyFramework>> List(HttpClient client) =>
        await client.GetFromJsonAsync<List<CompetencyFramework>>("api/competencyframeworks", JsonOptions, Ct);

    private async Task<CompetencyFramework> Single(HttpClient client, Guid id) =>
        await client.GetFromJsonAsync<CompetencyFramework>($"api/competencyframeworks/{id}", JsonOptions, Ct);

    private async Task<FrameworkDeleteCheck> CanDelete(HttpClient client, Guid id) =>
        await client.GetFromJsonAsync<FrameworkDeleteCheck>(
            $"api/competencyframeworks/{id}/can-delete", JsonOptions, Ct);

    private async Task<HttpResponseMessage> Create(HttpClient client, CompetencyFramework framework) =>
        await client.PostAsJsonAsync("api/competencyframeworks", framework, JsonOptions, Ct);

    private async Task<HttpResponseMessage> Update(HttpClient client, Guid id, CompetencyFramework framework) =>
        await client.PutAsJsonAsync($"api/competencyframeworks/{id}", framework, JsonOptions, Ct);

    /// <summary>
    /// The response body, having first insisted the request succeeded. Use <see cref="ReadError"/> for
    /// the failure cases.
    /// </summary>
    /// <remarks>
    /// An <c>ApiError</c> body shares no property names with a framework, so deserializing one into a
    /// <c>CompetencyFramework</c> yields defaults throughout - which is what some of the assertions here
    /// are looking for. Without this check a refusal can pass for the answer the test wanted.
    /// </remarks>
    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.True(
            response.IsSuccessStatusCode,
            $"Expected a success status, got {(int)response.StatusCode}: " +
            await response.Content.ReadAsStringAsync(Ct));

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
    }

    private async Task<ApiError> ReadError(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions, Ct);

    /// <summary>
    /// Asserts a server-stamped audit timestamp: present, and inside the window the test bracketed.
    /// </summary>
    private static void AssertStampedBetween(DateTime? actual, DateTime notBefore, DateTime notAfter)
    {
        Assert.NotNull(actual);
        Assert.InRange(actual.Value, notBefore, notAfter);
    }
}
