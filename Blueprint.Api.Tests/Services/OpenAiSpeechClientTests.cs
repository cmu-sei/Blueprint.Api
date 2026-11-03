// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using NSubstitute;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary>
/// <c>OpenAiSpeechClient</c> - an OpenAI-style /v1/audio/speech server, with no timings, so they are estimated from the text.
/// The server is a handler in the test.
/// </summary>
public class OpenAiSpeechClientTests
{
    private readonly IAiUsageRecorder _usage = Substitute.For<IAiUsageRecorder>();

    [Fact]
    public async Task Speak_PostsTheTextAndEstimatesTheMarks()
    {
        var (client, handler) = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Mp3Frames.Lasting(2304)) });

        var speech = await client.SpeakAsync("Go now. Stay calm.", "onyx", null, TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://speech.local/v1/audio/speech", request.Uri);
        Assert.Equal("Bearer sk-local", request.Authorization);
        var body = JsonDocument.Parse(request.Body).RootElement;
        Assert.Equal(("tts-1", "Go now. Stay calm.", "onyx", "mp3"), (body.GetProperty("model").GetString(), body.GetProperty("input").GetString(),
            body.GetProperty("voice").GetString(), body.GetProperty("response_format").GetString()));
        Assert.False(body.TryGetProperty("instructions", out _));
        Assert.Equal(2304, speech.DurationMs);
        // "Go now." is 8 of the 19 characters counted
        Assert.Equal([(0, "Go now."), (970, "Stay calm.")], speech.Sentences.Select(m => (m.Time, m.Text)));
        Assert.Equal(4, speech.Words.Count);
        await _usage.Received(1).RecordSpeechAsync("tts-1", 18, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Speak_InTheNewsStyle_SendsInstructions()
    {
        var (client, handler) = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Mp3Frames.Of(10)) });

        await client.SpeakAsync("Good evening.", "onyx", AiSpeechStyle.News, TestContext.Current.CancellationToken);

        Assert.Contains("news anchor", JsonDocument.Parse(handler.Requests[0].Body).RootElement.GetProperty("instructions").GetString());
    }

    /// <summary>Text past one request goes in pieces, and the second's marks follow the first's audio.</summary>
    [Fact]
    public async Task Speak_OfLongText_SplitsIt()
    {
        var (client, handler) = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Mp3Frames.Of(50)) });
        var text = string.Join(" ", Enumerable.Repeat("This sentence is about fifty characters long, ok.", 120));

        var speech = await client.SpeakAsync(text, "nova", null, TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2400, speech.DurationMs);
        Assert.Equal(1200, speech.Sentences.First(m => m.Time >= 1200).Time);
    }

    [Fact]
    public async Task Speak_WhenTheServerFails_SaysWhatItAnswered()
    {
        var (client, _) = Client(_ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("unknown voice") });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SpeakAsync("Hello.", "nobody", null, TestContext.Current.CancellationToken));

        Assert.Equal("The speech server answered 400: unknown voice", error.Message);
        await _usage.DidNotReceive().RecordSpeechAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A server that sends audio the frame reader cannot read is timed at 15 characters a second.</summary>
    [Fact]
    public async Task Speak_OfUnreadableAudio_EstimatesItsLength()
    {
        var (client, _) = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });

        var speech = await client.SpeakAsync("Thirty characters of text, ok.", "nova", null, TestContext.Current.CancellationToken);

        Assert.Equal(2000, speech.DurationMs);
    }

    private (OpenAiSpeechClient, Handler) Client(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var handler = new Handler(answer);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        var options = new AiOptions
        {
            Speech = new AiSpeechOptions { Provider = "OpenAI", BaseUrl = "http://speech.local/v1/", ApiKey = "sk-local", Model = "tts-1" }
        };
        return (new OpenAiSpeechClient(factory, options, _usage), handler);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(string Uri, string Authorization, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri.ToString(), request.Headers.Authorization?.ToString(), await request.Content.ReadAsStringAsync(ct)));
            return answer(request);
        }
    }
}
