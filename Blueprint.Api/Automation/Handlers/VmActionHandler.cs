// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Player.Vm.Api;

namespace Blueprint.Api.Automation.Handlers
{
    /// <summary>
    /// The actions on one VM of a Player view, through the Player VM API: power, snapshots, guest processes and guest
    /// files, on vSphere and Proxmox VMs. Ported from Steamfitter's VmOperationsService. A guest process that exits
    /// with a non-zero code fails the task, whatever its output.
    /// </summary>
    public class VmActionHandler : IAutomationActionHandler
    {
        private static readonly TimeSpan ProviderCacheTtl = TimeSpan.FromMinutes(5);
        private readonly IAutomationClientFactory _clients;
        private readonly IOutboundRequestGuard _guard;
        private readonly TimeProvider _time;
        private readonly ConcurrentDictionary<Guid, (VmType Type, DateTimeOffset Until)> _providers = new();

        public VmActionHandler(IAutomationClientFactory clients, IOutboundRequestGuard guard, TimeProvider time)
        {
            _clients = clients;
            _guard = guard;
            _time = time;
        }

        public IReadOnlyCollection<AutomationAction> Actions { get; } = new[]
        {
            AutomationAction.vm_hw_power_on, AutomationAction.vm_hw_power_off,
            AutomationAction.vm_snapshot_create, AutomationAction.vm_snapshot_revert, AutomationAction.vm_snapshot_delete,
            AutomationAction.guest_process_run, AutomationAction.guest_process_run_fast,
            AutomationAction.guest_file_read, AutomationAction.guest_file_upload_content, AutomationAction.guest_file_upload_file,
        };

        public async Task<ActionOutcome> ExecuteAsync(AutomationActionContext context, CancellationToken ct)
        {
            var vmId = context.VmId ?? throw new AutomationConfigurationException("The action runs on a VM, and the task's mask matched none.");
            var client = await _clients.PlayerVmAsync(ct);
            var type = await ProviderAsync(client, vmId, ct);
            return context.Action switch
            {
                AutomationAction.vm_hw_power_on => Done(type switch
                {
                    VmType.Vsphere => await client.PowerOnVsphereVirtualMachineAsync(vmId, ct),
                    VmType.Proxmox => await client.PowerOnProxmoxVirtualMachineAsync(vmId, ct),
                    _ => throw Unsupported(type, vmId, context.Action),
                }),
                AutomationAction.vm_hw_power_off => Done(type switch
                {
                    VmType.Vsphere => await client.PowerOffVsphereVirtualMachineAsync(vmId, ct),
                    VmType.Proxmox => await client.PowerOffProxmoxVirtualMachineAsync(vmId, ct),
                    _ => throw Unsupported(type, vmId, context.Action),
                }),
                AutomationAction.vm_snapshot_create => Done(type switch
                {
                    VmType.Vsphere => await client.CreateVsphereVirtualMachineSnapshotAsync(vmId, new CreateVsphereVirtualMachineSnapshot
                    {
                        SnapshotName = context.Get("SnapshotName"),
                        Description = context.Get("Description"),
                        IncludeMemory = context.GetBool("IncludeRam"),
                    }, ct),
                    VmType.Proxmox => await client.CreateProxmoxVirtualMachineSnapshotAsync(vmId, new CreateProxmoxVirtualMachineSnapshot
                    {
                        SnapshotName = context.Get("SnapshotName"),
                        Description = context.Get("Description"),
                        IncludeRam = context.GetBool("IncludeRam"),
                    }, ct),
                    _ => throw Unsupported(type, vmId, context.Action),
                }),
                AutomationAction.vm_snapshot_revert => await RevertAsync(client, type, vmId, context, ct),
                AutomationAction.vm_snapshot_delete => Done(type switch
                {
                    VmType.Vsphere => await client.DeleteVsphereVirtualMachineSnapshotAsync(vmId, context.Get("SnapshotName"), ct),
                    VmType.Proxmox => await client.DeleteProxmoxVirtualMachineSnapshotAsync(vmId, context.Get("SnapshotName"), ct),
                    _ => throw Unsupported(type, vmId, context.Action),
                }),
                AutomationAction.guest_process_run => await RunAsync(client, type, vmId, context, ct),
                AutomationAction.guest_process_run_fast => await RunFastAsync(client, type, vmId, context, ct),
                AutomationAction.guest_file_read => Done(type switch
                {
                    VmType.Vsphere => await client.ReadGuestFileFromVsphereVirtualMachineAsync(vmId, new ReadGuestFileFromVsphereVirtualMachine
                    {
                        Username = context.Get("Username"),
                        Password = context.Get("Password"),
                        GuestFilePath = context.Get("GuestFilePath"),
                    }, ct),
                    VmType.Proxmox => await client.ReadGuestFileFromProxmoxVirtualMachineAsync(vmId, new ReadGuestFileFromProxmoxVirtualMachine
                    {
                        GuestFilePath = context.Get("GuestFilePath"),
                    }, ct),
                    _ => throw Unsupported(type, vmId, context.Action),
                }),
                AutomationAction.guest_file_upload_content => await UploadContentAsync(client, type, vmId, context, ct),
                AutomationAction.guest_file_upload_file => await UploadFileAsync(client, type, vmId, context, ct),
                _ => throw new AutomationConfigurationException($"{context.Action} is not a VM action."),
            };
        }

        private static ActionOutcome Done(string output) => new(output ?? string.Empty);

        private static async Task<ActionOutcome> RevertAsync(IPlayerVmApiClient client, VmType type, Guid vmId, AutomationActionContext context, CancellationToken ct)
        {
            var snapshot = context.Get("SnapshotName");
            switch (type)
            {
                case VmType.Vsphere:
                    await client.RevertToVsphereVirtualMachineSnapshotAsync(vmId, new RevertToVsphereVirtualMachineSnapshot { SnapshotId = snapshot }, ct);
                    return Done($"Reverted to {snapshot}.");
                case VmType.Proxmox:
                    return Done(await client.RevertProxmoxVirtualMachineSnapshotAsync(vmId, snapshot, ct));
                default:
                    throw Unsupported(type, vmId, context.Action);
            }
        }

        private static async Task<ActionOutcome> RunAsync(IPlayerVmApiClient client, VmType type, Guid vmId, AutomationActionContext context, CancellationToken ct)
        {
            var timeout = context.GetInt("TimeoutSeconds");
            GuestProcessResult result = type switch
            {
                VmType.Vsphere => await client.RunGuestProcessOnVsphereVirtualMachineAsync(vmId, new RunGuestProcessOnVsphereVirtualMachine
                {
                    Username = context.Get("Username"),
                    Password = context.Get("Password"),
                    ProgramPath = context.Get("CommandText"),
                    Arguments = context.Get("CommandArgs"),
                    WorkingDirectory = context.Get("CommandWorkDirectory"),
                    TimeoutSeconds = timeout,
                }, ct),
                VmType.Proxmox => await client.RunGuestProcessOnProxmoxVirtualMachineAsync(vmId, new RunGuestProcessOnProxmoxVirtualMachine
                {
                    ProgramPath = context.Get("CommandText"),
                    Arguments = context.Get("CommandArgs"),
                    WorkingDirectory = context.Get("CommandWorkDirectory"),
                    TimeoutSeconds = timeout,
                }, ct),
                _ => throw Unsupported(type, vmId, context.Action),
            };
            var output = result?.Output ?? string.Empty;
            if (result != null && !result.Success)
            {
                var detail = string.IsNullOrEmpty(result.Error) ? output : $"{output}\n{result.Error}";
                throw new AutomationActionFailedException($"exit code {result.ExitCode}\n{detail}".TrimEnd());
            }
            return Done(output);
        }

        private static async Task<ActionOutcome> RunFastAsync(IPlayerVmApiClient client, VmType type, Guid vmId, AutomationActionContext context, CancellationToken ct)
        {
            long pid = type switch
            {
                VmType.Vsphere => await client.RunGuestProcessFastOnVsphereVirtualMachineAsync(vmId, new RunGuestProcessFastOnVsphereVirtualMachine
                {
                    Username = context.Get("Username"),
                    Password = context.Get("Password"),
                    ProgramPath = context.Get("CommandText"),
                    Arguments = context.Get("CommandArgs"),
                    WorkingDirectory = context.Get("CommandWorkDirectory"),
                }, ct),
                VmType.Proxmox => await client.RunGuestProcessFastOnProxmoxVirtualMachineAsync(vmId, new RunGuestProcessFastOnProxmoxVirtualMachine
                {
                    ProgramPath = context.Get("CommandText"),
                    Arguments = context.Get("CommandArgs"),
                    WorkingDirectory = context.Get("CommandWorkDirectory"),
                }, ct),
                _ => throw Unsupported(type, vmId, context.Action),
            };
            return Done(pid.ToString());
        }

        private static async Task<ActionOutcome> UploadContentAsync(IPlayerVmApiClient client, VmType type, Guid vmId, AutomationActionContext context, CancellationToken ct)
        {
            var path = context.Get("GuestFilePath") ?? string.Empty;
            var fileName = Path.GetFileName(path.TrimEnd('/', '\\'));
            var directory = string.IsNullOrEmpty(fileName) ? path : path.Substring(0, path.Length - fileName.Length);
            using var content = new MemoryStream(Encoding.UTF8.GetBytes(context.Get("GuestFileContent") ?? string.Empty));
            return Done(await UploadAsync(client, type, vmId, context, directory, string.IsNullOrEmpty(fileName) ? "uploaded.txt" : fileName, content, ct));
        }

        private async Task<ActionOutcome> UploadFileAsync(IPlayerVmApiClient client, VmType type, Guid vmId, AutomationActionContext context, CancellationToken ct)
        {
            var localPath = _guard.ResolveLocalFile(context.Get("FilePath"));
            if (!File.Exists(localPath))
                throw new AutomationConfigurationException($"The server file {context.Get("FilePath")} is not there.");
            await using var content = File.OpenRead(localPath);
            return Done(await UploadAsync(client, type, vmId, context, context.Get("GuestFilePath"), Path.GetFileName(localPath), content, ct));
        }

        private static async Task<string> UploadAsync(IPlayerVmApiClient client, VmType type, Guid vmId, AutomationActionContext context,
            string directory, string fileName, Stream content, CancellationToken ct)
        {
            var files = new[] { new FileParameter(content, fileName, "application/octet-stream") };
            return type switch
            {
                VmType.Vsphere => await client.UploadFileToVsphereVirtualMachineAsync(
                    vmId, context.Get("Username") ?? string.Empty, context.Get("Password") ?? string.Empty, directory ?? string.Empty, files, ct),
                VmType.Proxmox => await client.UploadFileToProxmoxVirtualMachineAsync(vmId, directory ?? string.Empty, files, ct),
                _ => throw Unsupported(type, vmId, context.Action),
            };
        }

        /// <summary>The VM's provider, from the Player VM API, kept for five minutes.</summary>
        private async Task<VmType> ProviderAsync(IPlayerVmApiClient client, Guid vmId, CancellationToken ct)
        {
            var now = _time.GetUtcNow();
            if (_providers.TryGetValue(vmId, out var cached) && cached.Until > now)
                return cached.Type;
            var vm = await client.GetVmAsync(vmId, ct);
            _providers[vmId] = (vm.Type, now.Add(ProviderCacheTtl));
            return vm.Type;
        }

        private static AutomationConfigurationException Unsupported(VmType type, Guid vmId, AutomationAction action) =>
            new($"VM {vmId} is a {type} VM, which does not support {action}.");
    }
}
