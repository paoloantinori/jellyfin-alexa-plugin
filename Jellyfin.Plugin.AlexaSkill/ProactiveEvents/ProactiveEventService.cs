using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.ProactiveEvents;

/// <summary>
/// Background service that periodically checks for recently added content
/// and sends proactive notifications to subscribed users.
/// </summary>
internal class ProactiveEventService : IHostedService, IDisposable
{
    private static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromHours(1);

    private readonly ProactiveEventClient _client;
    private readonly ProactiveEventRateLimiter _rateLimiter;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ProactiveEventService> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private Timer? _timer;

    public ProactiveEventService(
        ProactiveEventClient client,
        ProactiveEventRateLimiter rateLimiter,
        ILibraryManager libraryManager,
        ILogger<ProactiveEventService> logger)
    {
        _client = client;
        _rateLimiter = rateLimiter;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(
            async _ => await CheckAndNotifyAsync().ConfigureAwait(false),
            null,
            DefaultCheckInterval,
            DefaultCheckInterval);

        _logger.LogInformation("Proactive event service started with {Interval} check interval", DefaultCheckInterval);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        _logger.LogInformation("Proactive event service stopped");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Manually trigger a content check and notification cycle.
    /// </summary>
    public async Task CheckAndNotifyAsync()
    {
        if (!await _semaphore.WaitAsync(0).ConfigureAwait(false))
        {
            return; // Already running
        }

        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || string.IsNullOrWhiteSpace(config.LwaClientId))
            {
                return;
            }

            foreach (var user in config.Users)
            {
                if (!user.ProactiveEventsEnabled)
                {
                    continue;
                }

                string? alexaUserId = user.AlexaPersonId;
                if (string.IsNullOrEmpty(alexaUserId))
                {
                    continue;
                }

                if (!_rateLimiter.CanSend(alexaUserId))
                {
                    _logger.LogDebug("Rate limit reached for user {UserId}, skipping", user.Username);
                    continue;
                }

                await CheckForNewContentAsync(user, alexaUserId).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during proactive event check cycle");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Check for recently added content for a specific user and send a notification.
    /// </summary>
    private async Task CheckForNewContentAsync(Entities.User user, string alexaUserId)
    {
        var query = new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.Audio, BaseItemKind.Movie, BaseItemKind.Episode },
            MinDateCreated = DateTime.UtcNow.AddHours(-1),
            Limit = 5,
            OrderBy = new[] { (ItemSortBy.DateCreated, SortOrder.Descending) },
            DtoOptions = new DtoOptions(true)
        };

        var items = _libraryManager.GetItemList(query);

        if (items == null || items.Count == 0)
        {
            return;
        }

        foreach (var item in items)
        {
            if (!_rateLimiter.CanSend(alexaUserId))
            {
                break;
            }

            string? contentType = null;
            string? artistName = null;
            int? seasonNumber = null;
            int? episodeNumber = null;

            if (item is MediaBrowser.Controller.Entities.Audio.Audio audio)
            {
                contentType = "ALBUM";
                artistName = audio.Artists?.Count > 0 ? audio.Artists[0] : audio.Album;
            }
            else if (item is MediaBrowser.Controller.Entities.TV.Episode episode)
            {
                contentType = "EPISODE";
                artistName = episode.SeriesName;
                seasonNumber = episode.ParentIndexNumber;
                episodeNumber = episode.IndexNumber;
            }
            else if (item is MediaBrowser.Controller.Entities.Movies.Movie)
            {
                contentType = "MOVIE";
            }
            else
            {
                continue;
            }

            if (string.IsNullOrEmpty(contentType))
            {
                continue;
            }

            var eventPayload = ProactiveEventClient.BuildMediaContentAvailableEvent(
                contentType,
                item.Name,
                artistName,
                seasonNumber,
                episodeNumber);

            bool sent = await _client.SendEventAsync(alexaUserId, eventPayload).ConfigureAwait(false);
            if (sent)
            {
                _rateLimiter.RecordSend(alexaUserId);
                _logger.LogDebug("Sent proactive event for {ContentType} '{Name}' to user {Username}",
                    contentType, item.Name, user.Username);
            }

            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        _semaphore.Dispose();
    }
}
