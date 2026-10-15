// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Tests.Support;
using Caster.Api.Client;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

using ClientOptions = Blueprint.Api.Infrastructure.Options.ClientOptions;

namespace Blueprint.Api.Tests.Infrastructure.Extensions;

/// <summary><c>ServiceCollectionExtensions.AddCasterApiClient</c> - the registration the host's
/// <c>BlueprintAppFactory.Caster</c> substitute replaces.</summary>
/// <remarks>
/// <c>CasterService</c> leaves the decision of what a caller may see to Caster, which is sound only while
/// the client it is handed carries the caller's own token. So the registration is tested on its own: a
/// container with the registration, a request context and <see cref="SiblingApiHandler"/> as the transport.
/// </remarks>
public class ServiceCollectionExtensionsTests
{
    private const string CasterApiUrl = "http://caster.example/";

    [Fact]
    public async Task AddCasterApiClient_CallsCasterAtItsUrlWithTheRequestsOwnToken()
    {
        var handler = new SiblingApiHandler().AnswersJson("api/projects", "[]");
        var request = new DefaultHttpContext();
        request.Request.Headers.Authorization = "Bearer the-callers-token";

        await using var provider = Provider(handler, request);
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<ICasterApiClient>().GetAllProjectsAsync(false, Ct);

        var sent = Assert.Single(handler.Sent);
        Assert.StartsWith(CasterApiUrl, sent.Uri);
        Assert.Equal("Bearer the-callers-token", sent.Authorization);
    }

    /// <summary>Outside a request there is no token to forward, and the client carries none.</summary>
    [Fact]
    public async Task AddCasterApiClient_OutsideARequest_SendsNoToken()
    {
        var handler = new SiblingApiHandler().AnswersJson("api/projects", "[]");

        await using var provider = Provider(handler, null);
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<ICasterApiClient>().GetAllProjectsAsync(false, Ct);

        Assert.Null(Assert.Single(handler.Sent).Authorization);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A container holding the production registration, over <paramref name="handler"/>. The caller
    /// disposes it.</summary>
    private static ServiceProvider Provider(SiblingApiHandler handler, HttpContext request)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = request });
        services.AddSingleton(handler.AsFactory());
        services.AddSingleton(new ClientOptions { CasterApiUrl = CasterApiUrl });
        services.AddCasterApiClient();

        return services.BuildServiceProvider();
    }
}
