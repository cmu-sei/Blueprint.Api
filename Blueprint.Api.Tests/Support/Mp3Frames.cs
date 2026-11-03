// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Blueprint.Api.Tests.Support;

/// <summary>Silent MP3 audio of a known length, as Polly makes it: MPEG-2 layer III, 24 kHz, 48 kbps, 24 ms a frame</summary>
public static class Mp3Frames
{
    public const int FrameMs = 24;
    private const int FrameLength = 144;

    public static byte[] Of(int frames)
    {
        var data = new byte[frames * FrameLength];
        for (var i = 0; i < frames; i++)
        {
            data[i * FrameLength] = 0xFF;
            data[i * FrameLength + 1] = 0xF3;
            data[i * FrameLength + 2] = 0x64;
            data[i * FrameLength + 3] = 0xC4;
        }
        return data;
    }

    /// <summary>About ms long, to the frame</summary>
    public static byte[] Lasting(int ms) => Of((int)Math.Round(ms / (double)FrameMs));
}
