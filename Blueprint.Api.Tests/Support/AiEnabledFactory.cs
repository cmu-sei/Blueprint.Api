// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Blueprint.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Blueprint.Api.Tests.Support;

/// <summary>
/// A host with AI drafting turned on and <see cref="Model"/> and <see cref="Images"/> answering for Bedrock, so a request can be
/// followed to the prompt it sends and back to what the planner is shown.
/// </summary>
/// <remarks>
/// <c>AiOptions</c> is bound from configuration in <c>Startup.ConfigureServices</c>, so turning it on is a
/// <c>UseSetting</c>, as in <see cref="XApiEnabledFactory"/>. The substitute is not cleared by
/// <c>ResetForTest</c>; the test class clears it.
/// </remarks>
public class AiEnabledFactory(DatabaseFixture database) : BlueprintAppFactory(database)
{
    public IAiModelClient Model { get; } = Substitute.For<IAiModelClient>();

    /// <summary>Answers for the Bedrock image model; not configured unless a test says so</summary>
    public IAiImageClient Images { get; } = Substitute.For<IAiImageClient>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.UseSetting("Ai:Enabled", "true");
        builder.UseSetting("Ai:ModelId", "test-model");
        // a price for the test model, so usage can be costed; the image model has a per-image price
        builder.UseSetting("Ai:Prices:test-model:InputPerMillion", "2");
        builder.UseSetting("Ai:Prices:test-model:OutputPerMillion", "10");
        builder.UseSetting("Ai:Prices:test-image:PerImage", "0.04");

        builder.ConfigureTestServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton(Model));
            services.Replace(ServiceDescriptor.Singleton(Images));
        });
    }
}
