// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Blueprint.Api.Tests.Infrastructure;

/// <summary>
/// A host that remembers what was registered in it, so a test can ask what the application composes
/// rather than only what it answers.
/// </summary>
/// <remarks>
/// <para>
/// Two views, and the difference between them is the point. <see cref="Registrations"/> is a snapshot of
/// the collection as <c>Startup.ConfigureServices</c> left it - what a deployment runs -
/// and <see cref="Descriptors"/> is the live collection the host was actually built from, which is that
/// plus <see cref="BlueprintAppFactory"/>'s substitutions. So <c>Registrations</c> is the subject of a
/// question about production and <c>Descriptors</c> is the subject of a question about the harness, and
/// <c>CompositionTests.TheApplication_RegistersTheContextTwoWays</c> asserts both halves at once, which is
/// what proves the snapshot really is taken before the substitutions.
/// </para>
/// <para>
/// A subclass rather than a member on <see cref="BlueprintAppFactory"/> for the reason
/// <see cref="XApiEnabledFactory"/> gives: the ordering this depends on is fragile enough to want stating
/// in one place, and forty other test classes have no use for it. <c>ConfigureTestServices</c> callbacks
/// run in the order they are registered, so the snapshot callback goes in <em>before</em>
/// <c>base.ConfigureWebHost</c> - the opposite of <see cref="XApiEnabledFactory"/>, which registers after
/// in order to win.
/// </para>
/// </remarks>
public class CompositionFactory(DatabaseFixture database) : BlueprintAppFactory(database)
{
    private IServiceCollection _live;
    private List<ServiceDescriptor> _snapshot;

    /// <summary>What <c>Startup.ConfigureServices</c> registered, before the harness touched any of it.</summary>
    public IReadOnlyList<ServiceDescriptor> Registrations
    {
        get
        {
            Build();

            return _snapshot;
        }
    }

    /// <summary>The collection the running host was built from - <see cref="Registrations"/> plus the substitutes.</summary>
    public IEnumerable<ServiceDescriptor> Descriptors
    {
        get
        {
            Build();

            return _live;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            _snapshot = [.. services];
            _live = services;
        });

        base.ConfigureWebHost(builder);
    }

    /// <summary>
    /// Resolving anything is what builds the host, and building the host is what runs the callback above.
    /// </summary>
    private void Build()
    {
        _ = Services;

        if (_snapshot is null)
        {
            throw new InvalidOperationException(
                "The host was built without running CompositionFactory's snapshot callback. Check that " +
                "ConfigureWebHost still registers it before calling base.ConfigureWebHost.");
        }
    }
}
