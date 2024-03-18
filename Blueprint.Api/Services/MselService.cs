// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
using Blueprint.Api.Infrastructure.Options;
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
        Task<IEnumerable<ViewModels.Msel>> GetUserMselsAsync(Guid userId, CancellationToken ct);
        Task<ViewModels.Msel> GetAsync(Guid id, CancellationToken ct);
        Task<ViewModels.Msel> CreateAsync(ViewModels.Msel msel, CancellationToken ct);
        Task<ViewModels.Msel> CopyAsync(Guid mselId, CancellationToken ct);
        Task<ViewModels.Msel> UpdateAsync(Guid id, ViewModels.Msel msel, CancellationToken ct);
        Task<ViewModels.Msel> AddTeamToMselAsync(Guid mselId, Guid teamId, CancellationToken ct);
        Task<ViewModels.Msel> RemoveTeamFromMselAsync(Guid mselId, Guid teamId, CancellationToken ct);
        Task<ViewModels.Msel> AddUserMselRoleAsync(Guid mselId, Guid userId, MselRole mselRole, CancellationToken ct);
        Task<ViewModels.Msel> RemoveUserMselRoleAsync(Guid mselId, Guid userId, MselRole mselRole, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
        Task<Msel> UploadXlsxAsync(FileForm form, CancellationToken ct);
        Task<Msel> ReplaceAsync(FileForm form, Guid mselId, CancellationToken ct);
        Task<Tuple<MemoryStream, string>> DownloadXlsxAsync(Guid mselId, CancellationToken ct);
        Task<Msel> UploadJsonAsync(FileForm form, CancellationToken ct);
        Task<Tuple<MemoryStream, string>> DownloadJsonAsync(Guid mselId, CancellationToken ct);
        Task<DataTable> GetDataTableAsync(Guid mselId, CancellationToken ct);
        Task<ViewModels.Msel> PushIntegrationsAsync(Guid mselId, CancellationToken ct);
        Task<ViewModels.Msel> PullIntegrationsAsync(Guid mselId, CancellationToken ct);
        Task<IEnumerable<ViewModels.Msel>> GetMyJoinInvitationMselsAsync(CancellationToken ct);
        Task<IEnumerable<ViewModels.Msel>> GetMyLaunchInvitationMselsAsync(CancellationToken ct);
        Task<Guid> JoinMselAsync(Guid mselId, CancellationToken ct);  // returns the Player View ID
        Task<Guid> LaunchMselAsync(Guid mselId, CancellationToken ct);  // returns the Player View ID
    }

    public class MselService : IMselService
    {
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;
        private readonly ILogger<MselService> _logger;
        private readonly ClientOptions _clientOptions;
        private readonly IScenarioEventService _scenarioEventService;
        private readonly IIntegrationQueue _integrationQueue;
        private readonly IPlayerService _playerService;
        private readonly IJoinQueue _joinQueue;

        public MselService(
            BlueprintContext context,
            ClientOptions clientOptions,
            IAuthorizationService authorizationService,
            IScenarioEventService scenarioEventService,
            IIntegrationQueue integrationQueue,
            IPlayerService playerService,
            IJoinQueue joinQueue,
            IPrincipal user,
            ILogger<MselService> logger,
            IMapper mapper)
        {
            _context = context;
            _clientOptions = clientOptions;
            _authorizationService = authorizationService;
            _scenarioEventService = scenarioEventService;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
            _logger = logger;
            _integrationQueue = integrationQueue;
            _playerService = playerService;
            _joinQueue = joinQueue;
        }

        public async Task<IEnumerable<ViewModels.Msel>> GetAsync(MselGet queryParameters, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
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
                var mselTeamIdList = await _context.MselTeams
                    .Where(mt => mt.TeamId == teamId)
                    .Select(mt => mt.TeamId)
                    .ToListAsync();
                if (msels == null)
                {
                    msels = _context.Msels
                        .Where(m => mselTeamIdList.Contains(m.Id));
                }
                else
                {
                    msels = msels.Where(m => mselTeamIdList.Contains(m.Id));
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
            var userId = _user.GetId();
            return await GetUserMselsAsync(userId, ct);
        }

        public async Task<IEnumerable<ViewModels.Msel>> GetUserMselsAsync(Guid userId, CancellationToken ct)
        {
            var currentUserId = _user.GetId();
            if (currentUserId == userId)
            {
                if (!(await _authorizationService.AuthorizeAsync(_user, null, new BaseUserRequirement())).Succeeded)
                    throw new ForbiddenException();
            }
            else
            {
                if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                    throw new ForbiddenException();
            }
            // get the user's teams
            var teamIdList = await _context.TeamUsers
                .Where(tu => tu.UserId == userId)
                .Select(tu => tu.TeamId)
                .ToListAsync(ct);
            // get the teams' msels
            var teamMselList = await _context.MselTeams
                .Where(mt => teamIdList.Contains(mt.TeamId))
                .Select(mt => mt.Msel)
                .ToListAsync(ct);
            // get msels created by user and all templates
            var myMselList = await _context.Msels
                .Where(m => m.CreatedBy == userId || m.IsTemplate)
                .ToListAsync(ct);
            // combine lists
            var mselList = teamMselList.Union(myMselList).OrderByDescending(m => m.DateCreated);

            return _mapper.Map<IEnumerable<Msel>>(mselList);
        }

        public async Task<ViewModels.Msel> GetAsync(Guid id, CancellationToken ct)
        {
            if (
                    !(await MselViewRequirement.IsMet(_user.GetId(), id, _context)) &&
                    !(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded
               )
            {
                var mselCheck = await _context.Msels.FindAsync(id);
                if (!mselCheck.IsTemplate)
                    throw new ForbiddenException();
            }

            var mselEntity = await _context.Msels
                .Include(m => m.Teams)
                .ThenInclude(t => t.TeamUsers)
                .ThenInclude(tu => tu.User)
                .Include(m => m.UserMselRoles)
                .AsSplitQuery()
                .SingleOrDefaultAsync(sm => sm.Id == id, ct);
            var msel = _mapper.Map<Msel>(mselEntity);
            // add the needed parameters for Gallery integration
            if (msel.UseGallery)
            {
                msel.GalleryArticleParameters = Enum.GetNames(typeof(GalleryArticleParameter)).ToList();
                msel.GallerySourceTypes = Enum.GetNames(typeof(GallerySourceType)).ToList();
            }

            return msel;
        }

        public async Task<ViewModels.Msel> CreateAsync(ViewModels.Msel msel, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            msel.Id = msel.Id != Guid.Empty ? msel.Id : Guid.NewGuid();
            msel.DateCreated = DateTime.UtcNow;
            msel.CreatedBy = _user.GetId();
            msel.DateModified = msel.DateCreated;
            msel.ModifiedBy = msel.CreatedBy;
            var mselEntity = _mapper.Map<MselEntity>(msel);

            _context.Msels.Add(mselEntity);
            await _context.SaveChangesAsync(ct);
            msel = await GetAsync(mselEntity.Id, ct);

            return msel;
        }

        public async Task<ViewModels.Msel> CopyAsync(Guid mselId, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();
            
            var newMselEntity = await privateMselCopyAsync(mselId, null, ct);
            var msel = _mapper.Map<Msel>(newMselEntity);
            // add the needed parameters for Gallery integration
            if (msel.UseGallery)
            {
                msel.GalleryArticleParameters = Enum.GetNames(typeof(GalleryArticleParameter)).ToList();
                msel.GallerySourceTypes = Enum.GetNames(typeof(GallerySourceType)).ToList();
            }
            return msel;
        }

        private async Task<MselEntity> privateMselCopyAsync(Guid mselId, Guid? currentUserTeamId, CancellationToken ct)
        {
            var username = (await _context.Users.SingleOrDefaultAsync(u => u.Id == _user.GetId())).Name;
            var mselEntity = await _context.Msels
                .AsNoTracking()
                .Include(m => m.DataFields)
                .ThenInclude(df => df.DataOptions)
                .Include(m => m.ScenarioEvents)
                .ThenInclude(se => se.DataValues)
                .Include(m => m.Teams)
                .ThenInclude(t => t.TeamUsers)
                .ThenInclude(tu => tu.User)
                .Include(m => m.UserMselRoles)
                .Include(m => m.Moves)
                .Include(m => m.Organizations)
                .Include(m => m.Cards)
                .Include(m => m.CiteActions)
                .Include(m => m.CiteRoles)
                .AsSplitQuery()
                .SingleOrDefaultAsync(m => m.Id == mselId);
            if (mselEntity == null)
                throw new EntityNotFoundException<MselEntity>("MSEL not found with ID=" + mselId.ToString());

            mselEntity.Id = Guid.NewGuid();
            mselEntity.DateCreated = DateTime.UtcNow;
            mselEntity.CreatedBy = _user.GetId();
            mselEntity.DateModified = mselEntity.DateCreated;
            mselEntity.ModifiedBy = mselEntity.CreatedBy;
            mselEntity.Name = mselEntity.Name + " - " + username;
            mselEntity.IsTemplate = false;
            mselEntity.GalleryCollectionId = null;
            mselEntity.GalleryExhibitId = null;
            mselEntity.CiteEvaluationId = null;
            var dataFieldIdCrossReference = new Dictionary<Guid, Guid>();
            // copy DataFields
            foreach (var dataField in mselEntity.DataFields)
            {
                var newDataFieldId = Guid.NewGuid();
                dataFieldIdCrossReference[dataField.Id] = newDataFieldId;
                dataField.Id = newDataFieldId;
                dataField.MselId = mselEntity.Id;
                dataField.Msel = null;
                dataField.DateCreated = mselEntity.DateCreated;
                dataField.CreatedBy = mselEntity.CreatedBy;
                // copy DataOptions
                foreach (var dataOption in dataField.DataOptions)
                {
                    dataOption.Id = Guid.NewGuid();
                    dataOption.DataFieldId = dataField.Id;
                    dataOption.DataField = null;
                    dataOption.DateCreated = mselEntity.DateCreated;
                    dataOption.CreatedBy = mselEntity.CreatedBy;
                }
            }
            // copy Moves
            foreach (var move in mselEntity.Moves)
            {
                move.Id = Guid.NewGuid();
                move.MselId = mselEntity.Id;
                move.Msel = null;
                move.DateCreated = mselEntity.DateCreated;
                move.CreatedBy = mselEntity.CreatedBy;
            }
            // copy Teams
            foreach (var team in mselEntity.Teams)
            {
                var addUser = team.Id == currentUserTeamId;
                // add current user to the indicated team
                if (addUser)
                {
                    team.TeamUsers.Add(new TeamUserEntity{TeamId = team.Id, UserId = _user.GetId()});
                }
            }
            // copy Organizations
            foreach (var organization in mselEntity.Organizations)
            {
                organization.Id = Guid.NewGuid();
                organization.MselId = mselEntity.Id;
                organization.Msel = null;
                organization.DateCreated = mselEntity.DateCreated;
                organization.CreatedBy = mselEntity.CreatedBy;
            }
            // copy UserMselRoles
            foreach (var userMselRole in mselEntity.UserMselRoles)
            {
                userMselRole.Id = Guid.NewGuid();
                userMselRole.MselId = mselEntity.Id;
                userMselRole.Msel = null;
                userMselRole.User = null;
            }
            // copy ScenarioEvents
            foreach (var scenarioEvent in mselEntity.ScenarioEvents)
            {
                scenarioEvent.Id = Guid.NewGuid();
                scenarioEvent.MselId = mselEntity.Id;
                scenarioEvent.Msel = null;
                scenarioEvent.DateCreated = mselEntity.DateCreated;
                scenarioEvent.CreatedBy = mselEntity.CreatedBy;
                // copy DataValues
                foreach (var dataValue in scenarioEvent.DataValues)
                {
                    dataValue.Id = Guid.NewGuid();
                    dataValue.ScenarioEventId = scenarioEvent.Id;
                    dataValue.ScenarioEvent = null;
                    dataValue.DataFieldId = dataFieldIdCrossReference[dataValue.DataFieldId];
                    dataValue.DataField = null;
                    dataValue.DateCreated = mselEntity.DateCreated;
                    dataValue.CreatedBy = mselEntity.CreatedBy;
                }
            }
            // copy Gallery Cards
            foreach (var card in mselEntity.Cards)
            {
                card.Id = Guid.NewGuid();
                card.MselId = mselEntity.Id;
                card.Msel = null;
                card.DateCreated = mselEntity.DateCreated;
                card.CreatedBy = mselEntity.CreatedBy;
                card.GalleryId = null;
            }
            // copy CITE Roles
            foreach (var citeRole in mselEntity.CiteRoles)
            {
                citeRole.Id = Guid.NewGuid();
                citeRole.MselId = mselEntity.Id;
                citeRole.Msel = null;
                citeRole.DateCreated = mselEntity.DateCreated;
                citeRole.CreatedBy = mselEntity.CreatedBy;
            }
            // copy CITE Actions
            foreach (var citeAction in mselEntity.CiteActions)
            {
                citeAction.Id = Guid.NewGuid();
                citeAction.MselId = mselEntity.Id;
                citeAction.Msel = null;
                citeAction.DateCreated = mselEntity.DateCreated;
                citeAction.CreatedBy = mselEntity.CreatedBy;
            }

            _context.Msels.Add(mselEntity);
            await _context.SaveChangesAsync(ct);

            // get the new MSEL to return
            mselEntity = await _context.Msels
                .Include(m => m.Teams)
                .ThenInclude(t => t.TeamUsers)
                .ThenInclude(tu => tu.User)
                .Include(m => m.UserMselRoles)
                .AsSplitQuery()
                .SingleOrDefaultAsync(sm => sm.Id == mselEntity.Id, ct);

            return mselEntity;
        }

        public async Task<ViewModels.Msel> UpdateAsync(Guid id, ViewModels.Msel msel, CancellationToken ct)
        {
            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !( await MselOwnerRequirement.IsMet(_user.GetId(), id, _context)))
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

        public async Task<ViewModels.Msel> AddTeamToMselAsync(Guid mselId, Guid teamId, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(v => v.Id == mselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();

            var team = await _context.Teams.SingleOrDefaultAsync(v => v.Id == teamId, ct);
            if (team == null)
                throw new EntityNotFoundException<TeamEntity>();

            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), msel.Id, _context)))
                throw new ForbiddenException();

            if (await _context.MselTeams.AnyAsync(mt => mt.TeamId ==teamId && mt.MselId == mselId))
                throw new ArgumentException("MSEL Team already exists.");

            var mselTeam = new MselTeamEntity(teamId, mselId);
            mselTeam.Id = Guid.NewGuid();
            _context.MselTeams.Add(mselTeam);
            // change the MSEL modified info
            msel.ModifiedBy = _user.GetId();
            msel.DateModified = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);

            return await GetAsync(mselId, ct);
        }

        public async Task<ViewModels.Msel> RemoveTeamFromMselAsync(Guid mselId, Guid teamId, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(v => v.Id == mselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();

            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), msel.Id, _context)))
                throw new ForbiddenException();

            var item = await _context.MselTeams.FirstOrDefaultAsync(mt => mt.TeamId ==teamId && mt.MselId == mselId);
            if (item == null)
                throw new EntityNotFoundException<MselTeamEntity>();

            _context.MselTeams.Remove(item);
            // change the MSEL modified info
            msel.ModifiedBy = _user.GetId();
            msel.DateModified = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);

            return await GetAsync(mselId, ct);
        }

        public async Task<ViewModels.Msel> AddUserMselRoleAsync(Guid userId, Guid mselId, MselRole mselRole, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(v => v.Id == mselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();

            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), msel.Id, _context)))
                throw new ForbiddenException();

            if (await _context.UserMselRoles.AnyAsync(umr => umr.UserId == userId && umr.MselId == mselId && umr.Role == mselRole))
                throw new ArgumentException("User/MSEL/Role already exists.");

            var userMeslRole = new UserMselRoleEntity(userId, mselId, mselRole);
            userMeslRole.Id = Guid.NewGuid();
            _context.UserMselRoles.Add(userMeslRole);
            // change the MSEL modified info
            msel.ModifiedBy = _user.GetId();
            msel.DateModified = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);

            return await GetAsync(mselId, ct);
        }

        public async Task<ViewModels.Msel> RemoveUserMselRoleAsync(Guid userId, Guid mselId, MselRole mselRole, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(v => v.Id == mselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();

            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), msel.Id, _context)))
                throw new ForbiddenException();

            var item = await _context.UserMselRoles.FirstOrDefaultAsync(umr => umr.UserId == userId && umr.MselId == mselId && umr.Role == mselRole);
            if (item == null)
                throw new EntityNotFoundException<UserMselRoleEntity>();

            _context.UserMselRoles.Remove(item);
            // change the MSEL modified info
            msel.ModifiedBy = _user.GetId();
            msel.DateModified = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);

            return await GetAsync(mselId, ct);
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !( await MselOwnerRequirement.IsMet(_user.GetId(), id, _context)))
                throw new ForbiddenException();

            var mselToDelete = await _context.Msels.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (mselToDelete == null)
                throw new EntityNotFoundException<Msel>();

            _context.Msels.Remove(mselToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

        public async Task<Msel> UploadXlsxAsync(FileForm form, CancellationToken ct)
        {
            // user must be a Content Developer
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            var mselEntity = await createMselFromXlsxFile(form, null, ct);

            return _mapper.Map<Msel>(mselEntity);
        }

        public async Task<Msel> ReplaceAsync(FileForm form, Guid mselId, CancellationToken ct)
        {
            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !( await MselOwnerRequirement.IsMet(_user.GetId(), mselId, _context)))
                throw new ForbiddenException();

            if (form.MselId != null && form.MselId != mselId)
                throw new ArgumentException("The mselId from the URL (" + mselId.ToString() + ") does not match the mselId supplied with the form (" + form.MselId.ToString() + ").");

            var mselEntity = await _context.Msels
                .Include(m => m.DataFields)
                .SingleOrDefaultAsync(m => m.Id == mselId);
            if (mselEntity == null)
                throw new EntityNotFoundException<MselEntity>("The MSEL does not exist to be replaced.  " + mselId.ToString());

            // start a transaction, because we will make changes as we go that may need rolled back
            await _context.Database.BeginTransactionAsync();
            // update the existing MSEL
            mselEntity = await createMselFromXlsxFile(form, mselEntity, ct);
            await _context.Database.CommitTransactionAsync(ct);

            return _mapper.Map<Msel>(mselEntity);
        }

        public async Task<Tuple<MemoryStream, string>> DownloadXlsxAsync(Guid mselId, CancellationToken ct)
        {
            var msel = await _context.Msels
                .Where(f => f.Id == mselId)
                .SingleOrDefaultAsync(ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();
            var filename = msel.Name.ToLower().EndsWith(".xlsx") ? msel.Name : msel.Name + ".xlsx";

            // get the MSEL data into a DataTable
            var scenarioEventList = await _context.ScenarioEvents
                .Where(n => n.MselId == mselId)
                .OrderBy(se => se.DeltaSeconds)
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
                if (msel.HeaderRowMetadata != null && msel.HeaderRowMetadata.Length > 0)
                {
                    Double height;
                    double.TryParse(msel.HeaderRowMetadata, out height);
                    headerRow.Height = height;
                    headerRow.CustomHeight = true;
                }
                headerRow.RowIndex = 1;
                // add the cells to the header row
                Columns columns = new Columns();
                foreach (System.Data.DataColumn column in dataTable.Columns)
                {
                    var dataField = await _context.DataFields
                        .Where(df => df.MselId == mselId && df.Name == column.ColumnName)
                        .FirstOrDefaultAsync();
                    Double width;
                    double.TryParse(dataField.ColumnMetadata, out width);
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
                    cell.CellReference = GetCellReference(dataField.DisplayOrder, (int)headerRow.RowIndex.Value);
                    cell.StyleIndex = cellMetadata != null && cellMetadata.Length > 0 ? (UInt32)uniqueStyles[cellMetadata] : 0;
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
                        Double height;
                        double.TryParse(scenarioEventList[i].RowMetadata.Split(",")[0], out height);
                        newRow.Height = height;
                        newRow.CustomHeight = true;
                    }
                    newRow.RowIndex = (uint)(i + 2);  // the header row is RowIndex 1
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
                            var metadataParts = cellMetadata == null ? new string[0] : cellMetadata.Split(",");
                            var dataFieldType = metadataParts.Count() >= 3 ? (DataFieldType)int.Parse(metadataParts[3]) : DataFieldType.String;
                            switch (dataFieldType)
                            {
                                case DataFieldType.DateTime:
                                    cell.DataType = CellValues.Date;
                                    var dateParts = stringValue.Split("/");
                                    if (dateParts.Count() == 1)
                                    {
                                        cell.CellValue = new CellValue(stringValue);
                                    }
                                    else
                                    {
                                        cell.CellValue = new CellValue(dateParts[2] + "-" +
                                            dateParts[0].PadLeft(2, '0') + "-" +
                                            dateParts[1].PadLeft(2, '0') +
                                            "T00:00:00");
                                    }
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
                        cell.CellReference = GetCellReference(dataField.DisplayOrder, (int)newRow.RowIndex.Value);
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
                .OrderBy(se => se.DeltaSeconds)
                .ToListAsync(ct);
            // get the MSEL data into a DataTable
            var dataTable = await GetMselDataAsync(mselId, scenarioEventList, ct);

            return dataTable;
        }

        private async Task<MselEntity> createMselFromXlsxFile(FileForm form, MselEntity msel, CancellationToken ct)
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
                if (msel == null)
                {
                    var userId = _user.GetId();
                    var utcDatTimeNow = DateTime.UtcNow;
                    // create the MSEL entity
                    msel = new MselEntity() {
                        Id = Guid.NewGuid(),
                        Name = uploadItem.FileName.Replace(".xlsx", ""),
                        Description = "Uploaded from " + uploadItem.FileName,
                        Status = ItemStatus.Pending,
                        IsTemplate = false,
                        HeaderRowMetadata = headerRow.Height != null ? headerRow.Height.Value.ToString() : "",
                        CreatedBy = userId,
                        DateCreated = utcDatTimeNow,
                        ModifiedBy = userId,
                        DateModified = utcDatTimeNow,
                        DataFields = new List<DataFieldEntity>()
                    };
                    await _context.Msels.AddAsync(msel, ct);
                }
                else
                {
                   var mselScenarioEvents =  _context.ScenarioEvents
                        .Where(se => se.MselId == msel.Id);
                    _context.ScenarioEvents.RemoveRange(mselScenarioEvents);
                    await _context.SaveChangesAsync(ct);
                }
                // create the data fields
                CreateDataFields(msel, headerRow, workbookPart, columns);
                await _context.SaveChangesAsync(ct);
                // remove the header row from the sheet data before creating the scenario events
                sheetData.RemoveChild<Row>(headerRow);
                // create the sceanrio events and data values
                await CreateScenarioEventsAsync(msel.Id, sheetData, workbookPart, msel.DataFields);
            }
            await _context.SaveChangesAsync(ct);
            return msel;
        }

        private void CreateDataFields(MselEntity msel, Row headerRow, WorkbookPart workbookPart, Columns columns)
        {
            var dataFields = msel.DataFields.ToList();
            var displayOrder = 1;
            var verifyDataFields = msel.DataFields.Count > 0;
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
                    else
                    {
                        currentcellvalue = thecurrentcell.InnerText;
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
                    if (verifyDataFields)
                    {
                        var dataField = msel.DataFields.SingleOrDefault(df => df.Name.Trim() == currentcellvalue.Trim());
                        if (dataField == null)
                        {
                            throw new DataException($"The xlsx file column heading '{currentcellvalue}' does not exist in the current Data Fields.");
                        }
                        else if (dataField.DisplayOrder != displayOrder)
                        {
                            throw new DataException($"The xlsx file column heading '{currentcellvalue}' is not in the same order as in the current Data Fields.");
                        }
                    }
                    else
                    {
                        // create and store the DataField
                        var dataField = new DataFieldEntity() {
                            Id = Guid.NewGuid(),
                            MselId = msel.Id,
                            Name = currentcellvalue.Trim(),
                            DataType = cellDataType,
                            DisplayOrder = displayOrder,
                            IsChosenFromList = false,
                            OnScenarioEventList = true,
                            OnExerciseView = true,
                            CellMetadata = cellColor + "," + cellTint + ",bold," + (int)cellDataType,
                            ColumnMetadata = columnMetadata
                        };
                        msel.DataFields.Add(dataField);
                    }
                }
                displayOrder++;
            }
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

        private async Task CreateScenarioEventsAsync(Guid mselId, SheetData dataRows, WorkbookPart workbookPart, ICollection<DataFieldEntity> dataFields)
        {
            foreach (Row dataRow in dataRows)
            {
                var cellColor = "";
                var cells = dataRow.Elements<Cell>();
                var cellReference = GetCellReference(1, (int)dataRow.RowIndex.Value);
                var cell = cells.FirstOrDefault(c => c.CellReference == cellReference);
                double cellTint = 0.0;
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
                    Fill fill = (Fill)styles.Stylesheet.Fills.ChildElements[(int)cellFormat.FillId.Value];
                    PatternFill patternFill = fill.PatternFill;
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
                }
                var rowMetadata = dataRow.Height != null ? dataRow.Height.Value.ToString() : "";
                rowMetadata = rowMetadata + "," + GetTintedColor(cellColor, cellTint);
                var rowIndex = (int)dataRow.RowIndex.Value - 1;     // header row was index 1
                var scenarioEvent = new ScenarioEventEntity() {
                    Id = Guid.NewGuid(),
                    MselId = mselId,
                    RowIndex = rowIndex,    // rowIndex is no longer used to order the rows, because of parent/child relationships
                    DeltaSeconds = rowIndex * 60,    // value of seconds (1 minute) used to maintain the row order
                    RowMetadata = rowMetadata 
                };
                await _context.ScenarioEvents.AddAsync(scenarioEvent);
                await CreateDataValuesAsync(scenarioEvent, dataRow, workbookPart, dataFields);
            }
        }

        private async Task CreateDataValuesAsync(ScenarioEventEntity scenarioEvent, Row dataRow, WorkbookPart workbookPart, ICollection<DataFieldEntity> dataFields)
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
                        try
                        {
                            currentCellValue = DateTime.FromOADate(int.Parse(currentCellValue)).ToString("M/d/yyyy");
                        }
                        catch (System.Exception)
                        {
                            // value is not a valid date, so just use the value
                        }
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
                Double tint;
                double.TryParse(styleParts[1], out tint);
                ForegroundColor foregroundColor = new ForegroundColor() { Rgb = rgb, Tint = tint };
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

        private string GetTintedColor(string hexColor, double tint)
        {
            var rhex = "";
            var ghex = "";
            var bhex = "";
            if (hexColor.Length == 6)
            {
                rhex = hexColor.Substring(0, 2);
                ghex = hexColor.Substring(2, 2);
                bhex = hexColor.Substring(4, 2);
            }
            else if (hexColor.Length == 8)
            {
                rhex = hexColor.Substring(2, 2);
                ghex = hexColor.Substring(4, 2);
                bhex = hexColor.Substring(6, 2);
            }
            else
            {
                return "";
            }
            var rint = Convert.ToInt32(rhex, 16);
            var gint = Convert.ToInt32(ghex, 16);
            var bint = Convert.ToInt32(bhex, 16);
            var r = rint.ToString();
            var g = gint.ToString();
            var b = bint.ToString();
            return r + "," + g + "," + b;
        }

        public async Task<Tuple<MemoryStream, string>> DownloadJsonAsync(Guid mselId, CancellationToken ct)
        {
            // user must be a Content Developer
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            var msel = await _context.Msels
                .Include(m => m.Cards)
                .Include(m => m.CiteActions)
                .Include(m => m.CiteRoles)
                .Include(m => m.DataFields)
                .Include(m => m.Moves)
                .Include(m => m.Teams)
                .Include(m => m.Organizations)
                .Include(m => m.Pages)
                .Include(m => m.ScenarioEvents)
                .ThenInclude(s => s.DataValues)
                .Include(m => m.UserMselRoles)
                .AsSplitQuery()
                .SingleOrDefaultAsync(m => m.Id == mselId);
            if (msel == null)
            {
                throw new EntityNotFoundException<MselEntity>("MSEL not found " + mselId);
            }

            var mselJson = "";
            var options = new JsonSerializerOptions()
            {
                ReferenceHandler = ReferenceHandler.Preserve
            };
            mselJson = JsonSerializer.Serialize(msel, options);
            // convert string to stream
            byte[] byteArray = Encoding.ASCII.GetBytes(mselJson);
            MemoryStream memoryStream = new MemoryStream(byteArray);
            var filename = msel.Name.ToLower().EndsWith(".json") ? msel.Name : msel.Name + ".json";

            return System.Tuple.Create(memoryStream, filename);
        }

        public async Task<Msel> UploadJsonAsync(FileForm form, CancellationToken ct)
        {
            // user must be a Content Developer
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            var uploadItem = form.ToUpload;
            var mselJson = "";
            using (StreamReader reader = new StreamReader(uploadItem.OpenReadStream()))
            {
                // convert stream to string
                mselJson = reader.ReadToEnd();
            }
            var options = new JsonSerializerOptions()
            {
                ReferenceHandler = ReferenceHandler.Preserve
            };
            var mselEntity = JsonSerializer.Deserialize<MselEntity>(mselJson, options);
            // get the list of team IDs
            var teamsToAdd = mselEntity.Teams.Select(t => t.Id).ToList();
            foreach (var citeRole in mselEntity.CiteRoles)
            {
                var exists = await _context.Teams.AnyAsync(t => t.Id == citeRole.TeamId, ct);
                if (exists)
                {
                    citeRole.Team = null;
                }
                else
                {
                    if (teamsToAdd.Contains((Guid)citeRole.TeamId))
                    {
                        citeRole.Team = null;
                    }
                    else
                    {
                        teamsToAdd.Add((Guid)citeRole.TeamId);
                    }
                }
            }
            foreach (var citeAction in mselEntity.CiteActions)
            {
                var exists = await _context.Teams.AnyAsync(t => t.Id == citeAction.TeamId, ct);
                if (exists)
                {
                    citeAction.Team = null;
                }
                else
                {
                    if (teamsToAdd.Contains((Guid)citeAction.TeamId))
                    {
                        citeAction.Team = null;
                    }
                    else
                    {
                        teamsToAdd.Add((Guid)citeAction.TeamId);
                    }
                }
            }
            // add this msel to the database
            await _context.Msels.AddAsync(mselEntity);
            await _context.SaveChangesAsync(ct);

            return _mapper.Map<Msel>(mselEntity);
        }

        public async Task<ViewModels.Msel> PushIntegrationsAsync(Guid mselId, CancellationToken ct)
        {
            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), mselId, _context)))
                throw new ForbiddenException();
            // get the MSEL and verify data state
            var msel = await _context.Msels
                .Include(m => m.PlayerApplications)
                .ThenInclude(pa => pa.PlayerApplicationTeams)
                .AsSplitQuery()
                .SingleOrDefaultAsync(m => m.Id == mselId);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>($"MSEL {mselId} was not found when attempting to push integrations.");
            if (msel.PlayerViewId != null)
                throw new InvalidOperationException($"MSEL {mselId} is already deployed.");
            // verify that no users are on more than one team
            var userVerificationErrorMessage = await FindDuplicateMselUsersAsync(mselId, ct);
            if (!String.IsNullOrWhiteSpace(userVerificationErrorMessage))
                throw new InvalidOperationException(userVerificationErrorMessage);
            _integrationQueue.Add(new IntegrationInformation{MselId = mselId, PlayerViewId = null});
            
            return _mapper.Map<ViewModels.Msel>(msel); 
        }

        public async Task<ViewModels.Msel> PullIntegrationsAsync(Guid mselId, CancellationToken ct)
        {
            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), mselId, _context)))
                throw new ForbiddenException();
            // get the MSEL and verify data state
            var msel = await _context.Msels.FindAsync(mselId);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>($"MSEL {mselId} was not found when attempting to remove from Player.");
            if (msel.PlayerViewId == null)
                throw new InvalidOperationException($"MSEL {mselId} is not associated to a Player View.");
            // add msel to process queue
            _integrationQueue.Add(new IntegrationInformation{MselId = mselId, PlayerViewId = null});

            return _mapper.Map<ViewModels.Msel>(msel); 
        }

        private async Task<string> FindDuplicateMselUsersAsync(Guid mselId, CancellationToken ct)
        {
            var duplicateResultList = await _context.MselTeams
                .AsNoTracking()
                .Where(mt => mt.MselId == mselId && mt.CiteTeamTypeId != null)
                .SelectMany(mt => mt.Team.TeamUsers)
                .Select(tu => new DuplicateResult {
                    TeamId = tu.TeamId,
                    UserId = tu.UserId,
                    TeamName = tu.Team.ShortName,
                    UserName = tu.User.Name
                })
                .ToListAsync(ct);
            var duplicates = duplicateResultList
                .GroupBy(tu => tu.UserId)
                .Where(x => x.Count() > 1)
                .ToList();
            var explanation = "";
            if (duplicates.Any())
            {
                explanation = "Users can only be on one team.  The following users are on more than one team.  ";
                foreach (var dup in duplicates)
                {
                    var dupTeamUsers = dup.ToList();
                    explanation = explanation + "[" + dupTeamUsers[0].UserName + " is on teams ";
                    foreach (var teamUser in dupTeamUsers)
                    {
                        explanation = explanation + teamUser.TeamName + ", ";
                    }
                    explanation = explanation + "],   ";
                }
            }

            return explanation;
        }

        public async Task<IEnumerable<ViewModels.Msel>> GetMyJoinInvitationMselsAsync(CancellationToken ct)
        {
            var myMsels = (IQueryable<MselEntity>)_context.Msels;
            var myDeployedMselIds = await GetMyDeployedMselIdsAsync(ct);
            // get the IDs for MSELs where user has an invitation
            var currentDateTime = DateTime.UtcNow;
            var userEmail = _user.Claims.SingleOrDefault(c => c.Type == "Email").Value;
            var inviteMselIds = await _context.Invitations
                .Where(i =>
                    !i.WasDeactivated &&
                    i.ExpirationDateTime < currentDateTime &&
                    userEmail.EndsWith(i.EmailDomain)
                )
                .Select(i => i.MselId)
                .ToListAsync(ct);
            // get the actual MSELs
            myMsels = myMsels
                .Where(m =>
                    (m.Status == ItemStatus.Deployed && inviteMselIds.Contains(m.Id)) ||
                    myDeployedMselIds.Contains(m.Id)
                );
            return _mapper.Map<IEnumerable<Msel>>(await myMsels.ToListAsync(ct));;
        }

        public async Task<IEnumerable<ViewModels.Msel>> GetMyLaunchInvitationMselsAsync(CancellationToken ct)
        {
            // get the launchable MSELs where user has an invitation
            var currentDateTime = DateTime.UtcNow;
            var userEmail = _user.Claims.SingleOrDefault(c => c.Type == "Email").Value;
            var myMsels = _context.Invitations
                .Where(i =>
                    !i.WasDeactivated &&
                    i.ExpirationDateTime < currentDateTime &&
                    userEmail.EndsWith(i.EmailDomain) &&
                    i.Msel.IsTemplate
                )
                .Select(i => i.Msel);
            return _mapper.Map<IEnumerable<Msel>>(await myMsels.ToListAsync(ct));;
        }

        /// <summary>
        /// Joins the current user to a MSEL that has already been launched
        /// </summary>
        /// <param name="mselId"></param>
        /// <param name="ct"></param>
        /// <returns>The Player View ID</returns>
        public async Task<Guid> JoinMselAsync(Guid mselId, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(m => m.Id == mselId);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>($"MSEL {mselId} was not found when attempting to join.");

            Guid? playerViewId;
            var myDeployedMselIds = await GetMyDeployedMselIdsAsync(ct);
            if (myDeployedMselIds.Contains(mselId))
            {
                // Note: invitations cannot be used to add a user to multiple teams
                playerViewId = (Guid)msel.PlayerViewId;
            }
            else
            {
                // get MSEL, if the user has an invitation
                var currentDateTime = DateTime.UtcNow;
                var userEmail = _user.Claims.SingleOrDefault(c => c.Type == "Email").Value;
                var joinInformation = await _context.Invitations
                    .Where(i =>
                        !i.WasDeactivated &&
                        i.ExpirationDateTime < currentDateTime &&
                        (i.EmailDomain == null || userEmail.EndsWith(i.EmailDomain)) &&
                        i.MselId == mselId
                    )
                    .Select(i => 
                        new JoinInformation{
                            UserId = _user.GetId(),
                            PlayerViewId = (Guid)i.Msel.PlayerViewId,
                            PlayerTeamId = (Guid)i.Team.PlayerTeamId
                        }
                    )
                    .SingleOrDefaultAsync(ct);
                // add the join data to the join queue
                _joinQueue.Add(joinInformation);
                playerViewId = joinInformation.PlayerViewId;
            }
            return (Guid)playerViewId;
        }

        public async Task<Guid> LaunchMselAsync(Guid mselId, CancellationToken ct)
        {
            // determine if the user has a valid invitation
            var currentDateTime = DateTime.UtcNow;
            var userEmail = _user.Claims.SingleOrDefault(c => c.Type == "Email").Value;
            var invitation = await _context.Invitations
                .Where(i =>
                    !i.WasDeactivated &&
                    i.ExpirationDateTime < currentDateTime &&
                    (i.EmailDomain == null || userEmail.EndsWith(i.EmailDomain)) &&
                    i.MselId == mselId &&
                    i.Msel.IsTemplate
                )
                .SingleOrDefaultAsync(ct);

            if (invitation == null)
                throw new ForbiddenException();

            // clone the template MSEL
            var mselEntity = await privateMselCopyAsync(mselId, invitation.TeamId, ct);
            // create the new player view ID, so that the UI will be able to look for it to be created
            var playerViewId = Guid.NewGuid();
            // add the launch data to the launch queue
            _integrationQueue.Add(new IntegrationInformation{MselId = mselEntity.Id, PlayerViewId = playerViewId});

            return playerViewId;
        }

        private async Task<IEnumerable<Guid>> GetMyDeployedMselIdsAsync(CancellationToken ct)
        {
            // get the IDs for MSELs where user is already on a team in a Player View
            var myViewIds = (await _playerService.GetMyViewsAsync(ct)).Select(v => v.Id).ToList();
            var myDeployedMselIds = await _context.Msels
                .Where(m => m.PlayerViewId != null && myViewIds.Contains((Guid)m.PlayerViewId))
                .Select(umr => umr.Id)
                .ToListAsync(ct);
            return myDeployedMselIds;
        }

    }

    public class DuplicateResult
    {
        public Guid TeamId { get; set; }
        public Guid UserId { get; set; }
        public string TeamName { get; set; }
        public string UserName { get; set; }
    }
}

