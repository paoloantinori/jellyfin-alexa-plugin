#nullable enable
using System;
using System.Collections.Generic;
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
        => _records.Add((logLevel, formatter(state, exception)));
}
