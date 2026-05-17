using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Background service that maintains an in-memory index of all MusicArtist items.
/// Loads at startup and refreshes when artists are added/removed from the library.
/// Uses debounced refresh (5s window) to coalesce rapid library changes.
/// </summary>
public class ArtistIndexService : IArtistIndex, IHostedService, IDisposable
{
    private const int RefreshDebounceSeconds = 5;

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ArtistIndexService> _logger;
    private volatile List<BaseItem> _artists = [];
    private volatile Dictionary<Guid, Guid> _artistTopParentMap = new();
    private volatile bool _isReady;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _debounceLock = new();
    private Timer? _debounceTimer;
    private bool _disposed;

    public bool IsReady => _isReady;
    public int Count => _artists.Count;

    public ArtistIndexService(ILibraryManager libraryManager, ILogger<ArtistIndexService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _libraryManager.ItemAdded += OnLibraryChanged;
        _libraryManager.ItemRemoved += OnLibraryChanged;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public IReadOnlyList<BaseItem> GetArtists(Guid[]? topParentIds = null)
    {
        var artists = _artists;

        if (topParentIds == null || topParentIds.Length == 0)
        {
            return artists;
        }

        var map = _artistTopParentMap;
        return artists.Where(a => map.TryGetValue(a.Id, out var parentId) && Array.IndexOf(topParentIds, parentId) >= 0).ToList();
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var query = new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = new[] { BaseItemKind.MusicArtist },
                DtoOptions = new DtoOptions(true)
            };

            IReadOnlyList<BaseItem> artists = await Task.Run(
                () => _libraryManager.GetItemList(query), cancellationToken).ConfigureAwait(false);

            // Pre-compute top parent IDs for library filtering
            var topParentMap = new Dictionary<Guid, Guid>(artists.Count);
            foreach (var artist in artists)
            {
                topParentMap[artist.Id] = ResolveTopParentId(artist);
            }

            _artistTopParentMap = topParentMap;
            _artists = new List<BaseItem>(artists);
            _isReady = true;

            _logger.LogInformation("Artist index {Action}: {Count} artists",
                artists.Count > 0 ? "loaded" : "initialized (empty library)",
                artists.Count);
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load artist index");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Resolves the top-level parent folder ID for an artist by walking up the parent chain.
    /// Used for per-user library filtering without DB queries.
    /// </summary>
    private Guid ResolveTopParentId(BaseItem item)
    {
        var seen = new HashSet<Guid>();
        BaseItem? current = item;
        while (current != null && current.ParentId != Guid.Empty)
        {
            if (!seen.Add(current.Id))
            {
                break; // Cycle protection
            }

            var parent = _libraryManager.GetItemById(current.ParentId);
            if (parent == null)
            {
                break;
            }

            current = parent;
        }

        return current?.Id ?? item.Id;
    }

    private void OnLibraryChanged(object? sender, ItemChangeEventArgs e)
    {
        if (_disposed || e.Item is not MusicArtist)
        {
            return;
        }

        ScheduleRefresh();
    }

    private void ScheduleRefresh()
    {
        lock (_debounceLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(
                async _ =>
                {
                    try
                    {
                        await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Debounced artist index refresh failed");
                    }
                },
                null,
                TimeSpan.FromSeconds(RefreshDebounceSeconds),
                Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _libraryManager.ItemAdded -= OnLibraryChanged;
        _libraryManager.ItemRemoved -= OnLibraryChanged;
        lock (_debounceLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        _refreshLock.Dispose();
        _disposed = true;
    }
}
