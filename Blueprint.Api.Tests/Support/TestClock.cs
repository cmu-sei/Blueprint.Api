// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Blueprint.Api.Tests.Support;

/// <summary>
/// A clock a test sets, for the code that reads the time through <see cref="TimeProvider"/>. Only the reading is
/// controlled: timers still run on the system clock, so a test of an expiration uses short real durations.
/// </summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    public TestClock() : this(new DateTimeOffset(2026, 5, 25, 18, 0, 0, TimeSpan.Zero)) { }

    public DateTimeOffset Now { get; set; } = start;

    public DateTime UtcNow => Now.UtcDateTime;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now = Now.Add(by);
}
