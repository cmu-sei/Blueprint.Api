// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Linq;
using System.Text;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary><c>SpeechText</c> and <c>Mp3Audio</c> - text split for a speech provider, and voiced pieces joined into one clip.</summary>
public class SpeechTextTests
{
    [Fact]
    public void Sentences_SplitAfterTheirFullStops()
    {
        Assert.Equal(["Pumps stopped at six.", "\"Is it safe?\" she asked.", "Yes!", "Crews (two) left."],
            SpeechText.Sentences("Pumps stopped at six.  \"Is it safe?\" she asked.\nYes! Crews (two) left."));
    }

    /// <summary>Pieces end at sentences and stay under the limit; a sentence past it is broken between words.</summary>
    [Fact]
    public void Split_KeepsSentencesWholeUnderTheLimit()
    {
        var pieces = SpeechText.Split("One two three. Four five six. Seven eight nine ten eleven twelve.", 30);

        Assert.Equal(["One two three. Four five six.", "Seven eight nine ten eleven", "twelve."], pieces);
        Assert.All(pieces, p => Assert.True(p.Length <= 30));
        Assert.Equal(["Short."], SpeechText.Split("Short.", 30));
    }

    [Fact]
    public void Split_OfALongArticle_LosesNoWords()
    {
        var text = string.Join(" ", Enumerable.Range(1, 400).Select(i => $"Sentence number {i} is here."));

        var pieces = SpeechText.Split(text, PollySpeechClient.MaxRequestCharacters);

        Assert.True(pieces.Count > 1);
        Assert.All(pieces, p => Assert.True(p.Length <= PollySpeechClient.MaxRequestCharacters));
        Assert.Equal(text, string.Join(" ", pieces));
    }

    [Fact]
    public void Ssml_EscapesTheTextAndAsksForTheNewsStyle()
    {
        Assert.Equal("<speak>AT&amp;T &lt;down&gt;</speak>", SpeechText.Ssml("AT&T <down>", false));
        Assert.Equal("<speak><amazon:domain name=\"news\">Good evening.</amazon:domain></speak>", SpeechText.Ssml(" Good evening. ", true));
    }

    [Fact]
    public void Clean_DropsTheSsmlAndEntitiesOfAMark()
    {
        Assert.Equal("AT&T is down.", SpeechText.Clean("<amazon:domain name=\"news\">AT&amp;T is down.</amazon:domain>"));
    }

    /// <summary>Without timings from the provider, each sentence and word starts at its share of the characters before it.</summary>
    [Fact]
    public void EstimateMarks_SharesTheClipByCharacters()
    {
        // "Go now." is 8 of the 23 characters counted, with a space after each sentence
        var (sentences, words) = SpeechText.EstimateMarks("Go now. Stay calm, ok.", 2000);

        Assert.Equal([(0, "Go now."), (695, "Stay calm, ok.")], sentences.Select(m => (m.Time, m.Text)));
        Assert.Equal([0, 260, 695, 1130, 1652], words.Select(m => m.Time));
        Assert.Equal("calm,", words[3].Text);
    }

    /// <summary>Joined pieces play one after another, so the second piece's marks start where the first piece ends.</summary>
    [Fact]
    public void Join_MovesEachPiecesMarksPastThePiecesBeforeIt()
    {
        var first = new AiSpeech { Mp3 = Mp3Frames.Of(50), DurationMs = 1200, Sentences = [new() { Time = 0, Text = "One." }], Words = [new() { Time = 100, Text = "One." }] };
        var second = new AiSpeech { Mp3 = Mp3Frames.Of(25), DurationMs = 600, Sentences = [new() { Time = 50, Text = "Two." }], Words = [new() { Time = 60, Text = "Two." }] };

        var joined = SpeechText.Join([first, second]);

        Assert.Equal(1800, joined.DurationMs);
        Assert.Equal([0, 1250], joined.Sentences.Select(m => m.Time));
        Assert.Equal([100, 1260], joined.Words.Select(m => m.Time));
        Assert.Equal(75 * 144, joined.Mp3.Length);
        Assert.Equal(1800, Mp3Audio.DurationMs(joined.Mp3));
    }

    [Fact]
    public void Mp3Duration_CountsTheFrames()
    {
        Assert.Equal(2400, Mp3Audio.DurationMs(Mp3Frames.Of(100)));
        Assert.Equal(0, Mp3Audio.DurationMs([]));
        Assert.Equal(0, Mp3Audio.DurationMs(Encoding.ASCII.GetBytes("not audio at all")));
    }

    /// <summary>An ID3 tag before the frames is skipped, and an MPEG-1 frame is 1,152 samples.</summary>
    [Fact]
    public void Mp3Duration_SkipsAnId3TagAndReadsMpeg1()
    {
        // ID3v2 with a 20-byte body, then one MPEG-1 layer III frame at 128 kbps, 44.1 kHz: 417 bytes, 26.1 ms
        var tag = new byte[30];
        "ID3"u8.CopyTo(tag);
        tag[3] = 4;
        tag[9] = 20;
        var frame = new byte[417];
        frame[0] = 0xFF;
        frame[1] = 0xFB;
        frame[2] = 0x90;

        Assert.Equal(26, Mp3Audio.DurationMs([.. tag, .. frame]));
        Assert.Equal(52, Mp3Audio.DurationMs([.. tag, .. frame, .. frame]));
    }
}
