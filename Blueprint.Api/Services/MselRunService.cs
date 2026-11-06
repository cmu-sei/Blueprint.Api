// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface IMselRunService
    {
        Task<IEnumerable<ViewModels.MselRun>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.MselRun> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct);
    }

    public class MselRunService : IMselRunService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public MselRunService(BlueprintContext context, IPrincipal user, IMapper mapper)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
        }

        public async Task<IEnumerable<ViewModels.MselRun>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            if (!await _context.Msels.AnyAsync(m => m.Id == mselId, ct))
                throw new EntityNotFoundException<MselEntity>($"MSEL {mselId} was not found.");
            await AuthorizeAsync(mselId, hasSystemPermission);

            var runs = await _context.MselRuns
                .AsNoTracking()
                .Where(r => r.MselId == mselId)
                .OrderByDescending(r => r.DateCreated)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<MselRun>>(runs);
        }

        public async Task<ViewModels.MselRun> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct)
        {
            var run = await _context.MselRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
            if (run == null)
                throw new EntityNotFoundException<MselRun>($"Run {id} was not found.");
            await AuthorizeAsync(run.MselId, hasSystemPermission);

            return _mapper.Map<MselRun>(run);
        }

        // the MSEL roles that can open the Assessor View, which reads a run's statements
        private async Task AuthorizeAsync(Guid mselId, bool hasSystemPermission)
        {
            var userId = _user.GetId();
            if (!hasSystemPermission &&
                !await MselOwnerRequirement.IsMet(userId, mselId, _context) &&
                !await MselEditorRequirement.IsMet(userId, mselId, _context) &&
                !await MselApproverRequirement.IsMet(userId, mselId, _context) &&
                !await EvaluatorRequirement.IsMet(userId, mselId, _context))
                throw new ForbiddenException();
        }
    }
}
