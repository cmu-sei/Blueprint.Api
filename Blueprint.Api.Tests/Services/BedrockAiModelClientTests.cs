// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Text.Json;
using Blueprint.Api.Services;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary><c>BedrockAiModelClient.ReadToolInput</c> - turning the model's response into the draft,
/// or into a message the planner can act on.</summary>
public class BedrockAiModelClientTests
{
    [Fact]
    public void ReadToolInput_ReturnsTheToolUseInput()
    {
        var input = BedrockAiModelClient.ReadToolInput(Parse("""
            {"stop_reason":"tool_use","content":[{"type":"thinking","thinking":""},{"type":"tool_use","name":"submit_msel_draft","input":{"name":"Draft"}}]}
            """));

        Assert.Equal("Draft", input.GetProperty("name").GetString());
    }

    [Fact]
    public void ReadToolInput_NamesTheCategoryOfARefusal()
    {
        var error = Assert.Throws<InvalidOperationException>(() => BedrockAiModelClient.ReadToolInput(Parse("""
            {"stop_reason":"refusal","stop_details":{"type":"refusal","category":"cyber","explanation":null},"content":[]}
            """)));

        Assert.Contains("declined", error.Message);
        Assert.Contains("cyber", error.Message);
    }

    [Fact]
    public void ReadToolInput_ExplainsARefusalWithoutACategory()
    {
        var error = Assert.Throws<InvalidOperationException>(() => BedrockAiModelClient.ReadToolInput(Parse("""
            {"stop_reason":"refusal","stop_details":null,"content":[]}
            """)));

        Assert.Equal("The model declined to write this draft. Reword the request.", error.Message);
    }

    [Fact]
    public void ReadToolInput_ExplainsRunningOutOfTokens()
    {
        var error = Assert.Throws<InvalidOperationException>(() => BedrockAiModelClient.ReadToolInput(Parse("""
            {"stop_reason":"max_tokens","content":[{"type":"thinking","thinking":""}]}
            """)));

        Assert.Contains("ran out of output tokens", error.Message);
    }

    [Fact]
    public void ReadToolInput_ExplainsAnAnswerWithNoTool()
    {
        var error = Assert.Throws<InvalidOperationException>(() => BedrockAiModelClient.ReadToolInput(Parse("""
            {"stop_reason":"end_turn","content":[{"type":"text","text":"Here is a draft."}]}
            """)));

        Assert.Contains("without returning a draft", error.Message);
    }

    [Fact]
    public void StreamedMessage_JoinsTheToolInputAcrossDeltas()
    {
        var message = Stream(
            """{"type":"message_start","message":{"usage":{"input_tokens":120,"output_tokens":1}}}""",
            """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}""",
            """{"type":"content_block_stop","index":0}""",
            """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"t1","name":"submit_msel_draft","input":{}}}""",
            """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"name\":\"Dr"}}""",
            """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"aft\"}"}}""",
            """{"type":"content_block_stop","index":1}""",
            """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":42}}""",
            """{"type":"message_stop"}""");

        Assert.Equal("Draft", BedrockAiModelClient.ReadToolInput(message).GetProperty("name").GetString());
        Assert.Equal(120, message.GetProperty("usage").GetProperty("input_tokens").GetInt32());
        Assert.Equal(42, message.GetProperty("usage").GetProperty("output_tokens").GetInt32());
    }

    [Fact]
    public void StreamedMessage_KeepsTheRefusalCategory()
    {
        var message = Stream(
            """{"type":"message_start","message":{"usage":{"input_tokens":120}}}""",
            """{"type":"message_delta","delta":{"stop_reason":"refusal","stop_details":{"type":"refusal","category":"cyber"}},"usage":{"output_tokens":3632}}""");

        var error = Assert.Throws<InvalidOperationException>(() => BedrockAiModelClient.ReadToolInput(message));
        Assert.Contains("cyber", error.Message);
    }

    [Fact]
    public void StreamedMessage_JoinsTextAcrossDeltas()
    {
        var message = Stream(
            """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Here is "}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"a draft."}}""",
            """{"type":"message_delta","delta":{"stop_reason":"end_turn"}}""");

        Assert.Equal("Here is a draft.", message.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void StreamedMessage_RaisesAnErrorEvent()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Stream("""{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}"""));

        Assert.Contains("Overloaded", error.Message);
    }

    [Fact]
    public void RepairStringifiedJson_ParsesAnArraySentAsAString()
    {
        var input = Parse("""{"name":"Draft","moves":"[{\"moveNumber\":1},{\"moveNumber\":2}]"}""");

        var repaired = BedrockAiModelClient.RepairStringifiedJson(input, Schema());

        Assert.Equal(JsonValueKind.Array, repaired.GetProperty("moves").ValueKind);
        Assert.Equal(2, repaired.GetProperty("moves")[1].GetProperty("moveNumber").GetInt32());
    }

    [Fact]
    public void RepairStringifiedJson_ParsesAnObjectSentAsAStringInsideAnArray()
    {
        var input = Parse("""{"moves":["{\"moveNumber\":1}"]}""");

        var repaired = BedrockAiModelClient.RepairStringifiedJson(input, Schema());

        Assert.Equal(1, repaired.GetProperty("moves")[0].GetProperty("moveNumber").GetInt32());
    }

    [Fact]
    public void RepairStringifiedJson_LeavesStringsTheSchemaExpects()
    {
        var input = Parse("""{"name":"[1,2]","moves":"not json"}""");

        var repaired = BedrockAiModelClient.RepairStringifiedJson(input, Schema());

        Assert.Equal("[1,2]", repaired.GetProperty("name").GetString());
        Assert.Equal("not json", repaired.GetProperty("moves").GetString());
    }

    /// <summary>
    /// The shapes a model writes values in besides the schema's: one string for a list of strings, a number as text,
    /// a single object for a list, and an object for text. Each comes back in the schema's shape, so the draft reads.
    /// </summary>
    [Fact]
    public void RepairStringifiedJson_CoercesValuesToTheSchemasShape()
    {
        var input = Parse("""{"name":{"title":"Draft"},"assumptions":"Repair takes weeks","empty":" ","moves":{"moveNumber":"2","decisions":"Choose a lead"}}""");

        var repaired = BedrockAiModelClient.RepairStringifiedJson(input, CoercionSchema());

        Assert.Equal("{\"title\":\"Draft\"}", repaired.GetProperty("name").GetString());
        Assert.Equal(["Repair takes weeks"], repaired.GetProperty("assumptions").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(0, repaired.GetProperty("empty").GetArrayLength());
        var move = Assert.Single(repaired.GetProperty("moves").EnumerateArray());
        Assert.Equal(2, move.GetProperty("moveNumber").GetInt32());
        Assert.Equal(["Choose a lead"], move.GetProperty("decisions").EnumerateArray().Select(a => a.GetString()));
        Assert.NotNull(JsonSerializer.Deserialize<Blueprint.Api.ViewModels.MselDraft>(repaired.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }));
    }

    /// <summary>An object written as a paragraph becomes that paragraph in the object's first required text field.</summary>
    [Fact]
    public void RepairStringifiedJson_PutsAParagraphInTheObjectsFirstTextField()
    {
        var schema = System.Text.Json.Nodes.JsonNode.Parse("""
            {"type":"object","properties":{"scenario":{"type":"object","properties":{"problem":{"type":"string"},"premise":{"type":"string"}},"required":["problem","premise"]}}}
            """).AsObject();
        var input = Parse("""{"scenario":"Two cables to the islands are cut by a dragged anchor."}""");

        var repaired = BedrockAiModelClient.RepairStringifiedJson(input, schema);

        Assert.Equal("Two cables to the islands are cut by a dragged anchor.", repaired.GetProperty("scenario").GetProperty("problem").GetString());
    }

    /// <summary>
    /// A key the model repeats in one object keeps its last value, through the stream and the repair, where a copy
    /// that rejected the repeat would fail the whole draft.
    /// </summary>
    [Fact]
    public void RepairStringifiedJson_KeepsTheLastValueOfARepeatedKey()
    {
        var message = Stream(
            """{"type":"message_start","message":{"usage":{"input_tokens":5}}}""",
            """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","name":"t","input":{}}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"name\":\"Draft\",\"item\":1,\"moves\":[{\"moveNumber\":1,\"item\":\"a\",\"item\":\"b\"}],\"item\":2}"}}""",
            """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":9}}""");

        var repaired = BedrockAiModelClient.RepairStringifiedJson(BedrockAiModelClient.ReadToolInput(message), Schema());

        Assert.Equal(2, repaired.GetProperty("item").GetInt32());
        Assert.Equal("b", repaired.GetProperty("moves")[0].GetProperty("item").GetString());
        Assert.Equal(1, repaired.GetProperty("moves")[0].GetProperty("moveNumber").GetInt32());
    }

    private static System.Text.Json.Nodes.JsonObject CoercionSchema() =>
        System.Text.Json.Nodes.JsonNode.Parse("""
            {"type":"object","properties":{
              "name":{"type":"string"},
              "assumptions":{"type":"array","items":{"type":"string"}},
              "empty":{"type":"array","items":{"type":"string"}},
              "moves":{"type":"array","items":{"type":"object","properties":{"moveNumber":{"type":"integer"},"decisions":{"type":"array","items":{"type":"string"}}}}}}}
            """).AsObject();

    /// <summary>An answer written in the model's own call markup, inside the JSON, is caught, wherever it is.</summary>
    [Fact]
    public void HasCallMarkup_FindsMarkupInAnyValue()
    {
        // the stored message escapes < as \u003C, as the streamed answer is put back together
        var nested = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            name = "Catnip",
            plannerNotes = new[] { "<parameter name=\"0\">Template teams were replaced." }
        });

        Assert.True(BedrockAiModelClient.HasCallMarkup(Parse("""{"scenario":{"problem":"<parameter name=\"problem\">A crew is inside."}}""")));
        Assert.True(BedrockAiModelClient.HasCallMarkup(nested));
        Assert.False(BedrockAiModelClient.HasCallMarkup(Parse("""{"name":"Catnip","readIn":"<p>Read <b>this</b>.</p>","moves":[{"moveNumber":0}]}""")));
    }

    /// <summary>An answer cut off part-way still says why the model stopped, and what it used, for the record.</summary>
    [Fact]
    public void StreamedMessage_ReadsTheStopReasonOfAnAnswerCutOffPartWay()
    {
        var message = new StreamedMessage();
        message.Add("""{"type":"message_start","message":{"usage":{"input_tokens":40000}}}""");
        message.Add("""{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","name":"t","input":{}}}""");
        message.Add("""{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"injects\":[{\"minutes\":5"}}""");
        message.Add("""{"type":"message_delta","delta":{"stop_reason":"max_tokens"},"usage":{"output_tokens":64000}}""");

        Assert.Equal("max_tokens", message.StopReason);
        Assert.Contains("64000", message.Usage);
        Assert.ThrowsAny<JsonException>(() => message.ToMessage());
        Assert.Equal(64000, message.ToMessage(withInput: false).GetProperty("usage").GetProperty("output_tokens").GetInt32());
    }

    private static System.Text.Json.Nodes.JsonObject Schema() =>
        System.Text.Json.Nodes.JsonNode.Parse("""
            {"type":"object","properties":{
              "name":{"type":"string"},
              "moves":{"type":"array","items":{"type":"object","properties":{"moveNumber":{"type":"integer"}}}}}}
            """).AsObject();

    private static JsonElement Stream(params string[] events)
    {
        var message = new StreamedMessage();
        foreach (var e in events)
            message.Add(e);
        return message.ToMessage();
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
