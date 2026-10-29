// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Blueprint.Api.Services
{
    /// <summary>
    /// What the AI calls of one request are for. A service sets it before calling a model, and every call it makes
    /// is recorded against that MSEL or draft.
    /// </summary>
    public interface IAiUsageContext
    {
        string Operation { get; }
        Guid? MselId { get; }
        Guid? DraftId { get; }
        void Use(string operation, Guid? mselId = null, Guid? draftId = null);
    }

    public class AiUsageContext : IAiUsageContext
    {
        public string Operation { get; private set; }
        public Guid? MselId { get; private set; }
        public Guid? DraftId { get; private set; }

        public void Use(string operation, Guid? mselId = null, Guid? draftId = null)
        {
            Operation = operation;
            MselId = mselId;
            DraftId = draftId;
        }
    }

    /// <summary>Writes one row per model call, on its own context so it never saves anything else the request has in hand</summary>
    public interface IAiUsageRecorder
    {
        Task RecordAsync(string model, JsonElement message, int images, CancellationToken ct);
    }

    public class AiUsageRecorder : IAiUsageRecorder
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IAiUsageContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly ILogger<AiUsageRecorder> _logger;

        public AiUsageRecorder(IServiceScopeFactory scopeFactory, IAiUsageContext context, IPrincipal user, ILogger<AiUsageRecorder> logger)
        {
            _scopeFactory = scopeFactory;
            _context = context;
            _user = user as ClaimsPrincipal;
            _logger = logger;
        }

        public async Task RecordAsync(string model, JsonElement message, int images, CancellationToken ct)
        {
            var usage = Read(model, message, images);
            usage.Operation = images > 0 ? "photo" : _context.Operation ?? "other";
            usage.MselId = _context.MselId;
            usage.DraftId = _context.DraftId;
            usage.CreatedBy = UserId();
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BlueprintContext>();
                db.AiUsages.Add(usage);
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // the call already happened; losing its record must not lose the planner's draft
                _logger.LogWarning(ex, "Could not record the AI usage of a {Operation} call on {Model}", usage.Operation, model);
            }
        }

        // a call made with no signed-in user, such as a background step, is recorded without one
        private Guid UserId()
        {
            try
            {
                return _user?.GetId() ?? Guid.Empty;
            }
            catch (ArgumentException)
            {
                return Guid.Empty;
            }
        }

        /// <summary>The usage a Messages API answer reports; an image call has no answer and counts one image</summary>
        public static AiUsageEntity Read(string model, JsonElement message, int images)
        {
            var usage = new AiUsageEntity { Id = Guid.NewGuid(), Model = model, Images = images, DateCreated = DateTime.UtcNow };
            if (message.ValueKind != JsonValueKind.Object)
                return usage;
            if (message.TryGetProperty("stop_reason", out var stop) && stop.ValueKind == JsonValueKind.String)
                usage.StopReason = stop.GetString();
            if (message.TryGetProperty("usage", out var counts) && counts.ValueKind == JsonValueKind.Object)
            {
                int Count(string name) => counts.TryGetProperty(name, out var value) && value.TryGetInt32(out var n) ? n : 0;
                usage.InputTokens = Count("input_tokens");
                usage.OutputTokens = Count("output_tokens");
                usage.CacheReadTokens = Count("cache_read_input_tokens");
                usage.CacheWriteTokens = Count("cache_creation_input_tokens");
            }
            return usage;
        }
    }

    public interface IAiUsageService
    {
        Task<AiUsageSummary> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
        Task<IEnumerable<AiUsageSummary>> GetAllAsync(DateTime? since, CancellationToken ct);
        /// <summary>Moves the calls made while drafting onto the MSEL the draft became</summary>
        Task LinkDraftAsync(Guid draftId, Guid mselId, CancellationToken ct);
    }

    public class AiUsageService : IAiUsageService
    {
        private readonly BlueprintContext _context;
        private readonly AiOptions _aiOptions;
        private readonly ClaimsPrincipal _user;

        public AiUsageService(BlueprintContext context, AiOptions aiOptions, IPrincipal user)
        {
            _context = context;
            _aiOptions = aiOptions;
            _user = user as ClaimsPrincipal;
        }

        public async Task<AiUsageSummary> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            // what an exercise cost is for the people who own or run MSELs, not for every participant
            if (!hasSystemPermission && !await MselOwnerRequirement.IsMet(_user.GetId(), mselId, _context))
                throw new ForbiddenException();
            var msel = await _context.Msels.AsNoTracking().Where(m => m.Id == mselId).Select(m => new { m.Id, m.Name }).SingleOrDefaultAsync(ct)
                ?? throw new EntityNotFoundException<MselEntity>("MSEL not found with ID=" + mselId);
            var rows = await _context.AiUsages.AsNoTracking().Where(u => u.MselId == mselId).ToListAsync(ct);
            return Summarize(msel.Id, msel.Name, rows, _aiOptions.Prices);
        }

        public async Task<IEnumerable<AiUsageSummary>> GetAllAsync(DateTime? since, CancellationToken ct)
        {
            var query = _context.AiUsages.AsNoTracking().Where(u => u.MselId != null);
            if (since.HasValue)
                query = query.Where(u => u.DateCreated >= since.Value);
            var rows = await query.ToListAsync(ct);
            var ids = rows.Select(r => r.MselId.Value).Distinct().ToList();
            var names = await _context.Msels.AsNoTracking().Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.Name, ct);
            return rows.GroupBy(r => r.MselId.Value)
                .Select(g => Summarize(g.Key, names.GetValueOrDefault(g.Key), g.ToList(), _aiOptions.Prices))
                .OrderByDescending(s => s.EstimatedCostMicros ?? 0).ThenByDescending(s => s.InputTokens + s.OutputTokens)
                .ToList();
        }

        public async Task LinkDraftAsync(Guid draftId, Guid mselId, CancellationToken ct)
        {
            // only the drafter's own calls, so a draft ID passed in cannot claim anyone else's
            var userId = _user.GetId();
            await _context.AiUsages
                .Where(u => u.DraftId == draftId && u.MselId == null && u.CreatedBy == userId)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.MselId, mselId), ct);
        }

        /// <summary>
        /// A model's price. Environment variable names can't hold a model id's dots or hyphens, so a price keyed
        /// us_anthropic_claude_sonnet_5_5 is the price of us.anthropic.claude-sonnet-5-5.
        /// </summary>
        public static AiPrice PriceFor(IReadOnlyDictionary<string, AiPrice> prices, string model)
        {
            if (string.IsNullOrEmpty(model))
                return null;
            if (prices.TryGetValue(model, out var price))
                return price;
            static string Letters(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray());
            var key = Letters(model);
            return prices.FirstOrDefault(p => Letters(p.Key).Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
        }

        /// <summary>Totals for a MSEL, by operation, with a cost estimate where every model used has a price</summary>
        public static AiUsageSummary Summarize(Guid mselId, string mselName, List<AiUsageEntity> rows, IReadOnlyDictionary<string, AiPrice> prices)
        {
            long? Cost(IEnumerable<AiUsageEntity> calls)
            {
                decimal total = 0;
                foreach (var call in calls)
                {
                    if (PriceFor(prices, call.Model) is not AiPrice price)
                        return null;
                    total += call.InputTokens * price.InputPerMillion / 1_000_000m
                        + call.OutputTokens * price.OutputPerMillion / 1_000_000m
                        + call.CacheReadTokens * price.CacheReadPerMillion / 1_000_000m
                        + call.CacheWriteTokens * price.CacheWritePerMillion / 1_000_000m
                        + call.Images * price.PerImage;
                }
                return (long)Math.Round(total * 1_000_000m);
            }
            AiUsageTotals Totals(string name, List<AiUsageEntity> calls) => new()
            {
                Name = name,
                Calls = calls.Count,
                Refused = calls.Count(c => c.StopReason == "refusal"),
                InputTokens = calls.Sum(c => c.InputTokens),
                OutputTokens = calls.Sum(c => c.OutputTokens),
                CacheReadTokens = calls.Sum(c => c.CacheReadTokens),
                CacheWriteTokens = calls.Sum(c => c.CacheWriteTokens),
                Images = calls.Sum(c => c.Images),
                EstimatedCostMicros = Cost(calls)
            };
            var all = Totals(mselName, rows);
            return new AiUsageSummary
            {
                MselId = mselId,
                MselName = mselName,
                Calls = all.Calls,
                Refused = all.Refused,
                InputTokens = all.InputTokens,
                OutputTokens = all.OutputTokens,
                CacheReadTokens = all.CacheReadTokens,
                CacheWriteTokens = all.CacheWriteTokens,
                Images = all.Images,
                EstimatedCostMicros = all.EstimatedCostMicros,
                Unpriced = rows.Select(r => r.Model).Where(m => m != null && PriceFor(prices, m) == null).Distinct().Order().ToList(),
                FirstCall = rows.Count > 0 ? rows.Min(r => r.DateCreated) : null,
                LastCall = rows.Count > 0 ? rows.Max(r => r.DateCreated) : null,
                ByOperation = rows.GroupBy(r => r.Operation ?? "other").OrderBy(g => g.Key).Select(g => Totals(g.Key, g.ToList())).ToList()
            };
        }
    }
}
