using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// Shared lifecycle for the in-memory library indexes (artist, song n-gram):
/// startup load, debounced refresh on library changes (5s window), JF-419.1
/// failed-load self-recovery (one-shot re-arming retry timer until any load
/// succeeds), and dispose ordering (volatile flag set before the lock, with
/// in-lock re-checks so no event or retry callback can arm a timer after
/// cleanup). Extracted from the near-verbatim copies in ArtistIndexService and
/// SongNgramIndexService (JF-419.3): a third index service must derive from this
/// class, not copy the lifecycle again.
/// </summary>
public abstract class DebouncedLibraryIndexService : IHostedService, IDisposable
{
    private const int RefreshDebounceSeconds = 5;
    private const int FailedLoadRetrySeconds = 30;

    /// <summary>
    /// Give-up threshold (review round 1 finding 2): after this many consecutive
    /// failed loads the index is disabled instead of retried forever, so handlers
    /// degrade to their database paths instead of an endless warming refusal.
    /// </summary>
    internal const int MaxLoadAttempts = 10;

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _debounceLock = new();
    private Timer? _debounceTimer;
    private Timer? _retryTimer;
    private volatile bool _isReady;
    private volatile bool _isDisabled;
    private int _consecutiveLoadFailures;
    private volatile bool _disposed;

    /// <summary>Library manager for the load query and change events.</summary>
    protected ILibraryManager LibraryManager { get; }

    /// <summary>Logger shared by lifecycle and domain messages (the subclass's category).</summary>
    protected ILogger Logger { get; }

    /// <summary>
    /// Whether the index has loaded successfully at least once (sticky). Set by the
    /// base after <see cref="LoadAsync"/> returns; satisfies the subclasses'
    /// IsReady interface members.
    /// </summary>
    public bool IsReady => _isReady;

    /// <summary>
    /// Whether the index gave up after <see cref="MaxLoadAttempts"/> consecutive
    /// failed loads: warming gates treat a disabled index as absent (callers degrade
    /// to their database paths). A later successful refresh clears it.
    /// </summary>
    public bool IsDisabled => _isDisabled;

    /// <summary>Human-readable index name for lifecycle log messages.</summary>
    protected abstract string IndexName { get; }

    /// <summary>Whether a library change of this kind requires a refresh (item type filter).</summary>
    protected abstract bool ShouldRefreshOn(ItemChangeEventArgs eventArgs);

    /// <summary>
    /// Load the domain data and publish it into the subclass's own state. Do NOT set
    /// readiness: the base owns it (a successful return marks the index ready and
    /// disarms the retry timer; a throw logs and arms the retry).
    /// </summary>
    /// <param name="cancellationToken">Shutdown token.</param>
    /// <returns>A task representing the load.</returns>
    protected abstract Task LoadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// JF-419.1: delay before each background retry of a failed load. Internal test
    /// hook (not a ctor parameter: MS.DI cannot resolve optional parameters).
    /// </summary>
    internal TimeSpan FailedLoadRetryInterval { get; set; } = TimeSpan.FromSeconds(FailedLoadRetrySeconds);

    /// <summary>
    /// Initializes a new instance of the <see cref="DebouncedLibraryIndexService"/> class
    /// and subscribes to library change events.
    /// </summary>
    /// <param name="libraryManager">Library manager for the load query and change events.</param>
    /// <param name="logger">Logger (the concrete service's category).</param>
    protected DebouncedLibraryIndexService(ILibraryManager libraryManager, ILogger logger)
    {
        LibraryManager = libraryManager;
        Logger = logger;
        LibraryManager.ItemAdded += OnLibraryChanged;
        LibraryManager.ItemRemoved += OnLibraryChanged;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Runs one load under the refresh lock; arms the retry timer afterwards when the
    /// index is still not ready (single arming site: initial load, debounced refresh,
    /// and retry callback all funnel through here). A shutdown cancellation disarms
    /// instead: no timer may fire against a tearing-down host.
    /// </summary>
    /// <param name="cancellationToken">Shutdown token.</param>
    /// <returns>A task representing the refresh.</returns>
    protected async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool shutdownRequested = false;
        try
        {
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            _isReady = true;
            _isDisabled = false;
            _consecutiveLoadFailures = 0;
            DisarmRetryTimer();
        }
        catch (OperationCanceledException)
        {
            shutdownRequested = true;
        }
        catch (Exception ex)
        {
            _consecutiveLoadFailures++;
            Logger.LogError(ex, "Failed to load {IndexName} index (attempt {Attempt})", IndexName, _consecutiveLoadFailures);

            if (_consecutiveLoadFailures >= MaxLoadAttempts)
            {
                // Give-up path (review round 1 finding 2): a persistently failing
                // full-catalog load must not brick the handlers forever. Disabled
                // means "treat as absent": the warming gates pass and callers fall
                // back to their bounded DB paths. A later successful refresh (e.g.
                // triggered by a library change) re-enables the index.
                _isDisabled = true;
                Logger.LogError(
                    "{IndexName} index failed {Attempts} consecutive loads; disabling it (handlers degrade to database paths until a refresh succeeds)",
                    IndexName,
                    _consecutiveLoadFailures);
            }
        }
        finally
        {
            TryRelease();
            if (!shutdownRequested)
            {
                ScheduleRetryIfNotReady();
            }
        }
    }

    /// <summary>
    /// Releases the refresh lock, tolerating a concurrent Dispose teardown
    /// (ObjectDisposedException from Release after Dispose surfaces only as log
    /// noise during restarts otherwise).
    /// </summary>
    private void TryRelease()
    {
        try
        {
            _refreshLock.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnLibraryChanged(object? sender, ItemChangeEventArgs e)
    {
        if (_disposed || !ShouldRefreshOn(e))
        {
            return;
        }

        ScheduleRefresh();
    }

    private void ScheduleRefresh()
        => ArmOneShot(
            ref _debounceTimer,
            TimeSpan.FromSeconds(RefreshDebounceSeconds),
            $"Debounced {IndexName} index refresh failed");

    /// <summary>
    /// Arms (or re-arms) a one-shot timer whose callback runs one refresh. The single
    /// arm site for both the debounce and the failed-load retry timers: the
    /// dispose-race guard and the callback error handling live once (review round 1
    /// finding 10: the two blocks were structurally identical copies).
    /// </summary>
    private void ArmOneShot(ref Timer? timer, TimeSpan delay, string errorLabel)
    {
        lock (_debounceLock)
        {
            // In-lock re-check: a callback or event that passed the outer check
            // before Dispose must not arm a timer after cleanup
            if (_disposed)
            {
                return;
            }

            timer?.Dispose();
            timer = new Timer(
                async _ =>
                {
                    try
                    {
                        await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "{ErrorLabel}", errorLabel);
                    }
                },
                null,
                delay,
                Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// JF-419.1: arms a one-shot retry timer when the index is not ready and re-arms
    /// it after each failed attempt, until any load succeeds (initial, retry, or
    /// library-change refresh; the success path calls <see cref="DisarmRetryTimer"/>).
    /// Without this, one failed startup load left IsReady false forever and the
    /// warming gate refused every request until a server restart. A DISABLED index
    /// (give-up path) stops retrying: a library-change refresh is its way back.
    /// </summary>
    private void ScheduleRetryIfNotReady()
    {
        if (_disposed || _isReady || _isDisabled)
        {
            return;
        }

        ArmOneShot(
            ref _retryTimer,
            FailedLoadRetryInterval,
            $"{IndexName} index retry load failed");
    }

    private void DisarmRetryTimer()
    {
        lock (_debounceLock)
        {
            _retryTimer?.Dispose();
            _retryTimer = null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Set before taking the lock so a concurrent retry callback's
        // ScheduleRetryIfNotReady sees it and cannot re-arm after cleanup
        _disposed = true;

        LibraryManager.ItemAdded -= OnLibraryChanged;
        LibraryManager.ItemRemoved -= OnLibraryChanged;
        lock (_debounceLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _retryTimer?.Dispose();
            _retryTimer = null;
        }

        _refreshLock.Dispose();

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Loads every item of a kind server-wide off the caller's thread. Shared by
    /// both index loads and the artist index's album join.
    /// </summary>
    /// <param name="kind">The item kind to load.</param>
    /// <param name="cancellationToken">Token to cancel the load.</param>
    /// <returns>All items of the kind.</returns>
    protected Task<IReadOnlyList<BaseItem>> QueryAllItemsAsync(BaseItemKind kind, CancellationToken cancellationToken)
    {
        var query = new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = new[] { kind },
            DtoOptions = new DtoOptions(true)
        };

        return Task.Run(() => LibraryManager.GetItemList(query), cancellationToken);
    }

    /// <summary>
    /// Resolves the library folder ID for an item by walking up the parent chain.
    /// Used by both indexes for per-user library filtering without DB queries. The
    /// stop condition has FULL parity with Jellyfin's own <c>BaseItem.IsTopParent</c>
    /// boundary (all three edges: plugin folders and channels, live-tv views, and a
    /// parent that is the server-wide <see cref="AggregateFolder"/> root, whose ID
    /// cannot discriminate per library). This is the id space Jellyfin stores as the
    /// TopParentId column and the library filter resolves to, so the index maps and
    /// the filter agree for library-restricted users (JF-455). Returns the item's
    /// own ID when the chain ends without a boundary node (folderless artists stay
    /// self-mapped, the album join's signal).
    /// </summary>
    /// <param name="item">The item to resolve.</param>
    /// <returns>The library folder ID (the item's own ID when it has no parent chain).</returns>
    protected Guid ResolveTopParentId(BaseItem item)
    {
        var seen = new HashSet<Guid>();
        BaseItem? current = item;
        while (current != null)
        {
            BaseItem? parent = current.ParentId == Guid.Empty
                ? null
                : LibraryManager.GetItemById(current.ParentId);

            // IsTopParent parity (BaseItem.cs, v10.11.x): the node itself is a
            // boundary, or its parent is the server-wide aggregate root.
            if (current is BasePluginFolder
                || current is Channel
                || (current is IHasCollectionType view && view.CollectionType == CollectionType.livetv)
                || parent is AggregateFolder)
            {
                return current.Id;
            }

            if (parent == null || !seen.Add(current.Id))
            {
                break; // Chain end or cycle protection
            }

            current = parent;
        }

        return current?.Id ?? item.Id;
    }
}
