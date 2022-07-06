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
using ClosedXML.Excel;

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
    }

    public class MselService : IMselService
    {
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;
        private readonly ILogger<GalleryService> _logger;
        private Dictionary<XLThemeColor, string> _themeColors = new Dictionary<XLThemeColor, string>();

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

            var memoryStream = new MemoryStream();
            var workbook = new XLWorkbook();
            IXLWorksheet worksheet = workbook.Worksheets.Add("MSEL");
            // get the MSEL data into a DataTable
            await PopulateWorksheetAsync(mselId, worksheet, ct);
            // save the workbook to the memory stream
            workbook.SaveAs(memoryStream, false, false);
            // reset the stream position to the start of the stream
            memoryStream.Seek(0, SeekOrigin.Begin);
            var filename = msel.Description.ToLower().EndsWith(".xlsx") ? msel.Description : msel.Description + ".xlsx";

            return System.Tuple.Create(memoryStream, filename);
        }

        private async Task createMselFromXlsxFile(FileForm form, Guid mselId, CancellationToken ct)
        {
            var uploadItem = form.ToUpload;
            using (var workbook = new XLWorkbook(uploadItem.OpenReadStream()))
            {
                var worksheet = workbook.Worksheet(1);
                var headerRow = worksheet.FirstRowUsed();
                var lastUsedRow = worksheet.LastRowUsed();
                LoadThemeColors(workbook);
                // create the MSEL enitiy
                var msel = new MselEntity() {
                    Id = mselId,
                    Description = uploadItem.FileName,
                    Status = ItemStatus.Pending,
                    TeamId = form.TeamId,
                    IsTemplate = false,
                    HeaderRowMetadata = headerRow.Height.ToString(),
                    CreatedBy = _user.GetId(),
                    DateCreated = DateTime.UtcNow
                };
                await _context.Msels.AddAsync(msel, ct);
                // create the data fields
                var dataFields = CreateDataFields(mselId, headerRow);
                // create the sceanrio events and data values
                await CreateScenarioEventsAsync(mselId, headerRow, lastUsedRow, dataFields);
                await _context.DataFields.AddRangeAsync(dataFields);
            }
            await _context.SaveChangesAsync(ct);
        }

        private void LoadThemeColors(IXLWorkbook workbook)
        {
            _themeColors[XLThemeColor.Accent1] = workbook.Theme.Accent1.Color.ToArgb().ToString();
            _themeColors[XLThemeColor.Accent2] = workbook.Theme.Accent2.Color.ToArgb().ToString();
            _themeColors[XLThemeColor.Accent3] = workbook.Theme.Accent3.Color.ToArgb().ToString();
            _themeColors[XLThemeColor.Accent4] = workbook.Theme.Accent4.Color.ToArgb().ToString();
            _themeColors[XLThemeColor.Accent5] = workbook.Theme.Accent5.Color.ToArgb().ToString();
            _themeColors[XLThemeColor.Accent6] = workbook.Theme.Accent6.Color.ToArgb().ToString();
            _themeColors[XLThemeColor.Background1] = workbook.Theme.Background1.Color.ToArgb().ToString();
            _themeColors[XLThemeColor.Background2] = workbook.Theme.Background2.Color.ToArgb().ToString();
            _themeColors[XLThemeColor.Text1] = workbook.Theme.Text1.Color.ToArgb().ToString();
            _themeColors[XLThemeColor.Text2] = workbook.Theme.Text2.Color.ToArgb().ToString();
        }

        private List<DataFieldEntity> CreateDataFields(Guid mselId, IXLRow headerRow)
        {
            var dataFields = new List<DataFieldEntity>();
            var displayOrder = 1;
            foreach (IXLCell thecurrentcell in headerRow.Cells())
            {
                string currentcellvalue = thecurrentcell.Value.ToString();
                var theDataType = thecurrentcell.DataType;
                if (!string.IsNullOrEmpty(currentcellvalue))
                {
                    var cellColor = "";
                    var cellTint = "";
                    var fill = thecurrentcell.Style.Fill;
                    if (fill.BackgroundColor.ColorType == XLColorType.Theme)
                    {
                        cellColor = _themeColors[fill.BackgroundColor.ThemeColor];
                        cellTint = fill.BackgroundColor.ThemeTint.ToString();
                    }
                    else
                    {
                        cellColor = fill.BackgroundColor.Color.ToArgb().ToString();
                        cellTint = "0.0";
                    }
                    var isBold = thecurrentcell.Style.Font.Bold;
                    var columnMetadata = "0.0";
                    if (thecurrentcell.WorksheetColumn() != null)
                    {
                        var columnWidth = thecurrentcell.WorksheetColumn().Width;
                        columnMetadata = columnWidth.ToString();
                    }
                    var dataField = new DataFieldEntity() {
                        Id = Guid.NewGuid(),
                        MselId = mselId,
                        Name = currentcellvalue,
                        DataType = DataFieldType.String,
                        DisplayOrder = displayOrder,
                        IsChosenFromList = false,
                        CellMetadata = cellColor + "," + cellTint + "," + isBold.ToString(),
                        ColumnMetadata = columnMetadata,
                        CreatedBy = _user.GetId(),
                        DateCreated = DateTime.UtcNow
                    };
                    dataFields.Add(dataField);
                }
                displayOrder++;
            }

            return dataFields;
        }

        private async Task CreateScenarioEventsAsync(Guid mselId, IXLRow headerRow, IXLRow lastUsedRow, List<DataFieldEntity> dataFields)
        {
            var currentRow = headerRow;
            var scenarioEventList = new List<ScenarioEventEntity>();
            while (currentRow != lastUsedRow)
            {
                currentRow = currentRow.RowBelow();
                var scenarioEventId = Guid.NewGuid();
                var scenarioEvent = new ScenarioEventEntity() {
                    Id = scenarioEventId,
                    MselId = mselId,
                    RowIndex = currentRow.RowNumber(),
                    RowMetadata = currentRow.Height.ToString(),
                    CreatedBy = _user.GetId(),
                    DateCreated = DateTime.UtcNow
                };
                await _context.ScenarioEvents.AddAsync(scenarioEvent);
                await CreateDataValuesAsync(scenarioEvent, currentRow, dataFields);
            }
        }

        private async Task CreateDataValuesAsync(ScenarioEventEntity scenarioEvent, IXLRow dataRow, List<DataFieldEntity> dataFields)
        {
            foreach (IXLCell cell in dataRow.Cells())
            {
                //statement to take the integer value
                string currentcellvalue = "";
                switch (cell.DataType)
                {
                    case XLDataType.Boolean:
                        currentcellvalue = cell.GetValue<bool>().ToString();
                        dataFields[cell.WorksheetColumn().ColumnNumber() - 1].DataType = (DataFieldType.Boolean);
                        break;
                    case XLDataType.DateTime:
                        currentcellvalue = cell.GetValue<DateTime>().ToString();
                        dataFields[cell.WorksheetColumn().ColumnNumber() - 1].DataType = (DataFieldType.DateTime);
                        break;
                    case XLDataType.Number:
                        currentcellvalue = cell.GetValue<double>().ToString();
                        dataFields[cell.WorksheetColumn().ColumnNumber() - 1].DataType = (DataFieldType.Double);
                        break;
                    case XLDataType.TimeSpan:
                        currentcellvalue = cell.GetValue<TimeSpan>().ToString();
                        dataFields[cell.WorksheetColumn().ColumnNumber() - 1].DataType = (DataFieldType.String);
                        break;
                    case XLDataType.Text:
                    default:
                        currentcellvalue = cell.GetValue<string>();
                        break;
                }
                var displayOrder = cell.WorksheetColumn().ColumnNumber();
                var dataField = dataFields.FirstOrDefault(df => df.MselId == scenarioEvent.MselId && df.DisplayOrder == displayOrder);
                if (dataField != null)
                {
                    var cellColor = "";
                    var cellTint = "";
                    var fill = cell.Style.Fill;
                    if (fill.BackgroundColor.ColorType == XLColorType.Theme)
                    {
                        cellColor = _themeColors[fill.BackgroundColor.ThemeColor];
                        cellTint = fill.BackgroundColor.ThemeTint.ToString();
                    }
                    else
                    {
                        cellColor = fill.BackgroundColor.Color.ToArgb().ToString();
                        cellTint = "0.0";
                    }
                    var isBold = cell.Style.Font.Bold;
                    var dataValue = new DataValueEntity() {
                        Id = Guid.NewGuid(),
                        ScenarioEventId = scenarioEvent.Id,
                        DataFieldId = dataField.Id,
                        Value = currentcellvalue,
                        CellMetadata = cellColor + "," + cellTint + "," + isBold.ToString()
                    };
                    await _context.DataValues.AddAsync(dataValue);
                }
            }
        }

        private async Task PopulateWorksheetAsync(Guid mselId, IXLWorksheet worksheet, CancellationToken ct)
        {
            // add a column for each data field
            var dataFieldList = await _context.DataFields
                .Where(df => df.MselId == mselId)
                .OrderBy(df => df.DisplayOrder)
                .ToListAsync(ct);
            var columnIndexList = new Dictionary<string, int>();
            foreach (var dataField in dataFieldList)
            {
                columnIndexList[dataField.Name] = dataField.DisplayOrder;
                worksheet.Cell(1, dataField.DisplayOrder).DataType = XLDataType.Text;
                worksheet.Cell(1, dataField.DisplayOrder).Value = dataField.Name;
                worksheet.Cell(1, dataField.DisplayOrder).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                worksheet.Cell(1, dataField.DisplayOrder).Style.Fill.SetBackgroundColor(XLColor.FromArgb(int.Parse(dataField.CellMetadata.Split(",")[0])));
                worksheet.Cell(1, dataField.DisplayOrder).WorksheetColumn().Width = double.Parse(dataField.ColumnMetadata.Split(",")[0]);
            }
            // add a row for each scenarioEvent
            var scenarioEventList = await _context.ScenarioEvents
                .Where(n => n.MselId == mselId)
                .OrderBy(n => n.RowIndex)
                .ToListAsync(ct);
            foreach (var scenarioEvent in scenarioEventList)
            {
                var dataValueList = await _context.DataValues
                    .Where(dv => dv.ScenarioEventId == scenarioEvent.Id)
                    .OrderBy(dv =>dv.DataField.DisplayOrder)
                    .Select(dv => new { Column = dv.DataField.DisplayOrder, DataType = dv.DataField.DataType, Value = dv.Value, CellMetadata = dv.CellMetadata })
                    .ToListAsync(ct);
                foreach (var dataValue in dataValueList)
                {
                    worksheet.Cell(scenarioEvent.RowIndex, dataValue.Column).Value = dataValue.Value;
                    var dataType = XLDataType.Text;
                    switch (dataValue.DataType)
                    {
                        case DataFieldType.Boolean:
                            dataType = XLDataType.Boolean;
                            break;
                        case DataFieldType.DateTime:
                            dataType = XLDataType.DateTime;
                            break;
                        case DataFieldType.Integer:
                        case DataFieldType.Double:
                            dataType = XLDataType.Number;
                            break;
                        default:
                            break;
                    }
                    worksheet.Cell(scenarioEvent.RowIndex, dataValue.Column).DataType = dataType;
                    worksheet.Cell(scenarioEvent.RowIndex, dataValue.Column).Style.Fill.SetBackgroundColor(XLColor.FromArgb(int.Parse(dataValue.CellMetadata.Split(",")[0])));
                    worksheet.Cell(scenarioEvent.RowIndex, dataValue.Column).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                }
                // set row height
                worksheet.Cell(scenarioEvent.RowIndex, 1).WorksheetRow().Height = double.Parse(scenarioEvent.RowMetadata.Split(",")[0]);
            }
        }

    }
}

