// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
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
/// The third importer: the DoD Cyber Workforce Framework's own spreadsheet, uploaded to
/// <c>competencyframeworks/import-xlsx</c>.
/// </summary>
/// <remarks>
/// <para>
/// This one reads a published workbook rather than an export format, so it is built entirely out of
/// positions in that workbook: the categories and work roles come from rows 3 onwards of a sheet that must
/// be called <c>DCWF Roles</c>, reading columns B, C, D and E; the tasks, knowledge, skills and abilities
/// come from rows 2 onwards of a sheet that must be called <c>Master Task &amp; KSA List</c>, reading
/// columns A, D and E; and the relationships come from one sheet per work role, named
/// <c>(CODE) Role Name</c>, from row 7 onwards. None of that is discoverable from the file, and none of it
/// is configurable. A workbook whose author inserted a row, renamed a sheet, or changed the column order
/// imports as something between "partly" and "not at all", and the only report is the count of what
/// arrived.
/// </para>
/// <para>
/// The sheet names are matched with <c>==</c>, so <c>dcwf roles</c> is not <c>DCWF Roles</c>
/// (<see cref="Import_WithoutTheTwoRequiredSheetNames_Is500"/>) - while the <em>type</em> column inside the
/// sheets is upper-cased before matching, so <c>task</c> and <c>TASK</c> are both accepted. Two conventions
/// in one importer.
/// </para>
/// <para>
/// Three near-identical dictionary writes decide what a repeated identifier does, and they disagree.
/// Categories and TKSAs are guarded with <c>ContainsKey</c>, so the first row wins; work roles are written
/// through the indexer, so the <em>last</em> row wins
/// (<see cref="Import_ForARepeatedWorkRoleCode_KeepsTheLastRow"/>). Nothing about the format suggests the
/// distinction is deliberate.
/// </para>
/// <para>
/// Failures are all <c>ArgumentException</c>, so a workbook missing its sheets and a workbook with nothing
/// in them are both a <em>500</em> - as is any file that is not a spreadsheet at all, since
/// <c>SpreadsheetDocument.Open</c> throws. Only a missing or empty upload is the 400 it should be. The
/// action declares only <c>Created</c>, so a generated client knows about none of them; that belongs on the
/// Phase 4 contract list.
/// </para>
/// <para>
/// The preview endpoint for the same file is in <see cref="CompetencyFrameworkPreviewTests"/>, which is
/// also where the two are compared - they read the same workbook through different code and reach different
/// numbers.
/// </para>
/// </remarks>
public class CompetencyFrameworkDcwfImportTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // The framework itself
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Import_BuildsTheFrameworkFromTheQueryStringAlone()
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Dcwf(roles: [RoleRow(category: Category("Information Technology", "IT"))]),
            source: "DCWF",
            version: "1.0");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal("DCWF 1.0", framework.Name);
        Assert.Equal("DCWF-1.0", framework.IdNumber);
        Assert.Equal("Imported from DCWF 1.0", framework.Description);
        Assert.Equal("DCWF", framework.Source);
        Assert.Equal("1.0", framework.Version);
        Assert.Equal("Category,Work Role,Task,Knowledge,Skill,Ability", framework.Taxonomies);
    }

    [Fact]
    public async Task Import_StampsTheCallerAsTheCreator()
    {
        var actor = await Manager();

        var response = await ImportXlsx(
            Client(actor), Dcwf(roles: [RoleRow(category: Category("Information Technology", "IT"))]));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal(actor.Id, framework.CreatedBy);
        Assert.Equal(actor.Id, framework.Competencies.Single().CreatedBy);
    }

    [Fact]
    public async Task Import_ReturnsALocationHeaderForTheFramework()
    {
        var response = await ImportXlsx(Client(await Manager()), Dcwf(roles: [RoleRow(category: AnyCategory)]));

        var framework = await Read<CompetencyFramework>(response);
        Assert.EndsWith(
            $"/api/competencyframeworks/{framework.Id}", response.Headers.Location?.ToString());
    }

    /// <summary>
    /// The version is appended to the source to make the ID number, so successive editions of the same
    /// framework do not collide - unless the source already carries the version, in which case it is used
    /// as it stands.
    /// </summary>
    [Theory]
    [InlineData("DCWF", "1.0", "DCWF-1.0")]
    [InlineData("DCWF 1.0", "1.0", "DCWF 1.0")]
    [InlineData("DCWF-1.0", "1.0", "DCWF-1.0")]
    [InlineData("  DCWF  ", "  1.0  ", "DCWF-1.0")]
    [InlineData("DCWF", "", "DCWF")]
    public async Task Import_DerivesTheFrameworkIdNumberFromSourceAndVersion(
        string source, string version, string expected)
    {
        var response = await ImportXlsx(
            Client(await Manager()), Dcwf(roles: [RoleRow(category: AnyCategory)]), source, version);

        Assert.Equal(expected, (await Read<CompetencyFramework>(response)).IdNumber);
    }

    /// <summary>
    /// Characterizes what an upload with neither query parameter produces: a framework named <c>" "</c>,
    /// described as "Imported from DCWF ", and with no ID number at all - so the duplicate check that ID
    /// number exists for cannot fire, and the same workbook can be imported repeatedly.
    /// </summary>
    /// <remarks>
    /// Both parameters are optional to MVC and neither importer validates them. The CSV importer at least
    /// takes the framework's name from the file; this one has nowhere else to look, because a DCWF workbook
    /// does not say which edition it is.
    /// </remarks>
    [Fact]
    public async Task Import_WithNoSourceOrVersion_NamesTheFrameworkASingleSpace()
    {
        var client = Client(await Manager());

        var first = await Read<CompetencyFramework>(
            await ImportXlsx(client, Dcwf(roles: [RoleRow(category: AnyCategory)])));
        var second = await Read<CompetencyFramework>(
            await ImportXlsx(client, Dcwf(roles: [RoleRow(category: AnyCategory)])));

        Assert.Equal(" ", first.Name);
        Assert.Equal("Imported from DCWF ", first.Description);
        Assert.Null(first.IdNumber);
        Assert.Null(second.IdNumber);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task Import_ForAFrameworkIdNumberAlreadyPresent_Is409()
    {
        var existing = BlueprintAppFactory.CompetencyFramework(idNumber: "DCWF-1.0", version: "1.0");
        existing.Name = "The one already here";
        await Seed(existing);

        var response = await ImportXlsx(
            Client(await Manager()), Dcwf(roles: [RoleRow(category: AnyCategory)]), "DCWF", "1.0");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("The one already here", (await ReadError(response)).Title);
        Assert.Equal(1, await NewContext().CompetencyFrameworks.CountAsync(Ct));
    }

    /// <summary>
    /// Unlike the CSV importer, this one also refuses a source and version pair already imported - so
    /// renaming the ID number is not enough to get a second copy of DCWF 1.0 in.
    /// </summary>
    [Fact]
    public async Task Import_ForAnAlreadyImportedSourceAndVersion_Is409()
    {
        var existing = BlueprintAppFactory.CompetencyFramework(
            idNumber: "SOMETHING-ELSE", source: "DCWF", version: "1.0");
        existing.Name = "DCWF as imported last week";
        await Seed(existing);

        var response = await ImportXlsx(
            Client(await Manager()), Dcwf(roles: [RoleRow(category: AnyCategory)]), "DCWF", "1.0");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await ReadError(response);
        Assert.Contains("DCWF as imported last week", error.Title);
        Assert.Contains("source 'DCWF' version '1.0'", error.Title);
    }

    // ---------------------------------------------------------------------------------------------
    // The DCWF Roles sheet - categories and work roles
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The shape of the roles sheet: the category's name and code share one cell in column B separated by a
    /// newline, its description is in column C, and the work role's name and code number are in D and E.
    /// The role's own ID number is the category's code and its code number joined with a hyphen, which is
    /// how DCWF itself writes them ("IT-411").
    /// </summary>
    [Fact]
    public async Task Import_CreatesACategoryAndTheWorkRoleBeneathIt()
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Dcwf(roles:
            [
                RoleRow(
                    category: Category("Information Technology", "IT"),
                    categoryDescription: "Builds and runs the systems",
                    roleName: "Technical Support Specialist",
                    roleCode: "411")
            ]));

        var framework = await Read<CompetencyFramework>(response);
        var category = Competency(framework, "IT");
        Assert.Equal("Information Technology", category.ShortName);
        Assert.Equal("Builds and runs the systems", category.Description);
        Assert.Null(category.ParentId);
        Assert.Equal($"/{category.Id}", category.Path);

        var role = Competency(framework, "IT-411");
        Assert.Equal("Technical Support Specialist", role.ShortName);
        Assert.Equal("Technical Support Specialist", role.Description);
        Assert.Equal(category.Id, role.ParentId);
        Assert.Equal($"/{category.Id}/{role.Id}", role.Path);
    }

    /// <summary>
    /// A category with no description of its own is described by its own name rather than left blank, so the
    /// UI has something to show either way.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Import_ForACategoryWithNoDescription_DescribesItByItsName(string description)
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Dcwf(roles: [RoleRow(category: Category("Information Technology", "IT"), categoryDescription: description)]));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal("Information Technology", Competency(framework, "IT").Description);
    }

    /// <summary>
    /// The category cell is filled in only on the first row of each group, so the importer remembers the
    /// last one it saw and hands it to every role beneath. That memory is what makes the sheet's row order
    /// load-bearing.
    /// </summary>
    [Fact]
    public async Task Import_CarriesTheCategoryDownTheRowsBeneathIt()
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Dcwf(roles:
            [
                RoleRow(category: Category("Information Technology", "IT"), roleName: "First", roleCode: "411"),
                RoleRow(roleName: "Second", roleCode: "412"),
                RoleRow(category: Category("Securely Provision", "SP"), roleName: "Third", roleCode: "141"),
                RoleRow(roleName: "Fourth", roleCode: "142")
            ]));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal(Competency(framework, "IT").Id, Competency(framework, "IT-411").ParentId);
        Assert.Equal(Competency(framework, "IT").Id, Competency(framework, "IT-412").ParentId);
        Assert.Equal(Competency(framework, "SP").Id, Competency(framework, "SP-141").ParentId);
        Assert.Equal(Competency(framework, "SP").Id, Competency(framework, "SP-142").ParentId);
    }

    /// <summary>
    /// A work role above the first category in the sheet is dropped without a word. There is nothing to
    /// build its ID number out of, so this is the least bad option available to the importer - but it is the
    /// shape a workbook takes when someone sorts the sheet by role name, and it loses every role.
    /// </summary>
    [Fact]
    public async Task Import_DropsAWorkRoleThatPrecedesEveryCategory()
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Dcwf(roles:
            [
                RoleRow(roleName: "Orphan", roleCode: "411"),
                RoleRow(category: Category("Information Technology", "IT"), roleName: "Adopted", roleCode: "412")
            ]));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal(["IT", "IT-412"], framework.Competencies.Select(c => c.IdNumber).Order(StringComparer.Ordinal));
    }

    /// <summary>A work role needs both a name and a code number; either one alone is not a row.</summary>
    [Theory]
    [InlineData(null, "411")]
    [InlineData("", "411")]
    [InlineData("   ", "411")]
    [InlineData("Technical Support Specialist", null)]
    [InlineData("Technical Support Specialist", "")]
    [InlineData("Technical Support Specialist", "   ")]
    public async Task Import_DropsAWorkRoleMissingItsNameOrItsCode(string roleName, string roleCode)
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Dcwf(roles: [RoleRow(category: AnyCategory, roleName: roleName, roleCode: roleCode)]));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal(["IT"], framework.Competencies.Select(c => c.IdNumber));
    }

    /// <summary>
    /// The category cell is recognised by containing a bracket of each kind and by having a second line.
    /// Anything else is not a category - and, because the cell is also not an error, the rows beneath it
    /// inherit whichever category came before.
    /// </summary>
    [Theory]
    [InlineData("Information Technology")]
    [InlineData("Information Technology\nIT")]
    [InlineData("Information Technology (IT)")]
    [InlineData("Information Technology\n(IT")]
    [InlineData("Information Technology\nIT)")]
    public async Task Import_DoesNotRecogniseACategoryCellWrittenAnyOtherWay(string categoryText)
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Dcwf(roles: [RoleRow(category: categoryText, roleName: "Role", roleCode: "411")]));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("No competencies found in DCWF spreadsheet.", (await ReadError(response)).Title);
    }

    /// <summary>
    /// Only the second line of the category cell is read for the code, so a cell carrying its code on a
    /// third line produces a category whose ID number is that second line stripped of brackets.
    /// </summary>
    [Fact]
    public async Task Import_TakesTheCategoryCodeFromTheSecondLineOfTheCell()
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Dcwf(roles: [RoleRow(category: "Information Technology\nsubtitle\n(IT)")]));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal(["subtitle"], framework.Competencies.Select(c => c.IdNumber));
        Assert.Equal("Information Technology", framework.Competencies.Single().ShortName);
    }

    /// <summary>
    /// A repeated category code keeps the first row's name and description. DCWF's own sheet repeats the
    /// category on every row of a group in some editions, which is why the guard is there.
    /// </summary>
    [Fact]
    public async Task Import_ForARepeatedCategoryCode_KeepsTheFirstRow()
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Dcwf(roles:
            [
                RoleRow(category: Category("First name", "IT")),
                RoleRow(category: Category("Second name", "IT"))
            ]));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal("First name", Competency(framework, "IT").ShortName);
    }

    /// <summary>
    /// A repeated work role code keeps the <em>last</em> row - the opposite of what a repeated category or
    /// TKSA does, because this is the one of the three dictionary writes made through the indexer rather
    /// than behind a <c>ContainsKey</c> guard.
    /// </summary>
    /// <remarks>
    /// Characterization of an inconsistency rather than of a defect: either rule is defensible, and neither
    /// is written down. It reddens if the three are ever made to agree, whichever way they are made to
    /// agree - which is the point of pinning it.
    /// </remarks>
    [Fact]
    public async Task Import_ForARepeatedWorkRoleCode_KeepsTheLastRow()
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Dcwf(roles:
            [
                RoleRow(category: AnyCategory, roleName: "First name", roleCode: "411"),
                RoleRow(roleName: "Second name", roleCode: "411")
            ]));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal("Second name", Competency(framework, "IT-411").ShortName);
        Assert.Equal(2, framework.Competencies.Count);
    }

    /// <summary>
    /// The first two rows of the sheet are the workbook's title and its header, and are skipped by position
    /// rather than by looking at them - so a workbook with one fewer row above the data loses its first
    /// category, and one with an extra row loses nothing but shifts everything.
    /// </summary>
    [Fact]
    public async Task Import_SkipsTheFirstTwoRowsOfTheRolesSheetWhateverIsInThem()
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Workbooks.Build(
                new Workbooks.Sheet(
                    "DCWF Roles",
                    RoleRow(category: Category("Skipped as a title", "T1")),
                    RoleRow(category: Category("Skipped as a header", "T2")),
                    RoleRow(category: Category("Read", "IT"))),
                TasksSheet()));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal(["IT"], framework.Competencies.Select(c => c.IdNumber));
    }

    /// <summary>
    /// Sort order is assigned as the rows are read, so the categories and roles interleave in the order the
    /// sheet lists them and the TKSAs follow on behind.
    /// </summary>
    [Fact]
    public async Task Import_NumbersTheCompetenciesInReadingOrder()
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Dcwf(
                roles:
                [
                    RoleRow(category: Category("Information Technology", "IT"), roleName: "First", roleCode: "411"),
                    RoleRow(roleName: "Second", roleCode: "412")
                ],
                tksas: [Tksa("390A", "Task", "Do the thing")]));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal(
            ["IT", "IT-411", "IT-412", "T-390A"],
            framework.Competencies.OrderBy(c => c.SortOrder).Select(c => c.IdNumber));
        Assert.Equal([0, 1, 2, 3], framework.Competencies.Select(c => c.SortOrder).Order());
    }

    // ---------------------------------------------------------------------------------------------
    // The Master Task & KSA List sheet
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The TKSA sheet's type column decides the ID number's prefix, which is the only thing that
    /// distinguishes a task from a knowledge statement afterwards - the entity carries no type of its own.
    /// </summary>
    [Fact]
    public async Task Import_PrefixesEachTksaAccordingToItsType()
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Dcwf(
                roles: [RoleRow(category: AnyCategory)],
                tksas:
                [
                    Tksa("1", "Task", "A task"),
                    Tksa("2", "Knowledge", "Some knowledge"),
                    Tksa("3", "Skill", "A skill"),
                    Tksa("4", "Ability", "An ability")
                ]));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal("A task", Competency(framework, "T-1").Description);
        Assert.Equal("Some knowledge", Competency(framework, "K-2").Description);
        Assert.Equal("A skill", Competency(framework, "S-3").Description);
        Assert.Equal("An ability", Competency(framework, "A-4").Description);
    }

    /// <summary>A TKSA has no parent: the sheet says nothing about which role owns it.</summary>
    [Fact]
    public async Task Import_LeavesEveryTksaAtTheRootOfTheFramework()
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Dcwf(roles: [RoleRow(category: AnyCategory)], tksas: [Tksa("1", "Task", "A task")]));

        var framework = await Read<CompetencyFramework>(response);
        var task = Competency(framework, "T-1");
        Assert.Null(task.ParentId);
        Assert.Equal($"/{task.Id}", task.Path);
    }

    [Theory]
    [InlineData("Task")]
    [InlineData("task")]
    [InlineData("TASK")]
    [InlineData("tAsK")]
    public async Task Import_MatchesTheTksaTypeWithoutRegardToCase(string type)
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Dcwf(roles: [RoleRow(category: AnyCategory)], tksas: [Tksa("1", type, "A task")]));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Contains("T-1", framework.Competencies.Select(c => c.IdNumber));
    }

    /// <summary>
    /// A type the switch does not recognise produces no prefix, and a row with no prefix is dropped. So is a
    /// row missing its number or its description - the description is the competency's whole content, and
    /// the number is its identity.
    /// </summary>
    [Theory]
    [InlineData("Competency", "1", "A description")]
    [InlineData("Tasks", "1", "A description")]
    [InlineData("", "1", "A description")]
    [InlineData("Task", null, "A description")]
    [InlineData("Task", "", "A description")]
    [InlineData("Task", "   ", "A description")]
    [InlineData("Task", "1", null)]
    [InlineData("Task", "1", "")]
    [InlineData("Task", "1", "   ")]
    public async Task Import_DropsATksaRowItCannotRead(string type, string number, string description)
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Dcwf(roles: [RoleRow(category: AnyCategory)], tksas: [Tksa(number, type, description)]));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal(["IT"], framework.Competencies.Select(c => c.IdNumber));
    }

    /// <summary>
    /// A repeated TKSA id keeps the first row. The comment in the importer explains why - DCWF numbers
    /// variants of one statement as "390" and "390A", and the sheet lists both against the same number in
    /// some editions.
    /// </summary>
    [Fact]
    public async Task Import_ForARepeatedTksaId_KeepsTheFirstRow()
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Dcwf(
                roles: [RoleRow(category: AnyCategory)],
                tksas: [Tksa("390", "Task", "The first one"), Tksa("390", "Task", "The second one")]));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal("The first one", Competency(framework, "T-390").Description);
    }

    /// <summary>
    /// The prefix is part of the id, so the same number under two types is two competencies rather than a
    /// duplicate.
    /// </summary>
    [Fact]
    public async Task Import_TreatsOneNumberUnderTwoTypesAsTwoCompetencies()
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Dcwf(
                roles: [RoleRow(category: AnyCategory)],
                tksas: [Tksa("390", "Task", "As a task"), Tksa("390", "Skill", "As a skill")]));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal("As a task", Competency(framework, "T-390").Description);
        Assert.Equal("As a skill", Competency(framework, "S-390").Description);
    }

    /// <summary>
    /// A TKSA's description is often a paragraph, so the short name is the first hundred characters of it
    /// with an ellipsis. The full text stays in the description; nothing is lost.
    /// </summary>
    [Fact]
    public async Task Import_ShortensATksaDescriptionOverAHundredCharacters()
    {
        var description = new string('x', 150);

        var response = await ImportXlsx(
            Client(await Manager()),
            Dcwf(roles: [RoleRow(category: AnyCategory)], tksas: [Tksa("1", "Task", description)]));

        var framework = await Read<CompetencyFramework>(response);
        var task = Competency(framework, "T-1");
        Assert.Equal(new string('x', 100) + "...", task.ShortName);
        Assert.Equal(description, task.Description);
    }

    /// <summary>A description of exactly a hundred characters is used as it stands.</summary>
    [Fact]
    public async Task Import_KeepsATksaDescriptionOfExactlyAHundredCharacters()
    {
        var description = new string('x', 100);

        var response = await ImportXlsx(
            Client(await Manager()),
            Dcwf(roles: [RoleRow(category: AnyCategory)], tksas: [Tksa("1", "Task", description)]));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal(description, Competency(framework, "T-1").ShortName);
    }

    /// <summary>
    /// Only the header row is skipped on this sheet, where the roles sheet skips two - so a workbook whose
    /// two sheets have the same number of rows above their data loses a row from one of them.
    /// </summary>
    [Fact]
    public async Task Import_SkipsOnlyTheFirstRowOfTheTasksSheet()
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Workbooks.Build(
                RolesSheet(RoleRow(category: AnyCategory)),
                new Workbooks.Sheet(
                    "Master Task & KSA List",
                    Tksa("1", "Task", "Skipped as a header"),
                    Tksa("2", "Task", "Read"))));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal(["IT", "T-2"], framework.Competencies.Select(c => c.IdNumber).Order(StringComparer.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------
    // The per-role sheets - relationships
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Each work role has a sheet of its own listing the TKSAs it requires, and that list becomes the
    /// relationships. The sheet is matched to the role by the code in brackets at the front of its name.
    /// </summary>
    [Fact]
    public async Task Import_CreatesTheRelationshipsFromTheRoleSheets()
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Dcwf(
                roles: [RoleRow(category: AnyCategory, roleName: "Support", roleCode: "411")],
                tksas: [Tksa("1", "Task", "A task"), Tksa("2", "Knowledge", "Some knowledge")],
                RoleSheet("IT-411", "Support", Requires("1", "Task"), Requires("2", "Knowledge"))));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal(
            ["K-2", "T-1"],
            Competency(framework, "IT-411").RelatedIdNumbers.Order(StringComparer.Ordinal));
        Assert.Equal(["IT-411"], Competency(framework, "T-1").RelatedIdNumbers);
        Assert.Equal(["IT-411"], Competency(framework, "K-2").RelatedIdNumbers);
    }

    /// <summary>
    /// The relationship is stored once, on the role, and reported from both ends - the same rule the other
    /// two importers follow.
    /// </summary>
    [Fact]
    public async Task Import_StoresEachRelationshipOnceOnTheRole()
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Dcwf(
                roles: [RoleRow(category: AnyCategory, roleName: "Support", roleCode: "411")],
                tksas: [Tksa("1", "Task", "A task")],
                RoleSheet("IT-411", "Support", Requires("1", "Task"))));

        var framework = await Read<CompetencyFramework>(response);
        var relationship = await NewContext().CompetencyRelationships.SingleAsync(Ct);
        Assert.Equal(Competency(framework, "IT-411").Id, relationship.CompetencyId);
        Assert.Equal(Competency(framework, "T-1").Id, relationship.RelatedCompetencyId);
    }

    [Fact]
    public async Task Import_ForARoleSheetListingATksaTwice_StoresOneRelationship()
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Dcwf(
                roles: [RoleRow(category: AnyCategory, roleName: "Support", roleCode: "411")],
                tksas: [Tksa("1", "Task", "A task")],
                RoleSheet("IT-411", "Support", Requires("1", "Task"), Requires("1", "Task"))));

        await Read<CompetencyFramework>(response);
        Assert.Equal(1, await NewContext().CompetencyRelationships.CountAsync(Ct));
    }

    /// <summary>
    /// A TKSA the role sheet names but the master list does not define is dropped, so a workbook whose role
    /// sheets are one edition ahead of its master list imports with fewer relationships than it lists - and
    /// says nothing about it. <see cref="CompetencyFrameworkPreviewTests"/> shows the preview counting these
    /// anyway.
    /// </summary>
    [Fact]
    public async Task Import_IgnoresATksaTheMasterListDoesNotDefine()
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Dcwf(
                roles: [RoleRow(category: AnyCategory, roleName: "Support", roleCode: "411")],
                tksas: [Tksa("1", "Task", "A task")],
                RoleSheet("IT-411", "Support", Requires("1", "Task"), Requires("999", "Task"))));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal(["T-1"], Competency(framework, "IT-411").RelatedIdNumbers);
    }

    /// <summary>A role sheet whose code matches no work role is skipped entirely.</summary>
    [Fact]
    public async Task Import_IgnoresARoleSheetForACodeItHasNoRoleFor()
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Dcwf(
                roles: [RoleRow(category: AnyCategory, roleName: "Support", roleCode: "411")],
                tksas: [Tksa("1", "Task", "A task")],
                RoleSheet("IT-999", "Not imported", Requires("1", "Task"))));

        await Read<CompetencyFramework>(response);
        Assert.Equal(0, await NewContext().CompetencyRelationships.CountAsync(Ct));
    }

    /// <summary>
    /// A sheet is a role sheet only if its name opens with a bracket and closes one somewhere, so the two
    /// required sheets are passed over on the same test that proves an arbitrary extra sheet is.
    /// </summary>
    [Theory]
    [InlineData("IT-411 Support")]
    [InlineData("Sheet (IT-411)")]
    [InlineData("(IT-411 Support")]
    public async Task Import_IgnoresASheetThatIsNotNamedLikeARoleSheet(string sheetName)
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Workbooks.Build(
                RolesSheet(RoleRow(category: AnyCategory, roleName: "Support", roleCode: "411")),
                TasksSheet(Tksa("1", "Task", "A task")),
                new Workbooks.Sheet(
                    sheetName,
                    [], [], [], [], [], [],
                    Requires("1", "Task"))));

        await Read<CompetencyFramework>(response);
        Assert.Equal(0, await NewContext().CompetencyRelationships.CountAsync(Ct));
    }

    /// <summary>
    /// The first six rows of a role sheet are the workbook's preamble, skipped by position. A file with one
    /// fewer preamble row loses its first requirement.
    /// </summary>
    [Fact]
    public async Task Import_SkipsTheFirstSixRowsOfARoleSheet()
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Workbooks.Build(
                RolesSheet(RoleRow(category: AnyCategory, roleName: "Support", roleCode: "411")),
                TasksSheet(Tksa("1", "Task", "A task"), Tksa("2", "Task", "Another task")),
                new Workbooks.Sheet(
                    "(IT-411) Support",
                    [], [], [], [], [],
                    Requires("1", "Task"),
                    Requires("2", "Task"))));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal(["T-2"], Competency(framework, "IT-411").RelatedIdNumbers);
    }

    /// <summary>
    /// A role sheet row is read for its number and its type, and a row the type switch does not recognise is
    /// dropped - the same rule as the master list, and the same silence.
    /// </summary>
    [Theory]
    [InlineData(null, "Task")]
    [InlineData("", "Task")]
    [InlineData("   ", "Task")]
    [InlineData("1", "Competency")]
    [InlineData("1", "")]
    [InlineData("1", null)]
    public async Task Import_DropsARoleSheetRowItCannotRead(string number, string type)
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Dcwf(
                roles: [RoleRow(category: AnyCategory, roleName: "Support", roleCode: "411")],
                tksas: [Tksa("1", "Task", "A task")],
                RoleSheet("IT-411", "Support", Requires(number, type))));

        await Read<CompetencyFramework>(response);
        Assert.Equal(0, await NewContext().CompetencyRelationships.CountAsync(Ct));
    }

    /// <summary>
    /// The role sheet's own type column decides the prefix, and it is not checked against the master list -
    /// so a task listed on the role sheet as knowledge resolves to <c>K-1</c>, finds nothing, and is
    /// dropped. The relationship a reader of the workbook would expect does not appear.
    /// </summary>
    [Fact]
    public async Task Import_DropsARequirementWhoseTypeDisagreesWithTheMasterList()
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Dcwf(
                roles: [RoleRow(category: AnyCategory, roleName: "Support", roleCode: "411")],
                tksas: [Tksa("1", "Task", "A task")],
                RoleSheet("IT-411", "Support", Requires("1", "Knowledge"))));

        await Read<CompetencyFramework>(response);
        Assert.Equal(0, await NewContext().CompetencyRelationships.CountAsync(Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // Reading cells
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A workbook Excel saved keeps its text in a shared string table and its cells hold indexes into it.
    /// Every test here writes inline strings instead, because they are legible in a diff - so one test reads
    /// the same workbook the other way to prove the difference does not matter.
    /// </summary>
    [Fact]
    public async Task Import_ReadsAWorkbookWrittenWithASharedStringTable()
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Workbooks.SharedStrings(
                RolesSheet(RoleRow(
                    category: Category("Information Technology", "IT"),
                    roleName: "Support",
                    roleCode: "411")),
                TasksSheet(Tksa("1", "Task", "A task")),
                RoleSheet("IT-411", "Support", Requires("1", "Task"))));

        var framework = await Read<CompetencyFramework>(response);
        Assert.Equal("Information Technology", Competency(framework, "IT").ShortName);
        Assert.Equal("Support", Competency(framework, "IT-411").ShortName);
        Assert.Equal(["T-1"], Competency(framework, "IT-411").RelatedIdNumbers);
    }

    /// <summary>
    /// Every cell is placed by its column reference, and a cell without one is dropped rather than counted
    /// where it sits. The reference is optional in the format - a row's cells are positional without it -
    /// so a workbook written by a tool that leaves it out imports as empty and answers 500.
    /// </summary>
    /// <remarks>
    /// Excel always writes the reference, so this is not the common case; it is what happens to a workbook
    /// generated by something else. Turning red here would mean <c>GetColumnIndex</c> had learned to count
    /// position, which is the fix.
    /// </remarks>
    [Fact]
    public async Task Import_DropsEveryCellOfAWorkbookWrittenWithoutColumnReferences()
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Workbooks.WithoutCellReferences(
                RolesSheet(RoleRow(
                    category: Category("Information Technology", "IT"),
                    roleName: "Support",
                    roleCode: "411")),
                TasksSheet(Tksa("1", "Task", "A task"))));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("No competencies found in DCWF spreadsheet.", (await ReadError(response)).Title);
    }

    // ---------------------------------------------------------------------------------------------
    // Refusals
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Both sheet names are matched exactly, so a workbook that spells one of them differently - including
    /// in a different case - is refused outright. DCWF publishes the file; a customer who has opened it and
    /// saved it again has not renamed anything, but a customer who assembled it themselves easily has.
    /// </summary>
    [Theory]
    [InlineData("DCWF Roles", "Master Task & KSA list")]
    [InlineData("dcwf roles", "Master Task & KSA List")]
    [InlineData("DCWF Roles", "Master Task and KSA List")]
    [InlineData("DCWF  Roles", "Master Task & KSA List")]
    [InlineData("Roles", "Tasks")]
    public async Task Import_WithoutTheTwoRequiredSheetNames_Is500(string rolesName, string tasksName)
    {
        var response = await ImportXlsx(
            Client(await Manager()),
            Workbooks.Build(
                new Workbooks.Sheet(rolesName, [], [], RoleRow(category: AnyCategory)),
                new Workbooks.Sheet(tasksName, [], Tksa("1", "Task", "A task"))));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(
            "DCWF XLSX must have 'DCWF Roles' and 'Master Task & KSA List' sheets.",
            (await ReadError(response)).Title);
        Assert.Equal(0, await NewContext().CompetencyFrameworks.CountAsync(Ct));
    }

    /// <summary>
    /// A workbook with the right sheets and nothing readable in them is refused rather than imported as an
    /// empty framework - the one content check this importer makes.
    /// </summary>
    [Fact]
    public async Task Import_WithNoReadableRows_Is500()
    {
        var response = await ImportXlsx(Client(await Manager()), Dcwf());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("No competencies found in DCWF spreadsheet.", (await ReadError(response)).Title);
        Assert.Equal(0, await NewContext().CompetencyFrameworks.CountAsync(Ct));
    }

    /// <summary>
    /// A file that is not a spreadsheet reaches <c>SpreadsheetDocument.Open</c> and fails there, so the
    /// caller is told the upload broke the server rather than that they uploaded the wrong thing.
    /// </summary>
    [Fact]
    public async Task Import_WithAFileThatIsNotASpreadsheet_Is500()
    {
        var response = await ImportXlsx(Client(await Manager()), Encoding.UTF8.GetBytes("not a workbook"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0, await NewContext().CompetencyFrameworks.CountAsync(Ct));
    }

    [Fact]
    public async Task Import_WithAnEmptyFile_Is400()
    {
        var response = await ImportXlsx(Client(await Manager()), []);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("No file provided.", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Import_WithNoSystemPermission_Is403()
    {
        var response = await ImportXlsx(
            Client(await Actor().SeedAsync()), Dcwf(roles: [RoleRow(category: AnyCategory)]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await NewContext().CompetencyFrameworks.CountAsync(Ct));
    }

    [Fact]
    public async Task Import_WithoutAuthentication_Is401()
    {
        var response = await ImportXlsx(AnonymousClient, Dcwf(roles: [RoleRow(category: AnyCategory)]));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Progress
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// This importer reports the same six phases as the other two, so a client renders "step n of 6" without
    /// knowing which format was uploaded.
    /// </summary>
    [Fact]
    public async Task Import_ReportsItsProgressAgainstTheImportId()
    {
        var client = Client(await Manager());
        var importId = Guid.NewGuid();

        var framework = await Read<CompetencyFramework>(await ImportXlsx(
            client, Dcwf(roles: [RoleRow(category: AnyCategory)]), "DCWF", "1.0", importId));

        var status = await Read<CompetencyFrameworkImportStatus>(
            await client.GetAsync($"api/competencyframeworks/imports/{importId}", Ct));
        Assert.Equal(CompetencyFrameworkImportState.Succeeded, status.State);
        Assert.Equal(6, status.PhaseNumber);
        Assert.Equal(6, status.PhaseCount);
        Assert.Equal(100, status.PercentComplete);
        Assert.Equal(framework.Id, status.FrameworkId);
        Assert.Equal("DCWF 1.0", status.FrameworkName);
    }

    [Fact]
    public async Task Import_AfterAFailure_ReportsTheReasonAgainstTheImportId()
    {
        var client = Client(await Manager());
        var importId = Guid.NewGuid();

        await ImportXlsx(client, Dcwf(), importId: importId);

        var status = await Read<CompetencyFrameworkImportStatus>(
            await client.GetAsync($"api/competencyframeworks/imports/{importId}", Ct));
        Assert.Equal(CompetencyFrameworkImportState.Failed, status.State);
        Assert.Equal("No competencies found in DCWF spreadsheet.", status.Error);
        Assert.Null(status.FrameworkId);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private Task<TestActor> Manager() =>
        Actor().WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();

    private async Task<HttpResponseMessage> ImportXlsx(
        HttpClient client,
        byte[] xlsx,
        string source = null,
        string version = null,
        Guid? importId = null)
    {
        using var content = new MultipartFormDataContent();
        var upload = new ByteArrayContent(xlsx);
        upload.Headers.ContentType = new MediaTypeHeaderValue(XlsxContentType);
        content.Add(upload, "file", "framework.xlsx");

        // Awaited inside the using: TestServer reads the body during SendAsync, so returning the task
        // unawaited disposes the content before the request has been read.
        return await client.PostAsync(Url(source, version, importId), content, Ct);
    }

    private static string Url(string source, string version, Guid? importId)
    {
        var query = new System.Collections.Generic.List<string>();

        if (source != null)
            query.Add($"source={Uri.EscapeDataString(source)}");

        if (version != null)
            query.Add($"version={Uri.EscapeDataString(version)}");

        if (importId.HasValue)
            query.Add($"importId={importId}");

        const string path = "api/competencyframeworks/import-xlsx";

        return query.Count == 0 ? path : $"{path}?{string.Join("&", query)}";
    }

    private static Competency Competency(CompetencyFramework framework, string idNumber) =>
        framework.Competencies.Single(c => c.IdNumber == idNumber);

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
