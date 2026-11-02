// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Hubs;
using Blueprint.Api.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary>Counting what a streamed draft has written so far, for the planner's progress.</summary>
public class AiProgressTests
{
    [Fact]
    public void Counter_CountsEachTopLevelListsItemsAsTheyArrive()
    {
        var counter = new AiAnswerCounter();
        var json = """
            {"name":"Deep {Bite}","scenario":{"problem":"Cut","assumptions":["a","b"]},
             "moves":[{"moveNumber":0,"expectedDecisions":["Lead"]},{"moveNumber":1}],
             "injects":[{"minutes":5,"values":{"Title":"Say \"hi\" [now]"}},{"minutes":
            """;

        // in pieces, split anywhere, as the model streams it
        foreach (var piece in Enumerable.Range(0, (json.Length + 6) / 7).Select(i => json.Substring(i * 7, System.Math.Min(7, json.Length - i * 7))))
            counter.Add(piece);

        Assert.Equal(2, counter.Counts["moves"]);
        Assert.Equal(2, counter.Counts["injects"]);
        // an object that is not a list, a list of strings, and braces inside strings count nothing
        Assert.False(counter.Counts.ContainsKey("scenario"));
        Assert.Equal(2, counter.Counts.Count);
        Assert.Equal(json.Length, counter.Characters);
    }

    [Fact]
    public void StreamedMessage_ReturnsEachPieceOfTheToolInput()
    {
        var message = new StreamedMessage();

        Assert.Null(message.Add("""{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","name":"t","input":{}}}"""));
        Assert.Equal("{\"moves\":[", message.Add("""{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"moves\":["}}"""));
        Assert.Null(message.Add("""{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":3}}"""));
    }
    /// <summary>
    /// A browser connection that is not reading makes the hub's send wait; the progress gives up on it after two seconds and
    /// sends nothing more for the request, so the model's stream, read between sends, is never held up by it.
    /// </summary>
    [Fact]
    public async Task Progress_DoesNotWaitOnABrowserThatIsNotReading()
    {
        var proxy = Substitute.For<IClientProxy>();
        proxy.SendCoreAsync(Arg.Any<string>(), Arg.Any<object[]>(), Arg.Any<CancellationToken>()).Returns(new TaskCompletionSource().Task);
        var hub = Substitute.For<IHubContext<MainHub>>();
        hub.Clients.Group(Arg.Any<string>()).Returns(proxy);
        var context = Substitute.For<IAiUsageContext>();
        context.Operation.Returns("injects");
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString())], "test"));
        var progress = new AiProgress(hub, context, user, NullLogger<AiProgress>.Instance);

        var watch = Stopwatch.StartNew();
        await progress.AddAsync("""{"injects":[{""", TestContext.Current.CancellationToken);
        Assert.InRange(watch.Elapsed.TotalSeconds, 1.5, 8);

        // muted for the rest of the request: no second send, no second wait
        watch.Restart();
        await progress.CompleteAsync(TestContext.Current.CancellationToken);
        Assert.True(watch.Elapsed.TotalSeconds < 1, watch.Elapsed.ToString());
        await proxy.Received(1).SendCoreAsync(MainHubMethods.AiDraftProgress, Arg.Any<object[]>(), Arg.Any<CancellationToken>());
    }
}

