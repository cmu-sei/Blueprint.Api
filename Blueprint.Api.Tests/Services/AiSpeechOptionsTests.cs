// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary><c>Ai:Speech</c> - which provider voices clips, with which voices, and how what they voice is priced.</summary>
public class AiSpeechOptionsTests
{
    [Theory]
    [InlineData(null, null, null, false)]
    [InlineData("", null, null, false)]
    [InlineData("polly", null, null, true)]
    [InlineData("OpenAI", null, "tts-1", false)]
    [InlineData("OpenAI", "http://speech.local/v1", null, false)]
    [InlineData("OpenAI", "http://speech.local/v1", "tts-1", true)]
    [InlineData("Piper", "http://speech.local/v1", "tts-1", false)]
    public void IsConfigured_NeedsAKnownProviderAndWhatItNeeds(string provider, string baseUrl, string model, bool configured)
    {
        var options = new AiSpeechOptions { Provider = provider, BaseUrl = baseUrl, Model = model };

        Assert.Equal(configured, options.IsConfigured);
    }

    /// <summary>Without voices of its own, a provider has its usual English ones; Polly's newscaster voices are marked.</summary>
    [Fact]
    public void EffectiveVoices_AreTheProvidersUnlessConfigured()
    {
        var polly = new AiSpeechOptions { Provider = "Polly" }.EffectiveVoices();
        var openAi = new AiSpeechOptions { Provider = "OpenAI" }.EffectiveVoices();
        var own = new AiSpeechOptions { Provider = "OpenAI", Voices = [new AiVoice("af_bella", "Female", "US English"), new AiVoice { Name = " " }] }.EffectiveVoices();

        Assert.Equal(["Matthew", "Joanna", "Amy"], polly.Where(v => v.News).Select(v => v.Name));
        Assert.Contains(openAi, v => v.Name == "onyx");
        Assert.Equal(["af_bella"], own.Select(v => v.Name));
        Assert.Empty(new AiSpeechOptions().EffectiveVoices());
    }

    /// <summary>Configured voices bind from configuration, and replace the defaults rather than add to them.</summary>
    [Fact]
    public void Voices_BindFromConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Ai:Speech:Provider"] = "Polly",
            ["Ai:Speech:Engine"] = "Neural",
            ["Ai:Speech:Voices:0:Name"] = "Matthew",
            ["Ai:Speech:Voices:0:Gender"] = "Male",
            ["Ai:Speech:Voices:0:News"] = "true",
            ["Ai:Prices:polly_neural:PerMillionCharacters"] = "16"
        }).Build();
        var options = configuration.GetSection("Ai").Get<AiOptions>();

        var voice = Assert.Single(options.Speech.EffectiveVoices());
        Assert.Equal(("Matthew", "Male", true), (voice.Name, voice.Gender, voice.News));
        Assert.Equal("polly-neural", options.Speech.UsageModel);
        Assert.Equal(16m, AiUsageService.PriceFor(options.Prices, options.Speech.UsageModel).PerMillionCharacters);
    }

    [Fact]
    public void UsageModel_IsPollysEngineOrTheOpenAiModel()
    {
        Assert.Equal("polly-neural", new AiSpeechOptions { Provider = "Polly", Engine = "" }.UsageModel);
        Assert.Equal("polly-standard", new AiSpeechOptions { Provider = "Polly", Engine = "Standard" }.UsageModel);
        Assert.Equal("tts-1", new AiSpeechOptions { Provider = "OpenAI", Model = " tts-1 " }.UsageModel);
    }

    /// <summary>A speech row's input is characters, priced per million characters, and kept out of the token totals.</summary>
    [Fact]
    public void Summarize_PricesAudioByTheCharacter()
    {
        var mselId = Guid.NewGuid();
        var prices = new Dictionary<string, AiPrice>
        {
            ["polly_neural"] = new() { PerMillionCharacters = 16 },
            ["test-model"] = new() { InputPerMillion = 2, OutputPerMillion = 10 }
        };
        var rows = new List<AiUsageEntity>
        {
            new() { Operation = "script", Model = "test-model", InputTokens = 1000, OutputTokens = 500, DateCreated = DateTime.UtcNow },
            new() { Operation = AiUsageRecorder.Audio, Model = "polly-neural", InputTokens = 900, DateCreated = DateTime.UtcNow },
            new() { Operation = AiUsageRecorder.Audio, Model = "polly-neural", InputTokens = 900, DateCreated = DateTime.UtcNow }
        };

        var summary = AiUsageService.Summarize(mselId, "Exercise", rows, prices);

        Assert.Equal((1000L, 1800L), (summary.InputTokens, summary.Characters));
        var audio = summary.ByOperation.Single(o => o.Name == AiUsageRecorder.Audio);
        Assert.Equal((2, 0L, 1800L), (audio.Calls, audio.InputTokens, audio.Characters));
        // 1,800 characters at $16 per million
        Assert.Equal(28_800, audio.EstimatedCostMicros);
        // and 1,000 × $2 + 500 × $10 per million for the script
        Assert.Equal(28_800 + 7_000, summary.EstimatedCostMicros);
    }
}
