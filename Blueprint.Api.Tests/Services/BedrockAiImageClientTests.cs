// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Text.Json;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Services;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary><c>BedrockAiImageClient</c> - reading a Stability answer, and when photos are on.</summary>
public class BedrockAiImageClientTests
{
    [Fact]
    public void ReadPhoto_AnswersTheFirstImageAsAJpegDataUri()
    {
        var answer = JsonDocument.Parse("""{"images":["QUJD"],"finish_reasons":[null],"seeds":[1]}""").RootElement;

        Assert.Equal("data:image/jpeg;base64,QUJD", BedrockAiImageClient.ReadPhoto(answer));
    }

    [Fact]
    public void ReadPhoto_OfAFilteredAnswer_SaysWhy()
    {
        var answer = JsonDocument.Parse("""{"images":[""],"finish_reasons":["CONTENT_FILTERED"]}""").RootElement;

        var error = Assert.Throws<InvalidOperationException>(() => BedrockAiImageClient.ReadPhoto(answer));
        Assert.Equal("The image model made no photo (CONTENT_FILTERED).", error.Message);
    }

    [Theory]
    [InlineData(true, "stability.stable-image-core-v1:1", true)]
    [InlineData(true, "", false)]
    [InlineData(false, "stability.stable-image-core-v1:1", false)]
    public void IsConfigured_NeedsAIOnAndAnImageModel(bool enabled, string imageModelId, bool configured)
    {
        var client = new BedrockAiImageClient(new AiOptions { Enabled = enabled, ImageModelId = imageModelId });

        Assert.Equal(configured, client.IsConfigured);
    }
}
