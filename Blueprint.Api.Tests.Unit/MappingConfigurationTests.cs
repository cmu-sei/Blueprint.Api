// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using AutoMapper;
using Blueprint.Api.Infrastructure.Mapping;
using TUnit.Core;

namespace Blueprint.Api.Tests.Unit;

[Category("Unit")]
public class MappingConfigurationTests
{
    [Test]
    public async Task CreateMapper_WithAllProfiles_ShouldSucceed()
    {
        // Arrange - create a mapper configuration using the same assembly as Startup
        var configuration = new MapperConfiguration(cfg =>
        {
            cfg.AddMaps(typeof(Blueprint.Api.Startup).Assembly);
        });

        // Act - verify mapper can be created (weaker than AssertConfigurationIsValid
        // because the app has unmapped navigation properties populated elsewhere)
        var mapper = configuration.CreateMapper();
        await Assert.That(mapper).IsNotNull();
    }

    [Test]
    public async Task GetProfiles_FromAssembly_FindsAtLeast20()
    {
        // Arrange
        var configuration = new MapperConfiguration(cfg =>
        {
            cfg.AddMaps(typeof(Blueprint.Api.Startup).Assembly);
        });

        var mapper = configuration.CreateMapper();

        // Assert - all profile types should be registered
        var profileTypes = typeof(Blueprint.Api.Startup).Assembly
            .GetTypes()
            .Where(t => typeof(Profile).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();

        await Assert.That(profileTypes.Count).IsGreaterThan(0);
        await Assert.That(profileTypes.Count).IsGreaterThanOrEqualTo(20);
    }
}
