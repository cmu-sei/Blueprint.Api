// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Polly;
using Amazon.Polly.Model;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using NSubstitute;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary>
/// <c>PollySpeechClient</c> - each piece of text asked for as MP3 and as sentence and word marks, the newscaster style on the
/// neural engine, long text split and joined, and every request recorded as audio usage. Polly itself is a substitute.
/// </summary>
public class PollySpeechClientTests
{
    private readonly IAmazonPolly _polly = Substitute.For<IAmazonPolly>();
    private readonly IAiUsageRecorder _usage = Substitute.For<IAiUsageRecorder>();
    private readonly List<SynthesizeSpeechRequest> _requests = new();

    public PollySpeechClientTests()
    {
        _polly.SynthesizeSpeechAsync(Arg.Any<SynthesizeSpeechRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<SynthesizeSpeechRequest>();
            _requests.Add(request);
            var text = SpeechText.Clean(request.Text);
            // each piece is a second long, its marks a sentence and its first two words
            var body = request.OutputFormat == OutputFormat.Mp3
                ? Mp3Frames.Lasting(1008)
                : Encoding.UTF8.GetBytes(
                    $"{{\"time\":0,\"type\":\"sentence\",\"start\":7,\"end\":20,\"value\":\"{text.Split('.')[0]}.\"}}\n" +
                    "{\"time\":6,\"type\":\"word\",\"start\":7,\"end\":11,\"value\":\"Good\"}\n" +
                    "{\"time\":300,\"type\":\"word\",\"start\":12,\"end\":19,\"value\":\"evening\"}\n");
            return Task.FromResult(new SynthesizeSpeechResponse { AudioStream = new MemoryStream(body), RequestCharacters = text.Length });
        });
    }

    private PollySpeechClient Client(string engine = "neural") =>
        new(new AiOptions { Speech = new AiSpeechOptions { Provider = "Polly", Engine = engine } }, _usage, _polly);

    [Fact]
    public async Task Speak_AsksForMp3AndSentenceAndWordMarks()
    {
        var speech = await Client().SpeakAsync("Good evening. Pumps are down.", "Matthew", null, TestContext.Current.CancellationToken);

        Assert.Equal(2, _requests.Count);
        Assert.All(_requests, r => Assert.Equal(("neural", "Matthew", "ssml"), (r.Engine.Value, r.VoiceId.Value, r.TextType.Value)));
        Assert.Equal("<speak>Good evening. Pumps are down.</speak>", _requests[0].Text);
        Assert.Equal(("mp3", "24000"), (_requests[0].OutputFormat.Value, _requests[0].SampleRate));
        Assert.Equal(["sentence", "word"], _requests[1].SpeechMarkTypes);
        Assert.Equal(1008, speech.DurationMs);
        Assert.Equal([(0, "Good evening.")], speech.Sentences.Select(m => (m.Time, m.Text)));
        Assert.Equal([(6, "Good"), (300, "evening")], speech.Words.Select(m => (m.Time, m.Text)));
    }

    /// <summary>Both requests are billed, so both are recorded, as polly-neural, by the characters Polly says it billed.</summary>
    [Fact]
    public async Task Speak_RecordsEachRequestAsAudio()
    {
        await Client().SpeakAsync("Good evening.", "Matthew", null, TestContext.Current.CancellationToken);

        await _usage.Received(2).RecordSpeechAsync("polly-neural", 13, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Speak_InTheNewsStyle_WrapsTheTextInTheNewsDomain()
    {
        await Client().SpeakAsync("Good evening.", "Matthew", AiSpeechStyle.News, TestContext.Current.CancellationToken);

        Assert.Equal("<speak><amazon:domain name=\"news\">Good evening.</amazon:domain></speak>", _requests[0].Text);
    }

    /// <summary>Only the neural engine has the newscaster style, so another engine reads the text plain.</summary>
    [Fact]
    public async Task Speak_InTheNewsStyle_OnTheStandardEngine_ReadsPlain()
    {
        await Client("standard").SpeakAsync("Good evening.", "Matthew", AiSpeechStyle.News, TestContext.Current.CancellationToken);

        Assert.Equal("<speak>Good evening.</speak>", _requests[0].Text);
        Assert.Equal("standard", _requests[0].Engine.Value);
    }

    /// <summary>
    /// Text past one request is split at sentences; the clip is the pieces end to end, and the second piece's marks start where
    /// the first piece's audio ends.
    /// </summary>
    [Fact]
    public async Task Speak_OfLongText_SplitsItAndMovesTheMarksAlong()
    {
        var sentence = "Crews worked through the night to restore the pumps " + new string('x', 60) + ".";
        var text = string.Join(" ", Enumerable.Repeat(sentence, 40));

        var speech = await Client().SpeakAsync(text, "Joanna", null, TestContext.Current.CancellationToken);

        var pieces = SpeechText.Split(text, PollySpeechClient.MaxRequestCharacters);
        Assert.Equal(2, pieces.Count);
        Assert.Equal(4, _requests.Count);
        Assert.Equal(2016, speech.DurationMs);
        Assert.Equal([0, 1008], speech.Sentences.Select(m => m.Time));
        Assert.Equal([6, 300, 1014, 1308], speech.Words.Select(m => m.Time));
        Assert.Equal(Mp3Frames.Lasting(1008).Length * 2, speech.Mp3.Length);
    }

    [Fact]
    public async Task Speak_OfNothing_IsRefusedWithoutAskingPolly()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Client().SpeakAsync("  ", "Matthew", null, TestContext.Current.CancellationToken));

        Assert.Empty(_requests);
    }

    [Fact]
    public void ReadMarks_SkipsSsmlMarksAndBrokenLines()
    {
        var (sentences, words) = PollySpeechClient.ReadMarks(
            "{\"time\":0,\"type\":\"ssml\",\"value\":\"<amazon:domain name=\\\"news\\\">\"}\nnot json\n{\"time\":5,\"type\":\"sentence\",\"value\":\"AT&amp;T is down.\"}\n");

        Assert.Equal([(5, "AT&T is down.")], sentences.Select(m => (m.Time, m.Text)));
        Assert.Empty(words);
    }

    /// <summary>
    /// The real Polly, for a manual check. Runs only where BLUEPRINT_LIVE_POLLY is set and the default AWS credentials may call
    /// polly:SynthesizeSpeech, so builds never call out.
    /// </summary>
    [Fact]
    public async Task Live_VoicesASentenceWithMarks()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BLUEPRINT_LIVE_POLLY")), "No live Polly check was asked for.");
        var client = new PollySpeechClient(new AiOptions { Speech = new AiSpeechOptions { Provider = "Polly", Region = Environment.GetEnvironmentVariable("BLUEPRINT_LIVE_POLLY") } });

        var speech = await client.SpeakAsync("Good evening. Two pumping stations are offline after a cyber intrusion.", "Matthew", AiSpeechStyle.News,
            TestContext.Current.CancellationToken);

        Assert.True(speech.DurationMs > 2000, $"{speech.DurationMs} ms");
        Assert.Equal(2, speech.Sentences.Count);
        Assert.True(speech.Words.Count >= 10);
        if (Environment.GetEnvironmentVariable("BLUEPRINT_LIVE_POLLY_OUT") is { Length: > 0 } path)
            await File.WriteAllBytesAsync(path, speech.Mp3, TestContext.Current.CancellationToken);
    }
}
