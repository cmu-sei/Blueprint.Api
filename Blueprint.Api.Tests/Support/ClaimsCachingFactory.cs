// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Blueprint.Api.Tests.Support;

/// <summary>
/// A host with <c>UserClaimsService</c>'s claims cache switched on, as blueprint ships it, and an expiry
/// long enough that only an explicit invalidation can make a test see a change.
/// </summary>
/// <remarks>
/// <see cref="TestConfiguration"/> turns the cache off for every other host because it is keyed on the
/// user id alone and one host serves a whole test class. That is safe here: every test seeds its own
/// users, so no two tests share a cache entry. The source is added after the base factory's, so it wins.
/// </remarks>
public class ClaimsCachingFactory(DatabaseFixture database) : BlueprintAppFactory(database)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string>
            {
                ["ClaimsTransformation:EnableCaching"] = "true",
                ["ClaimsTransformation:CacheExpirationSeconds"] = "3600",
            }));
    }
}
