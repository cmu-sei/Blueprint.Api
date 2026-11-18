// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Renci.SshNet;

namespace Blueprint.Api.Automation.Handlers
{
    /// <summary>
    /// core_remote, linux_file_touch and linux_rm, over SSH to the hosts Automation:Ssh:AllowedHosts names. Ported from
    /// Steamfitter's SshService. The output has a section per host; a host that errors or exits non-zero fails the task.
    /// </summary>
    public class SshActionHandler : IAutomationActionHandler
    {
        private readonly IOutboundRequestGuard _guard;
        private readonly IOptionsMonitor<AutomationOptions> _options;
        private readonly ILogger<SshActionHandler> _logger;

        public SshActionHandler(IOutboundRequestGuard guard, IOptionsMonitor<AutomationOptions> options, ILogger<SshActionHandler> logger)
        {
            _guard = guard;
            _options = options;
            _logger = logger;
        }

        public IReadOnlyCollection<AutomationAction> Actions { get; } = new[]
        {
            AutomationAction.core_remote, AutomationAction.linux_file_touch, AutomationAction.linux_rm,
        };

        public Task<ActionOutcome> ExecuteAsync(AutomationActionContext context, CancellationToken ct)
        {
            var command = context.Action switch
            {
                AutomationAction.core_remote => BuildShellCommand(context.Get("Cwd"), context.Get("Env"), false, context.Get("Cmd")),
                AutomationAction.linux_file_touch => BuildShellCommand(context.Get("Cwd"), context.Get("Env"), context.GetBool("Sudo"),
                    $"touch {ShellQuote(context.Get("File"))}"),
                AutomationAction.linux_rm => BuildShellCommand(context.Get("Cwd"), context.Get("Env"), context.GetBool("Sudo"), RmCommand(context)),
                _ => throw new AutomationConfigurationException($"{context.Action} is not an SSH action."),
            };
            return RunOnHostsAsync(context.Get("Hosts"), context.Get("Port"), context.Get("Username"), context.Get("PrivateKey"), command, ct);
        }

        private static string RmCommand(AutomationActionContext context)
        {
            var rm = new StringBuilder("rm");
            if (context.GetBool("Recursive")) rm.Append(" -r");
            if (context.GetBool("Force")) rm.Append(" -f");
            if (!string.IsNullOrEmpty(context.Get("Args"))) rm.Append(' ').Append(context.Get("Args"));
            return $"{rm} {ShellQuote(context.Get("Target"))}";
        }

        private async Task<ActionOutcome> RunOnHostsAsync(string hostsCsv, string portText, string username, string privateKey, string command, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(hostsCsv))
                throw new AutomationConfigurationException("Hosts is required.");
            if (string.IsNullOrWhiteSpace(username))
                throw new AutomationConfigurationException("Username is required.");
            var hosts = hostsCsv.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var host in hosts)
                _guard.CheckSshHost(host);
            var port = int.TryParse(portText, out var p) && p > 0 && p < 65536 ? p : 22;
            var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.CurrentValue.Ssh.CommandTimeoutSeconds));
            using var key = PrivateKey(privateKey);
            var output = new StringBuilder();
            var failed = false;
            foreach (var host in hosts)
            {
                output.AppendLine($"[{host}]");
                try
                {
                    using var client = new SshClient(host, port, username, key);
                    client.ConnectionInfo.Timeout = timeout;
                    await client.ConnectAsync(ct);
                    using var cmd = client.CreateCommand(command);
                    cmd.CommandTimeout = timeout;
                    await cmd.ExecuteAsync(ct);
                    output.AppendLine(cmd.Result);
                    if (cmd.ExitStatus != 0)
                    {
                        failed = true;
                        output.AppendLine($"(exit {cmd.ExitStatus}) {cmd.Error}");
                    }
                    client.Disconnect();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "An SSH task failed on host {Host}", host);
                    failed = true;
                    output.AppendLine($"(error) {ex.Message}");
                }
            }
            return new ActionOutcome(output.ToString().TrimEnd(), failed ? false : null);
        }

        private PrivateKeyFile PrivateKey(string overrideKey)
        {
            var options = _options.CurrentValue.Ssh;
            var key = string.IsNullOrWhiteSpace(overrideKey) ? options.DefaultPrivateKey : overrideKey;
            var passphrase = options.DefaultPrivateKeyPassphrase;
            if (!string.IsNullOrEmpty(key))
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(key));
                return string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(stream) : new PrivateKeyFile(stream, passphrase);
            }
            if (!string.IsNullOrEmpty(options.DefaultPrivateKeyPath))
                return string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(options.DefaultPrivateKeyPath) : new PrivateKeyFile(options.DefaultPrivateKeyPath, passphrase);
            throw new AutomationConfigurationException("No SSH private key: set the task's private key, or Automation:Ssh:DefaultPrivateKey.");
        }

        private static string ShellQuote(string s) => string.IsNullOrEmpty(s) ? "''" : "'" + s.Replace("'", "'\\''") + "'";

        private static string BuildShellCommand(string cwd, string env, bool sudo, string cmd)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(env) && env.Trim() != "{}")
                foreach (var (k, v) in ParseEnvPairs(env))
                    sb.Append(k).Append('=').Append(ShellQuote(v)).Append(' ');
            if (!string.IsNullOrWhiteSpace(cwd))
                sb.Append("cd ").Append(ShellQuote(cwd)).Append(" && ");
            if (sudo) sb.Append("sudo ");
            sb.Append(cmd);
            return sb.ToString();
        }

        /// <summary>KEY=val,KEY2=val2 or a JSON object; a name that is not a shell variable name is refused.</summary>
        private static IEnumerable<(string Key, string Value)> ParseEnvPairs(string env)
        {
            var pairs = new List<(string, string)>();
            env = env.Trim();
            if (env.StartsWith("{") && env.EndsWith("}"))
            {
                using var doc = JsonDocument.Parse(env);
                pairs.AddRange(doc.RootElement.EnumerateObject().Select(p => (p.Name, p.Value.ToString())));
            }
            else
            {
                foreach (var pair in env.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var idx = pair.IndexOf('=');
                    if (idx > 0)
                        pairs.Add((pair.Substring(0, idx).Trim(), pair.Substring(idx + 1).Trim()));
                }
            }
            foreach (var (key, _) in pairs)
                if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^[A-Za-z_][A-Za-z0-9_]*$"))
                    throw new AutomationConfigurationException($"{key} is not an environment variable name.");
            return pairs;
        }
    }
}
