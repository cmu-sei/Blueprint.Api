// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Blueprint.Api.Tests.Infrastructure;

/// <summary>
/// Builds XLSX files in memory so a spreadsheet test can say what is in the sheets on the same screen as
/// its assertion, instead of pointing at a checked-in binary nobody can read in a diff.
/// </summary>
/// <remarks>
/// <para>
/// Written for the DCWF importer, which reads several named sheets at once and finds its columns by
/// cell reference rather than by counting cells. Both matter: <c>"DCWF Roles"</c> and
/// <c>"Master Task &amp; KSA List"</c> must exist under exactly those names, and a row in a real export
/// has gaps, so a value belonging to column E has to be written at E even when B, C and D are absent.
/// Passing <c>null</c> for a cell omits it entirely, which is how those gaps are made; passing <c>""</c>
/// writes a cell with no value, which the reader treats the same way but which exercises a different
/// branch of <c>GetCellValue</c>.
/// </para>
/// <para>
/// Cells are written as inline strings by default. <see cref="SharedStrings"/> builds the same sheet
/// through a shared string table instead, which is what Excel itself produces and which the reader
/// resolves through a separate code path. <see cref="WithoutCellReferences"/> omits the <c>r</c>
/// attribute, which the schema permits and some writers leave out - the reader does not survive it, and
/// that is worth a test rather than an assumption.
/// </para>
/// <para>
/// <c>MselServiceXlsxTests</c> keeps its own single-sheet builder rather than using this one: it also
/// reads workbooks back and asserts on cell styles, so it needs a stylesheet this one has no reason to
/// write.
/// </para>
/// </remarks>
public static class Workbooks
{
    /// <summary>A named sheet and its rows, where a null cell is one the sheet does not contain.</summary>
    public sealed record Sheet(string Name, params string[][] Rows);

    /// <summary>Builds a workbook whose cells are inline strings.</summary>
    public static byte[] Build(params Sheet[] sheets) => Build(shared: false, references: true, sheets);

    /// <summary>
    /// Builds a workbook whose cells resolve through a shared string table, as Excel's own output does.
    /// </summary>
    public static byte[] SharedStrings(params Sheet[] sheets) =>
        Build(shared: true, references: true, sheets);

    /// <summary>
    /// Builds a workbook whose cells carry no <c>r</c> attribute, leaving their column to be inferred from
    /// their order in the row. Cannot express a gap, so a null cell is written as an empty one.
    /// </summary>
    public static byte[] WithoutCellReferences(params Sheet[] sheets) =>
        Build(shared: false, references: false, sheets);

    private static byte[] Build(bool shared, bool references, Sheet[] sheets)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var sheetElements = workbookPart.Workbook.AppendChild(new Sheets());

            // The shared string table is a workbook-level part, so one table serves every sheet and the
            // index a cell carries is an offset into it.
            var strings = new List<string>();

            for (var s = 0; s < sheets.Length; s++)
            {
                var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                var sheetData = new SheetData();
                worksheetPart.Worksheet = new Worksheet(sheetData);

                var rows = sheets[s].Rows ?? [];
                for (var r = 0; r < rows.Length; r++)
                    sheetData.AppendChild(Row((uint)(r + 1), rows[r], shared, references, strings));

                sheetElements.Append(new DocumentFormat.OpenXml.Spreadsheet.Sheet
                {
                    Id = workbookPart.GetIdOfPart(worksheetPart),
                    SheetId = (uint)(s + 1),
                    Name = sheets[s].Name
                });
            }

            if (shared)
            {
                var tablePart = workbookPart.AddNewPart<SharedStringTablePart>();
                tablePart.SharedStringTable = new SharedStringTable(
                    strings.Select(v => new SharedStringItem(new Text(v))));
                tablePart.SharedStringTable.Save();
            }

            workbookPart.Workbook.Save();
        }

        return stream.ToArray();
    }

    private static Row Row(
        uint rowIndex, string[] values, bool shared, bool references, List<string> strings)
    {
        var row = new Row { RowIndex = rowIndex };
        for (var i = 0; i < (values?.Length ?? 0); i++)
        {
            // A null value means the sheet has no cell in that column at all, which is the ordinary
            // shape of a real export and the case that makes reading by cell reference load-bearing.
            if (values[i] == null && references)
                continue;

            var cell = new Cell();
            if (references)
                cell.CellReference = Reference(i, rowIndex);

            if (values[i]?.Length > 0)
            {
                if (shared)
                {
                    strings.Add(values[i]);
                    cell.DataType = CellValues.SharedString;
                    cell.CellValue = new CellValue((strings.Count - 1).ToString());
                }
                else
                {
                    cell.DataType = CellValues.String;
                    cell.CellValue = new CellValue(values[i]);
                }
            }

            row.AppendChild(cell);
        }

        return row;
    }

    /// <summary>An A1-style reference from a zero-based column index.</summary>
    public static string Reference(int columnIndex, uint rowIndex)
    {
        var column = "";
        for (var i = columnIndex; i >= 0; i = i / 26 - 1)
            column = (char)('A' + i % 26) + column;

        return column + rowIndex;
    }
}
