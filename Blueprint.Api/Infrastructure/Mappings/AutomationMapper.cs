// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>Automation tasks', runs' and results' mappings. A task's secrets never leave as anything but their names.</summary>
[Mapper]
public static partial class AutomationMapper
{
    [MapProperty(nameof(AutomationTaskEntity.SecretParameters), nameof(AutomationTask.SecretParameterKeys), Use = nameof(MapSecretKeys))]
    [MapperIgnoreSource(nameof(AutomationTaskEntity.Msel))]
    [MapperIgnoreSource(nameof(AutomationTaskEntity.ScenarioEvent))]
    [MapperIgnoreSource(nameof(AutomationTaskEntity.TriggerTask))]
    [MapperIgnoreSource(nameof(AutomationTaskEntity.TriggeredTasks))]
    public static partial AutomationTask ToAutomationTask(this AutomationTaskEntity source);

    /// <remarks><see cref="AutomationRun.Results"/> holds whatever results the context has loaded.</remarks>
    [MapperIgnoreSource(nameof(AutomationRunEntity.Msel))]
    [MapperIgnoreSource(nameof(AutomationRunEntity.AutomationTask))]
    [MapperIgnoreSource(nameof(AutomationRunEntity.ConfigSnapshot))]
    [MapperIgnoreSource(nameof(AutomationRunEntity.FollowOnTaskIds))]
    [MapperIgnoreSource(nameof(AutomationRunEntity.LeaseOwner))]
    [MapperIgnoreSource(nameof(AutomationRunEntity.LeaseUntil))]
    public static partial AutomationRun ToAutomationRun(this AutomationRunEntity source);

    [MapperIgnoreSource(nameof(AutomationResultEntity.Run))]
    public static partial AutomationResult ToAutomationResult(this AutomationResultEntity source);

    private static List<string> MapSecretKeys(string secretParameters) =>
        string.IsNullOrWhiteSpace(secretParameters)
            ? []
            : (JsonSerializer.Deserialize<Dictionary<string, string>>(secretParameters) ?? new()).Keys.OrderBy(k => k).ToList();

    private static Dictionary<string, string> MapActionParameters(Dictionary<string, string> source) =>
        source == null ? [] : new Dictionary<string, string>(source);

    private static List<AutomationResult> MapResults(ICollection<AutomationResultEntity> source) =>
        source == null ? [] : source.OrderByDescending(r => r.Iteration).ThenBy(r => r.VmName).Select(r => r.ToAutomationResult()).ToList();
}
