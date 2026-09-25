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
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// What the application composes, rather than what it answers: every service resolves, every controller
/// activates, the serializer carries the converters <c>Startup</c> gave it, and four background workers
/// are asked for.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint tests prove the composition works for the ~230 routes they drive. This file is for the
/// parts of it nothing drives: a service registered and resolved nowhere, a controller whose constructor
/// gained a dependency the container cannot supply, a duplicate registration. Those are startup failures
/// in a deployment and silence in a test suite.
/// </para>
/// <para>
/// Everything here reads <see cref="CompositionFactory"/>'s two views of the service collection, and the
/// difference between them is load-bearing: <c>Registrations</c> is what <c>Startup</c> left, which is
/// what a deployment runs, and <c>Descriptors</c> is that plus the harness' substitutions, which is what
/// can actually be resolved here. <see cref="TheApplication_RegistersTheContextTwoWays"/> is the canary
/// for that distinction - if the snapshot were taken too late, it is the test that says so.
/// </para>
/// <para>
/// BUG, recorded here rather than tested because nothing can observe it: the provider <c>switch</c> at
/// <c>Startup.cs:72-97</c> has no <c>default</c> arm, and it is what registers the health checks as well
/// as the context. A deployment whose <c>Database:Provider</c> is misspelled therefore starts with no
/// database registered at all and with <c>/api/health/ready</c> answering Healthy, having nothing to
/// check. It cannot be tested from here because the harness replaces the context registration anyway, so
/// a host built with a bogus provider behaves exactly like one built correctly.
/// </para>
/// </remarks>
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

    /// <remarks>
    /// The test the plan called for, and the one that would have caught the duplicate below on the day it
    /// was written. A registration whose implementation asks for something unregistered throws on first
    /// resolution, which for most of these is the first request that happens to need them - so a service
    /// used by one route can break a deployment nobody exercises that route on.
    /// </remarks>
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

    /// <remarks>
    /// <para>
    /// BUG: <c>services.AddScoped&lt;IInjectTypeService, InjectTypeService&gt;();</c> appears verbatim at
    /// both <c>Startup.cs:228</c> and <c>Startup.cs:231</c> - the same line, three apart. It is the only
    /// duplicate blueprint writes for itself, and it is harmless (see below) because both descriptors
    /// name the same implementation.
    /// </para>
    /// <para>
    /// <c>BlueprintContext</c> is the other, and it is not blueprint's mistake: EF's
    /// <c>AddPooledDbContextFactory&lt;TContext&gt;</c> registers the context scoped from a pool lease so
    /// it can be injected directly, and <c>AddEventPublishingDbContextFactory</c> then adds its own
    /// scoped registration - the one that sets <c>ServiceProvider</c> and clears <c>TrackedEntries</c>.
    /// Two scoped factory descriptors, and the container resolves the last, so the extension's wins and
    /// every injected context is the event-publishing one. That is an order dependence rather than a
    /// defect, and this assertion is what would notice if the order ever changed.
    /// </para>
    /// <para>
    /// A list rather than a count, so a third duplicate reddens it rather than passing unnoticed.
    /// </para>
    /// </remarks>
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

    /// <remarks>
    /// Which is why it has never been noticed: the container keeps both descriptors and resolves the last
    /// one, and both name the same implementation, so the only cost is a wasted descriptor. That is worth
    /// pinning rather than assuming - a duplicate naming two *different* implementations would be a
    /// silent last-wins, and is the shape this line makes easy to write.
    /// </remarks>
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

    /// <remarks>
    /// <para>
    /// Four, and the harness removes all four - which is the whole reason the suite can run at all, each
    /// of them being a <c>while(true)</c> that dials the IdP and then a sibling API. The fourth is
    /// <c>IntegrationService</c>, whose hosted registration is a factory lambda with no
    /// <c>ImplementationType</c>; it is therefore invisible to the name filter and only the count sees
    /// it. Adding a fifth worker and forgetting it here means the suite starts dialing something.
    /// </para>
    /// <para>
    /// The framework registers four of its own alongside them - <c>DataProtectionHostedService</c>,
    /// <c>GenericWebHostService</c>, <c>HealthCheckPublisherHostedService</c> and
    /// <c>TelemetryHostedService</c> - so a bare count of <c>IHostedService</c> descriptors is eight and
    /// is a statement about the framework's version rather than about blueprint. Hence the filter.
    /// </para>
    /// <para>
    /// The second half is deliberately narrow for the same reason.
    /// <c>RemoveAll&lt;IHostedService&gt;()</c> does remove all eight, but it cannot be checked by
    /// asserting that none survives: <c>DataProtectionHostedService</c> and
    /// <c>GenericWebHostService</c> are registered again after the test callbacks run, so two always do.
    /// What matters is that none of blueprint's is among them.
    /// </para>
    /// </remarks>
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

    /// <remarks>
    /// The canary for <see cref="CompositionFactory"/>'s ordering assumption, and the reason the two views
    /// exist. <c>AddEventPublishingDbContextFactory</c> registers a pooled
    /// <c>IDbContextFactory&lt;BlueprintContext&gt;</c> *and* a scoped <c>BlueprintContext</c>; the
    /// harness removes both and adds back only the context, per test. So the factory's presence in one
    /// view and absence from the other is exactly the difference between what a deployment runs and what
    /// this suite runs - and if the snapshot were taken after the substitutions, this is the assertion
    /// that fails rather than the three tests above quietly measuring the wrong collection.
    /// </remarks>
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
