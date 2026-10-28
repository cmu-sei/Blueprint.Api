// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using STT = System.Threading.Tasks;
using Caster.Api.Client;
using IdentityModel.Client;
using Blueprint.Api.Data.Models;
using ClientOptions = Blueprint.Api.Infrastructure.Options.ClientOptions;
using Player.Api.Client;

namespace Blueprint.Api.Infrastructure.Extensions
{
    /// <summary>
    /// Deploys and destroys a MSEL's topology as a Caster workspace. Mirrors Alloy's
    /// CasterApiExtensions so that a template directory that works under Alloy works here unchanged,
    /// but throws on failure instead of returning null, so the push reports which step failed.
    /// </summary>
    public static class IntegrationCasterExtensions
    {
        // statuses a run passes through before its plan is ready
        private static readonly RunStatus[] _planningStatuses = { RunStatus.Queued, RunStatus.Planning };
        // statuses a run passes through between the apply request and a terminal state
        private static readonly RunStatus[] _applyingStatuses = {
            RunStatus.Planned,
            RunStatus.ApplyQueued,
            RunStatus.Applying,
            RunStatus.Applied__State_Error,
            RunStatus.Failed__State_Error
        };
        private static readonly RunStatus[] _terminalStatuses = { RunStatus.Applied, RunStatus.Failed, RunStatus.Rejected };

        // Alloy's rule for team variable names: lower case, spaces to underscores, then drop @&'()<>#.
        // Existing templates declare their variables under these names, so keep it. Then drop anything
        // else an HCL identifier cannot hold (a comma, say): such a name never worked under Alloy either,
        // because terraform rejects the whole file, so no working template can depend on it.
        // ToLowerInvariant, as Alloy does: ToLower is culture-sensitive, so on a Turkish-locale host "I" would
        // lowercase to the dotless "ı" and the variable name would change.
        public static string CleanName(string name)
        {
            var alloyName = Regex.Replace((name ?? "").ToLowerInvariant().Replace(" ", "_"), "[@&'(\\s)<>#]", "", RegexOptions.None);
            return Regex.Replace(alloyName, "[^\\p{L}\\p{Nd}_-]", "", RegexOptions.None);
        }

        // Built from the MSEL ID alone, never its name: a free-text name has to satisfy Caster's
        // character rule, terraform's TF_WORKSPACE rule and, in Kubernetes job mode, a 63-character
        // label, and deriving a name that survives all three is what broke the first live push.
        public static string GetWorkspaceName(MselEntity msel)
        {
            return $"blueprint-{msel.Id}";
        }

        // teamVariables holds the template variable each MSEL team is mapped to, by team ID. Blueprint
        // creates the view's teams with the MSEL teams' IDs; a team missing from it keeps its own name.
        public static async STT.Task<string> GetVarsFileContentAsync(Guid playerViewId, Guid userId, string userName, IReadOnlyDictionary<Guid, string> teamVariables, PlayerApiClient playerApiClient, CancellationToken ct)
        {
            var teams = await playerApiClient.GetViewTeamsAsync(playerViewId, ct);
            return GetVarsFileContent(playerViewId, userId, userName, teams.Select(t => (t.Name, t.Id, teamVariables.GetValueOrDefault(t.Id))));
        }

        public static string GetVarsFileContent(Guid playerViewId, Guid userId, string userName, IEnumerable<(string Name, Guid Id, string Variable)> teams)
        {
            var content = new StringBuilder();
            // TODO: exercise_id is deprecated, but existing Caster modules still read it
            content.Append($"exercise_id = \"{playerViewId}\"\r\n");
            content.Append($"view_id = \"{playerViewId}\"\r\n");
            content.Append($"user_id = \"{userId}\"\r\n");
            content.Append($"username = \"{EscapeHclString(userName)}\"\r\n");
            // a second line for the same name is a duplicate attribute, which fails the whole file;
            // dropping one instead would deploy without that team's resources and report success
            var written = ResolveTeamVariables(teams).ToList();
            var duplicates = FindDuplicates(written.Select(t => (t.Name, t.Variable)));
            if (duplicates.Count > 0)
                throw new Exception(DuplicateTeamVariablesMessage(duplicates));
            foreach (var team in written)
                content.Append($"{team.Variable} = \"{team.Id}\"\r\n");
            return content.ToString();
        }

        // The line each team gets, if any. A team with no usable name gets none: no template can
        // declare it. A mapping beats a variable a team only gets from its name: Player's own Admin
        // team, for one, becomes "admin", which is the variable a planner maps a MSEL team to.
        private static IEnumerable<(string Name, Guid Id, string Variable)> ResolveTeamVariables(IEnumerable<(string Name, Guid Id, string Variable)> teams)
        {
            var resolved = teams
                .Select(t => (t.Name, t.Id, Variable: GetTeamVariableName(t.Name, t.Variable), Mapped: !string.IsNullOrWhiteSpace(t.Variable)))
                .Where(t => t.Variable != null)
                .ToList();
            var mapped = resolved.Where(t => t.Mapped).Select(t => t.Variable).ToHashSet();
            return resolved
                .Where(t => t.Mapped || !mapped.Contains(t.Variable))
                .Select(t => (t.Name, t.Id, t.Variable));
        }

        /// <summary>
        /// The tfvars variable a team's ID is written under, or null when the name leaves nothing usable.
        /// </summary>
        public static string GetTeamVariableName(string teamName)
        {
            var name = CleanName(teamName);
            if (name.Length == 0)
                return null;
            // An identifier must start with a letter or an underscore. No template can declare
            // "2nd_squad", so the prefix breaks nothing; terraform only warns about an undeclared
            // variable in a tfvars file, where a bad identifier fails the whole file.
            if (!char.IsLetter(name[0]) && name[0] != '_')
                name = "_" + name;
            return name;
        }

        /// <summary>
        /// The variable a team is mapped to, or else the one its name gives.
        /// </summary>
        public static string GetTeamVariableName(string teamName, string mappedVariable)
        {
            if (string.IsNullOrWhiteSpace(mappedVariable))
                return GetTeamVariableName(teamName);
            var variable = mappedVariable.Trim();
            // only reachable for a mapping that bypassed TeamService; written as is it would fail the whole file
            if (!IsMappableTeamVariable(variable))
                throw new Exception($"Team \"{teamName}\" is mapped to \"{variable}\", which cannot hold a team ID.");
            return variable;
        }

        /// <summary>
        /// Whether a team can be mapped to this variable: a terraform identifier that is not one of
        /// the variables Blueprint writes itself.
        /// </summary>
        public static bool IsMappableTeamVariable(string variable)
        {
            return variable != null
                && Regex.IsMatch(variable, "^[\\p{L}_][\\p{L}\\p{Nd}_-]*$")
                && !BlueprintVariables.Contains(variable);
        }

        // the variables GetVarsFileContent writes before the teams
        public static readonly IReadOnlySet<string> BlueprintVariables = new HashSet<string> { "exercise_id", "view_id", "user_id", "username" };

        /// <summary>
        /// Variable names that two or more of these teams would share, with the teams sharing each.
        /// </summary>
        public static Dictionary<string, List<string>> FindDuplicateTeamVariables(IEnumerable<(string Name, string Variable)> teams)
        {
            return FindDuplicates(ResolveTeamVariables(teams.Select(t => (t.Name, Guid.Empty, t.Variable))).Select(t => (t.Name, t.Variable)));
        }

        private static Dictionary<string, List<string>> FindDuplicates(IEnumerable<(string Name, string Variable)> teams)
        {
            return teams
                .Select(t => (Team: t.Name, t.Variable))
                .GroupBy(x => x.Variable)
                .Where(g => g.Count() > 1)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Team).ToList());
        }

        public static string DuplicateTeamVariablesMessage(Dictionary<string, List<string>> duplicates)
        {
            var clashes = duplicates.Select(d => $"{string.Join(" and ", d.Value.Select(t => $"\"{t}\""))} all become \"{d.Key}\"");
            return $"Caster needs a distinct variable name for each team, but {string.Join("; ", clashes)}. Rename one of each or map it to a different topology team.";
        }

        /// <summary>
        /// The variables a template leaves open for team IDs: those its .tf and .tf.json files declare
        /// as strings, less the sensitive ones, the ones its .tfvars files already assign and the ones
        /// Blueprint writes itself. A template takes team IDs this way by convention only, so this is
        /// the list a planner picks from, not a guarantee that a variable lands VMs on a team. One with no
        /// default is required: terraform fails the plan unless a team is mapped to it or Caster supplies it.
        /// </summary>
        public static List<ViewModels.CasterTeamVariable> FindTeamVariableSlots(IEnumerable<(string Name, string Content)> files)
        {
            var declared = new List<(string Name, bool Required)>();
            var assigned = new HashSet<string>();
            foreach (var (name, content) in files)
            {
                var fileName = (name ?? "").ToLowerInvariant();
                if (fileName.EndsWith(".tf"))
                    declared.AddRange(HclStringVariables(content ?? ""));
                else if (fileName.EndsWith(".tf.json"))
                    declared.AddRange(JsonStringVariables(content ?? ""));
                else if (fileName.EndsWith(".tfvars"))
                    assigned.UnionWith(HclTopLevelAttributes(content ?? ""));
                else if (fileName.EndsWith(".tfvars.json"))
                    assigned.UnionWith(JsonTopLevelKeys(content ?? ""));
            }
            // terraform refuses a variable declared twice, so the first declaration stands
            return declared
                .DistinctBy(v => v.Name)
                .Where(v => !assigned.Contains(v.Name) && IsMappableTeamVariable(v.Name))
                .OrderBy(v => v.Name, StringComparer.Ordinal)
                .Select(v => new ViewModels.CasterTeamVariable { Name = v.Name, Required = v.Required })
                .ToList();
        }

        private static readonly Regex _variableBlock = new("^[ \\t]*variable[ \\t]+\"?([^\"\\s{]+)\"?[ \\t]*\\{", RegexOptions.Multiline);

        private static IEnumerable<(string Name, bool Required)> HclStringVariables(string content)
        {
            content = Regex.Replace(content, "/\\*.*?\\*/", "", RegexOptions.Singleline);
            foreach (Match match in _variableBlock.Matches(content))
            {
                var body = BlockBody(content, match.Index + match.Length);
                var type = Regex.Match(body, "^[ \\t]*type[ \\t]*=[ \\t]*([^\\s#]+)", RegexOptions.Multiline);
                // an untyped variable takes a string
                if (type.Success && type.Groups[1].Value != "string")
                    continue;
                if (Regex.IsMatch(body, "^[ \\t]*sensitive[ \\t]*=[ \\t]*true", RegexOptions.Multiline))
                    continue;
                var required = !Regex.IsMatch(body, "^[ \\t]*default[ \\t]*=", RegexOptions.Multiline);
                yield return (match.Groups[1].Value, required);
            }
        }

        // the text from start up to the brace that closes the block start sits in
        private static string BlockBody(string content, int start)
        {
            var depth = 1;
            var inString = false;
            for (var i = start; i < content.Length; i++)
            {
                var c = content[i];
                if (inString)
                {
                    if (c == '\\')
                        i++;
                    else if (c == '"')
                        inString = false;
                }
                else if (c == '"')
                    inString = true;
                else if (c == '{')
                    depth++;
                else if (c == '}' && --depth == 0)
                    return content[start..i];
            }
            return content[start..];
        }

        // attribute names at the top level of a tfvars file, not keys inside an object value
        private static IEnumerable<string> HclTopLevelAttributes(string content)
        {
            var depth = 0;
            foreach (var line in content.Split('\n'))
            {
                if (depth == 0)
                {
                    var attribute = Regex.Match(line, "^[ \\t]*([\\p{L}_][\\p{L}\\p{Nd}_-]*)[ \\t]*=");
                    if (attribute.Success)
                        yield return attribute.Groups[1].Value;
                }
                var inString = false;
                for (var i = 0; i < line.Length; i++)
                {
                    var c = line[i];
                    if (inString)
                    {
                        if (c == '\\')
                            i++;
                        else if (c == '"')
                            inString = false;
                    }
                    else if (c == '"')
                        inString = true;
                    else if (c == '#' || (c == '/' && i + 1 < line.Length && line[i + 1] == '/'))
                        break;
                    else if (c == '{' || c == '[' || c == '(')
                        depth++;
                    else if ((c == '}' || c == ']' || c == ')') && depth > 0)
                        depth--;
                }
            }
        }

        private static IEnumerable<(string Name, bool Required)> JsonStringVariables(string content)
        {
            if (!TryParseJsonObject(content, out var root) ||
                !root.TryGetProperty("variable", out var variables) ||
                variables.ValueKind != JsonValueKind.Object)
                return [];
            return variables.EnumerateObject()
                .Where(v => v.Value.ValueKind != JsonValueKind.Object ||
                    ((!v.Value.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() == "string") &&
                     (!v.Value.TryGetProperty("sensitive", out var sensitive) || sensitive.ValueKind != JsonValueKind.True)))
                .Select(v => (v.Name, Required: v.Value.ValueKind != JsonValueKind.Object || !v.Value.TryGetProperty("default", out _)))
                .ToList();
        }

        private static IEnumerable<string> JsonTopLevelKeys(string content)
        {
            return TryParseJsonObject(content, out var root) ? root.EnumerateObject().Select(p => p.Name).ToList() : [];
        }

        // a file terraform cannot parse fails the run anyway; here it just declares nothing
        private static bool TryParseJsonObject(string content, out JsonElement root)
        {
            try
            {
                root = JsonDocument.Parse(content).RootElement.Clone();
                return root.ValueKind == JsonValueKind.Object;
            }
            catch (JsonException)
            {
                root = default;
                return false;
            }
        }

        // tfvars values are HCL strings: escape backslashes, quotes and template sequences
        private static string EscapeHclString(string value)
        {
            return (value ?? "")
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("${", "$${")
                .Replace("%{", "%%{");
        }

        public static async STT.Task<Workspace> CreateWorkspaceAsync(MselEntity msel, CasterApiClient casterApiClient, CancellationToken ct)
        {
            var workspaceCommand = new CreateWorkspaceCommand()
            {
                Name = GetWorkspaceName(msel),
                DirectoryId = (Guid)msel.CasterDirectoryId
            };
            return await casterApiClient.CreateWorkspaceAsync(workspaceCommand, ct);
        }

        public static async STT.Task CreateVarsFileAsync(Workspace workspace, string varsFileContent, CasterApiClient casterApiClient, CancellationToken ct)
        {
            var createFileCommand = new CreateFileCommand()
            {
                Name = $"{workspace.Name}.auto.tfvars",
                DirectoryId = workspace.DirectoryId,
                WorkspaceId = workspace.Id,
                Content = varsFileContent
            };
            await casterApiClient.CreateFileAsync(createFileCommand, ct);
        }

        /// <summary>
        /// Replaces the workspace's variables file, so a retry writes the variables as they are now,
        /// and creates it if the failed push never got that far.
        /// </summary>
        public static async STT.Task ReplaceVarsFileAsync(Workspace workspace, string varsFileContent, CasterApiClient casterApiClient, CancellationToken ct)
        {
            var name = $"{workspace.Name}.auto.tfvars";
            var files = await casterApiClient.GetFilesByDirectoryAsync(workspace.DirectoryId, false, false, ct);
            foreach (var file in files.Where(f => f.WorkspaceId == workspace.Id && f.Name == name))
            {
                try
                {
                    await casterApiClient.DeleteFileAsync(file.Id, ct);
                }
                catch (Caster.Api.Client.ApiException ex) when (ex.StatusCode == (int)HttpStatusCode.NoContent)
                {
                    // the generated client choking on an empty 204
                }
            }
            await CreateVarsFileAsync(workspace, varsFileContent, casterApiClient, ct);
        }

        /// <summary>The workspace, or null when Caster no longer has it.</summary>
        public static async STT.Task<Workspace> FindWorkspaceAsync(Guid workspaceId, CasterApiClient casterApiClient, CancellationToken ct)
        {
            try
            {
                return await casterApiClient.GetWorkspaceAsync(workspaceId, ct);
            }
            catch (Caster.Api.Client.ApiException ex) when (ex.StatusCode == (int)HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        public static async STT.Task<Run> CreateRunAsync(Guid workspaceId, bool isDestroy, CasterApiClient casterApiClient, CancellationToken ct)
        {
            var runCommand = new CreateRunCommand()
            {
                WorkspaceId = workspaceId,
                IsDestroy = isDestroy
            };
            return await casterApiClient.CreateRunAsync(runCommand, ct);
        }

        /// <summary>
        /// Plans a run, applies it and waits for it to finish. Throws unless the run ends Applied.
        /// </summary>
        public static async STT.Task PlanAndApplyAsync(
            Guid runId,
            CasterClientSource casterClientSource,
            ClientOptions clientOptions,
            int applyMaxWaitMinutes,
            Func<string, STT.Task> sendStatus,
            CancellationToken ct)
        {
            var label = "plan";
            await sendStatus("Caster - planning");
            var status = await WaitForRunAsync(runId, _planningStatuses, casterClientSource, clientOptions.CasterCheckIntervalSeconds, clientOptions.CasterPlanMaxWaitMinutes, label, ct);
            if (status != RunStatus.Planned)
                throw new Exception($"Caster run {runId} {label} ended {status}. See the run in Caster for the terraform output.");

            label = "apply";
            await sendStatus("Caster - applying");
            var applyClient = await casterClientSource.GetAsync(ct);
            await IgnoreStateErrorBodyAsync(() => applyClient.ApplyRunAsync(runId, ct));
            status = await WaitForRunAsync(runId, _applyingStatuses, casterClientSource, clientOptions.CasterCheckIntervalSeconds, applyMaxWaitMinutes, label, ct);
            if (status != RunStatus.Applied)
                throw new Exception($"Caster run {runId} {label} ended {status}. See the run in Caster for the terraform output.");
        }

        /// <summary>
        /// Polls a run while its status is in waitWhile. A *_StateError status means terraform finished
        /// but Caster could not save the state, so retry the save, as Alloy does.
        /// Throws on timeout; the token is refreshed on every poll that needs it.
        /// </summary>
        private static async STT.Task<RunStatus> WaitForRunAsync(
            Guid runId,
            RunStatus[] waitWhile,
            CasterClientSource casterClientSource,
            int intervalSeconds,
            int maxWaitMinutes,
            string label,
            CancellationToken ct)
        {
            var endTime = DateTime.UtcNow.AddMinutes(maxWaitMinutes);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var status = await casterClientSource.GetRunStatusAsync(runId, ct);
                if (!waitWhile.Contains(status))
                    return status;
                if (DateTime.UtcNow >= endTime)
                    throw new TimeoutException($"Caster run {runId} {label} was still {status} after {maxWaitMinutes} minutes. Time spent queued behind other Caster runs counts too.");
                if (status == RunStatus.Applied__State_Error || status == RunStatus.Failed__State_Error)
                {
                    var casterApiClient = await casterClientSource.GetAsync(ct);
                    await IgnoreStateErrorBodyAsync(() => casterApiClient.SaveStateAsync(runId, ct));
                }
                await STT.Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
            }
        }

        /// <summary>
        /// Destroys everything in the workspace and deletes it. Returns normally only when the
        /// workspace is gone; throws otherwise, so the caller can keep the workspace ID for a retry.
        /// </summary>
        public static async STT.Task DestroyWorkspaceAsync(
            Guid workspaceId,
            CasterClientSource casterClientSource,
            ClientOptions clientOptions,
            Func<string, STT.Task> sendStatus,
            CancellationToken ct)
        {
            var casterApiClient = await casterClientSource.GetAsync(ct);
            try
            {
                await casterApiClient.GetWorkspaceAsync(workspaceId, ct);
            }
            catch (Caster.Api.Client.ApiException ex) when (ex.StatusCode == (int)HttpStatusCode.NotFound)
            {
                // already gone
                return;
            }

            // A push that failed or was cancelled can leave a run in flight, and Caster refuses a
            // new run while one is incomplete.
            await sendStatus("Caster - stopping active runs");
            await StopActiveRunsAsync(workspaceId, casterClientSource, clientOptions, ct);

            // one retry: a destroy that times out on one resource usually succeeds the second time
            const int maxDestroyAttempts = 2;
            for (var attempt = 1; !await IsWorkspaceEmptyAsync(workspaceId, casterClientSource, ct); attempt++)
            {
                if (attempt > maxDestroyAttempts)
                    throw new Exception($"Caster workspace {workspaceId} still has resources after {maxDestroyAttempts} destroy attempts.");
                await sendStatus($"Caster - destroying workspace (attempt {attempt})");
                var run = await CreateRunAsync(workspaceId, true, await casterClientSource.GetAsync(ct), ct);
                try
                {
                    await PlanAndApplyAsync(run.Id, casterClientSource, clientOptions, clientOptions.CasterDestroyMaxWaitMinutes, sendStatus, ct);
                }
                catch (Exception ex) when (attempt < maxDestroyAttempts && ex is not OperationCanceledException)
                {
                    // a run that failed is what the retry is for; one that timed out is still going,
                    // and Caster refuses a new run until it is stopped
                    await StopActiveRunsAsync(workspaceId, casterClientSource, clientOptions, ct);
                }
            }

            await sendStatus("Caster - deleting workspace");
            try
            {
                await (await casterClientSource.GetAsync(ct)).DeleteWorkspaceAsync(workspaceId, ct);
            }
            catch (Caster.Api.Client.ApiException ex) when (ex.StatusCode == (int)HttpStatusCode.NotFound || ex.StatusCode == (int)HttpStatusCode.NoContent)
            {
                // already gone, or the generated client choking on an empty 204
            }
        }

        /// <summary>
        /// Brings every run in the workspace to a terminal state, as a destroy or a new run needs.
        /// </summary>
        public static async STT.Task StopActiveRunsAsync(Guid workspaceId, CasterClientSource casterClientSource, ClientOptions clientOptions, CancellationToken ct)
        {
            var runs = await casterClientSource.GetWorkspaceRunStatusesAsync(workspaceId, ct);
            foreach (var run in runs.Where(r => !_terminalStatuses.Contains(r.Status)))
            {
                var casterApiClient = await casterClientSource.GetAsync(ct);
                if (run.Status == RunStatus.Applying)
                {
                    // Cancelling mid-apply can leave the workspace locked with the run stuck in Applying,
                    // beyond the API's ability to recover. Waiting is slow but safe; the destroy follows.
                    await WaitForRunAsync(run.Id, _applyingStatuses, casterClientSource, clientOptions.CasterCheckIntervalSeconds, clientOptions.CasterApplyMaxWaitMinutes, "apply", ct);
                    continue;
                }
                if (run.Status == RunStatus.Planned)
                {
                    // planned but never applied: nothing was built, so rejecting is enough
                    await IgnoreStateErrorBodyAsync(() => casterApiClient.RejectRunAsync(run.Id, ct));
                    continue;
                }
                if (run.Status == RunStatus.Applied__State_Error || run.Status == RunStatus.Failed__State_Error)
                {
                    // terraform already finished; saving the state is what tells the destroy what exists
                    await IgnoreStateErrorBodyAsync(() => casterApiClient.SaveStateAsync(run.Id, ct));
                    continue;
                }
                // Queued, planning or waiting to apply: nothing has changed yet, so cancelling is safe.
                // Cancel once only; repeated cancels are what wedge a workspace.
                await IgnoreStateErrorBodyAsync(() => casterApiClient.CancelRunAsync(run.Id, new CancelRunCommand { Force = false }, ct));
                await WaitForRunAsync(
                    run.Id,
                    _planningStatuses.Concat(_applyingStatuses).ToArray(),
                    casterClientSource,
                    clientOptions.CasterCheckIntervalSeconds,
                    clientOptions.CasterDestroyMaxWaitMinutes,
                    "cancel",
                    ct);
            }
        }

        /// <summary>
        /// Runs a call whose answer is a Run or an Apply that Blueprint does not need to read.
        /// caster.api.client 1.5.0 puts JsonStringEnumConverter on Run.Status and Apply.Status, which
        /// ignores the EnumMember names Caster writes, so a body carrying "Applied - State Error" or
        /// "Failed - State Error" cannot be deserialized even though the action succeeded. Only that
        /// failure is ignored; any other unreadable answer is still an error. Every call wrapped here
        /// answers a single object, so the path is always "$.status"; lists of runs are never read
        /// through the typed client (see CasterClientSource.GetWorkspaceRunStatusesAsync).
        /// ApiException.Response is empty here because the client streams the body, so the response
        /// text cannot be checked instead.
        /// CRU-3090. Remove this, and the raw status reads in CasterClientSource, only once Blueprint
        /// references a caster.api.client newer than 1.5.0 that carries the fix. The ticket closing
        /// fixes the generator, not the package pinned in Directory.Packages.props.
        /// </summary>
        private static async STT.Task IgnoreStateErrorBodyAsync(Func<STT.Task> call)
        {
            try
            {
                await call();
            }
            catch (Caster.Api.Client.ApiException ex) when (
                ex.StatusCode >= 200 && ex.StatusCode < 300 &&
                ex.InnerException is JsonException jsonException &&
                jsonException.Path == "$.status" &&
                (jsonException.Message.Contains(nameof(RunStatus)) || jsonException.Message.Contains(nameof(ApplyStatus))))
            {
            }
        }

        private static async STT.Task<bool> IsWorkspaceEmptyAsync(Guid workspaceId, CasterClientSource casterClientSource, CancellationToken ct)
        {
            var resources = await (await casterClientSource.GetAsync(ct)).GetResourcesByWorkspaceAsync(workspaceId, ct);
            return resources.Count == 0;
        }
    }

    /// <summary>
    /// Hands out a Caster client whose token is still valid. The other integrations finish within
    /// one token lifetime; a terraform apply can run for hours.
    /// </summary>
    public class CasterClientSource
    {
        // refresh this long before the token actually expires
        private const int _refreshMarginSeconds = 60;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _apiUrl;
        private readonly Func<STT.Task<TokenResponse>> _getToken;
        private CasterApiClient _client;
        private HttpClient _httpClient;
        private DateTime _refreshAt;
        // Caster writes RunStatus by its EnumMember names; the generated client cannot read two of them
        private static readonly Dictionary<string, RunStatus> _runStatusNames = typeof(RunStatus)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .SelectMany(f => new[] { f.Name, f.GetCustomAttribute<EnumMemberAttribute>()?.Value ?? f.Name }
                .Distinct()
                .Select(name => (name, status: (RunStatus)f.GetValue(null))))
            .ToDictionary(x => x.name, x => x.status, StringComparer.OrdinalIgnoreCase);

        // no token yet: the first GetAsync fetches one, so it is fresh when the Caster work starts
        public CasterClientSource(IHttpClientFactory httpClientFactory, string apiUrl, Func<STT.Task<TokenResponse>> getToken)
        {
            _httpClientFactory = httpClientFactory;
            _apiUrl = apiUrl;
            _getToken = getToken;
        }

        public async STT.Task<CasterApiClient> GetAsync(CancellationToken ct)
        {
            if (DateTime.UtcNow >= _refreshAt)
            {
                ct.ThrowIfCancellationRequested();
                SetToken(await _getToken());
            }
            return _client;
        }

        // Read run statuses from the raw JSON (CRU-3090); see IntegrationCasterExtensions.IgnoreStateErrorBodyAsync
        // for why, and for when this can go.
        public async STT.Task<RunStatus> GetRunStatusAsync(Guid runId, CancellationToken ct)
        {
            using var document = await GetJsonAsync($"api/runs/{runId}", ct);
            return ReadStatus(document.RootElement);
        }

        public async STT.Task<List<(Guid Id, RunStatus Status)>> GetWorkspaceRunStatusesAsync(Guid workspaceId, CancellationToken ct)
        {
            using var document = await GetJsonAsync($"api/workspaces/{workspaceId}/runs", ct);
            return document.RootElement.EnumerateArray()
                .Select(run => (run.GetProperty("id").GetGuid(), ReadStatus(run)))
                .ToList();
        }

        private async STT.Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
        {
            await GetAsync(ct);
            using var response = await _httpClient.GetAsync(path, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new Caster.Api.Client.ApiException($"Caster GET {path} answered {(int)response.StatusCode}.", (int)response.StatusCode, body, null, null);
            return JsonDocument.Parse(body);
        }

        private static RunStatus ReadStatus(JsonElement run)
        {
            var status = run.GetProperty("status");
            if (status.ValueKind == JsonValueKind.Number)
                return (RunStatus)status.GetInt32();
            var name = status.GetString();
            return _runStatusNames.TryGetValue(name, out var value)
                ? value
                : throw new Exception($"Caster returned an unknown run status \"{name}\".");
        }

        private void SetToken(TokenResponse tokenResponse)
        {
            if (tokenResponse.IsError)
                throw new Exception($"Could not get a token for Caster: {tokenResponse.Error}");
            _httpClient = ApiClientsExtensions.GetHttpClient(_httpClientFactory, _apiUrl, tokenResponse);
            _client = new CasterApiClient(_httpClient);
            // ExpiresIn is 0 when the identity provider omits it; then refresh on every call
            _refreshAt = DateTime.UtcNow.AddSeconds(Math.Max(0, tokenResponse.ExpiresIn - _refreshMarginSeconds));
        }
    }
}
