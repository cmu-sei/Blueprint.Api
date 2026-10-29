// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Services;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary>A model's price is found by its id, or by the id written as an environment variable can hold it.</summary>
public class AiUsagePriceTests
{
    private static readonly AiPrice Sonnet = new() { InputPerMillion = 2 };

    [Theory]
    [InlineData("us.anthropic.claude-sonnet-5-5")]
    [InlineData("us_anthropic_claude_sonnet_5_5")]
    public void PriceFor_MatchesTheModelIgnoringPunctuation(string key)
    {
        var prices = new Dictionary<string, AiPrice> { [key] = Sonnet };

        Assert.Same(Sonnet, AiUsageService.PriceFor(prices, "us.anthropic.claude-sonnet-5-5"));
        Assert.Null(AiUsageService.PriceFor(prices, "us.anthropic.claude-haiku-5-5"));
        Assert.Null(AiUsageService.PriceFor(prices, null));
    }
}
