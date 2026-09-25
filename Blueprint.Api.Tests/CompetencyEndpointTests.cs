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
/// The three endpoints that edit one competency inside a framework: <c>POST
/// competencyframeworks/{frameworkId}/competencies</c>, <c>PUT competencies/{competencyId}</c> and
/// <c>DELETE competencies/{competencyId}</c>.
/// </summary>
/// <remarks>
/// <para>
/// These exist for hand-editing an imported framework - correcting a work role, adding a task the
/// spreadsheet missed - so they are the only path by which a competency arrives without going through an
/// importer, and they behave differently from the importers in ways that matter. Two stand out. The
/// create never sets <c>Path</c>, so a competency added by hand has a null one where every imported
/// sibling has a full ancestor chain (<see cref="Create_LeavesThePathNull"/>). And the update never
/// rebuilds <c>Path</c> either, so re-parenting a competency leaves it claiming its old ancestry
/// (<see cref="Update_DoesNotRebuildThePathWhenTheParentChanges"/>). Anything reading the tree from
/// <c>path</c> rather than <c>parentId</c> is reading a field these two endpoints do not maintain.
/// </para>
/// <para>
/// The update's relationship reconciliation is the substantial logic here and the code comment above it
/// is worth reading: because <c>GetAsync</c> reports a competency's related list as the <em>union</em> of
/// its outbound and inbound rows, an update has to act on both directions or a caller who drops a link
/// gets a 200 and no change. It does, and <see cref="Update_RemovesAnInboundLinkTheCallerLeftOut"/> and
/// <see cref="Update_KeepsAnInboundLinkWithoutAddingAReverseRow"/> pin the two halves. What it does not
/// do is exclude the competency itself, so a competency may be related to itself
/// (<see cref="Update_CanRelateACompetencyToItself"/>), just as it may be its own parent
/// (<see cref="Update_CanMakeACompetencyItsOwnParent"/>).
/// </para>
/// <para>
/// Neither write returns the relationships it just wrote: the mapping profile ignores
/// <c>RelatedIdNumbers</c> in the entity-to-view-model direction, so both responses carry an empty
/// collection whatever the request contained. A caller has to re-read the framework to see its own edit.
/// </para>
/// <para>
/// Authorization is the framework-wide <c>ManageCompetencyFrameworks</c> on all three, with no per-MSEL
/// dimension - see <c>CompetencyFrameworkEndpointTests</c> for why that is, and for the reads that are
/// not gated at all.
/// </para>
/// </remarks>
public class CompetencyEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // POST api/competencyframeworks/{frameworkId}/competencies
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Characterization. The 201's <c>Location</c> names the <em>framework</em>, not the competency just
    /// created, so a client following it gets the whole framework back.
    /// </summary>
    /// <remarks>
    /// <c>CreatedAtAction(nameof(Get), new { id = frameworkId }, created)</c> - and there is no
    /// single-competency route to point at, which is the underlying gap. Turns red when one is added, or
    /// when the header is dropped in favour of a plain <c>Ok</c>.
    /// </remarks>
    [Fact]
    public async Task Create_Is201_WithALocationHeaderPointingAtTheFramework()
    {
        var framework = await SeedFramework();
        var actor = await Manager();

        var response = await Create(Client(actor), framework.Id, Vm("NEW"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await Read<Competency>(response);
        Assert.NotEqual(framework.Id, created.Id);
        Assert.EndsWith($"/api/competencyframeworks/{framework.Id}", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Create_StoresTheCompetency()
    {
        var framework = await SeedFramework();
        var actor = await Manager();

        var response = await Create(Client(actor), framework.Id, new Competency
        {
            IdNumber = "T-99",
            ShortName = "Analyse the thing",
            Description = "Added by hand after the import",
            DescriptionFormat = 1,
            SortOrder = 7,
            RuleType = "outcome",
            RuleOutcome = 2,
            RuleConfig = "{}",
            ScaleValues = "1,2,3",
            ScaleConfiguration = "{ \"scale\": true }"
        });

        var created = await Read<Competency>(response);
        await using var db = NewContext();
        var stored = await db.Competencies.SingleAsync(c => c.Id == created.Id, Ct);
        Assert.Equal(framework.Id, stored.CompetencyFrameworkId);
        Assert.Equal("T-99", stored.IdNumber);
        Assert.Equal("Analyse the thing", stored.ShortName);
        Assert.Equal("Added by hand after the import", stored.Description);
        Assert.Equal(1, stored.DescriptionFormat);
        Assert.Equal(7, stored.SortOrder);
        Assert.Equal("outcome", stored.RuleType);
        Assert.Equal(2, stored.RuleOutcome);
        Assert.Equal("{}", stored.RuleConfig);
        Assert.Equal("1,2,3", stored.ScaleValues);
        Assert.Equal("{ \"scale\": true }", stored.ScaleConfiguration);
    }

    [Fact]
    public async Task Create_GivesTheCompetencyAFreshId()
    {
        var framework = await SeedFramework();
        var actor = await Manager();
        var payloadId = Guid.NewGuid();

        var response = await Create(Client(actor), framework.Id, Vm("NEW", id: payloadId));

        Assert.NotEqual(payloadId, (await Read<Competency>(response)).Id);
    }

    /// <summary>
    /// The route decides which framework the competency joins; the body's
    /// <c>competencyFrameworkId</c> is overwritten, not honoured and not rejected.
    /// </summary>
    [Fact]
    public async Task Create_IgnoresTheFrameworkIdInTheBody()
    {
        var other = await SeedFramework();
        var framework = await SeedFramework();
        var actor = await Manager();

        var response = await Create(Client(actor), framework.Id, new Competency
        {
            IdNumber = "NEW",
            ShortName = "n",
            CompetencyFrameworkId = other.Id
        });

        Assert.Equal(framework.Id, (await Read<Competency>(response)).CompetencyFrameworkId);
    }

    [Fact]
    public async Task Create_StoresTheCallerAsTheCreatorAndStampsTheAuditFields()
    {
        var framework = await SeedFramework();
        var actor = await Manager();
        var before = DateTime.UtcNow;

        var response = await Create(Client(actor), framework.Id, new Competency
        {
            IdNumber = "NEW",
            ShortName = "n",
            // Hostile: the audit fields belong to the server.
            CreatedBy = Guid.NewGuid(),
            DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ModifiedBy = Guid.NewGuid(),
            DateModified = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });

        var created = await Read<Competency>(response);
        Assert.Equal(actor.Id, created.CreatedBy);
        AssertStampedBetween(created.DateCreated, before, DateTime.UtcNow);
        Assert.Null(created.DateModified);
        Assert.Null(created.ModifiedBy);
    }

    [Fact]
    public async Task Create_ForAnUnknownFramework_Is404()
    {
        var actor = await Manager();

        var response = await Create(Client(actor), Guid.NewGuid(), Vm("NEW"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(SystemPermission.ViewCompetencyFrameworks)]
    [InlineData(SystemPermission.ManageMsels)]
    public async Task Create_WithoutManageCompetencyFrameworks_Is403(SystemPermission permission)
    {
        var framework = await SeedFramework();
        var actor = await Actor().WithSystemPermissions(permission).SeedAsync();

        var response = await Create(Client(actor), framework.Id, Vm("NEW"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var db = NewContext();
        Assert.Empty(await db.Competencies.ToListAsync(Ct));
    }

    [Fact]
    public async Task Create_TrimsTheIdNumber()
    {
        var framework = await SeedFramework();
        var actor = await Manager();

        var response = await Create(Client(actor), framework.Id, Vm("  T-1  "));

        Assert.Equal("T-1", (await Read<Competency>(response)).IdNumber);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_WithABlankIdNumber_StoresNull(string idNumber)
    {
        var framework = await SeedFramework();
        var actor = await Manager();

        var response = await Create(Client(actor), framework.Id, Vm(idNumber));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null((await Read<Competency>(response)).IdNumber);
    }

    [Fact]
    public async Task Create_WithAnIdNumberAlreadyInTheFramework_Is409_NamingTheCompetencyThatHasIt()
    {
        var framework = await SeedFramework();
        await SeedCompetency(framework, "T-1", c => c.ShortName = "The first task");
        var actor = await Manager();

        var response = await Create(Client(actor), framework.Id, Vm("T-1"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await ReadError(response);
        Assert.Contains("'T-1'", error.Title);
        Assert.Contains("'The first task'", error.Title);
    }

    /// <summary>
    /// ID numbers are unique per framework, not per installation - two frameworks describing the same
    /// work role both call it the same thing.
    /// </summary>
    [Fact]
    public async Task Create_WithAnIdNumberUsedInAnotherFramework_Is201()
    {
        var other = await SeedFramework();
        await SeedCompetency(other, "T-1");
        var framework = await SeedFramework();
        var actor = await Manager();

        var response = await Create(Client(actor), framework.Id, Vm("T-1"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Create_AllowsSeveralCompetenciesWithNoIdNumber()
    {
        var framework = await SeedFramework();
        var actor = await Manager();

        var first = await Create(Client(actor), framework.Id, Vm(null));
        var second = await Create(Client(actor), framework.Id, Vm("   "));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
    }

    /// <summary>
    /// Characterization. A competency added by hand has no <c>Path</c>, where every imported sibling has
    /// the full chain of ancestor ids that <c>CreateAsync</c>'s <c>BuildPath</c> gives it.
    /// </summary>
    /// <remarks>
    /// Nothing on this endpoint computes it, and nothing recomputes it later - see
    /// <see cref="Update_DoesNotRebuildThePathWhenTheParentChanges"/>. So a framework that has been
    /// edited holds a mixture of competencies with a path and competencies without, which is worse than
    /// either. Turns red once the create builds the path as the importers do.
    /// </remarks>
    [Fact]
    public async Task Create_LeavesThePathNull()
    {
        var framework = await SeedFramework();
        var parent = await SeedCompetency(framework, "PARENT");
        var actor = await Manager();

        var response = await Create(Client(actor), framework.Id, Vm("CHILD", parentId: parent.Id));

        var created = await Read<Competency>(response);
        Assert.Null(created.Path);
        await using var db = NewContext();
        Assert.Null((await db.Competencies.SingleAsync(c => c.Id == created.Id, Ct)).Path);
    }

    /// <summary>
    /// Characterization, and the other half of <see cref="Create_LeavesThePathNull"/>: the body's
    /// <c>path</c> is stored exactly as sent, so the one field a client must not author is the one field
    /// it can.
    /// </summary>
    [Fact]
    public async Task Create_StoresACallerSuppliedPathVerbatim()
    {
        var framework = await SeedFramework();
        var actor = await Manager();

        var response = await Create(Client(actor), framework.Id, new Competency
        {
            IdNumber = "NEW",
            ShortName = "n",
            Path = "/not/a/path/of/ids"
        });

        Assert.Equal("/not/a/path/of/ids", (await Read<Competency>(response)).Path);
    }

    [Fact]
    public async Task Create_ResolvesRelatedIdNumbersIntoRelationships()
    {
        var framework = await SeedFramework();
        var task = await SeedCompetency(framework, "T-1");
        var knowledge = await SeedCompetency(framework, "K-1");
        var actor = await Manager();

        var response = await Create(Client(actor), framework.Id, Vm("ROLE", related: ["T-1", "K-1"]));

        var created = await Read<Competency>(response);
        await using var db = NewContext();
        var stored = await db.CompetencyRelationships.ToListAsync(Ct);
        Assert.All(stored, r => Assert.Equal(created.Id, r.CompetencyId));
        Assert.Equal(
            [knowledge.Id, task.Id],
            stored.Select(r => r.RelatedCompetencyId).OrderBy(id => id == knowledge.Id ? 0 : 1));
        Assert.All(stored, r => Assert.Equal(actor.Id, r.CreatedBy));
    }

    [Fact]
    public async Task Create_WhenTheSameRelatedIdNumberIsListedTwice_StoresOneRelationship()
    {
        var framework = await SeedFramework();
        await SeedCompetency(framework, "T-1");
        var actor = await Manager();

        var response = await Create(Client(actor), framework.Id, Vm("ROLE", related: ["T-1", "T-1"]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var db = NewContext();
        Assert.Single(await db.CompetencyRelationships.ToListAsync(Ct));
    }

    [Fact]
    public async Task Create_IgnoresAnUnknownRelatedIdNumber()
    {
        var framework = await SeedFramework();
        var actor = await Manager();

        var response = await Create(Client(actor), framework.Id, Vm("ROLE", related: ["NOT-HERE"]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var db = NewContext();
        Assert.Empty(await db.CompetencyRelationships.ToListAsync(Ct));
    }

    /// <summary>
    /// Relationships are framework-local. A related ID number that exists only in another framework is
    /// ignored, silently - the same silence as an ID number that exists nowhere.
    /// </summary>
    [Fact]
    public async Task Create_IgnoresARelatedIdNumberInAnotherFramework()
    {
        var other = await SeedFramework();
        await SeedCompetency(other, "T-1");
        var framework = await SeedFramework();
        var actor = await Manager();

        var response = await Create(Client(actor), framework.Id, Vm("ROLE", related: ["T-1"]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var db = NewContext();
        Assert.Empty(await db.CompetencyRelationships.ToListAsync(Ct));
    }

    [Fact]
    public async Task Create_IgnoresABlankRelatedIdNumber()
    {
        var framework = await SeedFramework();
        var actor = await Manager();

        var response = await Create(Client(actor), framework.Id, Vm("ROLE", related: ["", "   "]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var db = NewContext();
        Assert.Empty(await db.CompetencyRelationships.ToListAsync(Ct));
    }

    /// <summary>
    /// Characterization. The response never carries the relationships the request just created, because
    /// <c>CompetencyProfile</c> ignores <c>RelatedIdNumbers</c> mapping out of the entity.
    /// </summary>
    /// <remarks>
    /// The rows are there - <see cref="Create_ResolvesRelatedIdNumbersIntoRelationships"/> reads them
    /// from the database, and a re-read of the framework shows them. But a client that trusts the
    /// response body concludes its own write was dropped. Same on the update. Turns red when the profile
    /// maps the member, or the service returns <c>GetAsync</c>'s shape instead of a mapped entity.
    /// </remarks>
    [Fact]
    public async Task Create_DoesNotReturnTheRelatedIdNumbersItStored()
    {
        var framework = await SeedFramework();
        await SeedCompetency(framework, "T-1");
        var actor = await Manager();

        var response = await Create(Client(actor), framework.Id, Vm("ROLE", related: ["T-1"]));

        Assert.Empty((await Read<Competency>(response)).RelatedIdNumbers);
    }

    [Fact]
    public async Task Create_TheNewCompetencyIsOnTheFrameworkWithItsRelationships()
    {
        var framework = await SeedFramework();
        await SeedCompetency(framework, "T-1");
        var actor = await Manager();

        await Create(Client(actor), framework.Id, Vm("ROLE", related: ["T-1"]));
        var reread = await Client(actor).GetFromJsonAsync<CompetencyFramework>(
            $"api/competencyframeworks/{framework.Id}", JsonOptions, Ct);

        Assert.Equal(["ROLE", "T-1"], reread.Competencies.Select(c => c.IdNumber).Order());
        Assert.Equal(["T-1"], Competency(reread, "ROLE").RelatedIdNumbers);
        Assert.Equal(["ROLE"], Competency(reread, "T-1").RelatedIdNumbers);
    }

    [Fact]
    public async Task Create_WithAParentInTheSameFramework_StoresTheParent()
    {
        var framework = await SeedFramework();
        var parent = await SeedCompetency(framework, "PARENT");
        var actor = await Manager();

        var response = await Create(Client(actor), framework.Id, Vm("CHILD", parentId: parent.Id));

        Assert.Equal(parent.Id, (await Read<Competency>(response)).ParentId);
    }

    /// <summary>
    /// Characterization. A parent in a <em>different</em> framework is accepted, because the foreign key
    /// only requires the competency to exist.
    /// </summary>
    /// <remarks>
    /// The result is a competency whose parent is not in its own framework's competency list, so
    /// <c>GET competencyframeworks/{id}</c> returns a <c>parentId</c> naming nothing the client can see,
    /// and deleting the other framework is blocked by a <c>Restrict</c> foreign key it has no way to
    /// explain. Turns red once the parent is checked against the framework.
    /// </remarks>
    [Fact]
    public async Task Create_WithAParentInAnotherFramework_Is201()
    {
        var other = await SeedFramework();
        var foreignParent = await SeedCompetency(other, "PARENT");
        var framework = await SeedFramework();
        var actor = await Manager();

        var response = await Create(Client(actor), framework.Id, Vm("CHILD", parentId: foreignParent.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(foreignParent.Id, (await Read<Competency>(response)).ParentId);
    }

    /// <summary>
    /// Characterization. A <c>parentId</c> naming no competency is a 500 carrying the raw database
    /// message, not a 400.
    /// </summary>
    /// <remarks>
    /// <c>TranslateDbUpdateException</c> recognizes the two unique indexes and turns everything else -
    /// this foreign key included - into an <c>ArgumentException</c>, which is not an <c>IApiException</c>.
    /// Turns red once the parent is validated, or once the fall-through carries a 400.
    /// </remarks>
    [Fact]
    public async Task Create_WithAParentThatDoesNotExist_Is500()
    {
        var framework = await SeedFramework();
        var actor = await Manager();

        var response = await Create(Client(actor), framework.Id, Vm("CHILD", parentId: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("Database error creating competency", (await ReadError(response)).Title);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT api/competencies/{competencyId}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_StoresTheEditableFields()
    {
        var framework = await SeedFramework();
        var parent = await SeedCompetency(framework, "PARENT");
        var competency = await SeedCompetency(framework, "T-1");
        var actor = await Manager();

        var response = await Update(Client(actor), competency.Id, new Competency
        {
            IdNumber = "T-2",
            ShortName = "Renamed",
            Description = "Rewritten",
            ParentId = parent.Id,
            SortOrder = 12
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = NewContext();
        var stored = await db.Competencies.SingleAsync(c => c.Id == competency.Id, Ct);
        Assert.Equal("T-2", stored.IdNumber);
        Assert.Equal("Renamed", stored.ShortName);
        Assert.Equal("Rewritten", stored.Description);
        Assert.Equal(parent.Id, stored.ParentId);
        Assert.Equal(12, stored.SortOrder);
    }

    [Fact]
    public async Task Update_OfAnUnknownCompetency_Is404()
    {
        var actor = await Manager();

        var response = await Update(Client(actor), Guid.NewGuid(), Vm("T-1"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(SystemPermission.ViewCompetencyFrameworks)]
    [InlineData(SystemPermission.ManageMsels)]
    public async Task Update_WithoutManageCompetencyFrameworks_Is403(SystemPermission permission)
    {
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "T-1");
        var actor = await Actor().WithSystemPermissions(permission).SeedAsync();

        var response = await Update(Client(actor), competency.Id, Vm("T-2"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var db = NewContext();
        Assert.Equal("T-1", (await db.Competencies.SingleAsync(c => c.Id == competency.Id, Ct)).IdNumber);
    }

    [Fact]
    public async Task Update_StampsTheAuditFieldsAndPreservesCreation()
    {
        var framework = await SeedFramework();
        var creator = Guid.NewGuid();
        var competency = await SeedCompetency(framework, "T-1", c => c.CreatedBy = creator);

        // Not the value the arrangement asked for: SaveEntries stamps DateCreated on insert, so the
        // seed's own date is the one the update has to preserve.
        var created = competency.DateCreated;

        var actor = await Manager();
        var before = DateTime.UtcNow;

        var response = await Update(Client(actor), competency.Id, new Competency
        {
            IdNumber = "T-1",
            ShortName = "Renamed",
            CreatedBy = Guid.NewGuid(),
            DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ModifiedBy = Guid.NewGuid()
        });

        var updated = await Read<Competency>(response);
        Assert.Equal(creator, updated.CreatedBy);
        Assert.Equal(created, updated.DateCreated, TimeSpan.FromMilliseconds(1));
        Assert.Equal(actor.Id, updated.ModifiedBy);
        AssertStampedBetween(updated.DateModified, before, DateTime.UtcNow);
    }

    /// <summary>
    /// Characterization. The update ignores the rule and scale fields and the description format, all of
    /// which the <em>create</em> stores - so a competency's rule can be set once and never corrected.
    /// </summary>
    /// <remarks>
    /// The asymmetry is the finding: the create maps the whole view model, the update assigns six named
    /// fields. Nothing tells the caller; the 200 carries the unchanged values back.
    /// </remarks>
    [Fact]
    public async Task Update_IgnoresTheRuleAndScaleFields()
    {
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "T-1", c =>
        {
            c.DescriptionFormat = 1;
            c.RuleType = "original rule";
            c.RuleOutcome = 1;
            c.RuleConfig = "original config";
            c.ScaleValues = "original scale";
            c.ScaleConfiguration = "original scale config";
        });
        var actor = await Manager();

        var response = await Update(Client(actor), competency.Id, new Competency
        {
            IdNumber = "T-1",
            ShortName = "Renamed",
            DescriptionFormat = 2,
            RuleType = "new rule",
            RuleOutcome = 2,
            RuleConfig = "new config",
            ScaleValues = "new scale",
            ScaleConfiguration = "new scale config"
        });

        var updated = await Read<Competency>(response);
        Assert.Equal(1, updated.DescriptionFormat);
        Assert.Equal("original rule", updated.RuleType);
        Assert.Equal(1, updated.RuleOutcome);
        Assert.Equal("original config", updated.RuleConfig);
        Assert.Equal("original scale", updated.ScaleValues);
        Assert.Equal("original scale config", updated.ScaleConfiguration);
    }

    /// <summary>
    /// The body's <c>competencyFrameworkId</c> is ignored, so a competency cannot be moved between
    /// frameworks. This one is deliberate and right; it is here because the ignoring is silent.
    /// </summary>
    [Fact]
    public async Task Update_IgnoresTheFrameworkIdInTheBody()
    {
        var framework = await SeedFramework();
        var other = await SeedFramework();
        var competency = await SeedCompetency(framework, "T-1");
        var actor = await Manager();

        var response = await Update(Client(actor), competency.Id, new Competency
        {
            IdNumber = "T-1",
            ShortName = "n",
            CompetencyFrameworkId = other.Id
        });

        Assert.Equal(framework.Id, (await Read<Competency>(response)).CompetencyFrameworkId);
    }

    [Fact]
    public async Task Update_IgnoresThePathInTheBody()
    {
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "T-1");
        var actor = await Manager();

        var response = await Update(Client(actor), competency.Id, new Competency
        {
            IdNumber = "T-1",
            ShortName = "n",
            Path = "/rewritten"
        });

        Assert.Equal(competency.Path, (await Read<Competency>(response)).Path);
    }

    /// <summary>
    /// Characterization. Re-parenting a competency does not rebuild its <c>Path</c>, so the row ends up
    /// asserting two different ancestries at once.
    /// </summary>
    /// <remarks>
    /// <c>CreateAsync</c> maintains <c>Path</c> for every importer; nothing maintains it afterwards.
    /// A client reading the tree from <c>path</c> - which is what it is for, since it is the only field
    /// giving the whole chain in one read - sees the competency under its old parent indefinitely. Turns
    /// red once the update rebuilds the path, which also has to rebuild every descendant's.
    /// </remarks>
    [Fact]
    public async Task Update_DoesNotRebuildThePathWhenTheParentChanges()
    {
        var framework = await SeedFramework();
        var oldParent = await SeedCompetency(framework, "OLD-PARENT");
        var newParent = await SeedCompetency(framework, "NEW-PARENT");
        var child = await SeedCompetency(framework, "CHILD", c =>
        {
            c.ParentId = oldParent.Id;
            c.Path = $"/{oldParent.Id}/{c.Id}";
        });
        var actor = await Manager();

        var response = await Update(Client(actor), child.Id, new Competency
        {
            IdNumber = "CHILD",
            ShortName = "n",
            ParentId = newParent.Id
        });

        var updated = await Read<Competency>(response);
        Assert.Equal(newParent.Id, updated.ParentId);
        Assert.Equal($"/{oldParent.Id}/{child.Id}", updated.Path);
    }

    [Fact]
    public async Task Update_WithNoParentInTheBody_DetachesTheCompetency()
    {
        var framework = await SeedFramework();
        var parent = await SeedCompetency(framework, "PARENT");
        var child = await SeedCompetency(framework, "CHILD", c => c.ParentId = parent.Id);
        var actor = await Manager();

        var response = await Update(Client(actor), child.Id, Vm("CHILD"));

        Assert.Null((await Read<Competency>(response)).ParentId);
    }

    /// <summary>
    /// Characterization. Nothing stops a competency being made its own parent.
    /// </summary>
    /// <remarks>
    /// The foreign key is satisfied by the row itself, and the update neither checks nor rebuilds the
    /// path, so the write succeeds. <c>CreateAsync</c>'s <c>BuildPath</c> guards against exactly this
    /// when importing, which is how the shape is known to occur in real files. Turns red once the update
    /// refuses a parent that is the competency itself or one of its descendants.
    /// </remarks>
    [Fact]
    public async Task Update_CanMakeACompetencyItsOwnParent()
    {
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "T-1");
        var actor = await Manager();

        var response = await Update(Client(actor), competency.Id, new Competency
        {
            IdNumber = "T-1",
            ShortName = "n",
            ParentId = competency.Id
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(competency.Id, (await Read<Competency>(response)).ParentId);
    }

    [Fact]
    public async Task Update_KeepingItsOwnIdNumber_Is200()
    {
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "T-1");
        var actor = await Manager();

        var response = await Update(Client(actor), competency.Id, Vm("T-1", "Renamed"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Renamed", (await Read<Competency>(response)).ShortName);
    }

    [Fact]
    public async Task Update_WithAnIdNumberUsedBySiblingInTheFramework_Is409()
    {
        var framework = await SeedFramework();
        await SeedCompetency(framework, "T-1", c => c.ShortName = "The first task");
        var competency = await SeedCompetency(framework, "T-2");
        var actor = await Manager();

        var response = await Update(Client(actor), competency.Id, Vm("T-1"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("'The first task'", (await ReadError(response)).Title);
    }

    [Fact]
    public async Task Update_WithAnIdNumberUsedInAnotherFramework_Is200()
    {
        var other = await SeedFramework();
        await SeedCompetency(other, "T-1");
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "T-2");
        var actor = await Manager();

        var response = await Update(Client(actor), competency.Id, Vm("T-1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Update_WithABlankIdNumber_ClearsIt()
    {
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "T-1");
        var actor = await Manager();

        var response = await Update(Client(actor), competency.Id, Vm("   "));

        Assert.Null((await Read<Competency>(response)).IdNumber);
    }

    // ---- the relationship reconciliation -------------------------------------------------------

    /// <summary>
    /// A null <c>relatedIdNumbers</c> means "I am not editing the relationships", which is what lets a
    /// client rename a competency without having to send its whole related list back.
    /// </summary>
    [Fact]
    public async Task Update_WithNoRelatedIdNumbers_LeavesTheLinksAlone()
    {
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "ROLE");
        var task = await SeedCompetency(framework, "T-1");
        await SeedRelationship(competency, task);
        var actor = await Manager();

        var response = await Update(Client(actor), competency.Id, new Competency
        {
            IdNumber = "ROLE",
            ShortName = "Renamed",
            RelatedIdNumbers = null
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = NewContext();
        Assert.Single(await db.CompetencyRelationships.ToListAsync(Ct));
    }

    [Fact]
    public async Task Update_WithAnEmptyRelatedIdNumbers_RemovesEveryLinkInBothDirections()
    {
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "ROLE");
        var outbound = await SeedCompetency(framework, "T-1");
        var inbound = await SeedCompetency(framework, "K-1");
        await SeedRelationship(competency, outbound);
        await SeedRelationship(inbound, competency);
        var actor = await Manager();

        await UpdateOk(Client(actor), competency.Id, new Competency
        {
            IdNumber = "ROLE",
            ShortName = "n",
            RelatedIdNumbers = []
        });

        await using var db = NewContext();
        Assert.Empty(await db.CompetencyRelationships.ToListAsync(Ct));
    }

    [Fact]
    public async Task Update_RemovesAnOutboundLinkTheCallerLeftOut()
    {
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "ROLE");
        var kept = await SeedCompetency(framework, "T-1");
        var dropped = await SeedCompetency(framework, "T-2");
        await SeedRelationship(competency, kept);
        await SeedRelationship(competency, dropped);
        var actor = await Manager();

        await UpdateOk(Client(actor), competency.Id, Vm("ROLE", related: ["T-1"]));

        await using var db = NewContext();
        var stored = Assert.Single(await db.CompetencyRelationships.ToListAsync(Ct));
        Assert.Equal(kept.Id, stored.RelatedCompetencyId);
    }

    /// <summary>
    /// The reconciliation's reason for existing: a link stored the other way round still appears in the
    /// competency's related list, so leaving it out has to remove it.
    /// </summary>
    /// <remarks>
    /// Before both directions were handled, this returned 200 and changed nothing - the client saw the
    /// link it had just deleted come straight back on the next read.
    /// </remarks>
    [Fact]
    public async Task Update_RemovesAnInboundLinkTheCallerLeftOut()
    {
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "ROLE");
        var inbound = await SeedCompetency(framework, "T-1");
        await SeedRelationship(inbound, competency);
        var actor = await Manager();

        await UpdateOk(Client(actor), competency.Id, new Competency
        {
            IdNumber = "ROLE",
            ShortName = "n",
            RelatedIdNumbers = []
        });

        await using var db = NewContext();
        Assert.Empty(await db.CompetencyRelationships.ToListAsync(Ct));
    }

    /// <summary>
    /// The other half: a link the caller <em>keeps</em> that happens to be stored inbound must not gain a
    /// second row in the outbound direction.
    /// </summary>
    /// <remarks>
    /// The unique index is on <c>(CompetencyId, RelatedCompetencyId)</c>, so a reverse row does not
    /// collide - it just accumulates, and every subsequent update adds another pair to reconcile.
    /// </remarks>
    [Fact]
    public async Task Update_KeepsAnInboundLinkWithoutAddingAReverseRow()
    {
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "ROLE");
        var inbound = await SeedCompetency(framework, "T-1");
        await SeedRelationship(inbound, competency);
        var actor = await Manager();

        await UpdateOk(Client(actor), competency.Id, Vm("ROLE", related: ["T-1"]));

        await using var db = NewContext();
        var stored = Assert.Single(await db.CompetencyRelationships.ToListAsync(Ct));
        Assert.Equal(inbound.Id, stored.CompetencyId);
        Assert.Equal(competency.Id, stored.RelatedCompetencyId);
    }

    [Fact]
    public async Task Update_AddsALinkThatDidNotExistInEitherDirection()
    {
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "ROLE");
        var added = await SeedCompetency(framework, "T-1");
        var actor = await Manager();

        await UpdateOk(Client(actor), competency.Id, Vm("ROLE", related: ["T-1"]));

        await using var db = NewContext();
        var stored = Assert.Single(await db.CompetencyRelationships.ToListAsync(Ct));
        Assert.Equal(competency.Id, stored.CompetencyId);
        Assert.Equal(added.Id, stored.RelatedCompetencyId);
    }

    [Fact]
    public async Task Update_ReplacingOneLinkWithAnother_RemovesTheOldAndAddsTheNew()
    {
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "ROLE");
        var dropped = await SeedCompetency(framework, "T-1");
        var added = await SeedCompetency(framework, "K-1");
        await SeedRelationship(competency, dropped);
        var actor = await Manager();

        await UpdateOk(Client(actor), competency.Id, Vm("ROLE", related: ["K-1"]));

        await using var db = NewContext();
        var stored = Assert.Single(await db.CompetencyRelationships.ToListAsync(Ct));
        Assert.Equal(added.Id, stored.RelatedCompetencyId);
    }

    [Fact]
    public async Task Update_DoesNotTouchAnotherCompetencysLinks()
    {
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "ROLE");
        var other = await SeedCompetency(framework, "OTHER-ROLE");
        var task = await SeedCompetency(framework, "T-1");
        await SeedRelationship(competency, task);
        await SeedRelationship(other, task);
        var actor = await Manager();

        await UpdateOk(Client(actor), competency.Id, new Competency
        {
            IdNumber = "ROLE",
            ShortName = "n",
            RelatedIdNumbers = []
        });

        await using var db = NewContext();
        var stored = Assert.Single(await db.CompetencyRelationships.ToListAsync(Ct));
        Assert.Equal(other.Id, stored.CompetencyId);
    }

    [Fact]
    public async Task Update_IgnoresAnUnknownRelatedIdNumber()
    {
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "ROLE");
        var actor = await Manager();

        var response = await Update(Client(actor), competency.Id, Vm("ROLE", related: ["NOT-HERE"]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = NewContext();
        Assert.Empty(await db.CompetencyRelationships.ToListAsync(Ct));
    }

    [Fact]
    public async Task Update_IgnoresARelatedIdNumberInAnotherFramework()
    {
        var other = await SeedFramework();
        await SeedCompetency(other, "T-1");
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "ROLE");
        var actor = await Manager();

        var response = await Update(Client(actor), competency.Id, Vm("ROLE", related: ["T-1"]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = NewContext();
        Assert.Empty(await db.CompetencyRelationships.ToListAsync(Ct));
    }

    [Fact]
    public async Task Update_IgnoresABlankRelatedIdNumber()
    {
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "ROLE");
        var task = await SeedCompetency(framework, "T-1");
        await SeedRelationship(competency, task);
        var actor = await Manager();

        // The blank entries are dropped, so this is the same request as ["T-1"] - the link survives.
        await UpdateOk(Client(actor), competency.Id, Vm("ROLE", related: ["T-1", "", "   "]));

        await using var db = NewContext();
        Assert.Single(await db.CompetencyRelationships.ToListAsync(Ct));
    }

    /// <summary>
    /// Characterization. A competency may be related to itself.
    /// </summary>
    /// <remarks>
    /// The reconciliation never excludes the competency being edited, and the unique index on
    /// <c>(CompetencyId, RelatedCompetencyId)</c> is satisfied by a self-pair, so listing its own ID
    /// number stores a row from it to itself - after which <c>GET competencyframeworks/{id}</c> reports
    /// the competency as related to itself. Turns red once the resolver skips <c>entity.Id</c>.
    /// </remarks>
    [Fact]
    public async Task Update_CanRelateACompetencyToItself()
    {
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "ROLE");
        var actor = await Manager();

        await UpdateOk(Client(actor), competency.Id, Vm("ROLE", related: ["ROLE"]));

        await using var db = NewContext();
        var stored = Assert.Single(await db.CompetencyRelationships.ToListAsync(Ct));
        Assert.Equal(competency.Id, stored.CompetencyId);
        Assert.Equal(competency.Id, stored.RelatedCompetencyId);
        var reread = await Client(actor).GetFromJsonAsync<CompetencyFramework>(
            $"api/competencyframeworks/{framework.Id}", JsonOptions, Ct);
        Assert.Equal(["ROLE"], Competency(reread, "ROLE").RelatedIdNumbers);
    }

    /// <summary>
    /// Characterization, matching <see cref="Create_DoesNotReturnTheRelatedIdNumbersItStored"/>: the
    /// update's response carries an empty related list however the request read.
    /// </summary>
    [Fact]
    public async Task Update_DoesNotReturnTheRelatedIdNumbersItStored()
    {
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "ROLE");
        await SeedCompetency(framework, "T-1");
        var actor = await Manager();

        var response = await Update(Client(actor), competency.Id, Vm("ROLE", related: ["T-1"]));

        Assert.Empty((await Read<Competency>(response)).RelatedIdNumbers);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE api/competencies/{competencyId}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_Is204_AndRemovesTheCompetency()
    {
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "T-1");
        var actor = await Manager();

        var response = await Client(actor).DeleteAsync($"api/competencies/{competency.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var db = NewContext();
        Assert.Empty(await db.Competencies.ToListAsync(Ct));
        Assert.True(await db.CompetencyFrameworks.AnyAsync(f => f.Id == framework.Id, Ct));
    }

    [Fact]
    public async Task Delete_OfAnUnknownCompetency_Is404()
    {
        var actor = await Manager();

        var response = await Client(actor).DeleteAsync($"api/competencies/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(SystemPermission.ViewCompetencyFrameworks)]
    [InlineData(SystemPermission.ManageMsels)]
    public async Task Delete_WithoutManageCompetencyFrameworks_Is403(SystemPermission permission)
    {
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "T-1");
        var actor = await Actor().WithSystemPermissions(permission).SeedAsync();

        var response = await Client(actor).DeleteAsync($"api/competencies/{competency.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var db = NewContext();
        Assert.True(await db.Competencies.AnyAsync(c => c.Id == competency.Id, Ct));
    }

    [Fact]
    public async Task Delete_TakesItsRelationshipsInBothDirections()
    {
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "ROLE");
        var outbound = await SeedCompetency(framework, "T-1");
        var inbound = await SeedCompetency(framework, "K-1");
        await SeedRelationship(competency, outbound);
        await SeedRelationship(inbound, competency);
        var actor = await Manager();

        var response = await Client(actor).DeleteAsync($"api/competencies/{competency.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var db = NewContext();
        Assert.Empty(await db.CompetencyRelationships.ToListAsync(Ct));
        Assert.Equal(2, await db.Competencies.CountAsync(Ct));
    }

    [Fact]
    public async Task Delete_LeavesItsSiblingsAlone()
    {
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "T-1");
        await SeedCompetency(framework, "T-2");
        var actor = await Manager();

        var response = await Client(actor).DeleteAsync($"api/competencies/{competency.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var db = NewContext();
        Assert.Equal("T-2", (await db.Competencies.SingleAsync(Ct)).IdNumber);
    }

    /// <summary>
    /// Characterization. A competency with children cannot be deleted, and the refusal is a 500 carrying
    /// the raw foreign key message.
    /// </summary>
    /// <remarks>
    /// The parent relationship is <c>Restrict</c>, deliberately - cascading would silently delete a whole
    /// subtree - but <c>DeleteCompetencyAsync</c> has no <c>try</c> at all, so the
    /// <c>DbUpdateException</c> reaches <c>JsonExceptionFilter</c> unchanged. A client is told nothing it
    /// can act on, when what it needs to hear is "delete or re-parent the children first". Turns red once
    /// the children are checked, or the save is translated as the create and update saves are.
    /// </remarks>
    [Fact]
    public async Task Delete_OfACompetencyWithChildren_Is500()
    {
        var framework = await SeedFramework();
        var parent = await SeedCompetency(framework, "PARENT");
        await SeedCompetency(framework, "CHILD", c => c.ParentId = parent.Id);
        var actor = await Manager();

        var response = await Client(actor).DeleteAsync($"api/competencies/{parent.Id}", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await using var db = NewContext();
        Assert.Equal(2, await db.Competencies.CountAsync(Ct));
    }

    /// <summary>
    /// Characterization. Deleting one competency removes it from every MSEL using it, with no check and
    /// no warning.
    /// </summary>
    /// <remarks>
    /// <c>GET competencyframeworks/{id}/can-delete</c> exists precisely so a whole framework cannot be
    /// deleted out from under a MSEL, and there is no equivalent for a single competency - so the
    /// protection can be walked around one competency at a time. Turns red once the delete checks
    /// <c>MselCompetencies</c>, which is the fix.
    /// </remarks>
    [Fact]
    public async Task Delete_RemovesTheCompetencyFromEveryMselUsingIt()
    {
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "T-1");
        var msel = await SeedMselUsing(competency);
        var actor = await Manager();

        var response = await Client(actor).DeleteAsync($"api/competencies/{competency.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var db = NewContext();
        Assert.Empty(await db.MselCompetencies.ToListAsync(Ct));
        Assert.True(await db.Msels.AnyAsync(m => m.Id == msel.Id, Ct));
    }

    [Fact]
    public async Task Delete_RemovesTheCompetencyFromEveryTeamUsingIt()
    {
        var framework = await SeedFramework();
        var competency = await SeedCompetency(framework, "T-1");
        var team = await SeedTeamUsing(competency);
        var actor = await Manager();

        var response = await Client(actor).DeleteAsync($"api/competencies/{competency.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var db = NewContext();
        Assert.Empty(await db.TeamCompetencies.ToListAsync(Ct));
        Assert.True(await db.Teams.AnyAsync(t => t.Id == team.Id, Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // Authentication
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("POST", "api/competencyframeworks/6f1f0d9e-0000-4000-8000-000000000001/competencies")]
    [InlineData("PUT", "api/competencies/6f1f0d9e-0000-4000-8000-000000000002")]
    [InlineData("DELETE", "api/competencies/6f1f0d9e-0000-4000-8000-000000000002")]
    public async Task EveryRoute_Anonymously_Is401(string method, string route)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), route);

        var response = await AnonymousClient.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private Task<TestActor> Manager() =>
        Actor().WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();

    private async Task<CompetencyFrameworkEntity> SeedFramework()
    {
        var framework = BlueprintAppFactory.CompetencyFramework();
        await Seed(framework);

        return framework;
    }

    private async Task<CompetencyEntity> SeedCompetency(
        CompetencyFrameworkEntity framework, string idNumber, Action<CompetencyEntity> arrange = null)
    {
        var competency = BlueprintAppFactory.Competency(framework.Id, idNumber);
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

    private async Task<MselEntity> SeedMselUsing(CompetencyEntity competency)
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        await Seed(new MselCompetencyEntity(msel.Id, competency.Id));

        return msel;
    }

    private async Task<TeamEntity> SeedTeamUsing(CompetencyEntity competency)
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);
        await Seed(new TeamCompetencyEntity(team.Id, competency.Id));

        return team;
    }

    /// <summary>
    /// A competency as a caller sends it. <paramref name="id"/> is only ever the value the service is
    /// expected to discard.
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
            RelatedIdNumbers = related
        };

    private static Competency Competency(CompetencyFramework framework, string idNumber) =>
        framework.Competencies.Single(c => c.IdNumber == idNumber);

    private async Task<HttpResponseMessage> Create(HttpClient client, Guid frameworkId, Competency competency) =>
        await client.PostAsJsonAsync(
            $"api/competencyframeworks/{frameworkId}/competencies", competency, JsonOptions, Ct);

    private async Task<HttpResponseMessage> Update(HttpClient client, Guid competencyId, Competency competency) =>
        await client.PutAsJsonAsync($"api/competencies/{competencyId}", competency, JsonOptions, Ct);

    /// <summary>
    /// An update that has to have succeeded. The reconciliation tests assert on the relationship rows
    /// rather than on the response, and a refused update leaves those rows exactly as they were - so
    /// without this a 409 or a 500 passes for a deliberate no-op.
    /// </summary>
    private async Task UpdateOk(HttpClient client, Guid competencyId, Competency competency)
    {
        var response = await Update(client, competencyId, competency);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected 200, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
    }

    /// <summary>
    /// The response body, having first insisted the request succeeded. Use <see cref="ReadError"/> for
    /// the failure cases.
    /// </summary>
    /// <remarks>
    /// The insisting is load-bearing rather than decorative. An <c>ApiError</c> body shares no property
    /// names with a competency, so deserializing one into a <c>Competency</c> yields defaults throughout
    /// - a null <c>ParentId</c>, an empty <c>RelatedIdNumbers</c>, an empty <c>Id</c> - which is exactly
    /// what several of the assertions below are looking for. Three tests were silently satisfied by a 409
    /// until this check existed; the mutation run that found them is in the commit message.
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

    private static void AssertStampedBetween(DateTime? actual, DateTime notBefore, DateTime notAfter)
    {
        Assert.NotNull(actual);
        Assert.InRange(actual.Value, notBefore, notAfter);
    }
}
