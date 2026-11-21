// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Automation;
using Blueprint.Api.Data.Enumerations;
using Xunit;

namespace Blueprint.Api.Tests.Automation;

/// <summary>The embedded action catalog, which the task form is built from and a task's parameters are checked against.</summary>
public class AutomationActionCatalogTests
{
    private readonly AutomationActionCatalog _catalog = new();

    [Fact]
    public void EveryAction_HasOneEntry()
    {
        Assert.Equal(
            Enum.GetValues<AutomationAction>().OrderBy(a => a),
            _catalog.Actions.Select(a => a.Action).OrderBy(a => a));
    }

    [Fact]
    public void EveryParameterKey_IsUniqueWithinItsAction()
    {
        Assert.All(_catalog.Actions, a =>
            Assert.Equal(a.Parameters.Count, a.Parameters.Select(p => p.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count()));
    }

    /// <summary>A VM action takes its VM from the run, so its parameters never name one.</summary>
    [Fact]
    public void NoAction_AsksForTheVm()
    {
        Assert.DoesNotContain(_catalog.Actions.SelectMany(a => a.Parameters), p =>
            p.Key.Equals(AutomationParameterBinder.Moid, StringComparison.OrdinalIgnoreCase) ||
            p.Key.Equals(AutomationParameterBinder.VmName, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(AutomationAction.guest_process_run, "Password")]
    [InlineData(AutomationAction.core_remote, "PrivateKey")]
    [InlineData(AutomationAction.http_post, "Headers")]
    public void Passwords_Keys_AndHeaders_AreSecret(AutomationAction action, string key)
    {
        Assert.Contains(key, _catalog.SecretKeys(action));
    }

    [Fact]
    public void Normalize_KeepsTheActionsOwnKeys_UnderTheCatalogsSpelling_WithoutSecretsOrEmptyValues()
    {
        var normalized = _catalog.Normalize(AutomationAction.guest_process_run, new Dictionary<string, string>
        {
            ["commandtext"] = "/bin/true",
            ["Password"] = "hunter2",
            ["CommandArgs"] = "",
            ["Moid"] = "{moid}",
            ["Unknown"] = "x",
        });

        Assert.Equal(new Dictionary<string, string> { ["CommandText"] = "/bin/true" }, normalized);
    }

    [Fact]
    public void Validate_NamesEachMissingRequiredParameter()
    {
        var errors = _catalog.Validate(AutomationAction.send_email, new Dictionary<string, string> { ["Subject"] = "x" }, []);

        Assert.Equal(["To is required.", "Message is required."], errors);
    }

    /// <summary>A required secret counts as set when the task already stores it.</summary>
    [Fact]
    public void Validate_CountsAStoredSecretAsSet()
    {
        var parameters = new Dictionary<string, string> { ["Hosts"] = "h", ["Username"] = "u", ["Cmd"] = "ls" };

        Assert.Empty(_catalog.Validate(AutomationAction.core_remote, parameters, ["PrivateKey"]));
    }

    [Theory]
    [InlineData("MoveNumber", "two", "Move number must be a whole number.")]
    [InlineData("InjectNumber", "1.5", "Inject number must be a whole number.")]
    public void Validate_RefusesANumberThatIsNotOne(string key, string value, string error)
    {
        var parameters = new Dictionary<string, string> { ["MoveNumber"] = "1", ["InjectNumber"] = "0", [key] = value };

        Assert.Equal([error], _catalog.Validate(AutomationAction.gallery_move_inject_set, parameters, []));
    }

    [Fact]
    public void Validate_RefusesAChoiceTheParameterDoesNotOffer()
    {
        var parameters = new Dictionary<string, string> { ["EmailTo"] = "a@b.c", ["Subject"] = "s", ["Message"] = "m", ["Mime"] = "rtf" };

        Assert.Equal(["Format must be one of plain, html."], _catalog.Validate(AutomationAction.send_email, parameters, []));
    }

    [Fact]
    public void Validate_RefusesASituationTimeThatIsNotADate()
    {
        var parameters = new Dictionary<string, string> { ["SituationTime"] = "soon", ["SituationDescription"] = "d" };

        Assert.Equal(["Situation time must be a date and time."], _catalog.Validate(AutomationAction.cite_situation_update, parameters, []));
    }
}
