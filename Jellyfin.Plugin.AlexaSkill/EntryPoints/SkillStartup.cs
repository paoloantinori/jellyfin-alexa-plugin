using System;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Management.AccountLinking;
using Alexa.NET.Management.Skills;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Cache;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Jellyfin.Plugin.AlexaSkill.Alexa.InteractionModel;
using Jellyfin.Plugin.AlexaSkill.Alexa.Manifest;
using Jellyfin.Plugin.AlexaSkill.Alexa.ModelDeployment;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Controller;
using Jellyfin.Plugin.AlexaSkill.Diagnostics;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Jellyfin.Plugin.AlexaSkill.Lwa;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Refit;

namespace Jellyfin.Plugin.AlexaSkill.EntryPoints;

/// <summary>
/// Setup the skill and update or create the skill in the Alexa cloud if it is outdated.
/// </summary>
public class SkillStartup : IHostedService, IDisposable
{
    private readonly ILogger<SkillStartup> _logger;
    private readonly ISessionManager _sessionManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ModelDeploymentManager _modelDeploymentManager;
    private readonly SearchResultCache _searchCache;
    private readonly CircuitBreaker _circuitBreaker;
    private readonly RequestCounters _requestCounters;
    private readonly JellyfinConnectivityChecker _connectivityChecker;
    private readonly Alexa.Playback.DeviceQueueManager _deviceQueueManager;
    private readonly Alexa.Playback.AudiobookPositionTracker _positionTracker;
    private CancellationTokenSource? _cts;
    private Task? _runningTask;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillStartup"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="loggerFactory">Logger.</param>
    /// <param name="httpClientFactory">HTTP client factory for outbound calls.</param>
    /// <param name="searchCache">Search result cache for fallback.</param>
    /// <param name="circuitBreaker">Circuit breaker for backend health tracking.</param>
    /// <param name="requestCounters">Request counters for metrics tracking.</param>
    /// <param name="connectivityChecker">Connectivity checker for Jellyfin server health.</param>
    /// <param name="deviceQueueManager">Per-device playback queue manager.</param>
    public SkillStartup(
        ISessionManager sessionManager,
        ILoggerFactory loggerFactory,
        IHttpClientFactory httpClientFactory,
        ModelDeploymentManager modelDeploymentManager,
        SearchResultCache searchCache,
        CircuitBreaker circuitBreaker,
        RequestCounters requestCounters,
        JellyfinConnectivityChecker connectivityChecker,
        Alexa.Playback.DeviceQueueManager deviceQueueManager,
        Alexa.Playback.AudiobookPositionTracker positionTracker)
    {
        _sessionManager = sessionManager;
        _httpClientFactory = httpClientFactory;
        _modelDeploymentManager = modelDeploymentManager;
        _searchCache = searchCache;
        _circuitBreaker = circuitBreaker;
        _requestCounters = requestCounters;
        _connectivityChecker = connectivityChecker;
        _deviceQueueManager = deviceQueueManager;
        _positionTracker = positionTracker;
        _logger = loggerFactory.CreateLogger<SkillStartup>();
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Skill version (local): v{Version}", Util.GetVersion());

        Plugin.Instance!.HttpClientFactory = _httpClientFactory;
        Plugin.Instance!.SearchCache = _searchCache;
        Plugin.Instance!.CircuitBreaker = _circuitBreaker;
        Plugin.Instance!.RequestCounters = _requestCounters;
        Plugin.Instance!.ConnectivityChecker = _connectivityChecker;
        Plugin.Instance!.DeviceQueueManager = _deviceQueueManager;
        Plugin.Instance!.AudiobookPositionTracker = _positionTracker;

        PluginConfiguration configuration = Plugin.Instance!.Configuration;

        if (string.IsNullOrEmpty(configuration.ServerAddress))
        {
            _logger.LogWarning("No server address configured. Skills will not be created or updated.");
            return;
        }

        ManifestSkill manifestSkill;
        try
        {
            manifestSkill = new ManifestSkill(ManifestSkill.EmbeddedManifestResourcePath, configuration.ServerAddress, configuration.SslCertType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load local skill manifest. Skills will not be created or updated.");
            return;
        }

        Plugin.Instance.ManifestSkill = manifestSkill;

        Uri endpointUri = new Uri(new Uri(configuration.ServerAddress), AlexaSkillController.ApiBaseUri);
        string endpointUriString = new Uri(endpointUri, "account-linking").ToString();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;

        _runningTask = Task.Run(
            async () =>
        {
            foreach (User user in configuration.Users)
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    // Restart recovery: reconstruct in-memory token from persisted refresh token.
                    // JF-545: the mechanical refresh/persist is SmapiTokenRefresher's; the
                    // de-authorization policy is JF-547's classification: only a PERMANENT
                    // rejection (invalid_grant/invalid_client - the grant is dead) de-auths.
                    // TRANSIENT failures (network, 429/5xx) keep the refresh token so the
                    // next boot's sweep retries; the user is NOT forced to re-link over a
                    // boot-time blip.
                    if (user.SmapiDeviceToken == null && !string.IsNullOrEmpty(user.SmapiRefreshToken))
                    {
                        _logger.LogInformation("Recovering SMAPI token for user {UserId} from persisted refresh token", user.Id);
                        var (recovered, outcome) = await SmapiTokenRefresher.TryRefreshAsync(user, _logger).ConfigureAwait(false);
                        if (recovered)
                        {
                            _logger.LogInformation("SMAPI token recovered for user {UserId}", user.Id);
                        }
                        else if (outcome == SmapiTokenRefresher.RefreshOutcome.Permanent
                                 || outcome == SmapiTokenRefresher.RefreshOutcome.NotConfigured)
                        {
                            _logger.LogError(
                                "Failed to recover SMAPI token for user {UserId} ({Outcome}). Re-authorization required.",
                                user.Id, outcome);
                            user.SmapiRefreshToken = null;
                            if (user.UserSkill != null)
                            {
                                user.UserSkill.UserSkillStatus = UserSkillStatus.LwaAuthPending;
                            }

                            Plugin.Instance.SaveConfiguration();
                            continue;
                        }
                        else
                        {
                            _logger.LogWarning(
                                "SMAPI token recovery transiently failed for user {UserId} ({Outcome}); keeping the refresh token for the next startup (JF-547)",
                                user.Id, outcome);
                            continue;
                        }
                    }

                    // Fetch and persist SMAPI vendor ID if missing
                    if (string.IsNullOrEmpty(user.VendorId) && user.SmapiManagement != null)
                    {
                        try
                        {
                            user.VendorId = await AlexaUtil.CallAsync(user, () => user.SmapiManagement.GetVendorIdAsync()).ConfigureAwait(false);
                            Plugin.Instance.SaveConfiguration();
                            _logger.LogInformation("Persisted vendor ID {VendorId} for user {UserId}", user.VendorId, user.Id);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to fetch vendor ID for user {UserId}. Catalog sync will be unavailable.", user.Id);
                        }
                    }

                    if (user.UserSkill != null)
                    {
                        Collection<SkillInteractionModel> skillInteractionModels = Plugin.Instance.BuildSkillInteractionModels(user.UserSkill.InvocationName);

                        ValidateLocaleRestrictions(skillInteractionModels);

                        if (!string.IsNullOrEmpty(user.UserSkill.SkillId) && user.SmapiManagement != null)
                        {
                            ManifestSkill? cloudManifestSkill = null;
                            try
                            {
                                cloudManifestSkill = await AlexaUtil.CallAsync(user, () => user.SmapiManagement.GetSkillAsync(user.UserSkill.SkillId!)).ConfigureAwait(false);
                            }
                            catch (Refit.ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                            {
                                _logger.LogWarning("Skill {SkillId} no longer exists in the cloud for user {UserId}. Will recreate.", user.UserSkill.SkillId, user.Id);
                                cloudManifestSkill = null;
                            }

                            if (cloudManifestSkill != null)
                            {
                                string? cloudVersion = cloudManifestSkill.GetVersionTag();
                                _logger.LogInformation("Skill version (cloud) for user {UserId}: {Version}", user.Id, cloudVersion ?? "(no tag)");

                                AccountLinkData accountLinkingData = await AlexaUtil.CallAsync(user, () => user.SmapiManagement.GetAccountLinkDataAsync(user.UserSkill.SkillId!)).ConfigureAwait(false);

                                SkillStatus status = await AlexaUtil.CallAsync(user, () => user.SmapiManagement.GetSkillStatusAsync(user.UserSkill.SkillId!)).ConfigureAwait(false);

                                if (cloudVersion != Util.GetVersion()
                                    || status.Manifest.LastModified.Status == SkillStatusState.FAILED)
                                {
                                    _logger.LogInformation("Skill for user {UserId} is outdated. Updating...", user.Id);
                                    await AlexaUtil.CallAsync<object?>(user, async () =>
                                    {
                                        await user.SmapiManagement.UpdateSkillAsync(user.UserSkill.SkillId!, manifestSkill, skillInteractionModels).ConfigureAwait(false);
                                        return null;
                                    }).ConfigureAwait(false);

                                    // The structurally paired capture + deferred
                                    // refresh (JF-722); the wrapper derives the
                                    // recapture mode from the capture's outcome.
                                    await CaptureAndScheduleStatusRefreshAsync(user, user.UserSkill.SkillId!, token).ConfigureAwait(false);
                                }

                                if (!accountLinkingData.AuthorizationUrl.Equals(endpointUriString, StringComparison.Ordinal)
                                    || !Plugin.Instance.Configuration.AccountLinkingClientId.Equals(accountLinkingData.ClientId, StringComparison.Ordinal))
                                {
                                    _logger.LogInformation("Account linking data for user {UserId} is outdated. Updating...", user.Id);
                                    await AlexaUtil.CallAsync<object?>(user, () =>
                                    {
                                        user.SmapiManagement.UpdateAccountLinkData(
                                            user.UserSkill.SkillId!,
                                            configuration.ServerAddress,
                                            configuration.AccountLinkingClientId);
                                        return Task.FromResult<object?>(null);
                                    }).ConfigureAwait(false);
                                }

                                if (user.TryTransitionToReady())
                                {
                                    _logger.LogInformation("Transitioning user {UserId} from AccountLinkPending to Ready (skill exists, token present)", user.Id);
                                    Plugin.Instance.SaveConfiguration();
                                }
                            }
                            else
                            {
                                _logger.LogWarning("Skill {SkillId} not found in cloud for user {UserId}. Clearing stored skill ID to trigger recreation.", user.UserSkill.SkillId, user.Id);
                                user.UserSkill.SkillId = null;
                                user.UserSkill.UserSkillStatus = UserSkillStatus.SkillCreating;
                                Plugin.Instance.SaveConfiguration();
                            }
                        }

                        if (string.IsNullOrEmpty(user.UserSkill.SkillId) && user.SmapiManagement != null)
                        {
                            _logger.LogInformation("Skill for user {UserId} not in cloud. Creating...", user.Id);
                            string skillId = await AlexaUtil.CallAsync(user, () => user.SmapiManagement.CreateSkillAsync(
                                manifestSkill,
                                skillInteractionModels,
                                configuration.ServerAddress,
                                configuration.AccountLinkingClientId)).ConfigureAwait(false);

                            user.UserSkill.SkillId = skillId;
                            user.UserSkill.UserSkillStatus = UserSkillStatus.AccountLinkPending;
                            Plugin.Instance.SaveConfiguration();

                            // The skill-creation twin of the version-mismatch
                            // site above; typically the sparse-capture world whose
                            // recapture rationale lives on the wrapper (JF-722).
                            await CaptureAndScheduleStatusRefreshAsync(user, skillId, token).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex) when (ex is Refit.ApiException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(
                        "SMAPI API error for user {UserId} during startup — skill sync deferred. Error: {Message}",
                        user.Id, ex.Message);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // Shutdown racing the user loop (the JF-722 capture's
                    // checkpoints rethrow it, as does the loop-top check):
                    // propagate to the same outer handler the loop-top OCE
                    // already reaches, instead of mislabeling it a per-user
                    // skill failure at Error level (code-review refresh RC2).
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing skill for user {UserId}. Continuing with other users.", user.Id);
                }
            }
        },
        token);

        try
        {
            await _runningTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Skill startup task failed. Jellyfin will continue but skill management may be unavailable until the issue is resolved.");
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("SkillStartup stopping...");

        if (_cts != null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_runningTask != null)
        {
            try
            {
                await Task.WhenAny(_runningTask, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("SkillStartup stop timed out or was cancelled");
            }
        }

        _logger.LogInformation("SkillStartup stopped");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _cts?.Cancel();
                _cts?.Dispose();
            }

            _disposed = true;
        }
    }

    /// <summary>
    /// Validates each locale's embedded interaction model against SMAPI restrictions.
    /// Logs warnings for any violations found, but does not block startup.
    /// </summary>
    private void ValidateLocaleRestrictions(Collection<SkillInteractionModel> models)
    {
        foreach (var sim in models)
        {
            string? modelJson = _modelDeploymentManager.GetDefaultModelJson(sim.Locale);
            if (modelJson == null)
            {
                continue;
            }

            var restrictionErrors = _modelDeploymentManager.ValidateSMAPIRestrictions(modelJson, sim.Locale);
            if (restrictionErrors.Count > 0)
            {
                _logger.LogWarning(
                    "SMAPI restriction violations for locale {Locale}: {Errors}",
                    sim.Locale, string.Join("; ", restrictionErrors));
            }
        }
    }

    /// <summary>
    /// Captures per-locale interaction model build status from SMAPI and stores it in configuration.
    /// JF-710: a CLEAN capture (no build errors of its own, observed state
    /// SUCCEEDED or IN_PROGRESS) no longer wipes the catalog-sync diagnostics
    /// off the row it replaces. The structured caveat fields and any foreign
    /// diagnostic survive via
    /// <see cref="LibrarySyncService.PreserveLedgerCaveatAcrossCapture"/>
    /// (a model rebuild does not reset the catalog state they describe), while
    /// a capture with its own errors or a failure-weight state still replaces
    /// the row wholesale. JF-719 widened the gate from SUCCEEDED-only to the
    /// two no-failure-weight states (the no-settle-wait rationale lives at
    /// the gate). JF-722: per-locale failures (a null LastModified entry on a
    /// freshly created skill, or any malformed per-locale shape) are isolated
    /// to their own locale instead of aborting the capture for every later
    /// locale in dictionary order. JF-724: every row is written through the
    /// atomic UpdateLocaleModelStatus (the preserve read and the write share
    /// one ledger-lock acquisition) and stamped with the observed skill's id
    /// (<see cref="Configuration.LocaleModelStatus.ObservedSkillId"/>), which
    /// the deferred refresh's family predicate matches against its own skill.
    /// Internal for the InternalsVisibleTo test seam (the startup-path pins
    /// drive the capture directly through a faked GetSkillStatusAsync); the
    /// call sites reach it only through
    /// <see cref="CaptureAndScheduleStatusRefreshAsync"/> so its deferred
    /// refresh (which settles the IN_PROGRESS statuses this no-settle-wait
    /// capture legitimately freezes, JF-722) is never left unscheduled.
    /// Returns whether ANY row was written: false is the sparse-status shape
    /// (no interactionModel surface, or every entry pre-build with a null
    /// LastModified), which is what tells the paired refresh to recapture
    /// later (JF-722 rework F3: both a freshly created skill and a transient
    /// sparse response on an established skill land there). The optional
    /// token exists for the refresh's creation-mode recapture leg: the capture
    /// checkpoints after its GET and before its save so a recapture poll
    /// cannot race process exit with an XML config write either (rework F2's
    /// boundary, closed at the save every writer-driven capture shares).
    /// </summary>
    internal async Task<bool> CaptureLocaleModelStatusesAsync(Entities.User user, string skillId, CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await AlexaUtil.CallAsync(user, () => user.SmapiManagement!.GetSkillStatusAsync(skillId)).ConfigureAwait(false);

            // Rework F2: the refresh's creation-mode recapture leg calls this
            // capture with its own token; stop here if the startup was torn down
            // while the GET was in flight, before any ledger write below.
            cancellationToken.ThrowIfCancellationRequested();

            var config = Plugin.Instance!.Configuration;
            var now = DateTime.UtcNow;
            bool wroteAny = false;

            // Same sparse-status guard the deferred refresh carries: a status GET
            // with no interactionModel surface at all means no locale has anything
            // to report yet. TWO shapes land here (JF-722 rework F7: the log must
            // not point a post-deploy triage at skill creation alone): a freshly
            // created skill before its first builds register, and a transient
            // sparse response on an established skill after a version bump. Skip
            // the capture instead of letting the foreach dereference throw into
            // the whole-method catch below.
            if (status.InteractionModel == null)
            {
                _logger.LogWarning(
                    "Startup capture found no per-locale build statuses at all for skill {SkillId} (expected on a freshly created skill until its first builds register, or a transient sparse status response on an established skill after a deploy); nothing captured this pass",
                    skillId);
                return false;
            }

            foreach (var kvp in status.InteractionModel)
            {
                string locale = kvp.Key;
                var localeStatus = kvp.Value;

                // JF-722 residual 2: a freshly created skill can register a locale
                // whose first build has not started (LastModified null), exactly the
                // skill-creation capture path below. There is no observation to
                // record, so the locale is SKIPPED (its previous row, if any, stays
                // the last settled truth) instead of throwing the
                // NullReferenceException the old unguarded dereference did.
                if (localeStatus?.LastModified == null)
                {
                    _logger.LogWarning(
                        "Startup capture found no reported build status for locale {Locale} (a freshly created skill registers locales before their first build starts); skipping it and capturing the remaining locales",
                        locale);
                    continue;
                }

                try
                {
                    string state = localeStatus.LastModified.Status.ToString();

                    var (observedError, observedCaveat) = ComposeObservedErrorsCaveat(localeStatus.Errors);
                    if (observedError != null)
                    {
                        _logger.LogWarning(
                            "Interaction model build {Status} for locale {Locale}: {Error}",
                            state, locale, observedError);
                    }

                    // The preserve gate (JF-710/JF-719): the capture describes the
                    // model-build surface only; the existing catalog-sync row's
                    // caveat fields and Error describe catalog state the rebuild
                    // did not reset. The gate is an ALLOWLIST of the two
                    // no-failure-weight states: SUCCEEDED (settled clean) and
                    // IN_PROGRESS (the freshly-PUT locale this no-settle-wait
                    // capture legitimately reads right after UpdateSkillAsync,
                    // JF-719; the diagnostics panel counts IN_PROGRESS as healthy,
                    // so a preserved clause on it beats the wiped row the
                    // SUCCEEDED-only gate left behind). Anything else keeps its
                    // own consumer weight (the panel's failedModels count) and
                    // replaces wholesale, so a build-failure observation never
                    // carries a catalog clause that had nothing to do with it; an
                    // allowlist, not a FAILED/TIMEOUT denylist, so a state the
                    // SkillStatusState enum grows into fails safe the same way.
                    // TIMEOUT, the ledger's other failure vocabulary, is already
                    // unreachable here: SkillStatusState is exactly
                    // IN_PROGRESS|FAILED|SUCCEEDED (reflection-dumped from the
                    // referenced Alexa.NET.Management), and TIMEOUT rows in the
                    // ledger come from the sync writers' own poll outcome, never
                    // from this status GET.
                    bool preserveEligible = observedError == null
                        && localeStatus.LastModified.Status is SkillStatusState.SUCCEEDED or SkillStatusState.IN_PROGRESS;

                    // CONCURRENCY (JF-724, closing the KNOWN RACE the JF-710
                    // gate-marker recorded; the full statement lives on the
                    // ledger's lock doc in PluginConfiguration): the preserve's
                    // read of the existing row and the write below are ONE atomic
                    // UpdateLocaleModelStatus call under the ledger lock, so a
                    // sync leg (or the deferred refresh) landing between them can
                    // no longer be overwritten with this pass's stale read; the
                    // old sub-millisecond window opened on every preserve fire
                    // (~10-17 locales per version-bump restart). JF-721's field
                    // reshape did not materially change the window (marker-string
                    // composition vs field copy: pure in-memory either way); the
                    // atomic accessor closes it.
                    // JF-721: the preserve is a pure FIELD COPY (no parsing); the
                    // run-scoped NoCatalogPut bit drops inside it because this
                    // capture follows a skill UPDATE that pushed a new version.
                    // JF-724: the row is stamped with the observed skill's id so
                    // the deferred refresh (per-skill) can tell this capture's
                    // rows from another linked user's.
                    var written = config.UpdateLocaleModelStatus(locale, existing =>
                    {
                        string? error = observedError;
                        var caveat = observedCaveat;
                        string? frozenCatalogTypes = null;
                        if (preserveEligible)
                        {
                            var preserved = LibrarySyncService.PreserveLedgerCaveatAcrossCapture(existing);
                            if (preserved is { } survivor)
                            {
                                caveat = survivor.Caveat;
                                frozenCatalogTypes = survivor.FrozenCatalogTypes;
                                error = survivor.Error;
                            }

                            // Nothing survived: error and caveat stay as initialized
                            // (null / None), the capture's own clean outcome.
                        }

                        return new Configuration.LocaleModelStatus
                        {
                            Status = state,
                            LastUpdated = now,
                            Error = error,
                            Caveat = caveat,
                            FrozenCatalogTypes = frozenCatalogTypes,
                            Source = CaptureLedgerSource,
                            ObservedSkillId = skillId,
                        };
                    });

                    if (preserveEligible)
                    {
                        // Branch-decision debug per the logging policy: this
                        // subsystem's incidents (JF-495/705/709/710/719) are
                        // triaged from ledger forensics, and the preserve arm is
                        // otherwise indistinguishable from an all-clean overwrite
                        // in the written row.
                        _logger.LogDebug(
                            "Startup capture preserve fired for locale {Locale}: observed {Status}, surviving Caveat {Caveat}, surviving Error '{Error}'",
                            locale, state, written!.Caveat, written.Error ?? "(nothing survived)");
                    }

                    wroteAny = true;
                }
                catch (Exception ex)
                {
                    // JF-722 residual 2: one malformed per-locale entry (or a
                    // ledger failure inside the atomic update above) must cost only
                    // THIS locale's row, not every later locale in dictionary order;
                    // the old whole-method catch turned it into a silent partial
                    // capture the admin could not distinguish from a complete one.
                    _logger.LogWarning(ex,
                        "Startup capture failed for locale {Locale}; continuing with the remaining locales",
                        locale);
                }
            }

            // Rework F2: last cancellation checkpoint before the XML write, the
            // save boundary every caller of this capture shares. The save runs
            // under the ledger lock (JF-724 code-review F1: SaveConfiguration
            // serializes the live collection and would otherwise race another
            // writer's Add/replace mid-enumeration), and a save FAILURE is
            // non-fatal WITHOUT flipping the return: the rows ARE written in
            // memory, and returning false here would flip the paired refresh
            // into its recapture mode, spending the whole budget re-GETting
            // rows this capture already wrote.
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                config.SaveUnderLedgerLock(() => Plugin.Instance!.SaveConfiguration());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to persist the captured locale statuses for skill {SkillId}; the rows stay in memory until the next save. Non-critical.",
                    skillId);
            }

            return wroteAny;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Real shutdown, not a capture failure: the token-state filter keeps
            // the generic warning below from mislabeling it. The filter is
            // load-bearing: an HttpClient TIMEOUT surfaces as
            // TaskCanceledException (an OCE subclass; Alexa.NET.Management's
            // client carries the default 100s timeout) WITHOUT any cancellation,
            // and rethrowing that shape would kill the whole deferred refresh
            // silently instead of retrying (code-review refresh RC1).
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to capture per-locale model status for skill {SkillId}. Non-critical.", skillId);
            return false;
        }
    }

    /// <summary>
    /// The ledger Source label shared by the startup capture and its deferred
    /// refresh (the capture family). The refresh's family predicate keys on this
    /// exact value (Ordinal), so writer and recognizer must be the one constant: a
    /// drifted literal on either side silently no-ops the whole refresh (the
    /// pre-check exits before any poll, no error anywhere) and the frozen
    /// IN_PROGRESS rows come back (the JF-722 symptom). Same pattern as
    /// <see cref="LibrarySyncService.CatalogSyncLedgerSource"/> for the sync writers.
    /// </summary>
    internal const string CaptureLedgerSource = "Embedded";

    /// <summary>
    /// The ONE composer of the observation family's own-errors arm (JF-722
    /// rework F1, made structural by JF-721), shared by the startup capture and
    /// its deferred refresh: a non-empty observed Errors array becomes the row's
    /// free-text Error TAGGED with the ObservedBuildErrors caveat bit, so the
    /// clean-settle arm can tell this arm's product from the preserve's product
    /// and drop the PAIR together (DropObservedBuildErrors, the drop rule's one
    /// owner). The bit-and-text pairing is load-bearing: a writer that sets one
    /// without the other re-creates a wire protocol, the exact failure class
    /// JF-721 deleted, hence one owner here beside the shared
    /// LibrarySyncService.FormatInvocationErrors formatter. A clean observation
    /// yields (null, None) and the caller's own arm logic owns the row.
    /// </summary>
    private static (string? Error, Configuration.CatalogLedgerCaveats Caveat) ComposeObservedErrorsCaveat(
        global::Alexa.NET.Management.Skills.InvocationError[]? errors) =>
        LibrarySyncService.FormatInvocationErrors(errors) is { } error
            ? (error, Configuration.CatalogLedgerCaveats.ObservedBuildErrors)
            : (null, Configuration.CatalogLedgerCaveats.None);

    /// <summary>
    /// The structural pairing of the startup capture and its deferred IN_PROGRESS
    /// refresh (JF-722 residual 1). Every capture site MUST go through here, so no
    /// future capture call path can freeze the freshly-PUT locales' Status into the
    /// ledger without also scheduling the background settle-and-rewrite that frees
    /// it (the "missed one" wiring class; a hand-scheduled site compiles clean and
    /// silently leaves rows gray until the weekly sync). The refresh's pre-check
    /// makes unconditional scheduling free when nothing froze. The capture is
    /// awaited FIRST so the refresh's ledger pre-check reads the rows it just
    /// wrote.
    /// THE RECAPTURE RATIONALE (JF-722 rework F3, authoritative here; the call
    /// sites and the worker's parameter carry only pointers): a capture can
    /// write ZERO rows when the status response is sparse, which happens BOTH
    /// at skill creation (the capture runs before any build registers) and as a
    /// transient sparse response on an established skill after a version bump.
    /// A zero-row capture would leave the refresh's no-frozen-rows pre-check
    /// nothing to find, so this build's builds would never be observed until
    /// the weekly sync (the frozen-gray JF-722 symptom through a second door).
    /// The wrapper therefore DERIVES the mode from the capture's own return
    /// (did it write any row) instead of a call-site literal: both sparse
    /// shapes are covered at both sites with no self-classification a future
    /// site could get wrong.
    /// </summary>
    private async Task CaptureAndScheduleStatusRefreshAsync(Entities.User user, string skillId, CancellationToken cancellationToken)
    {
        bool captureWroteNoRows = !await CaptureLocaleModelStatusesAsync(user, skillId, cancellationToken).ConfigureAwait(false);
        ScheduleInProgressLocaleStatusRefresh(user, skillId, captureWroteNoRows, cancellationToken);
    }

    /// <summary>
    /// How long the deferred IN_PROGRESS refresh waits before its first poll: model
    /// builds take ~15-30s and the capture fires at the PUT loop's end, so by
    /// capture+45s every concurrently-running build has settled (the JF-722 corrected
    /// analysis: ~10-17 of 17 locales read IN_PROGRESS at capture on a version-bump
    /// restart).
    /// </summary>
    private static readonly TimeSpan InProgressRefreshInitialDelay = TimeSpan.FromSeconds(45);

    /// <summary>
    /// The interval between the deferred refresh's polls, and (with the 4-poll cap)
    /// the shape of its budget: ~3 minutes of coverage, early-exit the moment the
    /// family empties. The budget deliberately does NOT chase a fully SMAPI-serialized
    /// 17-deep build queue (17 x 15s would need unbounded polling); rows still
    /// IN_PROGRESS at exhaustion keep their healthy-neutral status and the weekly
    /// catalog sync stays the backstop.
    /// </summary>
    private static readonly TimeSpan InProgressRefreshPollInterval = TimeSpan.FromSeconds(45);

    /// <summary>See <see cref="InProgressRefreshPollInterval"/> for the budget shape.</summary>
    private const int InProgressRefreshMaxPolls = 4;

    /// <summary>
    /// Fires the deferred IN_PROGRESS status refresh (JF-722 residual 1) for a
    /// capture that just ran: fire-and-forget, so startup latency is unaffected (the
    /// settle-wait alternative was rejected twice: once at JF-719 for the capture
    /// itself, and by the corrected JF-722 analysis showing it would cost ~17x the
    /// settle budget it implicitly assumes). Runs under the startup's linked
    /// cancellation token, so shutdown cancels the delay and the worker exits
    /// quietly; the discarded task never faults because the worker catches its own
    /// exceptions. The captureWroteNoRows flag is the derived recapture mode
    /// (see <see cref="CaptureAndScheduleStatusRefreshAsync"/>'s rationale).
    /// </summary>
    private void ScheduleInProgressLocaleStatusRefresh(Entities.User user, string skillId, bool captureWroteNoRows, CancellationToken cancellationToken)
    {
        _ = Task.Run(
            () => RefreshInProgressLocaleStatusesAsync(
                user,
                skillId,
                cancellationToken,
                InProgressRefreshInitialDelay,
                InProgressRefreshPollInterval,
                InProgressRefreshMaxPolls,
                captureWroteNoRows),
            cancellationToken);
    }

    /// <summary>
    /// The deferred Status half of the startup capture (JF-722 residual 1): the
    /// capture legitimately reads freshly-PUT locales as IN_PROGRESS (no settle-wait,
    /// JF-719), but nothing re-reads them afterwards (both capture call sites gate on
    /// version mismatch / manifest FAILED / skill creation, so an ordinary restart
    /// runs no capture), which froze the momentary state into the ledger and left the
    /// diagnostics panel's ModelsDeployed checklist false with most rows gray until
    /// the weekly sync. This refresh waits out the settle window in the background,
    /// then rewrites the frozen rows from a fresh observation: SUCCEEDED rows feed
    /// the checklist, FAILED rows surface their errors, and still-in-flight rows keep
    /// the healthy-neutral IN_PROGRESS. The JF-710/JF-719 preserve is NOT re-run
    /// here: the capture already composed the row's Error, so a clean settle carries
    /// it forward verbatim and only a failure-weight observation replaces wholesale
    /// (the capture's own branch semantics; see
    /// <see cref="RewriteSettledInProgressRowsAsync"/>). Internal for the
    /// InternalsVisibleTo test seam, driven through the same faked
    /// GetSkillStatusAsync as the capture.
    /// </summary>
    /// <param name="user">The user whose skill was captured.</param>
    /// <param name="skillId">The skill the capture read.</param>
    /// <param name="cancellationToken">The startup's cancellation token, observed
    /// at every delay and inside each poll body (after every await), so a full
    /// poll cannot run against a torn-down startup (JF-722 rework F2).</param>
    /// <param name="initialDelay">Wait before the first poll (tests pass zero).</param>
    /// <param name="pollInterval">Wait between polls (tests pass zero).</param>
    /// <param name="maxPolls">Poll budget (tests shrink it).</param>
    /// <param name="captureWroteNoRows">The derived recapture mode (see
    /// <see cref="CaptureAndScheduleStatusRefreshAsync"/>'s rationale): the
    /// paired capture wrote zero rows, so skip the no-frozen-rows pre-check
    /// and re-run the capture after the settle window until it writes
    /// rows.</param>
    internal async Task RefreshInProgressLocaleStatusesAsync(
        Entities.User user,
        string skillId,
        CancellationToken cancellationToken,
        TimeSpan? initialDelay = null,
        TimeSpan? pollInterval = null,
        int? maxPolls = null,
        bool captureWroteNoRows = false)
    {
        try
        {
            // The ledger is read FRESH at every use below (never captured across
            // the ~3-minute budget): Jellyfin's UpdateConfiguration REPLACES the
            // Configuration object on any admin save, so a captured reference
            // would write settled rows into an orphan the next SaveConfiguration
            // never persists (the JF-722 review's F2).
            //
            // Early exit BEFORE any delay or network call: captures whose every
            // observation settled (or that hit no IN_PROGRESS branch) leave nothing
            // to refresh. The scheduling is ordered after the capture completed, so
            // its rows are already written when this pre-check reads them. The
            // read goes through the locked snapshot and is scoped to THIS skill's
            // family rows (JF-724: another linked user's frozen rows neither
            // trigger this refresh's polls nor keep its budget alive); the
            // snapshot's lock retired the JF-709 enumeration guard this call used
            // to carry, and any future failure still falls to the worker's own
            // non-fatal catch below. In the derived recapture mode
            // (captureWroteNoRows) the pre-check is skipped: a zero-row capture is
            // the EXPECTED state there, not a nothing-to-refresh signal.
            bool hasFrozenRows = captureWroteNoRows;
            if (!captureWroteNoRows)
            {
                hasFrozenRows = HasInProgressCaptureRows(skillId);
            }

            if (!hasFrozenRows)
            {
                _logger.LogDebug("No IN_PROGRESS capture rows to refresh for skill {SkillId}; skipping the deferred refresh", skillId);
                return;
            }

            await Task.Delay(initialDelay ?? InProgressRefreshInitialDelay, cancellationToken).ConfigureAwait(false);

            int polls = maxPolls ?? InProgressRefreshMaxPolls;
            bool lastPollFailed = false;
            bool recapturePending = captureWroteNoRows;
            for (int poll = 1; poll <= polls; poll++)
            {
                try
                {
                    if (recapturePending)
                    {
                        // The derived recapture mode (JF-722 rework F3): write the
                        // rows the zero-row capture could not see. The capture
                        // swallows its own GET failures internally (its non-fatal
                        // whole-method contract), so the retry signal is the
                        // capture's OWN return, per-skill and per-invocation:
                        // keep recapturing until it reports writing rows (covers
                        // a still-sparse response AND transient GET failures, on
                        // a fresh install and a recreate alike); then the normal
                        // rewrite loop owns the settling.
                        bool recaptureWroteAny = await CaptureLocaleModelStatusesAsync(user, skillId, cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (recaptureWroteAny)
                        {
                            recapturePending = false;
                        }
                    }

                    await RewriteSettledInProgressRowsAsync(user, skillId, cancellationToken).ConfigureAwait(false);
                    lastPollFailed = false;

                    // Never early-exit while the recapture is still pending: an
                    // empty ledger is the expected pre-recapture state, not the
                    // all-settled signal it is on the version-bump path.
                    if (!recapturePending && !HasInProgressCaptureRows(skillId))
                    {
                        return;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Token-state filter, same reason as the capture's: a
                    // timeout-shaped TaskCanceledException from the status GET
                    // must fall through to the retry arm below, not exit here.
                    throw;
                }
                catch (Exception ex)
                {
                    lastPollFailed = true;

                    // A failed poll (e.g. a transient 429/5xx on the status GET)
                    // must not spend the whole budget: warn and let the next poll
                    // retry if the budget allows. The rows keep their
                    // healthy-neutral IN_PROGRESS.
                    _logger.LogWarning(ex,
                        "Deferred IN_PROGRESS status refresh poll {Poll}/{Polls} failed for skill {SkillId}. Non-critical.",
                        poll, polls, skillId);
                }

                if (poll < polls)
                {
                    await Task.Delay(pollInterval ?? InProgressRefreshPollInterval, cancellationToken).ConfigureAwait(false);
                }
            }

            // The two exhaustion shapes say different true things: rows OBSERVED
            // still in flight vs a budget that ended without a final observation
            // (this subsystem's incidents are triaged from log forensics; the
            // pair must not claim an observation that never happened).
            if (lastPollFailed)
            {
                _logger.LogWarning(
                    "Deferred IN_PROGRESS refresh budget for skill {SkillId} elapsed without a final observation (the last poll failed); rows last observed IN_PROGRESS keep their healthy-neutral status and the next catalog sync rewrites them",
                    skillId);
            }
            else if (recapturePending)
            {
                // The third exhaustion shape (code-review refresh RC3): the
                // status never became observable within the budget (every
                // recapture found nothing to report), so claiming rows are
                // still in flight over an empty ledger would mislead triage.
                _logger.LogWarning(
                    "The skill status for skill {SkillId} never became observable within the deferred refresh budget (every recapture found no per-locale statuses to report); no rows were written and the next catalog sync remains the backstop",
                    skillId);
            }
            else
            {
                _logger.LogWarning(
                    "Locale rows are still IN_PROGRESS after the deferred refresh budget for skill {SkillId}; they keep their healthy-neutral status (the diagnostics panel does not count IN_PROGRESS as failed) and the next catalog sync rewrites them",
                    skillId);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown raced the refresh. The IN_PROGRESS rows keep their documented
            // healthy-neutral weight; the next capture-and-refresh cycle owns them.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Deferred IN_PROGRESS locale status refresh failed for skill {SkillId}. Non-critical.", skillId);
        }
    }

    /// <summary>
    /// One refresh poll: re-reads the skill status and rewrites the settled rows of
    /// the capture family (see <see cref="IsInProgressCaptureStatus"/>). The ledger
    /// is read fresh at poll time (an admin config save replaces the Configuration
    /// object; a captured reference would orphan this pass's writes, and the next
    /// poll simply redoes the work against the live ledger). JF-724 closed the
    /// multiplied KNOWN RACE this poll used to be (up to 4 polls x 17 locales of
    /// unguarded read-modify-write overlapping the post-restart CatalogSyncTask
    /// window, whose clobber victim was a sync-authored settled row's real canary
    /// diagnostic): each row's family read, settle decision, and write are ONE
    /// atomic UpdateLocaleModelStatus call under the configuration's ledger lock.
    /// CROSS-USER (JF-724): the family predicate also matches the row's
    /// ObservedSkillId stamp against THIS refresh's skill, so another linked
    /// user's frozen rows are neither settled from this skill's status nor able
    /// to keep this refresh's budget alive. Caveat/Error semantics per row mirror
    /// the capture's own branches (field-keyed since JF-721): observed build
    /// errors (ObservedBuildErrors-tagged, like the capture's arm), or a
    /// failure-weight state without them, replace the row wholesale; a clean
    /// SUCCEEDED observation carries the existing caveat fields and Error
    /// forward verbatim, dropping them only when the row's
    /// ObservedBuildErrors bit says its Error was this observation family's own
    /// (stale observed-errors dropped, preserve product carried), because the
    /// capture already ran the JF-710/JF-719 preserve when it wrote the row and
    /// re-running it here would re-copy an already-copied product.
    /// </summary>
    private async Task RewriteSettledInProgressRowsAsync(Entities.User user, string skillId, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return;
        }

        var status = await AlexaUtil.CallAsync(user, () => user.SmapiManagement!.GetSkillStatusAsync(skillId)).ConfigureAwait(false);
        if (status.InteractionModel == null)
        {
            return;
        }

        // JF-722 rework F2: StopAsync/Dispose can have torn the startup's CTS down
        // while the GET was in flight; stop BEFORE touching the shared ledger or
        // saving, so a full poll cannot race process exit with an XML config
        // write. The OperationCanceledException lands in the worker's quiet
        // catch. Residual window (accepted, documented): the synchronous
        // in-memory loop pass itself contains no further checkpoints.
        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTime.UtcNow;
        bool any = false;
        foreach (var kvp in status.InteractionModel)
        {
            string locale = kvp.Key;
            var localeStatus = kvp.Value;

            // Same malformed-entry guard as the capture (JF-722 residual 2): no
            // LastModified means no observation for this locale; nothing to rewrite.
            if (localeStatus?.LastModified == null)
            {
                continue;
            }

            // Still building; a later poll in the budget picks it up. Checked
            // before the ledger read so the skip path does no discarded work.
            if (localeStatus.LastModified.Status is SkillStatusState.IN_PROGRESS)
            {
                continue;
            }

            // Same per-locale isolation as the capture (JF-722 residual 2): a
            // throw for one locale (a ledger failure inside the atomic update
            // below) must cost only THIS locale's row, not every later
            // locale's in dictionary order; the next poll retries what missed.
            try
            {
                string state = localeStatus.LastModified.Status.ToString();

                var (observedError, observedCaveat) = ComposeObservedErrorsCaveat(localeStatus.Errors);

                // Observed build errors replace the row wholesale; a clean
                // SUCCEEDED settle carries the row's fields forward; a
                // failure-weight state without error details replaces wholesale
                // too (matching the capture's FAILED-without-errors branch) -
                // both fall through with the observation's own (null, None).
                bool cleanSettle = observedError == null
                    && localeStatus.LastModified.Status == SkillStatusState.SUCCEEDED;

                // CONCURRENCY (JF-724, closing the multiplied KNOWN RACE): the
                // family read, the settle decision, and the write are ONE atomic
                // UpdateLocaleModelStatus call under the ledger lock, so a
                // sync-authored settled row carrying a real canary diagnostic
                // that lands between the read and the write can no longer be
                // overwritten with the stale read's Error (the diagnostic-loss
                // victim of the JF-722 rework F4 filing). The transform is pure
                // and runs under the lock; returning null declines the write
                // (not this skill's family row).
                var written = config.UpdateLocaleModelStatus(locale, existing =>
                {
                    if (existing == null || !IsInProgressCaptureStatus(existing.Status, existing.Source, existing.ObservedSkillId, skillId))
                    {
                        return null;
                    }

                    string? error = observedError;
                    var caveat = observedCaveat;
                    string? frozenCatalogTypes = null;
                    if (cleanSettle)
                    {
                        // Clean settle: the capture composed the row in exactly one of
                        // two arms. The preserve's product (frozen caveat bits, foreign
                        // diagnostic: durable catalog state the settle says nothing
                        // about) carries forward VERBATIM as a field copy, no
                        // re-decomposition. The observed-errors arm's product is
                        // arm-tagged build errors from the error-carrying observation:
                        // the settle observation is clean, so those errors are stale
                        // (the unverified SMAPI shape where an IN_PROGRESS status still
                        // carries the previous build's Errors array, JF-722 rework F1)
                        // and are DROPPED together with the bit by the ONE shared owner
                        // of that rule (LibrarySyncService.DropObservedBuildErrors, the
                        // same helper the capture's preserve routes through; a field
                        // read since JF-721, so no foreign text can ever collide with
                        // it the way the old prefix StartsWith could). The whole
                        // carry runs through the ONE shared owner of the copy shape
                        // (CarryLedgerCaveatAcrossCleanObservation, the same helper
                        // the capture's preserve routes through; gate-marker rework
                        // R2), which also migrates a pre-JF-721 composed Error once
                        // and guards payload-iff-bit (the preserve's F2 rule).
                        (caveat, frozenCatalogTypes, error) = LibrarySyncService.CarryLedgerCaveatAcrossCleanObservation(
                            existing.Caveat, existing.FrozenCatalogTypes, existing.Error);
                    }

                    // JF-724: the settled row keeps its Source and is re-stamped
                    // with THIS skill's id (a legacy null-attribution row the
                    // settle owns becomes attributed from here on).
                    return new Configuration.LocaleModelStatus
                    {
                        Status = state,
                        LastUpdated = now,
                        Error = error,
                        Caveat = caveat,
                        FrozenCatalogTypes = frozenCatalogTypes,
                        Source = existing.Source,
                        ObservedSkillId = skillId,
                    };
                });

                if (written == null)
                {
                    continue;
                }

                if (observedError != null)
                {
                    _logger.LogWarning(
                        "Interaction model build settled {Status} for locale {Locale}: {Error}",
                        state, locale, observedError);
                }

                // Branch-decision debug per the logging policy: the rewrite is otherwise
                // indistinguishable from a fresh capture in the written row. The
                // surviving Caveat rides the log like the capture's twin (JF-721
                // code-review F4): this subsystem's incidents are triaged from
                // ledger forensics, and the clean-settle arm carries or drops the
                // caveat bits the preserve minted.
                _logger.LogDebug(
                    "Deferred refresh rewrote locale {Locale}: IN_PROGRESS -> {Status}, Caveat {Caveat}, Error '{Error}'",
                    locale, written.Status, written.Caveat, written.Error ?? "(none)");

                any = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Deferred IN_PROGRESS refresh failed for locale {Locale}; continuing with the remaining locales",
                    locale);
            }
        }

        if (any)
        {
            // Last cancellation checkpoint before the XML write (JF-722 rework
            // F2); the save runs under the ledger lock (JF-724 code-review F1).
            cancellationToken.ThrowIfCancellationRequested();
            config.SaveUnderLedgerLock(() => Plugin.Instance!.SaveConfiguration());
        }
    }

    /// <summary>
    /// The refresh's family predicate: only rows the STARTUP CAPTURE wrote with a
    /// still-frozen IN_PROGRESS status. The catalog-sync writers never author
    /// IN_PROGRESS (PreservedOrSkippedStatus clamps it to Skipped; the PUT path
    /// records only settled outcomes), so Status IN_PROGRESS plus the capture
    /// family's Source label (<see cref="CaptureLedgerSource"/>, Ordinal as its
    /// writer emits it; the Status compare is OrdinalIgnoreCase to match the panel
    /// consumers) identifies exactly the frozen rows this refresh exists to settle.
    /// JF-724 cross-user scope: the row's ObservedSkillId stamp must name THIS
    /// refresh's skill (or be null: pre-JF-724 persisted rows and hand-authored
    /// rows keep today's any-refresh eligibility), so with two linked SMAPI
    /// users one user's refresh does not settle the other's frozen rows from
    /// its own skill's status. A writer that omits Source cannot join the
    /// family either: the record's Source default is EMPTY since JF-724 (the
    /// old "Embedded" default drafted omitting writers in, the JF-722 rework
    /// F5 gap).
    /// </summary>
    private static bool IsInProgressCaptureStatus(string? status, string? source, string? observedSkillId, string skillId) =>
        string.Equals(status, "IN_PROGRESS", StringComparison.OrdinalIgnoreCase)
        && string.Equals(source, CaptureLedgerSource, StringComparison.Ordinal)
        && (observedSkillId == null || string.Equals(observedSkillId, skillId, StringComparison.Ordinal));

    /// <summary>
    /// Whether any ledger row still belongs to THIS skill's frozen capture family.
    /// Reads the LIVE configuration on every call (never a captured reference: an
    /// admin config save replaces the Configuration object, JF-722 review F2)
    /// through the locked snapshot (JF-724: a concurrent writer's mutation can
    /// no longer throw the scan, which also retired the JF-709 degrade guard
    /// this call used to carry: any future failure here falls to the worker's
    /// own non-fatal catch).
    /// </summary>
    private static bool HasInProgressCaptureRows(string skillId)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return false;
        }

        foreach (var entry in config.GetLocaleModelStatusSnapshot())
        {
            if (IsInProgressCaptureStatus(entry.Status, entry.Source, entry.ObservedSkillId, skillId))
            {
                return true;
            }
        }

        return false;
    }
}
