// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Blueprint.Api.Automation;
using Blueprint.Api.Automation.Handlers;
using Blueprint.Api.Data.Enumerations;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Blueprint.Api.Tests.Automation;

/// <summary>
/// The email and SSH actions against real servers in containers: a mail catcher that offers no STARTTLS, and an
/// OpenSSH server that takes one key.
/// </summary>
public class AutomationServerHandlerTests : IAsyncLifetime
{
    private static System.Threading.CancellationToken Ct => TestContext.Current.CancellationToken;

    private IContainer _mail;
    private IContainer _authMail;
    private IContainer _ssh;
    private string _privateKey;

    public async ValueTask InitializeAsync()
    {
        _mail = new ContainerBuilder("axllent/mailpit:v1.27")
            .WithPortBinding(1025, true)
            .WithPortBinding(8025, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8025).ForPath("/readyz")))
            .Build();

        // one that takes mail only after a login
        _authMail = new ContainerBuilder("axllent/mailpit:v1.27")
            .WithEnvironment("MP_SMTP_AUTH", "white-cell:s3cret")
            .WithEnvironment("MP_SMTP_AUTH_ALLOW_INSECURE", "true")
            .WithPortBinding(1025, true)
            .WithPortBinding(8025, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8025).ForPath("/readyz")))
            .Build();

        using var rsa = RSA.Create(2048);
        _privateKey = rsa.ExportRSAPrivateKeyPem();
        _ssh = new ContainerBuilder("linuxserver/openssh-server:latest")
            .WithEnvironment("PUID", "1000")
            .WithEnvironment("PGID", "1000")
            .WithEnvironment("USER_NAME", "blue")
            .WithEnvironment("PUBLIC_KEY", OpenSshPublicKey(rsa))
            .WithPortBinding(2222, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("ls.io-init\\] done"))
            .Build();

        await Task.WhenAll(_mail.StartAsync(Ct), _authMail.StartAsync(Ct), _ssh.StartAsync(Ct));
    }

    public async ValueTask DisposeAsync()
    {
        await _mail.DisposeAsync();
        await _authMail.DisposeAsync();
        await _ssh.DisposeAsync();
    }

    private EmailActionHandler Email(bool useStartTls) => new(AutomationTestOptions.Automation(o =>
    {
        o.Email.SmtpHost = _mail.Hostname;
        o.Email.SmtpPort = _mail.GetMappedPublicPort(1025);
        o.Email.UseStartTls = useStartTls;
        o.Email.DefaultFromAddress = "white-cell@example.test";
    }));

    private static AutomationActionContext Context(AutomationAction action, Dictionary<string, string> parameters) => new()
    {
        MselId = Guid.NewGuid(),
        Action = action,
        Parameters = parameters,
    };

    private EmailActionHandler AuthEmail(string password) => new(AutomationTestOptions.Automation(o =>
    {
        o.Email.SmtpHost = _authMail.Hostname;
        o.Email.SmtpPort = _authMail.GetMappedPublicPort(1025);
        o.Email.UseStartTls = false;
        o.Email.SmtpUsername = "white-cell";
        o.Email.SmtpPassword = password;
        o.Email.DefaultFromAddress = "white-cell@example.test";
    }));

    private async Task<List<string>> MailSubjects(IContainer server = null)
    {
        server ??= _mail;
        using var http = new HttpClient { BaseAddress = new Uri($"http://{server.Hostname}:{server.GetMappedPublicPort(8025)}/") };
        using var document = JsonDocument.Parse(await http.GetStringAsync("api/v1/messages", Ct));
        return document.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("Subject").GetString()).ToList();
    }

    /// <summary>An email task delivers its message, with its subject, to the mail server.</summary>
    [Fact]
    public async Task AnEmailTask_DeliversItsMessage()
    {
        var outcome = await Email(useStartTls: false).ExecuteAsync(Context(AutomationAction.send_email, new()
        {
            ["EmailTo"] = "blue@example.test",
            ["Subject"] = "Move 2 begins",
            ["Message"] = "Report in.",
        }), Ct);

        Assert.True(outcome.Succeeded);
        Assert.Equal("Sent to blue@example.test.", outcome.Output);
        Assert.Contains("Move 2 begins", await MailSubjects());
    }

    /// <summary>With STARTTLS required, a server that does not offer it is refused before anything is sent.</summary>
    [Fact]
    public async Task AnEmailTask_RequiringStartTls_RefusesAServerWithoutIt()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => Email(useStartTls: true).ExecuteAsync(Context(AutomationAction.send_email, new()
        {
            ["EmailTo"] = "blue@example.test",
            ["Subject"] = "Never sent",
            ["Message"] = "x",
        }), Ct));

        Assert.DoesNotContain("Never sent", await MailSubjects());
    }

    /// <summary>With a user name set, the task logs in to the mail server, and a password it refuses sends nothing.</summary>
    [Fact]
    public async Task AnEmailTask_LogsInToAServerThatRequiresIt()
    {
        Dictionary<string, string> Message(string subject) => new()
        {
            ["EmailTo"] = "blue@example.test",
            ["Subject"] = subject,
            ["Message"] = "x",
        };

        var outcome = await AuthEmail("s3cret").ExecuteAsync(Context(AutomationAction.send_email, Message("Logged in")), Ct);
        await Assert.ThrowsAnyAsync<Exception>(() => AuthEmail("wrong").ExecuteAsync(Context(AutomationAction.send_email, Message("Refused")), Ct));

        Assert.True(outcome.Succeeded);
        var subjects = await MailSubjects(_authMail);
        Assert.Contains("Logged in", subjects);
        Assert.DoesNotContain("Refused", subjects);
    }

    private SshActionHandler Ssh() => new(
        new OutboundRequestGuard(AutomationTestOptions.Automation(o => o.Ssh.AllowedHosts = [_ssh.Hostname]), AutomationTestOptions.Clients(), new NoHttpClients()),
        AutomationTestOptions.Automation(o => o.Ssh.CommandTimeoutSeconds = 30),
        NullLogger<SshActionHandler>.Instance);

    private Dictionary<string, string> SshParameters(Dictionary<string, string> more) => new(more)
    {
        ["Hosts"] = _ssh.Hostname,
        ["Port"] = _ssh.GetMappedPublicPort(2222).ToString(),
        ["Username"] = "blue",
        ["PrivateKey"] = _privateKey,
    };

    /// <summary>An SSH task runs its command with its key, in its directory, with its variables, and returns the output.</summary>
    [Fact]
    public async Task AnSshTask_RunsItsCommand_InItsDirectory_WithItsVariables()
    {
        var outcome = await Ssh().ExecuteAsync(Context(AutomationAction.core_remote, SshParameters(new()
        {
            ["Cwd"] = "/tmp",
            ["Env"] = "TEAM=blue",
            // the variables are the program's environment: the shell expands the command line before they are set
            ["Cmd"] = "sh -c 'echo \"$TEAM in $(pwd)\"'",
        })), Ct);

        Assert.Null(outcome.Succeeded);
        Assert.Contains("blue in /tmp", outcome.Output);
    }

    /// <summary>A command that exits non-zero fails the task, with its exit status in the output.</summary>
    [Fact]
    public async Task AnSshTask_WhoseCommandFails_Fails()
    {
        var outcome = await Ssh().ExecuteAsync(Context(AutomationAction.core_remote, SshParameters(new() { ["Cmd"] = "exit 3" })), Ct);

        Assert.False(outcome.Succeeded);
        Assert.Contains("(exit 3)", outcome.Output);
    }

    /// <summary>A key the server does not take fails the task with the server's refusal; nothing runs.</summary>
    [Fact]
    public async Task AnSshTask_WithAKeyTheServerRefuses_Fails()
    {
        using var other = RSA.Create(2048);
        var parameters = SshParameters(new() { ["Cmd"] = "echo ran" });
        parameters["PrivateKey"] = other.ExportRSAPrivateKeyPem();

        var outcome = await Ssh().ExecuteAsync(Context(AutomationAction.core_remote, parameters), Ct);

        Assert.False(outcome.Succeeded);
        Assert.Contains("(error)", outcome.Output);
        Assert.DoesNotContain("ran", outcome.Output.Replace("(error)", ""));
    }

    /// <summary>The key's public half as an OpenSSH authorized_keys line.</summary>
    private static string OpenSshPublicKey(RSA rsa)
    {
        var parameters = rsa.ExportParameters(false);
        var blob = new List<byte>();
        void Add(byte[] bytes)
        {
            var length = BitConverter.GetBytes(bytes.Length);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(length);
            blob.AddRange(length);
            blob.AddRange(bytes);
        }
        // an mpint: big-endian, with a leading zero when the top bit is set
        byte[] MpInt(byte[] magnitude) => magnitude[0] >= 0x80 ? new byte[] { 0 }.Concat(magnitude).ToArray() : magnitude;
        Add(Encoding.ASCII.GetBytes("ssh-rsa"));
        Add(MpInt(parameters.Exponent));
        Add(MpInt(parameters.Modulus));
        return "ssh-rsa " + Convert.ToBase64String(blob.ToArray()) + " blueprint-test";
    }

    private sealed class NoHttpClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("An SSH task makes no http request.");
    }
}
