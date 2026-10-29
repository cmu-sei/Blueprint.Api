// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

using ClientOptions = Blueprint.Api.Infrastructure.Options.ClientOptions;

namespace Blueprint.Api.Tests.Services;

/// <summary>
/// <c>SteamfitterStartService</c> - what starts a pushed MSEL's Steamfitter scenario at the MSEL start time,
/// driven one check at a time through <c>StartDueScenariosAsync</c> rather than through its loop.
/// </summary>
public class SteamfitterStartServiceTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    private const string SteamfitterApiUrl = "http://siblings.test/steamfitter/";

    [Fact]
    public async Task Check_OfAPushedMselWhoseStartTimeHasCome_StartsItsScenario()
    {
        var msel = await SeedDue();
        var handler = Steamfitter("ready");

        var started = await Service(handler).StartDueScenariosAsync(Ct);

        Assert.Equal(1, started);
        Assert.Contains($"steamfitter/api/scenarios/{msel.SteamfitterScenarioId}/start", Starts(handler));
    }

    /// <summary>A MSEL with no duration has no end to be past, so its scenario is started.</summary>
    [Fact]
    public async Task Check_OfAMselWithNoDuration_StartsItsScenario()
    {
        await SeedDue(x => x.DurationSeconds = 0);
        var handler = Steamfitter("ready");

        Assert.Equal(1, await Service(handler).StartDueScenariosAsync(Ct));
    }

    /// <summary>
    /// Nothing is due, so nothing is asked of anyone - not even the identity provider for a token.
    /// </summary>
    [Theory]
    [InlineData("a start time still to come")]
    [InlineData("an exercise already over")]
    [InlineData("a scenario started manually")]
    [InlineData("a scenario started on the push")]
    [InlineData("a MSEL not deployed")]
    [InlineData("a MSEL not using Steamfitter")]
    [InlineData("a MSEL with no scenario")]
    public async Task Check_OfAMselThatIsNotDue_AsksNothing(string label)
    {
        await SeedDue(x =>
        {
            switch (label)
            {
                case "a start time still to come": x.StartTime = DateTime.UtcNow.AddMinutes(5); break;
                case "an exercise already over": x.StartTime = DateTime.UtcNow.AddHours(-2); x.DurationSeconds = 3600; break;
                case "a scenario started manually": x.SteamfitterScenarioStartMode = SteamfitterScenarioStartMode.Manual; break;
                case "a scenario started on the push": x.SteamfitterScenarioStartMode = SteamfitterScenarioStartMode.OnPush; break;
                case "a MSEL not deployed": x.Status = MselItemStatus.Approved; break;
                case "a MSEL not using Steamfitter": x.UseSteamfitter = false; break;
                case "a MSEL with no scenario": x.SteamfitterScenarioId = null; break;
            }
        });
        var handler = Steamfitter("ready");

        Assert.Equal(0, await Service(handler).StartDueScenariosAsync(Ct));
        Assert.Empty(handler.Sent);
    }

    /// <summary>
    /// A facilitator who started the scenario first wins: starting it again would reset its start date. The
    /// scenario is then dealt with, and is not read again.
    /// </summary>
    [Fact]
    public async Task Check_OfAScenarioAlreadyActive_LeavesItAndDoesNotReadItAgain()
    {
        await SeedDue();
        var handler = Steamfitter("active");
        var service = Service(handler);

        Assert.Equal(0, await service.StartDueScenariosAsync(Ct));
        Assert.Equal(0, await service.StartDueScenariosAsync(Ct));

        Assert.Empty(Starts(handler));
        Assert.Single(handler.Sent, x => x.Method == HttpMethod.Get && x.Path.StartsWith("steamfitter/"));
    }

    [Fact]
    public async Task Check_AfterStartingAScenario_DoesNotStartItAgain()
    {
        await SeedDue();
        var handler = Steamfitter("ready");
        var service = Service(handler);

        await service.StartDueScenariosAsync(Ct);
        var second = await service.StartDueScenariosAsync(Ct);

        Assert.Equal(0, second);
        Assert.Single(Starts(handler));
    }

    /// <summary>A scenario Steamfitter would not start is tried again on the next check.</summary>
    [Fact]
    public async Task Check_WhenTheStartFails_TriesAgainNextTime()
    {
        await SeedDue();
        var handler = Steamfitter("ready", first: x => x.AnswersOnce("PUT steamfitter/api/scenarios/*/start", HttpStatusCode.InternalServerError));
        var service = Service(handler);

        Assert.Equal(0, await service.StartDueScenariosAsync(Ct));
        Assert.Equal(1, await service.StartDueScenariosAsync(Ct));

        Assert.Equal(2, Starts(handler).Count());
    }

    /// <summary>One scenario that cannot be started does not keep the others from starting.</summary>
    [Fact]
    public async Task Check_WhenOneStartFails_StartsTheOthers()
    {
        var failing = await SeedDue();
        await SeedDue();
        var handler = Steamfitter("ready", first: x => x.Answers(
            $"PUT steamfitter/api/scenarios/{failing.SteamfitterScenarioId}/start", HttpStatusCode.Forbidden));

        Assert.Equal(1, await Service(handler).StartDueScenariosAsync(Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>A pushed MSEL whose scenario is to start at its start time, which came a minute ago.</summary>
    private async Task<MselEntity> SeedDue(Action<MselEntity> adjust = null)
    {
        var msel = TestData.Msel(status: MselItemStatus.Deployed);
        msel.UseSteamfitter = true;
        msel.SteamfitterScenarioId = Guid.NewGuid();
        msel.SteamfitterScenarioStartMode = SteamfitterScenarioStartMode.AtStartTime;
        msel.StartTime = DateTime.UtcNow.AddMinutes(-1);
        msel.DurationSeconds = 3600;
        adjust?.Invoke(msel);
        await Seed(msel);

        return msel;
    }

    private SteamfitterStartService Service(SiblingApiHandler handler)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Session.CreateContext());
        services.AddSingleton(handler.AsFactory());
        services.AddSingleton(new ResourceOwnerAuthorizationOptions
        {
            Authority = "http://localhost:8080/realms/crucible",
            ClientId = "blueprint-admin",
            UserName = "blueprint-admin",
            Password = string.Empty,
            Scope = "steamfitter"
        });
        var provider = services.BuildServiceProvider();
        var options = Substitute.For<IOptionsMonitor<ClientOptions>>();
        options.CurrentValue.Returns(new ClientOptions { SteamfitterApiUrl = SteamfitterApiUrl });

        return new SteamfitterStartService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            handler.AsFactory(),
            options,
            NullLogger<SteamfitterStartService>.Instance);
    }

    /// <summary>The identity provider, and Steamfitter answering every scenario read with <paramref name="status"/>.</summary>
    private static SiblingApiHandler Steamfitter(string status, Func<SiblingApiHandler, SiblingApiHandler> first = null) =>
        (first is null ? new SiblingApiHandler() : first(new SiblingApiHandler()))
            .AnswersJson(IntegrationServiceHarness.DiscoveryPath, """
                {
                  "issuer": "http://localhost:8080/realms/crucible",
                  "authorization_endpoint": "http://localhost:8080/realms/crucible/protocol/openid-connect/auth",
                  "token_endpoint": "http://localhost:8080/realms/crucible/protocol/openid-connect/token",
                  "jwks_uri": "http://localhost:8080/realms/crucible/protocol/openid-connect/certs",
                  "response_types_supported": ["code"],
                  "subject_types_supported": ["public"],
                  "id_token_signing_alg_values_supported": ["RS256"]
                }
                """)
            .AnswersJson(IntegrationServiceHarness.JwksPath, """{"keys":[]}""")
            .AnswersJson(IntegrationServiceHarness.TokenPath,
                """{"access_token":"abc123","token_type":"Bearer","expires_in":300}""")
            .AnswersJson("GET steamfitter/api/scenarios/*", $$"""{"id":"{{Guid.NewGuid()}}","name":"s","status":"{{status}}"}""")
            .AnswersJson("PUT steamfitter/api/scenarios/*/start", $$"""{"id":"{{Guid.NewGuid()}}","name":"s","status":"active"}""");

    private static System.Collections.Generic.IEnumerable<string> Starts(SiblingApiHandler handler) =>
        handler.Sent.Where(x => x.Method == HttpMethod.Put && x.Path.EndsWith("/start")).Select(x => x.Path);
}
