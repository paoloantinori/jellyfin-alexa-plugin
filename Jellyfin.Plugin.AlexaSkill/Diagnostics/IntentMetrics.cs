using System;

namespace Jellyfin.Plugin.AlexaSkill.Diagnostics;

/// <summary>
/// Per-intent metrics accumulator. Thread-safe via lock.
/// </summary>
public sealed class IntentMetrics
{
    internal long _count;
    internal long _errorCount;
    internal double _totalMs;
    internal double _minMs;
    internal double _maxMs;
    internal long _lastErrorAt;
    private readonly object _lock = new();

    public IntentMetrics()
    {
    }

    public IntentMetrics(double initialMs)
    {
        _count = 1;
        _totalMs = initialMs;
        _minMs = initialMs;
        _maxMs = initialMs;
    }

    public static IntentMetrics WithInitialError()
    {
        return new IntentMetrics
        {
            _errorCount = 1,
            _lastErrorAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
    }

    public IntentMetrics RecordResponse(double elapsedMs)
    {
        lock (_lock)
        {
            _count++;
            _totalMs += elapsedMs;
            if (_minMs == 0 || elapsedMs < _minMs) _minMs = elapsedMs;
            if (elapsedMs > _maxMs) _maxMs = elapsedMs;
            return this;
        }
    }

    public IntentMetrics RecordError()
    {
        lock (_lock)
        {
            _errorCount++;
            _lastErrorAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return this;
        }
    }

    public IntentMetricsSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new IntentMetricsSnapshot
            {
                Count = _count,
                ErrorCount = _errorCount,
                TotalMs = _totalMs,
                AverageMs = _count > 0 ? _totalMs / _count : 0,
                MinMs = _minMs,
                MaxMs = _maxMs,
                LastErrorAt = _lastErrorAt > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(_lastErrorAt).UtcDateTime
                    : (DateTime?)null
            };
        }
    }
}
