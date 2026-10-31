// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Blueprint.Api.Infrastructure.Options;
using Microsoft.Extensions.Logging;

namespace Blueprint.Api.Services
{
    public interface IAiModelClient
    {
        /// <summary>
        /// Sends one prompt and expects the model to answer by calling a single tool, so the answer
        /// is JSON that matches inputSchema. Returns the tool input.
        /// </summary>
        Task<JsonElement> InvokeToolAsync(string system, string prompt, string toolName, string toolDescription, JsonObject inputSchema, CancellationToken ct);
    }

    /// <summary>One provider's way of asking a model to answer by calling a tool</summary>
    public interface IAiProviderClient
    {
        Task<JsonElement> InvokeToolAsync(AiTaskModel model, string system, string prompt, string toolName, string toolDescription, JsonObject inputSchema, CancellationToken ct);
    }

    /// <summary>
    /// Sends each call to the provider and model its task is configured with: the design to one model, the articles to a cheaper
    /// one, or a task to a local model. The task is the one the call's usage is recorded under.
    /// </summary>
    public class AiModelRouter : IAiModelClient
    {
        private readonly AiOptions _options;
        private readonly IAiUsageContext _usage;
        private readonly BedrockAiModelClient _bedrock;
        private readonly OpenAiModelClient _openAi;

        public AiModelRouter(AiOptions options, IAiUsageContext usage, BedrockAiModelClient bedrock, OpenAiModelClient openAi)
        {
            _options = options;
            _usage = usage;
            _bedrock = bedrock;
            _openAi = openAi;
        }

        public Task<JsonElement> InvokeToolAsync(string system, string prompt, string toolName, string toolDescription, JsonObject inputSchema, CancellationToken ct)
        {
            var model = _options.ModelFor(_usage.Operation);
            if (!_options.Enabled || string.IsNullOrWhiteSpace(model.ModelId))
                throw new InvalidOperationException("AI drafting is not configured on this server.");
            IAiProviderClient provider = model.Provider switch
            {
                AiOptions.Bedrock => _bedrock,
                _ when string.Equals(model.Provider, AiOptions.OpenAi, StringComparison.OrdinalIgnoreCase) => _openAi,
                _ => throw new InvalidOperationException($"The AI provider {model.Provider} for {_usage.Operation} is not one this server knows: use Bedrock or OpenAI.")
            };
            return provider.InvokeToolAsync(model, system, prompt, toolName, toolDescription, inputSchema, ct);
        }
    }

    public class BedrockAiModelClient : IAiProviderClient
    {
        // Every prompt starts with this, so the model knows the security content it is asked for is training material.
        public const string Purpose =
@"You work inside the Crucible cyber range, which the Software Engineering Institute at Carnegie Mellon University uses to train
cyber defenders, incident responders and government and military staff. The people asking build exercises: the training scenarios
and the networks they run on. Exercises are about attacks, vulnerabilities, threat actors and their effects by design: that is
what participants practice responding to. Vulnerabilities, organizations and threat actors in a scenario are fictional unless the planner names a real one.
Describe adversary activity as defenders, reporters and officials would see and report it. Never write working exploit code,
malware, or step-by-step attack instructions; the scenario does not need them.";

        private readonly AiOptions _options;
        private readonly ILogger<BedrockAiModelClient> _logger;
        private readonly IAiUsageRecorder _usage;
        private readonly IAiProgress _progress;

        public BedrockAiModelClient(AiOptions options, ILogger<BedrockAiModelClient> logger, IAiUsageRecorder usage, IAiProgress progress)
        {
            _options = options;
            _logger = logger;
            _usage = usage;
            _progress = progress;
        }

        public async Task<JsonElement> InvokeToolAsync(AiTaskModel model, string system, string prompt, string toolName, string toolDescription, JsonObject inputSchema, CancellationToken ct)
        {
            // the Anthropic Messages body; only max_tokens is sent so newer models that reject sampling settings work
            var body = new JsonObject
            {
                ["anthropic_version"] = "bedrock-2023-05-31",
                ["max_tokens"] = model.MaxTokens,
                ["system"] = Purpose + "\n\n" + system,
                ["messages"] = new JsonArray(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = prompt
                }),
                ["tools"] = new JsonArray(new JsonObject
                {
                    ["name"] = toolName,
                    ["description"] = toolDescription,
                    ["input_schema"] = inputSchema.DeepClone(),
                    // without it each top-level field arrives whole, so a long list of injects shows no progress
                    // until it is done; the answer is read and checked here either way
                    ["eager_input_streaming"] = true
                }),
                // newer models reject a forced tool choice, so the system prompt asks for the tool instead
                ["tool_choice"] = new JsonObject { ["type"] = "auto" }
            };
            if (!string.IsNullOrWhiteSpace(model.Effort))
                body["output_config"] = new JsonObject { ["effort"] = model.Effort.Trim().ToLowerInvariant() };
            // constrained to the schema, so the answer is JSON of its shape, never the model's own call markup
            if (model.Strict != false)
            {
                body["tools"][0]["input_schema"] = StrictSchema(inputSchema);
                body["tools"][0]["strict"] = true;
            }

            var config = new AmazonBedrockRuntimeConfig
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(model.Region),
                Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds)
            };
            using var client = new AmazonBedrockRuntimeClient(config);

            // the asks: one, and one more for each thing Bedrock refuses or the model gets wrong, each fallback applying to the
            // retries too, since a retry can hit the next problem
            JsonElement input = default;
            var markupRetried = false;
            var eagerRetried = false;
            for (var attempt = 1; ; attempt++)
            {
                if (attempt > 5)
                    throw new InvalidOperationException("The model's answer could not be read after several tries. Draft again.");
                JsonElement response;
                try
                {
                    response = await InvokeAsync(client, model.ModelId, body, toolName, ct);
                }
                catch (AmazonBedrockRuntimeException ex) when (Refuses(ex, body))
                {
                    // Bedrock refuses one of the request's newer parts: strict mode when the schema's grammar is too large for it,
                    // eager streaming or effort on an older deployment. Only what it refused is left out of the next ask.
                    var dropped = new List<string>();
                    var tool = body["tools"][0].AsObject();
                    if (RefusesStrict(ex) && tool.ContainsKey("strict"))
                    {
                        tool.Remove("strict");
                        tool["input_schema"] = inputSchema.DeepClone();
                        dropped.Add("strict");
                    }
                    if (ex.Message.Contains("eager_input_streaming", StringComparison.OrdinalIgnoreCase) && tool.ContainsKey("eager_input_streaming"))
                    {
                        tool.Remove("eager_input_streaming");
                        dropped.Add("eager_input_streaming");
                    }
                    if (RefusesEffort(ex) && body.ContainsKey("output_config"))
                    {
                        body.Remove("output_config");
                        dropped.Add("output_config");
                    }
                    if (!dropped.Any())
                        throw;
                    _logger.LogInformation("{Model} refused {Fields}; asking without them: {Message}", model.ModelId, string.Join(", ", dropped), ex.Message);
                    continue;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not UnreadableAnswerException
                    && ex is not InvalidOperationException && body["tools"][0].AsObject().ContainsKey("strict"))
                {
                    // the stream failed while the answer was constrained to its schema; a schema too large for strict mode fails
                    // this way, so ask once more without it
                    _logger.LogWarning(ex, "The {Tool} answer from {Model} failed with strict mode ({Type}: {Message}); asking again without it",
                        toolName, model.ModelId, ex.GetType().Name, ex.Message);
                    body["tools"][0].AsObject().Remove("strict");
                    body["tools"][0]["input_schema"] = inputSchema.DeepClone();
                    continue;
                }
                catch (UnreadableAnswerException) when (!eagerRetried && body["tools"][0].AsObject().ContainsKey("eager_input_streaming"))
                {
                    // streamed as written, the tool input is not checked before it arrives, and the model wrote JSON that does not
                    // parse; asked again without it, Bedrock checks the input before it is sent
                    _logger.LogWarning("The {Tool} answer from {Model} was not valid JSON; asking again without eager streaming", toolName, model.ModelId);
                    body["tools"][0].AsObject().Remove("eager_input_streaming");
                    eagerRetried = true;
                    continue;
                }
                // a safety classifier can decline training content; one retry on another model, as the Claude API's own fallback does
                if (StopReason(response) == "refusal" && !string.IsNullOrWhiteSpace(model.FallbackModelId))
                    response = await InvokeAsync(client, model.FallbackModelId, body, toolName, ct);

                input = ReadToolInput(response);
                // a model sometimes writes the tool input in its own call markup, <parameter name="...">, inside the JSON,
                // which leaves most of the answer in the wrong fields; asked again it answers in JSON
                if (HasCallMarkup(input))
                {
                    if (markupRetried)
                        throw new InvalidOperationException("The model's answer came back garbled twice. Draft again.");
                    _logger.LogWarning("The {Tool} answer from {Model} was written in tool-call markup near: {Excerpt}; asking again", toolName, model.ModelId, MarkupExcerpt(input));
                    body["system"] = body["system"].GetValue<string>() + "\n\n" + JsonOnly;
                    markupRetried = true;
                    continue;
                }
                break;
            }
            try
            {
                return RepairStringifiedJson(input, inputSchema);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // the answer was paid for; what reads it next copes with what the repair could not fix
                _logger.LogWarning(ex, "Could not repair the {Tool} answer from {Model}; using it as written", toolName, model.ModelId);
                return input;
            }
        }

        private static bool RefusesStrict(Exception ex) =>
            ex.Message.Contains("strict", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("schema", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("grammar", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("too complex", StringComparison.OrdinalIgnoreCase);

        private static bool RefusesEffort(Exception ex) =>
            ex.Message.Contains("output_config", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("effort", StringComparison.OrdinalIgnoreCase);

        private static bool Refuses(AmazonBedrockRuntimeException ex, JsonObject body) =>
            ex.Message.Contains("eager_input_streaming", StringComparison.OrdinalIgnoreCase)
            || RefusesStrict(ex)
            || (body.ContainsKey("output_config") && RefusesEffort(ex));

        // what strict tool use cannot constrain, which Blueprint checks itself once the answer is read
        private static readonly string[] _unconstrained = ["minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf",
            "minLength", "maxLength", "minItems", "maxItems", "uniqueItems", "pattern"];

        /// <summary>
        /// The schema as strict tool use takes it: every object closed to properties it does not name, and without the
        /// numeric, length and array limits it does not support.
        /// </summary>
        public static JsonObject StrictSchema(JsonObject schema)
        {
            var copy = schema.DeepClone().AsObject();
            Close(copy);
            return copy;

            static void Close(JsonNode node)
            {
                if (node is JsonArray array)
                {
                    foreach (var item in array)
                        Close(item);
                    return;
                }
                if (node is not JsonObject obj)
                    return;
                foreach (var name in _unconstrained)
                    obj.Remove(name);
                if (obj["type"]?.GetValueKind() == JsonValueKind.String && obj["type"].GetValue<string>() == "object")
                    obj["additionalProperties"] = false;
                foreach (var (key, child) in obj.ToList())
                {
                    // a properties map is named by the answer's fields, which are not keywords whatever they are called
                    if (key == "properties" && child is JsonObject fields)
                        foreach (var (_, field) in fields.ToList())
                            Close(field);
                    else
                        Close(child);
                }
            }
        }

        private const string JsonOnly =
            "Write the tool input as plain JSON: every value a JSON string, number, array or object. " +
            "Never write <parameter> or other markup tags inside it.";

        // the forms a model's own call markup takes inside a value: parameter tags, or the call written out as XML
        private static readonly string[] _callMarkup = ["<parameter name=", "</parameter>", "<invoke", "</invoke>"];

        /// <summary>Where the first piece of call markup is in a tool input, and the text around it, for the log</summary>
        public static string MarkupExcerpt(JsonElement input) => Find(input, "$") ?? "";

        private static string Find(JsonElement element, string path)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    var text = element.GetString();
                    foreach (var marker in _callMarkup)
                    {
                        var at = text.IndexOf(marker, StringComparison.Ordinal);
                        if (at >= 0)
                        {
                            var start = Math.Max(0, at - 150);
                            return $"{path} has {marker}: ...{text.Substring(start, Math.Min(text.Length, at + 150) - start)}...";
                        }
                    }
                    return null;
                case JsonValueKind.Array:
                    return element.EnumerateArray().Select((item, i) => Find(item, $"{path}[{i}]")).FirstOrDefault(f => f != null);
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.Name.StartsWith("<parameter", StringComparison.Ordinal))
                            return $"{path} has a property named {property.Name}";
                        if (Find(property.Value, $"{path}.{property.Name}") is string found)
                            return found;
                    }
                    return null;
                default:
                    return null;
            }
        }

        /// <summary>Whether a tool input holds the model's own call markup instead of JSON values</summary>
        public static bool HasCallMarkup(JsonElement input) => input.ValueKind switch
        {
            JsonValueKind.String => _callMarkup.Any(m => input.GetString().Contains(m, StringComparison.Ordinal)),
            JsonValueKind.Array => input.EnumerateArray().Any(HasCallMarkup),
            JsonValueKind.Object => input.EnumerateObject().Any(p => p.Name.StartsWith("<parameter", StringComparison.Ordinal) || HasCallMarkup(p.Value)),
            _ => false
        };

        /// <summary>
        /// Models sometimes send a large array or object of a tool input as a string holding its JSON.
        /// Parses such strings back wherever the schema expects an array or object.
        /// </summary>
        public static JsonElement RepairStringifiedJson(JsonElement input, JsonObject schema)
        {
            var node = ToNode(input);
            return JsonSerializer.SerializeToElement(Repair(node, schema));
        }

        /// <summary>
        /// An editable copy of a model's JSON. A model sometimes repeats a key in one object; JSON allows it, but a
        /// <see cref="JsonObject"/> throws the first time it is read, so the copy keeps the last value, as a JSON reader does.
        /// </summary>
        public static JsonNode ToNode(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var obj = new JsonObject();
                    foreach (var property in element.EnumerateObject())
                        obj[property.Name] = ToNode(property.Value);
                    return obj;
                case JsonValueKind.Array:
                    var array = new JsonArray();
                    foreach (var item in element.EnumerateArray())
                        array.Add(ToNode(item));
                    return array;
                case JsonValueKind.Undefined:
                case JsonValueKind.Null:
                    return null;
                default:
                    return JsonValue.Create(element.Clone());
            }
        }

        private static string FirstRequiredText(JsonNode schema) =>
            (schema?["required"] as JsonArray)?
                .Select(r => r?.GetValue<string>())
                .FirstOrDefault(r => r != null && schema["properties"]?[r]?["type"]?.GetValue<string>() == "string");

        private static JsonNode Repair(JsonNode node, JsonNode schema)
        {
            var type = schema?["type"]?.GetValue<string>();
            if (node is JsonValue value && value.TryGetValue<string>(out var text) && type is "array" or "object")
            {
                try
                {
                    using var document = JsonDocument.Parse(text);
                    var parsed = ToNode(document.RootElement);
                    if ((type == "array" && parsed is JsonArray) || (type == "object" && parsed is JsonObject))
                        node = parsed;
                }
                catch (JsonException)
                {
                }
                // a list of strings written as one plain string is a list of that one string
                if (node is JsonValue && type == "array" && schema?["items"]?["type"]?.GetValue<string>() == "string")
                    node = string.IsNullOrWhiteSpace(text) ? new JsonArray() : new JsonArray(JsonValue.Create(text));
                // an object written as a paragraph is that paragraph in the object's first required text field
                else if (node is JsonValue && type == "object" && FirstRequiredText(schema) is string field)
                    node = string.IsNullOrWhiteSpace(text) ? null : new JsonObject { [field] = text };
            }
            // the other shapes a model writes a value in: a number as text, text as a number, a single item for a list
            if (node is JsonValue scalar)
            {
                if (type == "integer" && scalar.TryGetValue<string>(out var digits))
                    node = int.TryParse(digits.Trim(), out var number) ? JsonValue.Create(number) : null;
                else if (type == "string" && !scalar.TryGetValue<string>(out _))
                    node = JsonValue.Create(scalar.ToJsonString());
                else if (type == "array" && !scalar.TryGetValue<string>(out _))
                    node = new JsonArray(scalar.DeepClone());
            }
            else if (type == "string" && node is JsonObject or JsonArray)
                node = JsonValue.Create(node.ToJsonString());
            else if (type == "array" && node is JsonObject single)
                node = new JsonArray(single.DeepClone());
            if (node is JsonArray array && schema?["items"] is JsonNode items)
            {
                for (var i = 0; i < array.Count; i++)
                    array[i] = Repair(array[i]?.DeepClone(), items);
            }
            else if (node is JsonObject obj && schema?["properties"] is JsonObject properties)
            {
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    if (properties[name] is JsonNode propertySchema)
                        obj[name] = Repair(obj[name]?.DeepClone(), propertySchema);
                }
            }
            return node;
        }

        // Streams the answer, so a long one is not cut off by the request timeout, and returns it as one message.
        private async Task<JsonElement> InvokeAsync(AmazonBedrockRuntimeClient client, string modelId, JsonObject body, string toolName, CancellationToken ct)
        {
            var request = new InvokeModelWithResponseStreamRequest
            {
                ModelId = modelId,
                ContentType = "application/json",
                Accept = "application/json",
                Body = new MemoryStream(Encoding.UTF8.GetBytes(body.ToJsonString()))
            };
            using var response = await client.InvokeModelWithResponseStreamAsync(request, ct);
            var message = new StreamedMessage();
            try
            {
                await foreach (var item in response.Body.WithCancellation(ct))
                {
                    if (item is PayloadPart part)
                    {
                        using var reader = new StreamReader(part.Bytes);
                        var partialJson = message.Add(await reader.ReadToEndAsync(ct));
                        if (partialJson != null)
                            await _progress.AddAsync(partialJson, ct);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not InvalidOperationException)
            {
                // Bedrock ended the stream with an error event; its type and inner message say more than the outer one
                var inner = ex.InnerException == null ? "" : $" ({ex.InnerException.GetType().Name}: {ex.InnerException.Message})";
                _logger.LogWarning(ex, "The {Tool} stream from {Model} failed after {Usage}: {Type}: {Message}{Inner}",
                    toolName, modelId, message.Usage, ex.GetType().Name, ex.Message, inner);
                throw new AmazonBedrockRuntimeException($"Bedrock stopped the answer with an error: {ex.GetType().Name}: {ex.Message}{inner}", ex);
            }
            await _progress.CompleteAsync(ct);

            _logger.LogInformation("AI draft {Tool} on {Model} stopped with {StopReason}: {Usage}",
                toolName, modelId, message.StopReason, message.Usage);
            JsonElement result;
            try
            {
                result = message.ToMessage();
                if (message.Salvaged != null)
                    _logger.LogWarning("The {Tool} answer from {Model} was not quite JSON: {Repair}", toolName, modelId, message.Salvaged);
            }
            catch (JsonException ex)
            {
                // the call is billed even though its answer cannot be read
                await _usage.RecordAsync(modelId, message.ToMessage(withInput: false), 0, ct);
                if (message.StopReason == "max_tokens")
                {
                    _logger.LogWarning(ex, "The {Tool} answer from {Model} ran out of output tokens", toolName, modelId);
                    throw new InvalidOperationException("The model ran out of output tokens before it finished. Try again, or ask for fewer moves or injects.");
                }
                // the model finished but wrote JSON that does not parse; where it broke shows how
                _logger.LogWarning(ex, "The {Tool} answer from {Model} stopped with {StopReason} but is not valid JSON near: {Excerpt}",
                    toolName, modelId, message.StopReason, message.Excerpt(ex.BytePositionInLine));
                throw new UnreadableAnswerException("The model's answer could not be read. Try again.");
            }
            // every call is billed, a refused one or one retried on the fallback model too
            await _usage.RecordAsync(modelId, result, 0, ct);
            return result;
        }

        private static string StopReason(JsonElement message) =>
            message.TryGetProperty("stop_reason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null;

        /// <summary>
        /// Returns the input of the response's tool_use block, or explains why there is none.
        /// </summary>
        public static JsonElement ReadToolInput(JsonElement response)
        {
            var stopReason = StopReason(response);
            if (stopReason == "max_tokens")
                throw new InvalidOperationException("The model ran out of output tokens before finishing the draft. Ask for a smaller draft.");
            // a safety classifier declined the request: HTTP 200 with no tool_use, and a category in stop_details
            if (stopReason == "refusal")
            {
                var category = response.TryGetProperty("stop_details", out var details)
                    && details.ValueKind == JsonValueKind.Object
                    && details.TryGetProperty("category", out var c)
                    && c.ValueKind == JsonValueKind.String
                        ? c.GetString()
                        : null;
                throw new InvalidOperationException(category is null
                    ? "The model declined to write this draft. Reword the request."
                    : $"The model declined to write this draft (safety category: {category}). Reword the request.");
            }

            var toolUse = response.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
                ? content.EnumerateArray().FirstOrDefault(c => c.TryGetProperty("type", out var type) && type.GetString() == "tool_use")
                : default;
            if (toolUse.ValueKind == JsonValueKind.Undefined)
                throw new InvalidOperationException("The model answered without returning a draft. Try again or reword the request.");

            return toolUse.GetProperty("input").Clone();
        }
    }

    /// <summary>A model's answer that arrived whole but is not JSON that can be read</summary>
    public class UnreadableAnswerException : InvalidOperationException
    {
        public UnreadableAnswerException(string message) : base(message) { }
    }

    /// <summary>
    /// Puts the events of a streamed Messages response back together into the message a non-streamed call returns:
    /// stop_reason, stop_details, usage, and the content blocks with each tool_use input parsed.
    /// </summary>
    public class StreamedMessage
    {
        private readonly JsonObject _message = new() { ["content"] = new JsonArray() };
        private readonly SortedDictionary<int, JsonObject> _blocks = new();
        private readonly Dictionary<int, StringBuilder> _partialJson = new();

        /// <summary>Adds one event; returns the piece of a tool input it carried, if any</summary>
        public string Add(string eventJson)
        {
            var e = JsonNode.Parse(eventJson)?.AsObject();
            switch (e?["type"]?.GetValue<string>())
            {
                case "message_start":
                    if (e["message"]?["usage"] is JsonObject startUsage)
                        MergeUsage(startUsage);
                    break;
                case "content_block_start":
                    var index = e["index"].GetValue<int>();
                    _blocks[index] = e["content_block"].DeepClone().AsObject();
                    break;
                case "content_block_delta":
                    return AddDelta(e["index"].GetValue<int>(), e["delta"].AsObject());
                case "message_delta":
                    if (e["delta"] is JsonObject delta)
                    {
                        if (delta["stop_reason"] is JsonNode stopReason)
                            _message["stop_reason"] = stopReason.DeepClone();
                        if (delta["stop_details"] is JsonNode stopDetails)
                            _message["stop_details"] = stopDetails.DeepClone();
                    }
                    if (e["usage"] is JsonObject deltaUsage)
                        MergeUsage(deltaUsage);
                    break;
                case "error":
                    throw new InvalidOperationException($"The model stopped with an error: {e["error"]?["message"]?.GetValue<string>() ?? eventJson}");
            }
            return null;
        }

        /// <summary>The tool input around a position, to show where it stopped parsing</summary>
        public string Excerpt(long? position)
        {
            var json = string.Concat(_partialJson.Values.Select(b => b.ToString()));
            var at = (int)Math.Clamp(position ?? json.Length, 0, json.Length);
            var start = Math.Max(0, at - 200);
            return json.Substring(start, Math.Min(json.Length, at + 100) - start);
        }

        /// <summary>Why the model stopped, once the stream has said</summary>
        public string StopReason => _message["stop_reason"]?.GetValue<string>();

        public string Usage => _message["usage"]?.ToJsonString();

        /// <summary>The message; without the tool inputs, which an answer cut off part-way cannot give, its usage can still be recorded</summary>
        public JsonElement ToMessage(bool withInput = true)
        {
            var content = _message["content"].AsArray();
            content.Clear();
            // the inputs that parse; a stray second tool_use block the model wrote in its own markup is dropped when another parsed
            var inputs = withInput ? _partialJson.ToDictionary(p => p.Key, p => p.Value.Length == 0 ? (new JsonObject(), (JsonException)null) : ParseInput(p.Value.ToString())) : null;
            if (inputs != null && inputs.Values.Any(i => i.Item1 != null) && inputs.Values.Any(i => i.Item2 != null))
                Salvaged = (Salvaged == null ? "" : Salvaged + "; ") + $"{inputs.Values.Count(i => i.Item2 != null)} tool_use block(s) that were not JSON were dropped";
            foreach (var (index, block) in _blocks)
            {
                if (inputs != null && inputs.TryGetValue(index, out var input))
                {
                    if (input.Item1 == null)
                    {
                        if (inputs.Values.Any(i => i.Item1 != null))
                            continue;
                        throw input.Item2;
                    }
                    block["input"] = input.Item1;
                }
                content.Add(block.DeepClone());
            }
            return JsonSerializer.SerializeToElement(_message);
        }

        /// <summary>What was done to read the answer when it was not quite JSON, or null when it was</summary>
        public string Salvaged { get; private set; }

        // a model sometimes writes a stray tag or a word after the JSON; the object it wrote is still whole, so it is kept
        private (JsonNode, JsonException) ParseInput(string json)
        {
            try
            {
                return (BedrockAiModelClient.ToNode(JsonDocument.Parse(json).RootElement), null);
            }
            catch (JsonException ex)
            {
                var start = json.IndexOf('{');
                var end = json.LastIndexOf('}');
                if (start < 0 || end <= start)
                    return (null, ex);
                try
                {
                    var node = BedrockAiModelClient.ToNode(JsonDocument.Parse(json.Substring(start, end - start + 1)).RootElement);
                    Salvaged = "the text after the JSON was cut off";
                    return (node, null);
                }
                catch (JsonException)
                {
                    return (null, ex);
                }
            }
        }

        private string AddDelta(int index, JsonObject delta)
        {
            if (!_blocks.TryGetValue(index, out var block))
                return null;
            switch (delta["type"]?.GetValue<string>())
            {
                case "input_json_delta":
                    if (!_partialJson.TryGetValue(index, out var json))
                        _partialJson[index] = json = new StringBuilder();
                    var piece = delta["partial_json"]?.GetValue<string>();
                    json.Append(piece);
                    return piece;
                case "text_delta":
                    block["text"] = (block["text"]?.GetValue<string>() ?? "") + delta["text"]?.GetValue<string>();
                    break;
            }
            return null;
        }

        // message_start carries the input counts, message_delta the running output counts; later values win
        private void MergeUsage(JsonObject usage)
        {
            if (_message["usage"] is not JsonObject merged)
                _message["usage"] = merged = new JsonObject();
            foreach (var (key, value) in usage)
                merged[key] = value?.DeepClone();
        }
    }
}
