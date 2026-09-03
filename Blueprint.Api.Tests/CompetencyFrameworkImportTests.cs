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
using Blueprint.Api.Tests.Infrastructure;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Blueprint.Api.Tests.Infrastructure.Frameworks;

namespace Blueprint.Api.Tests;

/// <summary>
/// The two file importers a framework normally arrives through - Moodle's CSV export and NICE's JSON -
/// and the progress endpoint a client polls while one is running.
/// </summary>
/// <remarks>
/// <para>
/// Importing is how frameworks get into blueprint. The hand-editing endpoints covered in
/// <see cref="CompetencyEndpointTests"/> exist to patch up what an importer produced; nobody types a
/// 2,000-competency framework in. So the fidelity of these two parsers is the fidelity of the reference
/// data the whole competency feature rests on, and a row silently dropped here is a competency that an
/// exercise author will never be offered.
/// </para>
/// <para>
/// Silently is the operative word. Both importers are built to salvage a partly-bad file rather than
/// refuse it: the CSV parser skips any line with fewer than 14 fields
/// (<see cref="Import_SkipsARowWithTooFewFields"/>), and the importer then skips a row with no ID number
/// or no short name, and keeps only the first of a repeated ID number. The NICE importer drops any
/// relationship naming an element it does not have, and keeps only the <em>last</em> of a repeated element
/// identifier. None of that is reported anywhere - not in the response, not in the progress record, not in
/// a log line - so the only way to discover a file imported 1,900 of its 2,000 competencies is to count
/// them afterwards.
/// </para>
/// <para>
/// The failure statuses are wrong in a consistent way. A missing or empty file is a clean 400, but every
/// content problem - an empty CSV, a CSV with no framework row, malformed JSON, a NICE file missing one of
/// its three top-level arrays - is an <c>ArgumentException</c> or worse, and so a <em>500</em>. All three
/// import actions also declare only <c>Created</c> in <c>ProducesResponseType</c>, so the generated
/// client knows about none of the 400, 409 or 500 it will actually receive. Both belong on the Phase 4
/// contract list rather than being fixed here.
/// </para>
/// <para>
/// The two importers disagree about where a framework's identity comes from, which decides whether
/// re-importing is refused. The NICE importer derives the ID number from the file's own source and version
/// and additionally refuses a source/version pair already present
/// (<see cref="ImportJson_ForAnAlreadyImportedNiceSourceAndVersion_Is409"/>); the CSV importer takes the ID
/// number from the file and ignores the <c>source</c> and <c>version</c> query parameters entirely when
/// deciding, so the same framework can be imported repeatedly under different ID numbers
/// (<see cref="Import_DoesNotDeriveTheFrameworkIdNumberFromSourceAndVersion"/>).
/// </para>
/// <para>
/// Progress is host-wide and unauthorized. <c>CompetencyFrameworkImportProgressService</c> is a singleton
/// keyed only by the client-supplied import id, and <c>GetImportStatus</c> is the one action in this
/// controller with no permission check at all - so any account that can sign in can poll any import it can
/// guess the id of, and read the name of the framework being imported
/// (<see cref="GetImportStatus_ReportsAnotherAccountsImport"/>). Its own logic is covered without a host in
/// <see cref="CompetencyFrameworkImportProgressServiceTests"/>.
/// </para>
/// <para>
/// The third importer is in <see cref="CompetencyFrameworkDcwfImportTests"/>, and the three preview
/// endpoints that read the same three formats are in <see cref="CompetencyFrameworkPreviewTests"/>. The
/// file builders all three share are <see cref="Frameworks"/>, so a preview test can hand the preview and
/// the importer the same bytes and compare what they say.
/// </para>
/// </remarks>
public class CompetencyFrameworkImportTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // CSV - the framework row
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Import_CreatesTheFrameworkFromTheFrameworkRow()
    {
        var response = await ImportCsv(
            Client(await Manager()),
            Csv(Row(
                idNumber: "FW-1",
                shortName: "My Framework",
                description: "What it covers",
                descriptionFormat: "1",
                scaleValues: "[\"Not yet\",\"Competent\"]",
                scaleConfiguration: "{\"scaleid\":2}",
                isFramework: "1",
                taxonomy: "category,competency")),
            source: "NICE",
            version: "5.1");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal("My Framework", framework.Name);
        Assert.Equal("FW-1", framework.IdNumber);
        Assert.Equal("What it covers", framework.Description);
        Assert.Equal(1, framework.DescriptionFormat);
        Assert.Equal("[\"Not yet\",\"Competent\"]", framework.ScaleValues);
        Assert.Equal("{\"scaleid\":2}", framework.ScaleConfiguration);
        Assert.Equal("category,competency", framework.Taxonomies);
        Assert.Empty(framework.Competencies);
    }

    /// <summary>
    /// The framework's name is the short name column, not the description - Moodle's own export puts the
    /// human-readable title there.
    /// </summary>
    [Fact]
    public async Task Import_TakesTheSourceAndVersionFromTheQueryString()
    {
        var response = await ImportCsv(
            Client(await Manager()), Csv(FrameworkRow("FW-1")), source: "DCWF", version: "1.2");

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal("DCWF", framework.Source);
        Assert.Equal("1.2", framework.Version);
    }

    [Fact]
    public async Task Import_WithNoSourceOrVersion_StoresEmptyStrings()
    {
        var response = await ImportCsv(Client(await Manager()), Csv(FrameworkRow("FW-1")));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal("", framework.Source);
        Assert.Equal("", framework.Version);
    }

    [Fact]
    public async Task Import_StampsTheCallerAsTheCreator()
    {
        var actor = await Manager();

        var response = await ImportCsv(Client(actor), Csv(FrameworkRow("FW-1"), CompetencyRow("C1")));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal(actor.Id, framework.CreatedBy);
        Assert.Equal(actor.Id, framework.Competencies.Single().CreatedBy);
    }

    [Fact]
    public async Task Import_ReturnsALocationHeaderForTheFramework()
    {
        var response = await ImportCsv(Client(await Manager()), Csv(FrameworkRow("FW-1")));

        var framework = await Read<CompetencyFramework>(response);
        Assert.EndsWith(
            $"/api/competencyframeworks/{framework.Id}", response.Headers.Location?.ToString());
    }

    /// <summary>
    /// A blank framework ID number is stored as null rather than as an empty string, because the column is
    /// uniquely indexed and Postgres treats nulls as distinct - so two such imports both succeed where two
    /// empty strings would collide.
    /// </summary>
    [Fact]
    public async Task Import_WithABlankFrameworkIdNumber_StoresNullAndCanBeRepeated()
    {
        var client = Client(await Manager());

        var first = await Read<CompetencyFramework>(await ImportCsv(client, Csv(FrameworkRow("   "))));
        var second = await Read<CompetencyFramework>(await ImportCsv(client, Csv(FrameworkRow(""))));

        Assert.Null(first.IdNumber);
        Assert.Null(second.IdNumber);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task Import_TrimsTheFrameworkIdNumber()
    {
        var response = await ImportCsv(Client(await Manager()), Csv(FrameworkRow("  FW-1  ")));

        Assert.Equal("FW-1", (await Read<CompetencyFramework>(response)).IdNumber);
    }

    [Fact]
    public async Task Import_ForAFrameworkIdNumberAlreadyPresent_Is409()
    {
        var existing = BlueprintAppFactory.CompetencyFramework(idNumber: "FW-1", version: "4.0");
        existing.Name = "The one already here";
        await Seed(existing);

        var response = await ImportCsv(Client(await Manager()), Csv(FrameworkRow("FW-1")));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await ReadError(response);
        Assert.Contains("FW-1", error.Title);
        Assert.Contains("The one already here", error.Title);
        Assert.Contains("version 4.0", error.Title);
        Assert.Equal(1, await NewContext().CompetencyFrameworks.CountAsync(Ct));
    }

    /// <summary>
    /// Characterizes a difference between the importers rather than a defect. The CSV importer takes the
    /// framework's ID number from the file and treats <c>source</c> and <c>version</c> as free-text labels,
    /// so it never calls the source-and-version duplicate check the NICE importer does - see
    /// <see cref="ImportJson_ForAnAlreadyImportedNiceSourceAndVersion_Is409"/>. Importing the same published
    /// framework twice is therefore refused only if the file's own ID number is unchanged, and an
    /// installation ends up with two copies of NICE 5.1 the moment someone edits that one column.
    /// </summary>
    [Fact]
    public async Task Import_DoesNotDeriveTheFrameworkIdNumberFromSourceAndVersion()
    {
        var client = Client(await Manager());

        var first = await Read<CompetencyFramework>(
            await ImportCsv(client, Csv(FrameworkRow("FW-1")), source: "NICE", version: "5.1"));
        var second = await ImportCsv(client, Csv(FrameworkRow("FW-2")), source: "NICE", version: "5.1");

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var other = await Read<CompetencyFramework>(second);
        Assert.Equal("FW-1", first.IdNumber);
        Assert.Equal("FW-2", other.IdNumber);
        Assert.Equal("NICE", first.Source);
        Assert.Equal("NICE", other.Source);
        Assert.Equal("5.1", first.Version);
        Assert.Equal("5.1", other.Version);
    }

    /// <summary>
    /// The first row with the flag set wins - and the losing one disappears entirely, because the
    /// competency pass takes only rows <em>without</em> the flag. A file describing two frameworks
    /// therefore imports the first and silently discards the second along with nothing else, which is at
    /// least less surprising than importing it as a competency.
    /// </summary>
    [Fact]
    public async Task Import_WithTwoFrameworkRows_UsesTheFirstAndDiscardsTheSecond()
    {
        var response = await ImportCsv(
            Client(await Manager()),
            Csv(FrameworkRow("FW-1", "First"), FrameworkRow("FW-2", "Second"), CompetencyRow("C1")));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal("First", framework.Name);
        Assert.Equal("C1", framework.Competencies.Single().IdNumber);
    }

    /// <summary>
    /// The flag is matched against the literal string "1" after trimming, so every other way of writing
    /// true reads as false and the row becomes a competency instead - leaving the file with no framework
    /// row at all.
    /// </summary>
    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    [InlineData("01")]
    [InlineData("1.0")]
    [InlineData("-1")]
    public async Task Import_WithAnIsFrameworkFlagThatIsNotTheDigitOne_Is500(string flag)
    {
        var response = await ImportCsv(
            Client(await Manager()), Csv(Row(idNumber: "FW-1", shortName: "F", isFramework: flag)));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains(
            "CSV does not contain a framework row (Is Framework = 1).", (await ReadError(response)).Title);
    }

    [Fact]
    public async Task Import_WithAPaddedIsFrameworkFlag_ReadsItAsTheFrameworkRow()
    {
        var response = await ImportCsv(
            Client(await Manager()), Csv(Row(idNumber: "FW-1", shortName: "F", isFramework: "  1  ")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // CSV - the competency rows
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Import_CreatesTheCompetenciesInFileOrder()
    {
        var response = await ImportCsv(
            Client(await Manager()),
            Csv(FrameworkRow("FW-1"), CompetencyRow("C3"), CompetencyRow("C1"), CompetencyRow("C2")));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal(
            ["C3", "C1", "C2"],
            framework.Competencies.OrderBy(c => c.SortOrder).Select(c => c.IdNumber));
        Assert.Equal([0, 1, 2], framework.Competencies.Select(c => c.SortOrder).Order());
    }

    [Fact]
    public async Task Import_ReadsEveryCompetencyColumn()
    {
        var response = await ImportCsv(
            Client(await Manager()),
            Csv(FrameworkRow("FW-1"), Row(
                idNumber: "C1",
                shortName: "Read a packet capture",
                description: "<p>Longer text</p>",
                descriptionFormat: "1",
                scaleValues: "[\"No\",\"Yes\"]",
                scaleConfiguration: "{\"scaleid\":3}",
                ruleType: "core_competency\\competency_rule_all",
                ruleOutcome: "2",
                ruleConfig: "{}")));

        var competency = (await Read<CompetencyFramework>(response)).Competencies.Single();
        Assert.Equal("C1", competency.IdNumber);
        Assert.Equal("Read a packet capture", competency.ShortName);
        Assert.Equal("<p>Longer text</p>", competency.Description);
        Assert.Equal(1, competency.DescriptionFormat);
        Assert.Equal("[\"No\",\"Yes\"]", competency.ScaleValues);
        Assert.Equal("{\"scaleid\":3}", competency.ScaleConfiguration);
        Assert.Equal("core_competency\\competency_rule_all", competency.RuleType);
        Assert.Equal(2, competency.RuleOutcome);
        Assert.Equal("{}", competency.RuleConfig);
    }

    /// <summary>
    /// The two numeric columns are parsed leniently: anything that is not an integer reads as zero rather
    /// than failing the import, which is right for a file a human may have edited in a spreadsheet.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("none")]
    [InlineData("2.5")]
    public async Task Import_WithAnUnparsableNumber_ReadsItAsZero(string value)
    {
        var response = await ImportCsv(
            Client(await Manager()),
            Csv(FrameworkRow("FW-1"), Row(
                idNumber: "C1", shortName: "One", descriptionFormat: value, ruleOutcome: value)));

        var competency = (await Read<CompetencyFramework>(response)).Competencies.Single();
        Assert.Equal(0, competency.DescriptionFormat);
        Assert.Equal(0, competency.RuleOutcome);
    }

    [Theory]
    [InlineData("C1 - Read a packet capture")]
    [InlineData("C1- Read a packet capture")]
    [InlineData("c1 - Read a packet capture")]
    [InlineData("C1 -    Read a packet capture")]
    public async Task Import_StripsARedundantIdNumberPrefixFromTheShortName(string shortName)
    {
        var response = await ImportCsv(
            Client(await Manager()),
            Csv(FrameworkRow("FW-1"), Row(idNumber: "C1", shortName: shortName)));

        Assert.Equal(
            "Read a packet capture",
            (await Read<CompetencyFramework>(response)).Competencies.Single().ShortName);
    }

    /// <summary>
    /// Only the two separators the exports actually use are stripped, so a name that merely begins with
    /// the ID number is left alone - "C1x" is not "C1" with a separator, and a name like "C1: Something"
    /// keeps its prefix.
    /// </summary>
    [Theory]
    [InlineData("C1Read a packet capture")]
    [InlineData("C1: Read a packet capture")]
    [InlineData("C1 Read a packet capture")]
    [InlineData("C1  - Read a packet capture")]
    public async Task Import_KeepsAShortNameThatDoesNotCarryTheExactPrefix(string shortName)
    {
        var response = await ImportCsv(
            Client(await Manager()),
            Csv(FrameworkRow("FW-1"), Row(idNumber: "C1", shortName: shortName)));

        Assert.Equal(
            shortName, (await Read<CompetencyFramework>(response)).Competencies.Single().ShortName);
    }

    /// <summary>
    /// Characterizes a silent drop. A row with no ID number cannot be referred to by a parent or a
    /// cross-reference, so keeping it would produce an unreachable competency - but the import answers 201
    /// and says nothing, so the only evidence is the count.
    /// </summary>
    [Theory]
    [InlineData("", "One")]
    [InlineData("   ", "One")]
    [InlineData("C1", "")]
    [InlineData("C1", "   ")]
    public async Task Import_SkipsARowWithNoIdNumberOrNoShortName(string idNumber, string shortName)
    {
        var response = await ImportCsv(
            Client(await Manager()),
            Csv(FrameworkRow("FW-1"), Row(idNumber: idNumber, shortName: shortName), CompetencyRow("C9")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("C9", (await Read<CompetencyFramework>(response)).Competencies.Single().IdNumber);
    }

    /// <summary>
    /// Characterizes a silent drop. Two rows with one ID number would violate the unique index, so the
    /// second is discarded rather than failing the upload - which means a file where a human duplicated a
    /// row imports the version they were replacing, not the one they wrote.
    /// </summary>
    [Fact]
    public async Task Import_KeepsOnlyTheFirstRowForADuplicateIdNumber()
    {
        var response = await ImportCsv(
            Client(await Manager()),
            Csv(FrameworkRow("FW-1"), CompetencyRow("C1", "The first"), CompetencyRow("C1", "The second")));

        var competency = (await Read<CompetencyFramework>(response)).Competencies.Single();
        Assert.Equal("The first", competency.ShortName);
    }

    /// <summary>
    /// Characterizes a silent drop, and the one most likely to bite: the ID numbers are compared exactly,
    /// so a file is only deduplicated for an exact repeat, and two rows differing in case become two
    /// competencies.
    /// </summary>
    [Fact]
    public async Task Import_TreatsIdNumbersDifferingInCaseAsTwoCompetencies()
    {
        var response = await ImportCsv(
            Client(await Manager()),
            Csv(FrameworkRow("FW-1"), CompetencyRow("C1", "Upper"), CompetencyRow("c1", "Lower")));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal(2, framework.Competencies.Count);
        Assert.Equal(
            ["C1", "c1"],
            framework.Competencies.Select(c => c.IdNumber).OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>
    /// Characterizes a silent drop in the parser rather than in the importer. A short line is skipped
    /// without a word, which is what makes a truncated or wrongly-delimited export import as a framework
    /// with no competencies at all instead of as an error. Thirteen fields is one too few; the fourteenth,
    /// Taxonomy, is only ever read from the framework row.
    /// </summary>
    [Theory]
    [InlineData(",C1,One")]
    [InlineData(",C1,One,,0,,,,,,,,")]
    public async Task Import_SkipsARowWithTooFewFields(string row)
    {
        var response = await ImportCsv(
            Client(await Manager()), Csv(FrameworkRow("FW-1"), row, CompetencyRow("C9")));

        Assert.Equal("C9", (await Read<CompetencyFramework>(response)).Competencies.Single().IdNumber);
    }

    /// <summary>
    /// And exactly fourteen is enough, which is what makes the boundary above worth two cases rather than
    /// one.
    /// </summary>
    [Fact]
    public async Task Import_AcceptsARowWithExactlyFourteenFields()
    {
        var response = await ImportCsv(
            Client(await Manager()), Csv(FrameworkRow("FW-1"), ",C1,One,,0,,,,,,,,,"));

        Assert.Equal("C1", (await Read<CompetencyFramework>(response)).Competencies.Single().IdNumber);
    }

    /// <summary>
    /// Extra columns are ignored rather than rejected, so a Moodle export gaining a fifteenth column still
    /// imports.
    /// </summary>
    [Fact]
    public async Task Import_IgnoresColumnsBeyondTheFourteenth()
    {
        var response = await ImportCsv(
            Client(await Manager()),
            Csv(FrameworkRow("FW-1"), CompetencyRow("C1") + ",something,else"));

        Assert.Equal("C1", (await Read<CompetencyFramework>(response)).Competencies.Single().IdNumber);
    }

    // ---------------------------------------------------------------------------------------------
    // CSV - hierarchy and cross-references
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Import_SetsTheParentFromTheParentIdNumber()
    {
        var response = await ImportCsv(
            Client(await Manager()),
            Csv(FrameworkRow("FW-1"), CompetencyRow("C1"), CompetencyRow("C1.1", parent: "C1")));

        var framework = await Read<CompetencyFramework>(response);
        var parent = Competency(framework, "C1");
        var child = Competency(framework, "C1.1");
        Assert.Null(parent.ParentId);
        Assert.Equal(parent.Id, child.ParentId);
    }

    /// <summary>
    /// A parent may appear after its child - the file is read into memory before any parent is resolved -
    /// so an export ordered by ID number rather than by depth imports correctly.
    /// </summary>
    [Fact]
    public async Task Import_ResolvesAParentDeclaredAfterItsChild()
    {
        var response = await ImportCsv(
            Client(await Manager()),
            Csv(FrameworkRow("FW-1"), CompetencyRow("C1.1", parent: "C1"), CompetencyRow("C1")));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal(Competency(framework, "C1").Id, Competency(framework, "C1.1").ParentId);
    }

    [Fact]
    public async Task Import_BuildsThePathFromTheHierarchy()
    {
        var response = await ImportCsv(
            Client(await Manager()),
            Csv(
                FrameworkRow("FW-1"),
                CompetencyRow("C1"),
                CompetencyRow("C1.1", parent: "C1"),
                CompetencyRow("C1.1.1", parent: "C1.1")));

        var framework = await Read<CompetencyFramework>(response);
        var root = Competency(framework, "C1");
        var middle = Competency(framework, "C1.1");
        var leaf = Competency(framework, "C1.1.1");
        Assert.Equal($"/{root.Id}", root.Path);
        Assert.Equal($"/{root.Id}/{middle.Id}", middle.Path);
        Assert.Equal($"/{root.Id}/{middle.Id}/{leaf.Id}", leaf.Path);
    }

    /// <summary>
    /// Characterizes a silent drop. A parent ID number the file does not define leaves the competency at
    /// the root of the tree rather than failing the import, so a partial export - one branch of a
    /// framework - imports as a flat list.
    /// </summary>
    [Fact]
    public async Task Import_IgnoresAParentIdNumberItCannotResolve()
    {
        var response = await ImportCsv(
            Client(await Manager()),
            Csv(FrameworkRow("FW-1"), CompetencyRow("C1.1", parent: "C1")));

        var competency = (await Read<CompetencyFramework>(response)).Competencies.Single();
        Assert.Null(competency.ParentId);
        Assert.Equal($"/{competency.Id}", competency.Path);
    }

    /// <summary>
    /// A parent cycle - which an edited file can easily contain - is walked once and then stopped, rather
    /// than looping forever building a path. Both competencies keep their parent; only the path is
    /// truncated.
    /// </summary>
    [Fact]
    public async Task Import_StopsBuildingThePathAtAParentCycle()
    {
        var response = await ImportCsv(
            Client(await Manager()),
            Csv(
                FrameworkRow("FW-1"),
                CompetencyRow("C1", parent: "C2"),
                CompetencyRow("C2", parent: "C1")));

        var framework = await Read<CompetencyFramework>(response);
        var one = Competency(framework, "C1");
        var two = Competency(framework, "C2");
        Assert.Equal(two.Id, one.ParentId);
        Assert.Equal(one.Id, two.ParentId);
        Assert.Equal($"/{two.Id}/{one.Id}", one.Path);
        Assert.Equal($"/{one.Id}/{two.Id}", two.Path);
    }

    /// <summary>
    /// A competency that is its own parent survives for the same reason, which is worth pinning because it
    /// is the shape a spreadsheet fill-down produces.
    /// </summary>
    [Fact]
    public async Task Import_AcceptsACompetencyThatIsItsOwnParent()
    {
        var response = await ImportCsv(
            Client(await Manager()), Csv(FrameworkRow("FW-1"), CompetencyRow("C1", parent: "C1")));

        var competency = (await Read<CompetencyFramework>(response)).Competencies.Single();
        Assert.Equal(competency.Id, competency.ParentId);
        Assert.Equal($"/{competency.Id}", competency.Path);
    }

    [Fact]
    public async Task Import_CreatesTheCrossReferenceRelationships()
    {
        var response = await ImportCsv(
            Client(await Manager()),
            Csv(
                FrameworkRow("FW-1"),
                CompetencyRow("C1", related: "C2,C3"),
                CompetencyRow("C2"),
                CompetencyRow("C3")));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal(["C2", "C3"], Competency(framework, "C1").RelatedIdNumbers.Order());
        Assert.Equal(2, await NewContext().CompetencyRelationships.CountAsync(Ct));
    }

    /// <summary>
    /// The rows are written one way round only - the file says C1 relates to C2 and that is the single row
    /// stored - but the read reports the union of both directions, so C2 relates to C1 without a second
    /// row existing. That is why an export/import round-trip does not double the relationship count.
    /// </summary>
    [Fact]
    public async Task Import_StoresARelationshipOnceAndReportsItBothWays()
    {
        var response = await ImportCsv(
            Client(await Manager()),
            Csv(FrameworkRow("FW-1"), CompetencyRow("C1", related: "C2"), CompetencyRow("C2")));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal(["C2"], Competency(framework, "C1").RelatedIdNumbers);
        Assert.Equal(["C1"], Competency(framework, "C2").RelatedIdNumbers);

        var stored = await NewContext().CompetencyRelationships.SingleAsync(Ct);
        Assert.Equal(Competency(framework, "C1").Id, stored.CompetencyId);
        Assert.Equal(Competency(framework, "C2").Id, stored.RelatedCompetencyId);
    }

    /// <summary>
    /// Moodle escapes a comma inside an ID number as <c>%2C</c>, because the column itself is
    /// comma-separated. Unescaping it is what lets a framework whose ID numbers contain commas - DCWF's do
    /// - keep its cross-references.
    /// </summary>
    [Fact]
    public async Task Import_UnescapesACommaInARelatedIdNumber()
    {
        var response = await ImportCsv(
            Client(await Manager()),
            Csv(FrameworkRow("FW-1"), CompetencyRow("C1", related: "A%2CB"), CompetencyRow("A,B")));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal(["A,B"], Competency(framework, "C1").RelatedIdNumbers);
    }

    [Fact]
    public async Task Import_IgnoresBlanksAndRepeatsInTheRelatedIdNumbers()
    {
        var response = await ImportCsv(
            Client(await Manager()),
            Csv(FrameworkRow("FW-1"), CompetencyRow("C1", related: "C2, ,C2,,  C2  "), CompetencyRow("C2")));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal(["C2"], Competency(framework, "C1").RelatedIdNumbers);
        Assert.Equal(1, await NewContext().CompetencyRelationships.CountAsync(Ct));
    }

    /// <summary>
    /// The pair is deduplicated as well as the ID number, so a file naming the relationship from both ends
    /// - which an export of a bidirectional link produces - stores one row rather than colliding on the
    /// unique index and failing the whole import with a 409.
    /// </summary>
    [Fact]
    public async Task Import_StoresOneRowWhenBothEndsNameEachOther()
    {
        var response = await ImportCsv(
            Client(await Manager()),
            Csv(
                FrameworkRow("FW-1"),
                CompetencyRow("C1", related: "C2"),
                CompetencyRow("C2", related: "C1")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Both directions are present in the file, so both rows are wanted and neither is a duplicate of
        // the other: the pair set is keyed on the ordered pair.
        Assert.Equal(2, await NewContext().CompetencyRelationships.CountAsync(Ct));
    }

    /// <summary>
    /// Characterizes a silent drop. A related ID number the file does not define is discarded, so a
    /// framework exported one branch at a time loses every cross-reference that pointed out of the branch.
    /// </summary>
    [Fact]
    public async Task Import_IgnoresARelatedIdNumberItCannotResolve()
    {
        var response = await ImportCsv(
            Client(await Manager()),
            Csv(FrameworkRow("FW-1"), CompetencyRow("C1", related: "C2,C404"), CompetencyRow("C2")));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal(["C2"], Competency(framework, "C1").RelatedIdNumbers);
        Assert.Equal(1, await NewContext().CompetencyRelationships.CountAsync(Ct));
    }

    /// <summary>
    /// Characterizes a gap. Nothing excludes a competency from relating to itself, so the file's own
    /// mistake is stored - the same gap the hand-editing endpoints have. See
    /// <c>CompetencyEndpointTests.Create_AcceptsACompetencyRelatedToItself</c>.
    /// </summary>
    [Fact]
    public async Task Import_AcceptsACompetencyRelatedToItself()
    {
        var response = await ImportCsv(
            Client(await Manager()), Csv(FrameworkRow("FW-1"), CompetencyRow("C1", related: "C1")));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal(["C1"], Competency(framework, "C1").RelatedIdNumbers);
        Assert.Equal(1, await NewContext().CompetencyRelationships.CountAsync(Ct));
    }

    /// <summary>
    /// A cross-reference from a row that was itself skipped goes nowhere, rather than throwing on a
    /// dictionary lookup for the blank ID number.
    /// </summary>
    [Fact]
    public async Task Import_IgnoresTheRelatedIdNumbersOfASkippedRow()
    {
        var response = await ImportCsv(
            Client(await Manager()),
            Csv(FrameworkRow("FW-1"), Row(idNumber: "", shortName: "One", relatedIdNumbers: "C2"),
                CompetencyRow("C2")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Empty(await NewContext().CompetencyRelationships.ToListAsync(Ct));
    }

    /// <summary>
    /// Characterizes a defect, and the compounding cost of the silent skip in
    /// <see cref="Import_KeepsOnlyTheFirstRowForADuplicateIdNumber"/>. The relationship pass walks the
    /// <em>rows</em> and looks each one's ID number up in the entity map, so a discarded duplicate still
    /// finds an entity - the one that beat it - and its cross-references are grafted onto that. The
    /// competency the file described was thrown away; the relationships it declared were not, and now
    /// belong to a different competency that never claimed them.
    /// </summary>
    [Fact]
    public async Task Import_GraftsADiscardedDuplicateRowsRelationshipsOntoTheRowThatWon()
    {
        var response = await ImportCsv(
            Client(await Manager()),
            Csv(
                FrameworkRow("FW-1"),
                CompetencyRow("C1", "The first"),
                Row(idNumber: "C1", shortName: "The second", relatedIdNumbers: "C2"),
                CompetencyRow("C2")));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal("The first", Competency(framework, "C1").ShortName);
        Assert.Equal(["C2"], Competency(framework, "C1").RelatedIdNumbers);
        Assert.Equal(1, await NewContext().CompetencyRelationships.CountAsync(Ct));
    }

    /// <summary>
    /// The same walk means a row skipped for having no short name is not skipped by the relationship pass,
    /// because that pass tests only the ID number - so a row too incomplete to become a competency can
    /// still create a relationship between two others.
    /// </summary>
    [Fact]
    public async Task Import_AppliesTheRelationshipsOfARowSkippedForItsShortName()
    {
        var response = await ImportCsv(
            Client(await Manager()),
            Csv(
                FrameworkRow("FW-1"),
                CompetencyRow("C1", "The real one"),
                Row(idNumber: "C1", shortName: "", relatedIdNumbers: "C2"),
                CompetencyRow("C2")));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal("The real one", Competency(framework, "C1").ShortName);
        Assert.Equal(["C2"], Competency(framework, "C1").RelatedIdNumbers);
    }

    // ---------------------------------------------------------------------------------------------
    // CSV - the parser
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Moodle exports sometimes arrive with HTML or metadata ahead of the header, so everything before the
    /// line naming the first column is skipped.
    /// </summary>
    [Fact]
    public async Task Import_SkipsLinesBeforeTheHeader()
    {
        var csv = "<html><body>\nGenerated 2026-01-01\n" + Csv(FrameworkRow("FW-1"), CompetencyRow("C1"));

        var response = await ImportCsv(Client(await Manager()), csv);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("C1", (await Read<CompetencyFramework>(response)).Competencies.Single().IdNumber);
    }

    /// <summary>
    /// Junk on the same line as the header is tolerated too, though not for the reason the parser thinks.
    /// </summary>
    /// <remarks>
    /// The parser strips everything before <c>Parent ID number</c> off that line and then never looks at
    /// the line again - the loop that found the header breaks, and the next statement reads the next
    /// line - so the strip is dead code and deleting it would not change a single value observed here.
    /// The case works because the header is discarded whatever it contains: only its position matters.
    /// That also means junk on the header line is tolerated even when it contains commas or quotes.
    /// </remarks>
    [Fact]
    public async Task Import_ToleratesJunkOnTheHeaderLine()
    {
        var csv = "<pre>" + Csv(FrameworkRow("FW-1"), CompetencyRow("C1"));

        var response = await ImportCsv(Client(await Manager()), csv);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("C1", (await Read<CompetencyFramework>(response)).Competencies.Single().IdNumber);
    }

    [Fact]
    public async Task Import_MatchesTheHeaderCaseInsensitively()
    {
        var csv = Csv(FrameworkRow("FW-1")).Replace("Parent ID number", "PARENT ID NUMBER");

        var response = await ImportCsv(Client(await Manager()), csv);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Import_SkipsBlankLines()
    {
        var csv = Csv(FrameworkRow("FW-1")) + "\n\n   \n" + CompetencyRow("C1") + "\n\n";

        var response = await ImportCsv(Client(await Manager()), csv);

        Assert.Equal("C1", (await Read<CompetencyFramework>(response)).Competencies.Single().IdNumber);
    }

    [Fact]
    public async Task Import_ReadsAQuotedFieldContainingCommasAndQuotes()
    {
        var response = await ImportCsv(
            Client(await Manager()),
            Csv(FrameworkRow("FW-1"), Row(
                idNumber: "C1", shortName: "Detect, and \"report\", an intrusion")));

        Assert.Equal(
            "Detect, and \"report\", an intrusion",
            (await Read<CompetencyFramework>(response)).Competencies.Single().ShortName);
    }

    [Fact]
    public async Task Import_ReadsTheFileAsUtf8()
    {
        var response = await ImportCsv(
            Client(await Manager()),
            Csv(FrameworkRow("FW-1"), CompetencyRow("C1", "Sécurité des réseaux — 网络")));

        Assert.Equal(
            "Sécurité des réseaux — 网络",
            (await Read<CompetencyFramework>(response)).Competencies.Single().ShortName);
    }

    /// <summary>
    /// Characterizes a defect. RFC 4180 lets a quoted field contain a line break, but the parser reads a
    /// line at a time and only then splits it - so a multi-line field becomes two short lines, both of
    /// which are silently skipped for having fewer than fourteen fields. A Moodle export whose description
    /// column contains a paragraph break therefore imports as a framework with no competencies, and says
    /// 201.
    /// </summary>
    [Fact]
    public async Task Import_DiscardsARowWhoseQuotedFieldSpansTwoLines()
    {
        var csv = Csv(FrameworkRow("FW-1")) + "\n,C1,One,\"first line\nsecond line\",0,,,,,,,,,";

        var response = await ImportCsv(Client(await Manager()), csv);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Empty((await Read<CompetencyFramework>(response)).Competencies);
    }

    // ---------------------------------------------------------------------------------------------
    // CSV - refusals
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A multipart body with no parts at all never reaches the action: model binding cannot produce the
    /// <c>IFormFile</c> and <c>ValidateModelStateFilter</c> answers with problem details, so the
    /// controller's own "No file provided." is reserved for the empty-file case below. Both are 400, so
    /// nothing a client does depends on the difference - but a test asserting the message has to know which
    /// of the two it is reading.
    /// </summary>
    [Fact]
    public async Task Import_WithAnEmptyMultipartBody_Is400()
    {
        using var content = new MultipartFormDataContent();

        var response = await Client(await Manager())
            .PostAsync("api/competencyframeworks/import", content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("\"errors\"", body);
        Assert.DoesNotContain("No file provided.", body);
    }

    [Fact]
    public async Task Import_WithAnEmptyFile_Is400()
    {
        var response = await ImportCsv(Client(await Manager()), "");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("No file provided.", await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>
    /// Characterizes the wrong status. A file that is not a Moodle CSV at all is the caller's mistake, but
    /// it is reported as a server error: the importer throws a bare <c>ArgumentException</c>, which is not
    /// an <c>IApiException</c>, so <c>JsonExceptionFilter</c> answers 500. The message is at least the
    /// right one, and reaches <c>Title</c> because the harness runs in Development.
    /// </summary>
    [Theory]
    [InlineData("not a csv at all")]
    [InlineData("a,b,c\n1,2,3")]
    public async Task Import_WithAFileThatHasNoHeaderLine_Is500(string csv)
    {
        var response = await ImportCsv(Client(await Manager()), csv);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("CSV file is empty or has no data rows.", (await ReadError(response)).Title);
    }

    [Fact]
    public async Task Import_WithOnlyAHeaderLine_Is500()
    {
        var response = await ImportCsv(Client(await Manager()), Csv());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("CSV file is empty or has no data rows.", (await ReadError(response)).Title);
    }

    [Fact]
    public async Task Import_WithNoFrameworkRow_Is500()
    {
        var response = await ImportCsv(Client(await Manager()), Csv(CompetencyRow("C1")));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains(
            "CSV does not contain a framework row (Is Framework = 1).", (await ReadError(response)).Title);
        Assert.Empty(await NewContext().CompetencyFrameworks.ToListAsync(Ct));
    }

    /// <summary>
    /// Everything is written in one transaction, so a framework whose competencies fail to save leaves no
    /// half-imported framework behind.
    /// </summary>
    [Fact]
    public async Task Import_WhenTheFrameworkIsRefused_StoresNothing()
    {
        await Seed(BlueprintAppFactory.CompetencyFramework(idNumber: "FW-1"));

        await ImportCsv(
            Client(await Manager()), Csv(FrameworkRow("FW-1"), CompetencyRow("C1"), CompetencyRow("C2")));

        Assert.Empty(await NewContext().Competencies.ToListAsync(Ct));
    }

    /// <summary>
    /// Characterizes a gap in the UI's view of the world. Importing a framework broadcasts nothing over
    /// SignalR - there is no <c>CompetencyFrameworkHandler</c> among the twenty-five event handlers - so a
    /// second administrator with the framework list open sees no sign of it until they reload. Every other
    /// area of the API broadcasts its writes.
    /// </summary>
    [Fact]
    public async Task Import_BroadcastsNothing()
    {
        await ImportCsv(Client(await Manager()), Csv(FrameworkRow("FW-1"), CompetencyRow("C1")));

        Assert.Empty(Hub.Sends);
    }

    // ---------------------------------------------------------------------------------------------
    // JSON - which importer the file is dispatched to
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// One endpoint serves two unrelated formats, told apart by shape rather than by a parameter: an
    /// object with a <c>competencies</c> array is one of blueprint's own exports and goes to the create
    /// path, and anything else is assumed to be NICE.
    /// </summary>
    [Theory]
    [InlineData("competencies")]
    [InlineData("Competencies")]
    public async Task ImportJson_WithANativeExport_CreatesTheFramework(string property)
    {
        var json = $$"""
            {"name":"Native","idNumber":"FW-1","source":"SEI","version":"2.0",
             "{{property}}":[{"idNumber":"C1","shortName":"One","sortOrder":7}]}
            """;

        var response = await ImportJson(Client(await Manager()), json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal("Native", framework.Name);
        Assert.Equal("FW-1", framework.IdNumber);
        Assert.Equal("SEI", framework.Source);
        Assert.Equal("2.0", framework.Version);
        var competency = framework.Competencies.Single();
        Assert.Equal("C1", competency.IdNumber);
        Assert.Equal("One", competency.ShortName);
        Assert.Equal(7, competency.SortOrder);
    }

    /// <summary>
    /// An export with no competencies is still an export: the array is empty, not absent, so it does not
    /// fall through to the NICE parser.
    /// </summary>
    [Fact]
    public async Task ImportJson_WithAnEmptyCompetenciesArray_IsANativeExport()
    {
        var response = await ImportJson(
            Client(await Manager()), """{"name":"Native","idNumber":"FW-1","competencies":[]}""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Empty((await Read<CompetencyFramework>(response)).Competencies);
    }

    /// <summary>
    /// Numbers written as strings are accepted, deliberately, so an export produced by a tool that
    /// stringifies everything still round-trips.
    /// </summary>
    [Fact]
    public async Task ImportJson_ReadsANumberWrittenAsAString()
    {
        var json = """
            {"name":"Native","idNumber":"FW-1","descriptionFormat":"1",
             "competencies":[{"idNumber":"C1","shortName":"One","sortOrder":"9","ruleOutcome":"2"}]}
            """;

        var response = await ImportJson(Client(await Manager()), json);

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal(1, framework.DescriptionFormat);
        Assert.Equal(9, framework.Competencies.Single().SortOrder);
        Assert.Equal(2, framework.Competencies.Single().RuleOutcome);
    }

    /// <summary>
    /// Characterizes an unhelpful failure. A file that explicitly nulls its competencies - a legal way to
    /// write "this framework is empty" - is not an object with a <c>competencies</c> array, so it is handed
    /// to the NICE parser, which fails on the first property it looks for. The caller gets a 500 whose
    /// whole message is "The given key was not present in the dictionary." - <c>JsonElement.GetProperty</c>
    /// naming neither the property it wanted nor the file it was reading.
    /// </summary>
    [Fact]
    public async Task ImportJson_WithANullCompetenciesProperty_IsHandedToTheNiceParser()
    {
        var response = await ImportJson(
            Client(await Manager()), """{"name":"Native","idNumber":"FW-1","competencies":null}""");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("The given key was not present in the dictionary.", (await ReadError(response)).Title);
    }

    /// <summary>
    /// Same for a file whose root is an array rather than an object: not a native export, so it goes to
    /// NICE and fails there.
    /// </summary>
    [Fact]
    public async Task ImportJson_WithAnArrayAtTheRoot_IsHandedToTheNiceParser()
    {
        var response = await ImportJson(Client(await Manager()), """[{"idNumber":"C1"}]""");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task ImportJson_WithMalformedJson_Is500()
    {
        var response = await ImportJson(Client(await Manager()), "{not json");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task ImportJson_WithAnEmptyMultipartBody_Is400()
    {
        using var content = new MultipartFormDataContent();

        var response = await Client(await Manager())
            .PostAsync("api/competencyframeworks/import-json", content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ImportJson_WithAnEmptyFile_Is400()
    {
        var response = await ImportJson(Client(await Manager()), "");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // JSON - the native export path
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The ids in an export belong to the installation it came from, so every competency gets a fresh one
    /// and parent references are remapped onto them. Without that, re-importing an export while the
    /// original is still present would collide on the primary key.
    /// </summary>
    [Fact]
    public async Task ImportJson_AssignsFreshIdsAndRemapsTheParentReferences()
    {
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var json = $$"""
            {"name":"Native","idNumber":"FW-1","competencies":[
              {"id":"{{parentId}}","idNumber":"C1","shortName":"Parent"},
              {"id":"{{childId}}","idNumber":"C1.1","shortName":"Child","parentId":"{{parentId}}"}]}
            """;

        var response = await ImportJson(Client(await Manager()), json);

        var framework = await Read<CompetencyFramework>(response);
        var parent = Competency(framework, "C1");
        var child = Competency(framework, "C1.1");
        Assert.NotEqual(parentId, parent.Id);
        Assert.NotEqual(childId, child.Id);
        Assert.Equal(parent.Id, child.ParentId);
        Assert.Equal($"/{parent.Id}/{child.Id}", child.Path);
    }

    /// <summary>
    /// And a parent reference to a competency the file does not contain is dropped rather than kept as a
    /// dangling foreign key - the same salvage the CSV importer performs, by a different mechanism.
    /// </summary>
    [Fact]
    public async Task ImportJson_DropsAParentReferenceToACompetencyNotInTheFile()
    {
        var json = $$"""
            {"name":"Native","idNumber":"FW-1","competencies":[
              {"idNumber":"C1.1","shortName":"Child","parentId":"{{Guid.NewGuid()}}"}]}
            """;

        var response = await ImportJson(Client(await Manager()), json);

        Assert.Null((await Read<CompetencyFramework>(response)).Competencies.Single().ParentId);
    }

    [Fact]
    public async Task ImportJson_KeepsOnlyTheFirstCompetencyForADuplicateIdNumber()
    {
        var json = """
            {"name":"Native","idNumber":"FW-1","competencies":[
              {"idNumber":"C1","shortName":"The first"},
              {"idNumber":"C1","shortName":"The second"}]}
            """;

        var response = await ImportJson(Client(await Manager()), json);

        Assert.Equal(
            "The first", (await Read<CompetencyFramework>(response)).Competencies.Single().ShortName);
    }

    [Fact]
    public async Task ImportJson_ResolvesTheRelatedIdNumbers()
    {
        var json = """
            {"name":"Native","idNumber":"FW-1","competencies":[
              {"idNumber":"C1","shortName":"One","relatedIdNumbers":["C2"," ","C2","C404"]},
              {"idNumber":"C2","shortName":"Two"}]}
            """;

        var response = await ImportJson(Client(await Manager()), json);

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal(["C2"], Competency(framework, "C1").RelatedIdNumbers);
        Assert.Equal(1, await NewContext().CompetencyRelationships.CountAsync(Ct));
    }

    [Fact]
    public async Task ImportJson_ForAFrameworkIdNumberAlreadyPresent_Is409()
    {
        await Seed(BlueprintAppFactory.CompetencyFramework(idNumber: "FW-1"));

        var response = await ImportJson(
            Client(await Manager()), """{"name":"Native","idNumber":"FW-1","competencies":[]}""");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("FW-1", (await ReadError(response)).Title);
    }

    /// <summary>
    /// Characterizes a gap. The native path has no source-and-version check, only the ID number one - so
    /// an export with no ID number can be imported over and over, each time producing another copy of the
    /// same framework. Nothing in the UI distinguishes them.
    /// </summary>
    [Fact]
    public async Task ImportJson_WithNoIdNumber_CanBeImportedRepeatedly()
    {
        var client = Client(await Manager());
        var json = """{"name":"Native","source":"SEI","version":"2.0","competencies":[]}""";

        var first = await ImportJson(client, json);
        var second = await ImportJson(client, json);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(2, await NewContext().CompetencyFrameworks.CountAsync(Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // JSON - the NICE path
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ImportJson_WithANiceFile_CreatesTheFramework()
    {
        var response = await ImportJson(Client(await Manager()), Nice(
            elements:
            [
                Element("CAT1", "category", title: "Securely Provision"),
                Element("T0001", "task", text: "Acquire and manage resources")
            ]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal("NICE Framework", framework.Name);
        Assert.Equal("NICE-1.0", framework.IdNumber);
        Assert.Equal("NICE", framework.Source);
        Assert.Equal("1.0", framework.Version);
        Assert.Equal("Imported from NICE", framework.Description);
        Assert.Equal(["CAT1", "T0001"], framework.Competencies.Select(c => c.IdNumber).Order());
    }

    /// <summary>
    /// The container holding the three arrays is found in either of two places, because NICE's own
    /// download wraps it and files passed around by hand often do not.
    /// </summary>
    [Fact]
    public async Task ImportJson_WithAWrappedNiceFile_CreatesTheFramework()
    {
        var inner = Nice(elements: [Element("CAT1", "category", title: "Securely Provision")]);

        var response = await ImportJson(Client(await Manager()), $"{{\"response\":{{\"elements\":{inner}}}}}");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("CAT1", (await Read<CompetencyFramework>(response)).Competencies.Single().IdNumber);
    }

    /// <summary>
    /// The NICE importer derives the framework's ID number from the file's own identity rather than from
    /// anything the caller says, and includes the version so successive releases of one framework do not
    /// collide.
    /// </summary>
    [Theory]
    [InlineData("NICE", "1.0", "NICE-1.0")]
    [InlineData("NICE-1.0", "1.0", "NICE-1.0")]
    [InlineData("NICE_v1.0_final", "1.0", "NICE_v1.0_final")]
    [InlineData("NICE", "", "NICE")]
    [InlineData("", "1.0", null)]
    public async Task ImportJson_DerivesTheNiceFrameworkIdNumberFromTheDocument(
        string identifier, string version, string expected)
    {
        var response = await ImportJson(Client(await Manager()), Nice(
            identifier: identifier,
            version: version,
            elements: [Element("CAT1", "category", title: "One")]));

        Assert.Equal(expected, (await Read<CompetencyFramework>(response)).IdNumber);
    }

    /// <summary>
    /// Unlike the CSV importer, the NICE importer also refuses a source and version pair it already holds,
    /// whatever ID number that would produce - so re-importing NICE 1.0 is caught even if the file was
    /// edited.
    /// </summary>
    [Fact]
    public async Task ImportJson_ForAnAlreadyImportedNiceSourceAndVersion_Is409()
    {
        var existing = BlueprintAppFactory.CompetencyFramework(source: "NICE", version: "1.0");
        existing.Name = "NICE, already here";
        await Seed(existing);

        var response = await ImportJson(
            Client(await Manager()), Nice(elements: [Element("CAT1", "category", title: "One")]));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await ReadError(response);
        Assert.Contains("source 'NICE' version '1.0'", error.Title);
        Assert.Contains("NICE, already here", error.Title);
    }

    /// <summary>
    /// A document with neither a source nor a version has nothing that identifies it, so the check is
    /// skipped rather than matching every other such framework.
    /// </summary>
    [Fact]
    public async Task ImportJson_WithNoSourceOrVersion_SkipsTheDuplicateCheck()
    {
        await Seed(BlueprintAppFactory.CompetencyFramework(source: "", version: ""));

        var response = await ImportJson(Client(await Manager()), Nice(
            identifier: "", version: "", elements: [Element("CAT1", "category", title: "One")]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task ImportJson_NumbersTheNiceElementsInFileOrder()
    {
        var response = await ImportJson(Client(await Manager()), Nice(
            elements:
            [
                Element("C", "task", text: "Third"),
                Element("A", "task", text: "First"),
                Element("B", "task", text: "Second")
            ]));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal(
            ["C", "A", "B"], framework.Competencies.OrderBy(c => c.SortOrder).Select(c => c.IdNumber));
    }

    /// <summary>
    /// NICE files carry presentation rows - sort keys and OPM occupation codes - that are not competencies.
    /// They are dropped by element type.
    /// </summary>
    [Fact]
    public async Task ImportJson_SkipsSortAndOpmCodeElements()
    {
        var response = await ImportJson(Client(await Manager()), Nice(
            elements:
            [
                Element("S1", "sort", text: "1"),
                Element("O1", "opm_code", text: "2210"),
                Element("T1", "task", text: "A task")
            ]));

        Assert.Equal("T1", (await Read<CompetencyFramework>(response)).Competencies.Single().IdNumber);
    }

    /// <summary>
    /// Characterizes an inconsistency. The skipped types are matched case-sensitively while the hierarchy
    /// types are matched case-insensitively, both in the same method - so a file writing "Sort" imports its
    /// sort keys as competencies, and an exercise author is offered "1" and "2" to train against.
    /// </summary>
    [Theory]
    [InlineData("Sort")]
    [InlineData("SORT")]
    [InlineData("OPM_Code")]
    public async Task ImportJson_DoesNotSkipASortElementWrittenInADifferentCase(string elementType)
    {
        var response = await ImportJson(Client(await Manager()), Nice(
            elements: [Element("S1", elementType, text: "1"), Element("T1", "task", text: "A task")]));

        Assert.Equal(2, (await Read<CompetencyFramework>(response)).Competencies.Count);
    }

    /// <summary>
    /// The name shown in the UI is the title where there is one, the text where there is not, and the
    /// identifier as a last resort - so a NICE task, which carries only text, is named by its text.
    /// </summary>
    [Theory]
    [InlineData("Securely Provision", "some text", "Securely Provision")]
    [InlineData("", "some text", "some text")]
    [InlineData("   ", "some text", "some text")]
    [InlineData("N/A", "some text", "some text")]
    [InlineData("", "", "T1")]
    [InlineData("N/A", "", "T1")]
    public async Task ImportJson_NamesANiceElementFromItsTitleTextOrIdentifier(
        string title, string text, string expected)
    {
        var response = await ImportJson(Client(await Manager()), Nice(
            elements: [Element("T1", "task", title: title, text: text)]));

        var competency = (await Read<CompetencyFramework>(response)).Competencies.Single();
        Assert.Equal(expected, competency.ShortName);
        Assert.Equal(text, competency.Description);
    }

    /// <summary>
    /// Characterizes a near miss. The "not applicable" placeholder is compared exactly, so a file writing
    /// it any other way names the competency after the placeholder. Real NICE files use "N/A", which is
    /// why this has not bitten.
    /// </summary>
    [Theory]
    [InlineData("n/a")]
    [InlineData("N/A ")]
    [InlineData("NA")]
    public async Task ImportJson_UsesAnUnrecognizedPlaceholderAsTheShortName(string title)
    {
        var response = await ImportJson(Client(await Manager()), Nice(
            elements: [Element("T1", "task", title: title, text: "the real text")]));

        Assert.Equal(
            title, (await Read<CompetencyFramework>(response)).Competencies.Single().ShortName);
    }

    /// <summary>
    /// A relationship between two structural elements is a parent-child link, pointing from the parent to
    /// the child; a relationship involving anything else is a cross-reference. That single rule is how the
    /// whole NICE hierarchy is recovered from a flat list of edges.
    /// </summary>
    [Fact]
    public async Task ImportJson_BuildsTheHierarchyFromRelationshipsBetweenStructuralElements()
    {
        var response = await ImportJson(Client(await Manager()), Nice(
            elements:
            [
                Element("CAT1", "category", title: "Securely Provision"),
                Element("WR1", "work_role", title: "Systems Architect"),
                Element("T1", "task", text: "A task")
            ],
            relationships: [Link("CAT1", "WR1"), Link("WR1", "T1")]));

        var framework = await Read<CompetencyFramework>(response);
        var category = Competency(framework, "CAT1");
        var role = Competency(framework, "WR1");
        var task = Competency(framework, "T1");
        Assert.Null(category.ParentId);
        Assert.Equal(category.Id, role.ParentId);
        Assert.Equal($"/{category.Id}/{role.Id}", role.Path);

        // The work role's tasks are cross-references, not children - a task belongs to many roles.
        Assert.Null(task.ParentId);
        Assert.Equal(["T1"], role.RelatedIdNumbers);
    }

    [Theory]
    [InlineData("CATEGORY", "Work_Role")]
    [InlineData("Category", "WORK ROLE")]
    [InlineData("category", "specialty area")]
    [InlineData("category", "competency_area")]
    public async Task ImportJson_MatchesTheStructuralTypesCaseInsensitively(
        string parentType, string childType)
    {
        var response = await ImportJson(Client(await Manager()), Nice(
            elements: [Element("P", parentType, title: "Parent"), Element("C", childType, title: "Child")],
            relationships: [Link("P", "C")]));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal(Competency(framework, "P").Id, Competency(framework, "C").ParentId);
    }

    [Fact]
    public async Task ImportJson_IgnoresARelationshipNamingAnElementItDoesNotHave()
    {
        var response = await ImportJson(Client(await Manager()), Nice(
            elements: [Element("T1", "task", text: "A task")],
            relationships: [Link("T1", "MISSING"), Link("MISSING", "T1")]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Empty((await Read<CompetencyFramework>(response)).Competencies.Single().RelatedIdNumbers);
        Assert.Empty(await NewContext().CompetencyRelationships.ToListAsync(Ct));
    }

    /// <summary>
    /// A relationship to a skipped element is a relationship to an element the importer does not have, so
    /// dropping the sort rows also drops every edge that pointed at one.
    /// </summary>
    [Fact]
    public async Task ImportJson_IgnoresARelationshipToASkippedElement()
    {
        var response = await ImportJson(Client(await Manager()), Nice(
            elements: [Element("T1", "task", text: "A task"), Element("S1", "sort", text: "1")],
            relationships: [Link("T1", "S1")]));

        Assert.Empty(await NewContext().CompetencyRelationships.ToListAsync(Ct));
    }

    /// <summary>
    /// Characterizes a defect the CSV importer does not share. A repeated element identifier overwrites
    /// the earlier entry rather than being skipped, so where the CSV importer keeps the first of a
    /// duplicate this keeps the last - and it keeps the sort order it allocated to the one it discarded, so
    /// the file's ordering gains a gap.
    /// </summary>
    [Fact]
    public async Task ImportJson_KeepsOnlyTheLastElementForADuplicateIdentifier()
    {
        var response = await ImportJson(Client(await Manager()), Nice(
            elements:
            [
                Element("T1", "task", text: "The first"),
                Element("T1", "task", text: "The second"),
                Element("T2", "task", text: "Another")
            ]));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal(2, framework.Competencies.Count);
        Assert.Equal("The second", Competency(framework, "T1").ShortName);
        Assert.Equal([1, 2], framework.Competencies.Select(c => c.SortOrder).Order());
    }

    /// <summary>
    /// Characterizes a set of unhelpful failures. Every one of the file's required parts is read with an
    /// unguarded lookup, so a file missing any of them - or carrying an empty <c>documents</c> array - is a
    /// 500 naming an internal property rather than a 400 saying what was wrong with the upload.
    /// </summary>
    [Theory]
    [InlineData("""{"elements":[],"relationships":[]}""")]
    [InlineData("""{"documents":[],"elements":[],"relationships":[]}""")]
    [InlineData("""{"documents":[{"name":"N"}],"relationships":[]}""")]
    [InlineData("""{"documents":[{"name":"N"}],"elements":[]}""")]
    [InlineData("""{"documents":[{"name":"N"}],"elements":[{"element_identifier":"T1"}],"relationships":[]}""")]
    public async Task ImportJson_WithANiceFileMissingARequiredPart_Is500(string json)
    {
        var response = await ImportJson(Client(await Manager()), json);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(await NewContext().CompetencyFrameworks.ToListAsync(Ct));
    }

    /// <summary>
    /// A document with no name at all is imported under a default rather than refused, which is the one
    /// place the NICE parser does salvage a file instead of throwing.
    /// </summary>
    [Fact]
    public async Task ImportJson_WithADocumentWithNoName_UsesADefaultName()
    {
        var response = await ImportJson(
            Client(await Manager()),
            """{"documents":[{"doc_identifier":"SRC","version":"1.0"}],"elements":[],"relationships":[]}""");

        Assert.Equal("Imported Framework", (await Read<CompetencyFramework>(response)).Name);
    }

    // ---------------------------------------------------------------------------------------------
    // Import progress
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetImportStatus_ForAnUnknownId_Is404()
    {
        var response = await Status(Client(await Manager()), Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetImportStatus_AfterASuccessfulImport_ReportsTheFramework()
    {
        var client = Client(await Manager());
        var importId = Guid.NewGuid();
        var before = DateTime.UtcNow;

        var imported = await Read<CompetencyFramework>(await ImportCsv(
            client, Csv(FrameworkRow("FW-1", "My Framework"), CompetencyRow("C1")), importId: importId));

        var status = await Read<CompetencyFrameworkImportStatus>(await Status(client, importId));
        Assert.Equal(importId, status.Id);
        Assert.Equal(CompetencyFrameworkImportState.Succeeded, status.State);
        Assert.Equal("Complete", status.Phase);
        Assert.Equal(100, status.PercentComplete);
        Assert.Equal(imported.Id, status.FrameworkId);
        Assert.Equal("My Framework", status.FrameworkName);
        Assert.Null(status.Error);
        Assert.InRange(status.StartedAt, before, DateTime.UtcNow);
        Assert.NotNull(status.CompletedAt);
    }

    /// <summary>
    /// Every importer reports the same six phases, so a client can render "step n of 6" without knowing
    /// which format it uploaded.
    /// </summary>
    [Fact]
    public async Task GetImportStatus_ReportsSixPhases()
    {
        var client = Client(await Manager());
        var importId = Guid.NewGuid();
        await ImportCsv(client, Csv(FrameworkRow("FW-1")), importId: importId);

        var status = await Read<CompetencyFrameworkImportStatus>(await Status(client, importId));

        Assert.Equal(6, status.PhaseCount);
        Assert.Equal(6, status.PhaseNumber);
    }

    /// <summary>
    /// A failed import stays readable, carrying the exception's own message - which is the only place a
    /// client can read why an import failed once it has navigated away from the response.
    /// </summary>
    [Fact]
    public async Task GetImportStatus_AfterAFailedImport_ReportsTheError()
    {
        var client = Client(await Manager());
        var importId = Guid.NewGuid();
        await ImportCsv(client, Csv(CompetencyRow("C1")), importId: importId);

        var status = await Read<CompetencyFrameworkImportStatus>(await Status(client, importId));
        Assert.Equal(CompetencyFrameworkImportState.Failed, status.State);
        Assert.Equal("CSV does not contain a framework row (Is Framework = 1).", status.Error);
        Assert.Null(status.FrameworkId);
        Assert.NotNull(status.CompletedAt);
    }

    [Fact]
    public async Task GetImportStatus_AfterARefusedImport_ReportsTheConflict()
    {
        await Seed(BlueprintAppFactory.CompetencyFramework(idNumber: "FW-1"));
        var client = Client(await Manager());
        var importId = Guid.NewGuid();
        await ImportCsv(client, Csv(FrameworkRow("FW-1")), importId: importId);

        var status = await Read<CompetencyFrameworkImportStatus>(await Status(client, importId));
        Assert.Equal(CompetencyFrameworkImportState.Failed, status.State);
        Assert.Contains("FW-1", status.Error);
    }

    /// <summary>
    /// The phase it failed in survives the failure, so a client can say how far a large import got. The
    /// framework row saves in phase 2 and the competencies in phase 3, so a competency-level failure has
    /// passed both.
    /// </summary>
    [Fact]
    public async Task GetImportStatus_AfterAFailedImport_ReportsThePhaseItReached()
    {
        var client = Client(await Manager());
        var importId = Guid.NewGuid();
        await ImportCsv(client, Csv(CompetencyRow("C1")), importId: importId);

        var status = await Read<CompetencyFrameworkImportStatus>(await Status(client, importId));

        // The file never reached the transaction, so only the first phase was reported.
        Assert.Equal("Reading file", status.Phase);
        Assert.Equal(1, status.PhaseNumber);
        Assert.Equal(0, status.PercentComplete);
    }

    /// <summary>
    /// A refusal that happens before the import runs records no progress at all, so a client polling on
    /// the id it sent gets a 404 rather than a status that never changes. Worth pinning because these are
    /// the paths that skip <c>RunImportAsync</c> entirely.
    /// </summary>
    [Fact]
    public async Task Import_WithAnEmptyMultipartBody_RecordsNoProgress()
    {
        var client = Client(await Manager());
        var importId = Guid.NewGuid();
        using var content = new MultipartFormDataContent();
        await client.PostAsync($"api/competencyframeworks/import?importId={importId}", content, Ct);

        var response = await Status(client, importId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Import_WithNoSystemPermission_RecordsNoProgress()
    {
        var importId = Guid.NewGuid();
        var actor = await Actor().SeedAsync();
        await ImportCsv(Client(actor), Csv(FrameworkRow("FW-1")), importId: importId);

        var response = await Status(Client(await Manager()), importId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Reusing an id after a failure starts the record over, which is what makes a client-generated id
    /// safe to retry with.
    /// </summary>
    [Fact]
    public async Task GetImportStatus_ForAnImportIdReusedAfterAFailure_ReportsTheSecondAttempt()
    {
        var client = Client(await Manager());
        var importId = Guid.NewGuid();
        await ImportCsv(client, Csv(CompetencyRow("C1")), importId: importId);

        await ImportCsv(client, Csv(FrameworkRow("FW-1")), importId: importId);

        var status = await Read<CompetencyFrameworkImportStatus>(await Status(client, importId));
        Assert.Equal(CompetencyFrameworkImportState.Succeeded, status.State);
        Assert.Null(status.Error);
    }

    /// <summary>
    /// An import without an id still succeeds. It is still recorded, against an id generated on the server
    /// so that the code path is the same either way - but that id is not returned anywhere, in a header or
    /// in the body, so the record is unreachable and occupies the progress dictionary for the full
    /// thirty-minute retention. Harmless per import; less so on an installation that imports on a schedule.
    /// </summary>
    [Fact]
    public async Task Import_WithNoImportId_Succeeds()
    {
        var response = await ImportCsv(Client(await Manager()), Csv(FrameworkRow("FW-1")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.DoesNotContain("importid", string.Join(' ', response.Headers.Select(h => h.Key)).ToLowerInvariant());
    }

    /// <summary>
    /// Characterizes a leak. <c>GetImportStatus</c> is the only action in this controller with no
    /// permission check at all, and progress is keyed on nothing but the client-supplied id - so any
    /// account that can sign in can read the state, the phase and the <em>name</em> of a framework another
    /// account is importing. Reads elsewhere in this controller are equally open
    /// (<c>CompetencyFrameworkEndpointTests</c> characterizes those), but this one is not even scoped to
    /// the caller who started the work.
    /// </summary>
    [Fact]
    public async Task GetImportStatus_ReportsAnotherAccountsImport()
    {
        var importId = Guid.NewGuid();
        await ImportCsv(
            Client(await Manager()), Csv(FrameworkRow("FW-1", "Someone else's framework")),
            importId: importId);

        var response = await Status(Client(await Actor().SeedAsync()), importId);

        var status = await Read<CompetencyFrameworkImportStatus>(response);
        Assert.Equal(CompetencyFrameworkImportState.Succeeded, status.State);
        Assert.Equal("Someone else's framework", status.FrameworkName);
    }

    [Fact]
    public async Task GetImportStatus_SerializesTheStateAsItsName()
    {
        var client = Client(await Manager());
        var importId = Guid.NewGuid();
        await ImportCsv(client, Csv(FrameworkRow("FW-1")), importId: importId);

        var body = await (await Status(client, importId)).Content.ReadAsStringAsync(Ct);

        Assert.Contains("\"state\":\"Succeeded\"", body);
    }

    // ---------------------------------------------------------------------------------------------
    // Authorization
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("api/competencyframeworks/import")]
    [InlineData("api/competencyframeworks/import-json")]
    [InlineData("api/competencyframeworks/import-xlsx")]
    public async Task EveryImport_Anonymously_Is401(string route)
    {
        using var content = Form([1, 2, 3], "file.bin", "application/octet-stream");

        var response = await AnonymousClient.PostAsync(route, content, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetImportStatus_Anonymously_Is401()
    {
        var response = await AnonymousClient.GetAsync(
            $"api/competencyframeworks/imports/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The permission is checked before the file is looked at, so an account without it cannot learn
    /// whether its upload would have parsed - the file goes unread whatever it contains.
    /// </summary>
    [Theory]
    [InlineData("api/competencyframeworks/import")]
    [InlineData("api/competencyframeworks/import-json")]
    [InlineData("api/competencyframeworks/import-xlsx")]
    public async Task EveryImport_WithNoSystemPermission_Is403(string route)
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewCompetencyFrameworks).SeedAsync();
        using var content = Form(Encoding.UTF8.GetBytes(Csv(FrameworkRow("FW-1"))), "f.csv", "text/csv");

        var response = await Client(actor).PostAsync(route, content, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await NewContext().CompetencyFrameworks.ToListAsync(Ct));
    }

    /// <summary>
    /// Viewing a framework is a weaker permission than managing one, and the import routes want the
    /// stronger - so the theory above uses the viewer rather than an account with nothing, to prove the
    /// two are actually distinguished rather than any claim being enough.
    /// </summary>
    [Fact]
    public async Task Import_WithACompetencyFrameworkManager_IsAllowed()
    {
        var response = await ImportCsv(Client(await Manager()), Csv(FrameworkRow("FW-1")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private Task<TestActor> Manager() =>
        Actor().WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();

    private async Task<HttpResponseMessage> ImportCsv(
        HttpClient client, string csv, string source = null, string version = null, Guid? importId = null)
    {
        using var content = Form(Encoding.UTF8.GetBytes(csv), "framework.csv", "text/csv");

        // Awaited inside the using: TestServer reads the body during SendAsync, so returning the task
        // unawaited disposes the content before the request has been read.
        return await client.PostAsync(
            Url("api/competencyframeworks/import", source, version, importId), content, Ct);
    }

    private async Task<HttpResponseMessage> ImportJson(
        HttpClient client, string json, Guid? importId = null)
    {
        using var content = Form(Encoding.UTF8.GetBytes(json), "framework.json", "application/json");

        return await client.PostAsync(
            Url("api/competencyframeworks/import-json", null, null, importId), content, Ct);
    }

    private Task<HttpResponseMessage> Status(HttpClient client, Guid importId) =>
        client.GetAsync($"api/competencyframeworks/imports/{importId}", Ct);

    private static MultipartFormDataContent Form(byte[] file, string fileName, string contentType)
    {
        var content = new MultipartFormDataContent();
        var upload = new ByteArrayContent(file);
        upload.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(upload, "file", fileName);

        return content;
    }

    private static string Url(string path, string source, string version, Guid? importId)
    {
        var query = new List<string>();

        if (source != null)
            query.Add($"source={Uri.EscapeDataString(source)}");

        if (version != null)
            query.Add($"version={Uri.EscapeDataString(version)}");

        if (importId.HasValue)
            query.Add($"importId={importId}");

        return query.Count == 0 ? path : $"{path}?{string.Join("&", query)}";
    }

    private static Competency Competency(CompetencyFramework framework, string idNumber) =>
        framework.Competencies.Single(c => c.IdNumber == idNumber);

    /// <summary>
    /// The response body, having first insisted the request succeeded. An <c>ApiError</c> shares no
    /// property names with a framework, so deserializing one yields defaults throughout - an empty
    /// competency collection, a null ID number - which is what several assertions here are looking for.
    /// Use <see cref="ReadError"/> for the failure cases.
    /// </summary>
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
}
