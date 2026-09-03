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
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Tests.Infrastructure;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Blueprint.Api.Tests.Infrastructure.Frameworks;

namespace Blueprint.Api.Tests;

/// <summary>
/// The three preview endpoints - <c>preview-csv</c>, <c>preview-json</c> and <c>preview-xlsx</c> - which
/// answer "what would happen if I imported this file" before anybody imports it.
/// </summary>
/// <remarks>
/// <para>
/// A preview is only worth having if it agrees with the import, so most of the tests here upload one file
/// to both and compare. They disagree in seven ways, each of them a separate reading of the same bytes by
/// a second parser written alongside the first. The three previews share no code with the three importers.
/// </para>
/// <para>
/// <c>preview-csv</c> is the worst of the three. Its counting loop reads <c>cols[0]</c> - the <em>parent's</em>
/// ID number - as the row's own identifier, so a flat file previews as zero elements
/// (<see cref="PreviewCsv_CountsNoElementsForAFlatFile"/>) and a file with a hierarchy previews with every
/// row typed by its parent's prefix (<see cref="PreviewCsv_TypesARowByItsParentsIdNumber"/>). It then reads
/// <c>cols[11]</c> - the Export ID - as the cross-reference list, under a comment saying "column 12 =
/// relatedidnumbers", which the real list is not
/// (<see cref="PreviewCsv_ReadsTheExportIdColumnAsTheCrossReferences"/>). And it splits on commas by hand
/// while the conflict check ten lines above it uses the RFC 4180 parser the importer uses, so one method
/// reads one file two ways and gets two answers
/// (<see cref="PreviewCsv_MiscountsARowWhoseDescriptionContainsAComma"/> against
/// <see cref="PreviewCsv_UsesTheQuoteAwareParserToFindTheFrameworkRow"/>).
/// </para>
/// <para>
/// <c>preview-xlsx</c> systematically overstates a DCWF workbook. It counts a work-role row per row rather
/// than per code, counts a category whose code is blank, counts a TKSA with no description, and counts a
/// relationship for <em>every</em> role-sheet row with a non-blank first cell - no type filter, no check
/// that the TKSA exists, and no check that the role code belongs to a role it imported
/// (<see cref="PreviewXlsx_CountsEveryRoleSheetRowAsARelationship"/>). It also reports "0 elements, no
/// error" for several files the importer refuses outright.
/// </para>
/// <para>
/// <c>preview-json</c> is the closest to honest, and still counts relationships it will not create: the
/// total is the raw array length, so a link naming an element the file does not contain is counted, and so
/// is a hierarchy link the import turns into a parent rather than a relationship.
/// </para>
/// <para>
/// <strong>None of the three checks any permission.</strong> The two importers beside them require
/// <c>ManageCompetencyFrameworks</c>; these require only that the caller be signed in. So any account can
/// upload a file, have the server parse it, and - because the conflict check runs first and reports the
/// name and version of whatever framework already holds that ID number - read back the name of a framework
/// it cannot otherwise see (<see cref="PreviewCsv_WithNoPermission_RevealsAnExistingFrameworksName"/>).
/// </para>
/// <para>
/// All three declare only <c>200</c> in their <c>ProducesResponseType</c> while also answering 400, which
/// belongs on the Phase 4 contract list. Their errors are otherwise reported in the body's <c>Error</c>
/// property with a 200 status, deliberately - a preview that cannot read the file has still answered the
/// question.
/// </para>
/// </remarks>
public class CompetencyFrameworkPreviewTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // preview-csv
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task PreviewCsv_EchoesTheSourceAndVersionFromTheQueryString()
    {
        var csv = Csv(FrameworkRow("FW-1", "The name in the file"), CompetencyRow("C2", parent: "C1"));
        var client = Client(await Manager());

        var preview = await PreviewCsv(client, csv, "NICE", "5.1");

        Assert.Equal("NICE", preview.Source);
        Assert.Equal("5.1", preview.Version);
        Assert.Equal("NICE 5.1", preview.FrameworkName);

        // The name the import will actually use comes from the file, so the preview shows one the import
        // does not.
        var imported = await Read<CompetencyFramework>(await ImportCsv(client, csv, "NICE", "5.1"));
        Assert.Equal("The name in the file", imported.Name);
    }

    [Fact]
    public async Task PreviewCsv_WithNoSourceOrVersion_NamesTheFrameworkASingleSpace()
    {
        var preview = await PreviewCsv(
            Client(await Manager()), Csv(FrameworkRow("FW-1"), CompetencyRow("C2", parent: "C1")));

        Assert.Null(preview.Source);
        Assert.Null(preview.Version);
        Assert.Equal(" ", preview.FrameworkName);
    }

    /// <summary>
    /// The counting loop reads column 1 - the <em>parent</em> ID number - as the row's own identifier, so a
    /// file whose rows have no parent previews as containing nothing at all. That is every flat framework,
    /// which is most of them.
    /// </summary>
    /// <remarks>
    /// The conflict check immediately above this loop reads column 2 for the same rows and gets the right
    /// answer, so the mistake is local to the loop rather than a misreading of the format. It reddens when
    /// the loop is pointed at <c>cols[1]</c>.
    /// </remarks>
    [Fact]
    public async Task PreviewCsv_CountsNoElementsForAFlatFile()
    {
        var csv = Csv(FrameworkRow("FW-1"), CompetencyRow("C1"), CompetencyRow("C2"));
        var client = Client(await Manager());

        var preview = await PreviewCsv(client, csv);

        Assert.Null(preview.Error);
        Assert.Empty(preview.ElementTypeCounts);
        Assert.Equal(0, preview.TotalElements);

        var imported = await Read<CompetencyFramework>(await ImportCsv(client, csv));
        Assert.Equal(2, imported.Competencies.Count);
    }

    /// <summary>
    /// A row that does have a parent is counted, and typed from the prefix of the <em>parent's</em> ID
    /// number - so the breakdown describes each row's parent rather than the row.
    /// </summary>
    [Theory]
    [InlineData("WRL-1", "work_role")]
    [InlineData("T-1", "task")]
    [InlineData("K-1", "knowledge")]
    [InlineData("S-1", "skill")]
    [InlineData("A-1", "ability")]
    [InlineData("C1", "competency")]
    [InlineData("t-1", "competency")]
    public async Task PreviewCsv_TypesARowByItsParentsIdNumber(string parent, string expected)
    {
        var preview = await PreviewCsv(
            Client(await Manager()), Csv(FrameworkRow("FW-1"), CompetencyRow("X1", parent: parent)));

        Assert.Equal(1, preview.TotalElements);
        Assert.Equal([expected], Types(preview));
        Assert.Equal(1, Count(preview, expected));
    }

    /// <summary>
    /// Puts the two mistakes together against what the import creates: three competencies in the file, two
    /// of them children of the third, previews as "2 tasks" because their parent's ID number starts with
    /// <c>T-</c> and the parent itself has no parent to be counted by.
    /// </summary>
    [Fact]
    public async Task PreviewCsv_CountsRowsByTheirParentSoItsTotalIsNotTheImports()
    {
        var csv = Csv(
            FrameworkRow("FW-1"),
            CompetencyRow("T-1"),
            CompetencyRow("K-1", parent: "T-1"),
            CompetencyRow("K-2", parent: "T-1"));
        var client = Client(await Manager());

        var preview = await PreviewCsv(client, csv);

        Assert.Equal(2, preview.TotalElements);
        Assert.Equal(2, Count(preview, "task"));

        var imported = await Read<CompetencyFramework>(await ImportCsv(client, csv));
        Assert.Equal(3, imported.Competencies.Count);
        Assert.Equal(0, Count(preview, "knowledge"));
    }

    /// <summary>
    /// The relationship count reads column 12 - the Export ID - and not column 11, which is the cross
    /// referenced competency ID numbers the importer reads. The comment above the line says
    /// "column 12 = relatedidnumbers", counting from one; the index is zero-based.
    /// </summary>
    [Fact]
    public async Task PreviewCsv_ReadsTheExportIdColumnAsTheCrossReferences()
    {
        var csv = Csv(
            FrameworkRow("FW-1"),
            CompetencyRow("C1"),
            Row(
                parentIdNumber: "C1",
                idNumber: "C2",
                shortName: "two",
                relatedIdNumbers: "C1",
                exportId: "X5|X6|X7"));
        var client = Client(await Manager());

        var preview = await PreviewCsv(client, csv);

        Assert.Equal(3, preview.TotalRelationships);

        // The file declares exactly one cross-reference, and that is what the import creates.
        await ImportCsv(client, csv);
        Assert.Equal(1, await NewContext().CompetencyRelationships.CountAsync(Ct));
    }

    /// <summary>
    /// The counting loop splits on every comma, so a quoted field containing one shifts every column after
    /// it and the Export ID it was reading lands somewhere else.
    /// </summary>
    /// <remarks>
    /// A comma in a description is not exotic; Moodle quotes it and the importer's own parser handles it.
    /// This test pins the row still being counted while its relationships silently become zero, which is
    /// the failure mode that looks like working software.
    /// </remarks>
    [Fact]
    public async Task PreviewCsv_MiscountsARowWhoseDescriptionContainsAComma()
    {
        var preview = await PreviewCsv(
            Client(await Manager()),
            Csv(
                FrameworkRow("FW-1"),
                Row(
                    parentIdNumber: "C1",
                    idNumber: "C2",
                    shortName: "two",
                    description: "alpha, beta",
                    exportId: "X9")));

        Assert.Equal(1, preview.TotalElements);
        Assert.Equal(0, preview.TotalRelationships);
    }

    /// <summary>
    /// The conflict check in the same method uses the quote-aware parser, and finds a framework row whose
    /// description contains a comma - which the naive split ten lines below would have shifted out of
    /// recognition. One method, one file, two parsers.
    /// </summary>
    [Fact]
    public async Task PreviewCsv_UsesTheQuoteAwareParserToFindTheFrameworkRow()
    {
        var existing = BlueprintAppFactory.CompetencyFramework(idNumber: "FW-1");
        existing.Name = "Already here";
        await Seed(existing);

        var preview = await PreviewCsv(
            Client(await Manager()),
            Csv(Row(idNumber: "FW-1", shortName: "New", description: "alpha, beta", isFramework: "1")));

        Assert.Contains("FW-1", preview.Error);
        Assert.Contains("Already here", preview.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n   \n")]
    public async Task PreviewCsv_WithNoDataRow_ReportsAnError(string trailer)
    {
        var preview = await PreviewCsv(Client(await Manager()), Header + trailer, "NICE", "5.1");

        Assert.Equal("CSV file must have a header row and at least one data row.", preview.Error);

        // The query string is echoed even when nothing could be read, but the name derived from it is not.
        Assert.Equal("NICE", preview.Source);
        Assert.Null(preview.FrameworkName);
        Assert.Empty(preview.ElementTypeCounts);
    }

    [Fact]
    public async Task PreviewCsv_WithNoMoodleHeaderRow_ReportsAnError()
    {
        var preview = await PreviewCsv(Client(await Manager()), "one,two\nthree,four");

        Assert.Equal(
            "CSV file must contain a Moodle lpimportcsv header row with 'Parent ID number'.", preview.Error);
    }

    /// <summary>A header with nothing beneath it is the same error, reached by the second half of the guard.</summary>
    [Fact]
    public async Task PreviewCsv_WithTheHeaderOnTheLastLine_ReportsTheSameError()
    {
        var preview = await PreviewCsv(Client(await Manager()), "junk\n" + Header);

        Assert.Equal(
            "CSV file must contain a Moodle lpimportcsv header row with 'Parent ID number'.", preview.Error);
    }

    [Fact]
    public async Task PreviewCsv_WithFewerThanFourteenColumns_ReportsAnError()
    {
        var preview = await PreviewCsv(
            Client(await Manager()), "Parent ID number,ID number\nC1,C2");

        Assert.Equal("CSV file must have 14 columns (Moodle lpimportcsv format).", preview.Error);
    }

    /// <summary>
    /// Junk on the header line is stripped before the columns are counted, so the count is of the header's
    /// own columns rather than the junk's.
    /// </summary>
    /// <remarks>
    /// This is the only observable effect of that strip: it can only reduce the column count, so it can only
    /// turn a file that would have been accepted into the error it deserves. Without it the ten junk fields
    /// here would have made a five-column header look like fifteen columns.
    /// </remarks>
    [Fact]
    public async Task PreviewCsv_CountsTheColumnsAfterStrippingJunkFromTheHeaderLine()
    {
        var truncated =
            "a,b,c,d,e,f,g,h,i,j,Parent ID number,ID number,Short name,Description,Description format";

        var preview = await PreviewCsv(Client(await Manager()), truncated + "\nC1,C2,three,four,five");

        Assert.Equal("CSV file must have 14 columns (Moodle lpimportcsv format).", preview.Error);
    }

    [Fact]
    public async Task PreviewCsv_ToleratesJunkLinesBeforeTheHeader()
    {
        var preview = await PreviewCsv(
            Client(await Manager()),
            "<html><body>\nGenerated 2026-01-01\n" +
                Csv(FrameworkRow("FW-1"), CompetencyRow("C2", parent: "C1")));

        Assert.Null(preview.Error);
        Assert.Equal(1, preview.TotalElements);
    }

    /// <summary>
    /// The point of previewing: a framework whose ID number is already taken is reported before the user
    /// spends a minute on an import that will 409. The message names the framework in the way, and its
    /// version.
    /// </summary>
    [Fact]
    public async Task PreviewCsv_ReportsAFrameworkIdNumberAlreadyTaken()
    {
        var existing = BlueprintAppFactory.CompetencyFramework(idNumber: "FW-1", version: "2.0");
        existing.Name = "Already here";
        await Seed(existing);

        var preview = await PreviewCsv(
            Client(await Manager()), Csv(FrameworkRow("FW-1"), CompetencyRow("C2", parent: "C1")));

        Assert.Contains("FW-1", preview.Error);
        Assert.Contains("Already here", preview.Error);
        Assert.Contains("version 2.0", preview.Error);

        // Nothing is counted once the conflict is found - the file is not read any further.
        Assert.Empty(preview.ElementTypeCounts);
        Assert.Equal(0, preview.TotalElements);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PreviewCsv_ForABlankFrameworkIdNumber_ReportsNoConflict(string idNumber)
    {
        var preview = await PreviewCsv(
            Client(await Manager()), Csv(FrameworkRow(idNumber), CompetencyRow("C2", parent: "C1")));

        Assert.Null(preview.Error);
        Assert.Equal(1, preview.TotalElements);
    }

    /// <summary>
    /// Only the first framework row is checked, which matches the importer - it takes the first and discards
    /// the rest, so a later row's ID number is never going to be used.
    /// </summary>
    [Fact]
    public async Task PreviewCsv_ChecksOnlyTheFirstFrameworkRow()
    {
        var existing = BlueprintAppFactory.CompetencyFramework(idNumber: "FW-2");
        await Seed(existing);

        var preview = await PreviewCsv(
            Client(await Manager()),
            Csv(FrameworkRow("FW-1"), FrameworkRow("FW-2"), CompetencyRow("C2", parent: "C1")));

        Assert.Null(preview.Error);
    }

    // ---------------------------------------------------------------------------------------------
    // preview-json
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task PreviewJson_ReadsTheDocumentMetadata()
    {
        var preview = await PreviewJson(
            Client(await Manager()),
            Nice([Element("T1", "task")], name: "The NICE Framework", identifier: "NICE", version: "5.1"));

        Assert.Equal("The NICE Framework", preview.FrameworkName);
        Assert.Equal("NICE", preview.Source);
        Assert.Equal("5.1", preview.Version);
        Assert.Equal(1, preview.TotalElements);
    }

    [Fact]
    public async Task PreviewJson_ReadsAWrappedFile()
    {
        var preview = await PreviewJson(Client(await Manager()), Wrapped(Nice([Element("T1", "task")])));

        Assert.Equal("NICE Framework", preview.FrameworkName);
        Assert.Equal(1, preview.TotalElements);
    }

    [Fact]
    public async Task PreviewJson_CountsTheElementsByType()
    {
        var preview = await PreviewJson(
            Client(await Manager()),
            Nice([
                Element("W1", "work_role"),
                Element("W2", "work_role"),
                Element("T1", "task"),
                Element("K1", "knowledge")
            ]));

        Assert.Equal(["knowledge", "task", "work_role"], Types(preview));
        Assert.Equal(2, Count(preview, "work_role"));
        Assert.Equal(4, preview.TotalElements);
    }

    [Fact]
    public async Task PreviewJson_SkipsSortAndOpmCodeElements()
    {
        var preview = await PreviewJson(
            Client(await Manager()),
            Nice([Element("S1", "sort"), Element("O1", "opm_code"), Element("T1", "task")]));

        Assert.Equal(["task"], Types(preview));
        Assert.Equal(1, preview.TotalElements);
    }

    /// <summary>
    /// The skipped-type set is case-sensitive, so a file spelling the type <c>Sort</c> previews - and
    /// imports - as a competency of type "Sort".
    /// </summary>
    /// <remarks>
    /// The importer's own set is case-sensitive in the same way, so this is one of the few places the two
    /// halves agree. It is pinned on both sides because fixing one without the other would split them, and
    /// the preview is where the discrepancy would be seen first.
    /// </remarks>
    [Theory]
    [InlineData("Sort")]
    [InlineData("SORT")]
    [InlineData("OPM_Code")]
    public async Task PreviewJson_DoesNotSkipASkippedTypeWrittenInADifferentCase(string elementType)
    {
        var preview = await PreviewJson(Client(await Manager()), Nice([Element("E1", elementType)]));

        Assert.Equal([elementType], Types(preview));
        Assert.Equal(1, preview.TotalElements);
    }

    /// <summary>
    /// An element with no <c>element_type</c> is not counted at all, and no error is reported - while the
    /// importer reads the same property with <c>GetProperty</c> and throws, so the file previews as empty
    /// and imports as a 500.
    /// </summary>
    [Fact]
    public async Task PreviewJson_DoesNotCountAnElementWithoutAType()
    {
        var json = """
            {"documents":[{"name":"F","version":"1","doc_identifier":"D"}],
             "elements":[{"element_identifier":"T1"}],"relationships":[]}
            """;
        var client = Client(await Manager());

        var preview = await PreviewJson(client, json);

        Assert.Null(preview.Error);
        Assert.Empty(preview.ElementTypeCounts);
        Assert.Equal(0, preview.TotalElements);

        var response = await ImportJson(client, json);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// The relationship total is the length of the array, so a link naming an element the file does not
    /// contain is counted as one the import will create. It will not.
    /// </summary>
    [Fact]
    public async Task PreviewJson_CountsEveryRelationshipWithoutCheckingIt()
    {
        var json = Nice(
            [Element("W1", "work_role"), Element("T1", "task")],
            [Link("W1", "T1"), Link("W1", "MISSING"), Link("MISSING", "T1")]);
        var client = Client(await Manager());

        var preview = await PreviewJson(client, json);

        Assert.Equal(3, preview.TotalRelationships);

        await ImportJson(client, json);
        Assert.Equal(1, await NewContext().CompetencyRelationships.CountAsync(Ct));
    }

    /// <summary>
    /// A link between two structural types is the hierarchy, not a relationship - the import sets a parent
    /// and stores no relationship row, while the preview counts it as one.
    /// </summary>
    [Fact]
    public async Task PreviewJson_CountsAHierarchyLinkTheImportTurnsIntoAParent()
    {
        var json = Nice(
            [Element("C1", "category"), Element("W1", "work_role")], [Link("C1", "W1")]);
        var client = Client(await Manager());

        var preview = await PreviewJson(client, json);

        Assert.Equal(1, preview.TotalRelationships);

        var imported = await Read<CompetencyFramework>(await ImportJson(client, json));
        Assert.Equal(0, await NewContext().CompetencyRelationships.CountAsync(Ct));
        Assert.Equal(
            imported.Competencies.Single(c => c.IdNumber == "C1").Id,
            imported.Competencies.Single(c => c.IdNumber == "W1").ParentId);
    }

    [Fact]
    public async Task PreviewJson_ReportsAConflictOnTheIdNumberDerivedFromTheDocument()
    {
        var existing = BlueprintAppFactory.CompetencyFramework(idNumber: "NICE-5.1", version: "5.1");
        existing.Name = "Already here";
        await Seed(existing);

        var preview = await PreviewJson(
            Client(await Manager()),
            Nice([Element("T1", "task")], identifier: "NICE", version: "5.1"));

        Assert.Contains("Already here", preview.Error);
        Assert.Equal(0, preview.TotalElements);
    }

    /// <summary>
    /// A file with no <c>documents</c> array previews with no metadata at all rather than with the defaults
    /// the code beside it declares - those only apply when the array is present and its first document is
    /// missing the property.
    /// </summary>
    [Fact]
    public async Task PreviewJson_WithNoDocuments_LeavesTheMetadataNull()
    {
        var preview = await PreviewJson(
            Client(await Manager()),
            """{"elements":[{"element_identifier":"T1","element_type":"task"}],"relationships":[]}""");

        Assert.Null(preview.FrameworkName);
        Assert.Null(preview.Source);
        Assert.Null(preview.Version);
        Assert.Equal(1, preview.TotalElements);
    }

    [Fact]
    public async Task PreviewJson_WithADocumentMissingItsFields_UsesTheDeclaredDefaults()
    {
        var preview = await PreviewJson(
            Client(await Manager()), """{"documents":[{}],"elements":[],"relationships":[]}""");

        Assert.Equal("Imported Framework", preview.FrameworkName);
        Assert.Equal("", preview.Source);
        Assert.Equal("", preview.Version);
    }

    /// <summary>
    /// An <em>empty</em> <c>documents</c> array is a parse failure, because the first document of an empty
    /// array is an undefined element and reading a property off one throws. A missing array is fine and an
    /// empty one is not.
    /// </summary>
    [Fact]
    public async Task PreviewJson_WithAnEmptyDocumentsArray_FailsToParse()
    {
        var preview = await PreviewJson(
            Client(await Manager()), """{"documents":[],"elements":[],"relationships":[]}""");

        Assert.StartsWith("Failed to parse JSON:", preview.Error);
    }

    [Fact]
    public async Task PreviewJson_WithMalformedJson_ReportsAParseError()
    {
        var preview = await PreviewJson(Client(await Manager()), "{not json");

        Assert.StartsWith("Failed to parse JSON:", preview.Error);
    }

    /// <summary>
    /// A <c>competencies</c> property that is not an array is not a native export, so the file is previewed
    /// as a NICE document - the same dispatch the importer makes.
    /// </summary>
    [Fact]
    public async Task PreviewJson_WithANullCompetenciesProperty_IsPreviewedAsANiceFile()
    {
        var preview = await PreviewJson(
            Client(await Manager()),
            """
            {"competencies":null,
             "elements":[{"element_identifier":"T1","element_type":"task"}],"relationships":[]}
            """);

        Assert.Equal(["task"], Types(preview));
    }

    // ---------------------------------------------------------------------------------------------
    // preview-json, for Blueprint's own export
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A framework exported from Blueprint previews from its own fields. Note that this branch never fills
    /// in the type breakdown, so the UI is given a total with nothing to itemise it.
    /// </summary>
    [Fact]
    public async Task PreviewJson_ForANativeExport_ReadsItsOwnFields()
    {
        var preview = await PreviewJson(
            Client(await Manager()),
            Native("EX-1", NativeCompetency("C1", "C2", "C3"), NativeCompetency("C2")));

        Assert.Equal("Exported framework", preview.FrameworkName);
        Assert.Equal("SEI", preview.Source);
        Assert.Equal("3.0", preview.Version);
        Assert.Equal(2, preview.TotalElements);
        Assert.Equal(2, preview.TotalRelationships);
        Assert.Empty(preview.ElementTypeCounts);
    }

    /// <summary>
    /// The fields are matched without regard to case, while the <c>competencies</c> property that decides
    /// whether the file is a native export at all is matched against exactly two spellings. So a file
    /// written with <c>COMPETENCIES</c> is previewed as a NICE document and one written with
    /// <c>Competencies</c> is not.
    /// </summary>
    [Fact]
    public async Task PreviewJson_ForANativeExport_MatchesTheOtherFieldsWithoutRegardToCase()
    {
        var client = Client(await Manager());

        var native = await PreviewJson(
            client,
            """{"NAME":"Shouted","SOURCE":"SEI","VERSION":"3.0","IDNUMBER":"EX-1","Competencies":[]}""");
        var notNative = await PreviewJson(
            client,
            """{"NAME":"Shouted","SOURCE":"SEI","VERSION":"3.0","IDNUMBER":"EX-1","COMPETENCIES":[]}""");

        Assert.Equal("Shouted", native.FrameworkName);
        Assert.Equal("SEI", native.Source);
        Assert.Equal("3.0", native.Version);

        Assert.Null(notNative.FrameworkName);
        Assert.Null(notNative.Source);
    }

    [Fact]
    public async Task PreviewJson_ForANativeExport_IgnoresANameThatIsNotAString()
    {
        var preview = await PreviewJson(
            Client(await Manager()), """{"name":5,"source":"SEI","competencies":[]}""");

        Assert.Null(preview.FrameworkName);
        Assert.Equal("SEI", preview.Source);
    }

    [Fact]
    public async Task PreviewJson_ForANativeExport_ReportsAConflictOnItsOwnIdNumber()
    {
        var existing = BlueprintAppFactory.CompetencyFramework(idNumber: "EX-1");
        existing.Name = "Already here";
        await Seed(existing);

        var preview = await PreviewJson(
            Client(await Manager()), Native("EX-1", NativeCompetency("C1")));

        Assert.Contains("Already here", preview.Error);
        Assert.Equal(0, preview.TotalElements);
    }

    [Fact]
    public async Task PreviewJson_ForANativeExportWithNoCompetencies_ReportsNothingWithoutAnError()
    {
        var preview = await PreviewJson(Client(await Manager()), Native("EX-1"));

        Assert.Null(preview.Error);
        Assert.Equal(0, preview.TotalElements);
        Assert.Equal(0, preview.TotalRelationships);
    }

    // ---------------------------------------------------------------------------------------------
    // preview-xlsx, the DCWF shape
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task PreviewXlsx_EchoesTheSourceAndVersionAndNamesTheFramework()
    {
        var preview = await PreviewXlsx(
            Client(await Manager()), Dcwf(roles: [RoleRow(category: AnyCategory)]), "DCWF", "1.0");

        Assert.Equal("DCWF", preview.Source);
        Assert.Equal("1.0", preview.Version);
        Assert.Equal("DCWF 1.0", preview.FrameworkName);
    }

    [Fact]
    public async Task PreviewXlsx_WithNoSourceOrVersion_NamesTheFrameworkASingleSpace()
    {
        var preview = await PreviewXlsx(
            Client(await Manager()), Dcwf(roles: [RoleRow(category: AnyCategory)]));

        Assert.Equal(" ", preview.FrameworkName);
    }

    /// <summary>
    /// The ordinary case, where preview and import agree: distinct categories and distinct work roles are
    /// counted once each.
    /// </summary>
    [Fact]
    public async Task PreviewXlsx_CountsTheCategoriesAndWorkRoles()
    {
        var xlsx = Dcwf(roles:
        [
            RoleRow(category: Category("Information Technology", "IT"), roleName: "A", roleCode: "411"),
            RoleRow(roleName: "B", roleCode: "412"),
            RoleRow(category: Category("Securely Provision", "SP"), roleName: "C", roleCode: "141")
        ]);
        var client = Client(await Manager());

        var preview = await PreviewXlsx(client, xlsx);

        Assert.Equal(2, Count(preview, "category"));
        Assert.Equal(3, Count(preview, "work_role"));
        Assert.Equal(5, preview.TotalElements);

        var imported = await Read<CompetencyFramework>(await ImportXlsx(client, xlsx));
        Assert.Equal(5, imported.Competencies.Count);
    }

    /// <summary>
    /// A work role is counted per row rather than per code, while the importer keys them by code - so a
    /// workbook listing a role twice previews with one competency more than it imports.
    /// </summary>
    /// <remarks>
    /// The TKSA count in the same method dedupes by id, and the category count dedupes by code. The work
    /// role count is the one of the three that does not.
    /// </remarks>
    [Fact]
    public async Task PreviewXlsx_CountsAWorkRoleRowTwiceWhenItRepeatsACode()
    {
        var xlsx = Dcwf(roles:
        [
            RoleRow(category: AnyCategory, roleName: "First", roleCode: "411"),
            RoleRow(roleName: "Second", roleCode: "411")
        ]);
        var client = Client(await Manager());

        var preview = await PreviewXlsx(client, xlsx);

        Assert.Equal(2, Count(preview, "work_role"));
        Assert.Equal(3, preview.TotalElements);

        var imported = await Read<CompetencyFramework>(await ImportXlsx(client, xlsx));
        Assert.Equal(2, imported.Competencies.Count);
    }

    /// <summary>
    /// A category cell whose brackets are empty is counted as a category, because the count only requires
    /// the cell to have a second line - while the importer requires the code inside the brackets to be
    /// non-blank and creates nothing.
    /// </summary>
    [Fact]
    public async Task PreviewXlsx_CountsACategoryWhoseCodeIsBlank()
    {
        var xlsx = Dcwf(roles:
        [
            RoleRow(category: Category("Nameless", "")),
            RoleRow(category: Category("Information Technology", "IT"))
        ]);
        var client = Client(await Manager());

        var preview = await PreviewXlsx(client, xlsx);

        Assert.Equal(2, Count(preview, "category"));

        var imported = await Read<CompetencyFramework>(await ImportXlsx(client, xlsx));
        Assert.Equal(["IT"], imported.Competencies.Select(c => c.IdNumber));
    }

    [Fact]
    public async Task PreviewXlsx_CountsTheTksasByType()
    {
        var preview = await PreviewXlsx(
            Client(await Manager()),
            Dcwf(
                roles: [RoleRow(category: AnyCategory)],
                tksas:
                [
                    Tksa("1", "Task", "a"),
                    Tksa("2", "Knowledge", "b"),
                    Tksa("3", "Skill", "c"),
                    Tksa("4", "Ability", "d")
                ]));

        Assert.Equal(["ability", "category", "knowledge", "skill", "task"], Types(preview));
        Assert.Equal(5, preview.TotalElements);
    }

    [Theory]
    [InlineData("Task")]
    [InlineData("task")]
    [InlineData("TASK")]
    [InlineData("tAsK")]
    public async Task PreviewXlsx_MatchesTheTksaTypeWithoutRegardToCase(string type)
    {
        var preview = await PreviewXlsx(
            Client(await Manager()),
            Dcwf(roles: [RoleRow(category: AnyCategory)], tksas: [Tksa("1", type, "a")]));

        Assert.Equal(1, Count(preview, "task"));
    }

    [Theory]
    [InlineData(null, "Task")]
    [InlineData("   ", "Task")]
    [InlineData("1", "Competency")]
    [InlineData("1", "")]
    public async Task PreviewXlsx_SkipsATksaRowItCannotRead(string number, string type)
    {
        var preview = await PreviewXlsx(
            Client(await Manager()),
            Dcwf(roles: [RoleRow(category: AnyCategory)], tksas: [Tksa(number, type, "a")]));

        Assert.Equal(["category"], Types(preview));
        Assert.Equal(1, preview.TotalElements);
    }

    /// <summary>
    /// The preview does not look at the description column, so a TKSA with no description is counted - while
    /// the importer treats the description as the competency's whole content and drops the row.
    /// </summary>
    [Fact]
    public async Task PreviewXlsx_CountsATksaWithNoDescriptionThatTheImportDrops()
    {
        var xlsx = Dcwf(roles: [RoleRow(category: AnyCategory)], tksas: [Tksa("1", "Task", null)]);
        var client = Client(await Manager());

        var preview = await PreviewXlsx(client, xlsx);

        Assert.Equal(1, Count(preview, "task"));
        Assert.Equal(2, preview.TotalElements);

        var imported = await Read<CompetencyFramework>(await ImportXlsx(client, xlsx));
        Assert.Equal(["IT"], imported.Competencies.Select(c => c.IdNumber));
    }

    [Fact]
    public async Task PreviewXlsx_DedupesARepeatedTksaId()
    {
        var preview = await PreviewXlsx(
            Client(await Manager()),
            Dcwf(
                roles: [RoleRow(category: AnyCategory)],
                tksas: [Tksa("1", "Task", "a"), Tksa("1", "Task", "b")]));

        Assert.Equal(1, Count(preview, "task"));
    }

    [Fact]
    public async Task PreviewXlsx_TreatsOneNumberUnderTwoTypesAsTwoElements()
    {
        var preview = await PreviewXlsx(
            Client(await Manager()),
            Dcwf(
                roles: [RoleRow(category: AnyCategory)],
                tksas: [Tksa("1", "Task", "a"), Tksa("1", "Skill", "b")]));

        Assert.Equal(1, Count(preview, "task"));
        Assert.Equal(1, Count(preview, "skill"));
    }

    /// <summary>
    /// The relationship count is the number of role-sheet rows with a non-blank first cell. It does not
    /// check the row's type, does not check that the TKSA exists, and does not check that the sheet's role
    /// code belongs to a role the import will create - so it counts four where the import creates one.
    /// </summary>
    /// <remarks>
    /// This is the number a user reads before deciding whether the file is the right one. It reddens as soon
    /// as the count is made to answer the same three questions the importer asks.
    /// </remarks>
    [Fact]
    public async Task PreviewXlsx_CountsEveryRoleSheetRowAsARelationship()
    {
        var xlsx = Dcwf(
            roles: [RoleRow(category: AnyCategory, roleName: "Support", roleCode: "411")],
            tksas: [Tksa("1", "Task", "a")],
            RoleSheet("IT-411", "Support",
                Requires("1", "Task"),
                Requires("999", "Task"),
                Requires("1", "Competency")),
            RoleSheet("IT-999", "No such role", Requires("1", "Task")));
        var client = Client(await Manager());

        var preview = await PreviewXlsx(client, xlsx);

        Assert.Equal(4, preview.TotalRelationships);

        await ImportXlsx(client, xlsx);
        Assert.Equal(1, await NewContext().CompetencyRelationships.CountAsync(Ct));
    }

    /// <summary>
    /// The first cell of a role-sheet row is the first cell in document order, not the cell in column A, so
    /// a row whose only value sits in column B is counted - and the importer, which reads column A by
    /// reference, drops it.
    /// </summary>
    [Fact]
    public async Task PreviewXlsx_CountsARoleSheetRowWhoseFirstCellIsNotInColumnA()
    {
        var xlsx = Dcwf(
            roles: [RoleRow(category: AnyCategory, roleName: "Support", roleCode: "411")],
            tksas: [Tksa("1", "Task", "a")],
            RoleSheet("IT-411", "Support", Requires(null, "Task")));
        var client = Client(await Manager());

        var preview = await PreviewXlsx(client, xlsx);

        Assert.Equal(1, preview.TotalRelationships);

        await ImportXlsx(client, xlsx);
        Assert.Equal(0, await NewContext().CompetencyRelationships.CountAsync(Ct));
    }

    /// <summary>
    /// A DCWF workbook with nothing readable in it previews as zero elements and no error, where the import
    /// refuses it. The one entry in the breakdown is the category count, which is written unconditionally.
    /// </summary>
    [Fact]
    public async Task PreviewXlsx_ForAnEmptyDcwfFile_ReportsNothingWithoutAnError()
    {
        var client = Client(await Manager());

        var preview = await PreviewXlsx(client, Dcwf());

        Assert.Null(preview.Error);
        Assert.Equal(["category"], Types(preview));
        Assert.Equal(0, preview.TotalElements);

        var response = await ImportXlsx(client, Dcwf());
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // preview-xlsx, the single-sheet shape no importer accepts
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A workbook that is not DCWF falls back to a one-sheet shape - an ID number in column A and related
    /// IDs in column E - which none of the three importers can read. So this half of the endpoint previews
    /// files that cannot be imported at all.
    /// </summary>
    [Fact]
    public async Task PreviewXlsx_PreviewsASingleSheetFileTheImporterRefuses()
    {
        var xlsx = SingleSheet(["ID number"], SimpleRow("T-1"), SimpleRow("K-1"));
        var client = Client(await Manager());

        var preview = await PreviewXlsx(client, xlsx);

        Assert.Null(preview.Error);
        Assert.Equal(2, preview.TotalElements);

        var response = await ImportXlsx(client, xlsx);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(
            "DCWF XLSX must have 'DCWF Roles' and 'Master Task & KSA List' sheets.",
            (await ReadError(response)).Title);
    }

    [Theory]
    [InlineData("WRL-1", "work_role")]
    [InlineData("T-1", "task")]
    [InlineData("K-1", "knowledge")]
    [InlineData("S-1", "skill")]
    [InlineData("A-1", "ability")]
    [InlineData("IT", "category")]
    [InlineData("ITS", "category")]
    [InlineData("ITSM", "competency")]
    [InlineData("I-T", "competency")]
    [InlineData("t-1", "competency")]
    public async Task PreviewXlsx_ForASingleSheetFile_TypesEachRowByItsIdNumber(
        string idNumber, string expected)
    {
        var preview = await PreviewXlsx(
            Client(await Manager()), SingleSheet(["ID number"], SimpleRow(idNumber)));

        Assert.Equal([expected], Types(preview));
    }

    [Fact]
    public async Task PreviewXlsx_ForASingleSheetFile_CountsTheRelatedIdsInColumnE()
    {
        var preview = await PreviewXlsx(
            Client(await Manager()),
            SingleSheet(["ID number"], SimpleRow("C1", "C2,C3|C4"), SimpleRow("C2")));

        Assert.Equal(3, preview.TotalRelationships);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PreviewXlsx_ForASingleSheetFile_SkipsARowWithNoIdNumber(string idNumber)
    {
        var preview = await PreviewXlsx(
            Client(await Manager()),
            SingleSheet(["ID number"], SimpleRow(idNumber), SimpleRow("C1")));

        Assert.Equal(1, preview.TotalElements);
    }

    [Fact]
    public async Task PreviewXlsx_ForASingleSheetFileWithNoDataRow_ReportsAnError()
    {
        var preview = await PreviewXlsx(Client(await Manager()), SingleSheet(["ID number"]));

        Assert.Equal("Spreadsheet must have a header row and at least one data row.", preview.Error);
    }

    /// <summary>
    /// The fallback reads one worksheet and stops, so a multi-sheet workbook that is not DCWF is previewed
    /// from whichever sheet the package happens to list first and the rest are invisible.
    /// </summary>
    [Fact]
    public async Task PreviewXlsx_ForAMultiSheetFileThatIsNotDcwf_ReadsOnlyOneSheet()
    {
        var preview = await PreviewXlsx(
            Client(await Manager()),
            Workbooks.Build(
                new Workbooks.Sheet("One", ["ID number"], SimpleRow("C1")),
                new Workbooks.Sheet("Two", ["ID number"], SimpleRow("C2"))));

        Assert.Equal(1, preview.TotalElements);
    }

    /// <summary>
    /// A workbook with one of the two required sheets but not the other falls into the single-sheet
    /// fallback, where the roles sheet's columns mean nothing - so a DCWF file missing its TKSA sheet
    /// previews as containing nothing, without an error, and imports as a 500.
    /// </summary>
    [Fact]
    public async Task PreviewXlsx_WithOnlyOneOfTheTwoRequiredSheets_FallsBackAndFindsNothing()
    {
        var xlsx = Workbooks.Build(
            RolesSheet(RoleRow(category: AnyCategory, roleName: "Support", roleCode: "411")));
        var client = Client(await Manager());

        var preview = await PreviewXlsx(client, xlsx);

        Assert.Null(preview.Error);
        Assert.Empty(preview.ElementTypeCounts);
        Assert.Equal(0, preview.TotalElements);

        Assert.Equal(HttpStatusCode.InternalServerError, (await ImportXlsx(client, xlsx)).StatusCode);
    }

    /// <summary>
    /// The conflict check runs before the file is opened, so an unreadable file uploaded under a source and
    /// version already imported reports the conflict rather than the parse failure.
    /// </summary>
    [Fact]
    public async Task PreviewXlsx_ReportsAConflictBeforeItOpensTheFile()
    {
        var existing = BlueprintAppFactory.CompetencyFramework(idNumber: "DCWF-1.0", version: "1.0");
        existing.Name = "Already here";
        await Seed(existing);

        var preview = await PreviewXlsx(
            Client(await Manager()), Encoding.UTF8.GetBytes("not a workbook"), "DCWF", "1.0");

        Assert.Contains("Already here", preview.Error);
        Assert.DoesNotContain("Failed to parse", preview.Error);
    }

    [Fact]
    public async Task PreviewXlsx_ForAFileThatIsNotASpreadsheet_ReportsAParseError()
    {
        var preview = await PreviewXlsx(
            Client(await Manager()), Encoding.UTF8.GetBytes("not a workbook"), "DCWF", "1.0");

        Assert.StartsWith("Failed to parse XLSX:", preview.Error);
        Assert.Equal("DCWF 1.0", preview.FrameworkName);
    }

    // ---------------------------------------------------------------------------------------------
    // What the three of them ask of the caller
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// None of the three previews checks a permission, so an account holding nothing at all can upload a
    /// file and have the server parse it. The two importers beside them require
    /// <c>ManageCompetencyFrameworks</c>.
    /// </summary>
    /// <remarks>
    /// It turns red when the previews are given the permission check they should have, which is the fix.
    /// </remarks>
    [Theory]
    [InlineData("preview-csv")]
    [InlineData("preview-json")]
    [InlineData("preview-xlsx")]
    public async Task EveryPreview_WithNoSystemPermission_Is200(string route)
    {
        var response = await Post(
            Client(await Actor().SeedAsync()), route, Encoding.UTF8.GetBytes(Csv(FrameworkRow("FW-1"))));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// What that costs, concretely: the conflict check runs before anything else and names the framework
    /// holding the ID number, so a caller who cannot list frameworks learns one exists, what it is called
    /// and which version it is - by uploading a two-line file naming an ID number to probe.
    /// </summary>
    [Fact]
    public async Task PreviewCsv_WithNoPermission_RevealsAnExistingFrameworksName()
    {
        var existing = BlueprintAppFactory.CompetencyFramework(idNumber: "FW-1", version: "2.0");
        existing.Name = "Confidential Exercise 2026";
        await Seed(existing);

        var preview = await PreviewCsv(Client(await Actor().SeedAsync()), Csv(FrameworkRow("FW-1")));

        Assert.Contains("Confidential Exercise 2026", preview.Error);
        Assert.Contains("version 2.0", preview.Error);
    }

    [Theory]
    [InlineData("preview-csv")]
    [InlineData("preview-json")]
    [InlineData("preview-xlsx")]
    public async Task EveryPreview_WithoutAuthentication_Is401(string route)
    {
        var response = await Post(
            AnonymousClient, route, Encoding.UTF8.GetBytes(Csv(FrameworkRow("FW-1"))));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("preview-csv")]
    [InlineData("preview-json")]
    [InlineData("preview-xlsx")]
    public async Task EveryPreview_WithAnEmptyFile_Is400(string route)
    {
        var response = await Post(Client(await Manager()), route, []);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("No file provided.", await response.Content.ReadAsStringAsync(Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private Task<TestActor> Manager() =>
        Actor().WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();

    /// <summary>A framework as Blueprint's own export writes it, which the JSON preview has a branch for.</summary>
    private static string Native(string idNumber, params string[] competencies) =>
        $$"""
        {"name":"Exported framework","source":"SEI","version":"3.0","idNumber":{{Str(idNumber)}},
         "competencies":[{{string.Join(",", competencies)}}]}
        """;

    private static string NativeCompetency(string idNumber, params string[] relatedIdNumbers) =>
        $$"""
        {"idNumber":{{Str(idNumber)}},"shortName":{{Str(idNumber)}},
         "relatedIdNumbers":[{{string.Join(",", relatedIdNumbers.Select(Str))}}]}
        """;

    /// <summary>The count reported for one element type, or zero where the type is not reported at all.</summary>
    private static int Count(CompetencyFrameworkImportPreview preview, string type) =>
        preview.ElementTypeCounts.SingleOrDefault(c => c.Type == type)?.Count ?? 0;

    /// <summary>The element types reported, in a stable order so a test can assert the whole set.</summary>
    private static string[] Types(CompetencyFrameworkImportPreview preview) =>
        preview.ElementTypeCounts.Select(c => c.Type).Order(StringComparer.Ordinal).ToArray();

    private async Task<CompetencyFrameworkImportPreview> PreviewCsv(
        HttpClient client, string csv, string source = null, string version = null) =>
        await Read<CompetencyFrameworkImportPreview>(await Post(
            client, "preview-csv", Encoding.UTF8.GetBytes(csv), source, version, "framework.csv", "text/csv"));

    private async Task<CompetencyFrameworkImportPreview> PreviewJson(HttpClient client, string json) =>
        await Read<CompetencyFrameworkImportPreview>(await Post(
            client, "preview-json", Encoding.UTF8.GetBytes(json), null, null,
            "framework.json", "application/json"));

    private async Task<CompetencyFrameworkImportPreview> PreviewXlsx(
        HttpClient client, byte[] xlsx, string source = null, string version = null) =>
        await Read<CompetencyFrameworkImportPreview>(await Post(
            client, "preview-xlsx", xlsx, source, version, "framework.xlsx", XlsxContentType));

    private Task<HttpResponseMessage> ImportCsv(
        HttpClient client, string csv, string source = null, string version = null) =>
        Post(client, "import", Encoding.UTF8.GetBytes(csv), source, version, "framework.csv", "text/csv");

    private Task<HttpResponseMessage> ImportJson(HttpClient client, string json) =>
        Post(client, "import-json", Encoding.UTF8.GetBytes(json), null, null,
            "framework.json", "application/json");

    private Task<HttpResponseMessage> ImportXlsx(
        HttpClient client, byte[] xlsx, string source = null, string version = null) =>
        Post(client, "import-xlsx", xlsx, source, version, "framework.xlsx", XlsxContentType);

    private Task<HttpResponseMessage> Post(HttpClient client, string route, byte[] file) =>
        Post(client, route, file, null, null, "framework.csv", "text/csv");

    private async Task<HttpResponseMessage> Post(
        HttpClient client,
        string route,
        byte[] file,
        string source,
        string version,
        string fileName,
        string contentType)
    {
        using var content = new MultipartFormDataContent();
        var upload = new ByteArrayContent(file);
        upload.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(upload, "file", fileName);

        var query = new List<string>();

        if (source != null)
            query.Add($"source={Uri.EscapeDataString(source)}");

        if (version != null)
            query.Add($"version={Uri.EscapeDataString(version)}");

        var url = $"api/competencyframeworks/{route}" +
            (query.Count == 0 ? "" : $"?{string.Join("&", query)}");

        // Awaited inside the using: TestServer reads the body during SendAsync, so returning the task
        // unawaited disposes the content before the request has been read.
        return await client.PostAsync(url, content, Ct);
    }

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
