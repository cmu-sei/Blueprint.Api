// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

namespace Blueprint.Api.Infrastructure.Options
{
    public class AiOptions
    {
        public bool Enabled { get; set; }
        // AWS region of the Bedrock endpoint. Credentials come from the default AWS chain.
        public string Region { get; set; } = "us-east-1";
        // Bedrock model or inference profile id, e.g. us.anthropic.claude-sonnet-5
        public string ModelId { get; set; }
        public int MaxTokens { get; set; } = 16000;
        public int MaxInjects { get; set; } = 60;
        // the most competencies a draft may choose from; each one is a line of the prompt
        public int MaxCompetencies { get; set; } = 2500;
        public int TimeoutSeconds { get; set; } = 300;
    }
}
