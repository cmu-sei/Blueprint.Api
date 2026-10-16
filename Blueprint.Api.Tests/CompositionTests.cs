// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Blueprint.Api.Data;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>What the application composes, rather than what it answers: every service resolves, every
/// controller activates, the serializer carries the converters <c>Startup</c> gave it, and four background
/// workers are asked for.</summary>
public class CompositionTests(DatabaseFixture fixture, CompositionFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<CompositionFactory>
{
    private readonly CompositionFactory _factory = factory;

    /// <summary>
    /// Whether a type is blueprint's own. The sweeps below are about blueprint's composition, not about
    /// whether the framework can resolve its own services - and several of those legitimately cannot be
    /// resolved outside a request.
    /// </summary>
    private static bool IsBlueprints(Type type) =>
        type.Assembly == typeof(Startup).Assembly || type.Assembly == typeof(BlueprintContext).Assembly;

    /// <summary>Every service blueprint registers resolves.</summary>
    [Fact]
    public void EveryServiceTheApplicationRegisters_CanBeResolved()
    {
        var types = _factory.Descriptors
            .Where(x => !x.IsKeyedService)
            .Select(x => x.ServiceType)
            .Where(IsBlueprints)
            .Where(x => !x.IsGenericTypeDefinition)
            .Distinct()
            .ToList();

        using var scope = Factory.Services.CreateScope();
        var failures = new List<string>();

        foreach (var type in types)
        {
            try
            {
                if (scope.ServiceProvider.GetService(type) is null)
                {
                    failures.Add($"{type.Name}: resolved to null");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{type.Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.NotEmpty(types);
        Assert.Empty(failures);
    }

    /// <remarks>
    /// <c>ActivatorUtilities.CreateInstance</c> is what MVC's own controller activator uses, so this is
    /// faithful rather than an approximation - including its one difference from the container, which is
    /// that a constructor parameter resolving to null is an <c>InvalidOperationException</c> here where
    /// the container would inject the null. No controller takes an <see cref="IPrincipal"/>, so nothing
    /// in blueprint trips over that; see <see cref="ThePrincipal_ResolvedOutsideARequest_IsNull"/>.
    /// </remarks>
    [Fact]
    public void EveryController_CanBeActivated()
    {
        var controllers = typeof(Startup).Assembly.GetTypes()
            .Where(x => typeof(ControllerBase).IsAssignableFrom(x) && !x.IsAbstract)
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToList();

        using var scope = Factory.Services.CreateScope();
        var failures = new List<string>();

        foreach (var type in controllers)
        {
            try
            {
                ActivatorUtilities.CreateInstance(scope.ServiceProvider, type);
            }
            catch (Exception ex)
            {
                failures.Add($"{type.Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.NotEmpty(controllers);
        Assert.Empty(failures);
    }

    /// <summary>Two service types are registered twice.</summary>
    [Fact]
    public void TwoServiceTypes_AreRegisteredTwice()
    {
        var duplicates = _factory.Registrations
            .Where(x => !x.IsKeyedService)
            .Where(x => IsBlueprints(x.ServiceType))
            .GroupBy(x => x.ServiceType)
            .Where(x => x.Count() > 1)
            .Select(x => x.Key.Name)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["BlueprintContext", "IInjectTypeService"], duplicates);
    }

    /// <summary>The duplicate registration resolves the same implementation.</summary>
    [Fact]
    public void TheDuplicateRegistration_IsHarmless()
    {
        using var scope = Factory.Services.CreateScope();

        Assert.IsType<InjectTypeService>(scope.ServiceProvider.GetRequiredService<IInjectTypeService>());
    }

    /// <remarks>
    /// The wire format of every response in the API, in one assertion. Order matters: the first converter
    /// that says it can handle a type wins, so this is what decides that an <c>int</c> is written as a
    /// JSON string and a <c>Guid?</c> reads back from <c>""</c>. <c>JsonConverterTests</c> covers what
    /// each of them does; this is what says those three are the ones in use.
    /// </remarks>
    [Fact]
    public void TheMvcSerializer_CarriesTheFourConvertersInOrder()
    {
        var converters = JsonOptions.Converters.Select(x => x.GetType().Name).ToList();

        Assert.Equal(
            [
                "JsonNullableGuidConverter",
                "JsonDoubleConverter",
                "JsonIntegerConverter",
                "JsonStringEnumConverter",
            ],
            converters);
    }

    /// <remarks>
    /// <c>ReferenceHandler.IgnoreCycles</c> rather than <c>Preserve</c>, so a cycle in an entity graph is
    /// written as <c>null</c> and not as a <c>$ref</c> - which is why every response in the suite is
    /// ordinary JSON while the four download routes, which build their own options with <c>Preserve</c>,
    /// are not. Case-insensitive reading is the Web default and the contrast worth naming:
    /// <c>DatabaseInitializationTests</c> shows the seed file is the one place in blueprint that
    /// deserializes without it, and so the one place a camelCase document is silently discarded.
    /// </remarks>
    [Fact]
    public void TheMvcSerializer_WritesCamelCaseAndIgnoresCycles()
    {
        Assert.Same(ReferenceHandler.IgnoreCycles, JsonOptions.ReferenceHandler);
        Assert.Same(JsonNamingPolicy.CamelCase, JsonOptions.PropertyNamingPolicy);
        Assert.True(JsonOptions.PropertyNameCaseInsensitive);
    }

    /// <remarks>
    /// <c>Startup.cs:256</c> registers <c>IPrincipal</c> as
    /// <c>p =&gt; p.GetService&lt;IHttpContextAccessor&gt;()?.HttpContext?.User</c>, so off a request it is
    /// null rather than an error - and a service constructor is handed that null rather than being
    /// refused, because the container injects what a factory returns. Every one of the ~45 services takes
    /// an <c>IPrincipal</c>, so this is what decides that resolving one outside a request succeeds and
    /// then fails later, at the first <c>_user.GetId()</c>. It is why the workers in
    /// <c>IntegrationService</c> and friends build their own scopes and never read <c>_user</c>.
    /// </remarks>
    [Fact]
    public void ThePrincipal_ResolvedOutsideARequest_IsNull()
    {
        using var scope = Factory.Services.CreateScope();

        Assert.Null(scope.ServiceProvider.GetService<IPrincipal>());
    }

    /// <summary>The application registers four background workers of its own, and the harness removes
    /// them.</summary>
    [Fact]
    public void TheApplication_StartsFourBackgroundWorkers()
    {
        var registered = _factory.Registrations
            .Where(x => x.ServiceType == typeof(IHostedService))
            .Where(x => x.ImplementationType is null || IsBlueprints(x.ImplementationType))
            .ToList();

        var named = registered
            .Where(x => x.ImplementationType is not null)
            .Select(x => x.ImplementationType.Name)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var surviving = _factory.Descriptors
            .Where(x => x.ServiceType == typeof(IHostedService))
            .Where(x => x.ImplementationType is not null && IsBlueprints(x.ImplementationType))
            .Select(x => x.ImplementationType.Name)
            .ToList();

        Assert.Equal(4, registered.Count);
        Assert.Equal(["AddApplicationService", "JoinService", "XApiBackgroundService"], named);
        Assert.Empty(surviving);
    }

    /// <summary>The deployment registers a pooled context factory and a scoped context; the harness keeps only
    /// the context.</summary>
    [Fact]
    public void TheApplication_RegistersTheContextTwoWays()
    {
        var registered = _factory.Registrations.Select(x => x.ServiceType).ToList();
        var live = _factory.Descriptors.Select(x => x.ServiceType).ToList();

        Assert.Contains(typeof(BlueprintContext), registered);
        Assert.Contains(typeof(IDbContextFactory<BlueprintContext>), registered);

        Assert.Contains(typeof(BlueprintContext), live);
        Assert.DoesNotContain(typeof(IDbContextFactory<BlueprintContext>), live);
    }
}
