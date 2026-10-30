// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary>Drafting through an OpenAI-style chat completions endpoint, such as the OpenAI API or a vLLM server.</summary>
public class OpenAiModelClientTests
{
    private static readonly AiTaskModel Model = new() { Provider = AiOptions.OpenAi, ModelId = "test-model", BaseUrl = "http://models.test/v1", ApiKey = "key", MaxTokens = 4000, Effort = "xhigh" };

    /// <summary>The streamed tool call's arguments are put together, its progress reported, and its usage recorded in the records' terms.</summary>
    [Fact]
    public async Task InvokeTool_ReadsTheStreamedToolCall()
    {
        var (client, handler, usage, progress) = Client(Stream(
            """{"choices":[{"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"submit_org","arguments":""}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"name\":\"Clearflow"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":" Water\",\"moves\":\"[1,2]\"}"}}]}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""",
            """{"choices":[],"usage":{"prompt_tokens":140,"completion_tokens":106,"prompt_tokens_details":{"cached_tokens":40}}}"""));

        var input = await client.InvokeToolAsync(Model, "Draft.", "Name a utility.", "submit_org", "Submit it", Schema(), CancellationToken.None);

        Assert.Equal("Clearflow Water", input.GetProperty("name").GetString());
        // a list sent as a string of JSON is repaired, as for every provider
        Assert.Equal(JsonValueKind.Array, input.GetProperty("moves").ValueKind);
        var sent = JsonNode.Parse(handler.Body);
        Assert.Equal("http://models.test/v1/chat/completions", handler.Uri);
        Assert.Equal("Bearer key", handler.Authorization);
        Assert.Equal("submit_org", sent["tool_choice"]["function"]["name"].GetValue<string>());
        Assert.Equal(4000, sent["max_completion_tokens"].GetValue<int>());
        Assert.True(sent["stream"].GetValue<bool>());
        // OpenAI's efforts stop at high
        Assert.Equal("high", sent["reasoning_effort"].GetValue<string>());
        await progress.Received().AddAsync(Arg.Is<string>(s => s.Contains("Clearflow")), Arg.Any<CancellationToken>());
        await usage.Received().RecordAsync("test-model", Arg.Is<JsonElement>(m =>
            m.GetProperty("stop_reason").GetString() == "tool_use"
            && m.GetProperty("usage").GetProperty("input_tokens").GetInt32() == 100
            && m.GetProperty("usage").GetProperty("cache_read_input_tokens").GetInt32() == 40
            && m.GetProperty("usage").GetProperty("output_tokens").GetInt32() == 106), 0, Arg.Any<CancellationToken>());
    }

    /// <summary>A model that answers in text instead of calling the tool still has its JSON read.</summary>
    [Fact]
    public async Task InvokeTool_ReadsJsonWrittenAsText()
    {
        var (client, _, _, _) = Client(Stream(
            """{"choices":[{"delta":{"content":"Here it is:\n```json\n{\"name\": \"Harbor"}}]}""",
            """{"choices":[{"delta":{"content":" Water\"}\n```"},"finish_reason":"stop"}]}"""));

        var input = await client.InvokeToolAsync(Model, "Draft.", "Name a utility.", "submit_org", "Submit it", Schema(), CancellationToken.None);

        Assert.Equal("Harbor Water", input.GetProperty("name").GetString());
    }

    /// <summary>An answer cut off at the output limit, an answer with no draft, and a server error each say what happened.</summary>
    [Theory]
    [InlineData("length", "ran out of output tokens")]
    [InlineData("stop", "without returning a draft")]
    public async Task InvokeTool_SaysWhyThereIsNoDraft(string finishReason, string message)
    {
        var (client, _, usage, _) = Client(Stream(
            $$$"""{"choices":[{"delta":{"content":"Thinking"},"finish_reason":"{{{finishReason}}}"}],"usage":{"prompt_tokens":10,"completion_tokens":4000}}"""));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.InvokeToolAsync(Model, "Draft.", "Name a utility.", "submit_org", "Submit it", Schema(), CancellationToken.None));

        Assert.Contains(message, error.Message);
        // the call is billed even so
        await usage.Received().RecordAsync("test-model", Arg.Any<JsonElement>(), 0, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeTool_SaysWhatTheServerAnswered()
    {
        var (client, _, _, _) = Client(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"detail":"Model not found"}""") });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.InvokeToolAsync(Model, "Draft.", "Name a utility.", "submit_org", "Submit it", Schema(), CancellationToken.None));

        Assert.Contains("404", error.Message);
        Assert.Contains("Model not found", error.Message);
    }

    /// <summary>Each task goes to its own provider and model, and anything it leaves out is the default's.</summary>
    [Fact]
    public void ModelFor_TakesTheTasksOwnSettingsAndTheDefaultsForTheRest()
    {
        var options = new AiOptions
        {
            ModelId = "us.anthropic.claude-sonnet-5-5",
            Region = "us-east-1",
            MaxTokens = 64000,
            Effort = "high",
            Models = new Dictionary<string, AiTaskModel>
            {
                ["Article"] = new() { ModelId = "us.anthropic.claude-haiku-5-5", Effort = "low" },
                ["rows"] = new() { Provider = "openai", ModelId = "gpt-oss-120b", BaseUrl = "http://models.test/v1", MaxTokens = 16000 }
            }
        };

        Assert.Equal(("Bedrock", "us.anthropic.claude-sonnet-5-5"), (options.ModelFor("design").Provider, options.ModelFor("design").ModelId));
        Assert.Equal(("Bedrock", "us.anthropic.claude-haiku-5-5", "us-east-1"), (options.ModelFor("article").Provider, options.ModelFor("article").ModelId, options.ModelFor("article").Region));
        var rows = options.ModelFor("rows");
        Assert.Equal(("openai", "gpt-oss-120b", 16000), (rows.Provider, rows.ModelId, rows.MaxTokens));
        Assert.Equal(64000, options.ModelFor(null).MaxTokens);
        Assert.Equal(("low", "high"), (options.ModelFor("article").Effort, options.ModelFor("design").Effort));
    }

    /// <summary>
    /// A real Gallery article from a live OpenAI-style endpoint. Runs only where BLUEPRINT_LIVE_OPENAI_BASE_URL,
    /// BLUEPRINT_LIVE_OPENAI_API_KEY and BLUEPRINT_LIVE_OPENAI_MODEL are set, so builds never call out.
    /// </summary>
    [Fact]
    public async Task Live_WritesAGalleryArticle()
    {
        var baseUrl = Environment.GetEnvironmentVariable("BLUEPRINT_LIVE_OPENAI_BASE_URL");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(baseUrl), "No live OpenAI-style endpoint is configured.");
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        var client = new OpenAiModelClient(factory, new AiOptions { TimeoutSeconds = 300 }, NullLogger<OpenAiModelClient>.Instance,
            Substitute.For<IAiUsageRecorder>(), Substitute.For<IAiProgress>());
        var model = new AiTaskModel
        {
            Provider = AiOptions.OpenAi,
            BaseUrl = baseUrl,
            ApiKey = Environment.GetEnvironmentVariable("BLUEPRINT_LIVE_OPENAI_API_KEY"),
            ModelId = Environment.GetEnvironmentVariable("BLUEPRINT_LIVE_OPENAI_MODEL"),
            MaxTokens = 8000
        };
        var schema = JsonNode.Parse("""
            {"type":"object","properties":{"html":{"type":"string","description":"The article as an HTML fragment that follows the HTML rules."},
             "notes":{"type":"array","items":{"type":"string"}}},"required":["html"]}
            """).AsObject();

        var input = await client.InvokeToolAsync(model, ArticleDraftService.SystemPrompt,
            "Exercise: Deep Bite\nScenario: two undersea cables to an island state are cut.\n\nThe article:\nSource type: News\n" +
            "Source name: Harbour Herald\nHeadline or subject: Islands lose internet as two cables fail\nSummary: Both cables to the mainland failed overnight.",
            "submit_article", "Submit the article's HTML.", schema, TestContext.Current.CancellationToken);

        var html = input.GetProperty("html").GetString();
        // to read what the model wrote
        if (Environment.GetEnvironmentVariable("BLUEPRINT_LIVE_OPENAI_OUT") is string path && path.Length > 0)
            await System.IO.File.WriteAllTextAsync(path, html, TestContext.Current.CancellationToken);
        Assert.Contains("<", html);
        Assert.True(html.Length > 300, html);
    }

    private static (OpenAiModelClient, Handler, IAiUsageRecorder, IAiProgress) Client(HttpResponseMessage response)
    {
        var handler = new Handler(response);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        var usage = Substitute.For<IAiUsageRecorder>();
        var progress = Substitute.For<IAiProgress>();
        return (new OpenAiModelClient(factory, new AiOptions { TimeoutSeconds = 30 }, NullLogger<OpenAiModelClient>.Instance, usage, progress), handler, usage, progress);
    }

    private static HttpResponseMessage Stream(params string[] chunks)
    {
        var sb = new StringBuilder();
        foreach (var chunk in chunks)
            sb.Append("data: ").Append(chunk).Append("\n\n");
        sb.Append("data: [DONE]\n\n");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/event-stream") };
    }

    private static JsonObject Schema() => JsonNode.Parse("""
        {"type":"object","properties":{"name":{"type":"string"},"moves":{"type":"array","items":{"type":"integer"}}},"required":["name"]}
        """).AsObject();

    private sealed class Handler(HttpResponseMessage response) : HttpMessageHandler
    {
        public string Body { get; private set; }
        public string Uri { get; private set; }
        public string Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uri = request.RequestUri.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content.ReadAsStringAsync(ct);
            return response;
        }
    }
}
