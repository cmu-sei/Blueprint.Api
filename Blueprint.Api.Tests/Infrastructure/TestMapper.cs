// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using AutoMapper;
using AutoMapper.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Blueprint.Api.Tests.Infrastructure;

/// <summary>
/// The application's real AutoMapper configuration, for tests that construct a service directly rather
/// than driving it over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// Built the way <c>Startup</c> builds it - all 38 profiles by assembly scan from
/// <c>typeof(Startup)</c>, plus the global null-source rule below - rather than by registering the
/// profiles a test happens to need. A profile that stops compiling, or one whose destination gains an
/// unmapped member, then fails here too instead of only in the endpoint tests.
/// </para>
/// <para>
/// Shared and static. <c>IMapper</c> is immutable and thread-safe once built, and building it scans the
/// assembly, which is not worth repeating per test.
/// </para>
/// </remarks>
internal static class TestMapper
{
    public static IMapper Value { get; } = Build();

    /// <summary>
    /// Reproduces <c>Startup</c>'s configuration lambda: for every property map whose source is
    /// <c>T?</c> and whose destination is <c>T</c>, a null source leaves the destination value alone
    /// rather than overwriting it with <c>default</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes blueprint's <c>PUT</c> endpoints partial updates on nullable fields, so a
    /// service test that skips it is not testing the same mapper the API uses.
    /// </para>
    /// <para>
    /// The one deviation from production: <c>Blueprint.Api</c>'s own
    /// <c>Infrastructure.Mapping.IgnoreNullSourceValues</c> is declared with no accessibility modifier,
    /// so it is internal and cannot be named from here. <see cref="IgnoreNullSource"/> below is a copy of
    /// its two-line body. Should the production resolver ever grow behaviour, this stops mirroring it
    /// silently - which is why <c>MappingConfigurationTests</c> validates the configuration through the
    /// hosted app, where the real one applies.
    /// </para>
    /// </remarks>
    private static IMapper Build()
    {
        var services = new ServiceCollection();

        services.AddAutoMapper(
            cfg => cfg.Internal().ForAllPropertyMaps(
                pm => pm.SourceType != null && Nullable.GetUnderlyingType(pm.SourceType) == pm.DestinationType,
                (pm, c) => c.MapFrom<object, object, object, object>(
                    new IgnoreNullSource(), pm.SourceMember.Name)),
            typeof(Startup));

        return services.BuildServiceProvider().GetRequiredService<IMapper>();
    }

    private sealed class IgnoreNullSource : IMemberValueResolver<object, object, object, object>
    {
        public object Resolve(
            object source,
            object destination,
            object sourceMember,
            object destinationMember,
            ResolutionContext context) => sourceMember ?? destinationMember;
    }
}
