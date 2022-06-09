// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.QueryParameters;
using Blueprint.Api.ViewModels;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Packaging;

namespace Blueprint.Api.Services
{
    public interface IMselService
    {
        Task<IEnumerable<ViewModels.Msel>> GetAsync(MselGet queryParameters, CancellationToken ct);
        Task<IEnumerable<ViewModels.Msel>> GetMineAsync(CancellationToken ct);
        Task<ViewModels.Msel> GetAsync(Guid id, CancellationToken ct);
        Task<ViewModels.Msel> CreateAsync(ViewModels.Msel msel, CancellationToken ct);
        Task<ViewModels.Msel> UpdateAsync(Guid id, ViewModels.Msel msel, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
        Task<Guid> UploadAsync(FileForm form, CancellationToken ct);
        Task<Guid> ReplaceAsync(FileForm form, Guid mselId, CancellationToken ct);
        Task<Tuple<MemoryStream, string>> DownloadAsync(Guid mselId, CancellationToken ct);
        Task<DataTable> GetDataTableAsync(Guid mselId, CancellationToken ct);
    }

    public class MselService : IMselService
    {
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;
        private readonly ILogger<GalleryService> _logger;

        public MselService(
            BlueprintContext context,
            IAuthorizationService authorizationService,
            IPrincipal user,
            ILogger<GalleryService> logger,
            IMapper mapper)
        {
            _context = context;
            _authorizationService = authorizationService;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
            _logger = logger;
        }

        public async Task<IEnumerable<ViewModels.Msel>> GetAsync(MselGet queryParameters, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            IQueryable<MselEntity> msels = null;

            // filter based on user
            if (!String.IsNullOrEmpty(queryParameters.UserId))
            {
                Guid userId;
                Guid.TryParse(queryParameters.UserId, out userId);
                msels = _context.Msels
                    .Where(m => m.CreatedBy == userId);
            }
            // filter based on team
            if (!String.IsNullOrEmpty(queryParameters.TeamId))
            {
                Guid teamId;
                Guid.TryParse(queryParameters.TeamId, out teamId);
                if (msels == null)
                {
                    msels = _context.Msels.Where(m => m.TeamId == teamId);
                }
                else
                {
                    msels = msels.Where(m => m.TeamId == teamId);
                }
            }
            // filter based on description
            if (!String.IsNullOrEmpty(queryParameters.Description))
            {
                if (msels == null)
                {
                    msels = _context.Msels.Where(sm => sm.Description.Contains(queryParameters.Description));
                }
                else
                {
                    msels = msels.Where(sm => sm.Description.Contains(queryParameters.Description));
                }
            }
            if (msels == null)
            {
                msels = _context.Msels;
            }

            return _mapper.Map<IEnumerable<Msel>>(await msels.ToListAsync());
        }

        public async Task<IEnumerable<ViewModels.Msel>> GetMineAsync(CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new BaseUserRequirement())).Succeeded)
                throw new ForbiddenException();
            // get my teams
            var userId = _user.GetId();
            var teamIdList = await _context.TeamUsers
                .Where(tu => tu.UserId == userId)
                .Select(tu => tu.TeamId)
                .ToListAsync(ct);
            // get my teams' msels
            var mselList = await _context.Msels
                .Where(m => m.TeamId == null || teamIdList.Contains((Guid)m.TeamId))
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<Msel>>(mselList);
        }

        public async Task<ViewModels.Msel> GetAsync(Guid id, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new BaseUserRequirement())).Succeeded)
                throw new ForbiddenException();

            var item = await _context.Msels
                .Include(m => m.DataFields)
                .ThenInclude(df => df.DataOptions)
                .Include(m => m.ScenarioEvents)
                .ThenInclude(se => se.DataValues)
                .AsSingleQuery()
                .SingleOrDefaultAsync(sm => sm.Id == id, ct);

            return _mapper.Map<Msel>(item);
        }

        public async Task<ViewModels.Msel> CreateAsync(ViewModels.Msel msel, CancellationToken ct)
        {
            if (!(
                    (await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded ||
                    (await _authorizationService.AuthorizeAsync(_user, null, new TeamUserRequirement((Guid)msel.TeamId))).Succeeded
                 )
            )
                throw new ForbiddenException();

            msel.Id = msel.Id != Guid.Empty ? msel.Id : Guid.NewGuid();
            msel.DateCreated = DateTime.UtcNow;
            msel.CreatedBy = _user.GetId();
            msel.DateModified = null;
            msel.ModifiedBy = null;
            var mselEntity = _mapper.Map<MselEntity>(msel);

            _context.Msels.Add(mselEntity);
            await _context.SaveChangesAsync(ct);
            msel = await GetAsync(mselEntity.Id, ct);

            return msel;
        }

        public async Task<ViewModels.Msel> UpdateAsync(Guid id, ViewModels.Msel msel, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new CanIncrementMoveRequirement())).Succeeded)
                throw new ForbiddenException();

            var mselToUpdate = await _context.Msels.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (mselToUpdate == null)
                throw new EntityNotFoundException<Msel>();

            // okay to update this msel
            msel.CreatedBy = mselToUpdate.CreatedBy;
            msel.DateCreated = mselToUpdate.DateCreated;
            msel.ModifiedBy = _user.GetId();
            msel.DateModified = DateTime.UtcNow;
            _mapper.Map(msel, mselToUpdate);

            _context.Msels.Update(mselToUpdate);
            await _context.SaveChangesAsync(ct);

            msel = await GetAsync(mselToUpdate.Id, ct);

            return msel;
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            var mselToDelete = await _context.Msels.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (mselToDelete == null)
                throw new EntityNotFoundException<Msel>();
            // must be the user who created it or a content developer
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded && mselToDelete.CreatedBy != _user.GetId() )
                throw new ForbiddenException();

            _context.Msels.Remove(mselToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

        public async Task<Guid> UploadAsync(FileForm form, CancellationToken ct)
        {
            var uploadItem = form.ToUpload;
            var mselId = form.MselId != null ? (Guid)form.MselId : Guid.NewGuid();
            using (SpreadsheetDocument doc = SpreadsheetDocument.Open(uploadItem.OpenReadStream(),false))
            {
                //create the object for workbook part
                WorkbookPart workbookPart = doc.WorkbookPart;
                Sheets sheetCollection = workbookPart.Workbook.GetFirstChild<Sheets>();

                // handle multiple sheets
                // foreach (Sheet sheet in sheetCollection)
                // {
                //     Worksheet worksheet = ((WorksheetPart)workbookPart.GetPartById(sheet.Id)).Worksheet;
                // }

                //statement to get the worksheet object by using the sheet id
                Worksheet worksheet = ((WorksheetPart)workbookPart.GetPartById(sheetCollection.GetFirstChild<Sheet>().Id)).Worksheet;
                SheetData sheetData = (SheetData)worksheet.GetFirstChild<SheetData>();
                var headerRow = sheetData.GetFirstChild<Row>();
                var columns = worksheet.GetFirstChild<Columns>();
                // create the MSEL enitiy
                var msel = new MselEntity() {
                    Id = mselId,
                    Description = uploadItem.FileName,
                    Status = ItemStatus.Pending,
                    TeamId = form.TeamId,
                    IsTemplate = false,
                    HeaderRowMetadata = headerRow.Height != null ? headerRow.Height.Value.ToString() : ""
                };
                await _context.Msels.AddAsync(msel, ct);
                // create the data fields
                var dataFields = CreateDataFields(mselId, headerRow, workbookPart, columns);
                await _context.DataFields.AddRangeAsync(dataFields);
                // create the sceanrio events and data values
                sheetData.RemoveChild<Row>(headerRow);
                await CreateScenarioEventsAsync(mselId, sheetData, workbookPart, dataFields);
            }
            await _context.SaveChangesAsync(ct);

            return mselId;
        }

        public async Task<Guid> ReplaceAsync(FileForm form, Guid mselId, CancellationToken ct)
        {
            if (form.MselId != null && form.MselId != mselId)
                throw new ArgumentException("The mselId from the URL (" + mselId.ToString() + ") does not match the mselId supplied with the form (" + form.MselId.ToString() + ").");

            var msel = await _context.Msels.FindAsync(mselId);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>("The MSEL does not exist to be replaced.  " + mselId.ToString());

            // start a transaction, because we need the cascade delete to take affect before adding the new msel data
            await _context.Database.BeginTransactionAsync();
            // delete the existing MSEL
            _context.Msels.Remove(msel);
            await _context.SaveChangesAsync(ct);
            // create the replacement MSEL
            var uploadItem = form.ToUpload;
            using (SpreadsheetDocument doc = SpreadsheetDocument.Open(uploadItem.OpenReadStream(),false))
            {
                //create the object for workbook part
                WorkbookPart workbookPart = doc.WorkbookPart;
                Sheets sheetCollection = workbookPart.Workbook.GetFirstChild<Sheets>();

                //statement to get the worksheet object by using the sheet id
                Worksheet worksheet = ((WorksheetPart)workbookPart.GetPartById(sheetCollection.GetFirstChild<Sheet>().Id)).Worksheet;

                SheetData sheetData = (SheetData)worksheet.GetFirstChild<SheetData>();
                var newMsel = new MselEntity() {
                    Id = mselId,
                    Description = uploadItem.FileName,
                    Status = ItemStatus.Pending,
                    TeamId = form.TeamId,
                    IsTemplate = false
                };
                await _context.Msels.AddAsync(newMsel, ct);
                var headerRow = sheetData.GetFirstChild<Row>();
                var dataFields = CreateDataFields(mselId, headerRow, workbookPart, null);
                await _context.DataFields.AddRangeAsync(dataFields);
                sheetData.RemoveChild<Row>(headerRow);
                await CreateScenarioEventsAsync(mselId, sheetData, workbookPart, dataFields);
            }
            await _context.SaveChangesAsync(ct);
            await _context.Database.CommitTransactionAsync(ct);

            return mselId;
        }

        public async Task<Tuple<MemoryStream, string>> DownloadAsync(Guid mselId, CancellationToken ct)
        {
            var msel = await _context.Msels
                .Where(f => f.Id == mselId)
                .SingleOrDefaultAsync(ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();
            // get the MSEL data into a DataTable
            var scenarioEventList = await _context.ScenarioEvents
                .Where(n => n.MselId == mselId)
                .OrderBy(n => n.MoveNumber)
                .ThenBy(n => n.Time)
                .ThenBy(n => n.RowIndex)
                .ToListAsync(ct);
            var dataTable = await GetMselDataAsync(mselId, scenarioEventList, ct);
            // create the xlsx file in memory
            MemoryStream memoryStream = new MemoryStream();
            using (SpreadsheetDocument document = SpreadsheetDocument.Create(memoryStream, SpreadsheetDocumentType.Workbook))
            {
                WorkbookPart workbookPart = document.AddWorkbookPart();
                workbookPart.Workbook = new Workbook();

                // add styles to sheet
                Dictionary<string, int> uniqueStyles = await GetUniqueStylesAsync(mselId, ct);
                WorkbookStylesPart workbookStylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
                workbookStylesPart.Stylesheet = CreateStylesheet(uniqueStyles.OrderBy(uc => uc.Value).Select(uc => uc.Key).ToList());
                workbookStylesPart.Stylesheet.Save();

                WorksheetPart worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                var sheetData = new SheetData();
                worksheetPart.Worksheet = new Worksheet(sheetData);

                Sheets sheets = workbookPart.Workbook.AppendChild(new Sheets());
                Sheet sheet = new Sheet() { Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = 1, Name = "Sheet1" };

                sheets.Append(sheet);

                Row headerRow = new Row();
                if (msel.HeaderRowMetadata != "")
                {
                    headerRow.Height = double.Parse(msel.HeaderRowMetadata);
                    headerRow.CustomHeight = true;
                }
                Columns columns = new Columns();
                foreach (System.Data.DataColumn column in dataTable.Columns)
                {
                    var dataField = await _context.DataFields
                        .Where(df => df.MselId == mselId && df.Name == column.ColumnName)
                        .FirstOrDefaultAsync();
                    var width = double.Parse(dataField.ColumnMetadata);
                    var cellMetadata = dataField.CellMetadata;
                    columns.Append(new Column() {
                        Min = (UInt32)(column.Ordinal + 1),
                        Max = (UInt32)(column.Ordinal + 1),
                        Width = width,
                        CustomWidth = true
                    });

                    Cell cell = new Cell();
                    cell.DataType = CellValues.String;
                    cell.CellValue = new CellValue(column.ColumnName);
                    cell.StyleIndex = (UInt32)uniqueStyles[cellMetadata];
                    headerRow.AppendChild(cell);
                }
                worksheetPart.Worksheet.InsertAt(columns, 0);

                sheetData.AppendChild(headerRow);

                for (var i=0; i < dataTable.Rows.Count; i++)
                {
                    DataRow dsrow = dataTable.Rows[i];
                    Row newRow = new Row();
                    if (!String.IsNullOrEmpty(scenarioEventList[i].RowMetadata))
                    {
                        newRow.Height = double.Parse(scenarioEventList[i].RowMetadata);
                        newRow.CustomHeight = true;
                    }
                    foreach (System.Data.DataColumn column in dataTable.Columns)
                    {
                        Cell cell = new Cell();
                        cell.DataType = CellValues.String;
                        cell.CellValue = new CellValue(dsrow[column.ColumnName].ToString());
                        var dataField = await _context.DataFields
                            .Where(df => df.MselId == mselId && df.Name == column.ColumnName)
                            .FirstOrDefaultAsync();
                        var dataValue = await _context.DataValues
                            .Where(dv => dv.ScenarioEventId == scenarioEventList[i].Id && dv.DataFieldId == dataField.Id)
                            .FirstOrDefaultAsync();
                        var cellMetadata = dataValue.CellMetadata;
                        cell.StyleIndex = String.IsNullOrEmpty(cellMetadata) ? 0 : (UInt32)uniqueStyles[cellMetadata];
                        newRow.AppendChild(cell);
                    }

                    sheetData.AppendChild(newRow);
                }

                workbookPart.Workbook.Save();
            }
            //reset the stream position to the start of the stream
            memoryStream.Seek(0, SeekOrigin.Begin);
            var filename = msel.Description.ToLower().EndsWith(".xlsx") ? msel.Description : msel.Description + ".xlsx";

            return System.Tuple.Create(memoryStream, filename);
        }

        public async Task<DataTable> GetDataTableAsync(Guid mselId, CancellationToken ct)
        {
            var msel = await _context.Msels
                .Where(f => f.Id == mselId)
                .SingleOrDefaultAsync(ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();
            var scenarioEventList = await _context.ScenarioEvents
                .Where(n => n.MselId == mselId)
                .OrderBy(n => n.MoveNumber)
                .ThenBy(n => n.Time)
                .ThenBy(n => n.RowIndex)
                .ToListAsync(ct);
            // get the MSEL data into a DataTable
            var dataTable = await GetMselDataAsync(mselId, scenarioEventList, ct);

            return dataTable;
        }

        private List<DataFieldEntity> CreateDataFields(Guid mselId, Row headerRow, WorkbookPart workbookPart, Columns columns)
        {
            var dataFields = new List<DataFieldEntity>();
            var displayOrder = 1;
            foreach (Cell thecurrentcell in headerRow)
            {
                string currentcellvalue = string.Empty;
                if (thecurrentcell.DataType != null)
                {
                    if (thecurrentcell.DataType == CellValues.SharedString)
                    {
                        int id;
                        if (Int32.TryParse(thecurrentcell.InnerText, out id))
                        {
                            SharedStringItem item = workbookPart.SharedStringTablePart.SharedStringTable.Elements<SharedStringItem>().ElementAt(id);
                            if (item.Text != null)
                            {
                                currentcellvalue = item.Text.Text;
                            }
                            else if (item.InnerText != null)
                            {
                                currentcellvalue = item.InnerText;
                            }
                            else if (item.InnerXml != null)
                            {
                                currentcellvalue = item.InnerXml;
                            }
                        }
                    }
                }
                else
                {
                    currentcellvalue = thecurrentcell.InnerText;
                }
                if (!string.IsNullOrEmpty(currentcellvalue))
                {
                    int cellStyleIndex;
                    if (thecurrentcell.StyleIndex == null)
                    {
                        cellStyleIndex = 0;
                    }
                    else
                    {
                        cellStyleIndex = (int)thecurrentcell.StyleIndex.Value;
                    }
                    WorkbookStylesPart styles = (WorkbookStylesPart)workbookPart.WorkbookStylesPart;
                    CellFormat cellFormat = (CellFormat)styles.Stylesheet.CellFormats.ChildElements[cellStyleIndex];
                    Fill fill = (Fill)styles.Stylesheet.Fills.ChildElements[(int)cellFormat.FillId.Value];
                    PatternFill patternFill = fill.PatternFill;
                    var cellColor = "";
                    double cellTint = 0.0;
                    var pfType = patternFill.PatternType;
                    var colorType = (ColorType)patternFill.ForegroundColor;
                    if (colorType != null)
                    {
                        if (colorType.Rgb != null)
                        {
                            cellColor = colorType.Rgb.Value;
                        }
                        else if (colorType.Theme != null)
                        {
                            cellColor = ((DocumentFormat.OpenXml.Drawing.Color2Type)workbookPart.ThemePart.Theme.ThemeElements.ColorScheme.ChildElements[(int)colorType.Theme.Value]).RgbColorModelHex.Val;
                        }
                        cellTint = colorType.Tint == null ? 0.0 : colorType.Tint.Value;
                    }
                    var columnIndex = GetColumnIndex(thecurrentcell.CellReference.Value);
                    var column = (Column)columns.ChildElements.FirstOrDefault(ce => columnIndex >= ((Column)ce).Min.Value && columnIndex<= ((Column)ce).Max.Value);
                    var columnMetadata = column.Width == null ? "0.0" : column.Width.Value.ToString();
                    var dataField = new DataFieldEntity() {
                        Id = Guid.NewGuid(),
                        MselId = mselId,
                        Name = currentcellvalue,
                        DataType = DataFieldType.String,
                        DisplayOrder = displayOrder,
                        IsChosenFromList = false,
                        CellMetadata = cellColor + "," + cellTint + ",bold",
                        ColumnMetadata = columnMetadata
                    };
                    dataFields.Add(dataField);
                }
                displayOrder++;
            }

            return dataFields;
        }

        private int GetColumnIndex(string columnRef)
        {
            if (string.IsNullOrEmpty(columnRef)) throw new ArgumentNullException("columnName");

            columnRef = columnRef.ToUpperInvariant();

            int columnIndex = 0;

            for (int i = 0; i < columnRef.Length; i++)
            {
                if (columnRef[i] >= 'A' && columnRef[i] <= 'Z')
                {
                    columnIndex *= 26;
                    columnIndex += (columnRef[i] - 'A' + 1);
                }
            }

            return columnIndex;
        }


        private async Task CreateScenarioEventsAsync(Guid mselId, SheetData dataRows, WorkbookPart workbookPart, List<DataFieldEntity> dataFields)
        {
            var scenarioEventList = new List<ScenarioEventEntity>();
            foreach (Row dataRow in dataRows)
            {
                var scenarioEventId = Guid.NewGuid();
                var scenarioEvent = new ScenarioEventEntity() {
                    Id = scenarioEventId,
                    MselId = mselId,
                    RowIndex = (int)dataRow.RowIndex.Value,
                    RowMetadata = dataRow.Height != null ? dataRow.Height.Value.ToString() : ""
                };
                await _context.ScenarioEvents.AddAsync(scenarioEvent);
                await CreateDataValuesAsync(scenarioEvent, dataRow, workbookPart, dataFields);
            }
        }

        private async Task CreateDataValuesAsync(ScenarioEventEntity scenarioEvent, Row dataRow, WorkbookPart workbookPart, List<DataFieldEntity> dataFields)
        {
            foreach (Cell cell in dataRow)
            {
                //statement to take the integer value
                string currentcellvalue = string.Empty;
                if (cell.DataType != null)
                {
                    if (cell.DataType == CellValues.SharedString)
                    {
                        int id;
                        if (Int32.TryParse(cell.InnerText, out id))
                        {
                            SharedStringItem item = workbookPart.SharedStringTablePart.SharedStringTable.Elements<SharedStringItem>().ElementAt(id);
                            if (item.Text != null)
                            {
                                currentcellvalue = item.Text.Text;
                            }
                            else if (item.InnerText != null)
                            {
                                currentcellvalue = item.InnerText;
                            }
                            else if (item.InnerXml != null)
                            {
                                currentcellvalue = item.InnerXml;
                            }
                        }
                    }
                    else if (cell.DataType == CellValues.String)
                    {
                        currentcellvalue = cell.CellValue.Text;
                    }
                }
                else
                {
                    currentcellvalue = cell.InnerText;
                }
                var displayOrder = (int)cell.CellReference.Value[0] - 64;
                var dataField = dataFields.FirstOrDefault(df => df.MselId == scenarioEvent.MselId && df.DisplayOrder == displayOrder);
                if (dataField != null)
                {
                    int cellStyleIndex;
                    if (cell.StyleIndex == null)
                    {
                        cellStyleIndex = 0;
                    }
                    else
                    {
                        cellStyleIndex = (int)cell.StyleIndex.Value;
                    }
                    WorkbookStylesPart styles = (WorkbookStylesPart)workbookPart.WorkbookStylesPart;
                    CellFormat cellFormat = (CellFormat)styles.Stylesheet.CellFormats.ChildElements[cellStyleIndex];
                    Fill fill = (Fill)styles.Stylesheet.Fills.ChildElements[(int)cellFormat.FillId.Value];
                    PatternFill patternFill = fill.PatternFill;
                    var cellColor = "";
                    double cellTint = 0.0;
                    var pfType = patternFill.PatternType;
                    var colorType = (ColorType)patternFill.ForegroundColor;
                    if (colorType != null)
                    {
                        if (colorType.Rgb != null)
                        {
                            cellColor = colorType.Rgb.Value;
                        }
                        else if (colorType.Theme != null)
                        {
                            cellColor = ((DocumentFormat.OpenXml.Drawing.Color2Type)workbookPart.ThemePart.Theme.ThemeElements.ColorScheme.ChildElements[(int)colorType.Theme.Value]).RgbColorModelHex.Val;
                        }
                        cellTint = colorType.Tint == null ? 0.0 : colorType.Tint.Value;
                    }
                    var fontWeight = dataField.DisplayOrder == 1 ? "bold" : "normal";
                    var dataValue = new DataValueEntity() {
                        Id = Guid.NewGuid(),
                        ScenarioEventId = scenarioEvent.Id,
                        DataFieldId = dataField.Id,
                        Value = currentcellvalue,
                        CellMetadata = cellColor + "," + cellTint + "," + fontWeight
                    };
                    await _context.DataValues.AddAsync(dataValue);
                    // set scenarioEvent move or scenarioEvent based on value
                    if (dataField.Name.ToLower() == "move")
                    {
                        int moveNumber = 0;
                        scenarioEvent.MoveNumber = int.TryParse(currentcellvalue, out moveNumber) ? moveNumber : 0;
                    }
                    else if (dataField.Name.ToLower().Replace(" ", "") == "time")
                    {
                        scenarioEvent.Time = currentcellvalue;
                    }
                }
            }
        }

        private async Task<DataTable> GetMselDataAsync(Guid mselId, List<ScenarioEventEntity> scenarioEventList, CancellationToken ct)
        {
            // create data table to hold all of the scenarioEvent data
            DataTable dataTable = new DataTable();
            dataTable.Clear();
            // add a column for each data field
            var dataFieldList = await _context.DataFields
                .Where(df => df.MselId == mselId)
                .OrderBy(df => df.DisplayOrder)
                .ToListAsync(ct);
            foreach (var dataField in dataFieldList)
            {
                dataTable.Columns.Add(dataField.Name);
            }
            // add a row for each scenarioEvent
            foreach (var scenarioEvent in scenarioEventList)
            {
                var dataRow = dataTable.NewRow();
                var dataValueList = await _context.DataValues
                    .Where(dv => dv.ScenarioEventId == scenarioEvent.Id)
                    .OrderBy(dv =>dv.DataField.DisplayOrder)
                    .Select(dv => new { Name = dv.DataField.Name, Value = dv.Value })
                    .ToListAsync(ct);
                foreach (var dataValue in dataValueList)
                {
                    dataRow[dataValue.Name] = dataValue.Value;
                }
                dataTable.Rows.Add(dataRow);
            }

            return dataTable;
        }

        private async Task<Dictionary<string, int>> GetUniqueStylesAsync(Guid mselId, CancellationToken ct)
        {
            var uniqueStyles = new Dictionary<string, int>();
            var dataFields = await _context.DataFields
                .Where(df => df.MselId == mselId)
                .ToListAsync();
            var dataFieldColors = dataFields.Where(df => df.CellMetadata != null).DistinctBy(df => df.CellMetadata).Select(df => df.CellMetadata);
            var dataFieldIds = dataFields.Select(df => df.Id);
            var dataValueColors = await _context.DataValues
                .Where(dv => dataFieldIds.Contains(dv.DataFieldId) && dv.CellMetadata != null)
                .Select(dv => dv.CellMetadata)
                .ToListAsync();
            var allColors = dataFieldColors.Union(dataValueColors);
            foreach (var color in allColors)
            {
                uniqueStyles[color] = uniqueStyles.Count + 1;
            }

            return uniqueStyles;
        }

        private Stylesheet CreateStylesheet(List<string> uniqueStyles)
        {
            Stylesheet stylesheet = new Stylesheet() { MCAttributes = new MarkupCompatibilityAttributes() { Ignorable = "x14ac" } };
            stylesheet.AddNamespaceDeclaration("mc", "http://schemas.openxmlformats.org/markup-compatibility/2006");
            stylesheet.AddNamespaceDeclaration("x14ac", "http://schemas.microsoft.com/office/spreadsheetml/2009/9/ac");

            Fonts fonts = new Fonts();

            Font font = new Font();
            Font boldFont = new Font();
            boldFont.Append(new Bold());
            fonts.Append(font);
            fonts.Append(boldFont);

            Fills fills = new Fills() { Count = (UInt32Value)5U };

            // FillId = 0
            Fill fill1 = new Fill();
            PatternFill patternFill1 = new PatternFill() { PatternType = PatternValues.None };
            fill1.Append(patternFill1);
            fills.Append(fill1);

            // FillId = 1
            Fill fill2 = new Fill();
            PatternFill patternFill2 = new PatternFill() { PatternType = PatternValues.Gray125 };
            fill2.Append(patternFill2);
            fills.Append(fill2);

            for (var i=0; i < uniqueStyles.Count; i++)
            {
                var colorAndTint = uniqueStyles[i].Split(',');
                Fill newFill = new Fill();
                PatternFill patternFill = new PatternFill() { PatternType = PatternValues.Solid };
                ForegroundColor foregroundColor = new ForegroundColor() { Rgb = colorAndTint[0], Tint = double.Parse(colorAndTint[1]) };
                BackgroundColor backgroundColor = new BackgroundColor() { Indexed = (UInt32Value)64U };
                patternFill.Append(foregroundColor);
                patternFill.Append(backgroundColor);
                newFill.Append(patternFill);
                fills.Append(newFill);
            }

            // borders
            Borders borders = new Borders() { Count = (UInt32Value)1U };
            Border border = new Border();
            LeftBorder leftBorder = new LeftBorder(){ Style = BorderStyleValues.Thin };
            leftBorder.Append(new Color(){ Indexed = (UInt32Value)64U });
            RightBorder rightBorder = new RightBorder(){ Style = BorderStyleValues.Thin };
            rightBorder.Append(new Color(){ Indexed = (UInt32Value)64U });
            TopBorder topBorder = new TopBorder(){ Style = BorderStyleValues.Thin };
            topBorder.Append(new Color(){ Indexed = (UInt32Value)64U });
            BottomBorder bottomBorder = new BottomBorder(){ Style = BorderStyleValues.Thin };
            bottomBorder.Append(new Color(){ Indexed = (UInt32Value)64U });
            DiagonalBorder diagonalBorder = new DiagonalBorder(){ Style = BorderStyleValues.Thin };
            border.Append(leftBorder);
            border.Append(rightBorder);
            border.Append(topBorder);
            border.Append(bottomBorder);
            border.Append(diagonalBorder);
            borders.Append(border);

            CellStyleFormats cellStyleFormats = new CellStyleFormats() { Count = (UInt32Value)1U };
            CellFormat cellFormat1 = new CellFormat() { NumberFormatId = (UInt32Value)0U, FontId = (UInt32Value)0U, FillId = (UInt32Value)0U, BorderId = (UInt32Value)0U };

            cellStyleFormats.Append(cellFormat1);

            CellFormats cellFormats = new CellFormats() { Count = (UInt32Value)4U };
            CellFormat cellFormat2 = new CellFormat() { NumberFormatId = (UInt32Value)0U, FontId = (UInt32Value)0U, FillId = (UInt32Value)0U, BorderId = (UInt32Value)0U, FormatId = (UInt32Value)0U };
            cellFormats.Append(cellFormat2);
            for (int i=0; i < uniqueStyles.Count; i++)
            {
                UInt32 fontId = uniqueStyles[i].Split(",")[2] == "bold" ? 1U : 0U;
                CellFormat cellFormat = new CellFormat(new Alignment() { WrapText = true }) { NumberFormatId = (UInt32Value)0U, FontId = (UInt32Value)fontId, FillId = (UInt32Value)((UInt32)i + 2), BorderId = (UInt32Value)0U, FormatId = (UInt32Value)0U, ApplyFill = true };
                cellFormats.Append(cellFormat);
            }

            CellStyles cellStyles = new CellStyles() { Count = (UInt32Value)1U };
            CellStyle cellStyle1 = new CellStyle() { Name = "Normal", FormatId = (UInt32Value)0U, BuiltinId = (UInt32Value)0U };
            cellStyles.Append(cellStyle1);
            DifferentialFormats differentialFormats = new DifferentialFormats() { Count = (UInt32Value)0U };
            TableStyles tableStyles = new TableStyles() { Count = (UInt32Value)0U, DefaultTableStyle = "TableStyleMedium2", DefaultPivotStyle = "PivotStyleMedium9" };

            StylesheetExtensionList stylesheetExtensionList = new StylesheetExtensionList();
            StylesheetExtension stylesheetExtension1 = new StylesheetExtension() { Uri = "{EB79DEF2-80B8-43e5-95BD-54CBDDF9020C}" };
            stylesheetExtension1.AddNamespaceDeclaration("x14", "http://schemas.microsoft.com/office/spreadsheetml/2009/9/main");
            // X14.SlicerStyles slicerStyles1 = new X14.SlicerStyles() { DefaultSlicerStyle = "SlicerStyleLight1" };
            // stylesheetExtension1.Append(slicerStyles1);
            stylesheetExtensionList.Append(stylesheetExtension1);

            stylesheet.Append(fonts);
            stylesheet.Append(fills);
            stylesheet.Append(borders);
            stylesheet.Append(cellStyleFormats);
            stylesheet.Append(cellFormats);
            stylesheet.Append(cellStyles);
            stylesheet.Append(differentialFormats);
            stylesheet.Append(tableStyles);
            stylesheet.Append(stylesheetExtensionList);

            return stylesheet;
        }

    }
}

