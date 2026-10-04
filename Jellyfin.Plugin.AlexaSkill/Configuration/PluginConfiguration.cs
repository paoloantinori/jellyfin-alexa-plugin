using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
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
        StreamTokenSecret = Guid.NewGuid().ToString("N") + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
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
    /// Gets or sets the server-side HMAC secret for signing item-scoped stream tokens (JF-309).
    /// Auto-generated on first construction; persisted transparently. Rotating it invalidates all
    /// outstanding stream tokens (forces a one-time playback restart).
    /// </summary>
    public string StreamTokenSecret { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the additional locales (beyond it-IT) to include in catalog sync, as a
    /// comma-separated string of locale codes (e.g. "de-DE,en-US"). it-IT is always synced.
    /// Only locales with phonetic synonym generators (de/es/fr/pt/ja/nl) get phonetic variants;
    /// others get raw names. Use "*" to sync all active locales (default since v0.11.2.0).
    /// </summary>
    public string CatalogSyncLocales { get; set; } = "*";

    /// <summary>
    /// Gets or sets a value indicating whether the intent simulator endpoint is enabled.
    /// When disabled, all simulator endpoints return 404. Defaults to false for production safety.
    /// </summary>
    public bool SimulatorEnabled { get; set; }

    // Feature flags — disable intent groups via config page
    public bool RadioModeEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the next track is pre-enqueued when the
    /// current track STARTS playing (PlaybackStarted) instead of waiting for the
    /// timing-sensitive PlaybackNearlyFinished event. Eliminates the per-track
    /// round-trip dependency that breaks auto-advance on high-latency endpoints
    /// (JF-390 / GH #20). When on, PlaybackNearlyFinished still handles radio mode
    /// and PostPlay AutoPlay (queue exhaustion) but skips the normal sequential
    /// enqueue (the next track is already in the device queue).
    /// Default false (existing behavior: NearlyFinished-driven enqueue).
    /// </summary>
    public bool PreEnqueueOnStart { get; set; } = false;
    public bool PodcastsEnabled { get; set; } = true;
    public bool LiveTvEnabled { get; set; } = true;
    public bool SleepTimerEnabled { get; set; } = true;
    public bool QueueManagementEnabled { get; set; } = true;
    public bool BrowseLibraryEnabled { get; set; } = true;
    public bool RecommendationsEnabled { get; set; } = true;
    public bool AplVisualsEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the enhanced Echo Show screens (the alexa-layouts responsive
    /// catalog: blurred art background, progress bar, transport buttons) replace the
    /// hand-rolled NowPlaying document. Default FALSE and OUT of the release critical
    /// path by design (JF-624): the last attempt at the layouts import (1.5.0, May 10
    /// 2026, commit 29d49e16) failed SILENTLY with a black screen, so the catalog is
    /// re-adopted one screen per commit behind this flag, verified on-device each step.
    /// Flag off must keep the hand-rolled documents byte-identical.
    /// </summary>
    public bool AplEnhancedScreens { get; set; } = false;
    public bool VideoPlaybackEnabled { get; set; } = true;
    public bool ResumeOfferEnabled { get; set; } = true;
    public bool ResumeAnnounceTitle { get; set; } = true;

    /// <summary>
    /// Gets or sets the global default for whether the now-playing announce ("Now playing X")
    /// is spoken when content is launched. Per-user AnnounceNowPlaying overrides this. Resume/restart
    /// announces are not governed by this setting. This gates VIDEO-LAUNCH and audiobook
    /// fresh-start announces only (JF-353). Music plays (PlaySong/PlayAlbum/PlayArtistSongs) are
    /// gated separately by <see cref="AnnounceAudioPlays"/> (JF-352.4 — audio plays stay silent
    /// by default; opt in there).
    /// </summary>
    public bool DefaultAnnounceNowPlaying { get; set; } = true;

    /// <summary>
    /// Gets or sets the global opt-in for whether the now-playing announce is spoken on MUSIC
    /// plays (PlaySong/PlayAlbum/PlayArtistSongs). Per-user AnnounceAudioPlays overrides this.
    /// Default false: audio plays are silent by default (JF-352.4 — music is frequent, so the
    /// expected UX is a fast silent start). Video/book launches are unaffected (gated by
    /// <see cref="DefaultAnnounceNowPlaying"/>).
    /// </summary>
    public bool AnnounceAudioPlays { get; set; } = false;
    public bool AsrCompoundWordFixEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether cross-media substitutions are ANNOUNCED
    /// (JF-345): when a music request finds no song but an album (the song-to-album
    /// cascade) or no song/album but an artist (the cross-media artist fallback)
    /// matches instead, speak which one is playing ("I found the album X." /
    /// "I found the artist X."). When off, the substitution still plays; it just starts
    /// silently. Default true: a silent media-type swap is confusing on voice-only
    /// devices. NOT related to AnnounceAudioPlays (the per-track "Now playing X"
    /// announce) and NOT applied to PlayAlbum's same-media fuzzy name correction
    /// (JF-339), which corrects a misspoken name rather than substituting a media type.
    /// </summary>
    public bool AnnounceCrossMediaSubstitution { get; set; } = true;

    /// <summary>
    /// Gets or sets the global default for diagnostic interaction logging (JF-393): one
    /// "[diag]" log line per Alexa request/playback event with elapsed times since playback
    /// start. Used to collect data on intermittent voice-routing failures (JF-392 'alexa
    /// stop') and to support remote troubleshooting for other users. Per-user
    /// DiagnosticInteractionLogging overrides this. Default false (no extra log noise).
    /// </summary>
    public bool DefaultDiagnosticInteractionLogging { get; set; } = false;

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

    /// <summary>
    /// Gets or sets a value indicating whether the PAUSE response keeps the skill session
    /// open (ShouldEndSession=false) so a bare follow-up command ("suona jazz") stays
    /// in-skill instead of falling through to the device's default music service.
    /// Default true since the JF-488 device verification (2026-09-05): with the minimal
    /// pause word plus reprompt deployed, the JF-482 matrix re-ran clean on the device
    /// (zero EXCEEDED_MAX_REPROMPTS in the 3h window, no error beep, the open session
    /// survived 34s and closed USER_INITIATED, the immediate follow-up routed in-skill,
    /// and ResumeIntent resumed at the exact pause offset in milliseconds). This
    /// supersedes the JF-299-derived "pause ends the session" default, which JF-482
    /// showed does not apply to pause: JF-299's evidence (2026-07) covered PLAY
    /// responses keeping the session open DURING ACTIVE PLAYBACK (that broke
    /// stop-routing, the device sent SessionEndedRequest instead of PauseIntent), while
    /// pause answers when audio is already stopped. The AudioPlayer.Stop directive is
    /// sent either way (audio always stops); only the session flag changes. This flag
    /// does NOT affect play, stop, or cancel responses: those keep ending the session
    /// (JF-299 covers them).
    /// </summary>
    public bool PauseKeepsSession { get; set; } = true;

    // Media type visibility — exclude content types from search and library queries
    public bool MusicEnabled { get; set; } = true;
    public bool VideosEnabled { get; set; } = true;
    public bool BooksEnabled { get; set; } = true;

    // Playback preferences
    public bool ShuffleArtistSongs { get; set; } = false;

    /// <summary>
    /// Gets or sets the maximum size (in MB) for the video-audio MP4 cache.
    /// Oldest files are evicted when the limit is exceeded. Default: 4096 (4GB).
    /// JF-534: the old 2048 default could not hold even ONE episode of the video
    /// transcode tier. The measured 51-min Adolescence E2 HEVC transcode wrote
    /// 2747MB on disk (live log 2026-09-09 18:24), so the post-encode sweep
    /// (headroom 0, full-cap target) evicted the just-completed episode the moment
    /// ffmpeg exited: the cache retained no transcode-tier episode at all and every
    /// replay re-encoded from zero. 4096 holds a typical episode past its
    /// playback-recency window. Reserve-fit boundary (the estimator rounds UP to
    /// whole hours): the tier's 3072MB/h pre-encode reserve fits under the cap for
    /// content up to one hour (61-120min reserves 6144MB and runs in the JF-428
    /// half-cap-floor regime); on MEASURED actual bytes (~3.2GB/h, 2747MB/51min) a
    /// completed dir fits up to ~80min.
    /// </summary>
    public int VideoAudioCacheSizeMB { get; set; } = 4096;

    /// <summary>
    /// Maximum number of CONCURRENT ffmpeg encode processes across all video-audio
    /// endpoints (JF-310, DoS bound). Requests beyond the cap queue at the gate rather
    /// than spawning unbounded processes. Minimum 1; values below 1 are treated as 1.
    /// </summary>
    public int MaxConcurrentFfmpegEncodes { get; set; } = 2;

    /// <summary>
    /// Use VideoApp.Launch for audio playback instead of AudioPlayer.Play: the Echo Show
    /// gets its native player with a REAL seek bar (the AudioPlayer surface has no
    /// scrubber for custom skills and covers skill APL documents during playback, JF-624
    /// verdict 2026-09-24). The video track is the album cover (stillimage encode; black
    /// frame only when no art resolves). Costs: a few seconds of first-play encode
    /// latency, and the VideoApp path emits no playback events to the skill (position is
    /// tracked server-side). Per-user override: <see cref="Entities.User.VideoAppForAudio"/>.
    /// </summary>
    public bool NativeControlsForAudio { get; set; } = false;

    /// <summary>
    /// Use VideoApp.Launch for audiobook playback instead of AudioPlayer.Play.
    /// Independent from <see cref="NativeControlsForAudio"/> (which governs music): gives the
    /// native progress bar/scrubber on Echo Show for audiobooks and enables HLS-based resume.
    /// Videos are always VideoApp — no toggle.
    /// </summary>
    public bool NativeControlsForBooks { get; set; } = false;

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

    /// <summary>
    /// Gets or sets the default cross-media artist suggestion behavior for users without an
    /// explicit per-user setting. When a song/album is not found but a plausible artist is
    /// (a sub-strict-threshold match), this controls whether to offer it for confirmation
    /// (Confirm), auto-serve it (AutoServe), or do nothing (Off).
    /// </summary>
    public CrossMediaArtistSuggestion DefaultCrossMediaArtistSuggestion { get; set; } = CrossMediaArtistSuggestion.Confirm;

    // Display preferences — items sent to APL visual templates (voice reads 5 max)
    public int MaxListDisplayItems { get; set; } = 15;
    public int MaxInProgressDisplayItems { get; set; } = 10;
    public int MaxQueueDisplayItems { get; set; } = 10;

    /// <summary>
    /// Gets or sets the list of users. Copy-on-write (JF-319): the backing collection is
    /// swapped atomically on every write (AddUser/DeleteUser) via a CAS loop
    /// (<see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>), so concurrent readers (the
    /// request hot path iterates this via GetUserById/GetUserByPersonId and several handlers
    /// foreach over config.Users) always see one consistent snapshot that production write paths
    /// never mutate in place. This eliminates the <c>InvalidOperationException: Collection was
    /// modified</c> race and torn reads without requiring callers to take a lock. The setter is
    /// called by XmlSerializer deserialization with a fresh instance; runtime writes go through
    /// AddUser/DeleteUser, which build a new collection and commit via CAS loop so concurrent
    /// writers cannot silently clobber each other. (Tests may bypass this via config.Users.Add
    /// directly; the invariant holds for production code, which has no in-place mutation.)
    /// </summary>
#pragma warning disable CA2227
    public Collection<User> Users
    {
        get => _users;
        set => Interlocked.Exchange(ref _users, value);
    }

    private Collection<User> _users = new Collection<User>();
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
    /// OWNERSHIP MODEL (JF-724, the recorded decision): the ledger is keyed by
    /// LOCALE ONLY and deliberately GLOBAL across linked SMAPI users, because
    /// the admin panel's product surface is one row per locale; with two users
    /// both skills deploy the same embedded models, so the settled truths agree
    /// and a cross-user overwrite is invisible while the two skills' build
    /// health agrees. HONEST BOUND (JF-724 code-review F3): a divergent
    /// household (one account throttled so its builds FAIL while the other's
    /// succeed) shows the LAST writer's truth in the shared rows, panel
    /// consumer weight included (failedModels/ModelsDeployed), until the next
    /// sync; the <see cref="LocaleModelStatus.ObservedSkillId"/> attribution
    /// makes that divergence diagnosable, and a per-user key remains the fix if
    /// multi-user ever matters. The one cross-user read-decides-for-other path
    /// (the JF-722 deferred refresh settling the other user's frozen rows from
    /// ITS skill's status) is closed by row authorship instead: the observation
    /// family stamps <see cref="LocaleModelStatus.ObservedSkillId"/> and the
    /// refresh matches it against its own skill.
    /// CONCURRENCY (JF-724): every read and write of this collection MUST go
    /// through the locked accessors below (<see cref="GetLocaleModelStatus"/>,
    /// <see cref="SetLocaleModelStatus"/>, <see cref="UpdateLocaleModelStatus"/>,
    /// <see cref="GetLocaleModelStatusSnapshot"/>; the family is ASSEMBLY-INTERNAL
    /// so the plain Get/Set doors cannot be paired from outside the plugin+test
    /// seam, gate-marker F3) in PLUGIN code, and every plugin-code save goes
    /// through <see cref="PersistUnderLedgerLock"/> so
    /// the XML serializer's enumeration is serialized against writer mutations
    /// too; an unguarded enumeration 500s the admin surface on a concurrent
    /// writer's Add/indexer-set, and an unguarded read-then-write loses the
    /// intervening writer's row. The snapshot's copy is reference-shallow by
    /// design: its stability rests on the accessors' replace-only discipline
    /// (WriteLocaleModelStatusEntry swaps whole entry objects, never mutates
    /// one in place); nothing may mutate a LocaleModelStatusEntry in place
    /// anywhere. The lock is per Configuration object; the residual Jellyfin
    /// admin-save swap (the whole Configuration object is REPLACED, so an
    /// in-flight writer holding the pre-swap reference writes into an orphan
    /// the next save never persists) is the pre-existing documented window
    /// (the JF-722 refresh's read-fresh rationale) and is not lock-fixable
    /// across objects.
    /// </summary>
#pragma warning disable CA2227
    public Collection<LocaleModelStatusEntry> LocaleModelStatuses { get; set; } = new();
#pragma warning restore CA2227

    /// <summary>The ledger lock (JF-724): serializes every access to
    /// <see cref="LocaleModelStatuses"/> through the accessors below, both the
    /// plain reads/writes and the read-modify-write pairs (the capture
    /// preserve, the refresh settle, the no-PUT carry), so a concurrent writer
    /// can neither break an enumeration nor clobber a row landed between a
    /// reader's Get and Set. Private by design: nothing outside this class may
    /// enumerate or mutate the collection directly.</summary>
    private readonly object _localeLedgerLock = new();

    /// <summary>
    /// Gets or sets admin-defined custom mood → genre overrides that augment the
    /// built-in MoodGenreMap at resolve time. Each entry adds (or replaces) a mood
    /// word mapping to a comma-separated genre list. On save, custom mood words are
    /// also injected into the Mood slot type of every locale's interaction model
    /// (via the redeploy path) so the NLU can fill the slot one-shot. Stored as a
    /// list because XmlSerializer cannot serialize Dictionary.
    /// </summary>
#pragma warning disable CA2227
    public Collection<MoodGenreOverride> MoodGenreOverrides { get; set; } = new();
#pragma warning restore CA2227

    /// <summary>
    /// Gets or sets per-device default library bindings (JF-327 V1, library-only):
    /// an Alexa device whose deviceId has an entry here is restricted to the bound
    /// library for every request it makes, INTERSECTED with the speaking user's own
    /// AllowedLibraryIds (the binding can restrict further but never grant access
    /// the user does not have, AC#4). A recognized voice profile (PersonId mapped
    /// to a configured user) wins over the binding: the person speaking outranks
    /// the room. Unbound devices keep the account-linking behavior unchanged.
    /// Stored as a list because XmlSerializer cannot serialize Dictionary.
    /// </summary>
#pragma warning disable CA2227
    public Collection<DeviceLibraryBinding> DeviceLibraryBindings { get; set; } = new();
#pragma warning restore CA2227

    /// <summary>
    /// Finds the library binding for an Alexa device id, or null when the device is
    /// not bound. Case-insensitive device id compare (the config UI round-trips the
    /// exact id, but hand-edited config files have drifted case before).
    /// </summary>
    /// <param name="deviceId">The Alexa device id from the request context.</param>
    /// <returns>The binding, or null.</returns>
    public DeviceLibraryBinding? GetDeviceLibraryBinding(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        return DeviceLibraryBindings.FirstOrDefault(b =>
            string.Equals(b.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets locale model status by locale code. Locked (JF-724): a concurrent
    /// writer's collection mutation must not throw the scan. No production
    /// reader uses this plain read today (the writers that decide from the
    /// current row go through <see cref="UpdateLocaleModelStatus"/>); it
    /// remains the assembly-internal plain-read door and the test-seeding
    /// primitive (internal since gate-marker F3: a public Get paired with a
    /// public Set is two lock acquisitions and resurrects the closed
    /// read-modify-write from any future caller).
    /// </summary>
    internal LocaleModelStatus? GetLocaleModelStatus(string locale)
    {
        lock (_localeLedgerLock)
        {
            return FindLocaleModelStatusEntry(locale)?.ToStatus();
        }
    }

    /// <summary>
    /// Sets or updates locale model status, delegating to
    /// <see cref="UpdateLocaleModelStatus"/> so the lock and upsert plumbing
    /// have ONE site. No production writer uses this plain write today (every
    /// field-era writer decides its row from the current row); it remains the
    /// assembly-internal plain-write door and the test-seeding primitive
    /// (internal since gate-marker F3, the same closed-door reasoning as
    /// GetLocaleModelStatus). Writers that
    /// decide the row from the CURRENT row (the capture preserve, the refresh
    /// settle, the no-PUT carry) must use
    /// <see cref="UpdateLocaleModelStatus"/> directly, or the read and this
    /// write are two lock acquisitions with a race between them.
    /// </summary>
    internal void SetLocaleModelStatus(string locale, LocaleModelStatus status)
        => UpdateLocaleModelStatus(locale, _ => status);

    /// <summary>
    /// The atomic read-modify-write of one ledger row (JF-724): the existing
    /// row is read, the compose callback derives the next row from it, and the
    /// write lands, all under the ONE ledger lock acquisition that closed the
    /// KNOWN RACE class (a sync-authored settled row with a real canary
    /// diagnostic landing between a capture/refresh pass's family read and its
    /// Set used to be overwritten with the stale read's fields until the next
    /// sync run). The callback must be PURE (no I/O, no logging): it runs under
    /// the lock.
    /// </summary>
    /// <param name="locale">The locale whose row to update.</param>
    /// <param name="compose">Derives the next row from the current row (null
    /// for a locale with no row yet). Returning null DECLINES the write and
    /// leaves the row untouched (the refresh's not-my-family shape).</param>
    /// <returns>The row now stored, or null when the callback declined to
    /// write.</returns>
    internal LocaleModelStatus? UpdateLocaleModelStatus(string locale, Func<LocaleModelStatus?, LocaleModelStatus?> compose)
    {
        lock (_localeLedgerLock)
        {
            var next = compose(FindLocaleModelStatusEntry(locale)?.ToStatus());
            if (next == null)
            {
                return null;
            }

            WriteLocaleModelStatusEntry(locale, next);
            return next;
        }
    }

    /// <summary>
    /// A locked copy of the whole ledger for read-only consumers (JF-724): the
    /// diagnostics panel, the custom-model status endpoint, and the refresh's
    /// family pre-check. Enumerating <see cref="LocaleModelStatuses"/> directly
    /// throws (and 500s the admin surface) when a concurrent writer's
    /// Add/indexer-set bumps the collection version mid-enumeration; every
    /// consumer derives its answers from this snapshot instead. The copy is
    /// REFERENCE-shallow (the entries carry the Locale key the record lacks):
    /// it is stable because the accessors only ever REPLACE whole entry
    /// objects, never mutate one in place, so the referenced rows can never
    /// change under the snapshot's readers.
    /// </summary>
    /// <returns>A stable copy; never null, empty when the ledger is.</returns>
    internal IReadOnlyList<LocaleModelStatusEntry> GetLocaleModelStatusSnapshot()
    {
        lock (_localeLedgerLock)
        {
            return LocaleModelStatuses.ToArray();
        }
    }

    /// <summary>
    /// The ONE save path for plugin code (JF-724 gate-marker F4, absorbing
    /// code-review F1): persists the plugin's configuration under the ledger
    /// lock, because Jellyfin's SaveConfiguration serializes the LIVE
    /// LocaleModelStatuses collection and a concurrent ledger writer's
    /// Add/replace landing mid-serialization throws INSIDE the save (the
    /// write already succeeded, so the failure strands rows memory-only, and
    /// on the capture path a false return would mis-derive the paired
    /// refresh's recapture mode). Every plugin-code save that can execute
    /// once background work exists goes through here, so the invariant is
    /// structural instead of a repeated caller lambda. Reads
    /// <see cref="Plugin.Instance"/> itself: there is exactly one Plugin
    /// instance in production and in the test seam, and the saved
    /// configuration is that instance's.
    /// UNWRAPPED REMAINDER (the whole of it, JF-724 gate-marker F1's honest
    /// scope): the two Plugin.cs LOAD-TIME migrations (JF-300/JF-534), which
    /// run during plugin construction before any hosted service or scheduled
    /// task can run a ledger writer in the process, are already best-effort
    /// caught there; and Jellyfin's own configuration-save path when the
    /// admin updates plugin config through the framework, which is outside
    /// plugin code and human-paced. Nothing else may call
    /// SaveConfiguration directly.
    /// </summary>
    internal void PersistUnderLedgerLock()
    {
        lock (_localeLedgerLock)
        {
            // Instance is resolved BEFORE the seam fires (gate-marker round 2
            // F4): the interceptor must only ever count saves that will really
            // run, so a null Instance is a silent no-op on both counts.
            var plugin = Plugin.Instance;
            if (plugin == null)
            {
                return;
            }

            PersistInterceptorForTest?.Invoke();
            plugin.SaveConfiguration();
        }
    }

    /// <summary>Test seam (the TypeLegEntryProbeForTest pattern): invoked
    /// inside <see cref="PersistUnderLedgerLock"/> under the lock, ONLY when
    /// the save will really run (Instance non-null, resolved first); a test
    /// can throw from it to simulate a failing SaveConfiguration (the JF-724
    /// gate-marker F2 capture save-honesty pin) or observe the save boundary.
    /// Never set in production. Internal property: invisible to
    /// XmlSerializer.</summary>
    internal Action? PersistInterceptorForTest { get; set; }

    /// <summary>The locked-scan row lookup shared by the accessors. Caller
    /// holds <see cref="_localeLedgerLock"/>.</summary>
    private LocaleModelStatusEntry? FindLocaleModelStatusEntry(string locale)
    {
        for (int i = 0; i < LocaleModelStatuses.Count; i++)
        {
            if (string.Equals(LocaleModelStatuses[i].Locale, locale, StringComparison.OrdinalIgnoreCase))
            {
                return LocaleModelStatuses[i];
            }
        }

        return null;
    }

    /// <summary>The upsert shared by the accessors (replace the locale's row,
    /// or append one). Caller holds <see cref="_localeLedgerLock"/>.</summary>
    private void WriteLocaleModelStatusEntry(string locale, LocaleModelStatus status)
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
                Plugin.Instance.ManifestSkill = new ManifestSkill(ManifestSkill.EmbeddedManifestResourcePath, serverAddress, sslCertType);
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
        // Copy-on-write (JF-319): build a new collection and commit it via a CAS loop so a
        // concurrent writer cannot be silently lost. The duplicate check-then-add TOCTOU is
        // inherent (the caller's own GetUserById-then-AddUser already has this race, see
        // ConfigurationController.cs), but the CAS loop guarantees the build and the swap
        // commit against the SAME snapshot, eliminating the lost-update window between them.
        while (true)
        {
            Collection<User> snapshot = _users;
            if (snapshot.Any(u => user.Id == u.Id))
            {
                throw new ArgumentException("User already inside list");
            }

            var next = new Collection<User>(snapshot.ToList()) { user };
            if (Interlocked.CompareExchange(ref _users, next, snapshot) == snapshot)
            {
                return;
            }

            // Another writer swapped in between our read and commit; re-check against the new snapshot.
        }
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
        // Copy-on-write (JF-319): build a new collection without the user and commit via a CAS
        // loop so a concurrent writer cannot be silently lost (see AddUser for the rationale).
        while (true)
        {
            Collection<User> snapshot = _users;
            if (!snapshot.Any(u => guid == u.Id))
            {
                return false;
            }

            var next = new Collection<User>(snapshot.Where(u => u.Id != guid).ToList());
            if (Interlocked.CompareExchange(ref _users, next, snapshot) == snapshot)
            {
                return true;
            }

            // Another writer swapped in; re-check against the new snapshot.
        }
    }
}

/// <summary>
/// The structured caveat bits of a <see cref="LocaleModelStatus"/> row (JF-721),
/// replacing the marker-string wire protocol the ledger's Error field used to
/// carry: every writer sets its own bits instead of composing recognizable
/// literals into the free text, and every cross-writer decision (what a clean
/// capture preserves, what a clean settle drops) reads these bits instead of
/// parsing the text. Persisted through <see cref="LocaleModelStatusEntry"/> as
/// the enum's name(s); additive, so a pre-JF-721 persisted row deserializes as
/// <see cref="None"/> (its legacy composed Error text still renders verbatim).
/// </summary>
[Flags]
public enum CatalogLedgerCaveats
{
    /// <summary>No caveat: a plain row whose Error (if any) is a free-text diagnostic.</summary>
    None = 0,

    /// <summary>The last catalog sync froze one or more catalog types for this
    /// locale (JF-705/JF-709): their last-good catalog versions stay pinned. Durable
    /// catalog state: survives a clean startup capture (JF-710) and heals only when
    /// a later sync re-freezes (rewrites it) or succeeds (clears it). The frozen
    /// type names ride the row's <see cref="LocaleModelStatus.FrozenCatalogTypes"/>.</summary>
    FrozenCatalogs = 1,

    /// <summary>Run-scoped (JF-709): the sync leg that authored this row performed
    /// no model PUT because every catalog type was frozen. Never survives a clean
    /// startup capture: a new skill version WAS pushed, superseding the last run's
    /// no-PUT shape. Also the no-PUT writer's own-row marker: a later all-frozen run
    /// reading this bit replaces the row wholesale instead of re-carrying its Error
    /// (the once-only carry rule).</summary>
    NoCatalogPut = 2,

    /// <summary>Arm discriminator (JF-722, made structural by JF-721): the row's
    /// <see cref="LocaleModelStatus.Error"/> is the SMAPI build Errors array observed
    /// by THIS row's authoring observation (the startup capture or its deferred
    /// refresh), not a preserved foreign diagnostic. A later clean settle DROPS that
    /// Error and clears this bit (the errors describe an observation the settle
    /// supersedes, possibly stale in the unverified SMAPI in-flight shape); a clean
    /// capture never preserves an arm-tagged Error (the fresh build supersedes it).
    /// Never co-occurs with <see cref="FrozenCatalogs"/>: the own-errors arms replace
    /// the row wholesale and never preserve.</summary>
    ObservedBuildErrors = 4,
}

/// <summary>
/// Stores the SMAPI build status for a single locale's interaction model.
/// </summary>
public record LocaleModelStatus
{
    /// <summary>Gets the build status: "SUCCEEDED", "FAILED", "IN_PROGRESS", or the
    /// observation outcomes "TIMEOUT" / "UNVERIFIED" (build accepted, outcome not
    /// confirmed; JF-495) or "Skipped" (locale not carried by the skill, or a
    /// no-PUT catalog-sync leg with no SETTLED previous status to preserve: no
    /// prior entry, or a transient/unknown one; all types frozen, JF-709).</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Gets the UTC timestamp when this status was last checked.</summary>
    public DateTime LastUpdated { get; init; }

    /// <summary>Gets the free-text diagnostic for this row (a JF-495 canary
    /// mismatch, a failed-PUT reason, or a formatted SMAPI build Errors array);
    /// null otherwise. Since JF-721 this field is PURE free text: nothing composes
    /// recognizable markers into it and nothing parses it (the structured caveats
    /// live in <see cref="Caveat"/> and <see cref="FrozenCatalogTypes"/>), which
    /// closes the JF-710 invariant exposure by construction. Durable catalog-sync
    /// diagnostics survive a clean startup capture's overwrite of the row (JF-710:
    /// the model rebuild does not reset the catalog state they describe). The
    /// admin UI renders this field regardless of Status.</summary>
    public string? Error { get; init; }

    /// <summary>Gets the structured caveat bits (JF-721); see
    /// <see cref="CatalogLedgerCaveats"/> for each bit's writer and survival
    /// semantics. Additive persisted field: pre-JF-721 rows read as
    /// <see cref="CatalogLedgerCaveats.None"/>.</summary>
    public CatalogLedgerCaveats Caveat { get; init; }

    /// <summary>Gets the frozen catalog type names as a CSV ("Artist,Album").
    /// Set by every field-era frozen write whenever
    /// <see cref="Caveat"/> carries <see cref="CatalogLedgerCaveats.FrozenCatalogs"/>
    /// (bit-implies-payload is the writers' factory rule, the copy paths'
    /// guard, and the legacy migration's mint, which always recovers names
    /// from the clause head); null under that bit ONLY on a hand-authored
    /// persisted row, which renders the nameless clause. Names, not ids: only
    /// <see cref="CaveatText"/> renders them, nothing parses them (the JF-721
    /// no-wire-protocol rule).</summary>
    public string? FrozenCatalogTypes { get; init; }

    /// <summary>Gets the model source label: the observation family's
    /// <c>"Embedded"</c> (<see cref="EntryPoints.SkillStartup.CaptureLedgerSource"/>)
    /// or the catalog-sync writers'
    /// <c>"CatalogSyncGetModifyPut"</c>. Defaults to EMPTY (JF-724): the
    /// capture-family recognizer matches this field Ordinal against the
    /// capture's own constant, and a writer that composes a row WITHOUT
    /// choosing a source must land OUTSIDE that family (the old "Embedded"
    /// default silently drafted any omitting writer's IN_PROGRESS rows into
    /// the refresh's rewrite set, the JF-722 rework F5 gap). Every field-era
    /// writer sets Source explicitly.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Gets the skill whose status observation authored this row
    /// (JF-724): set by the startup capture and its deferred refresh (the
    /// observation family, which reads ONE skill's status into the shared
    /// locale-keyed rows); null on the catalog-sync writers' rows, on
    /// pre-JF-724 persisted rows, and on hand-authored rows. The refresh's
    /// family predicate matches this field against ITS OWN skill id (null
    /// matches any refresh, keeping legacy rows refreshable), so with two
    /// linked SMAPI users one user's refresh no longer settles the other
    /// user's frozen rows from its own skill's status, nor stays alive on
    /// them. XML-additive: a missing element deserializes null and an old DLL
    /// ignores the element (the JF-721 rollback-safe pattern).</summary>
    public string? ObservedSkillId { get; init; }

    /// <summary>
    /// Renders the row's caveat bits as the admin-panel text shown beside the
    /// free-text <see cref="Error"/> (JF-721): the frozen clause (JF-705's
    /// wording kept), the run-scoped no-PUT tail (JF-709's), and the build-errors
    /// label (JF-722's prefix, now a label not a marker), "; "-joined; null when
    /// no caveat applies (pre-JF-721 rows: their legacy composed Error text
    /// already carries its own clause and needs no duplicate). DISPLAY-ONLY: the
    /// old marker family's invariant burden is gone, but these strings must stay
    /// out of any recognition logic; nothing may ever parse ledger text again.
    /// </summary>
    public string? CaveatText
    {
        get
        {
            if (Caveat == CatalogLedgerCaveats.None)
            {
                return null;
            }

            var parts = new List<string>();
            if (Caveat.HasFlag(CatalogLedgerCaveats.FrozenCatalogs))
            {
                if (!string.IsNullOrEmpty(FrozenCatalogTypes))
                {
                    string[] types = FrozenCatalogTypes.Split(',');
                    parts.Add($"{string.Join(" + ", types)} catalog{(types.Length > 1 ? "s" : string.Empty)} FROZEN (last-good pinned)");
                }
                else
                {
                    // The legacy-migrated bit without recoverable names: the
                    // nameless clause still names the freeze.
                    parts.Add("catalogs FROZEN (last-good pinned)");
                }
            }

            if (Caveat.HasFlag(CatalogLedgerCaveats.NoCatalogPut))
            {
                parts.Add("no PUT this run");
            }

            if (Caveat.HasFlag(CatalogLedgerCaveats.ObservedBuildErrors))
            {
                parts.Add("build errors");
            }

            // Every Caveat != None shape contributes a part (the frozen arm
            // renders nameless when the payload is absent), so the join is
            // never empty here; the None case returned early above.
            return string.Join("; ", parts);
        }
    }
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
        Caveat = status.Caveat;
        FrozenCatalogTypes = status.FrozenCatalogTypes;
        Source = status.Source;
        ObservedSkillId = status.ObservedSkillId;
    }

    public string Locale { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime LastUpdated { get; set; }
    public string? Error { get; set; }

    /// <summary>The XML-persisted twin of <see cref="LocaleModelStatus.Caveat"/>
    /// (JF-721). Additive: pre-JF-721 rows have no Caveat element and deserialize
    /// as <see cref="CatalogLedgerCaveats.None"/>; an old DLL reading a newer
    /// config ignores the unknown element (rollback-safe).</summary>
    public CatalogLedgerCaveats Caveat { get; set; }

    /// <summary>The XML-persisted twin of
    /// <see cref="LocaleModelStatus.FrozenCatalogTypes"/> (JF-721).</summary>
    public string? FrozenCatalogTypes { get; set; }

    /// <summary>Unlike the record's EMPTY default (JF-724), the XML default
    /// stays "Embedded" and is LOAD-BEARING: a row persisted before the Source
    /// field existed deserializes with NO Source element and must keep reading
    /// as an embedded-authored row. The asymmetry is deliberate and pinned;
    /// do not "fix" it.</summary>
    public string Source { get; set; } = "Embedded";

    /// <summary>The XML-persisted twin of
    /// <see cref="LocaleModelStatus.ObservedSkillId"/> (JF-724). Additive:
    /// pre-JF-724 rows have no element and deserialize null (legacy rows every
    /// refresh may still settle); an old DLL ignores the element
    /// (rollback-safe).</summary>
    public string? ObservedSkillId { get; set; }

    public LocaleModelStatus ToStatus() => new()
    {
        Status = Status,
        LastUpdated = LastUpdated,
        Error = Error,
        Caveat = Caveat,
        FrozenCatalogTypes = FrozenCatalogTypes,
        Source = Source,
        ObservedSkillId = ObservedSkillId,
    };
}

/// <summary>
/// XML-serializable admin override mapping a mood word to a comma-separated
/// genre list. Merged into the mood handler's MoodGenreMap at resolve time, and
/// (when the model is redeployed) the <see cref="Mood"/> word is injected into
/// each locale's Mood slot type so the NLU fills the slot one-shot.
/// </summary>
public class MoodGenreOverride
{
    public MoodGenreOverride()
    {
    }

    public MoodGenreOverride(string mood, string genres)
    {
        Mood = mood;
        Genres = genres;
    }

    /// <summary>The mood word the user speaks (e.g. "coding"). Case-insensitive.</summary>
    public string Mood { get; set; } = string.Empty;

    /// <summary>Comma-separated Jellyfin genre names (e.g. "electronic,ambient").</summary>
    public string Genres { get; set; } = string.Empty;

    /// <summary>Parses Genres into a trimmed, non-empty array.</summary>
    public string[] GenreArray() =>
        Genres.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// XML-serializable admin mapping of an Alexa device (an Echo) to a default
/// Jellyfin library (JF-327 V1). DeviceId is the raw Alexa device id from the
/// request context (also visible in the plugin log's DeviceId scope); LibraryId
/// is the Jellyfin media folder GUID; LibraryName is display-only convenience
/// for the config table.
/// </summary>
public class DeviceLibraryBinding
{
    /// <summary>The Alexa device id (Context.System.Device.DeviceID).</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>The Jellyfin library (media folder) GUID as a string.</summary>
    public string LibraryId { get; set; } = string.Empty;

    /// <summary>Display-only library name for the config table.</summary>
    public string LibraryName { get; set; } = string.Empty;

    /// <summary>Display-only label for the device (e.g. "Cucina", "Kids room").</summary>
    public string DeviceName { get; set; } = string.Empty;
}