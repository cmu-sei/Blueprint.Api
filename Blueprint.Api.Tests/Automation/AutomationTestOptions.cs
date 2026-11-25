// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using Blueprint.Api.Automation;
using Blueprint.Api.Infrastructure.Options;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Blueprint.Api.Tests.Automation;

/// <summary>Option monitors over fixed values, for driving the automation classes directly.</summary>
internal static class AutomationTestOptions
{
    /// <summary>A 32 byte key, base64, as Automation:SecretKeys holds them.</summary>
    public static string NewKey() => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    public static IOptionsMonitor<T> Monitor<T>(T value)
    {
        var monitor = Substitute.For<IOptionsMonitor<T>>();
        monitor.CurrentValue.Returns(value);
        return monitor;
    }

    public static IOptionsMonitor<ClientOptions> Clients() => Monitor(new ClientOptions
    {
        CiteApiUrl = "http://cite.example/",
        GalleryApiUrl = "http://gallery.example/",
        PlayerApiUrl = "http://player.example",
        PlayerVmApiUrl = "http://vm.example/",
        CasterApiUrl = "http://caster.example/",
        BlueprintApiUrl = "http://blueprint.example",
    });

    public static IOptionsMonitor<AutomationOptions> Automation(Action<AutomationOptions> arrange = null)
    {
        var options = new AutomationOptions();
        arrange?.Invoke(options);
        return Monitor(options);
    }
}
