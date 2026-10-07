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
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Blueprint.Api.Tests.Support.Frameworks;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>The third importer: the DoD Cyber Workforce Framework's own spreadsheet, uploaded to
/// <c>competencyframeworks/import-xlsx</c>.</summary>
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

    /// <summary>Import with no source or version names the framework a single space.</summary>
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
        var existing = TestData.CompetencyFramework(idNumber: "DCWF-1.0", version: "1.0");
        existing.Name = "The one already here";
        await Seed(existing);

        var response = await ImportXlsx(
            Client(await Manager()), Dcwf(roles: [RoleRow(category: AnyCategory)]), "DCWF", "1.0");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("The one already here", (await ReadError(response)).Title);
        Assert.Equal(1, await ReadBack(rb => rb.CompetencyFrameworks.CountAsync(Ct)));
    }

    /// <summary>
    /// Unlike the CSV importer, this one also refuses a source and version pair already imported - so
    /// renaming the ID number is not enough to get a second copy of DCWF 1.0 in.
    /// </summary>
    [Fact]
    public async Task Import_ForAnAlreadyImportedSourceAndVersion_Is409()
    {
        var existing = TestData.CompetencyFramework(
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

    /// <summary>Import for a repeated work role code keeps the last row.</summary>
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
        var relationship = await ReadBack(rb => rb.CompetencyRelationships.SingleAsync(Ct));
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
        Assert.Equal(1, await ReadBack(rb => rb.CompetencyRelationships.CountAsync(Ct)));
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
        Assert.Equal(0, await ReadBack(rb => rb.CompetencyRelationships.CountAsync(Ct)));
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
        Assert.Equal(0, await ReadBack(rb => rb.CompetencyRelationships.CountAsync(Ct)));
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
        Assert.Equal(0, await ReadBack(rb => rb.CompetencyRelationships.CountAsync(Ct)));
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
        Assert.Equal(0, await ReadBack(rb => rb.CompetencyRelationships.CountAsync(Ct)));
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

    /// <summary>Import drops every cell of a workbook written without column references.</summary>
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

    /// <summary>A workbook whose sheet names differ from the two required names, case included, is answered with a 500.</summary>
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
        Assert.Equal(0, await ReadBack(rb => rb.CompetencyFrameworks.CountAsync(Ct)));
    }

    /// <summary>A workbook with the right sheets and no readable rows is answered with a 500.</summary>
    [Fact]
    public async Task Import_WithNoReadableRows_Is500()
    {
        var response = await ImportXlsx(Client(await Manager()), Dcwf());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("No competencies found in DCWF spreadsheet.", (await ReadError(response)).Title);
        Assert.Equal(0, await ReadBack(rb => rb.CompetencyFrameworks.CountAsync(Ct)));
    }

    /// <summary>A file that is not a spreadsheet is answered with a 500.</summary>
    [Fact]
    public async Task Import_WithAFileThatIsNotASpreadsheet_Is500()
    {
        var response = await ImportXlsx(Client(await Manager()), Encoding.UTF8.GetBytes("not a workbook"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0, await ReadBack(rb => rb.CompetencyFrameworks.CountAsync(Ct)));
    }

    [Fact]
    public async Task Import_WithAnEmptyFile_Is400()
    {
        var response = await ImportXlsx(Client(await Manager()), []);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("No file provided.", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Import_is_forbidden_for_a_caller_holding_only_ViewCompetencyFrameworks()
    {
        var response = await ImportXlsx(
            Client(await Actor().WithSystemPermissions(SystemPermission.ViewCompetencyFrameworks).SeedAsync()), Dcwf(roles: [RoleRow(category: AnyCategory)]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await ReadBack(rb => rb.CompetencyFrameworks.CountAsync(Ct)));
    }

    [Fact]
    public async Task Import_WithoutAuthentication_Is401()
    {
        var response = await ImportXlsx(Client(), Dcwf(roles: [RoleRow(category: AnyCategory)]));

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
