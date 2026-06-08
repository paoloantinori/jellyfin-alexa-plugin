using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Alexa.NET.Management;
using Jellyfin.Plugin.AlexaSkill.Alexa.Manifest;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Entities;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AlexaSkill.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    private SslCertificateType sslCertType;
    private string serverAddress;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        // set default options here
        sslCertType = SslCertificateType.Wildcard;

        serverAddress = string.Empty;
        AccountLinkingClientId = Guid.NewGuid().ToString();
    }

    /// <summary>
    /// Gets or sets the ssl cert type of the public jellyfin endpoint.
    /// </summary>
    public SslCertificateType SslCertType
    {
        get => sslCertType;
        set
        {
            sslCertType = value;
            UpdateManifestSkill();
        }
    }

    /// <summary>
    /// Gets or sets the server address.
    /// Normalized to always end with a trailing slash for correct relative URI resolution
    /// (e.g., when Jellyfin is behind a reverse proxy with a subpath like /jellyfin/).
    /// </summary>
    public string ServerAddress
    {
        get => serverAddress;
        set
        {
            serverAddress = NormalizeTrailingSlash(value);
            UpdateManifestSkill();
        }
    }

    private string lwaClientId = string.Empty;
    private string lwaClientSecret = string.Empty;

    /// <summary>
    /// Gets or sets the client id for LWA.
    /// Sanitized to strip invisible Unicode characters that browser copy-paste
    /// from the Amazon developer portal may introduce (e.g. zero-width spaces).
    /// </summary>
    public string LwaClientId
    {
        get => lwaClientId;
        set => lwaClientId = CredentialSanitizer.Sanitize(value);
    }

    /// <summary>
    /// Gets or sets the client secret for LWA.
    /// Sanitized to strip invisible Unicode characters that browser copy-paste
    /// from the Amazon developer portal may introduce (e.g. zero-width spaces).
    /// </summary>
    public string LwaClientSecret
    {
        get => lwaClientSecret;
        set => lwaClientSecret = CredentialSanitizer.Sanitize(value);
    }

    /// <summary>
    /// Gets or sets the account linking client id.
    /// </summary>
    public string AccountLinkingClientId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the intent simulator endpoint is enabled.
    /// When disabled, all simulator endpoints return 404. Defaults to false for production safety.
    /// </summary>
    public bool SimulatorEnabled { get; set; }

    // Feature flags — disable intent groups via config page
    public bool RadioModeEnabled { get; set; } = true;
    public bool PodcastsEnabled { get; set; } = true;
    public bool LiveTvEnabled { get; set; } = true;
    public bool SleepTimerEnabled { get; set; } = true;
    public bool QueueManagementEnabled { get; set; } = true;
    public bool BrowseLibraryEnabled { get; set; } = true;
    public bool RecommendationsEnabled { get; set; } = true;
    public bool AplVisualsEnabled { get; set; } = true;
    public bool VideoPlaybackEnabled { get; set; } = true;
    public bool ResumeOfferEnabled { get; set; } = true;
    public bool ResumeAnnounceTitle { get; set; } = true;
    public bool AsrCompoundWordFixEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether phonetic (Double Metaphone) matching
    /// is enabled for song title search. When enabled, misspelled titles (e.g. "rapsodi"
    /// for "rhapsody") can still match via phonetic encoding. Native English speakers
    /// can disable this to avoid false-positive phonetic matches.
    /// Phonetic search only activates when exact token matching yields no results,
    /// so disabling it has no effect on the fast path.
    /// </summary>
    public bool PhoneticSongSearchEnabled { get; set; } = true;
    public bool SeekEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to announce playback position when the user pauses.
    /// Requires SeekEnabled. When off, pause is silent (audio stops, no speech).
    /// </summary>
    public bool PauseAnnouncePosition { get; set; } = true;

    // Media type visibility — exclude content types from search and library queries
    public bool MusicEnabled { get; set; } = true;
    public bool VideosEnabled { get; set; } = true;
    public bool BooksEnabled { get; set; } = true;

    // Playback preferences
    public bool ShuffleArtistSongs { get; set; } = false;

    /// <summary>
    /// Gets or sets the maximum size (in MB) for the video-audio MP4 cache.
    /// Oldest files are evicted when the limit is exceeded. Default: 2048 (2GB).
    /// </summary>
    public int VideoAudioCacheSizeMB { get; set; } = 2048;

    /// <summary>
    /// Use VideoApp.Launch for audio playback instead of AudioPlayer.Play.
    /// Gives native progress bar/scrubber on Echo Show but without album art.
    /// </summary>
    public bool NativeControlsForAudio { get; set; } = false;

    public int InitialFetchSize { get; set; } = 5;
    public int ContinuationBatchSize { get; set; } = 10;
    public int PrefetchThreshold { get; set; } = 2;

    // Search preferences
    public int MaxSearchResults { get; set; } = 20;
    public int MaxBrowseResults { get; set; } = 5;
    public int MaxRecentlyAddedResults { get; set; } = 10;
    public int MaxRecommendationResults { get; set; } = 10;

    /// <summary>
    /// Gets or sets the default search response mode for users without an explicit per-user setting.
    /// Controls the trade-off between search speed and recall quality.
    /// </summary>
    public SearchResponseMode DefaultSearchResponseMode { get; set; } = SearchResponseMode.Thorough;

    /// <summary>
    /// Gets or sets the default post-play behavior for users without an explicit per-user setting.
    /// Controls what happens when playback ends and the queue is exhausted.
    /// </summary>
    public PostPlayBehavior DefaultPostPlayBehavior { get; set; } = PostPlayBehavior.Stop;

    // Display preferences — items sent to APL visual templates (voice reads 5 max)
    public int MaxListDisplayItems { get; set; } = 15;
    public int MaxInProgressDisplayItems { get; set; } = 10;
    public int MaxQueueDisplayItems { get; set; } = 10;

    /// <summary>
    /// Gets or sets the list of users.
    /// </summary>
#pragma warning disable CA2227
    public Collection<User> Users { get; set; } = new Collection<User>();
#pragma warning restore CA2227

    // Custom Interaction Model
    public string? CustomModelUrl { get; set; }

    public string CustomModelLocale { get; set; } = "en-US";

    public bool CustomModelEnabled { get; set; }

    public DateTime? LastModelDeployTime { get; set; }

    public string? LastModelDeployStatus { get; set; }

    /// <summary>
    /// Gets or sets per-locale interaction model build status from SMAPI.
    /// Stored as a list because XmlSerializer cannot serialize Dictionary.
    /// </summary>
#pragma warning disable CA2227
    public Collection<LocaleModelStatusEntry> LocaleModelStatuses { get; set; } = new();
#pragma warning restore CA2227

    /// <summary>
    /// Gets locale model status by locale code.
    /// </summary>
    public LocaleModelStatus? GetLocaleModelStatus(string locale)
    {
        foreach (var entry in LocaleModelStatuses)
        {
            if (string.Equals(entry.Locale, locale, StringComparison.OrdinalIgnoreCase))
            {
                return entry.ToStatus();
            }
        }

        return null;
    }

    /// <summary>
    /// Sets or updates locale model status.
    /// </summary>
    public void SetLocaleModelStatus(string locale, LocaleModelStatus status)
    {
        for (int i = 0; i < LocaleModelStatuses.Count; i++)
        {
            if (string.Equals(LocaleModelStatuses[i].Locale, locale, StringComparison.OrdinalIgnoreCase))
            {
                LocaleModelStatuses[i] = new LocaleModelStatusEntry(locale, status);
                return;
            }
        }

        LocaleModelStatuses.Add(new LocaleModelStatusEntry(locale, status));
    }

    /// <summary>
    /// Ensure the URL ends with a trailing slash so that relative URI construction
    /// preserves path segments. Without this, <c>new Uri("https://host/path", "Items/1")</c>
    /// resolves to <c>https://host/Items/1</c> instead of <c>https://host/path/Items/1</c>.
    /// </summary>
    private static string NormalizeTrailingSlash(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return string.Empty;
        }

        address = address.TrimEnd('/');
        return address + "/";
    }

    /// <summary>
    /// Validate the configuration and return a list of error messages.
    /// </summary>
    /// <returns>A list of validation error messages. Empty if valid.</returns>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (!string.IsNullOrWhiteSpace(serverAddress))
        {
            if (!Uri.TryCreate(serverAddress, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                errors.Add("Server address must be a valid HTTP or HTTPS URL.");
            }
        }

        if (Users.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(LwaClientId))
            {
                errors.Add("LWA Client ID is required when users are configured.");
            }

            if (string.IsNullOrWhiteSpace(LwaClientSecret))
            {
                errors.Add("LWA Client Secret is required when users are configured.");
            }
        }

        var seen = new HashSet<Guid>();
        foreach (User u in Users)
        {
            if (!seen.Add(u.Id))
            {
                errors.Add($"Duplicate user ID: {u.Id}");
            }
        }

        if (InitialFetchSize < 1 || InitialFetchSize > 20)
        {
            errors.Add("Initial Fetch Size must be between 1 and 20.");
        }

        if (ContinuationBatchSize < 1 || ContinuationBatchSize > 50)
        {
            errors.Add("Continuation Batch Size must be between 1 and 50.");
        }

        if (PrefetchThreshold < 0 || PrefetchThreshold > 10)
        {
            errors.Add("Pre-fetch Threshold must be between 0 and 10.");
        }

        if (MaxSearchResults < 1 || MaxSearchResults > 50)
        {
            errors.Add("Max Search Results must be between 1 and 50.");
        }

        if (MaxBrowseResults < 1 || MaxBrowseResults > 50)
        {
            errors.Add("Max Browse Results must be between 1 and 50.");
        }

        if (MaxRecentlyAddedResults < 1 || MaxRecentlyAddedResults > 50)
        {
            errors.Add("Max Recently Added Results must be between 1 and 50.");
        }

        if (MaxRecommendationResults < 1 || MaxRecommendationResults > 30)
        {
            errors.Add("Max Recommendation Results must be between 1 and 30.");
        }

        if (MaxListDisplayItems < 1 || MaxListDisplayItems > 50)
        {
            errors.Add("Max List Display Items must be between 1 and 50.");
        }

        if (MaxInProgressDisplayItems < 1 || MaxInProgressDisplayItems > 50)
        {
            errors.Add("Max In Progress Display Items must be between 1 and 50.");
        }

        if (MaxQueueDisplayItems < 1 || MaxQueueDisplayItems > 50)
        {
            errors.Add("Max Queue Display Items must be between 1 and 50.");
        }

        return errors;
    }

    /// <summary>
    /// Update the manifest skill with the current ServerAddress and SslCertType.
    /// No-op when the plugin instance is not yet initialized (e.g. during XML deserialization).
    /// </summary>
    private void UpdateManifestSkill()
    {
        if (Plugin.Instance == null)
        {
            return;
        }

        if (!Uri.TryCreate(serverAddress, UriKind.Absolute, out _))
        {
            return;
        }

        try
        {
            if (Plugin.Instance.ManifestSkill == null)
            {
                Plugin.Instance.ManifestSkill = new ManifestSkill("Jellyfin.Plugin.AlexaSkill.Alexa.Manifest.manifest.json", serverAddress, sslCertType);
            }
            else
            {
                Plugin.Instance.ManifestSkill.SetApiEndpoint(serverAddress, sslCertType);
            }
        }
        catch (Exception)
        {
            // Manifest loading can fail when embedded resources are unavailable
            // (e.g., during testing or partial initialization). Config setters
            // must not throw — the manifest will be updated on next successful load.
        }
    }

    /// <summary>
    /// Add a user to the list of users.
    /// </summary>
    /// <param name="user">The user to add.</param>
    public void AddUser(User user)
    {
        // check if the user is already inside the list
        foreach (User u in Users)
        {
            if (user.Id == u.Id)
            {
                throw new ArgumentException("User already inside list");
            }
        }

        Users.Add(user);
    }

    /// <summary>
    /// Get the user by its guid.
    /// </summary>
    /// <param name="guid">The guid of the user.</param>
    /// <returns>Instance of the <see cref="User"/> class or null if the user was not found.</returns>
    public User? GetUserById(Guid guid)
    {
        foreach (User u in Users)
        {
            if (guid == u.Id)
            {
                return u;
            }
        }

        return null;
    }

    /// <summary>
    /// Get the user by their Alexa person ID (voice profile).
    /// </summary>
    /// <param name="personId">The Alexa person ID from speaker recognition.</param>
    /// <returns>Instance of the <see cref="User"/> class or null if no mapping exists.</returns>
    public User? GetUserByPersonId(string personId)
    {
        if (string.IsNullOrEmpty(personId))
        {
            return null;
        }

        foreach (User u in Users)
        {
            if (string.Equals(u.AlexaPersonId, personId, StringComparison.Ordinal))
            {
                return u;
            }
        }

        return null;
    }

    /// <summary>
    /// Delete the user with the given guid.
    /// </summary>
    /// <param name="guid">The guid of the user.</param>
    /// <returns>True if the user was deleted, false otherwise.</returns>
    public bool DeleteUser(Guid guid)
    {
        foreach (User u in Users)
        {
            if (guid == u.Id)
            {
                return Users.Remove(u);
            }
        }

        return false;
    }
}

/// <summary>
/// Stores the SMAPI build status for a single locale's interaction model.
/// </summary>
public record LocaleModelStatus
{
    /// <summary>Gets the build status: "SUCCEEDED", "FAILED", or "IN_PROGRESS".</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Gets the UTC timestamp when this status was last checked.</summary>
    public DateTime LastUpdated { get; init; }

    /// <summary>Gets the error message if the build failed, null otherwise.</summary>
    public string? Error { get; init; }

    /// <summary>Gets the model source: "Embedded" (bundled) or "Custom" (user-provided).</summary>
    public string Source { get; init; } = "Embedded";
}

/// <summary>
/// XML-serializable entry for per-locale model status.
/// XmlSerializer cannot handle Dictionary, so we use a Collection of keyed entries.
/// </summary>
public class LocaleModelStatusEntry
{
    public LocaleModelStatusEntry()
    {
    }

    public LocaleModelStatusEntry(string locale, LocaleModelStatus status)
    {
        Locale = locale;
        Status = status.Status;
        LastUpdated = status.LastUpdated;
        Error = status.Error;
        Source = status.Source;
    }

    public string Locale { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime LastUpdated { get; set; }
    public string? Error { get; set; }
    public string Source { get; set; } = "Embedded";

    public LocaleModelStatus ToStatus() => new()
    {
        Status = Status,
        LastUpdated = LastUpdated,
        Error = Error,
        Source = Source,
    };
}