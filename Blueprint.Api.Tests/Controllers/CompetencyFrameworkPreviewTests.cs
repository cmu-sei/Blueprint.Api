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
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Blueprint.Api.Tests.Support.Frameworks;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>The three preview endpoints - <c>preview-csv</c>, <c>preview-json</c> and <c>preview-xlsx</c> -
/// which answer "what would happen if I imported this file" before anybody imports it.</summary>
public class CompetencyFrameworkPreviewTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // preview-csv
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task PreviewCsv_EchoesTheSourceAndVersionFromTheQueryString()
    {
        var csv = NamedCsv();
        var client = Client(await Manager());

        var preview = await PreviewCsv(client, csv, "NICE", "5.1");

        Assert.Equal("NICE", preview.Source);
        Assert.Equal("5.1", preview.Version);
        Assert.Equal("NICE 5.1", preview.FrameworkName);
    }

    [Fact]
    public async Task ImportCsv_NamesTheFrameworkFromTheFileRatherThanTheQueryString()
    {
        var csv = NamedCsv();
        var client = Client(await Manager());

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

    /// <summary>Preview CSV counts no elements for a flat file.</summary>
    [Fact]
    public async Task PreviewCsv_CountsNoElementsForAFlatFile()
    {
        var csv = FlatCsv();
        var client = Client(await Manager());

        var preview = await PreviewCsv(client, csv);

        Assert.Null(preview.Error);
        Assert.Empty(preview.ElementTypeCounts);
        Assert.Equal(0, preview.TotalElements);
    }

    [Fact]
    public async Task ImportCsv_OfAFlatFile_ImportsEveryCompetency()
    {
        var csv = FlatCsv();
        var client = Client(await Manager());

        var imported = await Read<CompetencyFramework>(await ImportCsv(client, csv));
        Assert.Equal(2, imported.Competencies.Count);
    }

    /// <summary>Preview CSV types a row by its parents id number.</summary>
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

    /// <summary>Preview CSV counts rows by their parent.</summary>
    [Fact]
    public async Task PreviewCsv_CountsRowsByTheirParentSoItsTotalIsNotTheImports()
    {
        var csv = ParentedCsv();
        var client = Client(await Manager());

        var preview = await PreviewCsv(client, csv);

        Assert.Equal(2, preview.TotalElements);
        Assert.Equal(2, Count(preview, "task"));
        Assert.Equal(0, Count(preview, "knowledge"));
    }

    [Fact]
    public async Task ImportCsv_ImportsEveryRowWhateverItsParent()
    {
        var csv = ParentedCsv();
        var client = Client(await Manager());

        var imported = await Read<CompetencyFramework>(await ImportCsv(client, csv));
        Assert.Equal(3, imported.Competencies.Count);
    }

    /// <summary>Preview CSV reads the export id column as the cross references.</summary>
    [Fact]
    public async Task PreviewCsv_ReadsTheExportIdColumnAsTheCrossReferences()
    {
        var csv = ExportIdCsv();
        var client = Client(await Manager());

        var preview = await PreviewCsv(client, csv);

        Assert.Equal(3, preview.TotalRelationships);
    }

    [Fact]
    public async Task ImportCsv_CreatesOnlyTheCrossReferenceTheFileDeclares()
    {
        var csv = ExportIdCsv();
        var client = Client(await Manager());

        // The file declares exactly one cross-reference, and that is what the import creates.
        await ImportCsv(client, csv);
        Assert.Equal(1, await ReadBack(rb => rb.CompetencyRelationships.CountAsync(Ct)));
    }

    /// <summary>Preview CSV miscounts a row whose description contains a comma.</summary>
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
        var existing = TestData.CompetencyFramework(idNumber: "FW-1");
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
        var existing = TestData.CompetencyFramework(idNumber: "FW-1", version: "2.0");
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
        var existing = TestData.CompetencyFramework(idNumber: "FW-2");
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

    /// <summary>A skipped element type written in another case is counted as a competency, as the import imports it.</summary>
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

    /// <summary>An element with no <c>element_type</c> is not counted, and no error is reported.</summary>
    [Fact]
    public async Task PreviewJson_DoesNotCountAnElementWithoutAType()
    {
        var json = UntypedElementJson();
        var client = Client(await Manager());

        var preview = await PreviewJson(client, json);

        Assert.Null(preview.Error);
        Assert.Empty(preview.ElementTypeCounts);
        Assert.Equal(0, preview.TotalElements);
    }

    // Same case as CompetencyFrameworkImportTests.ImportJson_WithANiceFileMissingARequiredPart_Is500.
    [Fact]
    public async Task ImportJson_OfAnElementWithoutAType_Is500()
    {
        var json = UntypedElementJson();
        var client = Client(await Manager());

        var response = await ImportJson(client, json);
        Assert.Equal("The given key was not present in the dictionary.", (await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
    }

    /// <summary>The relationship total is the length of the array, so a link naming an element the file does not contain is counted.</summary>
    [Fact]
    public async Task PreviewJson_CountsEveryRelationshipWithoutCheckingIt()
    {
        var json = DanglingLinksJson();
        var client = Client(await Manager());

        var preview = await PreviewJson(client, json);

        Assert.Equal(3, preview.TotalRelationships);
    }

    [Fact]
    public async Task ImportJson_CreatesOnlyTheRelationshipsBetweenElementsInTheFile()
    {
        var json = DanglingLinksJson();
        var client = Client(await Manager());

        await ImportJson(client, json);
        Assert.Equal(1, await ReadBack(rb => rb.CompetencyRelationships.CountAsync(Ct)));
    }

    /// <summary>A link between two structural types is counted as a relationship.</summary>
    [Fact]
    public async Task PreviewJson_CountsAHierarchyLinkTheImportTurnsIntoAParent()
    {
        var json = HierarchyLinkJson();
        var client = Client(await Manager());

        var preview = await PreviewJson(client, json);

        Assert.Equal(1, preview.TotalRelationships);
    }

    [Fact]
    public async Task ImportJson_TurnsALinkBetweenStructuralTypesIntoAParent()
    {
        var json = HierarchyLinkJson();
        var client = Client(await Manager());

        var imported = await Read<CompetencyFramework>(await ImportJson(client, json));
        Assert.Equal(0, await ReadBack(rb => rb.CompetencyRelationships.CountAsync(Ct)));
        Assert.Equal(
            imported.Competencies.Single(c => c.IdNumber == "C1").Id,
            imported.Competencies.Single(c => c.IdNumber == "W1").ParentId);
    }

    [Fact]
    public async Task PreviewJson_ReportsAConflictOnTheIdNumberDerivedFromTheDocument()
    {
        var existing = TestData.CompetencyFramework(idNumber: "NICE-5.1", version: "5.1");
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

    /// <summary>A framework exported from Blueprint previews from its own fields, with no type
    /// breakdown.</summary>
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

    /// <summary>A native export's fields are matched without regard to case.</summary>
    [Fact]
    public async Task PreviewJson_ForANativeExport_MatchesTheOtherFieldsWithoutRegardToCase()
    {
        var preview = await PreviewJson(
            Client(await Manager()),
            """{"NAME":"Shouted","SOURCE":"SEI","VERSION":"3.0","IDNUMBER":"EX-1","Competencies":[]}""");

        Assert.Equal("Shouted", preview.FrameworkName);
        Assert.Equal("SEI", preview.Source);
        Assert.Equal("3.0", preview.Version);
    }

    /// <summary>
    /// The <c>competencies</c> property that decides whether the file is a native export is matched against
    /// exactly two spellings, so a file written with <c>COMPETENCIES</c> is previewed as a NICE document.
    /// </summary>
    [Fact]
    public async Task PreviewJson_WithACompetenciesPropertyInCapitals_IsNotPreviewedAsANativeExport()
    {
        var preview = await PreviewJson(
            Client(await Manager()),
            """{"NAME":"Shouted","SOURCE":"SEI","VERSION":"3.0","IDNUMBER":"EX-1","COMPETENCIES":[]}""");

        Assert.Null(preview.FrameworkName);
        Assert.Null(preview.Source);
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
        var existing = TestData.CompetencyFramework(idNumber: "EX-1");
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

    /// <summary>Distinct categories and distinct work roles are counted once each.</summary>
    [Fact]
    public async Task PreviewXlsx_CountsTheCategoriesAndWorkRoles()
    {
        var xlsx = CategoriesAndRolesXlsx();
        var client = Client(await Manager());

        var preview = await PreviewXlsx(client, xlsx);

        Assert.Equal(2, Count(preview, "category"));
        Assert.Equal(3, Count(preview, "work_role"));
        Assert.Equal(5, preview.TotalElements);
    }

    [Fact]
    public async Task ImportXlsx_ImportsEachCategoryAndWorkRoleOnce()
    {
        var xlsx = CategoriesAndRolesXlsx();
        var client = Client(await Manager());

        var imported = await Read<CompetencyFramework>(await ImportXlsx(client, xlsx));
        Assert.Equal(5, imported.Competencies.Count);
    }

    /// <summary>Preview XLSX counts a work role row twice when it repeats a code.</summary>
    [Fact]
    public async Task PreviewXlsx_CountsAWorkRoleRowTwiceWhenItRepeatsACode()
    {
        var xlsx = RepeatedRoleCodeXlsx();
        var client = Client(await Manager());

        var preview = await PreviewXlsx(client, xlsx);

        Assert.Equal(2, Count(preview, "work_role"));
        Assert.Equal(3, preview.TotalElements);
    }

    [Fact]
    public async Task ImportXlsx_ImportsARepeatedWorkRoleCodeOnce()
    {
        var xlsx = RepeatedRoleCodeXlsx();
        var client = Client(await Manager());

        var imported = await Read<CompetencyFramework>(await ImportXlsx(client, xlsx));
        Assert.Equal(2, imported.Competencies.Count);
    }

    /// <summary>A category cell whose brackets are empty is counted as a category.</summary>
    [Fact]
    public async Task PreviewXlsx_CountsACategoryWhoseCodeIsBlank()
    {
        var xlsx = BlankCategoryCodeXlsx();
        var client = Client(await Manager());

        var preview = await PreviewXlsx(client, xlsx);

        Assert.Equal(2, Count(preview, "category"));
    }

    [Fact]
    public async Task ImportXlsx_DropsACategoryWhoseCodeIsBlank()
    {
        var xlsx = BlankCategoryCodeXlsx();
        var client = Client(await Manager());

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
        var xlsx = UndescribedTksaXlsx();
        var client = Client(await Manager());

        var preview = await PreviewXlsx(client, xlsx);

        Assert.Equal(1, Count(preview, "task"));
        Assert.Equal(2, preview.TotalElements);
    }

    [Fact]
    public async Task ImportXlsx_DropsATksaWithNoDescription()
    {
        var xlsx = UndescribedTksaXlsx();
        var client = Client(await Manager());

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

    /// <summary>Preview XLSX counts every role sheet row as a relationship.</summary>
    [Fact]
    public async Task PreviewXlsx_CountsEveryRoleSheetRowAsARelationship()
    {
        var xlsx = RoleSheetRelationshipsXlsx();
        var client = Client(await Manager());

        var preview = await PreviewXlsx(client, xlsx);

        Assert.Equal(4, preview.TotalRelationships);
    }

    [Fact]
    public async Task ImportXlsx_CreatesOneRelationshipForTheRoleSheet()
    {
        var xlsx = RoleSheetRelationshipsXlsx();
        var client = Client(await Manager());

        await ImportXlsx(client, xlsx);
        Assert.Equal(1, await ReadBack(rb => rb.CompetencyRelationships.CountAsync(Ct)));
    }

    /// <summary>
    /// The first cell of a role-sheet row is the first cell in document order, not the cell in column A, so
    /// a row whose only value sits in column B is counted - and the importer, which reads column A by
    /// reference, drops it.
    /// </summary>
    [Fact]
    public async Task PreviewXlsx_CountsARoleSheetRowWhoseFirstCellIsNotInColumnA()
    {
        var xlsx = OffsetRoleRowXlsx();
        var client = Client(await Manager());

        var preview = await PreviewXlsx(client, xlsx);

        Assert.Equal(1, preview.TotalRelationships);
    }

    [Fact]
    public async Task ImportXlsx_CreatesNoRelationshipForARoleSheetRowWhoseFirstCellIsNotInColumnA()
    {
        var xlsx = OffsetRoleRowXlsx();
        var client = Client(await Manager());

        await ImportXlsx(client, xlsx);
        Assert.Equal(0, await ReadBack(rb => rb.CompetencyRelationships.CountAsync(Ct)));
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
    }

    // ---------------------------------------------------------------------------------------------
    // preview-xlsx, the single-sheet shape no importer accepts
    // ---------------------------------------------------------------------------------------------

    /// <summary>A workbook that is not DCWF falls back to a one-sheet shape, an ID number in column A and related IDs in column E, and is previewed.</summary>
    [Fact]
    public async Task PreviewXlsx_PreviewsASingleSheetFileTheImporterRefuses()
    {
        var xlsx = SingleSheetXlsx();
        var client = Client(await Manager());

        var preview = await PreviewXlsx(client, xlsx);

        Assert.Null(preview.Error);
        Assert.Equal(2, preview.TotalElements);
    }

    // Same case as CompetencyFrameworkDcwfImportTests.Import_WithoutTheTwoRequiredSheetNames_Is500.
    [Fact]
    public async Task ImportXlsx_OfASingleSheetFile_Is500()
    {
        var xlsx = SingleSheetXlsx();
        var client = Client(await Manager());

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
        var xlsx = OneRequiredSheetXlsx();
        var client = Client(await Manager());

        var preview = await PreviewXlsx(client, xlsx);

        Assert.Null(preview.Error);
        Assert.Empty(preview.ElementTypeCounts);
        Assert.Equal(0, preview.TotalElements);
    }

    // Same case as CompetencyFrameworkDcwfImportTests.Import_WithoutTheTwoRequiredSheetNames_Is500.
    [Fact]
    public async Task ImportXlsx_WithOnlyOneOfTheTwoRequiredSheets_Is500()
    {
        var xlsx = OneRequiredSheetXlsx();
        var client = Client(await Manager());

        var response = await ImportXlsx(client, xlsx);
        Assert.Equal("DCWF XLSX must have 'DCWF Roles' and 'Master Task & KSA List' sheets.", (await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
    }

    /// <summary>
    /// The conflict check runs before the file is opened, so an unreadable file uploaded under a source and
    /// version already imported reports the conflict rather than the parse failure.
    /// </summary>
    [Fact]
    public async Task PreviewXlsx_ReportsAConflictBeforeItOpensTheFile()
    {
        var existing = TestData.CompetencyFramework(idNumber: "DCWF-1.0", version: "1.0");
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
    // What the three previews ask of the caller
    // ---------------------------------------------------------------------------------------------

    /// <summary>The CSV preview requires <c>ManageCompetencyFrameworks</c>, as the importers beside it do.</summary>
    /// <remarks>
    /// What the permission protects is not the parse but the conflict check, which runs before anything
    /// else and names the framework holding the ID number and its version
    /// (<see cref="PreviewCsv_ReportsAFrameworkIdNumberAlreadyTaken"/>). Without it, a caller who cannot
    /// list frameworks could learn that one exists and what it is called by uploading a two-line file
    /// naming an ID number to probe.
    /// </remarks>
    [Fact]
    public async Task PreviewCsv_is_forbidden_for_a_caller_holding_only_ViewCompetencyFrameworks()
    {
        var response = await Post(
            Client(await Viewer()), "preview-csv", Encoding.UTF8.GetBytes(Csv(FrameworkRow("FW-1"))));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PreviewJson_is_forbidden_for_a_caller_holding_only_ViewCompetencyFrameworks()
    {
        var response = await Post(
            Client(await Viewer()), "preview-json", Encoding.UTF8.GetBytes(Csv(FrameworkRow("FW-1"))));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PreviewXlsx_is_forbidden_for_a_caller_holding_only_ViewCompetencyFrameworks()
    {
        var response = await Post(
            Client(await Viewer()), "preview-xlsx", Encoding.UTF8.GetBytes(Csv(FrameworkRow("FW-1"))));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("preview-csv")]
    [InlineData("preview-json")]
    [InlineData("preview-xlsx")]
    public async Task EveryPreview_WithoutAuthentication_Is401(string route)
    {
        var response = await Post(
            Client(), route, Encoding.UTF8.GetBytes(Csv(FrameworkRow("FW-1"))));

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

    private static string NamedCsv() =>
        Csv(FrameworkRow("FW-1", "The name in the file"), CompetencyRow("C2", parent: "C1"));

    private static string FlatCsv() =>
        Csv(FrameworkRow("FW-1"), CompetencyRow("C1"), CompetencyRow("C2"));

    private static string ParentedCsv() =>
        Csv(
            FrameworkRow("FW-1"),
            CompetencyRow("T-1"),
            CompetencyRow("K-1", parent: "T-1"),
            CompetencyRow("K-2", parent: "T-1"));

    private static string ExportIdCsv() =>
        Csv(
            FrameworkRow("FW-1"),
            CompetencyRow("C1"),
            Row(
                parentIdNumber: "C1",
                idNumber: "C2",
                shortName: "two",
                relatedIdNumbers: "C1",
                exportId: "X5|X6|X7"));

    private static string UntypedElementJson() =>
        """
            {"documents":[{"name":"F","version":"1","doc_identifier":"D"}],
             "elements":[{"element_identifier":"T1"}],"relationships":[]}
            """;

    private static string DanglingLinksJson() =>
        Nice(
            [Element("W1", "work_role"), Element("T1", "task")],
            [Link("W1", "T1"), Link("W1", "MISSING"), Link("MISSING", "T1")]);

    private static string HierarchyLinkJson() =>
        Nice(
            [Element("C1", "category"), Element("W1", "work_role")], [Link("C1", "W1")]);

    private static byte[] CategoriesAndRolesXlsx() =>
        Dcwf(roles:
        [
            RoleRow(category: Category("Information Technology", "IT"), roleName: "A", roleCode: "411"),
            RoleRow(roleName: "B", roleCode: "412"),
            RoleRow(category: Category("Securely Provision", "SP"), roleName: "C", roleCode: "141")
        ]);

    private static byte[] RepeatedRoleCodeXlsx() =>
        Dcwf(roles:
        [
            RoleRow(category: AnyCategory, roleName: "First", roleCode: "411"),
            RoleRow(roleName: "Second", roleCode: "411")
        ]);

    private static byte[] BlankCategoryCodeXlsx() =>
        Dcwf(roles:
        [
            RoleRow(category: Category("Nameless", "")),
            RoleRow(category: Category("Information Technology", "IT"))
        ]);

    private static byte[] UndescribedTksaXlsx() =>
        Dcwf(roles: [RoleRow(category: AnyCategory)], tksas: [Tksa("1", "Task", null)]);

    private static byte[] RoleSheetRelationshipsXlsx() =>
        Dcwf(
            roles: [RoleRow(category: AnyCategory, roleName: "Support", roleCode: "411")],
            tksas: [Tksa("1", "Task", "a")],
            RoleSheet("IT-411", "Support",
                Requires("1", "Task"),
                Requires("999", "Task"),
                Requires("1", "Competency")),
            RoleSheet("IT-999", "No such role", Requires("1", "Task")));

    private static byte[] OffsetRoleRowXlsx() =>
        Dcwf(
            roles: [RoleRow(category: AnyCategory, roleName: "Support", roleCode: "411")],
            tksas: [Tksa("1", "Task", "a")],
            RoleSheet("IT-411", "Support", Requires(null, "Task")));

    private static byte[] SingleSheetXlsx() =>
        SingleSheet(["ID number"], SimpleRow("T-1"), SimpleRow("K-1"));

    private static byte[] OneRequiredSheetXlsx() =>
        Workbooks.Build(
            RolesSheet(RoleRow(category: AnyCategory, roleName: "Support", roleCode: "411")));

    private Task<TestActor> Viewer() =>
        Actor().WithSystemPermissions(SystemPermission.ViewCompetencyFrameworks).SeedAsync();

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
