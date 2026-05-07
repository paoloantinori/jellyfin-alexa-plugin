#nullable enable

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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

    private static readonly JsonSerializerOptions JsonOptions = new()
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
    /// <param name="slotTypeSignature">
    /// The slot type signature this catalog backs (e.g. "AMAZON.Musician" or a custom type name).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created catalog ID.</returns>
    public async Task<string> CreateCatalogAsync(
        string accessToken,
        string vendorId,
        string catalogName,
        string description,
        string slotTypeSignature,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Creating SMAPI catalog '{CatalogName}' with slot type signature '{SlotTypeSignature}'",
            catalogName, slotTypeSignature);

        var client = _httpClientFactory.CreateClient("AlexaSkill");

        var body = new
        {
            catalog = new
            {
                name = catalogName,
                description,
                slotTypeSignature,
                vendorId
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

        string catalogId = doc.RootElement.GetProperty("catalog").GetProperty("id").GetString()
            ?? throw new InvalidOperationException($"Catalog creation response missing catalog ID. Response: {json}");

        _logger.LogInformation("Catalog created successfully: {CatalogId}", catalogId);
        return catalogId;
    }

    /// <summary>
    /// Uploads a set of values to a catalog via the presigned-URL flow.
    /// SMAPI flow: create version -> get upload URL -> PUT JSON -> commit version.
    /// </summary>
    /// <param name="accessToken">The SMAPI access token.</param>
    /// <param name="catalogId">The target catalog ID.</param>
    /// <param name="payload">The catalog values payload to upload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The committed version string.</returns>
    public async Task<string> UploadCatalogValuesAsync(
        string accessToken,
        string catalogId,
        CatalogPayload payload,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Uploading {ValueCount} values to catalog {CatalogId}",
            payload.Values.Count, catalogId);

        var client = _httpClientFactory.CreateClient("AlexaSkill");

        // Step 1: Create a new catalog version to get the presigned upload URL
        var versionBody = new
        {
            description = $"Library sync {DateTime.UtcNow:O}"
        };

        using var versionRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{SmapiEndpoint}/v1/skills/api/custom/interactionModel/catalogs/{catalogId}/versions");
        versionRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        versionRequest.Content = JsonContent.Create(versionBody, options: JsonOptions);

        using var versionResponse = await client.SendAsync(versionRequest, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(versionResponse, cancellationToken).ConfigureAwait(false);

        string versionJson = await versionResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var versionDoc = JsonDocument.Parse(versionJson);

        string? uploadUrl = versionDoc.RootElement.GetProperty("uploadUrl").GetString();
        string version = versionDoc.RootElement.GetProperty("version").GetString() ?? "1";

        if (string.IsNullOrEmpty(uploadUrl))
        {
            throw new InvalidOperationException(
                $"Catalog version creation returned no upload URL. Response: {versionJson}");
        }

        _logger.LogDebug("Created catalog version {Version}, uploading to presigned URL", version);

        // Step 2: Upload the catalog JSON to the presigned URL
        string payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        using var uploadRequest = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
        uploadRequest.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

        using var uploadResponse = await client.SendAsync(uploadRequest, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(uploadResponse, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Upload complete, committing version {Version}", version);

        // Step 3: Commit the version
        using var commitRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{SmapiEndpoint}/v1/skills/api/custom/interactionModel/catalogs/{catalogId}/versions/{version}");
        commitRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var commitResponse = await client.SendAsync(commitRequest, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(commitResponse, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Catalog {CatalogId} version {Version} committed successfully", catalogId, version);
        return version;
    }

    /// <summary>
    /// Creates a custom slot type backed by a catalog.
    /// The slot type will be dynamically populated from the catalog values.
    /// </summary>
    /// <param name="accessToken">The SMAPI access token.</param>
    /// <param name="vendorId">The vendor ID for the skill owner.</param>
    /// <param name="slotTypeName">The name for the new slot type (e.g. "JELLYFIN_ARTIST").</param>
    /// <param name="catalogId">The catalog ID that supplies values.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task CreateSlotTypeAsync(
        string accessToken,
        string vendorId,
        string slotTypeName,
        string catalogId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Creating slot type '{SlotTypeName}' referencing catalog {CatalogId}",
            slotTypeName, catalogId);

        var client = _httpClientFactory.CreateClient("AlexaSkill");

        var body = new
        {
            slotType = new
            {
                name = slotTypeName,
                vendorId,
                values = Array.Empty<object>(),
                catalogValueSupplier = new { catalogId }
            }
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{SmapiEndpoint}/v1/skills/api/custom/interactionModel/slotTypes");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(body, options: JsonOptions);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Slot type '{SlotTypeName}' created successfully", slotTypeName);
    }

    /// <summary>
    /// Updates an existing slot type to reference a catalog.
    /// </summary>
    /// <param name="accessToken">The SMAPI access token.</param>
    /// <param name="slotTypeName">The name of the existing slot type to update.</param>
    /// <param name="catalogId">The catalog ID that supplies values.</param>
    /// <param name="etag">Optional ETag for optimistic concurrency control.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task UpdateSlotTypeAsync(
        string accessToken,
        string slotTypeName,
        string catalogId,
        string? etag,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Updating slot type '{SlotTypeName}' to reference catalog {CatalogId}",
            slotTypeName, catalogId);

        var client = _httpClientFactory.CreateClient("AlexaSkill");

        var body = new
        {
            slotType = new
            {
                name = slotTypeName,
                values = Array.Empty<object>(),
                catalogValueSupplier = new { catalogId }
            }
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"{SmapiEndpoint}/v1/skills/api/custom/interactionModel/slotTypes/{slotTypeName}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        if (!string.IsNullOrEmpty(etag))
        {
            request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{etag}\""));
        }

        request.Content = JsonContent.Create(body, options: JsonOptions);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Slot type '{SlotTypeName}' updated successfully", slotTypeName);
    }

    /// <summary>
    /// Triggers an interaction model update after a catalog version change.
    /// This propagates catalog updates to the skill's interaction model so
    /// the new values are available for NLU resolution.
    /// </summary>
    /// <param name="accessToken">The SMAPI access token.</param>
    /// <param name="skillId">The skill ID whose model should be updated.</param>
    /// <param name="stage">The skill stage (e.g. "development").</param>
    /// <param name="locale">The locale to update (e.g. "en-US").</param>
    /// <param name="catalogId">The catalog ID that was updated.</param>
    /// <param name="version">The new catalog version.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task TriggerModelUpdateAsync(
        string accessToken,
        string skillId,
        string stage,
        string locale,
        string catalogId,
        string version,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Triggering interaction model update for skill {SkillId}, stage {Stage}, locale {Locale}, " +
            "catalog {CatalogId} version {Version}",
            skillId, stage, locale, catalogId, version);

        var client = _httpClientFactory.CreateClient("AlexaSkill");

        var body = new
        {
            update = new
            {
                type = "ReferencedResourceVersionUpdate",
                @params = new
                {
                    catalogId,
                    version
                }
            }
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{SmapiEndpoint}/v1/skills/{skillId}/stages/{stage}/interactionModel/locales/{locale}/update");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(body, options: JsonOptions);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Model update triggered successfully for skill {SkillId} locale {Locale}",
            skillId, locale);
    }

    /// <summary>
    /// Creates a slot type, or updates it if it already exists (HTTP 409).
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
        CancellationToken cancellationToken)
    {
        try
        {
            await CreateSlotTypeAsync(accessToken, vendorId, slotTypeName, catalogId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            _logger.LogInformation("Slot type '{SlotTypeName}' already exists, updating instead", slotTypeName);
            await UpdateSlotTypeAsync(accessToken, slotTypeName, catalogId, null, cancellationToken)
                .ConfigureAwait(false);
        }
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
