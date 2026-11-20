// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Blueprint.Api.Automation;
using Xunit;

namespace Blueprint.Api.Tests.Automation;

/// <summary>Where a task, which runs as Blueprint's service account inside its network, may reach.</summary>
public class OutboundRequestGuardTests
{
    private static OutboundRequestGuard Guard(Action<AutomationOptions> arrange = null) =>
        new(AutomationTestOptions.Automation(arrange), AutomationTestOptions.Clients(), new NoClients());

    /// <summary>With no hosts named, a task may call the Crucible APIs Blueprint is configured to call, and nothing else.</summary>
    [Theory]
    [InlineData("http://cite.example/api/evaluations", true)]
    [InlineData("http://player.example/api/views", true)]
    [InlineData("http://vm.example/api/vms", true)]
    [InlineData("http://elsewhere.example/", false)]
    public void WithNoAllowedHosts_OnlyTheCrucibleApisAreAllowed(string url, bool allowed)
    {
        var guard = Guard();

        if (allowed)
            Assert.Equal(new Uri(url), guard.CheckHttpUrl(url));
        else
            Assert.Throws<AutomationConfigurationException>(() => guard.CheckHttpUrl(url));
    }

    [Theory]
    [InlineData("https://api.range.example/x", true)]
    [InlineData("https://range.example/x", true)]
    [InlineData("https://evilrange.example/x", false)]
    [InlineData("https://cite.example/x", false)]
    public void AWildcardHost_AllowsTheDomainAndItsHosts(string url, bool allowed)
    {
        var guard = Guard(o => o.Http.AllowedHosts = ["*.range.example"]);

        Assert.Equal(allowed, Record.Exception(() => guard.CheckHttpUrl(url)) == null);
    }

    [Theory]
    [InlineData("ftp://cite.example/file")]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://user:pw@cite.example/")]
    [InlineData("/relative")]
    [InlineData("")]
    public void AUrlThatIsNotPlainHttp_IsRefused(string url)
    {
        Assert.Throws<AutomationConfigurationException>(() => Guard().CheckHttpUrl(url));
    }

    /// <summary>Even when allowed by name, a metadata endpoint's address is refused.</summary>
    [Fact]
    public void AnAllowedLinkLocalAddress_IsStillRefused()
    {
        var guard = Guard(o => o.Http.AllowedHosts = ["169.254.169.254"]);

        Assert.Throws<AutomationConfigurationException>(() => guard.CheckHttpUrl("http://169.254.169.254/latest/meta-data"));
    }

    [Theory]
    [InlineData("169.254.169.254", false, false)]
    [InlineData("fe80::1", false, false)]
    [InlineData("fd00:ec2::254", false, false)]
    [InlineData("0.0.0.0", false, false)]
    [InlineData("224.0.0.1", false, false)]
    [InlineData("255.255.255.255", false, false)]
    [InlineData("127.0.0.1", false, false)]
    [InlineData("127.0.0.1", true, true)]
    [InlineData("::1", true, true)]
    [InlineData("::ffff:169.254.169.254", true, false)]
    [InlineData("10.1.2.3", false, true)]
    [InlineData("192.168.1.10", false, true)]
    [InlineData("93.184.216.34", false, true)]
    public void IsAllowedAddress_RefusesLinkLocalAndSpecialAddresses_AndLoopbackUnlessAllowed(string address, bool allowLoopback, bool allowed)
    {
        Assert.Equal(allowed, OutboundRequestGuard.IsAllowedAddress(IPAddress.Parse(address), allowLoopback));
    }

    /// <summary>The connection is refused when the name resolves to a refused address, whatever the URL check saw.</summary>
    [Fact]
    public async Task Connect_ToANameThatResolvesToLoopback_IsRefusedUnlessLoopbackIsAllowed()
    {
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await OutboundRequestGuard.ConnectAsync(new DnsEndPoint("localhost", 9), false, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The guarded client ignores the system proxy: through one, the connect check would see the proxy's address, not
    /// the destination's, and a name resolving to a metadata endpoint would get through.
    /// </summary>
    [Fact]
    public void TheGuardedClient_UsesNoProxy()
    {
        using var handler = OutboundRequestGuard.CreatePrimaryHandler(AutomationTestOptions.Automation());

        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public void TheServiceToken_GoesOnlyToTheTokenHosts()
    {
        var guard = Guard(o =>
        {
            o.Http.AllowedHosts = ["*.example"];
            o.Http.TokenHosts = ["cite.example"];
        });

        Assert.True(guard.SendsServiceToken(new Uri("http://cite.example/api")));
        Assert.False(guard.SendsServiceToken(new Uri("http://gallery.example/api")));
        Assert.False(guard.SendsServiceToken(new Uri("http://other.example/api")));
    }

    /// <summary>
    /// With no token hosts the service token goes nowhere: a task that could send it to a Crucible API could read any
    /// of that API's data as the service account, and show it in a test.
    /// </summary>
    [Fact]
    public void WithNoTokenHosts_TheServiceTokenGoesNowhere()
    {
        var guard = Guard(o => o.Http.AllowedHosts = ["*.example"]);

        Assert.False(guard.SendsServiceToken(new Uri("http://gallery.example/api")));
        Assert.False(guard.SendsServiceToken(new Uri("http://other.example/api")));
    }

    /// <summary>
    /// On a domain the Crucible apps share by path, only Blueprint's own path is kept from the token: the other apps on
    /// the host still get it.
    /// </summary>
    [Theory]
    [InlineData("https://crucible.example/cite/api/evaluations", true)]
    [InlineData("https://crucible.example/blueprint/api/users", false)]
    [InlineData("https://crucible.example/BLUEPRINT/api/users", false)]
    [InlineData("https://crucible.example/blueprint", false)]
    [InlineData("https://crucible.example/blueprintish/api", true)]
    public void OnASharedDomain_OnlyBlueprintsPathIsKeptFromTheToken(string url, bool sent)
    {
        var clients = AutomationTestOptions.Monitor(new Blueprint.Api.Infrastructure.Options.ClientOptions
        {
            BlueprintApiUrl = "https://crucible.example/blueprint/",
            CiteApiUrl = "https://crucible.example/cite/",
        });
        var guard = new OutboundRequestGuard(
            AutomationTestOptions.Automation(o =>
            {
                o.Http.AllowedHosts = ["crucible.example"];
                o.Http.TokenHosts = ["crucible.example"];
            }), clients, new NoClients());

        Assert.Equal(sent, guard.SendsServiceToken(new Uri(url)));
    }

    /// <summary>Blueprint's own API never gets its service token from a task, even when its host is listed.</summary>
    [Theory]
    [InlineData("http://blueprint.example/api/users", false)]
    [InlineData("http://blueprint.example./api/users", false)]
    [InlineData("http://BLUEPRINT.example/api/users", false)]
    [InlineData("http://blueprint.example:8080/api/users", true)]
    public void BlueprintsOwnApi_NeverGetsTheServiceToken(string url, bool sent)
    {
        var guard = Guard(o =>
        {
            o.Http.AllowedHosts = ["*.example"];
            o.Http.TokenHosts = ["blueprint.example"];
        });

        Assert.Equal(sent, guard.SendsServiceToken(new Uri(url)));
    }

    [Fact]
    public void SshTasks_AreOff_UntilAHostIsAllowed()
    {
        Assert.Throws<AutomationConfigurationException>(() => Guard().CheckSshHost("range-host"));
        Guard(o => o.Ssh.AllowedHosts = ["*.range"]).CheckSshHost("web.range");
        Assert.Throws<AutomationConfigurationException>(() => Guard(o => o.Ssh.AllowedHosts = ["*.range"]).CheckSshHost("other"));
    }

    [Fact]
    public void ResolveLocalFile_StaysInsideTheConfiguredDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "bp-files-" + Guid.NewGuid());
        var guard = Guard(o => o.Files.LocalDirectory = root);

        Assert.Equal(Path.Combine(root, "tools", "a.ps1"), guard.ResolveLocalFile("tools/a.ps1"));
        Assert.Throws<AutomationConfigurationException>(() => guard.ResolveLocalFile("../outside.txt"));
        Assert.Throws<AutomationConfigurationException>(() => guard.ResolveLocalFile("/etc/passwd"));
    }

    [Fact]
    public void ResolveLocalFile_IsOff_WithoutADirectory()
    {
        Assert.Throws<AutomationConfigurationException>(() => Guard().ResolveLocalFile("a.txt"));
    }

    private sealed class NoClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No client in these tests.");
    }
}
