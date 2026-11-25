// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blueprint.Api.Automation
{
    /// <summary>
    /// Encrypts, once at startup, the task secrets stored before they were encrypted (the http headers the Steamfitter
    /// migration carried over) and those encrypted with a key that is no longer the active one, after a rotation. With
    /// no key configured it only says how many are waiting.
    /// </summary>
    public class AutomationSecretUpgrader : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IAutomationSecretProtector _secrets;
        private readonly ILogger<AutomationSecretUpgrader> _logger;

        public AutomationSecretUpgrader(IServiceScopeFactory scopeFactory, IAutomationSecretProtector secrets, ILogger<AutomationSecretUpgrader> logger)
        {
            _scopeFactory = scopeFactory;
            _secrets = secrets;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BlueprintContext>();
                var count = await UpgradeAsync(db, _secrets, stoppingToken);
                if (count > 0)
                    _logger.LogInformation("Encrypted the secrets of {Count} automation task(s)", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Encrypting automation task secrets failed");
            }
        }

        public async Task<int> UpgradeAsync(BlueprintContext db, IAutomationSecretProtector secrets, CancellationToken ct)
        {
            var tasks = await db.AutomationTasks.Where(t => t.SecretParameters != null).ToListAsync(ct);
            var waiting = tasks.Where(t => secrets.IsConfigured ? secrets.NeedsProtecting(t.SecretParameters) : secrets.HasUnencrypted(t.SecretParameters)).ToList();
            if (waiting.Count == 0)
                return 0;
            if (!secrets.IsConfigured)
            {
                _logger.LogWarning("{Count} automation task(s) hold secrets that are not encrypted: set Automation:SecretKeys", waiting.Count);
                return 0;
            }
            foreach (var task in waiting)
                task.SecretParameters = secrets.Reprotect(task.SecretParameters);
            await db.SaveChangesAsync(ct);
            return waiting.Count;
        }
    }
}
