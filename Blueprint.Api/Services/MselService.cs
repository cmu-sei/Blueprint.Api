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

                //statement to get the worksheet object by using the sheet id
                Worksheet worksheet = ((WorksheetPart)workbookPart.GetPartById(sheetCollection.GetFirstChild<Sheet>().Id)).Worksheet;

                SheetData sheetData = (SheetData)worksheet.GetFirstChild<SheetData>();
                var msel = new MselEntity() {
                    Id = mselId,
                    Description = uploadItem.FileName,
                    Status = ItemStatus.Pending,
                    TeamId = form.TeamId,
                    IsTemplate = false
                };
                await _context.Msels.AddAsync(msel, ct);
                var headerRow = sheetData.GetFirstChild<Row>();
                var dataFields = CreateDataFields(mselId, headerRow, workbookPart);
                await _context.DataFields.AddRangeAsync(dataFields);
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
                var dataFields = CreateDataFields(mselId, headerRow, workbookPart);
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
            var dataTable = await GetMselDataAsync(mselId, ct);
            // create the xlsx file in memory
            MemoryStream memoryStream = new MemoryStream();
            using (SpreadsheetDocument document = SpreadsheetDocument.Create(memoryStream, SpreadsheetDocumentType.Workbook))
            {
                WorkbookPart workbookPart = document.AddWorkbookPart();
                workbookPart.Workbook = new Workbook();

                WorksheetPart worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                var sheetData = new SheetData();
                worksheetPart.Worksheet = new Worksheet(sheetData);

                Sheets sheets = workbookPart.Workbook.AppendChild(new Sheets());
                Sheet sheet = new Sheet() { Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = 1, Name = "Sheet1" };

                sheets.Append(sheet);

                Row headerRow = new Row();

                List<String> columns = new List<string>();
                foreach (System.Data.DataColumn column in dataTable.Columns)
                {
                    columns.Add(column.ColumnName);

                    Cell cell = new Cell();
                    cell.DataType = CellValues.String;
                    cell.CellValue = new CellValue(column.ColumnName);
                    headerRow.AppendChild(cell);
                }

                sheetData.AppendChild(headerRow);

                foreach (DataRow dsrow in dataTable.Rows)
                {
                    Row newRow = new Row();
                    foreach (String col in columns)
                    {
                        Cell cell = new Cell();
                        cell.DataType = CellValues.String;
                        cell.CellValue = new CellValue(dsrow[col].ToString());
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
            // get the MSEL data into a DataTable
            var dataTable = await GetMselDataAsync(mselId, ct);

            return dataTable;
        }

        private List<DataFieldEntity> CreateDataFields(Guid mselId, Row headerRow, WorkbookPart workbookPart)
        {
            var dataFields = new List<DataFieldEntity>();
            var displayOrder = 1;
            foreach (Cell thecurrentcell in headerRow)
            {
                //statement to take the integer value
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
                    var dataField = new DataFieldEntity() {
                        Id = Guid.NewGuid(),
                        MselId = mselId,
                        Name = currentcellvalue,
                        DataType = DataFieldType.String,
                        DisplayOrder = displayOrder,
                        IsChosenFromList = false
                    };
                    dataFields.Add(dataField);
                }
                displayOrder++;
            }

            return dataFields;
        }

        private async Task CreateScenarioEventsAsync(Guid mselId, SheetData dataRows, WorkbookPart workbookPart, List<DataFieldEntity> dataFields)
        {
            var scenarioEventList = new List<ScenarioEventEntity>();
            foreach (Row dataRow in dataRows)
            {
                var scenarioEventId = Guid.NewGuid();
                var scenarioEvent = new ScenarioEventEntity() {
                    Id = scenarioEventId,
                    MselId = mselId
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
                var dataField = dataFields.First(df => df.MselId == scenarioEvent.MselId && df.DisplayOrder == displayOrder);
                var dataValue = new DataValueEntity() {
                    Id = Guid.NewGuid(),
                    ScenarioEventId = scenarioEvent.Id,
                    DataFieldId = dataField.Id,
                    Value = currentcellvalue
                };
                await _context.DataValues.AddAsync(dataValue);
                // set scenarioEvent move or scenarioEvent based on value
                if (dataField.Name.ToLower() == "move")
                {
                    int moveNumber = 0;
                    scenarioEvent.MoveNumber = int.TryParse(currentcellvalue, out moveNumber) ? moveNumber : 0;
                }
                else if (dataField.Name.ToLower().Replace(" ", "") == "scenarioevent" || dataField.Name.ToLower() == "inject")
                {
                    int scenarioEventNumber = 0;
                    scenarioEvent.ScenarioEventNumber = int.TryParse(currentcellvalue, out scenarioEventNumber) ? scenarioEventNumber : 0;
                }
                else if (dataField.Name.ToLower().Replace(" ", "") == "group")
                {
                    scenarioEvent.Group = currentcellvalue;
                }
            }
        }

        private async Task<DataTable> GetMselDataAsync(Guid mselId, CancellationToken ct)
        {
            // create data table to hold all of the scenarioEvent data
            DataTable dataTable = new DataTable();
            dataTable.Clear();
            // add a column for each data field
            var dataFieldList = await _context.DataFields
                .Where(df => df.MselId == mselId)
                .OrderBy(df => df.DisplayOrder)
                .Select(df => new { DisplayOrder = df.DisplayOrder, Name = df.Name })
                .ToListAsync(ct);
            foreach (var dataField in dataFieldList)
            {
                dataTable.Columns.Add(dataField.Name);
            }
            // add a row for each scenarioEvent
            var scenarioEventList = await _context.ScenarioEvents
                .Where(n => n.MselId == mselId)
                .OrderBy(n => n.MoveNumber)
                .ThenBy(n => n.ScenarioEventNumber)
                .ToListAsync(ct);
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

    }
}

