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
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CatalogManager> _logger;

    private const string SmapiEndpoint = "https://api.amazonalexa.com";

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
        _logger.LogInformation("Creating SMAPI catalog '{CatalogName}' for vendor {VendorId}",
            catalogName, vendorId);

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

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{SmapiEndpoint}/v1/skills/api/custom/interactionModel/catalogs");
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
    /// Creates a catalog version by providing a hosted URL for SMAPI to pull.
    /// SMAPI flow: store payload in cache -> create version with source URL -> poll status.
    /// </summary>
    /// <param name="accessToken">The SMAPI access token.</param>
    /// <param name="catalogId">The target catalog ID.</param>
    /// <param name="payload">The catalog values payload to upload.</param>
    /// <param name="catalogUrl">The public URL where SMAPI can fetch the catalog JSON.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The committed version string.</returns>
    public async Task<string> UploadCatalogValuesAsync(
        string accessToken,
        string catalogId,
        CatalogPayload payload,
        string catalogUrl,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Creating catalog version for {CatalogId} with {ValueCount} values from {Url}",
            catalogId, payload.Values.Count, catalogUrl);

        var client = _httpClientFactory.CreateClient("AlexaSkill");

        var versionBody = new
        {
            source = new
            {
                type = "URL",
                url = catalogUrl
            },
            description = $"Library sync {DateTime.UtcNow:O}"
        };

        using var versionRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{SmapiEndpoint}/v1/skills/api/custom/interactionModel/catalogs/{catalogId}/versions");
        versionRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        versionRequest.Content = JsonContent.Create(versionBody, options: JsonOptions);

        using var versionResponse = await client.SendAsync(versionRequest, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(versionResponse, cancellationToken).ConfigureAwait(false);

        // 202 Accepted with Location header for polling
        Uri? locationUri = versionResponse.Headers.Location;
        if (locationUri == null)
        {
            _logger.LogWarning("Catalog version creation returned no Location header");
            return "1";
        }

        locationUri = ResolveLocationUri(locationUri);

        string? version = await PollSmapiOperationAsync(
            accessToken, client, locationUri, "Catalog version", cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Catalog {CatalogId} version {Version} created successfully", catalogId, version);
        return version ?? "1";
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

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{SmapiEndpoint}/v1/skills/api/custom/interactionModel/slotTypes");
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
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task CreateSlotTypeVersionAsync(
        string accessToken,
        string slotTypeId,
        string catalogId,
        string catalogVersion,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Creating slot type version for {SlotTypeId} referencing catalog {CatalogId} version {Version}",
            slotTypeId, catalogId, catalogVersion);

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

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{SmapiEndpoint}/v1/skills/api/custom/interactionModel/slotTypes/{slotTypeId}/versions");
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
    /// Implements the full 3-step SMAPI process: create entity → create version with CatalogValueSupplier.
    /// </summary>
    /// <param name="accessToken">The SMAPI access token.</param>
    /// <param name="vendorId">The vendor ID for the skill owner.</param>
    /// <param name="slotTypeName">The name for the slot type (e.g. "JELLYFIN_ARTIST").</param>
    /// <param name="catalogId">The catalog ID that supplies values.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
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
    /// </summary>
    /// <param name="accessToken">The SMAPI access token.</param>
    /// <param name="skillId">The skill ID whose model should be updated.</param>
    /// <param name="stage">The skill stage (e.g. "development").</param>
    /// <param name="locale">The locale to update (e.g. "it-IT").</param>
    /// <param name="artistCatalogId">The artist catalog ID (may be null).</param>
    /// <param name="albumCatalogId">The album catalog ID (may be null).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task UpdateInteractionModelAsync(
        string accessToken,
        string skillId,
        string stage,
        string locale,
        string? artistCatalogId,
        string? albumCatalogId,
        string? artistCatalogVersion,
        string? albumCatalogVersion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(artistCatalogId) && string.IsNullOrEmpty(albumCatalogId))
        {
            _logger.LogInformation("No catalogs to inject into interaction model, skipping update");
            return;
        }

        var client = _httpClientFactory.CreateClient("AlexaSkill");
        string modelUrl = $"{SmapiEndpoint}/v1/skills/{skillId}/stages/{stage}/interactionModel/locales/{locale}";

        _logger.LogInformation("Fetching interaction model for skill {SkillId} locale {Locale}", skillId, locale);

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, modelUrl);
        getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var getResponse = await client.SendAsync(getRequest, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(getResponse, cancellationToken).ConfigureAwait(false);

        string modelJson = await getResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        string modifiedJson = InjectCatalogReferences(modelJson, artistCatalogId, albumCatalogId, artistCatalogVersion, albumCatalogVersion);

        _logger.LogInformation("Pushing updated interaction model for skill {SkillId} locale {Locale}", skillId, locale);

        using var putRequest = new HttpRequestMessage(HttpMethod.Put, modelUrl);
        putRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        putRequest.Content = new StringContent(modifiedJson, Encoding.UTF8, "application/json");

        using var putResponse = await client.SendAsync(putRequest, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(putResponse, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Interaction model update submitted for skill {SkillId} locale {Locale}", skillId, locale);
    }

    /// <summary>
    /// Injects catalog-backed slot type definitions into the interaction model.
    /// Uses JsonNode for efficient in-place mutation without serialize/deserialize round-trips.
    /// </summary>
    internal string InjectCatalogReferences(string modelJson, string? artistCatalogId, string? albumCatalogId, string? artistCatalogVersion, string? albumCatalogVersion)
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
            catalogMappings.Add((artistCatalogId!, artistCatalogVersion ?? "1",
                CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Artist],
                CatalogSlotTypes.Names[CatalogType.Artist]));
        }

        if (!string.IsNullOrEmpty(albumCatalogId))
        {
            catalogMappings.Add((albumCatalogId!, albumCatalogVersion ?? "1",
                CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Album],
                null));
        }

        _logger.LogInformation("Injecting {Count} catalog references into interaction model ({SlotTypes})",
            catalogMappings.Count, string.Join(", ", catalogMappings.Select(m => m.SlotTypeName)));

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
                _logger.LogInformation("Replacing slot type {SlotTypeName} (index {Index}) with catalog {CatalogId}",
                    slotTypeName, existingIndex, catalogId);
                typesArray[existingIndex] = catalogType;
            }
            else
            {
                _logger.LogInformation("Adding new catalog-backed slot type {SlotTypeName} with catalog {CatalogId}",
                    slotTypeName, catalogId);
                typesArray.Add(catalogType);
            }

            if (replacesType != null)
            {
                UpdateIntentSlotTypes(lmNode, replacesType, slotTypeName);
            }
        }

        return root.ToJsonString();
    }

    /// <summary>
    /// Updates all intent slot type references from <paramref name="oldType"/> to <paramref name="newType"/>.
    /// </summary>
    internal void UpdateIntentSlotTypes(JsonObject languageModel, string oldType, string newType)
    {
        var intentsArray = languageModel["intents"] as JsonArray;
        if (intentsArray == null)
        {
            return;
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

        _logger.LogInformation("Updated {Count} intent slot references from {OldType} to {NewType}",
            updatedCount, oldType, newType);
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

            string? status = pollDoc.RootElement.TryGetProperty("status", out var statusEl)
                ? statusEl.GetString()
                : null;

            _logger.LogDebug("{Operation} poll {Iteration}: status={Status}", operationName, i + 1, status);

            if (status == "SUCCEEDED")
            {
                string? version = pollDoc.RootElement.TryGetProperty("version", out var versionEl)
                    ? versionEl.GetString()
                    : null;
                return version;
            }

            if (status == "FAILED")
            {
                string reason = pollDoc.RootElement.TryGetProperty("errors", out var errors)
                    ? errors.GetRawText() : "unknown";
                throw new InvalidOperationException($"{operationName} failed: {reason}");
            }
        }

        _logger.LogWarning("{Operation} polling timed out at {Location}", operationName, location);
        throw new TimeoutException($"{operationName} polling timed out after 30 attempts at {location}");
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
        _logger.LogError("SMAPI request failed: {StatusCode} {ReasonPhrase}. Body: {Body}",
            (int)response.StatusCode, response.ReasonPhrase, errorBody);

        response.EnsureSuccessStatusCode();
    }
}
