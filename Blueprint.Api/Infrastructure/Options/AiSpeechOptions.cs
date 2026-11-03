// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Blueprint.Api.Infrastructure.Options
{
    /// <summary>
    /// Where the voices of audio and video injects come from: Amazon Polly, or a server offering OpenAI's /v1/audio/speech, such as
    /// the OpenAI API or a local text-to-speech server on a network with no internet. Ai:Speech in configuration.
    /// </summary>
    public class AiSpeechOptions
    {
        public const string Polly = "Polly";
        public const string OpenAi = "OpenAI";

        // Polly or OpenAI; empty for no audio
        public string Provider { get; set; }
        // Polly's AWS region; credentials come from the default AWS chain, as Bedrock's do, and need polly:SynthesizeSpeech
        public string Region { get; set; } = "us-east-1";
        // Polly's engine: neural (the default), standard, long-form or generative. Only neural reads in the newscaster style.
        public string Engine { get; set; } = "neural";
        // the OpenAI-style endpoint, e.g. https://api.openai.com/v1; audio/speech is added to it
        public string BaseUrl { get; set; }
        public string ApiKey { get; set; }
        // the OpenAI-style speech model, e.g. tts-1
        public string Model { get; set; }
        // the voices a clip may use; empty for the provider's usual English voices
        public List<AiVoice> Voices { get; set; } = new List<AiVoice>();
        // the most characters one clip may voice, so an article read aloud stays a few minutes long and its cost known
        public int MaxCharacters { get; set; } = 6000;
        public int TimeoutSeconds { get; set; } = 120;

        public bool IsPolly => string.Equals(Provider?.Trim(), Polly, StringComparison.OrdinalIgnoreCase);
        public bool IsOpenAi => string.Equals(Provider?.Trim(), OpenAi, StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether a provider is named and has what it needs</summary>
        public bool IsConfigured => IsPolly || (IsOpenAi && !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model));

        /// <summary>The engine Polly is asked for, in the form it takes: neural, standard, long-form or generative</summary>
        public string PollyEngine => string.IsNullOrWhiteSpace(Engine) ? "neural" : Engine.Trim().ToLowerInvariant();

        /// <summary>
        /// The model a voiced clip is recorded and priced under: polly-neural for Polly's neural voices, or the OpenAI-style model.
        /// A price keyed polly_neural is the same price, as environment variables write it.
        /// </summary>
        public string UsageModel => IsPolly ? "polly-" + PollyEngine : Model?.Trim();

        /// <summary>The configured voices with a name, or the provider's usual ones when none are</summary>
        public IReadOnlyList<AiVoice> EffectiveVoices()
        {
            // configuration binding adds to a list it is given, so the defaults are never the list itself
            var own = Voices?.Where(v => !string.IsNullOrWhiteSpace(v?.Name)).ToList() ?? new List<AiVoice>();
            if (own.Count > 0)
                return own;
            return IsPolly ? PollyVoices : IsOpenAi ? OpenAiVoices : Array.Empty<AiVoice>();
        }

        /// <summary>Polly's adult English neural voices; the newscaster style is Matthew's, Joanna's and Amy's</summary>
        public static readonly IReadOnlyList<AiVoice> PollyVoices =
        [
            new("Matthew", "Male", "US English", true),
            new("Joanna", "Female", "US English", true),
            new("Gregory", "Male", "US English"),
            new("Danielle", "Female", "US English"),
            new("Stephen", "Male", "US English"),
            new("Ruth", "Female", "US English"),
            new("Joey", "Male", "US English"),
            new("Kendra", "Female", "US English"),
            new("Salli", "Female", "US English"),
            new("Kimberly", "Female", "US English"),
            new("Amy", "Female", "British English", true),
            new("Brian", "Male", "British English"),
            new("Arthur", "Male", "British English"),
            new("Emma", "Female", "British English"),
            new("Olivia", "Female", "Australian English"),
            new("Niamh", "Female", "Irish English"),
            new("Aria", "Female", "New Zealand English"),
            new("Ayanda", "Female", "South African English")
        ];

        /// <summary>The voices OpenAI's tts-1 offers; many local servers answer to the same names</summary>
        public static readonly IReadOnlyList<AiVoice> OpenAiVoices =
        [
            new("onyx", "Male", "English"),
            new("nova", "Female", "English"),
            new("echo", "Male", "English"),
            new("shimmer", "Female", "English"),
            new("fable", "Male", "English"),
            new("alloy", "Female", "English")
        ];
    }

    /// <summary>One voice a clip may use</summary>
    public class AiVoice
    {
        public AiVoice() { }

        public AiVoice(string name, string gender, string label, bool news = false)
        {
            Name = name;
            Gender = gender;
            Label = label;
            News = news;
        }

        // the provider's voice id, e.g. Matthew or onyx
        public string Name { get; set; }
        // Female or Male, so the model gives a speaker a voice that fits
        public string Gender { get; set; }
        // the accent or anything else that tells voices apart, e.g. British English
        public string Label { get; set; }
        // whether the voice reads in a newscaster style: Polly's Matthew, Joanna and Amy on the neural engine
        public bool News { get; set; }
    }
}
