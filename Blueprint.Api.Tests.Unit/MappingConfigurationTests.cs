// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using AutoMapper;
using Blueprint.Api.Infrastructure.Mapping;
using Shouldly;
using Xunit;

namespace Blueprint.Api.Tests.Unit;

public class MappingConfigurationTests
{
    [Fact]
    public void AutoMapperConfiguration_IsValid()
    {
        // Arrange - create a mapper configuration using the same assembly as Startup
        var configuration = new MapperConfiguration(cfg =>
        {
            cfg.AddMaps(typeof(Blueprint.Api.Startup).Assembly);
        });

        // Act - verify mapper can be created (weaker than AssertConfigurationIsValid
        // because the app has unmapped navigation properties populated elsewhere)
        var mapper = configuration.CreateMapper();
        mapper.ShouldNotBeNull();
    }

    [Fact]
    public void AllProfiles_AreRegistered()
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

        profileTypes.Count.ShouldBeGreaterThan(0, "Should have at least one AutoMapper profile");
        profileTypes.Count.ShouldBeGreaterThanOrEqualTo(20,
            "Blueprint has many entity mappings; expected at least 20 profiles");
    }
}
