// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Linq;
using System.Text.Json;

namespace Blueprint.Api.Tests.Infrastructure;

/// <summary>
/// Builders for the four file formats the competency-framework importers and previews read: Moodle's
/// 14-column CSV, NICE's JSON, Blueprint's own JSON export, and the DCWF workbook.
/// </summary>
/// <remarks>
/// <para>
/// These live here rather than in one test class because the importers and the preview endpoints read the
/// same files through different code, and the tests worth writing are the ones that hand both halves the
/// same bytes and compare what they say about it. A builder duplicated per class could not do that
/// honestly.
/// </para>
/// <para>
/// Every builder names its columns, so a test says which two or three fields it cares about instead of
/// counting commas, and the quoting comes out the way an exporter would produce it. The DCWF builders wrap
/// <see cref="Workbooks"/> and put the rows each sheet's reader skips by position in front of the data, so
/// a test's first row is the first row that is read.
/// </para>
/// </remarks>
public static class Frameworks
{
    // ---------------------------------------------------------------------------------------------
    // Moodle CSV
    // ---------------------------------------------------------------------------------------------

    /// <summary>The 14 columns of Moodle's lpimportcsv format, in order.</summary>
    public const string Header =
        "Parent ID number,ID number,Short name,Description,Description format,Scale values," +
        "Scale configuration,Rule type,Rule outcome,Rule config," +
        "Cross referenced competency ID numbers,Export ID,Is framework,Taxonomy";

    public static string Csv(params string[] rows) =>
        string.Join("\n", new[] { Header }.Concat(rows));

    /// <summary>One CSV line, quoted where quoting is required.</summary>
    public static string Row(
        string parentIdNumber = "",
        string idNumber = "",
        string shortName = "",
        string description = "",
        string descriptionFormat = "",
        string scaleValues = "",
        string scaleConfiguration = "",
        string ruleType = "",
        string ruleOutcome = "",
        string ruleConfig = "",
        string relatedIdNumbers = "",
        string exportId = "",
        string isFramework = "",
        string taxonomy = "") =>
        string.Join(",", new[]
        {
            parentIdNumber, idNumber, shortName, description, descriptionFormat, scaleValues,
            scaleConfiguration, ruleType, ruleOutcome, ruleConfig, relatedIdNumbers, exportId,
            isFramework, taxonomy
        }.Select(Quote));

    public static string Quote(string field) =>
        field.Contains(',') || field.Contains('"')
            ? $"\"{field.Replace("\"", "\"\"")}\""
            : field;

    public static string FrameworkRow(string idNumber, string shortName = "Framework") =>
        Row(idNumber: idNumber, shortName: shortName, isFramework: "1");

    public static string CompetencyRow(
        string idNumber, string shortName = null, string parent = "", string related = "") =>
        Row(
            parentIdNumber: parent,
            idNumber: idNumber,
            shortName: shortName ?? $"competency {idNumber}",
            relatedIdNumbers: related);

    // ---------------------------------------------------------------------------------------------
    // NICE JSON, and Blueprint's own export
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A NICE file in its flat form: the container with the three arrays the importer needs. Pass it to
    /// <see cref="Wrapped"/> for the shape NICE's own download produces.
    /// </summary>
    public static string Nice(
        string[] elements,
        string[] relationships = null,
        string name = "NICE Framework",
        string identifier = "NICE",
        string version = "1.0") =>
        $$"""
        {"documents":[{"name":{{Str(name)}},"version":{{Str(version)}},"doc_identifier":{{Str(identifier)}}}],
         "elements":[{{string.Join(",", elements)}}],
         "relationships":[{{string.Join(",", relationships ?? [])}}]}
        """;

    /// <summary>The two levels of envelope NICE's CPRT download puts around the container.</summary>
    public static string Wrapped(string container) => $"{{\"response\":{{\"elements\":{container}}}}}";

    public static string Element(string identifier, string elementType, string title = "", string text = "") =>
        $$"""
        {"element_identifier":{{Str(identifier)}},"element_type":{{Str(elementType)}},
         "title":{{Str(title)}},"text":{{Str(text)}}}
        """;

    public static string Link(string source, string dest) =>
        $$"""
        {"source_element_identifier":{{Str(source)}},"dest_element_identifier":{{Str(dest)}}}
        """;

    public static string Str(string value) => JsonSerializer.Serialize(value);

    // ---------------------------------------------------------------------------------------------
    // The DCWF workbook
    // ---------------------------------------------------------------------------------------------

    public const string XlsxContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>The category cell most tests use, where the category itself is not the point.</summary>
    public static readonly string AnyCategory = Category("Information Technology", "IT");

    /// <summary>
    /// A whole DCWF workbook: the two sheets the reader requires, in the shape it requires, plus whatever
    /// per-role sheets the test needs. Both required sheets are always present, so a test about the roles
    /// sheet does not fail for the reason a test about a missing sheet does.
    /// </summary>
    public static byte[] Dcwf(
        string[][] roles = null, string[][] tksas = null, params Workbooks.Sheet[] roleSheets) =>
        Workbooks.Build([RolesSheet(roles ?? []), TasksSheet(tksas ?? []), .. roleSheets]);

    /// <summary>The roles sheet, with the two rows its reader skips already in front of the data.</summary>
    public static Workbooks.Sheet RolesSheet(params string[][] rows) =>
        new("DCWF Roles", [[], [], .. rows]);

    /// <summary>The TKSA sheet, with the one row its reader skips already in front of the data.</summary>
    public static Workbooks.Sheet TasksSheet(params string[][] rows) =>
        new("Master Task & KSA List", [[], .. rows]);

    /// <summary>
    /// One role's sheet, named the way the reader matches it, with the six preamble rows it skips already
    /// in front of the data.
    /// </summary>
    public static Workbooks.Sheet RoleSheet(string roleCode, string name, params string[][] rows) =>
        new($"({roleCode}) {name}", [[], [], [], [], [], [], .. rows]);

    /// <summary>
    /// A roles-sheet row. Column A is left out entirely rather than blanked, because that is how the
    /// published workbook is laid out and because it is what makes reading by column reference matter.
    /// </summary>
    public static string[] RoleRow(
        string category = null,
        string categoryDescription = null,
        string roleName = null,
        string roleCode = null) =>
        [null, category, categoryDescription, roleName, roleCode];

    /// <summary>The category cell's two lines: the name, then the code in brackets.</summary>
    public static string Category(string name, string code) => $"{name}\n({code})";

    /// <summary>A TKSA-sheet row: the number in column A, the type in D and the description in E.</summary>
    public static string[] Tksa(string number, string type, string description) =>
        [number, null, null, type, description];

    /// <summary>A role-sheet row: the TKSA's number in column A and its type in B.</summary>
    public static string[] Requires(string number, string type) => [number, type];

    /// <summary>
    /// One sheet of arbitrary rows, for the single-sheet shape <c>PreviewXlsxAsync</c> falls back to and
    /// the DCWF importer refuses outright.
    /// </summary>
    public static byte[] SingleSheet(params string[][] rows) =>
        Workbooks.Build(new Workbooks.Sheet("Sheet1", rows));

    /// <summary>A row of the single-sheet shape: an ID number in column A and related IDs in column E.</summary>
    public static string[] SimpleRow(string idNumber, string relatedIdNumbers = null) =>
        [idNumber, null, null, null, relatedIdNumbers];
}
