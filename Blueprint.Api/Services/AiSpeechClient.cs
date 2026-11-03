// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.Polly;
using Amazon.Polly.Model;
using Blueprint.Api.Infrastructure.Options;

namespace Blueprint.Api.Services
{
    public interface IAiSpeechClient
    {
        /// <summary>Whether a speech provider is set, so injects may be voiced</summary>
        bool IsConfigured { get; }

        /// <summary>The voices a clip may use</summary>
        IReadOnlyList<AiVoice> Voices { get; }

        /// <summary>
        /// The text read aloud in one voice, as MP3, with when each sentence and word starts. A style of
        /// <see cref="AiSpeechStyle.News"/> reads it as a newscaster, where the voice can.
        /// </summary>
        Task<AiSpeech> SpeakAsync(string text, string voice, string style, CancellationToken ct);
    }

    public static class AiSpeechStyle
    {
        public const string News = "news";
    }

    /// <summary>When a sentence or word starts, in milliseconds from the start of the clip</summary>
    public class AiSpeechMark
    {
        public int Time { get; set; }
        public string Text { get; set; }
    }

    /// <summary>One voiced text</summary>
    public class AiSpeech
    {
        public byte[] Mp3 { get; set; } = [];
        public int DurationMs { get; set; }
        public List<AiSpeechMark> Sentences { get; set; } = new List<AiSpeechMark>();
        public List<AiSpeechMark> Words { get; set; } = new List<AiSpeechMark>();
    }

    /// <summary>Sends each clip to the provider Ai:Speech names</summary>
    public class AiSpeechRouter : IAiSpeechClient
    {
        private readonly AiSpeechOptions _options;
        private readonly PollySpeechClient _polly;
        private readonly OpenAiSpeechClient _openAi;

        public AiSpeechRouter(AiOptions options, PollySpeechClient polly, OpenAiSpeechClient openAi)
        {
            _options = options.Speech ?? new AiSpeechOptions();
            _polly = polly;
            _openAi = openAi;
        }

        public bool IsConfigured => _options.IsConfigured;

        public IReadOnlyList<AiVoice> Voices => _options.EffectiveVoices();

        public Task<AiSpeech> SpeakAsync(string text, string voice, string style, CancellationToken ct)
        {
            if (!_options.IsConfigured)
                throw new InvalidOperationException("No speech provider is configured on this server.");
            return _options.IsPolly ? _polly.SpeakAsync(text, voice, style, ct) : _openAi.SpeakAsync(text, voice, style, ct);
        }
    }

    /// <summary>
    /// Voices from Amazon Polly. Each piece of text is asked for twice, once as MP3 and once as speech marks, and Polly bills both.
    /// Text past what one request takes is split at sentences and the pieces joined, their marks moved along by the audio before them.
    /// </summary>
    public class PollySpeechClient : IAiSpeechClient
    {
        // Polly bills up to 3,000 characters a request and takes 6,000 with the SSML around them
        public const int MaxRequestCharacters = 2800;
        private readonly AiOptions _options;
        private readonly IAiUsageRecorder _usage;
        private readonly IAmazonPolly _polly;

        // polly is the Polly client; without one, each clip makes its own for Ai:Speech:Region
        public PollySpeechClient(AiOptions options, IAiUsageRecorder usage = null, IAmazonPolly polly = null)
        {
            _options = options;
            _usage = usage;
            _polly = polly;
        }

        private AiSpeechOptions Speech => _options.Speech ?? new AiSpeechOptions();

        public bool IsConfigured => Speech.IsPolly;

        public IReadOnlyList<AiVoice> Voices => Speech.EffectiveVoices();

        public async Task<AiSpeech> SpeakAsync(string text, string voice, string style, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("There is nothing to read aloud.");
            var engine = Speech.PollyEngine;
            // only the neural engine reads as a newscaster
            var news = style == AiSpeechStyle.News && engine == "neural";
            var client = _polly ?? new AmazonPollyClient(new AmazonPollyConfig
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(Speech.Region),
                Timeout = TimeSpan.FromSeconds(Speech.TimeoutSeconds)
            });
            try
            {
                var pieces = new List<AiSpeech>();
                foreach (var piece in SpeechText.Split(text, MaxRequestCharacters))
                    pieces.Add(await SpeakPieceAsync(client, piece, voice, engine, news, ct));
                return SpeechText.Join(pieces);
            }
            finally
            {
                if (_polly == null)
                    client.Dispose();
            }
        }

        private async Task<AiSpeech> SpeakPieceAsync(IAmazonPolly client, string text, string voice, string engine, bool news, CancellationToken ct)
        {
            var ssml = SpeechText.Ssml(text, news);
            var audio = await client.SynthesizeSpeechAsync(new SynthesizeSpeechRequest
            {
                Engine = engine,
                VoiceId = voice,
                OutputFormat = OutputFormat.Mp3,
                SampleRate = "24000",
                TextType = TextType.Ssml,
                Text = ssml
            }, ct);
            var mp3 = await ReadAllAsync(audio.AudioStream, ct);
            await RecordAsync(audio.RequestCharacters, text, ct);

            var marks = await client.SynthesizeSpeechAsync(new SynthesizeSpeechRequest
            {
                Engine = engine,
                VoiceId = voice,
                OutputFormat = OutputFormat.Json,
                SpeechMarkTypes = ["sentence", "word"],
                TextType = TextType.Ssml,
                Text = ssml
            }, ct);
            var json = Encoding.UTF8.GetString(await ReadAllAsync(marks.AudioStream, ct));
            await RecordAsync(marks.RequestCharacters, text, ct);

            var (sentences, words) = ReadMarks(json);
            var speech = new AiSpeech { Mp3 = mp3, DurationMs = Mp3Audio.DurationMs(mp3), Sentences = sentences, Words = words };
            // a clip whose frames could not be read is as long as its last word, and a little
            if (speech.DurationMs == 0)
                speech.DurationMs = (words.Concat(sentences).Select(m => (int?)m.Time).Max() ?? 0) + 600;
            return speech;
        }

        private Task RecordAsync(int? billed, string text, CancellationToken ct) =>
            _usage == null ? Task.CompletedTask : _usage.RecordSpeechAsync(Speech.UsageModel, billed > 0 ? billed.Value : text.Length, ct);

        /// <summary>Polly's speech marks, one JSON object a line, as the sentences and words they time</summary>
        public static (List<AiSpeechMark> Sentences, List<AiSpeechMark> Words) ReadMarks(string json)
        {
            var sentences = new List<AiSpeechMark>();
            var words = new List<AiSpeechMark>();
            foreach (var line in (json ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                JsonElement mark;
                try
                {
                    mark = JsonDocument.Parse(line).RootElement;
                }
                catch (JsonException)
                {
                    continue;
                }
                var type = mark.TryGetProperty("type", out var t) ? t.GetString() : null;
                var value = SpeechText.Clean(mark.TryGetProperty("value", out var v) ? v.GetString() : null);
                var time = mark.TryGetProperty("time", out var ms) && ms.TryGetInt32(out var n) ? n : 0;
                if (string.IsNullOrEmpty(value))
                    continue;
                if (type == "sentence")
                    sentences.Add(new AiSpeechMark { Time = time, Text = value });
                else if (type == "word")
                    words.Add(new AiSpeechMark { Time = time, Text = value });
            }
            return (sentences, words);
        }

        private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken ct)
        {
            if (stream == null)
                return [];
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, ct);
            await stream.DisposeAsync();
            return memory.ToArray();
        }
    }

    /// <summary>
    /// Voices from a server offering OpenAI's /v1/audio/speech: the OpenAI API, or a local text-to-speech server for a network
    /// with no internet. It gives no timings, so each sentence's and word's start is estimated from its share of the characters.
    /// </summary>
    public class OpenAiSpeechClient : IAiSpeechClient
    {
        // OpenAI takes 4,096 characters a request
        public const int MaxRequestCharacters = 4000;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly AiOptions _options;
        private readonly IAiUsageRecorder _usage;

        public OpenAiSpeechClient(IHttpClientFactory httpClientFactory, AiOptions options, IAiUsageRecorder usage = null)
        {
            _httpClientFactory = httpClientFactory;
            _options = options;
            _usage = usage;
        }

        private AiSpeechOptions Speech => _options.Speech ?? new AiSpeechOptions();

        public bool IsConfigured => Speech.IsOpenAi && Speech.IsConfigured;

        public IReadOnlyList<AiVoice> Voices => Speech.EffectiveVoices();

        public async Task<AiSpeech> SpeakAsync(string text, string voice, string style, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(Speech.BaseUrl) || string.IsNullOrWhiteSpace(Speech.Model))
                throw new InvalidOperationException("The OpenAI speech provider needs Ai:Speech:BaseUrl and Ai:Speech:Model.");
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("There is nothing to read aloud.");
            var pieces = new List<AiSpeech>();
            foreach (var piece in SpeechText.Split(text, MaxRequestCharacters))
                pieces.Add(await SpeakPieceAsync(piece, voice, style, ct));
            return SpeechText.Join(pieces);
        }

        private async Task<AiSpeech> SpeakPieceAsync(string text, string voice, string style, CancellationToken ct)
        {
            var body = new JsonObject
            {
                ["model"] = Speech.Model.Trim(),
                ["input"] = text,
                ["voice"] = voice,
                ["response_format"] = "mp3"
            };
            // models that take instructions read in the style asked; the others ignore it
            if (style == AiSpeechStyle.News)
                body["instructions"] = "Read this as a television news anchor: clear, measured and authoritative.";
            using var request = new HttpRequestMessage(HttpMethod.Post, Speech.BaseUrl.TrimEnd('/') + "/audio/speech")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(Speech.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Speech.ApiKey.Trim());
            var client = _httpClientFactory.CreateClient("ai-speech");
            client.Timeout = TimeSpan.FromSeconds(Speech.TimeoutSeconds);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"The speech server answered {(int)response.StatusCode}: {(error.Length > 300 ? error[..300] : error)}");
            }
            var mp3 = await response.Content.ReadAsByteArrayAsync(ct);
            if (_usage != null)
                await _usage.RecordSpeechAsync(Speech.UsageModel, text.Length, ct);
            var duration = Mp3Audio.DurationMs(mp3);
            if (duration == 0)
                duration = SpeechText.EstimatedMs(text);
            var (sentences, words) = SpeechText.EstimateMarks(text, duration);
            return new AiSpeech { Mp3 = mp3, DurationMs = duration, Sentences = sentences, Words = words };
        }
    }

    /// <summary>Text made ready to read aloud, and voiced pieces joined into one clip</summary>
    public static class SpeechText
    {
        // a full stop, then space, then anything but a lower-case letter, which follows a quoted question rather than starts a sentence
        private static readonly Regex SentenceEnd = new(@"(?<=[.!?][""'”’)\]]*)\s+(?=[^\s\p{Ll}])");
        private static readonly Regex Space = new(@"\s+");

        /// <summary>The text's sentences, in order</summary>
        public static List<string> Sentences(string text) =>
            SentenceEnd.Split(Space.Replace(text ?? "", " ").Trim()).Where(s => s.Length > 0).ToList();

        /// <summary>
        /// The text in pieces no longer than max, each ending at a sentence; a sentence longer than max is broken between words,
        /// and a word longer than max is cut.
        /// </summary>
        public static List<string> Split(string text, int max)
        {
            var pieces = new List<string>();
            var current = new StringBuilder();
            void Add(string part)
            {
                if (current.Length > 0 && current.Length + 1 + part.Length > max)
                {
                    pieces.Add(current.ToString());
                    current.Clear();
                }
                if (current.Length > 0)
                    current.Append(' ');
                current.Append(part);
            }
            foreach (var sentence in Sentences(text))
            {
                if (sentence.Length <= max)
                {
                    Add(sentence);
                    continue;
                }
                foreach (var word in sentence.Split(' '))
                    for (var i = 0; i < word.Length; i += max)
                        Add(word.Substring(i, Math.Min(max, word.Length - i)));
            }
            if (current.Length > 0)
                pieces.Add(current.ToString());
            return pieces;
        }

        /// <summary>The text as SSML, read as a newscaster when asked</summary>
        public static string Ssml(string text, bool news)
        {
            var escaped = WebUtility.HtmlEncode(Space.Replace(text ?? "", " ").Trim());
            return news ? $"<speak><amazon:domain name=\"news\">{escaped}</amazon:domain></speak>" : $"<speak>{escaped}</speak>";
        }

        /// <summary>A mark's text without the SSML or entities it may carry</summary>
        public static string Clean(string value) =>
            string.IsNullOrEmpty(value) ? value : Space.Replace(WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]*>", "")), " ").Trim();

        /// <summary>About how long the text takes to read: 15 characters a second</summary>
        public static int EstimatedMs(string text) => (int)Math.Round((text?.Length ?? 0) / 15.0 * 1000);

        /// <summary>When each sentence and word starts, as its share of the characters before it of a clip durationMs long</summary>
        public static (List<AiSpeechMark> Sentences, List<AiSpeechMark> Words) EstimateMarks(string text, int durationMs)
        {
            var sentences = new List<AiSpeechMark>();
            var words = new List<AiSpeechMark>();
            var all = Sentences(text);
            var total = all.Sum(s => s.Length + 1);
            if (total == 0)
                return (sentences, words);
            var before = 0;
            foreach (var sentence in all)
            {
                sentences.Add(new AiSpeechMark { Time = (int)((long)durationMs * before / total), Text = sentence });
                var at = before;
                foreach (var word in sentence.Split(' '))
                {
                    words.Add(new AiSpeechMark { Time = (int)((long)durationMs * at / total), Text = word });
                    at += word.Length + 1;
                }
                before += sentence.Length + 1;
            }
            return (sentences, words);
        }

        /// <summary>Pieces voiced one after another as one clip: the MP3 frames end to end, and each piece's marks after those before it</summary>
        public static AiSpeech Join(IReadOnlyList<AiSpeech> pieces)
        {
            if (pieces.Count == 1)
                return pieces[0];
            var joined = new AiSpeech();
            using var mp3 = new MemoryStream();
            var offset = 0;
            foreach (var piece in pieces)
            {
                mp3.Write(piece.Mp3);
                joined.Sentences.AddRange(piece.Sentences.Select(m => new AiSpeechMark { Time = m.Time + offset, Text = m.Text }));
                joined.Words.AddRange(piece.Words.Select(m => new AiSpeechMark { Time = m.Time + offset, Text = m.Text }));
                offset += piece.DurationMs;
            }
            joined.Mp3 = mp3.ToArray();
            joined.DurationMs = offset;
            return joined;
        }
    }

    /// <summary>How long MP3 audio plays, from its frame headers</summary>
    public static class Mp3Audio
    {
        private static readonly int[] SampleRates = [44100, 48000, 32000];
        private static readonly int[][] Bitrates =
        [
            [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448], // MPEG-1 layer I
            [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384],    // MPEG-1 layer II
            [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320],     // MPEG-1 layer III
            [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256],    // MPEG-2 and 2.5 layer I
            [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160]          // MPEG-2 and 2.5 layers II and III
        ];

        /// <summary>The milliseconds the frames play; 0 when there are none</summary>
        public static int DurationMs(byte[] data)
        {
            if (data == null)
                return 0;
            var i = 0;
            // an ID3v2 tag before the frames
            if (data.Length >= 10 && data[0] == 'I' && data[1] == 'D' && data[2] == '3')
                i = 10 + ((data[6] & 0x7F) << 21 | (data[7] & 0x7F) << 14 | (data[8] & 0x7F) << 7 | (data[9] & 0x7F)) + ((data[5] & 0x10) != 0 ? 10 : 0);
            double seconds = 0;
            while (i + 4 <= data.Length)
            {
                var frame = Frame(data, i);
                if (frame == null)
                {
                    i++;
                    continue;
                }
                seconds += frame.Value.Seconds;
                i += frame.Value.Length;
            }
            return (int)Math.Round(seconds * 1000);
        }

        private static (int Length, double Seconds)? Frame(byte[] data, int i)
        {
            if (data[i] != 0xFF || (data[i + 1] & 0xE0) != 0xE0)
                return null;
            var version = (data[i + 1] >> 3) & 3;   // 0 MPEG-2.5, 2 MPEG-2, 3 MPEG-1
            var layer = (data[i + 1] >> 1) & 3;     // 1 layer III, 2 layer II, 3 layer I
            var bitrateIndex = (data[i + 2] >> 4) & 0xF;
            var rateIndex = (data[i + 2] >> 2) & 3;
            var padding = (data[i + 2] >> 1) & 1;
            if (version == 1 || layer == 0 || bitrateIndex == 0 || bitrateIndex == 15 || rateIndex == 3)
                return null;
            var mpeg1 = version == 3;
            var rate = SampleRates[rateIndex] / (mpeg1 ? 1 : version == 2 ? 2 : 4);
            var table = mpeg1 ? 3 - layer : layer == 3 ? 3 : 4;
            var bitrate = Bitrates[table][bitrateIndex] * 1000;
            var samples = layer == 3 ? 384 : layer == 2 || mpeg1 ? 1152 : 576;
            var length = layer == 3 ? (12 * bitrate / rate + padding) * 4 : samples / 8 * bitrate / rate + padding;
            return length <= 0 ? null : (length, (double)samples / rate);
        }
    }
}
