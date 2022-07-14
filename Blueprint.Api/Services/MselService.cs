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
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
            {
                TeamUserEntity teamUser;
                if (msel.TeamId == null)
                {
                    teamUser = await _context.TeamUsers
                        .FirstOrDefaultAsync(tu => tu.UserId == _user.GetId());
                }
                else
                {
                    teamUser = await _context.TeamUsers
                        .FirstOrDefaultAsync(tu => tu.UserId == _user.GetId() && tu.TeamId == msel.TeamId);
                }
                if (teamUser == null)
                    throw new ForbiddenException();

                msel.TeamId = teamUser.TeamId;
            }

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
            // user must be a Content Developer or be on the requested team and be able to submit
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
            {
                var teamId = await _context.Msels
                    .Where(m => m.Id == msel.Id)
                    .Select(m => m.TeamId)
                    .FirstOrDefaultAsync();
                if (!(
                        (await _authorizationService.AuthorizeAsync(_user, null, new CanSubmitRequirement())).Succeeded &&
                        teamId != null &&
                        (await _authorizationService.AuthorizeAsync(_user, null, new TeamUserRequirement((Guid)teamId))).Succeeded
                     )
                )
                    throw new ForbiddenException();
            }

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

            // user must be a Content Developer or be on the requested team and be able to submit
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
            {
                var teamId = await _context.Msels
                    .Where(m => m.Id == mselToDelete.Id)
                    .Select(m => m.TeamId)
                    .FirstOrDefaultAsync();
                if (!(
                        (await _authorizationService.AuthorizeAsync(_user, null, new CanSubmitRequirement())).Succeeded &&
                        teamId != null &&
                        (await _authorizationService.AuthorizeAsync(_user, null, new TeamUserRequirement((Guid)teamId))).Succeeded
                     )
                )
                    throw new ForbiddenException();
            }

            _context.Msels.Remove(mselToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

        public async Task<Guid> UploadAsync(FileForm form, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
            {
                TeamUserEntity teamUser;
                if (form.TeamId == null)
                {
                    teamUser = await _context.TeamUsers
                        .FirstOrDefaultAsync(tu => tu.UserId == _user.GetId());
                }
                else
                {
                    teamUser = await _context.TeamUsers
                        .FirstOrDefaultAsync(tu => tu.UserId == _user.GetId() && tu.TeamId == form.TeamId);
                }
                if (teamUser == null)
                    throw new ForbiddenException();

                form.TeamId = teamUser.TeamId;
            }

            var mselId = form.MselId != null ? (Guid)form.MselId : Guid.NewGuid();
            await createMselFromXlsxFile(form, mselId, ct);

            return mselId;
        }

        public async Task<Guid> ReplaceAsync(FileForm form, Guid mselId, CancellationToken ct)
        {
            if (form.MselId != null && form.MselId != mselId)
                throw new ArgumentException("The mselId from the URL (" + mselId.ToString() + ") does not match the mselId supplied with the form (" + form.MselId.ToString() + ").");

            var msel = await _context.Msels.FindAsync(mselId);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>("The MSEL does not exist to be replaced.  " + mselId.ToString());

            // user must be a Content Developer or be on the requested team and be able to submit
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
            {
                var teamId = await _context.Msels
                    .Where(m => m.Id == msel.Id)
                    .Select(m => m.TeamId)
                    .FirstOrDefaultAsync();
                if (!(
                        (await _authorizationService.AuthorizeAsync(_user, null, new CanSubmitRequirement())).Succeeded &&
                        teamId != null &&
                        (await _authorizationService.AuthorizeAsync(_user, null, new TeamUserRequirement((Guid)teamId))).Succeeded
                     )
                )
                    throw new ForbiddenException();
            }

            // start a transaction, because we need the cascade delete to take affect before adding the new msel data
            await _context.Database.BeginTransactionAsync();
            // delete the existing MSEL
            _context.Msels.Remove(msel);
            await _context.SaveChangesAsync(ct);
            await createMselFromXlsxFile(form, mselId, ct);
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
            var filename = msel.Description.ToLower().EndsWith(".xlsx") ? msel.Description : msel.Description + ".xlsx";

            // get the MSEL data into a DataTable
            var scenarioEventList = await _context.ScenarioEvents
                .Where(n => n.MselId == mselId)
                .OrderBy(n => n.RowIndex)
                .ToListAsync(ct);
            var dataTable = await GetMselDataAsync(mselId, scenarioEventList, ct);

            // create the xlsx file in memory
            MemoryStream memoryStream = new MemoryStream();
            using (SpreadsheetDocument document = SpreadsheetDocument.Create(memoryStream, SpreadsheetDocumentType.Workbook))
            {
                // create the workbook
                WorkbookPart workbookPart = document.AddWorkbookPart();
                workbookPart.Workbook = new Workbook();

                // create the style sheet
                Dictionary<string, int> uniqueStyles = await GetUniqueStylesAsync(mselId, ct);
                WorkbookStylesPart workbookStylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
                workbookStylesPart.Stylesheet = CreateStylesheet(uniqueStyles.OrderBy(uc => uc.Value).Select(uc => uc.Key).ToList());
                workbookStylesPart.Stylesheet.Save();

                // create the worksheet with sheet data
                WorksheetPart worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                var sheetData = new SheetData();
                worksheetPart.Worksheet = new Worksheet(sheetData);
                Sheets sheets = workbookPart.Workbook.AppendChild(new Sheets());
                Sheet sheet = new Sheet() { Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = 1, Name = "Sheet1" };
                sheets.Append(sheet);

                // add the header row
                Row headerRow = new Row();
                if (msel.HeaderRowMetadata != "")
                {
                    headerRow.Height = double.Parse(msel.HeaderRowMetadata);
                    headerRow.CustomHeight = true;
                }
                // add the cells to the header row
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
                        Width = width == 0 ? 10 : width,
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

                // add a row for each ScenarioEvent contained in a dataTable row
                for (var i=0; i < dataTable.Rows.Count; i++)
                {
                    // create the row
                    DataRow dsrow = dataTable.Rows[i];
                    Row newRow = new Row();
                    if (!String.IsNullOrEmpty(scenarioEventList[i].RowMetadata))
                    {
                        newRow.Height = double.Parse(scenarioEventList[i].RowMetadata);
                        newRow.CustomHeight = true;
                    }
                    // add the cells for this row
                    foreach (System.Data.DataColumn column in dataTable.Columns)
                    {
                        Cell cell = new Cell();
                        var stringValue = dsrow[column.ColumnName].ToString();
                        var dataField = await _context.DataFields
                            .Where(df => df.MselId == mselId && df.Name == column.ColumnName)
                            .FirstOrDefaultAsync();
                        var dataValue = await _context.DataValues
                            .Where(dv => dv.ScenarioEventId == scenarioEventList[i].Id && dv.DataFieldId == dataField.Id)
                            .FirstOrDefaultAsync();
                        var cellMetadata = dataValue.CellMetadata;
                        cell.StyleIndex = String.IsNullOrEmpty(cellMetadata) ? 0 : (UInt32)uniqueStyles[cellMetadata];
                        // handle differences between data types
                        if (!string.IsNullOrWhiteSpace(stringValue))
                        {
                            var dataFieldType = (DataFieldType)int.Parse(cellMetadata.Split(",")[3]);
                            switch (dataFieldType)
                            {
                                case DataFieldType.DateTime:
                                    cell.DataType = CellValues.Date;
                                    cell.CellValue = new CellValue(stringValue);
                                    break;
                                case DataFieldType.Boolean:
                                    cell.DataType = CellValues.Boolean;
                                    cell.CellValue = new CellValue(stringValue);
                                    break;
                                case DataFieldType.Double:
                                case DataFieldType.Integer:
                                    cell.CellValue = new CellValue(stringValue);
                                    break;
                                default:
                                    cell.DataType = CellValues.String;
                                    cell.CellValue = new CellValue(stringValue);
                                    break;
                            }
                        }
                        newRow.AppendChild(cell);
                    }
                    sheetData.AppendChild(newRow);
                }
                workbookPart.Workbook.Save();
            }
            //reset the stream position to the start of the stream
            memoryStream.Seek(0, SeekOrigin.Begin);

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
                .OrderBy(n => n.RowIndex)
                .ToListAsync(ct);
            // get the MSEL data into a DataTable
            var dataTable = await GetMselDataAsync(mselId, scenarioEventList, ct);

            return dataTable;
        }

        private async Task createMselFromXlsxFile(FileForm form, Guid mselId, CancellationToken ct)
        {
            var uploadItem = form.ToUpload;
            using (SpreadsheetDocument doc = SpreadsheetDocument.Open(uploadItem.OpenReadStream(),false))
            {
                //create the object for workbook part
                WorkbookPart workbookPart = doc.WorkbookPart;
                Sheets sheetCollection = workbookPart.Workbook.GetFirstChild<Sheets>();

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
                    HeaderRowMetadata = headerRow.Height != null ? headerRow.Height.Value.ToString() : "",
                    CreatedBy = _user.GetId(),
                    DateCreated = DateTime.UtcNow
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
        }

        private List<DataFieldEntity> CreateDataFields(Guid mselId, Row headerRow, WorkbookPart workbookPart, Columns columns)
        {
            var dataFields = new List<DataFieldEntity>();
            var displayOrder = 1;
            foreach (Cell thecurrentcell in headerRow)
            {
                string currentcellvalue = string.Empty;
                // All DataFields are initialized as String data types
                // this DataType can changed based on the data values found in this column
                DataFieldType cellDataType = DataFieldType.String;

                // get the cell value
                if (thecurrentcell.DataType != null)
                {
                    var id = 0;
                    if (thecurrentcell.DataType == CellValues.SharedString && Int32.TryParse(thecurrentcell.InnerText, out id))
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
                else
                {
                    currentcellvalue = thecurrentcell.InnerText;
                }

                // get the cell style and save it to the cell metadata and column metadata
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
                    var columnMetadata = "0.0";
                    if (columns != null)
                    {
                        var columnIndex = GetColumnIndex(thecurrentcell.CellReference.Value);
                        var column = (Column)columns.ChildElements.FirstOrDefault(ce => columnIndex >= ((Column)ce).Min.Value && columnIndex<= ((Column)ce).Max.Value);
                        columnMetadata = column == null || column.Width == null ? "0.0" : column.Width.Value.ToString();
                    }
                    // store the DataField
                    var dataField = new DataFieldEntity() {
                        Id = Guid.NewGuid(),
                        MselId = mselId,
                        Name = currentcellvalue,
                        DataType = cellDataType,
                        DisplayOrder = displayOrder,
                        IsChosenFromList = false,
                        CellMetadata = cellColor + "," + cellTint + ",bold," + (int)cellDataType,
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
            // loop through each DataField
            var cells = dataRow.Elements<Cell>();
            foreach (var dataField in dataFields)
            {
                string currentCellValue = string.Empty;
                string cellMetadata = ",0,normal," + (int)dataField.DataType;
                var cellReference = GetCellReference(dataField.DisplayOrder, (int)dataRow.RowIndex.Value);
                var cell = cells.FirstOrDefault(c => c.CellReference == cellReference);
                if (cell != null)
                {
                    // get the cell format from the style index
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

                    // get the cell data type and value
                    DataFieldType dataFieldType = DataFieldType.String;
                    if (cell.DataType != null)
                    {
                        switch (cell.DataType.Value)
                        {
                            case CellValues.SharedString:
                                int id;
                                if (Int32.TryParse(cell.InnerText, out id))
                                {
                                    SharedStringItem item = workbookPart.SharedStringTablePart.SharedStringTable.Elements<SharedStringItem>().ElementAt(id);
                                    if (item.Text != null)
                                    {
                                        currentCellValue = item.Text.Text;
                                    }
                                    else if (item.InnerText != null)
                                    {
                                        currentCellValue = item.InnerText;
                                    }
                                    else if (item.InnerXml != null)
                                    {
                                        currentCellValue = item.InnerXml;
                                    }
                                }
                                break;
                            case CellValues.Boolean:
                                dataFieldType = DataFieldType.Boolean;
                                currentCellValue = cell.CellValue.Text;
                                break;
                            case CellValues.Number:
                                dataFieldType = DataFieldType.Double;
                                currentCellValue = cell.CellValue.Text;
                                break;
                            case CellValues.Date:
                                dataFieldType = DataFieldType.DateTime;
                                currentCellValue = cell.CellValue.Text;
                                break;
                            case CellValues.InlineString:
                            case CellValues.String:
                            default:
                                currentCellValue = cell.CellValue.Text;
                                break;
                        }
                    }
                    else
                    {
                        currentCellValue = cell.InnerText;
                    }
                    if (cellFormat.ApplyNumberFormat != null && cellFormat.ApplyNumberFormat && cellFormat.NumberFormatId != null)
                    {
                        int numberFormatId = (int)cellFormat.NumberFormatId.Value;
                        dataFieldType = GetDataFieldDataTypeFromCellNumberFormat(numberFormatId);
                    }
                    if (dataFieldType != DataFieldType.String && dataFieldType != dataField.DataType)
                    {
                        dataField.DataType = dataFieldType;
                    }
                    if (dataFieldType == DataFieldType.DateTime)
                    {
                        currentCellValue = DateTime.FromOADate(int.Parse(currentCellValue)).ToString("s");
                    }
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
                            var themeChild = (DocumentFormat.OpenXml.Drawing.Color2Type)workbookPart.ThemePart.Theme.ThemeElements.ColorScheme.ChildElements[(int)colorType.Theme.Value];
                            cellColor = themeChild.RgbColorModelHex == null ? "" : themeChild.RgbColorModelHex.Val;
                        }
                        cellTint = colorType.Tint == null ? 0.0 : colorType.Tint.Value;
                    }
                    Font font = (Font)styles.Stylesheet.Fonts.ChildElements[(int)cellFormat.FontId.Value];
                    var fontWeight = font.Bold == null ? "normal" : "bold";
                    cellMetadata = cellColor + "," + cellTint + "," + fontWeight + "," + (int)dataField.DataType;
                }

                var dataValue = new DataValueEntity() {
                    Id = Guid.NewGuid(),
                    ScenarioEventId = scenarioEvent.Id,
                    DataFieldId = dataField.Id,
                    Value = currentCellValue,
                    CellMetadata = cellMetadata
                };
                await _context.DataValues.AddAsync(dataValue);
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
            var dataFieldStyles = dataFields.Where(df => df.CellMetadata != null).DistinctBy(df => df.CellMetadata).Select(df => df.CellMetadata);
            var dataFieldIds = dataFields.Select(df => df.Id);
            var dataValueStyles = await _context.DataValues
                .Where(dv => dataFieldIds.Contains(dv.DataFieldId) && dv.CellMetadata != null)
                .Select(dv => dv.CellMetadata)
                .ToListAsync();
            var allStyles = dataFieldStyles.Union(dataValueStyles);
            foreach (var style in allStyles)
            {
                uniqueStyles[style] = uniqueStyles.Count + 1;
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
                var styleParts = uniqueStyles[i].Split(',');
                Fill newFill = new Fill();
                PatternFill patternFill = new PatternFill() { PatternType = PatternValues.Solid };
                var rgb = styleParts[0] == "" ? "FFFFFF" : styleParts[0];
                ForegroundColor foregroundColor = new ForegroundColor() { Rgb = rgb, Tint = double.Parse(styleParts[1]) };
                BackgroundColor backgroundColor = new BackgroundColor() { Indexed = (UInt32Value)64U };
                patternFill.Append(foregroundColor);
                patternFill.Append(backgroundColor);
                newFill.Append(patternFill);
                fills.Append(newFill);
            }

            // borders
            Borders borders = new Borders();
            Border noBorder = new Border();
            Border border = new Border();
            LeftBorder leftBorder = new LeftBorder(){ Style = BorderStyleValues.Thin };
            leftBorder.Append(new Color(){ Indexed = (UInt32Value)64U });
            RightBorder rightBorder = new RightBorder(){ Style = BorderStyleValues.Thin };
            rightBorder.Append(new Color(){ Indexed = (UInt32Value)64U });
            TopBorder topBorder = new TopBorder(){ Style = BorderStyleValues.Thin };
            topBorder.Append(new Color(){ Indexed = (UInt32Value)64U });
            BottomBorder bottomBorder = new BottomBorder(){ Style = BorderStyleValues.Thin };
            bottomBorder.Append(new Color(){ Indexed = (UInt32Value)64U });
            border.Append(leftBorder);
            border.Append(rightBorder);
            border.Append(topBorder);
            border.Append(bottomBorder);
            borders.Append(noBorder);
            borders.AppendChild(border);
            borders.Count = (UInt32Value)2U;

            CellStyleFormats cellStyleFormats = new CellStyleFormats() { Count = (UInt32Value)1U };
            CellFormat cellFormat1 = new CellFormat() { NumberFormatId = (UInt32Value)0U, FontId = (UInt32Value)0U, FillId = (UInt32Value)0U, BorderId = (UInt32Value)1U };

            cellStyleFormats.Append(cellFormat1);

            CellFormats cellFormats = new CellFormats();
            CellFormat cellFormat2 = new CellFormat() { NumberFormatId = (UInt32Value)0U, FontId = (UInt32Value)0U, FillId = (UInt32Value)0U, BorderId = (UInt32Value)1U, FormatId = (UInt32Value)0U };
            cellFormats.Append(cellFormat2);
            for (int i=0; i < uniqueStyles.Count; i++)
            {
                var styleParts = uniqueStyles[i].Split(',');
                UInt32 fontId = styleParts[2] == "bold" ? 1U : 0U;
                UInt32 numberFormatId = 0;
                var applyNumberFormat = false;
                GetCellNumberFormatFromDataFieldDataType((DataFieldType)int.Parse(styleParts[3]), out numberFormatId, out applyNumberFormat);
                CellFormat cellFormat = new CellFormat(new Alignment() { WrapText = true }) {
                    NumberFormatId = (UInt32Value)numberFormatId,
                    FontId = (UInt32Value)fontId,
                    FillId = (UInt32Value)((UInt32)i + 2),
                    BorderId = (UInt32Value)1U,
                    FormatId = (UInt32Value)0U,
                    ApplyFill = true,
                    ApplyBorder = true,
                    ApplyAlignment = true };
                    if (applyNumberFormat)
                    {
                        cellFormat.ApplyNumberFormat = true;
                    }
                    if (fontId != 0)
                    {
                        cellFormat.ApplyFont = true;
                    }
                cellFormats.Append(cellFormat);
            }
            cellFormats.Count = (uint)cellFormats.ChildElements.Count;

            CellStyles cellStyles = new CellStyles() { Count = (UInt32Value)1U };
            CellStyle cellStyle1 = new CellStyle() { Name = "Normal", FormatId = (UInt32Value)0U, BuiltinId = (UInt32Value)0U };
            cellStyles.Append(cellStyle1);
            DifferentialFormats differentialFormats = new DifferentialFormats() { Count = (UInt32Value)0U };
            TableStyles tableStyles = new TableStyles() { Count = (UInt32Value)0U, DefaultTableStyle = "TableStyleMedium2", DefaultPivotStyle = "PivotStyleMedium9" };

            StylesheetExtensionList stylesheetExtensionList = new StylesheetExtensionList();
            StylesheetExtension stylesheetExtension1 = new StylesheetExtension() { Uri = "{EB79DEF2-80B8-43e5-95BD-54CBDDF9020C}" };
            stylesheetExtension1.AddNamespaceDeclaration("x14", "http://schemas.microsoft.com/office/spreadsheetml/2009/9/main");
            StylesheetExtension stylesheetExtension2 = new StylesheetExtension() { Uri = "{9260A510-F301-46a8-8635-F512D64BE5F5}" };
            stylesheetExtension2.AddNamespaceDeclaration("x14", "http://schemas.microsoft.com/office/spreadsheetml/2010/11/main");
            // X14.SlicerStyles slicerStyles1 = new X14.SlicerStyles() { DefaultSlicerStyle = "SlicerStyleLight1" };
            // stylesheetExtension1.Append(slicerStyles1);
            stylesheetExtensionList.Append(stylesheetExtension1);
            stylesheetExtensionList.Append(stylesheetExtension2);

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

        private CellValues GetCellDataTypeFromDataFieldType(DataFieldType dataFieldType)
        {
            var cellDataType = CellValues.String;
            switch (dataFieldType)
            {
                case DataFieldType.Boolean:
                    cellDataType = CellValues.Boolean;
                    break;
                case DataFieldType.DateTime:
                    cellDataType = CellValues.Date;
                    break;
                case DataFieldType.Double:
                case DataFieldType.Integer:
                    cellDataType = CellValues.Number;
                    break;
                default:
                    break;
            }
            return cellDataType;
        }

        /**
            the GetDataFieldDataTypeFromCellNumberFormat method makes use of these built-in number formats to determine a date value
            in particular IDs 14-22, 30, and 45-47
            0 = 'General';
            1 = '0';
            2 = '0.00';
            3 = '#,##0';
            4 = '#,##0.00';
            5 = '$#,##0;\-$#,##0';
            6 = '$#,##0;[Red]\-$#,##0';
            7 = '$#,##0.00;\-$#,##0.00';
            8 = '$#,##0.00;[Red]\-$#,##0.00';
            9 = '0%';
            10 = '0.00%';
            11 = '0.00E+00';
            12 = '# ?/?';
            13 = '# ??/??';
            14 = 'mm-dd-yy';
            15 = 'd-mmm-yy';
            16 = 'd-mmm';
            17 = 'mmm-yy';
            18 = 'h:mm AM/PM';
            19 = 'h:mm:ss AM/PM';
            20 = 'h:mm';
            21 = 'h:mm:ss';
            22 = 'm/d/yy h:mm';
            27 = '[$-404]e/m/d';
            30 = 'm/d/yy';
            36 = '[$-404]e/m/d';
            37 = '#,##0 ;(#,##0)';
            38 = '#,##0 ;[Red](#,##0)';
            39 = '#,##0.00;(#,##0.00)';
            40 = '#,##0.00;[Red](#,##0.00)';
            44 = '_("$"* #,##0.00_);_("$"* \(#,##0.00\);_("$"* "-"??_);_(@_)';
            45 = 'mm:ss';
            46 = '[h]:mm:ss';
            47 = 'mmss.0';
            48 = '##0.0E+0';
            49 = '@';
            50 = '[$-404]e/m/d';
            57 = '[$-404]e/m/d';
            59 = 't0';
            60 = 't0.00';
            61 = 't#,##0';
            62 = 't#,##0.00';
            67 = 't0%';
            68 = 't0.00%';
            69 = 't# ?/?';
            70 = 't# ??/??';
        **/
        private DataFieldType GetDataFieldDataTypeFromCellNumberFormat(int numberFormatId)
        {
            DataFieldType dataFieldType;
            switch (numberFormatId)
            {
                case 1:
                    dataFieldType = DataFieldType.Integer;
                    break;
                case 2:
                case 3:
                case 4:
                case 5:
                case 6:
                case 7:
                case 8:
                case 9:
                case 10:
                case 11:
                    dataFieldType = DataFieldType.Double;
                    break;
                case 14:
                case 15:
                case 16:
                case 17:
                case 30:
                case 45:
                case 46:
                case 47:
                    dataFieldType = DataFieldType.DateTime;
                    break;
                default:
                    dataFieldType = DataFieldType.String;
                    break;
            }
            return dataFieldType;
        }

        private void GetCellNumberFormatFromDataFieldDataType(DataFieldType dataFieldType, out UInt32 numberFormatId, out bool applyNumberFormat)
        {
                switch (dataFieldType)
                {
                    case DataFieldType.Double:
                        numberFormatId = 1;
                        applyNumberFormat = true;
                        break;
                    case DataFieldType.Integer:
                        numberFormatId = 1;
                        applyNumberFormat = true;
                        break;
                    case DataFieldType.DateTime:
                        numberFormatId = 14;
                        applyNumberFormat = true;
                        break;
                    default:
                        numberFormatId = 0;
                        applyNumberFormat = false;
                        break;
                }
        }

        private string GetCellReference(int columnIndex, int rowIndex)
        {
            var firstLetter = columnIndex > 26 ? char.ConvertFromUtf32(64 + columnIndex / 26) : "";
            var secondLetter = char.ConvertFromUtf32(64 + columnIndex % 26);

            return firstLetter + secondLetter + rowIndex.ToString();
        }

    }
}

