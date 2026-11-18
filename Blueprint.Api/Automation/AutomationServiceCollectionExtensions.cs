// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using Blueprint.Api.Automation.Handlers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Blueprint.Api.Automation
{
    public static class AutomationServiceCollectionExtensions
    {
        /// <summary>What runs automation tasks' actions: the catalog, the handlers and their clients.</summary>
        public static IServiceCollection AddAutomationActions(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<AutomationOptions>(configuration.GetSection(AutomationOptions.SectionName));
            services.TryAddSingleton(TimeProvider.System);
            services.AddHttpClient(OutboundRequestGuard.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(sp => OutboundRequestGuard.CreatePrimaryHandler(sp.GetRequiredService<IOptionsMonitor<AutomationOptions>>()));

            services.AddSingleton<IAutomationActionCatalog, AutomationActionCatalog>();
            services.AddSingleton<IAutomationSecretProtector, AutomationSecretProtector>();
            services.AddSingleton<IOutboundRequestGuard, OutboundRequestGuard>();
            services.AddSingleton<IServiceAccountTokenProvider, ServiceAccountTokenProvider>();
            services.AddSingleton<IAutomationClientFactory, AutomationClientFactory>();
            services.AddSingleton<IAutomationVmResolver, AutomationVmResolver>();

            services.AddSingleton<IAutomationActionHandler, HttpActionHandler>();
            services.AddSingleton<IAutomationActionHandler, BlueprintActionHandler>();
            services.AddSingleton<IAutomationActionHandler, VmActionHandler>();
            services.AddSingleton<IAutomationActionHandler, SshActionHandler>();
            services.AddSingleton<IAutomationActionHandler, EmailActionHandler>();
            services.AddSingleton<IAutomationActionRunner, AutomationActionRunner>();
            return services;
        }

        /// <summary>What runs automation tasks: the dispatcher, its scheduler and the executor of a run.</summary>
        public static IServiceCollection AddAutomationEngine(this IServiceCollection services)
        {
            services.AddSingleton<AutomationRunRegistry>();
            services.AddScoped<AutomationScheduler>();
            services.AddScoped<IAutomationRunExecutor, AutomationRunExecutor>();
            services.AddScoped<IAutomationRunControl, AutomationRunControl>();
            services.AddHostedService<AutomationDispatcher>();
            return services;
        }
    }
}
