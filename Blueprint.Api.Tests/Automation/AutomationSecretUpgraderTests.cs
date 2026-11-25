// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Blueprint.Api.Automation;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Blueprint.Api.Tests.Automation;

/// <summary>
/// The startup pass that encrypts the task secrets stored in the clear, which the Steamfitter migration carries over,
/// and re-encrypts those under a key that is no longer the active one.
/// </summary>
public class AutomationSecretUpgraderTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    private static readonly string OldKey = AutomationTestOptions.NewKey();
    private static readonly string NewKey = AutomationTestOptions.NewKey();

    [Fact]
    public async Task Upgrade_EncryptsASecretStoredInTheClear()
    {
        var task = await SeedTask("{\"Headers\":\"{\\\"X-Team\\\":\\\"blue\\\"}\"}");
        var protector = Protector(("k1", OldKey));

        var count = await Upgrader().UpgradeAsync(Db, protector, Ct);

        Assert.Equal(1, count);
        var stored = await Stored(task);
        Assert.DoesNotContain("X-Team", stored);
        Assert.StartsWith("enc:v1:k1:", Secrets(stored)["Headers"]);
        Assert.Equal("{\"X-Team\":\"blue\"}", protector.Unprotect(stored)["Headers"]);
    }

    /// <summary>After a rotation a secret is encrypted again under the active key, and still reads back.</summary>
    [Fact]
    public async Task Upgrade_ReencryptsASecretUnderAKeyThatIsNoLongerActive()
    {
        var before = Protector(("k1", OldKey));
        var task = await SeedTask(before.Protect(new Dictionary<string, string> { ["Password"] = "hunter2" }));
        var after = Protector(("k2", NewKey), ("k1", OldKey));

        var count = await Upgrader().UpgradeAsync(Db, after, Ct);

        Assert.Equal(1, count);
        var stored = await Stored(task);
        Assert.StartsWith("enc:v1:k2:", Secrets(stored)["Password"]);
        Assert.Equal("hunter2", after.Unprotect(stored)["Password"]);
    }

    [Fact]
    public async Task Upgrade_LeavesASecretUnderTheActiveKeyAlone()
    {
        var protector = Protector(("k1", OldKey));
        var original = protector.Protect(new Dictionary<string, string> { ["Password"] = "hunter2" });
        var task = await SeedTask(original);

        Assert.Equal(0, await Upgrader().UpgradeAsync(Db, protector, Ct));
        Assert.Equal(original, await Stored(task));
    }

    /// <summary>With no key there is nothing to encrypt with: the secret stays as it is, to be encrypted once one is set.</summary>
    [Fact]
    public async Task Upgrade_WithNoKey_LeavesTheSecretAsItIs()
    {
        const string clear = "{\"Headers\":\"{}\"}";
        var task = await SeedTask(clear);

        Assert.Equal(0, await Upgrader().UpgradeAsync(Db, Protector(), Ct));
        Assert.Equal(clear, await Stored(task));
    }

    private async Task<AutomationTaskEntity> SeedTask(string secretParameters)
    {
        var msel = TestData.Msel();
        var scenarioEvent = TestData.ScenarioEvent(msel.Id);
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id, Data.Enumerations.AutomationAction.http_get);
        task.ActionParameters = new() { ["Url"] = "http://localhost:4720/api/health" };
        task.SecretParameters = secretParameters;
        await Seed(msel, scenarioEvent, task);
        return task;
    }

    private Task<string> Stored(AutomationTaskEntity task) =>
        ReadBack(db => db.AutomationTasks.AsNoTracking().Where(t => t.Id == task.Id).Select(t => t.SecretParameters).SingleAsync(Ct));

    private static Dictionary<string, string> Secrets(string stored) => JsonSerializer.Deserialize<Dictionary<string, string>>(stored);

    /// <summary>A protector whose first key is the active one.</summary>
    private static AutomationSecretProtector Protector(params (string Id, string Key)[] keys) =>
        new(AutomationTestOptions.Automation(o =>
        {
            o.SecretKeys = keys.ToDictionary(k => k.Id, k => k.Key);
            o.ActiveSecretKeyId = keys.Length == 0 ? "" : keys[0].Id;
        }));

    private static AutomationSecretUpgrader Upgrader() =>
        new(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Protector(), NullLogger<AutomationSecretUpgrader>.Instance);
}
