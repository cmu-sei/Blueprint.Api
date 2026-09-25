// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Blueprint.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Blueprint.Api.Tests.Infrastructure;

/// <summary>
/// A host with xAPI turned on and the <em>real</em> <c>XApiService</c> behind
/// <c>XApiController</c>, so a request can be followed all the way to the row it queues.
/// </summary>
/// <remarks>
/// <para>
/// This is the suite's first <see cref="BlueprintAppFactory"/> subclass, and it exists because both
/// things it changes are decided before a test can reach them.
/// <c>XApiOptions</c> is bound from configuration in <c>Startup.ConfigureServices</c>, so turning the
/// feature on means a <c>UseSetting</c> rather than an arrangement; and the standard factory substitutes
/// <c>IXApiService</c>, which is the right default for the other 40 controllers - none of them should
/// reach an LRS - but leaves nothing to assert here beyond the controller's forwarding. Subclass rather
/// than a flag on <see cref="BlueprintAppFactory"/>: a second host costs about a second, and the
/// alternative is a fixture whose behaviour depends on which test ran first.
/// </para>
/// <para>
/// <c>Endpoint</c> points at <see cref="UnreachableLrs"/> - a port nothing listens on, on loopback - so
/// every outbound call fails immediately with connection refused rather than reaching a real LRS or
/// waiting out a timeout. Only <c>GetStatementsAsync</c> makes one: everything else hands its statement
/// to <c>XApiQueueService</c>, and the background service that would drain the queue is removed with the
/// rest of the hosted services by the base factory.
/// </para>
/// <para>
/// <c>ConfigureTestServices</c> callbacks run in the order they are registered, so calling
/// <c>base.ConfigureWebHost</c> first and replacing afterwards is what makes the real service win. The
/// substitute on <see cref="BlueprintAppFactory.XApi"/> is still there and still reset between tests;
/// nothing resolves it.
/// </para>
/// </remarks>
public class XApiEnabledFactory(DatabaseFixture database) : BlueprintAppFactory(database)
{
    /// <summary>An LRS address nothing is listening on.</summary>
    public const string UnreachableLrs = "http://127.0.0.1:1/xapi";

    /// <summary>
    /// Must end in a slash. <c>XApiService</c> concatenates it bare (<c>ApiUrl + type + "/" + id</c>).
    /// </summary>
    public const string ApiUrl = "https://blueprint.test/api/";

    /// <summary>
    /// Must <em>not</em> end in a slash. <c>XApiService</c> concatenates it with an already-rooted path
    /// (<c>UiUrl + "/msel/" + id</c>). The opposite convention to <see cref="ApiUrl"/>, and nothing in
    /// the application documents or validates either.
    /// </summary>
    public const string UiUrl = "https://blueprint.test";

    public const string Platform = "Blueprint";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // IsConfigured() wants the flag and the username; the background service reads the username
        // alone. Both are set, so the two halves of the feature agree for these tests.
        builder.UseSetting("XApiOptions:Enabled", "true");
        builder.UseSetting("XApiOptions:Username", "lrs-user");
        builder.UseSetting("XApiOptions:Password", "lrs-password");
        builder.UseSetting("XApiOptions:Endpoint", UnreachableLrs);
        builder.UseSetting("XApiOptions:ApiUrl", ApiUrl);
        builder.UseSetting("XApiOptions:UiUrl", UiUrl);
        builder.UseSetting("XApiOptions:Platform", Platform);
        // IssuerUrl stays empty, as appsettings.json ships it, so the actor's account homePage is built
        // from the token's own iss claim - TestAuthHandler.Issuer.

        builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Scoped<IXApiService, XApiService>()));
    }
}
