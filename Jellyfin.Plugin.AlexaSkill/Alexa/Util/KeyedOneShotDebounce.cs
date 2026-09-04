using System;
using System.Collections.Generic;
using System.Threading;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// Keyed one-shot debounce timer map shared by the playback persistence owners
/// (<c>DeviceQueueManager</c>, <c>AudiobookPositionTracker</c>). Extracted by
/// JF-449 from the near-verbatim copies of the JF-429 arm-guard idiom so the
/// arm/dispose mutual exclusion and the callback-vs-teardown interleaving fix
/// live exactly once (DebouncedLibraryIndexService.ArmOneShot is the DELIBERATE
/// non-adopter: its callback awaits a full library load for seconds and so runs
/// OUTSIDE its arming lock, while this helper's payloads run INSIDE the gate
/// because that is what makes Disarm a barrier; do not consolidate it here and
/// do not add a fourth copy). Owns the volatile disposed flag, the gate lock,
/// and the per-key timer map. Payloads must not call Arm/Disarm/DisposeAll on
/// this same instance: the gate is non-reentrant and would self-deadlock.
///
/// Interleaving contract (why callbacks run INSIDE the gate):
/// <list type="bullet">
/// <item><description>Arm registers or re-arms a timer under the gate. The
/// payload is whatever the OWNER captured when arming, so a straggler callback
/// can carry state that predates a later Disarm (that is the JF-449 Clear
/// resurrect hazard made explicit).</description></item>
/// <item><description>The timer wrapper re-reads the entry under the gate and
/// no-ops when the key was disarmed. Entry REMOVAL is the invalidation, chosen
/// over a generation counter because it needs zero extra state; a counter only
/// buys something when the callback cannot consult the map, which this design
/// never allows.</description></item>
/// <item><description>Running the callback body inside the gate makes
/// Disarm/DisposeAll a BARRIER: when either returns, no callback is executing
/// and none can start afterwards. That closes the three JF-449 interleavings:
/// Clear vs an already-started persist (the callback finishes before the caller
/// proceeds to the file delete), the tracker final flush vs an in-flight
/// debounce write (the flush runs only after the barrier drains it), and DQM
/// PersistAll vs a firing timer (teardown precedes the final flush via the
/// unified Dispose order both owners share: flag, teardown, final
/// flush).</description></item>
/// </list>
/// Callbacks run on thread-pool timer threads and must catch their own
/// exceptions (both adopters catch and log inside their persist methods); an
/// escaping exception would otherwise terminate the process. The tradeoff of
/// the shared gate is that one instance serializes callback execution across
/// its keys: an arming caller waits for at most one in-flight callback (both
/// adopters' payloads are small local-file writes).
/// </summary>
internal sealed class KeyedOneShotDebounce : IDisposable
{
    /// <summary>One armed key: its timer and the latest payload callback.</summary>
    private sealed class Entry
    {
        public Timer Timer = null!;
        public Action Callback = null!;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private volatile bool _disposed;

    /// <summary>
    /// Debounce delay read at arm and re-arm time. Internal test hook (the
    /// InternalsVisibleTo seam): set it to milliseconds to run the race tests
    /// without wall-clock sleeps. Set it before the first Arm of a key; an
    /// already-armed timer keeps the delay it was armed with.
    /// </summary>
    internal TimeSpan Interval { get; set; }

    /// <summary>
    /// Test seam invoked under the gate BEFORE an armed callback runs, so a
    /// test can park a callback mid-flight (deterministic interleaving). Null
    /// in production. Must not call Arm/Disarm on this instance while parked:
    /// those need the gate the parked callback holds.
    /// </summary>
    internal Action? BeforeCallbackGate { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="KeyedOneShotDebounce"/> class.
    /// </summary>
    /// <param name="interval">Debounce delay for armed keys.</param>
    internal KeyedOneShotDebounce(TimeSpan interval)
    {
        Interval = interval;
    }

    /// <summary>
    /// Arms (first call) or re-arms (subsequent calls) the one-shot debounce
    /// for a key, installing <paramref name="callback"/> as the payload a fire
    /// will run. Re-arming resets the timer and replaces the payload, so an
    /// older fire that was already queued runs the LATEST payload, never a
    /// stale one. Returns false when disposed: this is the single JF-429
    /// arm/dispose guard (volatile flag plus in-lock re-check), so adopters do
    /// not carry their own.
    /// </summary>
    /// <param name="key">The debounce key.</param>
    /// <param name="callback">Payload to run when the debounce fires.</param>
    /// <returns>True when armed; false when the map is disposed.</returns>
    internal bool Arm(string key, Action callback)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            if (_entries.TryGetValue(key, out Entry? existing))
            {
                existing.Callback = callback;
                existing.Timer.Change(Interval, Timeout.InfiniteTimeSpan);
                return true;
            }

            var entry = new Entry { Callback = callback };
            entry.Timer = new Timer(_ => RunCallback(key), null, Interval, Timeout.InfiniteTimeSpan);
            _entries[key] = entry;
            return true;
        }
    }

    /// <summary>
    /// Cancels the pending debounce for a key and acts as a barrier: blocks
    /// until an already-started callback for any key finishes, and invalidates
    /// the entry so a queued straggler no-ops instead of running its (possibly
    /// pre-Disarm) payload. Callers that subsequently delete persisted state
    /// (DeviceQueueManager.Clear) cannot have that deletion resurrected by a
    /// late write.
    /// </summary>
    /// <param name="key">The debounce key to cancel.</param>
    internal void Disarm(string key)
    {
        lock (_gate)
        {
            if (_entries.Remove(key, out Entry? entry))
            {
                entry.Timer.Dispose();
            }
        }
    }

    /// <summary>
    /// Tears down every timer (barrier for in-flight callbacks, same as
    /// <see cref="Disarm"/>) and permanently stops the map: later Arm calls
    /// return false and no timer can fire afterwards.
    /// </summary>
    internal void DisposeAll()
    {
        lock (_gate)
        {
            _disposed = true;

            foreach (Entry entry in _entries.Values)
            {
                entry.Timer.Dispose();
            }

            _entries.Clear();
        }
    }

    /// <summary>
    /// Runs the key's current payload synchronously under the same rules as a
    /// timer fire (no-op when disarmed or disposed). Test seam for forcing the
    /// "already-started callback" side of the JF-449 interleavings
    /// deterministically, including as a maximally late straggler after a
    /// Disarm or DisposeAll.
    /// </summary>
    /// <param name="key">The debounce key to fire.</param>
    internal void FireNow(string key) => RunCallback(key);

    /// <summary>Timer callback and <see cref="FireNow"/> share this body.</summary>
    /// <param name="key">The debounce key whose payload to run.</param>
    private void RunCallback(string key)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Entry presence is the straggler invalidation (see class doc): a
            // key disarmed before this point runs nothing. The whole callback
            // body stays inside the gate so Disarm/DisposeAll cannot return
            // while it executes.
            if (!_entries.TryGetValue(key, out Entry? entry))
            {
                return;
            }

            BeforeCallbackGate?.Invoke();
            entry.Callback();
        }
    }

    /// <inheritdoc />
    public void Dispose() => DisposeAll();
}
