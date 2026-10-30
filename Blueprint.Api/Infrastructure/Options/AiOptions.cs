// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Blueprint.Api.Infrastructure.Options
{
    public class AiOptions
    {
        public bool Enabled { get; set; }
        // AWS region of the Bedrock endpoint. Credentials come from the default AWS chain.
        public string Region { get; set; } = "us-east-1";
        // Bedrock model or inference profile id, e.g. us.anthropic.claude-sonnet-5
        public string ModelId { get; set; }
        // tried once when ModelId's safety classifiers decline a request; empty for no retry
        public string FallbackModelId { get; set; }
        // answers are streamed, so this can be well past what one unstreamed request finishes in
        public int MaxTokens { get; set; } = 64000;
        public int MaxInjects { get; set; } = 60;
        // the most competencies a draft may choose from; each one is a line of the prompt
        public int MaxCompetencies { get; set; } = 2500;
        public int TimeoutSeconds { get; set; } = 600;
        // Bedrock image model for news photos, e.g. stability.stable-image-core-v1:1; empty for no photos
        public string ImageModelId { get; set; }
        // Stability's image models are offered in fewer regions than the text models
        public string ImageRegion { get; set; } = "us-west-2";
        // what each model costs, keyed by model or inference profile id, so the AI usage of a MSEL can be priced;
        // a model without a price is counted in tokens only
        public Dictionary<string, AiPrice> Prices { get; set; } = new Dictionary<string, AiPrice>();
        // the model each task uses where it is not the default above, keyed by the task the usage records name:
        // design, injects, rows, article, profile, value, assist, draft. Any field left empty is the default's.
        public Dictionary<string, AiTaskModel> Models { get; set; } = new Dictionary<string, AiTaskModel>();
        // how hard a model thinks before it answers: low, medium, high, xhigh or max; empty for the model's own default.
        // Lower is faster and spends fewer tokens; a task's own Effort wins.
        public string Effort { get; set; }
        // how many of a drafted MSEL's rows, articles and profiles are written at once
        public int Parallel { get; set; } = 6;

        public const string Bedrock = "Bedrock";
        public const string OpenAi = "OpenAI";

        /// <summary>The provider and model a task's calls go to: its own settings where it has them, the defaults where not</summary>
        public AiTaskModel ModelFor(string task)
        {
            var own = task == null ? null : Models.FirstOrDefault(m => string.Equals(m.Key, task, StringComparison.OrdinalIgnoreCase)).Value;
            var provider = string.IsNullOrWhiteSpace(own?.Provider) ? Bedrock : own.Provider.Trim();
            var bedrock = string.Equals(provider, Bedrock, StringComparison.OrdinalIgnoreCase);
            return new AiTaskModel
            {
                Provider = bedrock ? Bedrock : provider,
                // another provider's models are not Bedrock's, so it takes nothing of the default but the limits
                ModelId = Pick(own?.ModelId, bedrock ? ModelId : null),
                FallbackModelId = Pick(own?.FallbackModelId, bedrock ? FallbackModelId : null),
                Region = Pick(own?.Region, Region),
                BaseUrl = own?.BaseUrl,
                ApiKey = own?.ApiKey,
                MaxTokens = own?.MaxTokens > 0 ? own.MaxTokens : MaxTokens,
                Effort = Pick(own?.Effort, Effort)
            };

            static string Pick(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }
    }

    /// <summary>Where one task's model calls go</summary>
    public class AiTaskModel
    {
        // Bedrock, the default, or OpenAI for the OpenAI API and servers that offer it, such as vLLM and Ollama
        public string Provider { get; set; }
        public string ModelId { get; set; }
        public string FallbackModelId { get; set; }
        // Bedrock's region
        public string Region { get; set; }
        // an OpenAI-style endpoint, e.g. https://api.openai.com/v1; chat/completions is added to it
        public string BaseUrl { get; set; }
        public string ApiKey { get; set; }
        public int MaxTokens { get; set; }
        // low, medium, high, xhigh or max; empty for the default
        public string Effort { get; set; }
    }

    /// <summary>A model's price in US dollars: per million tokens of each kind, and per image</summary>
    public class AiPrice
    {
        public decimal InputPerMillion { get; set; }
        public decimal OutputPerMillion { get; set; }
        public decimal CacheReadPerMillion { get; set; }
        public decimal CacheWritePerMillion { get; set; }
        public decimal PerImage { get; set; }
    }
}
