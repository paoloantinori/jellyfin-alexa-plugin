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

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;

/// <summary>
/// Manages the SMAPI catalog lifecycle: creating catalogs, uploading values,
/// and creating/updating slot types that reference those catalogs.
/// Each user's dynamic media library values (artists, albums, etc.) are
/// stored in SMAPI catalogs so the Alexa NLU can resolve them at runtime.
/// </summary>
public class CatalogManager
{
    private const string SmapiEndpoint = "https://api.amazonalexa.com";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CatalogManager> _logger;

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
                await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
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
    /// deploy's build is still queued); after the PUT, polls the update request and
    /// runs a canary GET-back comparing the live intent/sample counts with what was
    /// submitted. Both measures make the 2026-09-05 silent-stale-regression class
    /// either impossible (GET race) or loud (canary mismatch ERROR).
    /// </summary>
    /// <param name="accessToken">The SMAPI access token.</param>
    /// <param name="skillId">The skill ID whose model should be updated.</param>
    /// <param name="stage">The skill stage (e.g. "development").</param>
    /// <param name="locale">The locale to update (e.g. "it-IT").</param>
    /// <param name="artistCatalogId">The artist catalog ID (may be null).</param>
    /// <param name="albumCatalogId">The album catalog ID (may be null).</param>
    /// <param name="seriesCatalogId">The series catalog ID (may be null).</param>
    /// <param name="artistCatalogVersion">The artist catalog version (may be null).</param>
    /// <param name="albumCatalogVersion">The album catalog version (may be null).</param>
    /// <param name="seriesCatalogVersion">The series catalog version (may be null).</param>
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
        string? artistCatalogVersion,
        string? albumCatalogVersion,
        string? seriesCatalogVersion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(artistCatalogId) && string.IsNullOrEmpty(albumCatalogId) && string.IsNullOrEmpty(seriesCatalogId))
        {
            _logger.LogInformation("No catalogs to inject into interaction model, skipping update");
            return new CatalogModelUpdateResult("Skipped", null, 0, 0);
        }

        var client = _httpClientFactory.CreateClient("AlexaSkill");
        string modelUrl = $"{SmapiEndpoint}/v1/skills/{skillId}/stages/{stage}/interactionModel/locales/{locale}";

        // JF-495: serialize against concurrently-pending builds BEFORE the GET. When a
        // rebuild (or any other writer) submitted a model moments ago, its build may
        // still be IN_PROGRESS and the GET would return the last SUCCEEDED (stale)
        // content; PUTting that content back redeploys yesterday's model.
        await WaitForLocaleBuildToSettleAsync(accessToken, client, skillId, stage, locale, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Fetching interaction model for skill {SkillId} locale {Locale}", skillId, locale);

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, modelUrl);
        getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var getResponse = await client.SendAsync(getRequest, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(getResponse, cancellationToken).ConfigureAwait(false);

        string modelJson = await getResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        string modifiedJson = InjectCatalogReferences(modelJson, artistCatalogId, albumCatalogId, seriesCatalogId, artistCatalogVersion, albumCatalogVersion, seriesCatalogVersion);

        // JF-495: greppable audit line before the PUT.
        var (putIntents, putSamples) = InteractionModelPutAudit.CountFromJson(modifiedJson);
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
        putRequest.Content = new StringContent(modifiedJson, Encoding.UTF8, "application/json");

        using var putResponse = await client.SendAsync(putRequest, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(putResponse, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Interaction model update submitted for skill {SkillId} locale {Locale}", skillId, locale);

        // JF-495: the PUT is asynchronous on SMAPI (202 + update-request Location).
        // Wait for the build so this sync's build cannot complete and clobber the
        // live model minutes later, unobserved.
        string buildStatus;
        Uri? buildLocation = putResponse.Headers.Location;
        if (buildLocation != null)
        {
            try
            {
                await PollSmapiOperationAsync(
                    accessToken, client, ResolveLocationUri(buildLocation), "Interaction model update", cancellationToken).ConfigureAwait(false);
                buildStatus = "SUCCEEDED";
            }
            catch (TimeoutException ex)
            {
                buildStatus = "TIMEOUT";
                _logger.LogWarning(ex,
                    "Interaction model update build did not settle within the poll budget for skill {SkillId} locale {Locale}; the live model state is unverified (JF-495)",
                    skillId, locale);
            }
            catch (HttpRequestException ex)
            {
                // JF-495 review fix: the PUT itself succeeded (a failure above would have
                // thrown before the poll); only the OBSERVATION failed (e.g. a 429
                // mid-poll). Do not mark the locale failed or skip its ledger entry for
                // an observation error.
                buildStatus = "UNVERIFIED";
                _logger.LogWarning(ex,
                    "Interaction model update poll failed transiently for skill {SkillId} locale {Locale}; the build outcome is unverified, not failed (JF-495)",
                    skillId, locale);
            }
            catch (InvalidOperationException ex)
            {
                buildStatus = "FAILED";
                _logger.LogError(ex,
                    "Interaction model update build FAILED for skill {SkillId} locale {Locale} (JF-495)",
                    skillId, locale);
            }
        }
        else
        {
            _logger.LogWarning(
                "Interaction model PUT for skill {SkillId} locale {Locale} returned no Location header; tracking the build via the skill-status endpoint instead (JF-495)",
                skillId, locale);
            buildStatus = await WaitForModelBuildOutcomeViaSkillStatusAsync(
                accessToken, client, skillId, stage, locale, cancellationToken).ConfigureAwait(false);
        }

        // Canary: only meaningful once the build reports SUCCEEDED; a GET before the
        // build settles would race and produce a false mismatch.
        if (buildStatus == "SUCCEEDED")
        {
            return await VerifyPutCanaryAsync(accessToken, client, modelUrl, locale, skillId, putIntents, putSamples, buildStatus, cancellationToken).ConfigureAwait(false);
        }

        return new CatalogModelUpdateResult(buildStatus, null, putIntents, putSamples);
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
    /// Builds a bearer-authorized GET request for a SMAPI URL.
    /// </summary>
    private static HttpRequestMessage CreateAuthorizedGet(string url, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    /// <summary>
    /// Reads the locale's interaction-model build state from the skill-status
    /// endpoint. Returns null when the status cannot be read (endpoint failure)
    /// or the locale has no reported status; failures are logged as warnings.
    /// </summary>
    private async Task<string?> TryGetLocaleModelStatusAsync(
        string accessToken,
        HttpClient client,
        string skillId,
        string stage,
        string locale,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateAuthorizedGet(
                $"{SmapiEndpoint}/v1/skills/{skillId}/stages/{stage}/status", accessToken);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ExtractLocaleModelStatus(json, locale);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TimeoutException)
        {
            _logger.LogWarning(ex,
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
    private async Task WaitForLocaleBuildToSettleAsync(
        string accessToken,
        HttpClient client,
        string skillId,
        string stage,
        string locale,
        CancellationToken cancellationToken)
    {
        int delay = 500;
        for (int i = 0; i < 30; i++)
        {
            string? state = await TryGetLocaleModelStatusAsync(
                accessToken, client, skillId, stage, locale, cancellationToken).ConfigureAwait(false);
            if (state is null || state != "IN_PROGRESS")
            {
                return;
            }

            _logger.LogInformation(
                "Waiting for pending interaction model build ({State}) to settle before GET-modify-PUT for skill {SkillId} locale {Locale} (JF-495)",
                state, skillId, locale);

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = Math.Min(delay * 2, 2000);
        }

        _logger.LogWarning(
            "Interaction model build for skill {SkillId} locale {Locale} is still IN_PROGRESS after the settle budget; proceeding with GET-modify-PUT anyway (JF-495)",
            skillId, locale);
    }

    /// <summary>
    /// Fallback build tracker for model PUTs whose response carries no Location
    /// header (JF-495): polls the skill-status endpoint until the locale's model
    /// build reaches a terminal state. When the first observation is already
    /// terminal it may reflect the PREVIOUS build; that ambiguity is logged and
    /// the canary still verifies the live counts afterwards.
    /// </summary>
    /// <returns>"SUCCEEDED", "FAILED", or "TIMEOUT" when the budget is exhausted.</returns>
    private async Task<string> WaitForModelBuildOutcomeViaSkillStatusAsync(
        string accessToken,
        HttpClient client,
        string skillId,
        string stage,
        string locale,
        CancellationToken cancellationToken)
    {
        // Give SMAPI a moment to flip the locale's status to IN_PROGRESS before
        // the first poll, so a stale terminal status from the previous build is
        // not mistaken for this PUT's outcome.
        await Task.Delay(500, cancellationToken).ConfigureAwait(false);

        int delay = 500;
        for (int i = 0; i < 30; i++)
        {
            string? state = await TryGetLocaleModelStatusAsync(
                accessToken, client, skillId, stage, locale, cancellationToken).ConfigureAwait(false);

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
    /// (the JSON shape served by GET /v1/skills/[id]/stages/[stage]/status:
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
    /// Injects catalog-backed slot type definitions into the interaction model.
    /// Uses JsonNode for efficient in-place mutation without serialize/deserialize round-trips.
    /// </summary>
    /// <param name="modelJson">The raw interaction model JSON string.</param>
    /// <param name="artistCatalogId">The artist catalog ID to inject.</param>
    /// <param name="albumCatalogId">The album catalog ID to inject.</param>
    /// <param name="seriesCatalogId">The series catalog ID to inject.</param>
    /// <param name="artistCatalogVersion">The artist catalog version.</param>
    /// <param name="albumCatalogVersion">The album catalog version.</param>
    /// <param name="seriesCatalogVersion">The series catalog version.</param>
    /// <returns>The modified interaction model JSON string.</returns>
    internal string InjectCatalogReferences(string modelJson, string? artistCatalogId, string? albumCatalogId, string? seriesCatalogId, string? artistCatalogVersion, string? albumCatalogVersion, string? seriesCatalogVersion)
    {
        JsonNode? root = JsonNode.Parse(modelJson);
        if (root == null)
        {
            return modelJson;
        }

        var lmNode = root["interactionModel"]?["languageModel"] as JsonObject;
        if (lmNode == null)
        {
            _logger.LogWarning("Interaction model has unexpected structure, skipping catalog injection");
            return modelJson;
        }

        var typesArray = lmNode["types"] as JsonArray;
        if (typesArray == null)
        {
            typesArray = new JsonArray();
            lmNode["types"] = typesArray;
        }

        var catalogMappings = new List<(string CatalogId, string Version, string SlotTypeName, string? ReplacesType)>();
        if (!string.IsNullOrEmpty(artistCatalogId))
        {
            catalogMappings.Add((artistCatalogId!, ResolveCatalogVersion(artistCatalogVersion, CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Artist], artistCatalogId),
                CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Artist],
                CatalogSlotTypes.Names[CatalogType.Artist]));
        }

        if (!string.IsNullOrEmpty(albumCatalogId))
        {
            catalogMappings.Add((albumCatalogId!, ResolveCatalogVersion(albumCatalogVersion, CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Album], albumCatalogId),
                CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Album],
                null));
        }

        if (!string.IsNullOrEmpty(seriesCatalogId))
        {
            // No ReplacesType: every locale model already declares SeriesName
            // (static seed) and slots reference it directly, so the injection
            // replaces the type definition in place without re-typing slots.
            catalogMappings.Add((seriesCatalogId!, ResolveCatalogVersion(seriesCatalogVersion, CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Series], seriesCatalogId),
                CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Series],
                null));
        }

        WarnOnCrossTypeCatalogIds(artistCatalogId, albumCatalogId, seriesCatalogId);

        _logger.LogInformation(
            "Injecting {Count} catalog references into interaction model ({SlotTypes})",
            catalogMappings.Count,
            string.Join(", ", catalogMappings.Select(m => m.SlotTypeName)));

        foreach (var (catalogId, catalogVersion, slotTypeName, replacesType) in catalogMappings)
        {
            int existingIndex = Enumerable.Range(0, typesArray.Count)
                .FirstOrDefault(i => typesArray[i]?["name"]?.GetValue<string>() == slotTypeName, -1);

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
                _logger.LogInformation(
                    "Replacing slot type {SlotTypeName} (index {Index}) with catalog {CatalogId}",
                    slotTypeName,
                    existingIndex,
                    catalogId);
                typesArray[existingIndex] = catalogType;
            }
            else
            {
                _logger.LogInformation(
                    "Adding new catalog-backed slot type {SlotTypeName} with catalog {CatalogId}",
                    slotTypeName,
                    catalogId);
                typesArray.Add(catalogType);
            }

            if (replacesType != null)
            {
                UpdateIntentSlotTypes(lmNode, replacesType, slotTypeName);
                // The dialog model also declares per-intent slot types; they MUST match
                // the interaction model or SMAPI rejects the build with MismatchedSlotType
                // (e.g. FindSongByArtistIntent.musician stayed AMAZON.Musician). JF-332.
                var dialogNode = root["interactionModel"]?["dialog"] as JsonObject;
                UpdateDialogSlotTypes(dialogNode, replacesType, slotTypeName);
            }
        }

        return root.ToJsonString();
    }

    /// <summary>
    /// Resolves the catalog version to pin, warning when the stale "1" fallback
    /// engages (JF-495). A null/empty version with a non-empty catalog id pins
    /// version "1"; on a long-lived catalog that version may be purged or far
    /// behind, which can degrade NLU slot resolution while the model build still
    /// reports SUCCEEDED. Version "1" is ambiguous (it is also a real first
    /// version), so this warns rather than rejects.
    /// </summary>
    private string ResolveCatalogVersion(string? catalogVersion, string slotTypeName, string catalogId)
    {
        if (!string.IsNullOrWhiteSpace(catalogVersion))
        {
            return catalogVersion!;
        }

        _logger.LogWarning(
            "Catalog version for slot type {SlotTypeName} on catalog {CatalogId} is null or empty; pinning the stale fallback version \"1\". If this catalog has newer (or purged) versions the model may reference a version Amazon cannot resolve (JF-495)",
            slotTypeName, catalogId);
        return "1";
    }

    /// <summary>
    /// Warns when the same catalog id is supplied for two different entity types
    /// (JF-495): one slot type would then be backed by another type's catalog
    /// (e.g. the artist catalog feeding AlbumName), corrupting slot resolution.
    /// </summary>
    private void WarnOnCrossTypeCatalogIds(string? artistCatalogId, string? albumCatalogId, string? seriesCatalogId)
    {
        var supplied = new[]
        {
            (Type: "Artist", Id: artistCatalogId),
            (Type: "Album", Id: albumCatalogId),
            (Type: "Series", Id: seriesCatalogId)
        };

        for (int i = 0; i < supplied.Length; i++)
        {
            for (int j = i + 1; j < supplied.Length; j++)
            {
                if (!string.IsNullOrEmpty(supplied[i].Id)
                    && string.Equals(supplied[i].Id, supplied[j].Id, StringComparison.Ordinal))
                {
                    _logger.LogWarning(
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
    internal void UpdateIntentSlotTypes(JsonObject languageModel, string oldType, string newType)
    {
        int updatedCount = UpdateSlotTypesInIntents(languageModel["intents"] as JsonArray, oldType, newType);
        _logger.LogInformation(
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
    internal void UpdateDialogSlotTypes(JsonObject? dialog, string oldType, string newType)
    {
        int updatedCount = UpdateSlotTypesInIntents(dialog?["intents"] as JsonArray, oldType, newType);
        if (updatedCount > 0)
        {
            _logger.LogInformation(
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

        int updatedCount = 0;
        foreach (var intentNode in intentsArray)
        {
            var slotsArray = intentNode?["slots"] as JsonArray;
            if (slotsArray == null)
            {
                continue;
            }

            foreach (var slotNode in slotsArray)
            {
                if (slotNode is JsonObject slotObj &&
                    slotObj["type"]?.GetValue<string>() == oldType)
                {
                    slotObj["type"] = newType;
                    updatedCount++;
                }
            }
        }

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
    /// Polls a SMAPI async operation until SUCCEEDED or FAILED.
    /// Returns the "version" property from the final response if present, otherwise null.
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

        int delay = 500;
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = Math.Min(delay * 2, 2000);

            using var pollRequest = new HttpRequestMessage(HttpMethod.Get, location);
            pollRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var pollResponse = await client.SendAsync(pollRequest, cancellationToken).ConfigureAwait(false);
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
    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogError(
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
/// <param name="BuildStatus">"SUCCEEDED", "FAILED", "TIMEOUT" (poll budget exhausted), or "Skipped" (no catalogs to inject).</param>
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
