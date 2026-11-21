// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Text.Json;
using Blueprint.Api.Automation;
using Xunit;

namespace Blueprint.Api.Tests.Automation;

/// <summary>How a task's passwords, keys and headers are stored: encrypted with a key that is not in the database.</summary>
public class AutomationSecretProtectorTests
{
    private static AutomationSecretProtector Protector(string keyId = "k1", string key = null, Dictionary<string, string> more = null) =>
        new(AutomationTestOptions.Automation(o =>
        {
            o.SecretKeys = new Dictionary<string, string>(more ?? new()) { [keyId] = key ?? AutomationTestOptions.NewKey() };
            o.ActiveSecretKeyId = keyId;
        }));

    [Fact]
    public void Protect_ThenUnprotect_GivesTheSecretsBack()
    {
        var protector = Protector();

        var stored = protector.Protect(new Dictionary<string, string> { ["Password"] = "hunter2", ["Headers"] = "{\"X\":\"y\"}" });

        Assert.Equal(new Dictionary<string, string> { ["Password"] = "hunter2", ["Headers"] = "{\"X\":\"y\"}" }, protector.Unprotect(stored));
    }

    [Fact]
    public void Protect_StoresNoSecretInTheClear()
    {
        var stored = Protector().Protect(new Dictionary<string, string> { ["Password"] = "hunter2" });

        Assert.DoesNotContain("hunter2", stored);
        Assert.StartsWith("enc:v1:k1:", JsonSerializer.Deserialize<Dictionary<string, string>>(stored)["Password"]);
    }

    /// <summary>The parameter's name is bound into its ciphertext, so a value moved to another parameter does not decrypt.</summary>
    [Fact]
    public void Unprotect_OfAValueMovedToAnotherParameter_Throws()
    {
        var protector = Protector();
        var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(
            protector.Protect(new Dictionary<string, string> { ["Password"] = "hunter2" }));

        var moved = JsonSerializer.Serialize(new Dictionary<string, string> { ["PrivateKey"] = stored["Password"] });

        Assert.Throws<AutomationConfigurationException>(() => protector.Unprotect(moved));
    }

    [Fact]
    public void Update_KeepsWhatItDoesNotName_SetsWhatItDoes_AndRemovesWhatItEmpties()
    {
        var protector = Protector();
        var stored = protector.Protect(new Dictionary<string, string> { ["Password"] = "old", ["Headers"] = "h", ["PrivateKey"] = "k" });

        var updated = protector.Update(stored, new Dictionary<string, string> { ["password"] = "new", ["Headers"] = "" });

        Assert.Equal(new Dictionary<string, string> { ["password"] = "new", ["PrivateKey"] = "k" }, protector.Unprotect(updated));
    }

    [Fact]
    public void Update_ThatRemovesTheLastSecret_StoresNothing()
    {
        var protector = Protector();
        var stored = protector.Protect(new Dictionary<string, string> { ["Password"] = "old" });

        Assert.Null(protector.Update(stored, new Dictionary<string, string> { ["Password"] = null }));
    }

    [Fact]
    public void WithoutAKey_NothingCanBeProtected()
    {
        var protector = new AutomationSecretProtector(AutomationTestOptions.Automation());

        Assert.False(protector.IsConfigured);
        Assert.Throws<AutomationConfigurationException>(() => protector.Protect(new Dictionary<string, string> { ["Password"] = "x" }));
    }

    /// <summary>A value migrated from a Steamfitter task is in the clear until it is protected again.</summary>
    [Fact]
    public void Unprotect_ReturnsAValueStoredInTheClear_AndNeedsProtectingSaysSo()
    {
        var protector = Protector();
        var clear = JsonSerializer.Serialize(new Dictionary<string, string> { ["Password"] = "legacy" });

        Assert.Equal("legacy", protector.Unprotect(clear)["Password"]);
        Assert.True(protector.NeedsProtecting(clear));
        Assert.False(protector.NeedsProtecting(protector.Reprotect(clear)));
    }

    /// <summary>A rotated key: what an old key encrypted still decrypts, and is encrypted again with the active key.</summary>
    [Fact]
    public void AfterAKeyRotation_OldValuesDecrypt_AndReprotectMovesThemToTheActiveKey()
    {
        var oldKey = AutomationTestOptions.NewKey();
        var stored = Protector("old", oldKey).Protect(new Dictionary<string, string> { ["Password"] = "p" });
        var rotated = Protector("new", more: new() { ["old"] = oldKey });

        Assert.True(rotated.NeedsProtecting(stored));
        var reprotected = rotated.Reprotect(stored);
        Assert.Equal("p", rotated.Unprotect(reprotected)["Password"]);
        Assert.StartsWith("enc:v1:new:", JsonSerializer.Deserialize<Dictionary<string, string>>(reprotected)["Password"]);
    }

    [Fact]
    public void Unprotect_WithAKeyThatIsNoLongerConfigured_Throws()
    {
        var stored = Protector("gone").Protect(new Dictionary<string, string> { ["Password"] = "p" });

        Assert.Throws<AutomationConfigurationException>(() => Protector("other").Unprotect(stored));
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("c2hvcnQ=")]
    public void AKeyThatIsNot32Base64Bytes_IsRefused(string key)
    {
        Assert.Throws<AutomationConfigurationException>(() => Protector(key: key).Protect(new Dictionary<string, string> { ["Password"] = "p" }));
    }
}
