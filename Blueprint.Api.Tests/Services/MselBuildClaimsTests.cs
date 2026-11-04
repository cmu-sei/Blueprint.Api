// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Threading.Tasks;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Services;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary>Which tab builds each MSEL, and which of its steps are running.</summary>
public class MselBuildClaimsTests
{
    private static readonly Guid Ada = Guid.NewGuid();

    /// <summary>Tabs claiming a free build at the same moment: exactly one gets it, and the rest are refused.</summary>
    [Fact]
    public async Task Claim_GivesAFreeBuildToExactlyOneTab()
    {
        var claims = new MselBuildClaims();
        var mselId = Guid.NewGuid();
        for (var i = 0; i < 32; i++)
            claims.Connected($"tab-{i}");

        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() =>
        {
            try
            {
                claims.Claim(mselId, new MselBuildClaim($"tab-{i}", Ada, "Ada"), takeOver: false);
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        })));

        Assert.Single(results, won => won);
    }

    /// <summary>The tab with the build claims it again freely; a take-over returns the claim it replaced.</summary>
    [Fact]
    public void Claim_TakingOverReturnsTheTabThatHadIt()
    {
        var claims = Open("tab-a", "tab-b");
        var mselId = Guid.NewGuid();
        claims.Claim(mselId, new MselBuildClaim("tab-a", Ada, "Ada"), takeOver: false);

        Assert.Null(claims.Claim(mselId, new MselBuildClaim("tab-a", Ada, "Ada"), takeOver: false));
        var replaced = claims.Claim(mselId, new MselBuildClaim("tab-b", Ada, "Ada"), takeOver: true);

        Assert.Equal("tab-a", replaced.ConnectionId);
        Assert.Equal("tab-b", claims.Holder(mselId).ConnectionId);
    }

    /// <summary>A closed tab frees every build it had, and only those.</summary>
    [Fact]
    public void ReleaseConnection_FreesOnlyThatTabsBuilds()
    {
        var claims = Open("tab-a", "tab-b");
        Guid first = Guid.NewGuid(), second = Guid.NewGuid(), other = Guid.NewGuid();
        claims.Claim(first, new MselBuildClaim("tab-a", Ada, "Ada"), false);
        claims.Claim(second, new MselBuildClaim("tab-a", Ada, "Ada"), false);
        claims.Claim(other, new MselBuildClaim("tab-b", Ada, "Ada"), false);

        Assert.Equal(new[] { first, second }.Order(), claims.ReleaseConnection("tab-a").Order());
        Assert.Null(claims.Holder(first));
        Assert.Equal("tab-b", claims.Holder(other).ConnectionId);
        Assert.False(claims.Release(other, "tab-a"));
    }

    /// <summary>A step already running is refused until it ends, as when a tab that was taken over is still writing it.</summary>
    [Fact]
    public void Run_RefusesAStepThatIsAlreadyRunning()
    {
        var claims = new MselBuildClaims();
        var mselId = Guid.NewGuid();

        var step = claims.Run(mselId, "Injects", "The MSEL's injects are");
        var error = Assert.Throws<ConflictException>(() => claims.Run(mselId, "Injects", "The MSEL's injects are"));
        Assert.Equal("The MSEL's injects are already being written. Wait for it to finish.", error.Message);
        // another MSEL's step is its own
        claims.Run(Guid.NewGuid(), "Injects", "The MSEL's injects are").Dispose();
        step.Dispose();

        claims.Run(mselId, "Injects", "The MSEL's injects are").Dispose();
    }

    /// <summary>
    /// A tab that reconnects under a new connection takes its build back: the claim its closed connection held is free,
    /// and a claim is never shown as held by a closed connection.
    /// </summary>
    [Fact]
    public void Claim_HeldByAClosedConnection_IsFree()
    {
        var claims = Open("old", "new", "other");
        var mselId = Guid.NewGuid();
        claims.Claim(mselId, new MselBuildClaim("old", Ada, "Ada"), takeOver: false);

        claims.ReleaseConnection("old");
        // the claim came in after its connection closed: as when a request was already on its way
        claims.Claim(mselId, new MselBuildClaim("old", Ada, "Ada"), takeOver: false);
        Assert.Null(claims.Holder(mselId));

        Assert.Null(claims.Claim(mselId, new MselBuildClaim("new", Ada, "Ada"), takeOver: false));
        Assert.Equal("new", claims.Holder(mselId).ConnectionId);
        Assert.Throws<ConflictException>(() => claims.Claim(mselId, new MselBuildClaim("other", Ada, "Ada"), takeOver: false));
    }

    /// <summary>A request from a closed connection writes only while no open tab has the build, and claims nothing.</summary>
    [Fact]
    public void Claim_FromAClosedConnection_ClaimsNothing()
    {
        var claims = Open("open");
        var free = Guid.NewGuid();
        var held = Guid.NewGuid();
        claims.Claim(held, new MselBuildClaim("open", Ada, "Ada"), takeOver: false);

        Assert.Null(claims.Claim(free, new MselBuildClaim("closed", Ada, "Ada"), takeOver: false));
        Assert.Null(claims.Holder(free));
        Assert.Throws<ConflictException>(() => claims.Claim(held, new MselBuildClaim("closed", Ada, "Ada"), takeOver: true));
    }

    private static MselBuildClaims Open(params string[] connectionIds)
    {
        var claims = new MselBuildClaims();
        foreach (var id in connectionIds)
            claims.Connected(id);
        return claims;
    }
}
