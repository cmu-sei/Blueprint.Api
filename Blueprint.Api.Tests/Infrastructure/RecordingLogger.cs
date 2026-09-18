// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Blueprint.Api.Tests.Infrastructure;

/// <summary>
/// An <see cref="ILogger{T}"/> that keeps what it was told, so a test can assert on a log line.
/// </summary>
/// <remarks>
/// <para>
/// Deferred since Phase 1 on the rule that the harness ships no dead code, and needed at last by the
/// two queue workers: <c>JoinService</c> and <c>AddApplicationService</c> write nothing to the
/// database, broadcast nothing to the hub and return nothing to anybody, so when either one fails the
/// <strong>only</strong> evidence that anything happened is the line its outermost <c>catch</c> logs.
/// A test with no way to read that cannot tell a failed join from a join nobody asked for.
/// </para>
/// <para>
/// It is also the only way to see what blueprint swallows elsewhere - <c>BlueprintContext.SaveEntries</c>
/// (twice), <c>UserClaimsService.ValidateUser</c>, <c>ClaimsPrincipalExtensions.GetId</c> and the whole
/// body of <c>DatabaseExtensions.InitializeDatabase</c> all catch and carry on.
/// </para>
/// <para>
/// Entries are appended under a lock. Every current caller logs from a thread the test did not start -
/// <c>ProcessTheJoin</c> runs on a <c>new Thread</c> - so an unsynchronized list would tear.
/// </para>
/// </remarks>
public sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly Lock _lock = new();
    private readonly List<LogEntry> _entries = [];

    /// <summary>Everything logged, in order.</summary>
    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries.ToList();
            }
        }
    }

    /// <summary>The <c>LogError</c> calls, which is what a swallowed exception becomes.</summary>
    public IEnumerable<LogEntry> Errors => Entries.Where(x => x.Level == LogLevel.Error);

    /// <summary>
    /// Always true, so nothing under test decides not to log. Production reads this: the workers'
    /// <c>LogDebug</c> calls are the only trace of a queue item being taken.
    /// </summary>
    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception exception,
        Func<TState, Exception, string> formatter)
    {
        var entry = new LogEntry(logLevel, formatter(state, exception), exception);

        lock (_lock)
        {
            _entries.Add(entry);
        }
    }

    /// <summary>One log call: what level, what it said, and what it was carrying.</summary>
    /// <remarks>
    /// <paramref name="Message"/> is the formatted message, which for blueprint is usually an
    /// interpolated string rather than a template with arguments - so it is the whole of what a reader
    /// of the log sees, and the right thing for a test to assert against.
    /// </remarks>
    public sealed record LogEntry(LogLevel Level, string Message, Exception Exception);

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
