// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
using System;
using Microsoft.EntityFrameworkCore;
namespace Blueprint.Api.Data;
public class BlueprintContextFactory : IDbContextFactory<BlueprintContext>
{
    private readonly IDbContextFactory<BlueprintContext> _pooledFactory;
    private readonly IServiceProvider _serviceProvider;
    public BlueprintContextFactory(
        IDbContextFactory<BlueprintContext> pooledFactory,
        IServiceProvider serviceProvider)
    {
        _pooledFactory = pooledFactory;
        _serviceProvider = serviceProvider;
    }
    public BlueprintContext CreateDbContext()
    {
        var context = _pooledFactory.CreateDbContext();
        // Inject the current scope's ServiceProvider
        context.ServiceProvider = _serviceProvider;
        return context;
    }
}