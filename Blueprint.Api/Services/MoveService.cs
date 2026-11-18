// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.ViewModels;
using Blueprint.Api.Infrastructure.Mappings;

namespace Blueprint.Api.Services
{
    public interface IMoveService
    {
        Task<IEnumerable<ViewModels.Move>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.Move> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.Move> CreateAsync(ViewModels.MoveCreate move, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.Move> UpdateAsync(Guid id, ViewModels.MoveEdit move, bool hasSystemPermission, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, bool hasSystemPermission, CancellationToken ct);
    }

    public class MoveService : IMoveService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;

        public MoveService(
            BlueprintContext context,
            IPrincipal user)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
        }

        public async Task<IEnumerable<ViewModels.Move>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            if (!hasSystemPermission && !await MselUserRequirement.IsMet(_user.GetId(), mselId, _context))
                throw new ForbiddenException();

            var moveEntities = await _context.Moves
                .Where(move => move.MselId == mselId)
                .ToListAsync(ct);

            return moveEntities.Select(x => x.ToMove()).ToList();;
        }

        public async Task<ViewModels.Move> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct)
        {
            var item = await _context.Moves.SingleAsync(move => move.Id == id, ct);
            if (item == null)
                throw new EntityNotFoundException<DataValueEntity>("DataValue not found: " + id);

            if (!hasSystemPermission && !await MselUserRequirement.IsMet(_user.GetId(), item.MselId, _context))
                throw new ForbiddenException();

            return item.ToMove();
        }

        public async Task<ViewModels.Move> CreateAsync(ViewModels.MoveCreate move, bool hasSystemPermission, CancellationToken ct)
        {
            // user must be a Content Developer or a MSEL owner
            if (!hasSystemPermission &&
                ! await MselOwnerRequirement.IsMet(_user.GetId(), move.MselId, _context) &&
                ! await MoveEditorRequirement.IsMet(_user.GetId(), move.MselId, _context))
                throw new ForbiddenException();

            await ValidateMoveNumberAsync(move.MselId, null, move.MoveNumber, ct);

            // start a transaction, because we may also update other data fields
            await _context.Database.BeginTransactionAsync(ct);
            var moveEntity = move.ToEntity();
            moveEntity.Id = move.Id != Guid.Empty ? move.Id : Guid.NewGuid();
            moveEntity.CreatedBy = _user.GetId();
            _context.Moves.Add(moveEntity);
            await SaveMoveAsync(moveEntity.MoveNumber, ct);
            // update the MSEL modified info
            await ServiceUtilities.SetMselModifiedAsync(moveEntity.MselId, moveEntity.CreatedBy, DateTime.UtcNow, _context, ct);
            var created = await GetAsync(moveEntity.Id, true, ct);
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);

            return created;
        }

        public async Task<ViewModels.Move> UpdateAsync(Guid id, ViewModels.MoveEdit move, bool hasSystemPermission, CancellationToken ct)
        {
            var moveToUpdate = await _context.Moves.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (moveToUpdate == null)
                throw new EntityNotFoundException<Move>();

            // user must be a Content Developer, or an owner or move editor of the move's MSEL
            if (!hasSystemPermission &&
                !( await MselOwnerRequirement.IsMet(_user.GetId(), moveToUpdate.MselId, _context)) &&
                !( await MoveEditorRequirement.IsMet(_user.GetId(), moveToUpdate.MselId, _context)))
                throw new ForbiddenException();

            if (move.MoveNumber != moveToUpdate.MoveNumber)
                await ValidateMoveNumberAsync(moveToUpdate.MselId, moveToUpdate.Id, move.MoveNumber, ct);

            // start a transaction, because we may also update other data fields
            await _context.Database.BeginTransactionAsync(ct);
            move.ApplyTo(moveToUpdate);
            moveToUpdate.ModifiedBy = _user.GetId();
            _context.Moves.Update(moveToUpdate);
            await SaveMoveAsync(moveToUpdate.MoveNumber, ct);
            // update the MSEL modified info
            await ServiceUtilities.SetMselModifiedAsync(moveToUpdate.MselId, moveToUpdate.ModifiedBy, DateTime.UtcNow, _context, ct);
            var updated = await GetAsync(moveToUpdate.Id, true, ct);
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);

            return updated;
        }

        // the unique index on a move's MSEL and number, which a second move with the same number breaks
        private const string MoveNumberIndex = "IX_moves_msel_id_move_number";

        private static string MoveNumberTaken(int moveNumber) =>
            $"Move {moveNumber} already exists in this MSEL. Each move needs its own number.";

        // each move in a MSEL has its own number, so a number another of its moves has is refused
        private async Task ValidateMoveNumberAsync(Guid mselId, Guid? moveId, int moveNumber, CancellationToken ct)
        {
            if (await _context.Moves.AnyAsync(m => m.MselId == mselId && m.MoveNumber == moveNumber && m.Id != moveId, ct))
                throw new BadRequestException(MoveNumberTaken(moveNumber));
        }

        // a request that took the number after ValidateMoveNumberAsync looked is refused the same way
        private async Task SaveMoveAsync(int moveNumber, CancellationToken ct)
        {
            try
            {
                await _context.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException
                { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: MoveNumberIndex })
            {
                throw new BadRequestException(MoveNumberTaken(moveNumber));
            }
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

            return true;
        }

    }
}

