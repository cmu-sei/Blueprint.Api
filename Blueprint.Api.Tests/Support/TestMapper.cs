// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// App-specific: Startup's AddAutoMapper call scans typeof(Startup) and adds one global convention, the
// null-source rule copied privately below. Built through AddAutoMapper, as Startup builds it, so value
// resolvers the profiles name are constructed the same way.

using System;
using AutoMapper;
using AutoMapper.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Blueprint.Api.Tests.Support;

/// <summary>The application's real AutoMapper configuration, built without starting the application.</summary>
public static class TestMapper
{
    private static readonly Lazy<IMapper> LazyMapper = new(Build);

    /// <summary>The shared configuration, built once for the run.</summary>
    public static MapperConfiguration Configuration => (MapperConfiguration)Mapper.ConfigurationProvider;

    /// <summary>A mapper over <see cref="Configuration"/>. Thread-safe; tests share one.</summary>
    public static IMapper Mapper => LazyMapper.Value;

    private static IMapper Build()
    {
        var services = new ServiceCollection();

        services.AddAutoMapper(
            cfg => cfg.Internal().ForAllPropertyMaps(
                pm => pm.SourceType != null && Nullable.GetUnderlyingType(pm.SourceType) == pm.DestinationType,
                (pm, c) => c.MapFrom<object, object, object, object>(
                    new IgnoreNullSourceValues(), pm.SourceMember.Name)),
            typeof(Startup));

        return services.BuildServiceProvider().GetRequiredService<IMapper>();
    }

    /// <summary>A private copy of the API's internal <c>IgnoreNullSourceValues</c>, guarded by MappingConfigurationTests.</summary>
    private sealed class IgnoreNullSourceValues : IMemberValueResolver<object, object, object, object>
    {
        public object Resolve(
            object source,
            object destination,
            object sourceMember,
            object destinationMember,
            ResolutionContext context) => sourceMember ?? destinationMember;
    }
}
