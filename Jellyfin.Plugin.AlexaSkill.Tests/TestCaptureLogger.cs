#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Tests;

/// <summary>
/// The shared capturing logger for log-assertion tests (JF-546: was four private
/// per-file copies that had already drifted between string-only and
/// (LogLevel, Message) tuple records). Records every formatted message with its
/// level; assertions filter by substring on <see cref="Records"/>.
/// </summary>
internal static class TestCaptureLogger
{
    /// <summary>Creates a provider writing into the given list.</summary>
    internal static CaptureLoggerProvider Into(List<(LogLevel Level, string Message)> records) => new(records);

    /// <summary>
    /// A lock-consistent copy of the captured records. Tests whose handler logs
    /// from background threads (ffmpeg encode paths) must enumerate a snapshot,
    /// never the live list.
    /// </summary>
    internal static List<(LogLevel Level, string Message)> Snapshot(List<(LogLevel Level, string Message)> records)
    {
        lock (records)
        {
            return records.ToList();
        }
    }

    /// <summary>
    /// The capture-only Trace-level logger factory (JF-760: was a 24-site inline
    /// LoggerFactory.Create block in VideoAudioControllerTests plus a
    /// SkillResponseLoggingTests sibling; the shared home here because the idiom
    /// spans files, unlike the file-private CreateDeletingLoggerFactory sibling
    /// of JF-751). The Trace minimum level is load-bearing, not decorative: the
    /// factory's rule filter drops Debug/Trace messages BEFORE the provider's
    /// always-true IsEnabled is ever consulted, so a higher floor silently
    /// empties the Debug-level log assertions. Non-vacuity contract (JF-692):
    /// removing the AddProvider below must redden every log-asserting consumer.
    /// Not to be confused with the private StructuredLoggingTests
    /// CreateCapturingLoggerFactory (one word apart): that one is a
    /// factory-level capturer with no provider registration and no Trace floor;
    /// this one registers <see cref="Into"/> at the Trace floor.
    /// </summary>
    internal static ILoggerFactory CreateCaptureLoggerFactory(List<(LogLevel Level, string Message)> records)
        => LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(Into(records));
        });
}

internal sealed class CaptureLoggerProvider : ILoggerProvider
{
    private readonly List<(LogLevel Level, string Message)> _records;

    internal CaptureLoggerProvider(List<(LogLevel Level, string Message)> records) => _records = records;

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(_records);

    public void Dispose()
    {
    }
}

internal sealed class CaptureLogger : ILogger
{
    private readonly List<(LogLevel Level, string Message)> _records;

    internal CaptureLogger(List<(LogLevel Level, string Message)> records) => _records = records;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        // Background threads (ffmpeg encode polls, fire-and-forget tasks) log while
        // the test enumerates; Add must be synchronized or a concurrent grow throws
        // "Collection was modified" in the test's LINQ (live CI flake 2026-09-13).
        lock (_records)
        {
            _records.Add((logLevel, formatter(state, exception)));
        }
    }
}
