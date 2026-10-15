// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Xunit;

namespace Blueprint.Api.Tests.Infrastructure.Extensions;

/// <summary>
/// <c>IntegrationCasterExtensions</c> - the workspace name and variables file blueprint hands Caster,
/// and the run polling behind a push and a pull.
/// </summary>
/// <remarks>
/// No database and no host: <see cref="SiblingApiHandler"/> stands in for caster.api while the generated
/// <c>CasterApiClient</c> builds every route for real. The poll interval is zero so the loops run flat out.
/// </remarks>
public class IntegrationCasterExtensionsTests
{
    private static readonly Guid WorkspaceId = Guid.NewGuid();
    private static readonly Guid RunId = Guid.NewGuid();
    private static readonly Guid ViewId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly (string Name, Guid Id, string Variable)[] NoTeams = [];

    private static readonly ClientOptions Options = new()
    {
        CasterCheckIntervalSeconds = 0,
        CasterPlanMaxWaitMinutes = 1,
        CasterApplyMaxWaitMinutes = 1,
        CasterDestroyMaxWaitMinutes = 1
    };

    // --- names ---

    /// <remarks>
    /// Caster's job mode copies the workspace name into a Kubernetes label, which allows 63 characters with
    /// alphanumeric ends, and a MSEL name can hold anything.
    /// </remarks>
    [Theory]
    [InlineData("Official Project Lagoon TTX - Admin User")]
    [InlineData("Équipe, Week #1 🚀")]
    [InlineData("")]
    public void GetWorkspaceName_IgnoresTheMselNameAndIsAlwaysValid(string mselName)
    {
        var msel = TestData.Msel();
        msel.Name = mselName;

        var name = IntegrationCasterExtensions.GetWorkspaceName(msel);

        Assert.Equal($"blueprint-{msel.Id}", name);
        Assert.Matches("^[a-z0-9]([a-z0-9_.-]{0,61}[a-z0-9])?$", name);
    }

    // --- variables file ---

    [Fact]
    public void GetVarsFileContent_MatchesAlloy()
    {
        var blueTeam = Guid.NewGuid();
        var redTeam = Guid.NewGuid();

        var content = IntegrationCasterExtensions.GetVarsFileContent(
            ViewId, UserId, "Pat Planner", [("Blue Team", blueTeam, null), ("Red (Opfor) #2, West", redTeam, null)]);

        Assert.Equal(
            $"exercise_id = \"{ViewId}\"\r\n" +
            $"view_id = \"{ViewId}\"\r\n" +
            $"user_id = \"{UserId}\"\r\n" +
            "username = \"Pat Planner\"\r\n" +
            $"blue_team = \"{blueTeam}\"\r\n" +
            $"red_opfor_2_west = \"{redTeam}\"\r\n",
            content);
    }

    [Theory]
    [InlineData("Blue Team", "blue_team")]
    [InlineData("Équipe Bleue", "équipe_bleue")]
    [InlineData("team-1_a", "team-1_a")]
    public void CleanName_LeavesEveryNameAlloyAcceptedAsItWas(string teamName, string variable)
    {
        Assert.Equal(variable, IntegrationCasterExtensions.CleanName(teamName));
    }

    [Fact]
    public void CleanName_IgnoresTheHostCulture()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

            Assert.Equal("intel_team", IntegrationCasterExtensions.CleanName("INTEL Team"));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void GetVarsFileContent_WritesOnlyValidIdentifiers()
    {
        var squad = Guid.NewGuid();

        var content = IntegrationCasterExtensions.GetVarsFileContent(
            ViewId, UserId, "u", [("2nd Squad", squad, null), ("***", Guid.NewGuid(), null)]);

        var teamLines = content.Split("\r\n").Skip(4).Where(l => l.Length > 0).ToList();
        Assert.Equal([$"_2nd_squad = \"{squad}\""], teamLines);
    }

    /// <remarks>
    /// Dropping one of the two would deploy without that team's resources and report success.
    /// </remarks>
    [Fact]
    public void GetVarsFileContent_RefusesTeamsThatShareAVariable()
    {
        var ex = Assert.Throws<Exception>(() => IntegrationCasterExtensions.GetVarsFileContent(
            ViewId, UserId, "u", [("Blue Team", Guid.NewGuid(), null), ("Blue-Team", Guid.NewGuid(), null), ("blue team", Guid.NewGuid(), null)]));

        Assert.Contains("\"Blue Team\" and \"blue team\" all become \"blue_team\"", ex.Message);
    }

    [Fact]
    public void GetVarsFileContent_EscapesTheUserName()
    {
        var content = IntegrationCasterExtensions.GetVarsFileContent(ViewId, UserId, "a \"b\" \\ ${c}", NoTeams);

        Assert.Contains("username = \"a \\\"b\\\" \\\\ $${c}\"\r\n", content);
    }

    [Fact]
    public void GetVarsFileContent_ToleratesAMissingUserName()
    {
        var content = IntegrationCasterExtensions.GetVarsFileContent(ViewId, UserId, null, NoTeams);

        Assert.Contains("username = \"\"\r\n", content);
    }

    // --- team mapping ---

    [Fact]
    public void GetVarsFileContent_WritesAMappedTeamUnderItsTopologyTeam()
    {
        var control = Guid.NewGuid();
        var blue = Guid.NewGuid();

        var content = IntegrationCasterExtensions.GetVarsFileContent(
            ViewId, UserId, "u", [("Exercise Control", control, "admin"), ("Blue Team", blue, null)]);

        var teamLines = content.Split("\r\n").Skip(4).Where(l => l.Length > 0).ToList();
        Assert.Equal([$"admin = \"{control}\"", $"blue_team = \"{blue}\""], teamLines);
    }

    [Fact]
    public void GetVarsFileContent_RefusesTwoTeamsMappedToOneTopologyTeam()
    {
        var ex = Assert.Throws<Exception>(() => IntegrationCasterExtensions.GetVarsFileContent(
            ViewId, UserId, "u", [("Exercise Control", Guid.NewGuid(), "admin"), ("White Cell", Guid.NewGuid(), "admin")]));

        Assert.Contains("\"Exercise Control\" and \"White Cell\" all become \"admin\"", ex.Message);
    }

    /// <remarks>
    /// Blueprint creates the view with Player's own Admin team, which becomes "admin" by name: the
    /// variable a template such as Basic Topology declares for it.
    /// </remarks>
    [Fact]
    public void GetVarsFileContent_AMappingReplacesATeamThatOnlyHasTheVariableByName()
    {
        var control = Guid.NewGuid();

        var content = IntegrationCasterExtensions.GetVarsFileContent(
            ViewId, UserId, "u", [("Admin", Guid.NewGuid(), null), ("Exercise Control", control, "admin")]);

        var teamLines = content.Split("\r\n").Skip(4).Where(l => l.Length > 0).ToList();
        Assert.Equal([$"admin = \"{control}\""], teamLines);
    }

    /// <summary>The push's duplicate check agrees with the variables file: a mapping is no duplicate of a
    /// team that only has the variable by name.</summary>
    [Fact]
    public void FindDuplicateTeamVariables_AMappingIsNoDuplicateOfATeamThatOnlyHasTheVariableByName()
    {
        Assert.Empty(IntegrationCasterExtensions.FindDuplicateTeamVariables([("Admin", null), ("Exercise Control", "admin")]));
    }

    [Fact]
    public void FindDuplicateTeamVariables_CatchesUnmappedTeamsThatShareAName()
    {
        var duplicates = IntegrationCasterExtensions.FindDuplicateTeamVariables([("Gold Cell", null), ("Blue Team", null), ("gold cell", null)]);

        Assert.Equal(["gold_cell"], duplicates.Keys);
    }

    [Theory]
    [InlineData("view_id")]
    [InlineData("username")]
    [InlineData("2nd_squad")]
    [InlineData("admin = \"x\"\nother")]
    public void GetVarsFileContent_RefusesAMappingTfvarsCannotHold(string variable)
    {
        Assert.Throws<Exception>(() => IntegrationCasterExtensions.GetVarsFileContent(
            ViewId, UserId, "u", [("Blue Team", Guid.NewGuid(), variable)]));
    }

    [Theory]
    [InlineData("view_id")]
    [InlineData("username")]
    [InlineData("2nd_squad")]
    [InlineData("admin = \"x\"\nother")]
    public void IsMappableTeamVariable_RefusesANameTfvarsCannotHold(string variable)
    {
        Assert.False(IntegrationCasterExtensions.IsMappableTeamVariable(variable));
    }

    /// <remarks>
    /// A template shaped like Basic Topology: two team variables, the rest connection
    /// settings that its terraform.tfvars fills in.
    /// </remarks>
    [Fact]
    public void FindTeamVariableSlots_FindsTheTemplatesTeamVariables()
    {
        var variables = """
            variable "admin" {
              description = "Player Admin team ID injected by Alloy from the cloned view team named Admin."
              type        = string
              default     = null
            }
            variable "admin_user" {
              type    = string
              default = null
            }
            variable "proxmox_endpoint" {
              type = string
            }
            variable "proxmox_insecure" {
              type    = bool
              default = true
            }
            variable "vm_count" {
              type    = number
              default = 1
            }
            variable "crucible_password" {
              type      = string
              sensitive = true
            }
            variable "exercise_id" {
              type    = string
              default = null
            }
            variable "user_id" {
              type = string
            }
            variable "notes" {}
            # variable "commented_out" {}
            /*
            variable "block_commented" {}
            */
            """;
        var tfvars = """
            proxmox_endpoint = "https://10.0.100.2:8006/"
            tags = {
              notes = "an object key, not an assignment"
            }
            """;

        var slots = IntegrationCasterExtensions.FindTeamVariableSlots(
            [("variables.tf", variables), ("terraform.tfvars", tfvars), ("main.tf", "locals {}")]);

        Assert.Equal([("admin", false), ("admin_user", false), ("notes", true)], slots.Select(s => (s.Name, s.Required)));
    }

    [Fact]
    public void FindTeamVariableSlots_ARequiredVariableIsOneWithNoDefault()
    {
        var variables = """
            variable "blue" { default = "" }
            variable "red" {
              type        = string
              description = "default = is in the description, not an attribute"
            }
            variable "green" {
              type = string
              validation {
                condition     = length(var.green) > 0
                error_message = "Needs a team."
              }
            }
            """;

        var slots = IntegrationCasterExtensions.FindTeamVariableSlots([("variables.tf", variables)]);

        Assert.Equal([("blue", false), ("green", true), ("red", true)], slots.Select(s => (s.Name, s.Required)));
    }

    [Fact]
    public void FindTeamVariableSlots_ReadsJsonFiles()
    {
        var tfJson = """
            {"variable": {"red": {"type": "string"}, "count": {"type": "number"}, "blue": {"default": null}, "assigned": {}}}
            """;

        var slots = IntegrationCasterExtensions.FindTeamVariableSlots(
            [("teams.tf.json", tfJson), ("x.auto.tfvars.json", """{"assigned": "a"}"""), ("broken.tf.json", "{")]);

        Assert.Equal([("blue", false), ("red", true)], slots.Select(s => (s.Name, s.Required)));
    }

    // --- push ---

    [Fact]
    public async Task PlanAndApply_WaitsThroughEveryInterimStatusAndAppliesOnce()
    {
        var handler = new SiblingApiHandler()
            .AnswersJson($"GET api/runs/{RunId}", Run("Queued"), once: true)
            .AnswersJson($"GET api/runs/{RunId}", Run("Planning"), once: true)
            .AnswersJson($"GET api/runs/{RunId}", Run("Planned"), once: true)
            .AnswersJson($"GET api/runs/{RunId}", Run("ApplyQueued"), once: true)
            .AnswersJson($"GET api/runs/{RunId}", Run("Applying"), once: true)
            .AnswersJson($"GET api/runs/{RunId}", Run("Applied"))
            .AnswersJson($"POST api/runs/{RunId}/actions/apply", "{}");
        var statuses = new List<string>();

        await IntegrationCasterExtensions.PlanAndApplyAsync(
            RunId, Source(handler), Options, 1, Record(statuses), Ct);

        Assert.Single(handler.Sent, x => x.Path == $"api/runs/{RunId}/actions/apply");
        Assert.Equal(["Caster - planning", "Caster - applying"], statuses);
    }

    [Fact]
    public async Task PlanAndApply_AFailedPlanIsNeverApplied()
    {
        var handler = new SiblingApiHandler()
            .AnswersJson($"GET api/runs/{RunId}", Run("Failed"));

        var ex = await Assert.ThrowsAsync<Exception>(async () => await IntegrationCasterExtensions.PlanAndApplyAsync(
            RunId, Source(handler), Options, 1, _ => Task.CompletedTask, Ct));

        Assert.Contains("plan ended Failed", ex.Message);
        Assert.DoesNotContain(handler.Sent, x => x.Path.EndsWith("/actions/apply"));
    }

    [Fact]
    public async Task PlanAndApply_SavesStateAfterAStateError()
    {
        var handler = new SiblingApiHandler()
            .AnswersJson($"GET api/runs/{RunId}", Run("Planned"), once: true)
            .AnswersJson($"GET api/runs/{RunId}", Run("Applied - State Error"), once: true)
            .AnswersJson($"GET api/runs/{RunId}", Run("Applied"))
            .AnswersJson($"POST api/runs/{RunId}/actions/apply", "{}")
            .AnswersJson($"POST api/runs/{RunId}/actions/save-state", Run("Applied"));

        await IntegrationCasterExtensions.PlanAndApplyAsync(
            RunId, Source(handler), Options, 1, _ => Task.CompletedTask, Ct);

        Assert.Single(handler.Sent, x => x.Path == $"api/runs/{RunId}/actions/save-state");
    }

    [Fact]
    public async Task PlanAndApply_StopsWhenCancelled()
    {
        var handler = new SiblingApiHandler()
            .AnswersJson($"GET api/runs/{RunId}", Run("Planning"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await IntegrationCasterExtensions.PlanAndApplyAsync(
            RunId, Source(handler), Options, 1, _ => Task.CompletedTask, cts.Token));
    }

    /// <summary>A run still planning when the plan's wait runs out is reported as a timeout and never
    /// applied.</summary>
    [Fact]
    public async Task PlanAndApply_ARunStillPlanningWhenTheWaitRunsOutTimesOut()
    {
        var handler = new SiblingApiHandler()
            .AnswersJson($"GET api/runs/{RunId}", Run("Planning"));
        var noWait = new ClientOptions { CasterCheckIntervalSeconds = 0, CasterPlanMaxWaitMinutes = 0 };

        var ex = await Assert.ThrowsAsync<TimeoutException>(async () => await IntegrationCasterExtensions.PlanAndApplyAsync(
            RunId, Source(handler), noWait, 1, _ => Task.CompletedTask, Ct));

        Assert.Equal(
            $"Caster run {RunId} plan was still Planning after 0 minutes. Time spent queued behind other Caster runs counts too.",
            ex.Message);
        Assert.DoesNotContain(handler.Sent, x => x.Path.EndsWith("/actions/apply"));
    }

    [Fact]
    public async Task PlanAndApply_RefusesARunStatusItDoesNotKnow()
    {
        var handler = new SiblingApiHandler()
            .AnswersJson($"GET api/runs/{RunId}", Run("Exploded"));

        var ex = await Assert.ThrowsAsync<Exception>(async () => await IntegrationCasterExtensions.PlanAndApplyAsync(
            RunId, Source(handler), Options, 1, _ => Task.CompletedTask, Ct));

        Assert.Equal("Caster returned an unknown run status \"Exploded\".", ex.Message);
        Assert.DoesNotContain(handler.Sent, x => x.Path.EndsWith("/actions/apply"));
    }

    // --- pull ---

    [Fact]
    public async Task DestroyWorkspace_AWorkspaceThatIsAlreadyGoneIsDone()
    {
        var handler = new SiblingApiHandler()
            .Answers($"GET api/workspaces/{WorkspaceId}", HttpStatusCode.NotFound);

        await IntegrationCasterExtensions.DestroyWorkspaceAsync(
            WorkspaceId, Source(handler), Options, _ => Task.CompletedTask, Ct);

        Assert.Single(handler.Sent);
    }

    [Fact]
    public async Task DestroyWorkspace_DestroysThenDeletes()
    {
        var destroyRunId = Guid.NewGuid();
        var handler = Workspace("[]")
            .AnswersJson($"GET api/workspaces/{WorkspaceId}/resources", Resources(1), once: true)
            .AnswersJson($"GET api/workspaces/{WorkspaceId}/resources", "[]")
            .AnswersJson("POST api/runs", Run("Queued", destroyRunId), HttpStatusCode.Created)
            .AnswersJson($"GET api/runs/{destroyRunId}", Run("Planned", destroyRunId), once: true)
            .AnswersJson($"GET api/runs/{destroyRunId}", Run("Applied", destroyRunId))
            .AnswersJson($"POST api/runs/{destroyRunId}/actions/apply", "{}")
            .Answers($"DELETE api/workspaces/{WorkspaceId}", HttpStatusCode.NoContent);

        await IntegrationCasterExtensions.DestroyWorkspaceAsync(
            WorkspaceId, Source(handler), Options, _ => Task.CompletedTask, Ct);

        Assert.Contains("\"isDestroy\":true", handler.Sent.Single(x => x.Method == HttpMethod.Post && x.Path == "api/runs").Body);
        Assert.Equal(HttpMethod.Delete, handler.Sent.Last().Method);
    }

    [Fact]
    public async Task DestroyWorkspace_WaitsForARunningApplyInsteadOfCancellingIt()
    {
        var handler = Workspace($"[{Run("Applying")}]")
            .AnswersJson($"GET api/runs/{RunId}", Run("Applying"), once: true)
            .AnswersJson($"GET api/runs/{RunId}", Run("Applied"))
            .AnswersJson($"GET api/workspaces/{WorkspaceId}/resources", "[]")
            .Answers($"DELETE api/workspaces/{WorkspaceId}", HttpStatusCode.NoContent);

        await IntegrationCasterExtensions.DestroyWorkspaceAsync(
            WorkspaceId, Source(handler), Options, _ => Task.CompletedTask, Ct);

        Assert.DoesNotContain(handler.Sent, x => x.Path.EndsWith("/actions/cancel"));
        Assert.Equal(2, handler.Sent.Count(x => x.Path == $"api/runs/{RunId}"));
    }

    /// <summary>A queued run is cancelled once, without force, and waited out before the destroy.</summary>
    [Fact]
    public async Task DestroyWorkspace_CancelsAQueuedRunOnceThenDestroys()
    {
        var destroyRunId = Guid.NewGuid();
        var handler = Workspace($"[{Run("Queued")}]")
            .AnswersJson($"POST api/runs/{RunId}/actions/cancel", Run("Queued"))
            .AnswersJson($"GET api/runs/{RunId}", Run("Queued"), once: true)
            .AnswersJson($"GET api/runs/{RunId}", Run("Rejected"))
            .AnswersJson($"GET api/workspaces/{WorkspaceId}/resources", Resources(1), once: true)
            .AnswersJson($"GET api/workspaces/{WorkspaceId}/resources", "[]")
            .AnswersJson("POST api/runs", Run("Queued", destroyRunId), HttpStatusCode.Created)
            .AnswersJson($"GET api/runs/{destroyRunId}", Run("Planned", destroyRunId), once: true)
            .AnswersJson($"GET api/runs/{destroyRunId}", Run("Applied", destroyRunId))
            .AnswersJson($"POST api/runs/{destroyRunId}/actions/apply", "{}")
            .Answers($"DELETE api/workspaces/{WorkspaceId}", HttpStatusCode.NoContent);

        await IntegrationCasterExtensions.DestroyWorkspaceAsync(
            WorkspaceId, Source(handler), Options, _ => Task.CompletedTask, Ct);

        var cancel = Assert.Single(handler.Sent, x => x.Path.EndsWith("/actions/cancel"));
        Assert.Equal($"api/runs/{RunId}/actions/cancel", cancel.Path);
        Assert.Contains("\"force\":false", cancel.Body);

        // the cancelled run is polled until it ends, and only then is the destroy created
        var requests = handler.Sent.Select(x => $"{x.Method} {x.Path}").ToList();
        Assert.Equal(2, requests.Count(x => x == $"GET api/runs/{RunId}"));
        Assert.True(requests.LastIndexOf($"GET api/runs/{RunId}") < requests.IndexOf("POST api/runs"));

        Assert.Contains("\"isDestroy\":true", handler.Sent.Single(x => x.Method == HttpMethod.Post && x.Path == "api/runs").Body);
        Assert.Equal(HttpMethod.Delete, handler.Sent.Last().Method);
    }

    [Fact]
    public async Task DestroyWorkspace_RejectsAPlanThatWasNeverApplied()
    {
        var handler = Workspace($"[{Run("Planned")}]")
            .AnswersJson($"POST api/runs/{RunId}/actions/reject", Run("Rejected"))
            .AnswersJson($"GET api/workspaces/{WorkspaceId}/resources", "[]")
            .Answers($"DELETE api/workspaces/{WorkspaceId}", HttpStatusCode.NoContent);

        await IntegrationCasterExtensions.DestroyWorkspaceAsync(
            WorkspaceId, Source(handler), Options, _ => Task.CompletedTask, Ct);

        Assert.Single(handler.Sent, x => x.Path == $"api/runs/{RunId}/actions/reject");
    }

    /// <remarks>
    /// Caster writes this status by its EnumMember name, which the typed client's Run.Status converter does
    /// not read, so the run list is read raw.
    /// </remarks>
    [Fact]
    public async Task DestroyWorkspace_SavesTheStateOfARunCasterCouldNotSave()
    {
        var handler = Workspace($"[{Run("Failed - State Error")}]")
            .AnswersJson($"POST api/runs/{RunId}/actions/save-state", Run("Failed - State Error"))
            .AnswersJson($"GET api/workspaces/{WorkspaceId}/resources", "[]")
            .Answers($"DELETE api/workspaces/{WorkspaceId}", HttpStatusCode.NoContent);

        await IntegrationCasterExtensions.DestroyWorkspaceAsync(
            WorkspaceId, Source(handler), Options, _ => Task.CompletedTask, Ct);

        Assert.Single(handler.Sent, x => x.Path == $"api/runs/{RunId}/actions/save-state");
        Assert.Equal(HttpMethod.Delete, handler.Sent.Last().Method);
    }

    [Fact]
    public async Task DestroyWorkspace_RetriesADestroyThatFailed()
    {
        var firstRunId = Guid.NewGuid();
        var secondRunId = Guid.NewGuid();
        var handler = Workspace("[]")
            // still there after the failed first destroy, gone after the second
            .AnswersJson($"GET api/workspaces/{WorkspaceId}/resources", Resources(1), once: true)
            .AnswersJson($"GET api/workspaces/{WorkspaceId}/resources", Resources(1), once: true)
            .AnswersJson($"GET api/workspaces/{WorkspaceId}/resources", "[]")
            .AnswersJson("POST api/runs", Run("Queued", firstRunId), HttpStatusCode.Created, once: true)
            .AnswersJson("POST api/runs", Run("Queued", secondRunId), HttpStatusCode.Created)
            .AnswersJson($"GET api/runs/{firstRunId}", Run("Failed", firstRunId))
            .AnswersJson($"GET api/runs/{secondRunId}", Run("Planned", secondRunId), once: true)
            .AnswersJson($"GET api/runs/{secondRunId}", Run("Applied", secondRunId))
            .AnswersJson($"POST api/runs/{secondRunId}/actions/apply", "{}")
            .Answers($"DELETE api/workspaces/{WorkspaceId}", HttpStatusCode.NoContent);

        await IntegrationCasterExtensions.DestroyWorkspaceAsync(
            WorkspaceId, Source(handler), Options, _ => Task.CompletedTask, Ct);

        Assert.Equal(2, handler.Sent.Count(x => x.Method == HttpMethod.Post && x.Path == "api/runs"));
        Assert.Equal(HttpMethod.Delete, handler.Sent.Last().Method);
    }

    [Fact]
    public async Task DestroyWorkspace_ReportsTheLastDestroysFailure()
    {
        var handler = Workspace("[]")
            .AnswersJson($"GET api/workspaces/{WorkspaceId}/resources", Resources(1))
            .AnswersJson("POST api/runs", Run("Queued"), HttpStatusCode.Created)
            .AnswersJson($"GET api/runs/{RunId}", Run("Failed"));

        await Assert.ThrowsAsync<Exception>(async () => await IntegrationCasterExtensions.DestroyWorkspaceAsync(
            WorkspaceId, Source(handler), Options, _ => Task.CompletedTask, Ct));

        Assert.Equal(2, handler.Sent.Count(x => x.Method == HttpMethod.Post && x.Path == "api/runs"));
        Assert.DoesNotContain(handler.Sent, x => x.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task DestroyWorkspace_GivesUpAfterTwoDestroys()
    {
        var destroyRunId = Guid.NewGuid();
        var handler = Workspace("[]")
            .AnswersJson($"GET api/workspaces/{WorkspaceId}/resources", Resources(1))
            .AnswersJson("POST api/runs", Run("Queued", destroyRunId), HttpStatusCode.Created)
            .AnswersJson($"GET api/runs/{destroyRunId}", Run("Planned", destroyRunId), once: true)
            .AnswersJson($"GET api/runs/{destroyRunId}", Run("Applied", destroyRunId), once: true)
            .AnswersJson($"GET api/runs/{destroyRunId}", Run("Planned", destroyRunId), once: true)
            .AnswersJson($"GET api/runs/{destroyRunId}", Run("Applied", destroyRunId), once: true)
            .AnswersJson($"POST api/runs/{destroyRunId}/actions/apply", "{}");

        var ex = await Assert.ThrowsAsync<Exception>(async () => await IntegrationCasterExtensions.DestroyWorkspaceAsync(
            WorkspaceId, Source(handler), Options, _ => Task.CompletedTask, Ct));

        Assert.Contains("still has resources after 2 destroy attempts", ex.Message);
        Assert.Equal(2, handler.Sent.Count(x => x.Method == HttpMethod.Post && x.Path == "api/runs"));
        Assert.DoesNotContain(handler.Sent, x => x.Method == HttpMethod.Delete);
    }

    // --- token ---

    [Fact]
    public async Task ClientSource_FetchesItsFirstTokenWhenFirstUsed()
    {
        var fetches = 0;
        var source = new CasterClientSource(
            new SiblingApiHandler().AsFactory(),
            "http://caster.example/",
            async () => { fetches++; return await Tokens.Bearer(); });

        Assert.Equal(0, fetches);
        await source.GetAsync(Ct);

        Assert.Equal(1, fetches);
    }

    /// <summary>A 300-second token outlasts the refresh margin, so the next call keeps it.</summary>
    [Fact]
    public async Task ClientSource_KeepsATokenThatLasts()
    {
        var fetches = 0;
        var source = new CasterClientSource(
            new SiblingApiHandler().AsFactory(),
            "http://caster.example/",
            async () => { fetches++; return await Tokens.Bearer(); });
        await source.GetAsync(Ct);

        await source.GetAsync(Ct);

        Assert.Equal(1, fetches);
    }

    /// <summary>A 30-second token is inside the refresh margin, so the next call replaces it.</summary>
    [Fact]
    public async Task ClientSource_RefreshesATokenAboutToExpire()
    {
        var fetches = 0;
        var source = new CasterClientSource(
            new SiblingApiHandler().AsFactory(),
            "http://caster.example/",
            async () => { fetches++; return await Tokens.Bearer(expiresIn: fetches == 1 ? 30 : 300); });
        await source.GetAsync(Ct);

        await source.GetAsync(Ct);

        Assert.Equal(2, fetches);
    }

    /// <summary>The 300-second token a refresh got lasts for the call after it.</summary>
    [Fact]
    public async Task ClientSource_KeepsTheTokenARefreshGot()
    {
        var fetches = 0;
        var source = new CasterClientSource(
            new SiblingApiHandler().AsFactory(),
            "http://caster.example/",
            async () => { fetches++; return await Tokens.Bearer(expiresIn: fetches == 1 ? 30 : 300); });
        await source.GetAsync(Ct);
        await source.GetAsync(Ct);

        await source.GetAsync(Ct);

        Assert.Equal(2, fetches);
    }

    /// <summary>A token response that is an error is refused, naming the error.</summary>
    [Fact]
    public async Task ClientSource_RefusesATokenResponseThatIsAnError()
    {
        using var refusal = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"invalid_grant"}""", System.Text.Encoding.UTF8, "application/json")
        };
        var token = await IdentityModel.Client.ProtocolResponse.FromHttpResponseAsync<IdentityModel.Client.TokenResponse>(refusal);
        var source = new CasterClientSource(
            new SiblingApiHandler().AsFactory(), "http://caster.example/", () => Task.FromResult(token));

        var ex = await Assert.ThrowsAsync<Exception>(async () => await source.GetAsync(Ct));

        Assert.Equal("Could not get a token for Caster: invalid_grant", ex.Message);
    }

    // --- helpers ---

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CasterClientSource Source(SiblingApiHandler handler) =>
        new(handler.AsFactory(), "http://caster.example/", () => Tokens.Bearer());

    private static SiblingApiHandler Workspace(string runs) =>
        new SiblingApiHandler()
            .AnswersJson($"GET api/workspaces/{WorkspaceId}", $$"""{"id":"{{WorkspaceId}}","name":"ws","directoryId":"{{Guid.NewGuid()}}"}""")
            .AnswersJson($"GET api/workspaces/{WorkspaceId}/runs", runs);

    private static string Run(string status, Guid? id = null) =>
        $$"""{"id":"{{id ?? RunId}}","workspaceId":"{{WorkspaceId}}","isDestroy":false,"status":"{{status}}","createdAt":"2026-10-07T00:00:00Z"}""";

    private static string Resources(int count) =>
        "[" + string.Join(",", Enumerable.Range(0, count).Select(i => $$"""{"id":"r{{i}}","name":"vm{{i}}","type":"vsphere_virtual_machine","address":"vm{{i}}"}""")) + "]";

    private static Func<string, Task> Record(List<string> statuses) => s =>
    {
        statuses.Add(s);
        return Task.CompletedTask;
    };
}
