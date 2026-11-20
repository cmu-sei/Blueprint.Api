// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Automation;
using Blueprint.Api.Automation.Handlers;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Tests.Support;
using Crucible.Api.Testing;
using Gallery.Api.Client;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Player.Vm.Api;
using Xunit;

namespace Blueprint.Api.Tests.Automation;

/// <summary>The handlers that perform automation actions, driven directly.</summary>
public class AutomationActionHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AutomationActionContext Context(AutomationAction action, Dictionary<string, string> parameters = null,
        Guid? viewId = null, Guid? citeId = null, Guid? galleryId = null, Guid? vmId = null, bool isTest = false, bool dryRun = false) => new()
    {
        MselId = Guid.NewGuid(),
        Action = action,
        Parameters = parameters ?? new(),
        PlayerViewId = viewId,
        CiteEvaluationId = citeId,
        GalleryExhibitId = galleryId,
        VmId = vmId,
        IsTest = isTest,
        DryRun = dryRun,
    };

    // ---------------------------------------------------------------------------------------------
    // http
    // ---------------------------------------------------------------------------------------------

    private static async Task<(HttpActionHandler Handler, StubHttpMessageHandler Http)> Http(Action<AutomationOptions> arrange = null)
    {
        var http = new StubHttpMessageHandler().RefusesUnmatched();
        var options = AutomationTestOptions.Automation(o =>
        {
            o.Http.AllowedHosts = ["*.example"];
            arrange?.Invoke(o);
        });
        var guard = new OutboundRequestGuard(options, AutomationTestOptions.Clients(), new StubHttpClientFactory(http));
        var tokens = Substitute.For<IServiceAccountTokenProvider>();
        var token = await Tokens.Bearer();
        tokens.GetTokenAsync(Arg.Any<CancellationToken>()).Returns(token);
        return (new HttpActionHandler(guard, tokens, options), http);
    }

    [Fact]
    public async Task Http_Post_SendsTheBody_AndReturnsTheResponse()
    {
        var (handler, http) = await Http();
        http.AnswersJson("POST api/things", """{"id":"7"}""");

        var outcome = await handler.ExecuteAsync(Context(AutomationAction.http_post, new()
        {
            ["Url"] = "http://other.example/api/things",
            ["Body"] = """{"name":"x"}""",
        }), Ct);

        Assert.Equal("""{"id":"7"}""", outcome.Output);
        Assert.Null(outcome.Succeeded);
        var sent = Assert.Single(http.Sent);
        Assert.Equal("""{"name":"x"}""", sent.Body);
    }

    /// <summary>Steamfitter dropped a task's headers when no header replacements were configured.</summary>
    [Fact]
    public async Task Http_SendsTheTasksHeaders_WithNoReplacementsConfigured()
    {
        var (handler, http) = await Http();
        http.Answers("GET api/things", HttpStatusCode.OK);

        await handler.ExecuteAsync(Context(AutomationAction.http_get, new()
        {
            ["Url"] = "http://other.example/api/things",
            ["Headers"] = """{"X-Range":"red","Authorization":"Basic abc"}""",
        }), Ct);

        var sent = Assert.Single(http.Sent);
        Assert.Equal("red", sent.Headers["X-Range"]);
        Assert.Equal("Basic abc", sent.Authorization);
    }

    [Fact]
    public async Task Http_ReplacesAHeaderMarker_FromTheReplacements()
    {
        var (handler, http) = await Http(o => o.Http.HeaderReplacements = new() { ["rangeKey"] = "s3cret" });
        http.Answers("GET api/things", HttpStatusCode.OK);

        await handler.ExecuteAsync(Context(AutomationAction.http_get, new()
        {
            ["Url"] = "http://other.example/api/things",
            ["Headers"] = """{"X-Key":"key={rangeKey}"}""",
        }), Ct);

        Assert.Equal("key=s3cret", Assert.Single(http.Sent).Headers["X-Key"]);
    }

    /// <summary>Steamfitter sent its token to any URL a task named.</summary>
    [Theory]
    [InlineData("http://cite.example/api/x", true)]
    [InlineData("http://other.example/api/x", false)]
    public async Task Http_SendsTheServiceToken_OnlyToATokenHost(string url, bool carriesToken)
    {
        var (handler, http) = await Http(o => o.Http.TokenHosts = ["cite.example"]);
        http.Answers("GET api/x", HttpStatusCode.OK);

        await handler.ExecuteAsync(Context(AutomationAction.http_get, new() { ["Url"] = url }), Ct);

        Assert.Equal(carriesToken ? Tokens.Header : null, Assert.Single(http.Sent).Authorization);
    }

    [Fact]
    public async Task Http_KeepsTheTasksOwnAuthorization_ForATokenHost()
    {
        var (handler, http) = await Http(o => o.Http.TokenHosts = ["cite.example"]);
        http.Answers("GET api/x", HttpStatusCode.OK);

        await handler.ExecuteAsync(Context(AutomationAction.http_get, new()
        {
            ["Url"] = "http://cite.example/api/x",
            ["Headers"] = """{"Authorization":"Bearer mine"}""",
        }), Ct);

        Assert.Equal("Bearer mine", Assert.Single(http.Sent).Authorization);
    }

    [Fact]
    public async Task Http_AResponseThatIsNot2xx_FailsTheTask()
    {
        var (handler, http) = await Http();
        http.AnswersJson("DELETE api/x", """{"error":"gone"}""", HttpStatusCode.NotFound);

        var outcome = await handler.ExecuteAsync(Context(AutomationAction.http_delete, new() { ["Url"] = "http://other.example/api/x" }), Ct);

        Assert.False(outcome.Succeeded);
        Assert.StartsWith("404 Not Found", outcome.Output);
        Assert.Contains("gone", outcome.Output);
    }

    [Fact]
    public async Task Http_ToAHostThatIsNotAllowed_SendsNothing()
    {
        var (handler, http) = await Http();

        await Assert.ThrowsAsync<AutomationConfigurationException>(() =>
            handler.ExecuteAsync(Context(AutomationAction.http_get, new() { ["Url"] = "http://elsewhere.test/x" }), Ct));
        Assert.Empty(http.Sent);
    }

    [Fact]
    public async Task Http_KeepsAtMostTheConfiguredResponse()
    {
        var (handler, http) = await Http(o => o.Http.MaxResponseBytes = 5);
        http.AnswersJson("GET api/x", "\"0123456789\"");

        var outcome = await handler.ExecuteAsync(Context(AutomationAction.http_get, new() { ["Url"] = "http://other.example/api/x" }), Ct);

        Assert.Equal("\"0123\n… (response truncated)", outcome.Output);
    }

    [Fact]
    public async Task Http_HeadersThatAreNotAJsonObject_AreRefused()
    {
        var (handler, _) = await Http();

        await Assert.ThrowsAsync<AutomationConfigurationException>(() => handler.ExecuteAsync(Context(AutomationAction.http_get, new()
        {
            ["Url"] = "http://other.example/api/x",
            ["Headers"] = "X-Range: red",
        }), Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // Blueprint's own actions
    // ---------------------------------------------------------------------------------------------

    private static (BlueprintActionHandler Handler, IAutomationClientFactory Clients) Blueprint()
    {
        var clients = Substitute.For<IAutomationClientFactory>();
        return (new BlueprintActionHandler(clients), clients);
    }

    [Fact]
    public async Task PlayerNotification_BroadcastsTheTextToTheRunsView()
    {
        var (handler, clients) = Blueprint();
        var player = Substitute.For<Player.Api.Client.IPlayerApiClient>();
        clients.PlayerAsync(Arg.Any<CancellationToken>()).Returns(player);
        var viewId = Guid.NewGuid();

        var outcome = await handler.ExecuteAsync(Context(AutomationAction.player_notification, new() { ["Text"] = "Move 2 begins" }, viewId: viewId), Ct);

        Assert.True(outcome.Succeeded);
        await player.Received(1).BroadcastToViewAsync(viewId,
            Arg.Is<Player.Api.Client.Notification>(n => n.Text == "Move 2 begins"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlayerNotification_WithNoView_IsRefused()
    {
        var (handler, _) = Blueprint();

        await Assert.ThrowsAsync<AutomationConfigurationException>(() =>
            handler.ExecuteAsync(Context(AutomationAction.player_notification, new() { ["Text"] = "x" }), Ct));
    }

    [Fact]
    public async Task CiteMoveSet_MovesTheMselsEvaluation()
    {
        var (handler, clients) = Blueprint();
        var cite = Substitute.For<Cite.Api.Client.ICiteApiClient>();
        clients.CiteAsync(Arg.Any<CancellationToken>()).Returns(cite);
        var evaluationId = Guid.NewGuid();
        cite.SetEvaluationCurrentMoveAsync(evaluationId, 2, Arg.Any<CancellationToken>())
            .Returns(new Cite.Api.Client.Evaluation { Id = evaluationId, CurrentMoveNumber = 2 });

        var outcome = await handler.ExecuteAsync(Context(AutomationAction.cite_move_set, new() { ["MoveNumber"] = "2" }, citeId: evaluationId), Ct);

        Assert.True(outcome.Succeeded);
        Assert.Contains("move 2", outcome.Output);
    }

    /// <summary>A test of a CITE or Gallery action reports what it would do, and calls nothing.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ATestOfACiteOrGalleryAction_IsADryRun(bool dryRun, bool noEvaluation)
    {
        var (handler, clients) = Blueprint();

        var outcome = await handler.ExecuteAsync(Context(AutomationAction.cite_move_set, new() { ["MoveNumber"] = "2" },
            citeId: noEvaluation ? null : Guid.NewGuid(), isTest: true, dryRun: dryRun), Ct);

        Assert.True(outcome.Succeeded);
        Assert.StartsWith("Dry run:", outcome.Output);
        await clients.DidNotReceive().CiteAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ACiteAction_ForAMselWithNoEvaluation_IsRefusedOutsideATest()
    {
        var (handler, _) = Blueprint();

        await Assert.ThrowsAsync<AutomationConfigurationException>(() =>
            handler.ExecuteAsync(Context(AutomationAction.cite_move_set, new() { ["MoveNumber"] = "2" }), Ct));
    }

    [Fact]
    public async Task CiteSituationUpdate_SetsTheTimeAndDescription()
    {
        var (handler, clients) = Blueprint();
        var cite = Substitute.For<Cite.Api.Client.ICiteApiClient>();
        clients.CiteAsync(Arg.Any<CancellationToken>()).Returns(cite);
        var evaluationId = Guid.NewGuid();
        cite.UpdateEvaluationSituationAsync(evaluationId, Arg.Any<Cite.Api.Client.EvaluationSituation>(), Arg.Any<CancellationToken>())
            .Returns(new Cite.Api.Client.Evaluation { Id = evaluationId });

        await handler.ExecuteAsync(Context(AutomationAction.cite_situation_update, new()
        {
            ["SituationTime"] = "2026-05-25T18:15:00Z",
            ["SituationDescription"] = "<p>Ransom demanded</p>",
        }, citeId: evaluationId), Ct);

        await cite.Received(1).UpdateEvaluationSituationAsync(evaluationId, Arg.Is<Cite.Api.Client.EvaluationSituation>(s =>
            s.SituationTime == new DateTimeOffset(2026, 5, 25, 18, 15, 0, TimeSpan.Zero) && s.SituationDescription == "<p>Ransom demanded</p>"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GalleryMoveInjectSet_FailsWhenTheExhibitDidNotMove()
    {
        var (handler, clients) = Blueprint();
        var gallery = Substitute.For<IGalleryApiClient>();
        clients.GalleryAsync(Arg.Any<CancellationToken>()).Returns(gallery);
        var exhibitId = Guid.NewGuid();
        gallery.SetExhibitMoveAndInjectAsync(exhibitId, 3, 1, Arg.Any<CancellationToken>())
            .Returns(new Exhibit { Id = exhibitId, CurrentMove = 3, CurrentInject = 0 });

        var outcome = await handler.ExecuteAsync(Context(AutomationAction.gallery_move_inject_set,
            new() { ["MoveNumber"] = "3", ["InjectNumber"] = "1" }, galleryId: exhibitId), Ct);

        Assert.False(outcome.Succeeded);
    }

    // ---------------------------------------------------------------------------------------------
    // VMs
    // ---------------------------------------------------------------------------------------------

    private static (VmActionHandler Handler, IPlayerVmApiClient Client) Vm(VmType type, Guid vmId)
    {
        var client = Substitute.For<IPlayerVmApiClient>();
        client.GetVmAsync(vmId, Arg.Any<CancellationToken>()).Returns(new Player.Vm.Api.Vm { Id = vmId, Name = "web-01", Type = type });
        var clients = Substitute.For<IAutomationClientFactory>();
        clients.PlayerVmAsync(Arg.Any<CancellationToken>()).Returns(client);
        var guard = new OutboundRequestGuard(AutomationTestOptions.Automation(), AutomationTestOptions.Clients(), new StubHttpClientFactory(new StubHttpMessageHandler()));
        return (new VmActionHandler(clients, guard, TimeProvider.System), client);
    }

    [Fact]
    public async Task GuestProcessRun_ReturnsTheProcessesOutput()
    {
        var vmId = Guid.NewGuid();
        var (handler, client) = Vm(VmType.Vsphere, vmId);
        client.RunGuestProcessOnVsphereVirtualMachineAsync(vmId, Arg.Any<RunGuestProcessOnVsphereVirtualMachine>(), Arg.Any<CancellationToken>())
            .Returns(new GuestProcessResult { Success = true, ExitCode = 0, Output = "GET / 200" });

        var outcome = await handler.ExecuteAsync(Context(AutomationAction.guest_process_run, new()
        {
            ["Username"] = "Administrator",
            ["Password"] = "p",
            ["CommandText"] = "powershell.exe",
            ["CommandArgs"] = "-Command Get-Content log",
            ["TimeoutSeconds"] = "30",
        }, vmId: vmId), Ct);

        Assert.Equal("GET / 200", outcome.Output);
        await client.Received(1).RunGuestProcessOnVsphereVirtualMachineAsync(vmId, Arg.Is<RunGuestProcessOnVsphereVirtualMachine>(r =>
            r.Username == "Administrator" && r.Password == "p" && r.ProgramPath == "powershell.exe" && r.TimeoutSeconds == 30),
            Arg.Any<CancellationToken>());
    }

    /// <summary>A process that exits non-zero fails the task whatever it printed, as in Steamfitter.</summary>
    [Fact]
    public async Task GuestProcessRun_ThatExitsNonZero_Fails()
    {
        var vmId = Guid.NewGuid();
        var (handler, client) = Vm(VmType.Proxmox, vmId);
        client.RunGuestProcessOnProxmoxVirtualMachineAsync(vmId, Arg.Any<RunGuestProcessOnProxmoxVirtualMachine>(), Arg.Any<CancellationToken>())
            .Returns(new GuestProcessResult { Success = false, ExitCode = 1, Output = "partial", Error = "Cannot find path" });

        var failure = await Assert.ThrowsAsync<AutomationActionFailedException>(() =>
            handler.ExecuteAsync(Context(AutomationAction.guest_process_run, new() { ["CommandText"] = "/bin/false" }, vmId: vmId), Ct));

        Assert.Equal("exit code 1\npartial\nCannot find path", failure.Message);
    }

    [Theory]
    [InlineData(VmType.Vsphere)]
    [InlineData(VmType.Proxmox)]
    public async Task PowerOn_UsesTheVmsProvider(VmType type)
    {
        var vmId = Guid.NewGuid();
        var (handler, client) = Vm(type, vmId);

        await handler.ExecuteAsync(Context(AutomationAction.vm_hw_power_on, vmId: vmId), Ct);

        if (type == VmType.Vsphere)
            await client.Received(1).PowerOnVsphereVirtualMachineAsync(vmId, Arg.Any<CancellationToken>());
        else
            await client.Received(1).PowerOnProxmoxVirtualMachineAsync(vmId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AVmAction_WithNoVm_IsRefused()
    {
        var (handler, _) = Vm(VmType.Vsphere, Guid.NewGuid());

        await Assert.ThrowsAsync<AutomationConfigurationException>(() => handler.ExecuteAsync(Context(AutomationAction.vm_hw_power_off), Ct));
    }

    /// <summary>
    /// An SSH command's variables apply to the command, not to the cd before it, and come after sudo so it passes them on.
    /// </summary>
    [Theory]
    [InlineData(null, "TOKEN=value", false, "TOKEN='value' program")]
    [InlineData("/tmp", "TOKEN=value", false, "cd '/tmp' && TOKEN='value' program")]
    [InlineData("/tmp", "TOKEN=value", true, "cd '/tmp' && sudo TOKEN='value' program")]
    [InlineData("/tmp", null, true, "cd '/tmp' && sudo program")]
    public void AnSshCommand_PutsItsVariablesOnTheCommand(string cwd, string env, bool sudo, string expected)
    {
        Assert.Equal(expected, global::Blueprint.Api.Automation.Handlers.SshActionHandler.BuildShellCommand(cwd, env, sudo, "program"));
    }

    /// <summary>The guest's path is split at its last separator, a Windows guest's backslash too, whatever the API host runs on.</summary>
    [Theory]
    [InlineData("/tmp/notes/readme.txt", "/tmp/notes/", "readme.txt")]
    [InlineData(@"C:\Temp\notes.txt", @"C:\Temp\", "notes.txt")]
    [InlineData("readme.txt", "", "readme.txt")]
    public async Task UploadContent_SplitsThePathIntoItsDirectoryAndFileName(string guestPath, string directory, string fileName)
    {
        var vmId = Guid.NewGuid();
        var (handler, client) = Vm(VmType.Proxmox, vmId);

        await handler.ExecuteAsync(Context(AutomationAction.guest_file_upload_content, new()
        {
            ["GuestFilePath"] = guestPath,
            ["GuestFileContent"] = "hello",
        }, vmId: vmId), Ct);

        await client.Received(1).UploadFileToProxmoxVirtualMachineAsync(vmId, directory,
            Arg.Is<IEnumerable<Player.Vm.Api.FileParameter>>(f => f.Single().FileName == fileName), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheVmsProvider_IsAskedOnce_AcrossActions()
    {
        var vmId = Guid.NewGuid();
        var (handler, client) = Vm(VmType.Vsphere, vmId);

        await handler.ExecuteAsync(Context(AutomationAction.vm_hw_power_on, vmId: vmId), Ct);
        await handler.ExecuteAsync(Context(AutomationAction.vm_hw_power_off, vmId: vmId), Ct);

        await client.Received(1).GetVmAsync(vmId, Arg.Any<CancellationToken>());
    }

    // ---------------------------------------------------------------------------------------------
    // dispatch
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheRunner_HandsAnActionToItsHandler_AndRefusesOneWithNone()
    {
        var handler = Substitute.For<IAutomationActionHandler>();
        handler.Actions.Returns([AutomationAction.send_email]);
        handler.ExecuteAsync(Arg.Any<AutomationActionContext>(), Arg.Any<CancellationToken>()).Returns(new ActionOutcome("sent", true));
        var runner = new AutomationActionRunner([handler]);

        Assert.Equal("sent", (await runner.ExecuteAsync(Context(AutomationAction.send_email), Ct)).Output);
        await Assert.ThrowsAsync<AutomationConfigurationException>(() => runner.ExecuteAsync(Context(AutomationAction.linux_rm), Ct));
    }

    /// <summary>Every action has a handler in the application's composition.</summary>
    [Fact]
    public void EveryAction_HasAHandler()
    {
        var clients = Substitute.For<IAutomationClientFactory>();
        var options = AutomationTestOptions.Automation();
        var guard = new OutboundRequestGuard(options, AutomationTestOptions.Clients(), new StubHttpClientFactory(new StubHttpMessageHandler()));
        IAutomationActionHandler[] handlers =
        [
            new HttpActionHandler(guard, Substitute.For<IServiceAccountTokenProvider>(), options),
            new BlueprintActionHandler(clients),
            new VmActionHandler(clients, guard, TimeProvider.System),
            new SshActionHandler(guard, options, Microsoft.Extensions.Logging.Abstractions.NullLogger<SshActionHandler>.Instance),
            new EmailActionHandler(options),
        ];

        Assert.Equal(Enum.GetValues<AutomationAction>().OrderBy(a => a), handlers.SelectMany(h => h.Actions).OrderBy(a => a));
    }
}
