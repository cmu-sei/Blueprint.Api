// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Blueprint.Api.Automation
{
    /// <summary>The values the <c>{name}</c> markers in a task's parameters stand for, as one result runs.</summary>
    public sealed record AutomationSubstitutions(
        Guid MselId,
        Guid? PlayerViewId,
        Guid? CiteEvaluationId,
        Guid? GalleryExhibitId,
        int? MoveNumber,
        int? GroupNumber);

    /// <summary>
    /// The parameters one result runs with. The VM is never a task's to name: <c>Moid</c> and <c>VmName</c> are set to the
    /// VM the result was bound to, which is always one the task's mask matched in the run's Player view, whatever the
    /// task stored. The markers <c>{mselId}</c>, <c>{playerViewId}</c>, <c>{citeEvaluationId}</c>, <c>{galleryExhibitId}</c>,
    /// <c>{moveNumber}</c>, <c>{groupNumber}</c>, <c>{moid}</c> and <c>{VmName}</c> are replaced, so a hand written http task
    /// can address the MSEL's own integrations, as Steamfitter tasks Blueprint pushed did.
    /// </summary>
    public static class AutomationParameterBinder
    {
        public const string Moid = "Moid";
        public const string VmName = "VmName";
        public const string Redacted = "********";
        private static readonly Regex Marker = new(@"\{(\w+)\}", RegexOptions.Compiled);

        public static Dictionary<string, string> Bind(
            IReadOnlyDictionary<string, string> parameters,
            IReadOnlyDictionary<string, string> secrets,
            AutomationSubstitutions substitutions,
            Guid? vmId,
            string vmName)
        {
            var values = new Dictionary<string, string>
            {
                ["mselid"] = substitutions.MselId.ToString(),
                ["playerviewid"] = substitutions.PlayerViewId?.ToString() ?? string.Empty,
                ["citeevaluationid"] = substitutions.CiteEvaluationId?.ToString() ?? string.Empty,
                ["galleryexhibitid"] = substitutions.GalleryExhibitId?.ToString() ?? string.Empty,
                ["movenumber"] = substitutions.MoveNumber?.ToString() ?? string.Empty,
                ["groupnumber"] = substitutions.GroupNumber?.ToString() ?? string.Empty,
                ["moid"] = vmId?.ToString() ?? string.Empty,
                ["vmname"] = vmName ?? string.Empty,
            };
            var bound = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in new[] { parameters, secrets })
                foreach (var (key, value) in source ?? new Dictionary<string, string>())
                    bound[key] = value == null ? null : Marker.Replace(value, m => values.TryGetValue(m.Groups[1].Value.ToLowerInvariant(), out var v) ? v : m.Value);
            bound[Moid] = vmId?.ToString() ?? string.Empty;
            bound[VmName] = vmName ?? string.Empty;
            return bound;
        }

        /// <summary>The parameters as a result records them: JSON, with every secret's value redacted.</summary>
        public static string Describe(IReadOnlyDictionary<string, string> bound, IReadOnlySet<string> secretKeys)
        {
            var shown = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in bound)
                shown[key] = secretKeys.Contains(key) && !string.IsNullOrEmpty(value) ? Redacted : value;
            return JsonSerializer.Serialize(shown);
        }
    }

    /// <summary>A VM of a Player view.</summary>
    public sealed record AutomationVm(Guid Id, string Name);

    /// <summary>
    /// The VMs of a Player view a task's mask matches, as Steamfitter matched them: a mask that is a comma separated list
    /// of VM ids matches those of the view's VMs, and any other mask matches the VMs whose names contain it, ignoring case.
    /// Every VM of the view counts, personal ones included: Blueprint's service account holds the VM permissions at the
    /// system level but is on none of the view's teams.
    /// </summary>
    public interface IAutomationVmResolver
    {
        Task<IReadOnlyList<AutomationVm>> ResolveAsync(Guid playerViewId, string vmMask, CancellationToken ct);
    }

    public class AutomationVmResolver : IAutomationVmResolver
    {
        private readonly IAutomationClientFactory _clients;

        public AutomationVmResolver(IAutomationClientFactory clients)
        {
            _clients = clients;
        }

        public async Task<IReadOnlyList<AutomationVm>> ResolveAsync(Guid playerViewId, string vmMask, CancellationToken ct)
        {
            var client = await _clients.PlayerVmAsync(ct);
            var vms = (await client.GetAllViewVmsAsync(playerViewId, ct)).Select(v => new AutomationVm(v.Id, v.Name));
            return Match(vms, vmMask);
        }

        public static IReadOnlyList<AutomationVm> Match(IEnumerable<AutomationVm> vms, string vmMask)
        {
            if (string.IsNullOrWhiteSpace(vmMask))
                return Array.Empty<AutomationVm>();
            var parts = vmMask.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var ids = parts.Select(p => Guid.TryParse(p, out var id) ? id : (Guid?)null).Where(id => id.HasValue).Select(id => id.Value).ToHashSet();
            if (ids.Count > 0)
                return vms.Where(v => ids.Contains(v.Id)).OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var name = vmMask.Trim();
            return vms.Where(v => (v.Name ?? string.Empty).Contains(name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
