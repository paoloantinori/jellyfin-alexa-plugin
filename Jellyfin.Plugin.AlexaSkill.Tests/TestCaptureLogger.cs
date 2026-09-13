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
