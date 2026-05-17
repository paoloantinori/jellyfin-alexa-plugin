#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET.Management;
using global::Alexa.NET.Management.Api;
using global::Alexa.NET.Management.Skills;
using Jellyfin.Plugin.AlexaSkill.Alexa.InteractionModel;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.ModelDeployment;

/// <summary>
/// Manages deployment of custom interaction models via SMAPI.
/// Supports validating, fetching, deploying, and restoring Alexa interaction models.
/// </summary>
public class ModelDeploymentManager
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ModelDeploymentManager> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelDeploymentManager"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory for creating named clients.</param>
    /// <param name="logger">The logger instance.</param>
    public ModelDeploymentManager(IHttpClientFactory httpClientFactory, ILogger<ModelDeploymentManager> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Validates interaction model JSON structure.
    /// Checks for required fields: invocationName and at least one intent.
    /// If the JSON is not wrapped in an "interactionModel" envelope, attempts to wrap it.
    /// </summary>
    /// <param name="json">The raw interaction model JSON string to validate.</param>
    /// <returns>A <see cref="ModelValidationResult"/> with validation outcome details.</returns>
    public ModelValidationResult ValidateModelJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new ModelValidationResult(false, "JSON is empty or whitespace.", 0, string.Empty);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return new ModelValidationResult(false, $"Invalid JSON: {ex.Message}", 0, string.Empty);
        }

        JsonElement root = doc.RootElement;

        // If top level is not wrapped in "interactionModel", try wrapping it.
        // Some users may provide just the languageModel portion directly.
        JsonElement interactionModel;
        if (root.TryGetProperty("interactionModel", out var imElement))
        {
            interactionModel = imElement;
        }
        else if (root.TryGetProperty("languageModel", out _))
        {
            // The root itself looks like a language model — wrapping hint but not required for validation.
            interactionModel = root;
        }
        else
        {
            return new ModelValidationResult(false,
                "JSON must contain 'interactionModel' or 'languageModel' at the top level.", 0, string.Empty);
        }

        // Check for languageModel
        if (!interactionModel.TryGetProperty("languageModel", out var languageModel))
        {
            return new ModelValidationResult(false,
                "'interactionModel.languageModel' is missing.", 0, string.Empty);
        }

        // Check invocationName
        string invocationName = string.Empty;
        if (languageModel.TryGetProperty("invocationName", out var invocationNameEl))
        {
            invocationName = invocationNameEl.GetString() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(invocationName))
        {
            return new ModelValidationResult(false,
                "'interactionModel.languageModel.invocationName' is missing or empty.", 0, string.Empty);
        }

        // Check intents array
        if (!languageModel.TryGetProperty("intents", out var intents))
        {
            return new ModelValidationResult(false,
                "'interactionModel.languageModel.intents' is missing.", 0, invocationName);
        }

        int intentCount = 0;
        if (intents.ValueKind == JsonValueKind.Array)
        {
            intentCount = intents.GetArrayLength();
        }

        if (intentCount < 1)
        {
            return new ModelValidationResult(false,
                "'interactionModel.languageModel.intents' must contain at least 1 intent.", 0, invocationName);
        }

        return new ModelValidationResult(true, string.Empty, intentCount, invocationName);
    }

    /// <summary>
    /// Fetches interaction model JSON from a URL.
    /// </summary>
    /// <param name="url">The URL to fetch the model JSON from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The raw JSON string from the URL.</returns>
    /// <exception cref="ArgumentException">Thrown when the URL is null or empty.</exception>
    /// <exception cref="HttpRequestException">Thrown when the HTTP request fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the response body is empty.</exception>
    public async Task<string> FetchModelJsonAsync(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("URL cannot be null or empty.", nameof(url));
        }

        _logger.LogInformation("Fetching interaction model from {Url}", url);

        var client = _httpClientFactory.CreateClient("AlexaSkill");

        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Failed to fetch model from {url}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}. Body: {errorBody}");
        }

        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException($"Response body from {url} is empty.");
        }

        _logger.LogInformation("Fetched {CharCount} characters from {Url}", json.Length, url);
        return json;
    }

    /// <summary>
    /// Deploys a custom interaction model to SMAPI for the specified locale.
    /// </summary>
    /// <param name="modelJson">The interaction model JSON to deploy.</param>
    /// <param name="locale">The target locale (e.g. "en-US").</param>
    /// <param name="user">The user whose SMAPI credentials to use.</param>
    /// <param name="skillId">The skill ID to update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="ModelDeploymentResult"/> with deployment outcome details.</returns>
    public async Task<ModelDeploymentResult> DeployCustomModelAsync(
        string modelJson,
        string locale,
        User user,
        string skillId,
        CancellationToken cancellationToken)
    {
        if (user.SmapiDeviceToken == null)
        {
            return new ModelDeploymentResult(false, "User has no SMAPI device token.", string.Empty);
        }

        var smapi = user.SmapiManagement;
        if (smapi == null)
        {
            return new ModelDeploymentResult(false, "Failed to create SMAPI management instance.", string.Empty);
        }

        _logger.LogInformation(
            "Deploying custom interaction model for skill {SkillId} locale {Locale}",
            skillId, locale);

        try
        {
            // Parse the provided JSON and create a SkillInteractionModel.
            // The model JSON should contain the "interactionModel" envelope that SMAPI expects.
            var interactionModel = CreateSkillInteractionModel(modelJson, locale);

            await smapi.InteractionModel.Update(skillId, SkillStage.Development, locale, interactionModel)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Interaction model update submitted for skill {SkillId} locale {Locale}, waiting for build",
                skillId, locale);

            var finalStatus = await smapi.WaitForSkillStatusAsync(skillId, cancellationToken).ConfigureAwait(false);

            string status = finalStatus.Manifest.LastModified.Status.ToString();
            _logger.LogInformation(
                "Custom model deployed successfully for skill {SkillId} locale {Locale}, status: {Status}",
                skillId, locale, status);

            return new ModelDeploymentResult(true, "Deployment successful.", status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to deploy custom interaction model for skill {SkillId} locale {Locale}",
                skillId, locale);
            return new ModelDeploymentResult(false, $"Deployment failed: {ex.Message}", string.Empty);
        }
    }

    /// <summary>
    /// Restores the default (embedded) interaction model for the specified locale.
    /// </summary>
    /// <param name="locale">The target locale (e.g. "en-US").</param>
    /// <param name="user">The user whose SMAPI credentials to use.</param>
    /// <param name="skillId">The skill ID to update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="ModelDeploymentResult"/> with deployment outcome details.</returns>
    public async Task<ModelDeploymentResult> RestoreDefaultModelAsync(
        string locale,
        User user,
        string skillId,
        CancellationToken cancellationToken)
    {
        string defaultJson = GetDefaultModelJson(locale);
        if (string.IsNullOrWhiteSpace(defaultJson))
        {
            return new ModelDeploymentResult(false,
                $"No embedded interaction model found for locale '{locale}'.", string.Empty);
        }

        _logger.LogInformation(
            "Restoring default interaction model for skill {SkillId} locale {Locale}",
            skillId, locale);

        return await DeployCustomModelAsync(defaultJson, locale, user, skillId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the raw JSON of the default (embedded) interaction model for the specified locale.
    /// </summary>
    /// <param name="locale">The target locale (e.g. "en-US").</param>
    /// <returns>The raw JSON string of the embedded model, or null if not found.</returns>
    public string? GetDefaultModelJson(string locale)
    {
        var models = global::Jellyfin.Plugin.AlexaSkill.Util.GetLocalInteractionModels();
        var match = models.FirstOrDefault(m => m.Item1 == locale);
        if (match == null)
        {
            _logger.LogWarning("No embedded interaction model found for locale {Locale}", locale);
            return null;
        }

        var assembly = typeof(global::Jellyfin.Plugin.AlexaSkill.Util).Assembly;
        Stream? resource = assembly.GetManifestResourceStream(match.Item2);
        if (resource == null)
        {
            _logger.LogError(
                "Embedded resource '{ResourcePath}' not found for locale {Locale}",
                match.Item2, locale);
            return null;
        }

        using var reader = new StreamReader(resource);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Creates a <see cref="SkillInteractionModel"/> from raw JSON.
    /// Handles both wrapped ("interactionModel" envelope) and unwrapped formats.
    /// </summary>
    /// <param name="modelJson">The raw model JSON string.</param>
    /// <param name="locale">The locale for the model.</param>
    /// <returns>A populated <see cref="SkillInteractionModel"/>.</returns>
    private static SkillInteractionModel CreateSkillInteractionModel(string modelJson, string locale)
    {
        // Ensure the JSON has the "interactionModel" envelope that SkillInteraction expects.
        string wrappedJson = EnsureInteractionModelEnvelope(modelJson);

        // Deserialize directly from the JSON string instead of using the
        // SkillInteractionModel(locale, resourcePath, invocationName) constructor,
        // which relies on embedded resource streams and will not work with arbitrary JSON.
        var skillInteraction = Newtonsoft.Json.JsonConvert.DeserializeObject<SkillInteraction>(wrappedJson);
        if (skillInteraction == null)
        {
            throw new InvalidOperationException("Failed to deserialize interaction model JSON.");
        }

        var model = new SkillInteractionModel(locale, skillInteraction);
        return model;
    }

    /// <summary>
    /// Ensures the JSON has the "interactionModel" envelope required by SMAPI.
    /// If the JSON already has it, returns it as-is. Otherwise wraps it.
    /// </summary>
    /// <param name="modelJson">The raw model JSON string.</param>
    /// <returns>JSON with the "interactionModel" envelope.</returns>
    private static string EnsureInteractionModelEnvelope(string modelJson)
    {
        using var doc = JsonDocument.Parse(modelJson);
        if (doc.RootElement.TryGetProperty("interactionModel", out _))
        {
            return modelJson;
        }

        // Wrap: {"interactionModel": <existing content>}
        return $"{{\"interactionModel\":{modelJson}}}";
    }

    /// <summary>
    /// Gets a human-readable build status string from the skill status API.
}

/// <summary>
/// Result of interaction model JSON validation.
/// </summary>
/// <param name="IsValid">Whether the model JSON is valid.</param>
/// <param name="ErrorMessage">Error message if validation failed, empty if valid.</param>
/// <param name="IntentCount">Number of intents found in the model.</param>
/// <param name="InvocationName">The invocation name from the model.</param>
public record ModelValidationResult(bool IsValid, string ErrorMessage, int IntentCount, string InvocationName);

/// <summary>
/// Result of an interaction model deployment operation.
/// </summary>
/// <param name="Success">Whether the deployment succeeded.</param>
/// <param name="Message">Human-readable result message.</param>
/// <param name="BuildStatus">The final SMAPI build status, or empty on failure.</param>
public record ModelDeploymentResult(bool Success, string Message, string BuildStatus);
