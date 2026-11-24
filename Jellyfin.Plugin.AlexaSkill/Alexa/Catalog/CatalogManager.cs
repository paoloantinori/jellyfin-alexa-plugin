#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa.InteractionModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;

/// <summary>
/// Manages the SMAPI catalog lifecycle: creating catalogs, uploading values,
/// and creating/updating slot types that reference those catalogs.
/// Each user's dynamic media library values (artists, albums, etc.) are
/// stored in SMAPI catalogs so the Alexa NLU can resolve them at runtime.
/// </summary>
public class CatalogManager
{
    internal const string SmapiEndpoint = "https://api.amazonalexa.com";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CatalogManager> _logger;

    /// <summary>
    /// Locales where Amazon's asynchronous FULL build fails whenever the model carries a
    /// catalog-backed slot type, with NO error detail (only LANGUAGE_MODEL_FULL_BUILD
    /// FAILED; dialog and NFI steps succeed). The failure rate scales with the referenced
    /// catalog's value count, the same payload builds reliably in every other locale, and
    /// the locale's own embedded model builds reliably. Live bisection 2026-09-12 (JF-543):
    /// ar-SA + artist catalog (1135 values) = 0/7 builds; + album catalog (895) = 1/5;
    /// + series catalog (138) = 3/3; de-DE + the identical artist-catalog definition = 3/3.
    /// Until Amazon fixes the ar-SA trainer, catalog wiring stays off there: the locale
    /// keeps its embedded model with AMAZON.Musician slots, which builds and serves
    /// normally. Re-probe before removing a locale from this set.
    /// </summary>
    internal static readonly HashSet<string> CatalogWiringUnsupportedLocales =
        new(StringComparer.OrdinalIgnoreCase) { "ar-SA" };

    /// <summary>Whether the locale's model can carry catalog-backed slot types on this
    /// platform today (see <see cref="CatalogWiringUnsupportedLocales"/> for evidence).</summary>
    internal static bool IsCatalogWiringSupported(string locale) =>
        !CatalogWiringUnsupportedLocales.Contains(locale);

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogManager"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory for creating named clients.</param>
    /// <param name="logger">The logger instance.</param>
    public CatalogManager(IHttpClientFactory httpClientFactory, ILogger<CatalogManager> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// The pre-poll delay and backoff seed of every SMAPI poll wait in this class;
    /// doubles up to the 2000ms cap on each iteration after the first.
    /// </summary>
    private const int SmapiPollInitialDelayMs = 500;

    /// <summary>
    /// Pacing delay between transient-fetch retries (SMAPI could not fetch the
    /// catalog source URL; the brief pause lets the reverse proxy recover).
    /// </summary>
    private const int TransientFetchRetryDelayMs = 2000;

    /// <summary>
    /// Test seam (null in production, the InterLocaleDelayMsForTest pattern JF-717):
    /// overrides the pacing delays of the SMAPI waits in this class, namely the
    /// poll loop seed of <see cref="PollSmapiOperationAsync"/>, the pre-poll
    /// settle delay and loop seed of
    /// <see cref="WaitForModelBuildOutcomeViaSkillStatusAsync"/>, the loop seed
    /// passed to <see cref="WaitForLocaleBuildToSettleAsync"/>, and the
    /// transient-fetch retry backoff of <see cref="UploadCatalogValuesAsync"/>.
    /// The catalog-sync suites drive instant in-memory fakes, so these
    /// production-coded sleeps dominate their runtime (JF-725 measured ~46s per
    /// TFM across the five delay-paying suites, almost all of it 500ms poll
    /// pre-delays); zero makes each wait a no-op while leaving every budget
    /// (poll count) and status-transition semantic untouched. Beware nonzero
    /// values: they pace the transient-retry backoff at the SAME value, not the
    /// production 2000ms (the knob is not scaled per site); every current suite
    /// uses 0.
    /// </summary>
    internal int? PollDelayMsForTest { get; set; }

    /// <summary>The seam-resolved poll delay every SMAPI wait in this class paces by.</summary>
    private int PollDelayMs => PollDelayMsForTest ?? SmapiPollInitialDelayMs;

    /// <summary>
    /// Creates a new SMAPI catalog for a slot type.
    /// </summary>
    /// <param name="accessToken">The SMAPI access token.</param>
    /// <param name="vendorId">The vendor ID for the skill owner.</param>
    /// <param name="catalogName">A human-readable name for the catalog.</param>
    /// <param name="description">A description for the catalog.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created catalog ID.</returns>
    public async Task<string> CreateCatalogAsync(
        string accessToken,
        string vendorId,
        string catalogName,
        string description,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Creating SMAPI catalog '{CatalogName}' for vendor {VendorId}", catalogName, vendorId);

        var client = _httpClientFactory.CreateClient("AlexaSkill");

        var body = new
        {
            vendorId,
            catalog = new
            {
                name = catalogName,
                description
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{SmapiEndpoint}/v1/skills/api/custom/interactionModel/catalogs");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(body, options: JsonOptions);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        string catalogId = doc.RootElement.GetProperty("catalogId").GetString()
            ?? throw new InvalidOperationException($"Catalog creation response missing catalog ID. Response: {json}");

        _logger.LogInformation("Catalog created successfully: {CatalogId}", catalogId);
        return catalogId;
    }

    /// <summary>
    /// Deletes an SMAPI catalog (JF-949: the song-catalog opt-out teardown).
    /// The caller owns the guard: the id passed here MUST be the one stored on the
    /// caller's own user record (LibrarySyncService passes user.SongCatalogId and
    /// clears it only after this call succeeds), so a foreign catalog can never
    /// be deleted through this path. A 404 is treated as already-deleted success:
    /// the teardown is idempotent and Amazon may have removed the catalog between
    /// the wiring strip and this call. Transient SMAPI failures (503/502/504, the
    /// same reverse-proxy-warming class the upload path retries) retry up to
    /// <see cref="TransientFetchMaxAttempts"/> attempts so one transient 5xx does
    /// not defer the privacy teardown by a full sync interval.
    /// </summary>
    /// <param name="accessToken">The SMAPI access token.</param>
    /// <param name="catalogId">The catalog ID to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    public async Task DeleteCatalogAsync(
        string accessToken,
        string catalogId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Deleting SMAPI catalog {CatalogId} (JF-949 song-catalog opt-out teardown)", catalogId);

        var client = _httpClientFactory.CreateClient("AlexaSkill");
        string url = $"{SmapiEndpoint}/v1/skills/api/custom/interactionModel/catalogs/{Uri.EscapeDataString(catalogId)}";

        for (int attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogInformation("SMAPI catalog {CatalogId} already gone (404); treating the delete as done (JF-949 idempotency)", catalogId);
                return;
            }

            // The same transient family the upload path retries (502/503/504 plus
            // the GATEWAY_ERROR body code). 429 is deliberately NOT in the set:
            // the upload path does not retry it either, so the delete keeps the
            // exact same semantics (a rate-limited delete defers the teardown one
            // sync interval, same as a rate-limited upload would, JF-949 review
            // round note).
            bool transient = (int)response.StatusCode is 502 or 503 or 504;
            if (!transient || attempt >= TransientFetchMaxAttempts)
            {
                await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
                break;
            }

            _logger.LogWarning(
                "Transient SMAPI {StatusCode} deleting catalog {CatalogId} (attempt {Attempt}/{Max}); retrying (JF-949)",
                (int)response.StatusCode, catalogId, attempt, TransientFetchMaxAttempts);
            await Task.Delay(PollDelayMsForTest ?? TransientFetchRetryDelayMs, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("SMAPI catalog {CatalogId} deleted", catalogId);
    }

    /// <summary>
    /// Maximum number of attempts when a catalog version build fails with a transient
    /// GATEWAY_ERROR (SMAPI could not fetch the source URL, e.g. the reverse proxy was
    /// still warming up after a Jellyfin restart). Each retry re-stores the payload (fresh
    /// source URL, since SMAPI consumes the URL on fetch) and re-creates the version.
    /// </summary>
    private const int TransientFetchMaxAttempts = 3;

    /// <summary>
    /// Creates a catalog version by providing a hosted URL for SMAPI to pull.
    /// SMAPI flow: store payload in cache -> create version with source URL -> poll status.
    /// On a transient GATEWAY_ERROR (SMAPI could not fetch the source URL), retries with a
    /// fresh URL from <paramref name="catalogUrlFactory"/> up to <see cref="TransientFetchMaxAttempts"/>
    /// times. Non-transient failures (real validation errors) throw immediately.
    /// </summary>
    /// <param name="accessToken">The SMAPI access token.</param>
    /// <param name="catalogId">The target catalog ID.</param>
    /// <param name="payload">The catalog values payload to upload.</param>
    /// <param name="catalogUrlFactory">Returns a fresh public URL where SMAPI can fetch the catalog JSON. Called once per attempt so each retry gets an unconsumed URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The committed version string.</returns>
    public async Task<string> UploadCatalogValuesAsync(
        string accessToken,
        string catalogId,
        CatalogPayload payload,
        Func<string> catalogUrlFactory,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("AlexaSkill");

        Exception? lastFailure = null;
        for (int attempt = 1; attempt <= TransientFetchMaxAttempts; attempt++)
        {
            string catalogUrl = catalogUrlFactory();
            _logger.LogInformation(
                "Creating catalog version for {CatalogId} with {ValueCount} values from {Url} (attempt {Attempt}/{Max})",
                catalogId,
                payload.Values.Count,
                catalogUrl,
                attempt,
                TransientFetchMaxAttempts);

            var versionBody = new
            {
                source = new
                {
                    type = "URL",
                    url = catalogUrl
                },
                description = $"Library sync {DateTime.UtcNow:O}"
            };

            using var versionRequest = new HttpRequestMessage(HttpMethod.Post, $"{SmapiEndpoint}/v1/skills/api/custom/interactionModel/catalogs/{catalogId}/versions");
            versionRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            versionRequest.Content = JsonContent.Create(versionBody, options: JsonOptions);

            using var versionResponse = await client.SendAsync(versionRequest, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(versionResponse, cancellationToken).ConfigureAwait(false);

            // 202 Accepted with Location header for polling
            Uri? locationUri = versionResponse.Headers.Location;
            if (locationUri == null)
            {
                _logger.LogWarning("Catalog version creation returned no Location header");
                _logger.LogWarning(
                    "Falling back to catalog version \"1\" for {CatalogId}; the version minted by this upload is unknown and the interaction model may pin a stale version (JF-495)",
                    catalogId);
                return "1";
            }

            locationUri = ResolveLocationUri(locationUri);

            try
            {
                string? version = await PollSmapiOperationAsync(
                    accessToken, client, locationUri, "Catalog version", cancellationToken).ConfigureAwait(false);

                if (version == null)
                {
                    _logger.LogWarning(
                        "Catalog version poll for {CatalogId} succeeded but reported no version; falling back to \"1\", which may pin a stale version in the interaction model (JF-495)",
                        catalogId);
                }

                _logger.LogInformation("Catalog {CatalogId} version {Version} created successfully", catalogId, version);
                return version ?? "1";
            }
            catch (InvalidOperationException ex) when (attempt < TransientFetchMaxAttempts && IsTransientFetchFailure(ex))
            {
                // SMAPI could not fetch the source URL (503/GATEWAY_ERROR). The URL is
                // consumed, so retry with a fresh one. Brief delay to let the proxy recover.
                lastFailure = ex;
                _logger.LogWarning(
                    "Catalog version for {CatalogId} failed with a transient fetch error (attempt {Attempt}/{Max}); retrying with a fresh source URL. Error: {Error}",
                    catalogId, attempt, TransientFetchMaxAttempts, ex.Message);
                await Task.Delay(PollDelayMsForTest ?? TransientFetchRetryDelayMs, cancellationToken).ConfigureAwait(false);
            }
        }

        // Exhausted retries (or the loop exited with a non-transient failure preserved below).
        throw lastFailure ?? new InvalidOperationException($"Catalog version creation exhausted {TransientFetchMaxAttempts} attempts for {catalogId}");
    }

    /// <summary>
    /// True when a FAILED poll's error indicates SMAPI could not fetch the catalog source
    /// URL (a transient, retryable condition): a GATEWAY_ERROR code, or a 502/503/504
    /// status mentioned in the error text. Real validation errors ("rejected catalog
    /// value") are NOT transient and must surface immediately.
    /// </summary>
    private static bool IsTransientFetchFailure(InvalidOperationException ex)
    {
        string msg = ex.Message;
        return msg.Contains("GATEWAY_ERROR", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("502", StringComparison.Ordinal)
            || msg.Contains("503", StringComparison.Ordinal)
            || msg.Contains("504", StringComparison.Ordinal);
    }

    /// <summary>
    /// Creates a new slot type entity in SMAPI.
    /// Step 1 of the 3-step slot type process: create the entity, then create a version.
    /// </summary>
    /// <param name="accessToken">The SMAPI access token.</param>
    /// <param name="vendorId">The vendor ID for the skill owner.</param>
    /// <param name="slotTypeName">The name for the new slot type (e.g. "JELLYFIN_ARTIST").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created slot type ID.</returns>
    public async Task<string> CreateSlotTypeAsync(
        string accessToken,
        string vendorId,
        string slotTypeName,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Creating slot type entity '{SlotTypeName}'", slotTypeName);

        var client = _httpClientFactory.CreateClient("AlexaSkill");

        var body = new
        {
            vendorId,
            slotType = new
            {
                name = slotTypeName,
                description = $"Dynamic slot type for {slotTypeName}"
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{SmapiEndpoint}/v1/skills/api/custom/interactionModel/slotTypes");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(body, options: JsonOptions);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        string slotTypeId = doc.RootElement.GetProperty("slotType").GetProperty("id").GetString()
            ?? throw new InvalidOperationException($"Slot type creation response missing slotType.id. Response: {json}");

        _logger.LogInformation("Slot type '{SlotTypeName}' created with ID {SlotTypeId}", slotTypeName, slotTypeId);
        return slotTypeId;
    }

    /// <summary>
    /// Creates a new version of a slot type backed by a catalog.
    /// Step 2 of the 3-step slot type process: the version binds the slot type to catalog values.
    /// </summary>
    /// <param name="accessToken">The SMAPI access token.</param>
    /// <param name="slotTypeId">The slot type ID (from CreateSlotTypeAsync or GetSlotTypeAsync).</param>
    /// <param name="catalogId">The catalog ID that supplies values.</param>
    /// <param name="catalogVersion">The catalog version to reference.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task CreateSlotTypeVersionAsync(
        string accessToken,
        string slotTypeId,
        string catalogId,
        string catalogVersion,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Creating slot type version for {SlotTypeId} referencing catalog {CatalogId} version {Version}", slotTypeId, catalogId, catalogVersion);

        var client = _httpClientFactory.CreateClient("AlexaSkill");

        var body = new
        {
            slotType = new
            {
                definition = new
                {
                    valueSupplier = new
                    {
                        type = "CatalogValueSupplier",
                        valueCatalog = new { catalogId, version = catalogVersion }
                    }
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{SmapiEndpoint}/v1/skills/api/custom/interactionModel/slotTypes/{slotTypeId}/versions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(body, options: JsonOptions);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        // 202 Accepted with Location header for polling
        Uri? locationUri = response.Headers.Location;
        if (locationUri == null)
        {
            _logger.LogWarning("Slot type version creation returned no Location header, assuming success");
            return;
        }

        locationUri = ResolveLocationUri(locationUri);

        await PollSmapiOperationAsync(
            accessToken, client, locationUri, "Slot type version", cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Slot type version for {SlotTypeId} created successfully", slotTypeId);
    }

    /// <summary>
    /// Gets an existing slot type by name, returning its slotTypeId.
    /// Used when the slot type already exists (409 conflict on create).
    /// </summary>
    /// <param name="accessToken">The SMAPI access token.</param>
    /// <param name="vendorId">The vendor ID.</param>
    /// <param name="slotTypeName">The slot type name to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The slot type ID.</returns>
    public async Task<string> GetSlotTypeIdAsync(
        string accessToken,
        string vendorId,
        string slotTypeName,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Looking up slot type '{SlotTypeName}' via list endpoint", slotTypeName);

        var client = _httpClientFactory.CreateClient("AlexaSkill");

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{SmapiEndpoint}/v1/skills/api/custom/interactionModel/slotTypes?vendorId={Uri.EscapeDataString(vendorId)}&maxResults=50");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("slotTypes", out var slotTypes))
        {
            throw new InvalidOperationException($"Slot type list response missing 'slotTypes'. Response: {json}");
        }

        foreach (var st in slotTypes.EnumerateArray())
        {
            string? name = st.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            if (name == slotTypeName)
            {
                string slotTypeId = st.GetProperty("id").GetString()
                    ?? throw new InvalidOperationException($"Slot type entry missing id. Response: {json}");

                _logger.LogInformation("Found slot type '{SlotTypeName}' with ID {SlotTypeId}", slotTypeName, slotTypeId);
                return slotTypeId;
            }
        }

        throw new InvalidOperationException($"Slot type '{slotTypeName}' not found in vendor's slot types. Response: {json}");
    }

    /// <summary>
    /// Creates a slot type with catalog-backed values, or updates it if it already exists.
    /// Implements the full 3-step SMAPI process: create entity -> create version with CatalogValueSupplier.
    /// </summary>
    /// <param name="accessToken">The SMAPI access token.</param>
    /// <param name="vendorId">The vendor ID for the skill owner.</param>
    /// <param name="slotTypeName">The name for the slot type (e.g. "JELLYFIN_ARTIST").</param>
    /// <param name="catalogId">The catalog ID that supplies values.</param>
    /// <param name="catalogVersion">The catalog version to reference.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task CreateOrUpdateSlotTypeAsync(
        string accessToken,
        string vendorId,
        string slotTypeName,
        string catalogId,
        string catalogVersion,
        CancellationToken cancellationToken)
    {
        string slotTypeId;

        try
        {
            slotTypeId = await CreateSlotTypeAsync(accessToken, vendorId, slotTypeName, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            _logger.LogInformation("Slot type '{SlotTypeName}' already exists, looking up ID", slotTypeName);
            slotTypeId = await GetSlotTypeIdAsync(accessToken, vendorId, slotTypeName, cancellationToken)
                .ConfigureAwait(false);
        }

        await CreateSlotTypeVersionAsync(accessToken, slotTypeId, catalogId, catalogVersion, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Updates the interaction model to reference the artist and album catalogs.
    /// Uses GET-modify-PUT: fetches the current model, injects catalog-backed
    /// slot type definitions, and pushes the modified model back.
    /// This replaces the broken POST /update incremental endpoint.
    /// JF-495: before the GET, waits for any in-flight build of this locale's model
    /// to settle (so the GET cannot capture pre-build, stale content while another
    /// deploy's build is still queued); after the PUT, waits for the build (polling
    /// the update request when its Location is one, or tracking the skill status
    /// otherwise, JF-497) and runs a canary GET-back comparing the live
    /// intent/sample counts with what was submitted. Both measures make the
    /// 2026-09-05 silent-stale-regression class either impossible (GET race) or
    /// loud (canary mismatch ERROR).
    /// </summary>
    /// <param name="accessToken">The SMAPI access token.</param>
    /// <param name="skillId">The skill ID whose model should be updated.</param>
    /// <param name="stage">The skill stage (e.g. "development").</param>
    /// <param name="locale">The locale to update (e.g. "it-IT").</param>
    /// <param name="artistCatalogId">The artist catalog ID (may be null).</param>
    /// <param name="albumCatalogId">The album catalog ID (may be null).</param>
    /// <param name="seriesCatalogId">The series catalog ID (may be null).</param>
    /// <param name="audiobookCatalogId">The audiobook catalog ID (may be null, JF-823).</param>
    /// <param name="movieCatalogId">The movie catalog ID (may be null, JF-874).</param>
    /// <param name="artistCatalogVersion">The artist catalog version (may be null).</param>
    /// <param name="albumCatalogVersion">The album catalog version (may be null).</param>
    /// <param name="seriesCatalogVersion">The series catalog version (may be null).</param>
    /// <param name="audiobookCatalogVersion">The audiobook catalog version (may be null, JF-823).</param>
    /// <param name="movieCatalogVersion">The movie catalog version (may be null, JF-874).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The build outcome and canary counts for the status ledger.</returns>
    public async Task<CatalogModelUpdateResult> UpdateInteractionModelAsync(
        string accessToken,
        string skillId,
        string stage,
        string locale,
        string? artistCatalogId,
        string? albumCatalogId,
        string? seriesCatalogId,
        string? audiobookCatalogId,
        string? movieCatalogId,
        string? artistCatalogVersion,
        string? albumCatalogVersion,
        string? seriesCatalogVersion,
        string? audiobookCatalogVersion,
        string? movieCatalogVersion,
        // JF-946: trailing-optional so the per-type parameter set stays the
        // JF-706 loud edge for the PRODUCTION call sites (which forward the
        // song pair explicitly) without churning the test call sites that
        // assert pre-song shapes with all-null song semantics.
        string? songCatalogId = null,
        string? songCatalogVersion = null,
        // JF-951: the serialized byte length of each type's catalog payload, in
        // memory at the sync's call site. Wired types (a non-null catalog id)
        // contribute their bytes to the pre-flight effective-size estimate; a
        // null dictionary disables the guard (older callers, tests asserting
        // pre-guard shapes). Trailing-optional like the song pair above.
        IReadOnlyDictionary<CatalogType, long>? payloadBytesByType = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(artistCatalogId) && string.IsNullOrEmpty(albumCatalogId) && string.IsNullOrEmpty(seriesCatalogId) && string.IsNullOrEmpty(audiobookCatalogId) && string.IsNullOrEmpty(movieCatalogId) && string.IsNullOrEmpty(songCatalogId))
        {
            _logger.LogInformation("No catalogs to inject into interaction model, skipping update");
            return new CatalogModelUpdateResult("Skipped", null, 0, 0);
        }

        // JF-543 choke point: Amazon's full build fails for catalog-wired models in
        // some locales (see CatalogWiringUnsupportedLocales). LibrarySyncService
        // filters these locales before the per-leg uploads; this guard is the second
        // layer so a future caller cannot re-introduce wiring with the suite green.
        if (!IsCatalogWiringSupported(locale))
        {
            _logger.LogWarning(
                "Refusing to catalog-wire {Locale}: Amazon's full build fails for catalog-backed models there (JF-543); the locale keeps its embedded model",
                locale);
            return new CatalogModelUpdateResult("Skipped", null, 0, 0);
        }

        var client = _httpClientFactory.CreateClient("AlexaSkill");
        string modelUrl = LocaleModelUrl(skillId, stage, locale);

        // JF-495: serialize against concurrently-pending builds BEFORE the GET
        // (the guard lives inside the shared fetch helper below).
        string modelJson = await FetchLiveModelAsync(
            accessToken, client, modelUrl, skillId, locale, cancellationToken).ConfigureAwait(false);

        string modifiedJson = InjectCatalogReferences(modelJson, artistCatalogId, albumCatalogId, seriesCatalogId, audiobookCatalogId, movieCatalogId, artistCatalogVersion, albumCatalogVersion, seriesCatalogVersion, audiobookCatalogVersion, movieCatalogVersion, _logger, songCatalogId, songCatalogVersion);

        // JF-951: the model is UTF-8 encoded ONCE. The guard below counts these
        // exact bytes and the PUT sends the same array, so the guard's model
        // figure is provably the wire bytes (identical content to the former
        // StringContent re-encode).
        byte[] modelUtf8 = Encoding.UTF8.GetBytes(modifiedJson);

        // The PUT audit counts, computed once for the audit line and the
        // SkippedOversize outcome.
        var (putIntents, putSamples) = InteractionModelPutAudit.CountFromJson(modifiedJson);

        // JF-951 pre-flight size guard: Amazon's per-locale interaction-model cap is
        // EFFECTIVE (the serialized model plus the bytes of every catalog referenced
        // by it), and an over-cap PUT fails ExceededMaximumModelSize on Amazon's
        // servers AFTER the build, leaving the locale's live model FAILED until
        // manually restored (the 2026-11-23 live findings: a 9,458-title library
        // with phonetic song synonyms measured it-IT 2.0MB / de-DE 1.6MB / ja-JP
        // 2.5MB). The payload bytes are in memory at the sync's call site, the
        // post-injection model bytes are in memory here, so the check runs LOCALLY
        // before the PUT: on exceed, skip the PUT (the previous good model stays
        // live), report a SkippedOversize outcome the sync surfaces in its
        // SyncResult, and log the numbers and the levers the user controls. The
        // wired set is read from the POST-INJECTION MODEL ITSELF (the valueSupplier
        // carriers, the IsCatalogWired predicate): exactly what Amazon will count,
        // so a mapping the JF-952 declare check skipped contributes no payload
        // bytes, and a wired block is counted only when this run knows its bytes.
        // KNOWN RESIDUAL GAP (absorbed by the safety margin): a STALE wired block
        // of a type that minted no version this run (zero items, or a frozen type)
        // keeps Amazon counting its PREVIOUS run's payload, which no local byte
        // count can know; the worst case degrades to the pre-guard behavior (a
        // server-side build failure on that locale), never to a wrong skip.
        if (payloadBytesByType != null)
        {
            List<long> wiredPayloadBytes = CollectWiredPayloadBytes(modifiedJson, payloadBytesByType);
            long modelBytes = modelUtf8.Length;
            long thresholdBytes = EffectiveModelSizeSafetyThresholdBytes;
            long effectiveBytes = EstimateEffectiveModelSizeBytes(modelBytes, wiredPayloadBytes);
            // The ONE sum lives in the estimator; the log's payload figure is
            // derived from it, never re-summed inline (the estimator's doc
            // contract).
            long payloadBytes = effectiveBytes - modelBytes;
            if (effectiveBytes > thresholdBytes)
            {
                _logger.LogError(
                    "Interaction model update SKIPPED for skill {SkillId} locale {Locale} (JF-951 pre-flight size guard): effective size {EffectiveBytes} bytes exceeds the {ThresholdBytes}-byte safety threshold (Amazon's per-model cap is {CapBytes} bytes; model JSON {ModelBytes} bytes + wired catalog payloads {PayloadBytes} bytes). The locale keeps its previous good model. {Levers}",
                    skillId, locale, effectiveBytes, thresholdBytes, ModelSizeCapBytes, modelBytes, payloadBytes, OversizeGuardLeversText);
                return new CatalogModelUpdateResult(BuildStatusSkippedOversize, null, putIntents, putSamples, CanaryError: $"skipped by the JF-951 pre-flight size guard: effective {effectiveBytes} bytes > threshold {thresholdBytes} (model {modelBytes} + payloads {payloadBytes})");
            }

            _logger.LogDebug(
                "Interaction model size for skill {SkillId} locale {Locale}: effective {EffectiveBytes} bytes (model {ModelBytes} + payloads {PayloadBytes}; threshold {ThresholdBytes})",
                skillId, locale, effectiveBytes, modelBytes, payloadBytes, thresholdBytes);
        }

        // JF-495: greppable audit line before the PUT, AFTER the JF-951 guard:
        // the audit reconciles PUTs against deployments, and a size-skipped
        // locale sends no PUT, so it must not mint an audit entry.
        InteractionModelPutAudit.LogModelPut(
            _logger,
            InteractionModelPutAudit.SourceGetModifyPut,
            locale,
            skillId,
            putIntents,
            putSamples);

        _logger.LogInformation("Pushing updated interaction model for skill {SkillId} locale {Locale}", skillId, locale);

        using var putRequest = new HttpRequestMessage(HttpMethod.Put, modelUrl);
        putRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        putRequest.Content = new ByteArrayContent(modelUtf8);
        putRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using var putResponse = await client.SendAsync(putRequest, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(putResponse, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Interaction model update submitted for skill {SkillId} locale {Locale}", skillId, locale);

        // The poll dispatch (JF-495/JF-497) is shared with the JF-949 strip path:
        // ONE copy of the stale-build semantics.
        string buildStatus = await AwaitModelPutBuildAsync(
            accessToken, client, putResponse, skillId, locale, "Interaction model update", cancellationToken).ConfigureAwait(false);

        // Canary: only meaningful once the build reports SUCCEEDED; a GET before the
        // build settles would race and produce a false mismatch.
        if (buildStatus == "SUCCEEDED")
        {
            return await VerifyPutCanaryAsync(accessToken, client, modelUrl, locale, skillId, putIntents, putSamples, buildStatus, cancellationToken).ConfigureAwait(false);
        }

        return new CatalogModelUpdateResult(buildStatus, null, putIntents, putSamples);
    }

    /// <summary>
    /// The shared GET half of the GET-modify-PUT model paths (JF-495, extracted so
    /// the injection path and the JF-949 wiring-strip path share ONE copy):
    /// waits for any in-flight build of this locale to settle, then fetches the
    /// live model JSON from the caller-computed URL (ONE derivation of
    /// LocaleModelUrl per flow, so the GET and any later PUT/canary cannot drift).
    /// Waiting BEFORE the GET matters: a rebuild (or any other writer) that
    /// submitted a model moments ago may still be IN_PROGRESS, the GET would
    /// return the last SUCCEEDED (stale) content, and PUTting that content back
    /// redeploys yesterday's model (the 2026-09-05 silent-stale regression class).
    /// </summary>
    /// <param name="accessToken">The SMAPI access token.</param>
    /// <param name="client">The HTTP client.</param>
    /// <param name="modelUrl">The locale model URL the flow already computed.</param>
    /// <param name="skillId">The skill ID.</param>
    /// <param name="locale">The locale.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The live interaction model JSON.</returns>
    private async Task<string> FetchLiveModelAsync(
        string accessToken,
        HttpClient client,
        string modelUrl,
        string skillId,
        string locale,
        CancellationToken cancellationToken)
    {
        await WaitForLocaleBuildToSettleAsync(
            accessToken, client, skillId, locale, _logger, cancellationToken,
            PollDelayMs).ConfigureAwait(false);

        return await GetModelJsonAsync(accessToken, client, modelUrl, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The bare authorized model GET (no build-settle wait): the settle-half
    /// lives in <see cref="FetchLiveModelAsync"/>, and the JF-949 strip canary
    /// deliberately calls THIS one. The poll dispatch already confirmed the
    /// build SUCCEEDED before the canary runs, so re-running the settle wait
    /// would double the budget burn for no protection.
    /// </summary>
    /// <param name="accessToken">The SMAPI access token.</param>
    /// <param name="client">The HTTP client.</param>
    /// <param name="modelUrl">The locale model URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The model JSON.</returns>
    private async Task<string> GetModelJsonAsync(
        string accessToken,
        HttpClient client,
        string modelUrl,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Fetching interaction model for skill locale model {ModelUrl}", modelUrl);

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, modelUrl);
        getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var getResponse = await client.SendAsync(getRequest, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(getResponse, cancellationToken).ConfigureAwait(false);

        return await getResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The shared PUT-follow-through of the model-update paths (JF-495/JF-497, ONE
    /// copy for the injection path and the JF-949 wiring-strip path). The PUT is
    /// asynchronous on SMAPI (202 + a Location header); wait for the build so this
    /// update cannot complete later and clobber a fresher model unobserved. The
    /// Location is not always a pollable update-request URL (observed live
    /// 2026-09-06: a skill-status URL whose response nests the per-locale status
    /// under interactionModel.{locale}; ExtractPollStatus can never parse it, so
    /// the poll once burned its full budget on every locale, landed TIMEOUT, and
    /// the canary never fired). Dispatch on the Location's shape: poll only genuine
    /// update-request URLs, and fall back to the skill-status tracker immediately
    /// otherwise (no budget burn on an unparseable URL). The HttpRequestException
    /// arm keeps the JF-495 review distinction: the PUT itself succeeded, only the
    /// OBSERVATION failed, so the outcome is UNVERIFIED, not failed.
    /// </summary>
    /// <param name="accessToken">The SMAPI access token.</param>
    /// <param name="client">The HTTP client.</param>
    /// <param name="putResponse">The already-verified PUT response.</param>
    /// <param name="skillId">The skill ID.</param>
    /// <param name="locale">The locale.</param>
    /// <param name="opLabel">Human-readable operation name for the poll and the log lines.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The build status: "SUCCEEDED", "TIMEOUT", "UNVERIFIED", or whatever the skill-status tracker reports.</returns>
    private async Task<string> AwaitModelPutBuildAsync(
        string accessToken,
        HttpClient client,
        HttpResponseMessage putResponse,
        string skillId,
        string locale,
        string opLabel,
        CancellationToken cancellationToken)
    {
        Uri? rawLocation = putResponse.Headers.Location;
        Uri? pollLocation = rawLocation == null ? null : ResolveLocationUri(rawLocation);
        if (pollLocation != null && IsUpdateRequestLocation(pollLocation))
        {
            try
            {
                await PollSmapiOperationAsync(
                    accessToken, client, pollLocation, opLabel, cancellationToken).ConfigureAwait(false);
                return "SUCCEEDED";
            }
            catch (TimeoutException ex)
            {
                _logger.LogWarning(ex,
                    "{OpLabel} build did not settle within the poll budget for skill {SkillId} locale {Locale}; the live model state is unverified (JF-495)",
                    opLabel, skillId, locale);
                return "TIMEOUT";
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex,
                    "{OpLabel} poll failed transiently for skill {SkillId} locale {Locale}; the build outcome is unverified, not failed (JF-495)",
                    opLabel, skillId, locale);
                return "UNVERIFIED";
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex,
                    "{OpLabel} build FAILED for skill {SkillId} locale {Locale} (JF-495)",
                    opLabel, skillId, locale);
                return "FAILED";
            }
        }

        if (pollLocation == null)
        {
            _logger.LogWarning(
                "{OpLabel} PUT for skill {SkillId} locale {Locale} returned no Location header; tracking the build via the skill-status endpoint instead (JF-495)",
                opLabel, skillId, locale);
        }
        else
        {
            _logger.LogInformation(
                "{OpLabel} PUT for skill {SkillId} locale {Locale} returned a non-update-request Location ({Location}); tracking the build via the skill-status endpoint instead (JF-497)",
                opLabel, skillId, locale, pollLocation);
        }

        return await WaitForModelBuildOutcomeViaSkillStatusAsync(
            accessToken, client, skillId, locale, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes the catalog wiring from one slot type in a locale's live interaction
    /// model, restoring the given static replacement block (JF-949: the song-catalog
    /// opt-out teardown). Same GET-modify-PUT shape as
    /// <see cref="UpdateInteractionModelAsync"/> (build settle before the GET, build
    /// poll after the PUT) so the 2026-09-05 silent-stale class stays impossible
    /// here too. Idempotent: when the live model carries no WIRED block of this type
    /// (absent type, or a static values block), nothing is PUT and a "Skipped"
    /// result returns. The JF-543 guard applies: wiring can only exist in supported
    /// locales, so an unsupported locale short-circuits to "Skipped".
    /// A build that does not settle SUCCEEDED reports that status (the caller treats
    /// anything but "SUCCEEDED"/"Skipped" as a failed strip and keeps its catalog).
    /// After a SUCCEEDED build the live model is GET-ed back once and the named type
    /// verified unwired: a stale GET-back (the silent-stale class) would otherwise
    /// leave the model referencing a catalog the caller is about to delete, so the
    /// verification failure surfaces as an "UNVERIFIED" result with the mismatch in
    /// <see cref="CatalogModelUpdateResult.CanaryError"/>.
    /// </summary>
    /// <param name="accessToken">The SMAPI access token.</param>
    /// <param name="skillId">The skill ID whose model should be updated.</param>
    /// <param name="stage">The skill stage (e.g. "development").</param>
    /// <param name="locale">The locale to update (e.g. "it-IT").</param>
    /// <param name="slotTypeName">The slot type whose catalog wiring to remove.</param>
    /// <param name="replacementTypeJson">The static type block JSON to restore in its place (read from the locale's own embedded model), or null to remove the wired block outright (a locale whose embedded model never carried the type; the JF-952 inert-wiring removal shape).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The build outcome for the status ledger.</returns>
    public async Task<CatalogModelUpdateResult> StripCatalogWiringAsync(
        string accessToken,
        string skillId,
        string stage,
        string locale,
        string slotTypeName,
        string? replacementTypeJson,
        CancellationToken cancellationToken)
    {
        if (!IsCatalogWiringSupported(locale))
        {
            _logger.LogInformation(
                "Skipping catalog wiring strip for {SlotTypeName} in {Locale}: wiring cannot exist in a JF-543 locale",
                slotTypeName, locale);
            return new CatalogModelUpdateResult("Skipped", null, 0, 0);
        }

        var client = _httpClientFactory.CreateClient("AlexaSkill");
        string modelUrl = LocaleModelUrl(skillId, stage, locale);

        string modelJson = await FetchLiveModelAsync(
            accessToken, client, modelUrl, skillId, locale, cancellationToken).ConfigureAwait(false);

        (string modifiedJson, CatalogStripOutcome outcome) = StripCatalogWiring(modelJson, slotTypeName, replacementTypeJson, _logger);
        if (outcome == CatalogStripOutcome.Malformed)
        {
            // A body we cannot parse (or a replacement block we cannot parse) leaves
            // the wiring state UNKNOWN: the caller must treat this as a failed strip
            // and keep the catalog, never alias it with the genuinely-static case
            // (JF-949 review round; the detail rides CanaryError into the ledger).
            _logger.LogError(
                "Wiring strip for {SlotTypeName} in {Locale} aborted: a model body could not be parsed, so the wiring state is unknown and the catalog must stay (JF-949)",
                slotTypeName, locale);
            return new CatalogModelUpdateResult("FAILED", null, 0, 0, CanaryError: "strip aborted: a model body could not be parsed; wiring state unknown");
        }

        if (outcome == CatalogStripOutcome.Unchanged)
        {
            _logger.LogInformation(
                "No wired {SlotTypeName} block in the live model for skill {SkillId} locale {Locale}; nothing to strip (JF-949 idempotency)",
                slotTypeName, skillId, locale);
            return new CatalogModelUpdateResult("Skipped", null, 0, 0);
        }

        using var putRequest = new HttpRequestMessage(HttpMethod.Put, modelUrl);
        putRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        putRequest.Content = new StringContent(modifiedJson, Encoding.UTF8, "application/json");

        using var putResponse = await client.SendAsync(putRequest, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(putResponse, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Wiring-strip model update submitted for skill {SkillId} locale {Locale}", skillId, locale);

        // The shared poll dispatch (JF-495/JF-497): ONE copy of the stale-build
        // semantics for both model-update paths.
        string buildStatus = await AwaitModelPutBuildAsync(
            accessToken, client, putResponse, skillId, locale, "Wiring strip model update", cancellationToken).ConfigureAwait(false);

        if (buildStatus != "SUCCEEDED")
        {
            return new CatalogModelUpdateResult(buildStatus, null, 0, 0);
        }

        // Strip canary (the JF-495 canary's counterpart; the counts-based canary is
        // meaningless here since the strip never changes intent/sample counts): GET
        // the model back and confirm the named type carries no catalog supplier
        // anymore. A wired GET-back means the build served stale content and the
        // model still references the catalog the caller will delete, so the strip
        // reports UNVERIFIED with the mismatch in CanaryError and the caller keeps
        // the catalog.
        try
        {
            // No settle wait here: AwaitModelPutBuildAsync already confirmed the
            // build SUCCEEDED, so the canary does the bare GET (JF-949 review
            // round: the double settle burned the budget twice for no protection).
            string liveJson = await GetModelJsonAsync(accessToken, client, modelUrl, cancellationToken).ConfigureAwait(false);
            if (IsSlotTypeWired(liveJson, slotTypeName))
            {
                _logger.LogError(
                    "Wiring strip canary MISMATCH for skill {SkillId} locale {Locale}: the live {SlotTypeName} block still carries a catalog supplier after the strip build settled; treating the strip as unverified (JF-949)",
                    skillId, locale, slotTypeName);
                return new CatalogModelUpdateResult("UNVERIFIED", false, 0, 0, CanaryError: "strip canary mismatch: the live model still carries the wired block");
            }

            return new CatalogModelUpdateResult(buildStatus, true, 0, 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Wiring strip canary GET failed for skill {SkillId} locale {Locale}; the build outcome is unverified, not failed (JF-949)",
                skillId, locale);
            return new CatalogModelUpdateResult("UNVERIFIED", null, 0, 0, CanaryError: "strip canary GET failed; the build outcome is unverified");
        }
    }

    /// <summary>
    /// True when the named slot type block in the given model JSON carries a
    /// catalog valueSupplier (the strip canary's stale-detection read). Tolerant:
    /// a malformed body reads as wired-free (the canary then passes; the
    /// malformed-model case fails loudly earlier in the pipeline).
    /// </summary>
    /// <param name="modelJson">The live interaction model JSON envelope.</param>
    /// <param name="slotTypeName">The slot type to inspect.</param>
    /// <returns>True when the block is present and catalog-wired.</returns>
    private static bool IsSlotTypeWired(string modelJson, string slotTypeName)
    {
        try
        {
            var typesArray = JsonNode.Parse(modelJson)?["interactionModel"]?["languageModel"]?["types"] as JsonArray;
            if (typesArray == null)
            {
                return false;
            }

            int index = FindTypeIndex(typesArray, slotTypeName);
            return index >= 0 && IsCatalogWired(typesArray[index]);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The JF-949 strip core's three outcomes (JF-949 review round: a malformed
    /// body must NOT alias the genuinely-static case, because the caller deletes
    /// the SMAPI catalog when every locale strips clean; an unknown wiring state
    /// has to block that delete).
    /// </summary>
    internal enum CatalogStripOutcome
    {
        /// <summary>No WIRED block of the type exists; nothing changed.</summary>
        Unchanged,

        /// <summary>A WIRED block was removed (replaced or deleted outright).</summary>
        Removed,

        /// <summary>A body could not be parsed; the wiring state is UNKNOWN and must block the catalog delete.</summary>
        Malformed
    }

    /// <summary>
    /// Static core of <see cref="StripCatalogWiringAsync"/> (JF-949): replaces the
    /// WIRED block of <paramref name="slotTypeName"/> (a valueSupplier carrier, the
    /// <see cref="IsCatalogWired"/> definition, matching
    /// CatalogWiringGraft.ExtractWiring) with the static replacement block, or with
    /// nothing when the replacement is null (a locale whose embedded model never
    /// carried the type: the JF-952 inert-wiring removal shape). An absent type or
    /// a static values block leaves the model untouched with
    /// <see cref="CatalogStripOutcome.Unchanged"/> (the teardown's idempotent
    /// no-op; a static block is never re-PUT and never overwritten, byte-identical
    /// to the JF-952 keep-static rule). A malformed live body, or a malformed
    /// replacement block, returns <see cref="CatalogStripOutcome.Malformed"/>: the
    /// wiring state is unknown and the caller keeps the catalog (a parse failure
    /// must never read as the static case, JF-949 review round).
    /// </summary>
    /// <param name="modelJson">The live interaction model JSON envelope.</param>
    /// <param name="slotTypeName">The slot type whose wiring to remove.</param>
    /// <param name="replacementTypeJson">The static type block JSON to restore, or null to remove the block.</param>
    /// <param name="logger">Optional logger for the strip diagnostics.</param>
    /// <returns>The (possibly unchanged) model JSON and the strip outcome.</returns>
    internal static (string ModifiedJson, CatalogStripOutcome Outcome) StripCatalogWiring(string modelJson, string slotTypeName, string? replacementTypeJson, ILogger? logger = null)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(modelJson);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex,
                "Wiring strip for {SlotTypeName}: the live model body is malformed; the wiring state is unknown",
                slotTypeName);
            return (modelJson, CatalogStripOutcome.Malformed);
        }

        if (root == null)
        {
            return (modelJson, CatalogStripOutcome.Malformed);
        }

        var typesArray = root["interactionModel"]?["languageModel"]?["types"] as JsonArray;
        if (typesArray == null)
        {
            return (modelJson, CatalogStripOutcome.Malformed);
        }

        int existingIndex = FindTypeIndex(typesArray, slotTypeName);
        if (existingIndex < 0 || !IsCatalogWired(typesArray[existingIndex]))
        {
            return (modelJson, CatalogStripOutcome.Unchanged);
        }

        JsonNode? replacement = null;
        if (replacementTypeJson != null)
        {
            try
            {
                replacement = JsonNode.Parse(replacementTypeJson);
            }
            catch (JsonException ex)
            {
                logger?.LogWarning(ex,
                    "Wiring strip for {SlotTypeName}: the replacement block failed to parse; the wiring state is unknown",
                    slotTypeName);
                return (modelJson, CatalogStripOutcome.Malformed);
            }
        }

        if (replacement != null)
        {
            logger?.LogInformation(
                "Stripping catalog wiring from {SlotTypeName} (index {Index}); restoring the static seed block (JF-949)",
                slotTypeName, existingIndex);
            typesArray[existingIndex] = replacement;
        }
        else
        {
            logger?.LogInformation(
                "Removing wired {SlotTypeName} block (index {Index}): the locale carries no embedded static block for the type, so the wiring goes outright (JF-949, the JF-952 removal shape)",
                slotTypeName, existingIndex);
            typesArray.RemoveAt(existingIndex);
        }

        return (root.ToJsonString(), CatalogStripOutcome.Removed);
    }

    /// <summary>
    /// Post-deploy canary (JF-495): GET the model back and compare the live intent
    /// and sample counts with what was PUT. A mismatch is logged as an ERROR naming
    /// both counts; there is no auto-rollback by design.
    /// </summary>
    private async Task<CatalogModelUpdateResult> VerifyPutCanaryAsync(
        string accessToken,
        HttpClient client,
        string modelUrl,
        string locale,
        string skillId,
        int putIntents,
        int putSamples,
        string buildStatus,
        CancellationToken cancellationToken)
    {
        try
        {
            using var canaryRequest = CreateAuthorizedGet(modelUrl, accessToken);

            using var canaryResponse = await client.SendAsync(canaryRequest, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(canaryResponse, cancellationToken).ConfigureAwait(false);

            string liveJson = await canaryResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var (liveIntents, liveSamples) = InteractionModelPutAudit.CountFromJson(liveJson);

            if (liveIntents == putIntents && liveSamples == putSamples)
            {
                InteractionModelPutAudit.LogCanaryOk(_logger, InteractionModelPutAudit.SourceGetModifyPut, locale, liveIntents, liveSamples);
                return new CatalogModelUpdateResult(buildStatus, true, putIntents, putSamples, liveIntents, liveSamples);
            }

            InteractionModelPutAudit.LogCanaryMismatch(
                _logger, InteractionModelPutAudit.SourceGetModifyPut, locale, skillId, putIntents, putSamples, liveIntents, liveSamples);
            return new CatalogModelUpdateResult(
                buildStatus,
                false,
                putIntents,
                putSamples,
                liveIntents,
                liveSamples,
                $"canary mismatch: submitted {putIntents} intents/{putSamples} samples but live model reports {liveIntents}/{liveSamples}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Model canary GET failed for skill {SkillId} locale {Locale}; live counts unverified (non-fatal)",
                skillId, locale);
            return new CatalogModelUpdateResult(buildStatus, null, putIntents, putSamples);
        }
    }

    /// <summary>
    /// Builds the stage-scoped locale interaction-model URL. Single owner of the
    /// string so the sync leg, the raw PUT, and the graft GET cannot drift apart
    /// (same rule as <see cref="SkillStatusUrl"/>, JF-497).
    /// </summary>
    /// <param name="skillId">The skill id.</param>
    /// <param name="stage">The stage, e.g. "development".</param>
    /// <param name="locale">The locale.</param>
    /// <returns>The absolute URL.</returns>
    internal static string LocaleModelUrl(string skillId, string stage, string locale)
        => $"{SmapiEndpoint}/v1/skills/{skillId}/stages/{stage}/interactionModel/locales/{locale}";

    /// <summary>
    /// Builds a bearer-authorized GET request for a SMAPI URL.
    /// </summary>
    internal static HttpRequestMessage CreateAuthorizedGet(string url, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    /// <summary>
    /// Builds the skill-status URL used by both the pre-GET settle wait and the
    /// fallback build tracker. Deliberately NOT stage-scoped:
    /// /v1/skills/{id}/stages/{stage}/status answered 404 for every locale of
    /// the 2026-09-06 startup sync, while /v1/skills/{id}/status works (the
    /// same URL Alexa.NET.Management's Skills.Status calls, and the shape the
    /// model PUT's Location header carries). Single owner of the string so the
    /// two callers cannot drift apart (JF-497).
    /// </summary>
    /// <param name="skillId">The skill whose status is read.</param>
    /// <returns>The absolute skill-status URL.</returns>
    private static string SkillStatusUrl(string skillId) => $"{SmapiEndpoint}/v1/skills/{skillId}/status";

    /// <summary>
    /// Reads the locale's interaction-model build state from the skill-status
    /// endpoint. Returns null when the status cannot be read (endpoint failure)
    /// or the locale has no reported status; failures are logged as warnings.
    /// </summary>
    internal static async Task<string?> TryGetLocaleModelStatusAsync(
        string accessToken,
        HttpClient client,
        string skillId,
        string locale,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateAuthorizedGet(SkillStatusUrl(skillId), accessToken);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // JF-502: the skill-status URL can answer 404 (observed live for every
            // locale of the 2026-09-06 startup sync, one ERR per locale). "No status
            // to read" is the same best-effort outcome as the catch below (return
            // null, skip the wait), so handle the 404 quietly instead of letting
            // EnsureSuccessAsync log an ERR for it.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                logger.LogDebug(
                    "Skill status for skill {SkillId} locale {Locale} returned 404 (no readable status); treating as no reported status (JF-502)",
                    skillId,
                    locale);
                return null;
            }

            await EnsureSuccessSharedAsync(response, logger, cancellationToken).ConfigureAwait(false);

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ExtractLocaleModelStatus(json, locale);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TimeoutException)
        {
            logger.LogWarning(ex,
                "Could not read skill status for skill {SkillId} locale {Locale} (JF-495)",
                skillId, locale);
            return null;
        }
    }

    /// <summary>
    /// Waits until the locale's interaction model build is no longer IN_PROGRESS
    /// (JF-495 GET-race guard). Best-effort: when the status cannot be read the
    /// wait is skipped rather than failing the sync.
    /// </summary>
    /// <param name="initialDelayMs">Backoff seed; the sole in-class caller passes
    /// <see cref="PollDelayMs"/> (the seam-resolved poll delay) so the suites can
    /// skip the pacing against instant fakes. The out-of-class caller
    /// (SmapiManagement.GetLiveModelJsonAsync) keeps the production default.</param>
    internal static async Task WaitForLocaleBuildToSettleAsync(
        string accessToken,
        HttpClient client,
        string skillId,
        string locale,
        ILogger logger,
        CancellationToken cancellationToken,
        int initialDelayMs = SmapiPollInitialDelayMs)
    {
        int delay = initialDelayMs;
        for (int i = 0; i < 30; i++)
        {
            string? state = await TryGetLocaleModelStatusAsync(
                accessToken, client, skillId, locale, logger, cancellationToken).ConfigureAwait(false);
            if (state is null || state != "IN_PROGRESS")
            {
                return;
            }

            logger.LogInformation(
                "Waiting for pending interaction model build ({State}) to settle before GET-modify-PUT for skill {SkillId} locale {Locale} (JF-495)",
                state, skillId, locale);

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = Math.Min(delay * 2, 2000);
        }

        logger.LogWarning(
            "Interaction model build for skill {SkillId} locale {Locale} is still IN_PROGRESS after the settle budget; proceeding with GET-modify-PUT anyway (JF-495)",
            skillId, locale);
    }

    /// <summary>
    /// Fallback build tracker for model PUTs whose response carries no Location
    /// header (JF-495), or whose Location is not a pollable update-request URL
    /// (observed live: a skill-status URL, JF-497): polls the skill-status
    /// endpoint until the locale's model build reaches a terminal state. When
    /// the first observation is already terminal it may reflect the PREVIOUS
    /// build; that ambiguity is logged and the canary still verifies the live
    /// counts afterwards.
    /// </summary>
    /// <returns>"SUCCEEDED", "FAILED", or "TIMEOUT" when the budget is exhausted.</returns>
    private async Task<string> WaitForModelBuildOutcomeViaSkillStatusAsync(
        string accessToken,
        HttpClient client,
        string skillId,
        string locale,
        CancellationToken cancellationToken)
    {
        // Give SMAPI a moment to flip the locale's status to IN_PROGRESS before
        // the first poll, so a stale terminal status from the previous build is
        // not mistaken for this PUT's outcome.
        await Task.Delay(PollDelayMs, cancellationToken).ConfigureAwait(false);

        int delay = PollDelayMs;
        // 120 iterations with the 2s backoff cap is a ~200-230s budget: a full sync
        // queues FIVE serialized SMAPI builds per locale (4 catalog versions + the
        // model build since JF-823's fourth synced type; 3 + 1 before), so a
        // locale's model build legitimately settles deep in the
        // queue; the previous 30-iteration budget (~57s) landed TIMEOUT on every
        // locale and the canary never fired (live evidence 2026-09-06: 80-85s per
        // locale at 12 locales). The budget then held at 90 iterations
        // (~150-170s) for FOUR builds per locale at 17 locales / 68 queued builds
        // after JF-513 (live 2026-09-12: 79-132s per locale, zero TIMEOUTs across
        // the full sync), so it is sized on the serialized-queue depth, not the
        // locale count; JF-823's fifth per-locale build scales it ~25% to keep
        // the same settle margin (JF-823 gate-marker finding 1).
        for (int i = 0; i < 120; i++)
        {
            string? state = await TryGetLocaleModelStatusAsync(
                accessToken, client, skillId, locale, _logger, cancellationToken).ConfigureAwait(false);

            if (state == "SUCCEEDED" || state == "FAILED")
            {
                if (i == 0)
                {
                    _logger.LogInformation(
                        "Locale {Locale} model status was already {State} on the first fallback poll; it may reflect the previous build (JF-495)",
                        locale, state);
                }

                return state;
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = Math.Min(delay * 2, 2000);
        }

        return "TIMEOUT";
    }

    /// <summary>
    /// Extracts the build status of one locale from a raw skill-status response body
    /// (the JSON shape served by GET /v1/skills/[id]/status:
    /// "interactionModel" maps each locale to its "lastUpdateRequest" object whose
    /// "status" is the build state, e.g. SUCCEEDED or IN_PROGRESS).
    /// Returns null when the locale has no reported status.
    /// </summary>
    internal static string? ExtractLocaleModelStatus(string statusJson, string locale)
    {
        try
        {
            using var doc = JsonDocument.Parse(statusJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("interactionModel", out var im)
                || im.ValueKind != JsonValueKind.Object
                || !im.TryGetProperty(locale, out var localeNode)
                || localeNode.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            JsonElement container = localeNode.TryGetProperty("lastUpdateRequest", out var lur)
                && lur.ValueKind == JsonValueKind.Object
                    ? lur
                    : localeNode;

            return container.TryGetProperty("status", out var s) ? s.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// JF-951: Amazon's per-locale interaction-model size cap, EFFECTIVE form
    /// (the serialized model JSON plus the bytes of every catalog wired into
    /// it). Amazon documents 1.5MB; the constant carries the SI reading
    /// (1,500,000 bytes), the same number the 2026-11-23 live
    /// ExceededMaximumModelSize findings were measured against.
    /// </summary>
    internal const long ModelSizeCapBytes = 1_500_000;

    /// <summary>
    /// JF-951: the BuildStatus value the pre-flight size guard returns on a
    /// skipped PUT. The ONE literal; the sync's consumer compares against this
    /// constant, never its own copy.
    /// </summary>
    internal const string BuildStatusSkippedOversize = "SkippedOversize";

    /// <summary>
    /// JF-951: the ONE copy of the lever text every size-guard log line names,
    /// so the two writers (this class's guard line and the sync's per-user
    /// warning) cannot drift. The guard can fire on ANY wired type's payload
    /// (artist and album catalogs count too), so the wording names the general
    /// lever first and the song-specific one as the example.
    /// </summary>
    internal const string OversizeGuardLeversText = "Levers: sync fewer titles (for the song catalog, disabling phonetic synonyms via CatalogSongSynonymsEnabled removes most of the added bytes)";

    /// <summary>
    /// JF-951: the pre-flight guard threshold, the cap minus a safety margin
    /// (the 1.45MB line the task pins). The margin absorbs the estimator's
    /// known gaps (SMAPI's own serialization of the injected reference block,
    /// catalog-side metadata Amazon may count beyond the raw payload bytes),
    /// so a payload that clears the threshold locally is comfortably inside
    /// Amazon's effective cap. The ONE threshold the guard compares against;
    /// the <see cref="ModelSizeSafetyThresholdBytesForTest"/> seam only
    /// overrides the comparison in tests, never this definition.
    /// </summary>
    internal const long ModelSizeSafetyThresholdBytes = 1_450_000;

    /// <summary>
    /// Test seam (null in production, the PollDelayMsForTest pattern): overrides
    /// the guard threshold so the full-sync pins can drive a leg past the
    /// pre-flight guard without materializing a 1.45MB payload. Zero or negative
    /// forces the skip for every wired locale.
    /// </summary>
    internal long? ModelSizeSafetyThresholdBytesForTest { get; set; }

    /// <summary>
    /// JF-951: the ONE estimator of the guard (pure, unit-pinned over known
    /// sizes): the effective per-model size is the serialized model JSON bytes
    /// plus the sum of the serialized bytes of every catalog payload wired into
    /// that model. Nothing else may re-derive the sum inline.
    /// </summary>
    /// <param name="modelBytes">The serialized (post-injection) model JSON byte count.</param>
    /// <param name="wiredPayloadBytes">The serialized byte counts of the wired catalog payloads.</param>
    /// <returns>The effective size in bytes.</returns>
    internal static long EstimateEffectiveModelSizeBytes(long modelBytes, IReadOnlyCollection<long> wiredPayloadBytes) =>
        modelBytes + wiredPayloadBytes.Sum();

    /// <summary>
    /// JF-951: the payload byte counts of the catalog blocks ACTUALLY WIRED into
    /// the post-injection model (each type block carrying a valueSupplier, the
    /// <see cref="IsCatalogWired"/> definition). Reading the model itself, not
    /// the caller's forwarded ids, is load-bearing: the JF-952 declare check can
    /// skip a mapping whose id was forwarded (a non-declaring locale never
    /// carries the block, so Amazon never counts its payload), and a wired
    /// block with no bytes in the dictionary (a STALE block of a type that
    /// minted nothing this run) contributes nothing locally (the guard's
    /// documented residual gap). A malformed body yields an empty list (the
    /// guard then never fires; the malformed-model case fails loudly earlier).
    /// </summary>
    internal static List<long> CollectWiredPayloadBytes(
        string modifiedJson,
        IReadOnlyDictionary<CatalogType, long> payloadBytesByType)
    {
        var wired = new List<long>(6);
        try
        {
            var typesArray = JsonNode.Parse(modifiedJson)?["interactionModel"]?["languageModel"]?["types"] as JsonArray;
            if (typesArray == null)
            {
                return wired;
            }

            foreach (JsonNode? typeNode in typesArray)
            {
                string? slotTypeName = typeNode?["name"]?.GetValue<string>();
                if (slotTypeName == null || !IsCatalogWired(typeNode!))
                {
                    continue;
                }

                if (TryGetCatalogTypeForSlotTypeName(slotTypeName, out CatalogType type)
                    && payloadBytesByType.TryGetValue(type, out long bytes))
                {
                    wired.Add(bytes);
                }
            }
        }
        catch (JsonException)
        {
            // Malformed model: no wired bytes to count; the guard stays silent.
        }

        return wired;
    }

    /// <summary>
    /// The reverse of <see cref="CatalogSlotTypes.CatalogSlotTypeNames"/>: a
    /// wired model block's slot type name back to its CatalogType. Built once;
    /// an unknown name (a block the catalog family does not own) resolves
    /// false.
    /// </summary>
    private static bool TryGetCatalogTypeForSlotTypeName(string slotTypeName, out CatalogType type)
    {
        foreach ((CatalogType candidate, string name) in CatalogSlotTypes.CatalogSlotTypeNames)
        {
            if (string.Equals(name, slotTypeName, StringComparison.Ordinal))
            {
                type = candidate;
                return true;
            }
        }

        type = default;
        return false;
    }

    /// <summary>
    /// The guard threshold this instance enforces: the test seam, else the const.
    /// </summary>
    private long EffectiveModelSizeSafetyThresholdBytes =>
        ModelSizeSafetyThresholdBytesForTest ?? ModelSizeSafetyThresholdBytes;

    /// <summary>
    /// Injects catalog-backed slot type definitions into the interaction model.
    /// Uses JsonNode for efficient in-place mutation without serialize/deserialize round-trips.
    /// </summary>
    /// <param name="modelJson">The raw interaction model JSON string.</param>
    /// <param name="artistCatalogId">The artist catalog ID to inject.</param>
    /// <param name="albumCatalogId">The album catalog ID to inject.</param>
    /// <param name="seriesCatalogId">The series catalog ID to inject.</param>
    /// <param name="audiobookCatalogId">The audiobook catalog ID to inject (JF-823).</param>
    /// <param name="movieCatalogId">The movie catalog ID to inject (JF-874).</param>
    /// <param name="artistCatalogVersion">The artist catalog version.</param>
    /// <param name="albumCatalogVersion">The album catalog version.</param>
    /// <param name="seriesCatalogVersion">The series catalog version.</param>
    /// <param name="audiobookCatalogVersion">The audiobook catalog version (JF-823).</param>
    /// <param name="movieCatalogVersion">The movie catalog version (JF-874).</param>
    /// <returns>The modified interaction model JSON string.</returns>
    internal static string InjectCatalogReferences(string modelJson, string? artistCatalogId, string? albumCatalogId, string? seriesCatalogId, string? audiobookCatalogId, string? movieCatalogId, string? artistCatalogVersion, string? albumCatalogVersion, string? seriesCatalogVersion, string? audiobookCatalogVersion, string? movieCatalogVersion, ILogger? logger = null, string? songCatalogId = null, string? songCatalogVersion = null)
    {
        logger ??= NullLogger.Instance;
        JsonNode? root = JsonNode.Parse(modelJson);
        if (root == null)
        {
            return modelJson;
        }

        var lmNode = root["interactionModel"]?["languageModel"] as JsonObject;
        if (lmNode == null)
        {
            logger.LogWarning("Interaction model has unexpected structure, skipping catalog injection");
            return modelJson;
        }

        var typesArray = lmNode["types"] as JsonArray;
        if (typesArray == null)
        {
            typesArray = new JsonArray();
            lmNode["types"] = typesArray;
        }

        // JF-952: the former fifth column (the per-type OnlyReplaceExisting
        // flag, song-only) is gone. One universal declare check in the loop
        // below now gates every synced type; see the check's comment for the
        // rationale and the branch order.
        var catalogMappings = new List<(string CatalogId, string Version, string SlotTypeName, string? ReplacesType)>();
        if (!string.IsNullOrEmpty(artistCatalogId))
        {
            catalogMappings.Add((artistCatalogId!, ResolveCatalogVersion(artistCatalogVersion, CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Artist], artistCatalogId, logger),
                CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Artist],
                CatalogSlotTypes.Names[CatalogType.Artist]));
        }

        if (!string.IsNullOrEmpty(albumCatalogId))
        {
            catalogMappings.Add((albumCatalogId!, ResolveCatalogVersion(albumCatalogVersion, CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Album], albumCatalogId, logger),
                CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Album],
                null));
        }

        if (!string.IsNullOrEmpty(seriesCatalogId))
        {
            // No ReplacesType: every locale model already declares SeriesName
            // (static seed) and slots reference it directly, so the injection
            // replaces the type definition in place without re-typing slots.
            catalogMappings.Add((seriesCatalogId!, ResolveCatalogVersion(seriesCatalogVersion, CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Series], seriesCatalogId, logger),
                CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Series],
                null));
        }

        if (!string.IsNullOrEmpty(audiobookCatalogId))
        {
            // No ReplacesType (JF-823): the SeriesName shape; the rationale lives
            // on the CatalogSlotTypes.CatalogSlotTypeNames audiobook entry.
            catalogMappings.Add((audiobookCatalogId!, ResolveCatalogVersion(audiobookCatalogVersion, CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Audiobook], audiobookCatalogId, logger),
                CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Audiobook],
                null));
        }

        if (!string.IsNullOrEmpty(movieCatalogId))
        {
            // No ReplacesType (JF-874): the SeriesName/AudiobookTitle shape,
            // declared by it-IT only; the loop's declare check owns what the
            // other 16 locales receive.
            catalogMappings.Add((movieCatalogId!, ResolveCatalogVersion(movieCatalogVersion, CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Movie], movieCatalogId, logger),
                CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Movie],
                null));
        }

        if (!string.IsNullOrEmpty(songCatalogId))
        {
            // No ReplacesType (JF-946): the SeriesName replace-in-place shape.
            // Wiring lands ONLY in locales whose intents declare the SongTitle
            // slot (it-IT and de-DE today): the universal declare check below
            // is the mechanism (it used to be this row's own
            // OnlyReplaceExisting flag, generalized by JF-952). GATED upstream
            // by the per-user CatalogSongSyncEnabled opt-in: a disabled flag
            // forwards no song catalog id, so nothing injects here.
            catalogMappings.Add((songCatalogId!, ResolveCatalogVersion(songCatalogVersion, CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Song], songCatalogId, logger),
                CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Song],
                null));
        }

        WarnOnCrossTypeCatalogIds(artistCatalogId, albumCatalogId, seriesCatalogId, audiobookCatalogId, movieCatalogId, songCatalogId, logger);

        var wiredTypes = new List<string>();
        var keptStaticTypes = new List<string>();
        var removedTypes = new List<string>();

        foreach (var (catalogId, catalogVersion, slotTypeName, replacesType) in catalogMappings)
        {
            int existingIndex = FindTypeIndex(typesArray, slotTypeName);

            // JF-952 universal declare check. Load-bearing branch order (the
            // JF-946 live lesson 2026-11-23, generalized): the check and the
            // block-shape inspection run BEFORE the JsonObject below is built
            // and BEFORE the replace branch decides, so a skipped or removed
            // mapping never constructs the block it refuses. Rationale: an
            // unreferenced catalog block buys nothing yet counts against
            // Amazon's 1.5MB EFFECTIVE per-model limit, which counts
            // referenced values (the JF-946 lesson: an inert SongTitle block
            // pushed ja-JP, fr, pt-BR and nl-NL over the cap; every
            // add-anywhere type already carried the same liability). The four
            // outcomes:
            // - DECLARED (a slot names the type, or the artist leg names the
            //   replaced type, so the 11 AMAZON.Musician locales keep
            //   JellyfinArtist): wire, replacing an existing block in place.
            // - NOT DECLARED, no existing block: skip the add.
            // - NOT DECLARED, existing WIRED block (a JsonObject
            //   valueSupplier, the ExtractWiring definition): REMOVE it, the
            //   JF-946 stale-block heal generalized to all six types. This is
            //   the one path that shrinks an over-cap live model
            //   (ExceededMaximumModelSize, 2026-11-23 incident class); a
            //   future template that declares the slot re-earns wiring on the
            //   next sync's declare check, so the removal is recoverable.
            // - NOT DECLARED, existing STATIC block (plain values array): the
            //   authoring template owns it; leave it byte-untouched and never
            //   convert it to a supplier (a static seed must not become the
            //   full catalog behind a model that cannot read it).
            bool declared = AnyIntentSlotDeclaresType(lmNode, slotTypeName)
                || (replacesType != null && AnyIntentSlotDeclaresType(lmNode, replacesType));

            if (!declared)
            {
                if (existingIndex < 0)
                {
                    logger.LogInformation(
                        "Skipping catalog wiring for {SlotTypeName}: the locale declares no slot of this type (or of the type it replaces), so the block would be inert against the effective-model size cap (JF-952)",
                        slotTypeName);
                    continue;
                }

                if (IsCatalogWired(typesArray[existingIndex]))
                {
                    logger.LogInformation(
                        "Removing inert catalog wiring for {SlotTypeName}: the locale declares no slot of this type and the block carries a catalog supplier, so the model returns to its static size (JF-946 heal, generalized by JF-952)",
                        slotTypeName);
                    typesArray.RemoveAt(existingIndex);
                    removedTypes.Add(slotTypeName);
                }
                else
                {
                    logger.LogInformation(
                        "Keeping static seed block for {SlotTypeName}: the locale declares no slot of this type and the block carries static values, which stay untouched (JF-952, no seed-to-catalog swap)",
                        slotTypeName);
                    keptStaticTypes.Add(slotTypeName);
                }

                continue;
            }

            var catalogType = new JsonObject
            {
                ["name"] = slotTypeName,
                ["valueSupplier"] = new JsonObject
                {
                    ["type"] = "CatalogValueSupplier",
                    ["valueCatalog"] = new JsonObject
                    {
                        ["catalogId"] = catalogId,
                        ["version"] = catalogVersion
                    }
                }
            };

            if (existingIndex >= 0)
            {
                logger.LogInformation(
                    "Replacing slot type {SlotTypeName} (index {Index}) with catalog {CatalogId}",
                    slotTypeName,
                    existingIndex,
                    catalogId);
                typesArray[existingIndex] = catalogType;
            }
            else
            {
                logger.LogInformation(
                    "Adding new catalog-backed slot type {SlotTypeName} with catalog {CatalogId}",
                    slotTypeName,
                    catalogId);
                typesArray.Add(catalogType);
            }

            wiredTypes.Add(slotTypeName);

            if (replacesType != null)
            {
                UpdateIntentSlotTypes(lmNode, replacesType, slotTypeName, logger);
                // The dialog model also declares per-intent slot types; they MUST match
                // the interaction model or SMAPI rejects the build with MismatchedSlotType
                // (e.g. FindSongByArtistIntent.musician stayed AMAZON.Musician). JF-332.
                var dialogNode = root["interactionModel"]?["dialog"] as JsonObject;
                UpdateDialogSlotTypes(dialogNode, replacesType, slotTypeName, logger);
            }
        }

        // The summary comes AFTER the loop on purpose: the JF-952 check can
        // skip, remove or keep-static mappings, so a pre-loop count of the
        // attempted rows would overstate what landed (the JF-946 over-cap
        // triage correlates these lines against model size).
        logger.LogInformation(
            "Catalog injection: wired {Wired} of {Attempted} mappings ({WiredTypes}); static seeds kept untouched: {KeptStatic}; inert wiring removed: {Removed}",
            wiredTypes.Count,
            catalogMappings.Count,
            string.Join(", ", wiredTypes),
            keptStaticTypes.Count == 0 ? "none" : string.Join(", ", keptStaticTypes),
            removedTypes.Count == 0 ? "none" : string.Join(", ", removedTypes));

        return root.ToJsonString();
    }

    /// <summary>
    /// True when any intent slot in the language model references the given
    /// slot type name: the declaring-locale test of the JF-952 universal
    /// declare check (every synced catalog type wires only where the model
    /// actually uses it).
    /// </summary>
    private static bool AnyIntentSlotDeclaresType(JsonObject lmNode, string slotTypeName)
    {
        if (lmNode["intents"] is not JsonArray intents)
        {
            return false;
        }

        return VisitIntentSlots(intents, slot => slot["type"]?.GetValue<string>() == slotTypeName);
    }

    /// <summary>
    /// True when an existing types-array block carries catalog wiring: a
    /// JsonObject valueSupplier (the ExtractWiring definition, JF-555
    /// tolerant shape: a null/scalar supplier reads as NOT wired). The
    /// JF-952 review round's block-shape inspector: it is what lets the
    /// declare check remove a stale WIRED block (the JF-946 heal) while
    /// leaving a static seed byte-untouched.
    /// </summary>
    private static bool IsCatalogWired(JsonNode? typeBlock) => typeBlock?["valueSupplier"] is JsonObject;

    /// <summary>
    /// The ONE slot-type block lookup for the injection loop and the JF-949 strip:
    /// the index of the type named <paramref name="slotTypeName"/> in the types
    /// array, or -1. Shared so a guard against a malformed (non-string) name node,
    /// if one ever lands, cannot miss one of the two copies (the repo's
    /// "missed one" class; the reviewers flagged the verbatim duplication).
    /// </summary>
    /// <param name="typesArray">The language model's types array.</param>
    /// <param name="slotTypeName">The slot type name to find.</param>
    /// <returns>The index, or -1 when absent.</returns>
    private static int FindTypeIndex(JsonArray typesArray, string slotTypeName) =>
        Enumerable.Range(0, typesArray.Count)
            .FirstOrDefault(i => typesArray[i]?["name"]?.GetValue<string>() == slotTypeName, -1);

    /// <summary>
    /// JF-952: the shared slot-scan core, the ONE intents-array to slots-array
    /// to slot["type"] traversal. Applies <paramref name="onSlot"/> to every
    /// slot object until it returns true and reports whether some call stopped
    /// the walk: the declare predicate short-circuits on the first match, the
    /// JF-332 mutator visits every slot and always returns false. Null
    /// intents, non-object entries and slotless intents are skipped; the
    /// extraction had to pick one shape and took the predicate's tolerant one
    /// (the mutator previously THREW on a non-object intent entry, a shape no
    /// live model reaches today).
    /// </summary>
    private static bool VisitIntentSlots(JsonArray intents, Func<JsonObject, bool> onSlot)
    {
        foreach (var intent in intents.OfType<JsonObject>())
        {
            if (intent["slots"] is JsonArray slots)
            {
                foreach (var slot in slots.OfType<JsonObject>())
                {
                    if (onSlot(slot))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves the catalog version to pin, warning when the stale "1" fallback
    /// engages (JF-495). A null/empty version with a non-empty catalog id pins
    /// version "1"; on a long-lived catalog that version may be purged or far
    /// behind, which can degrade NLU slot resolution while the model build still
    /// reports SUCCEEDED. Version "1" is ambiguous (it is also a real first
    /// version), so this warns rather than rejects.
    /// </summary>
    private static string ResolveCatalogVersion(string? catalogVersion, string slotTypeName, string catalogId, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(catalogVersion))
        {
            return catalogVersion!;
        }

        logger.LogWarning(
            "Catalog version for slot type {SlotTypeName} on catalog {CatalogId} is null or empty; pinning the stale fallback version \"1\". If this catalog has newer (or purged) versions the model may reference a version Amazon cannot resolve (JF-495)",
            slotTypeName, catalogId);
        return "1";
    }

    /// <summary>
    /// Warns when the same catalog id is supplied for two different entity types
    /// (JF-495): one slot type would then be backed by another type's catalog
    /// (e.g. the artist catalog feeding AlbumName), corrupting slot resolution.
    /// </summary>
    private static void WarnOnCrossTypeCatalogIds(string? artistCatalogId, string? albumCatalogId, string? seriesCatalogId, string? audiobookCatalogId, string? movieCatalogId, string? songCatalogId, ILogger logger)
    {
        var supplied = new[]
        {
            (Type: "Artist", Id: artistCatalogId),
            (Type: "Album", Id: albumCatalogId),
            (Type: "Series", Id: seriesCatalogId),
            (Type: "Audiobook", Id: audiobookCatalogId),
            (Type: "Movie", Id: movieCatalogId),
            (Type: "Song", Id: songCatalogId)
        };

        for (int i = 0; i < supplied.Length; i++)
        {
            for (int j = i + 1; j < supplied.Length; j++)
            {
                if (!string.IsNullOrEmpty(supplied[i].Id)
                    && string.Equals(supplied[i].Id, supplied[j].Id, StringComparison.Ordinal))
                {
                    logger.LogWarning(
                        "Catalog {CatalogId} is referenced for both the {TypeA} and {TypeB} slot types; one slot type is pointing at another type's catalog (JF-495)",
                        supplied[i].Id, supplied[i].Type, supplied[j].Type);
                }
            }
        }
    }

    /// <summary>
    /// Updates all interaction-model intent slot type references from
    /// <paramref name="oldType"/> to <paramref name="newType"/>.
    /// </summary>
    /// <param name="languageModel">The language model JSON object to update.</param>
    /// <param name="oldType">The old slot type name to replace.</param>
    /// <param name="newType">The new slot type name to use.</param>
    internal static void UpdateIntentSlotTypes(JsonObject languageModel, string oldType, string newType, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        int updatedCount = UpdateSlotTypesInIntents(languageModel["intents"] as JsonArray, oldType, newType);
        logger.LogInformation(
            "Updated {Count} intent slot references from {OldType} to {NewType}",
            updatedCount,
            oldType,
            newType);
    }

    /// <summary>
    /// Updates all dialog-model intent slot type references from <paramref name="oldType"/>
    /// to <paramref name="newType"/>. The dialog model must agree with the interaction
    /// model's slot types or SMAPI rejects the build (MismatchedSlotType). JF-332.
    /// </summary>
    /// <param name="dialog">The dialog model JSON object, or null if absent.</param>
    /// <param name="oldType">The old slot type name to replace.</param>
    /// <param name="newType">The new slot type name to use.</param>
    internal static void UpdateDialogSlotTypes(JsonObject? dialog, string oldType, string newType, ILogger logger)
    {
        int updatedCount = UpdateSlotTypesInIntents(dialog?["intents"] as JsonArray, oldType, newType);
        if (updatedCount > 0)
        {
            logger.LogInformation(
                "Updated {Count} dialog slot references from {OldType} to {NewType}",
                updatedCount,
                oldType,
                newType);
        }
    }

    private static int UpdateSlotTypesInIntents(JsonArray? intentsArray, string oldType, string newType)
    {
        if (intentsArray == null)
        {
            return 0;
        }

        // JF-952: the traversal is the shared VisitIntentSlots core; the
        // mutator only supplies the per-slot rewrite and never stops the walk.
        int updatedCount = 0;
        VisitIntentSlots(intentsArray, slot =>
        {
            if (slot["type"]?.GetValue<string>() == oldType)
            {
                slot["type"] = newType;
                updatedCount++;
            }

            return false;
        });

        return updatedCount;
    }

    /// <summary>
    /// Resolves a potentially relative Location URI against the SMAPI base endpoint.
    /// </summary>
    private static Uri ResolveLocationUri(Uri locationUri)
    {
        if (!locationUri.IsAbsoluteUri)
        {
            locationUri = new Uri(new Uri(SmapiEndpoint), locationUri);
        }

        return locationUri;
    }

    /// <summary>
    /// True when a resolved Location header points at a pollable SMAPI
    /// update-request resource: a URL carrying an "updateRequest" path segment
    /// (e.g. .../catalogs/{id}/updateRequest/{requestId}), the only response
    /// shape <see cref="ExtractPollStatus"/> can parse. Any other Location
    /// (observed live, JF-497: a skill-status URL like /v1/skills/{id}/status,
    /// whose response nests the status under interactionModel.{locale}) must be
    /// tracked via <see cref="WaitForModelBuildOutcomeViaSkillStatusAsync"/>
    /// instead of being polled into a guaranteed timeout.
    /// </summary>
    /// <param name="location">The resolved Location URI from the 202 response.</param>
    /// <returns>True when the URL names an update-request resource.</returns>
    internal static bool IsUpdateRequestLocation(Uri location)
    {
        string path = location.IsAbsoluteUri ? location.AbsolutePath : location.ToString();
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (string segment in segments)
        {
            if (string.Equals(segment, "updateRequest", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Polls a SMAPI async operation until SUCCEEDED or FAILED.
    /// Returns the "version" property from the final response if present, otherwise null.
    /// A 404 from the polled update-request URL is treated as TERMINAL-COMPLETED with
    /// an unknown version (null): SMAPI consumes/expires the one-shot updateRequest
    /// resource once the operation completes, so a poll that outlives the build gets
    /// 404 with an HTML error page rather than a status. Logged quietly (JF-502);
    /// other HTTP failures keep the transient behavior (log ERR + throw).
    /// </summary>
    private async Task<string?> PollSmapiOperationAsync(
        string accessToken,
        HttpClient client,
        Uri locationUri,
        string operationName,
        CancellationToken cancellationToken)
    {
        string location = locationUri.ToString();
        _logger.LogDebug("{Operation} creation accepted, polling at {Location}", operationName, location);

        int delay = PollDelayMs;
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = Math.Min(delay * 2, 2000);

            using var pollRequest = new HttpRequestMessage(HttpMethod.Get, location);
            pollRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var pollResponse = await client.SendAsync(pollRequest, cancellationToken).ConfigureAwait(false);

            // JF-502: a 404 from the polled update-request URL means the update
            // request is GONE, not that the poll failed: SMAPI consumes/expires the
            // one-shot updateRequest resource once the operation completes, so a
            // poll arriving after completion gets 404 with an HTML error page.
            // Treat it as terminal-completed with an unknown version and log
            // quietly; EnsureSuccessAsync would log an ERR and throw, marking the
            // whole operation failed. Other HTTP failures (429, 5xx, ...) keep the
            // transient current behavior.
            if (pollResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogDebug(
                    "{Operation} poll at {Location} returned 404 (update request consumed or expired after completion); treating as terminal-completed with an unknown version (JF-502)",
                    operationName,
                    location);
                return null;
            }

            await EnsureSuccessAsync(pollResponse, cancellationToken).ConfigureAwait(false);

            string pollJson = await pollResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var pollDoc = JsonDocument.Parse(pollJson);

            var (status, version, errorJson) = ExtractPollStatus(pollDoc);

            _logger.LogDebug("{Operation} poll {Iteration}: status={Status}", operationName, i + 1, status);

            if (status == "SUCCEEDED")
            {
                return version;
            }

            if (status == "FAILED")
            {
                throw new InvalidOperationException($"{operationName} failed: {errorJson ?? "unknown"}");
            }
        }

        _logger.LogWarning("{Operation} polling timed out at {Location}", operationName, location);
        throw new TimeoutException($"{operationName} polling timed out after 30 attempts at {location}");
    }

    /// <summary>
    /// Extracts status/version/errors from a SMAPI updateRequest poll response.
    /// SMAPI nests these under "lastUpdateRequest" for interactionModel catalog
    /// and slot-type update requests — e.g. GET .../catalogs/{id}/updateRequest/{reqId}
    /// returns {"lastUpdateRequest":{"status":"SUCCEEDED","version":"2"}}. The previous
    /// implementation read "status"/"version" from the JSON root, which is always null
    /// for these endpoints, causing every poll to time out (JF-332). Falls back to
    /// root-level for any endpoint that returns status at the top level.
    /// </summary>
    /// <param name="pollDoc">The parsed poll response JSON document.</param>
    /// <returns>A tuple of (status, version, errors raw JSON); any may be null.</returns>
    internal static (string? Status, string? Version, string? ErrorJson) ExtractPollStatus(JsonDocument pollDoc)
    {
        JsonElement root = pollDoc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null);
        }

        JsonElement container = root.TryGetProperty("lastUpdateRequest", out var lur)
            && lur.ValueKind == JsonValueKind.Object
                ? lur
                : root;

        string? status = container.TryGetProperty("status", out var s) ? s.GetString() : null;
        string? version = container.TryGetProperty("version", out var v) ? v.GetString() : null;
        string? errorJson = container.TryGetProperty("errors", out var e) ? e.GetRawText() : null;
        return (status, version, errorJson);
    }

    /// <summary>
    /// Ensures the HTTP response indicates success. Reads the error body for logging on failure.
    /// </summary>
    /// <param name="response">The HTTP response message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        => EnsureSuccessSharedAsync(response, _logger, cancellationToken);

    /// <summary>
    /// The shared success gate for raw SMAPI responses (instance callers above and
    /// the transport helpers that carry their own logger, JF-554).
    /// </summary>
    internal static async Task EnsureSuccessSharedAsync(HttpResponseMessage response, ILogger logger, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        logger.LogError(
            "SMAPI request failed: {StatusCode} {ReasonPhrase}. Body: {Body}",
            (int)response.StatusCode,
            response.ReasonPhrase,
            errorBody);

        response.EnsureSuccessStatusCode();
    }
}

/// <summary>
/// Outcome of a catalog-sync interaction-model update (JF-495): the SMAPI build
/// status plus the post-deploy canary counts, consumed by the status ledger
/// (LocaleModelStatuses) so catalog-sync PUTs are recorded next to
/// ModelDeploymentManager deployments.
/// </summary>
/// <param name="BuildStatus">"SUCCEEDED", "FAILED", "TIMEOUT" (poll budget exhausted), "Skipped" (no catalogs to inject), or "SkippedOversize" (JF-951: the pre-flight size guard skipped the PUT; the skip reason rides CanaryError, which the ledger displays in its Error column even though nothing canary failed).</param>
/// <param name="CanaryMatch">True/false when the canary ran; null when it could not (build not settled or GET failed).</param>
/// <param name="PutIntents">Intent count of the submitted payload.</param>
/// <param name="PutSamples">Sample count of the submitted payload.</param>
/// <param name="LiveIntents">Intent count of the live model after the build, when the canary ran.</param>
/// <param name="LiveSamples">Sample count of the live model after the build, when the canary ran.</param>
/// <param name="CanaryError">Human-readable canary mismatch description, or null.</param>
public sealed record CatalogModelUpdateResult(
    string BuildStatus,
    bool? CanaryMatch,
    int PutIntents,
    int PutSamples,
    int? LiveIntents = null,
    int? LiveSamples = null,
    string? CanaryError = null);
