// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

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
using Microsoft.Extensions.DependencyInjection;

namespace Blueprint.Api.Services
{
    public interface IMoveService
    {
        Task<IEnumerable<ViewModels.Move>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.Move> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.Move> CreateAsync(ViewModels.Move move, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.Move> UpdateAsync(Guid id, ViewModels.Move move, bool hasSystemPermission, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, bool hasSystemPermission, CancellationToken ct);
    }

    public class MoveService : IMoveService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public MoveService(
            BlueprintContext context,
            IPrincipal user,
            IMapper mapper)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
        }

        private async Task PublishTransactionEventsAsync(CancellationToken ct)
        {
            if (_context.EntityEvents.Count > 0 && _context.ServiceProvider != null)
            {
                var mediator = _context.ServiceProvider.GetRequiredService<MediatR.IMediator>();
                foreach (var evt in _context.EntityEvents.Cast<MediatR.INotification>())
                {
                    await mediator.Publish(evt, ct);
                }
                _context.EntityEvents.Clear();
            }
            _context.ClearPreservedEntries();
        }

        public async Task<IEnumerable<ViewModels.Move>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            if (!hasSystemPermission && !await MselUserRequirement.IsMet(_user.GetId(), mselId, _context))
                throw new ForbiddenException();

            var moveEntities = await _context.Moves
                .Where(move => move.MselId == mselId)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<Move>>(moveEntities).ToList();;
        }

        public async Task<ViewModels.Move> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct)
        {
            var item = await _context.Moves.SingleAsync(move => move.Id == id, ct);
            if (item == null)
                throw new EntityNotFoundException<DataValueEntity>("DataValue not found: " + id);

            if (!hasSystemPermission && !await MselUserRequirement.IsMet(_user.GetId(), item.MselId, _context))
                throw new ForbiddenException();

            return _mapper.Map<Move>(item);
        }

        public async Task<ViewModels.Move> CreateAsync(ViewModels.Move move, bool hasSystemPermission, CancellationToken ct)
        {
            // user must be a Content Developer or a MSEL owner
            if (!hasSystemPermission &&
                ! await MselOwnerRequirement.IsMet(_user.GetId(), move.MselId, _context) &&
                ! await MoveEditorRequirement.IsMet(_user.GetId(), move.MselId, _context))
                throw new ForbiddenException();

            // start a transaction, because we may also update other data fields
            await _context.Database.BeginTransactionAsync(ct);
            move.Id = move.Id != Guid.Empty ? move.Id : Guid.NewGuid();
            move.CreatedBy = _user.GetId();
            var moveEntity = _mapper.Map<MoveEntity>(move);
            _context.Moves.Add(moveEntity);
            await _context.SaveChangesAsync(ct);
            // update the MSEL modified info
            await ServiceUtilities.SetMselModifiedAsync(move.MselId, move.CreatedBy, DateTime.UtcNow, _context, ct);
            move = await GetAsync(moveEntity.Id, true, ct);
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);
            await PublishTransactionEventsAsync(ct);

            return move;
        }

        public async Task<ViewModels.Move> UpdateAsync(Guid id, ViewModels.Move move, bool hasSystemPermission, CancellationToken ct)
        {
            // user must be a Content Developer or a MSEL owner
            if (!hasSystemPermission &&
                !( await MselOwnerRequirement.IsMet(_user.GetId(), move.MselId, _context)) &&
                !( await MoveEditorRequirement.IsMet(_user.GetId(), move.MselId, _context)))
                throw new ForbiddenException();

            var moveToUpdate = await _context.Moves.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (moveToUpdate == null)
                throw new EntityNotFoundException<Move>();

            // start a transaction, because we may also update other data fields
            await _context.Database.BeginTransactionAsync(ct);
            move.ModifiedBy = _user.GetId();
            _mapper.Map(move, moveToUpdate);
            _context.Moves.Update(moveToUpdate);
            await _context.SaveChangesAsync(ct);
            // update the MSEL modified info
            await ServiceUtilities.SetMselModifiedAsync(moveToUpdate.MselId, moveToUpdate.ModifiedBy, DateTime.UtcNow, _context, ct);
            move = await GetAsync(moveToUpdate.Id, true, ct);
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);
            await PublishTransactionEventsAsync(ct);

            return move;
        }

        public async Task<bool> DeleteAsync(Guid id, bool hasSystemPermission, CancellationToken ct)
        {
            var moveToDelete = await _context.Moves.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (moveToDelete == null)
                throw new EntityNotFoundException<Move>();

            // user must be a Content Developer or a MSEL owner
            if (!hasSystemPermission &&
                !( await MselOwnerRequirement.IsMet(_user.GetId(), moveToDelete.MselId, _context)) &&
                !( await MoveEditorRequirement.IsMet(_user.GetId(), moveToDelete.MselId, _context)))
                throw new ForbiddenException();

            // start a transaction, because we may also update other data fields
            await _context.Database.BeginTransactionAsync();
            _context.Moves.Remove(moveToDelete);
            await _context.SaveChangesAsync(ct);
            // update the MSEL modified info
            await ServiceUtilities.SetMselModifiedAsync(moveToDelete.MselId, _user.GetId(), DateTime.UtcNow, _context, ct);
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);
            await PublishTransactionEventsAsync(ct);

            return true;
        }

    }
}

