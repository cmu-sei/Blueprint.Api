// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Linq;
using Blueprint.Api.Services;
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
}
