// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Net.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Blueprint.Api.Tests.Support;

/// <summary>
/// A host whose <c>IHttpClientFactory</c> answers from <see cref="Siblings"/>, which a test replaces, so
/// the network map routes can be followed out to Caster, Player's VM API, Player and the identity
/// provider. Those routes build their own clients from <c>IHttpClientFactory</c> rather than using the
/// substituted generated clients.
/// </summary>
/// <remarks>
/// The sibling urls have distinct path prefixes, as in <see cref="IntegrationServiceHarness"/>, because
/// <see cref="SiblingApiHandler"/> matches on the path alone.
/// </remarks>
public class TopologyMapAppFactory(DatabaseFixture database) : BlueprintAppFactory(database)
{
    public const string CasterApiUrl = "http://siblings.test/caster/";
    public const string PlayerApiUrl = "http://siblings.test/player/";
    public const string VmApiUrl = "http://siblings.test/vm/";
    public const string VmUiUrl = "http://vm.test/";

    /// <summary>The transport for this test. Replace it at the start of each test.</summary>
    public SiblingApiHandler Siblings { get; set; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.UseSetting("ClientSettings:CasterApiUrl", CasterApiUrl);
        builder.UseSetting("ClientSettings:PlayerApiUrl", PlayerApiUrl);
        builder.UseSetting("ClientSettings:VmApiUrl", VmApiUrl);
        builder.UseSetting("ClientSettings:VmUiUrl", VmUiUrl);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new CurrentSiblings(this));
        });
    }

    private sealed class CurrentSiblings(TopologyMapAppFactory factory) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(factory.Siblings, disposeHandler: false);
    }
}
