// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Blueprint.Api.Infrastructure.Options;

namespace Blueprint.Api.Services
{
    public interface IAiImageClient
    {
        /// <summary>Whether an image model is set, so articles may ask for photos</summary>
        bool IsConfigured { get; }

        /// <summary>A realistic news photo of the scene, as a JPEG data URI</summary>
        Task<string> PhotoAsync(string scene, CancellationToken ct);
    }

    /// <summary>
    /// News photos from a Stability image model on Bedrock. The models cannot spell, so the photos are asked to hold no
    /// text at all; anything with words in it (logos, maps, charts) stays an SVG the article model draws.
    /// </summary>
    public class BedrockAiImageClient : IAiImageClient
    {
        public const string Style = "Photojournalism news photo, {0}, natural light, realistic, shot on a 35mm lens";
        public const string NegativePrompt =
            "text, letters, words, writing, numbers, signage, caution tape, banners, screens with text, watermark, logo, flag, insignia, " +
            "recognizable face, celebrity, cartoon, illustration";

        private readonly AiOptions _options;
        private readonly IAiUsageRecorder _usage;

        public BedrockAiImageClient(AiOptions options, IAiUsageRecorder usage = null)
        {
            _options = options;
            _usage = usage;
        }

        public bool IsConfigured => _options.Enabled && !string.IsNullOrWhiteSpace(_options.ImageModelId);

        public async Task<string> PhotoAsync(string scene, CancellationToken ct)
        {
            if (!IsConfigured)
                throw new InvalidOperationException("No image model is configured on this server.");
            var body = new JsonObject
            {
                ["prompt"] = string.Format(Style, scene.Trim().TrimEnd('.')),
                ["negative_prompt"] = NegativePrompt,
                ["aspect_ratio"] = "16:9",
                ["output_format"] = "jpeg"
            };
            var config = new AmazonBedrockRuntimeConfig
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(_options.ImageRegion),
                Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds)
            };
            using var client = new AmazonBedrockRuntimeClient(config);
            var response = await client.InvokeModelAsync(new InvokeModelRequest
            {
                ModelId = _options.ImageModelId,
                ContentType = "application/json",
                Accept = "application/json",
                Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(body))
            }, ct);
            if (_usage != null)
                await _usage.RecordAsync(_options.ImageModelId, default, 1, ct);
            return ReadPhoto(JsonDocument.Parse(response.Body).RootElement);
        }

        /// <summary>The first image of a Stability answer as a JPEG data URI; a filtered or empty answer says why</summary>
        public static string ReadPhoto(JsonElement answer)
        {
            var reason = answer.TryGetProperty("finish_reasons", out var reasons) && reasons.ValueKind == JsonValueKind.Array
                ? reasons.EnumerateArray().Select(r => r.ValueKind == JsonValueKind.String ? r.GetString() : null).FirstOrDefault()
                : null;
            if (reason != null)
                throw new InvalidOperationException($"The image model made no photo ({reason}).");
            var image = answer.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array
                ? images.EnumerateArray().Select(i => i.GetString()).FirstOrDefault(i => !string.IsNullOrEmpty(i))
                : null;
            return image == null
                ? throw new InvalidOperationException("The image model made no photo.")
                : "data:image/jpeg;base64," + image;
        }
    }
}
