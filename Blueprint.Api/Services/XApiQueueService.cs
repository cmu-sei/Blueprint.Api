// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;

namespace Blueprint.Api.Services
{
    public interface IXApiQueueService
    {
        Task EnqueueAsync(XApiQueuedStatementEntity statement, CancellationToken ct = default);
        Task<List<XApiQueuedStatementEntity>> DequeueAsync(int batchSize = 10, CancellationToken ct = default);
        Task MarkCompletedAsync(Guid statementId, CancellationToken ct = default);
        Task MarkFailedAsync(Guid statementId, string errorMessage, CancellationToken ct = default);
        Task<int> GetQueueDepthAsync(CancellationToken ct = default);
    }

    public class XApiQueueService : IXApiQueueService
    {
        private readonly BlueprintContext _context;
        private readonly ILogger<XApiQueueService> _logger;
        private const int MaxRetryCount = 5;

        public XApiQueueService(
            BlueprintContext context,
            ILogger<XApiQueueService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task EnqueueAsync(XApiQueuedStatementEntity statement, CancellationToken ct = default)
        {
            statement.QueuedAt = DateTime.UtcNow;
            statement.Status = XApiQueueStatus.Pending;
            statement.RetryCount = 0;

            _context.XApiQueuedStatements.Add(statement);
            await _context.SaveChangesAsync(ct);

            _logger.LogDebug("Enqueued xAPI statement {StatementId} for verb {Verb}", statement.Id, statement.Verb);
        }

        public async Task<List<XApiQueuedStatementEntity>> DequeueAsync(int batchSize = 10, CancellationToken ct = default)
        {
            // Get pending statements that haven't exceeded retry count
            var statements = await _context.XApiQueuedStatements
                .Where(s => s.Status == XApiQueueStatus.Pending && s.RetryCount < MaxRetryCount)
                .OrderBy(s => s.QueuedAt)
                .Take(batchSize)
                .ToListAsync(ct);

            // Mark them as processing
            foreach (var statement in statements)
            {
                statement.Status = XApiQueueStatus.Processing;
                statement.LastAttemptAt = DateTime.UtcNow;
                statement.RetryCount++;
            }

            if (statements.Any())
            {
                await _context.SaveChangesAsync(ct);
                _logger.LogInformation("Dequeued {Count} xAPI statements for processing", statements.Count);
            }

            return statements;
        }

        public async Task MarkCompletedAsync(Guid statementId, CancellationToken ct = default)
        {
            var statement = await _context.XApiQueuedStatements.FindAsync(new object[] { statementId }, ct);
            if (statement == null)
            {
                _logger.LogWarning("Attempted to mark non-existent statement {StatementId} as completed", statementId);
                return;
            }

            statement.Status = XApiQueueStatus.Completed;
            await _context.SaveChangesAsync(ct);

            _logger.LogDebug("Marked xAPI statement {StatementId} as completed", statementId);
        }

        public async Task MarkFailedAsync(Guid statementId, string errorMessage, CancellationToken ct = default)
        {
            var statement = await _context.XApiQueuedStatements.FindAsync(new object[] { statementId }, ct);
            if (statement == null)
            {
                _logger.LogWarning("Attempted to mark non-existent statement {StatementId} as failed", statementId);
                return;
            }

            statement.Status = statement.RetryCount >= MaxRetryCount
                ? XApiQueueStatus.Failed
                : XApiQueueStatus.Pending; // Will retry if under limit
            statement.ErrorMessage = errorMessage;

            await _context.SaveChangesAsync(ct);

            if (statement.Status == XApiQueueStatus.Failed)
            {
                _logger.LogError("xAPI statement {StatementId} failed after {RetryCount} attempts: {Error}",
                    statementId, statement.RetryCount, errorMessage);
            }
            else
            {
                _logger.LogWarning("xAPI statement {StatementId} failed attempt {RetryCount}, will retry: {Error}",
                    statementId, statement.RetryCount, errorMessage);
            }
        }

        public async Task<int> GetQueueDepthAsync(CancellationToken ct = default)
        {
            return await _context.XApiQueuedStatements
                .CountAsync(s => s.Status == XApiQueueStatus.Pending || s.Status == XApiQueueStatus.Processing, ct);
        }
    }
}
