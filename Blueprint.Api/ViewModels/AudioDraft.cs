// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;

namespace Blueprint.Api.ViewModels
{
    /// <summary>The kind of clip to make from an inject</summary>
    public enum AudioDraftKind
    {
        /// <summary>The article read aloud as written, in its outlet's voice; no model writes anything</summary>
        Listen,
        /// <summary>A voicemail left by the inject's sender, written by the model</summary>
        Voicemail,
        /// <summary>An intercepted or recorded phone call between two or more speakers, written by the model</summary>
        Call,
        /// <summary>A 30 to 60 second broadcast news segment from the outlet, written by the model</summary>
        Broadcast,
        /// <summary>A 15 to 30 second vertical social video of the story, read by the outlet's voice over its picture</summary>
        Reel
    }

    /// <summary>
    /// One inject to voice. The inject's values come from the dialog, so unsaved edits count. Nothing is saved.
    /// </summary>
    public class AudioDraftRequest
    {
        /// <summary>The inject being edited; empty for one that is not saved yet</summary>
        public Guid? ScenarioEventId { get; set; }
        /// <summary>The HTML field the clip goes into</summary>
        public Guid DataFieldId { get; set; }
        /// <summary>The inject's delivery time</summary>
        public int DeltaSeconds { get; set; }
        /// <summary>The inject's current values, keyed by data field id</summary>
        public Dictionary<Guid, string> Values { get; set; } = new Dictionary<Guid, string>();
        public AudioDraftKind Kind { get; set; }
        /// <summary>Anything else the planner wants of the script</summary>
        public string Instruction { get; set; }
    }

    /// <summary>When a sentence or word starts, in milliseconds from the start of its line</summary>
    public class AudioDraftMark
    {
        public int StartMs { get; set; }
        public string Text { get; set; }
    }

    /// <summary>One speaker's line, voiced</summary>
    public class AudioDraftLine
    {
        public string Speaker { get; set; }
        public string Voice { get; set; }
        public string Text { get; set; }
        /// <summary>The line as an MP3 data URI</summary>
        public string Audio { get; set; }
        public int DurationMs { get; set; }
        /// <summary>When each sentence starts, for captions</summary>
        public List<AudioDraftMark> Marks { get; set; } = new List<AudioDraftMark>();
        /// <summary>When each word starts, for captions burned into a reel</summary>
        public List<AudioDraftMark> Words { get; set; } = new List<AudioDraftMark>();
    }

    /// <summary>The MSEL's marking as a bar across a video's title card</summary>
    public class AudioDraftMarking
    {
        public string Text { get; set; }
        public string Background { get; set; }
        public string Color { get; set; }
    }

    /// <summary>
    /// A clip's voiced lines and what goes with them, for the browser to join into audio or video. Nothing is saved.
    /// </summary>
    public class AudioDraftResult
    {
        public AudioDraftKind Kind { get; set; }
        /// <summary>The lines in the order they are heard</summary>
        public List<AudioDraftLine> Lines { get; set; } = new List<AudioDraftLine>();
        /// <summary>The script, speaker by speaker, in a Transcript disclosure; none for Listen, whose article is its transcript</summary>
        public string Transcript { get; set; }
        /// <summary>Whether the clip sounds as if over a phone line: a voicemail or a call</summary>
        public bool Phone { get; set; }
        /// <summary>The voiced lines' length, not counting the pauses between them, rounded up</summary>
        public int Seconds { get; set; }
        /// <summary>The title card's title: Voicemail, Intercept, or the outlet's name</summary>
        public string Title { get; set; }
        /// <summary>The inject's headline or subject</summary>
        public string Headline { get; set; }
        /// <summary>The outlet a broadcast or reel is from</summary>
        public string Outlet { get; set; }
        /// <summary>A reel's social media handle, e.g. @WPNNews</summary>
        public string Handle { get; set; }
        /// <summary>The text of a reel's post</summary>
        public string Post { get; set; }
        /// <summary>A reel's picture as a data URI: the article's own photo, or one made for it; none for a plain card</summary>
        public string Photo { get; set; }
        /// <summary>Where the reel's picture came from: article, generated or none</summary>
        public string PhotoSource { get; set; }
        /// <summary>The MSEL's marking, for the title card; none when the MSEL has none</summary>
        public AudioDraftMarking Marking { get; set; }
        /// <summary>Assumptions and problems the planner should check</summary>
        public List<string> Notes { get; set; } = new List<string>();
    }

    /// <summary>Which audio a MSEL's injects can have on this server</summary>
    public class AudioDraftAvailability
    {
        /// <summary>A speech provider is configured, so an article can be read aloud</summary>
        public bool Available { get; set; }
        /// <summary>A text model is configured too, so voicemails, calls, broadcasts and reels can be written</summary>
        public bool Scripts { get; set; }
        /// <summary>An image model is configured, so a reel without a photo of its own gets one</summary>
        public bool Photos { get; set; }
    }
}
