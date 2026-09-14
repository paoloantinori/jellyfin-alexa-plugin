using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Management;
using Alexa.NET.Management.AccountLinking;
using Alexa.NET.Management.Api;
using Alexa.NET.Management.Skills;
using Jellyfin.Plugin.AlexaSkill.Alexa.InteractionModel;
using Jellyfin.Plugin.AlexaSkill.Alexa.Manifest;
using Jellyfin.Plugin.AlexaSkill.Lwa;
using Microsoft.Extensions.Logging;
using Refit;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Util methods.
/// </summary>
public class SmapiManagement : ManagementApi
{
    private const int MaxPollRetries = 60;
    private readonly ILogger _logger;
    private readonly string _accessToken;

    /// <summary>
    /// Initializes a new instance of the <see cref="SmapiManagement"/> class.
    /// </summary>
    /// <param name="smapiDeviceToken">The smapi device token.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public SmapiManagement(DeviceToken smapiDeviceToken, ILoggerFactory loggerFactory) : base(smapiDeviceToken.AccessToken)
    {
        _logger = loggerFactory.CreateLogger<SmapiManagement>();
        _accessToken = smapiDeviceToken.AccessToken;
    }

    /// <summary>
    /// Gets the SMAPI vendor ID for the authenticated developer account.
    /// </summary>
    /// <returns>The vendor ID string.</returns>
    public async Task<string> GetVendorIdAsync()
    {
        VendorResponse vendor = await this.Vendors.Get().ConfigureAwait(false);
        return vendor.Vendors[0].Id;
    }

    /// <summary>
    /// Creates a new skill.
    /// </summary>
    /// <param name="manifestSkill">The manifest skill.</param>
    /// <param name="interactionModels">The interaction models.</param>
    /// <param name="endpointUri">The alexa api endpoint.</param>
    /// <param name="clientId">The client api which will be used in alexa requests to the api endpoint.</param>
    /// <returns>The id of the created skill.</returns>
    public async Task<string> CreateSkillAsync(ManifestSkill manifestSkill, Collection<SkillInteractionModel> interactionModels, string endpointUri, string clientId)
    {
        _logger.LogInformation("Creating new skill...");

        VendorResponse vendor = await this.Vendors.Get().ConfigureAwait(false);
        string vendorId = vendor.Vendors[0].Id;

        SkillId skillId = await this.Skills.Create(vendorId, manifestSkill).ConfigureAwait(false);
        _logger.LogInformation("Skill creation initiated: {SkillId}", skillId.Id);

        await WaitForSkillStatusAsync(skillId.Id).ConfigureAwait(false);

        _logger.LogInformation("Skill manifest processed, updating account linking and interaction models");

        this.UpdateAccountLinkData(skillId.Id, endpointUri, clientId);

        var failedLocales = await UpdateInteractionModelsAsync(skillId.Id, interactionModels).ConfigureAwait(false);

        if (failedLocales.Count > 0)
        {
            _logger.LogWarning("Skill created {SkillId} but {Failed}/{Total} interaction models failed to update",
                skillId.Id, failedLocales.Count, interactionModels.Count);
        }

        _logger.LogInformation("Skill created successfully: {SkillId}", skillId.Id);
        return skillId.Id;
    }

    /// <summary>
    /// Updates a skill.
    /// </summary>
    /// <param name="skillId">The id of the skill to update.</param>
    /// <param name="manifestSkill">The new manifest skill.</param>
    /// <param name="interactionModels">The new interaction models.</param>
    /// <returns>A task representing the async operation.</returns>
    public virtual async Task<Dictionary<string, string>> UpdateSkillAsync(string skillId, ManifestSkill manifestSkill, Collection<SkillInteractionModel> interactionModels)
    {
        _logger.LogInformation("Updating skill {SkillId}...", skillId);

        try
        {
            _ = await this.Skills.Update(skillId, SkillStage.Development, manifestSkill).ConfigureAwait(false);
        }
        catch (Refit.ApiException ex)
        {
            _logger.LogError(ex, "SMAPI skill update failed for {SkillId}: {StatusCode} — {Body}", skillId, (int)ex.StatusCode, ex.Content);
            throw;
        }

        await WaitForSkillStatusAsync(skillId).ConfigureAwait(false);

        var failedLocales = await UpdateInteractionModelsAsync(skillId, interactionModels).ConfigureAwait(false);

        _logger.LogInformation(
            "Skill updated: {SkillId} — {Success} of {Total} locales succeeded",
            skillId,
            interactionModels.Count - failedLocales.Count,
            interactionModels.Count);

        return failedLocales;
    }

    /// <summary>
    /// Updates all interaction models for a skill with retry and inter-locale spacing.
    /// Returns a dictionary of locale → error message for any that failed after retries.
    /// </summary>
    private async Task<Dictionary<string, string>> UpdateInteractionModelsAsync(
        string skillId, Collection<SkillInteractionModel> interactionModels)
    {
        var failedLocales = new Dictionary<string, string>(StringComparer.Ordinal);

        for (int i = 0; i < interactionModels.Count; i++)
        {
            var interactionModel = interactionModels[i];

            if (i > 0)
            {
                await Task.Delay(500).ConfigureAwait(false);
            }

            // JF-495: one greppable audit line per submitted model, before the first
            // attempt (not inside the retry loop). All current callers build these
            // models from the DLL-embedded resources.
            var (intentCount, sampleCount) = InteractionModelPutAudit.Count(interactionModel);
            InteractionModelPutAudit.LogModelPut(
                _logger,
                InteractionModelPutAudit.SourceEmbedded,
                interactionModel.Locale,
                skillId,
                intentCount,
                sampleCount);

            try
            {
                await RetryHelper.ExecuteWithRetryAsync(
                    async () =>
                    {
                        await PutLocaleModelPreservingWiringAsync(skillId, interactionModel.Locale, interactionModel).ConfigureAwait(false);
                        return (object?)null;
                    },
                    _logger,
                    $"InteractionModel.Update[{interactionModel.Locale}]",
                    maxRetries: 3,
                    initialDelayMs: 2000).ConfigureAwait(false);

                _logger.LogInformation("Interaction model updated for locale {Locale}", interactionModel.Locale);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update interaction model for locale {Locale} after retries", interactionModel.Locale);
                failedLocales[interactionModel.Locale] = ex.Message;
            }
        }

        return failedLocales;
    }

    /// <summary>
    /// Test seam (JF-366 pattern, as LwaClient.HttpClientOverrideForTests): when
    /// set, the raw model PUT/GET below uses the returned client instead of the
    /// shared one, so the wiring-preservation composition is testable without
    /// SMAPI. Evaluated per call, so each test can install its own handler.
    /// </summary>
    internal static Func<HttpClient>? RawModelClientOverrideForTests;

    /// <summary>
    /// PUTs one locale's interaction model as raw JSON, preserving the live
    /// model's catalog wiring (JF-552). The typed InteractionModel.Update cannot
    /// carry valueSupplier/valueCatalog (see CatalogWiring for the probed
    /// evidence), so every embedded-model PUT through this class used to
    /// downgrade catalog-backed slot types to the static seed until the next
    /// catalog sync. The graft re-applies the wiring the GET finds; a locale with
    /// no live wiring (first deploy, or never synced) PUTs unchanged. The
    /// extracted version can lag one sync if the GET races a catalog-sync build;
    /// catalog content is immutable per version, so that pins yesterday's values
    /// at worst.
    /// </summary>
    /// <param name="skillId">The skill being updated.</param>
    /// <param name="locale">The locale of the model.</param>
    /// <param name="model">The freshly built model to PUT.</param>
    /// <returns>A task representing the raw PUT.</returns>
    internal async Task PutLocaleModelPreservingWiringAsync(string skillId, string locale, SkillInteractionModel model)
    {
        string modelJson = Newtonsoft.Json.JsonConvert.SerializeObject(model);

        Catalog.CatalogWiring? wiring = null;
        if (Catalog.CatalogManager.IsCatalogWiringSupported(locale))
        {
            // No swallow here (JF-555): GetLiveModelJsonAsync returns null only for
            // "no live model" (404); every other GET failure must propagate so the
            // RetryHelper retries the whole GET+PUT and a persistent failure lands
            // the locale in failedLocales with its wired model left intact, instead
            // of silently PUTting unwired.
            wiring = Catalog.CatalogWiringGraft.ExtractWiring(await GetLiveModelJsonAsync(skillId, locale).ConfigureAwait(false));
        }

        string putJson = Catalog.CatalogWiringGraft.Apply(modelJson, locale, wiring, _logger);

        HttpClient client = RawModelClientOverrideForTests?.Invoke() ?? Plugin.HttpClient;
        using var putRequest = new HttpRequestMessage(HttpMethod.Put, Catalog.CatalogManager.LocaleModelUrl(skillId, "development", locale));
        putRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);
        putRequest.Content = new StringContent(putJson, System.Text.Encoding.UTF8, "application/json");

        using var putResponse = await client.SendAsync(putRequest).ConfigureAwait(false);
        if (!putResponse.IsSuccessStatusCode)
        {
            string body = await putResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new HttpRequestException(
                $"SMAPI interaction-model PUT for locale {locale} failed: {(int)putResponse.StatusCode} {putResponse.ReasonPhrase}. Body: {body}");
        }
    }

    /// <summary>
    /// GETs the locale's live interaction-model envelope. Null means "no live
    /// model exists" (404: first deploy). Any OTHER non-success status throws:
    /// a transient 429/5xx on this GET must NOT silently degrade the PUT to
    /// unwired (that reintroduces the JF-552 regression until the next sync,
    /// invisibly); throwing feeds the RetryHelper wrapper, which retries the
    /// whole GET+PUT, and a persistent failure lands the locale in
    /// failedLocales with its old wired model left intact (JF-555).
    /// JF-554 AC#3: the read is preceded by the JF-495 settle-wait, so a
    /// concurrently-pending catalog-sync build cannot make the graft pin the
    /// previous sync's catalog version.
    /// </summary>
    /// <param name="skillId">The skill.</param>
    /// <param name="locale">The locale.</param>
    /// <returns>The live model JSON, or null when none exists yet.</returns>
    internal async Task<string?> GetLiveModelJsonAsync(string skillId, string locale)
    {
        HttpClient client = RawModelClientOverrideForTests?.Invoke() ?? Plugin.HttpClient;
        await Catalog.CatalogManager.WaitForLocaleBuildToSettleAsync(
            _accessToken, client, skillId, locale, _logger, CancellationToken.None).ConfigureAwait(false);
        using var getRequest = Catalog.CatalogManager.CreateAuthorizedGet(
            Catalog.CatalogManager.LocaleModelUrl(skillId, "development", locale), _accessToken);

        using var getResponse = await client.SendAsync(getRequest).ConfigureAwait(false);
        if (getResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogDebug("No live model for locale {Locale} (404); nothing to graft", locale);
            return null;
        }

        if (!getResponse.IsSuccessStatusCode)
        {
            string body = await getResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new HttpRequestException(
                $"SMAPI interaction-model GET for locale {locale} failed: {(int)getResponse.StatusCode} {getResponse.ReasonPhrase}. Body: {body}");
        }

        return await getResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the skill from the Alexa cloud.
    /// Returns null if the manifest cannot be deserialized (e.g. unknown enum values in events).
    /// </summary>
    /// <param name="skillId">The id of the skill to get.</param>
    /// <returns>The skill, or null if deserialization fails.</returns>
    public async Task<ManifestSkill?> GetSkillAsync(string skillId)
    {
        _logger.LogDebug("Getting skill {SkillId}", skillId);

        try
        {
            var skillResponse = await this.Skills.Get(skillId, SkillStage.Development).ConfigureAwait(false);
            return new ManifestSkill(skillResponse.Manifest);
        }
        catch (Exception ex) when (ex is Newtonsoft.Json.JsonSerializationException
            || (ex.InnerException is Newtonsoft.Json.JsonSerializationException))
        {
            _logger.LogWarning(
                ex,
                "Failed to deserialize skill {SkillId} manifest from cloud. " +
                "This is typically caused by an unrecognized Alexa event type. " +
                "Skipping cloud manifest fetch and using local manifest.",
                skillId);
            return null;
        }
        catch (Refit.ApiException ex)
        {
            _logger.LogWarning(
                "SMAPI call to get skill {SkillId} returned {StatusCode} — will retry with token refresh if available",
                skillId, (int)ex.StatusCode);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error getting skill {SkillId}", skillId);
            throw;
        }
    }

    /// <summary>
    /// Deletes a skill.
    /// </summary>
    /// <param name="skillId">The id of the skill to delete.</param>
    /// <returns>A task representing the async operation.</returns>
    public async Task DeleteSkillAsync(string skillId)
    {
        _logger.LogInformation("Deleting skill {SkillId}...", skillId);

        try
        {
            await this.Skills.Delete(skillId).ConfigureAwait(false);
            _logger.LogInformation("Skill deleted: {SkillId}", skillId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete skill {SkillId}", skillId);
            throw;
        }
    }

    /// <summary>
    /// Gets the AccountLink data.
    /// </summary>
    /// <param name="skillId">The id of the skill to get the AccountLinking data from.</param>
    /// <returns>The AccountLinking data.</returns>
    public async Task<AccountLinkData> GetAccountLinkDataAsync(string skillId)
    {
        _logger.LogDebug("Getting account link data for skill {SkillId}", skillId);

        try
        {
            return await this.AccountLinking.Get(skillId, SkillStage.Development).ConfigureAwait(false);
        }
        catch (Refit.ApiException ex)
        {
            _logger.LogWarning(
                "SMAPI call to get account link data for skill {SkillId} returned {StatusCode}",
                skillId, (int)ex.StatusCode);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error getting account link data for skill {SkillId}", skillId);
            throw;
        }
    }

    /// <summary>
    /// Updates the AccountLink data.
    /// </summary>
    /// <param name="skillId">The id of the skill to update the AccountLinking data from.</param>
    /// <param name="endpointUri">The endpoint uri.</param>
    /// <param name="clientId">The client id.</param>
    public void UpdateAccountLinkData(string skillId, string endpointUri, string clientId)
    {
        _logger.LogDebug("Updating account link data for skill {SkillId}", skillId);

        AccountLinkData accountLinkData = new AccountLinkData()
        {
            Type = AccountLinkType.IMPLICIT,
            AuthorizationUrl = endpointUri,
            ClientId = clientId,
        };
        this.AccountLinking.Update(skillId, accountLinkData);
    }

    /// <summary>
    /// Polls skill status until it transitions out of IN_PROGRESS.
    /// Returns the final skill status.
    /// </summary>
    /// <param name="skillId">The skill ID to poll.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<SkillStatus> WaitForSkillStatusAsync(string skillId, CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < MaxPollRetries; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var status = await GetSkillStatusAsync(skillId).ConfigureAwait(false);
            if (status.Manifest.LastModified.Status != SkillStatusState.IN_PROGRESS)
            {
                return status;
            }

            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Skill {skillId} did not transition from IN_PROGRESS within {MaxPollRetries} seconds");
    }

    /// <summary>
    /// Gets skill status.
    /// </summary>
    /// <param name="skillId">The id of the skill.</param>
    /// <returns>The skill status.</returns>
    public virtual async Task<SkillStatus> GetSkillStatusAsync(string skillId)
    {
        return await this.Skills.Status(skillId).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the live interaction model for one locale of the development-stage skill.
    /// Used by the post-deploy canary (JF-495) to verify the live model still
    /// carries the counts that were submitted. Virtual so tests can fake the
    /// GET without network, matching the other seam methods.
    /// </summary>
    /// <param name="skillId">The id of the skill.</param>
    /// <param name="locale">The locale whose model to fetch (e.g. "it-IT").</param>
    /// <returns>The deserialized live interaction model.</returns>
    public virtual Task<SkillInteractionContainer> GetInteractionModelAsync(string skillId, string locale)
    {
        return this.InteractionModel.Get(skillId, SkillStage.Development, locale);
    }

    /// <summary>
    /// Searches for an existing skill in the developer's account by matching
    /// the skill name against the manifest's publishing name.
    /// Returns the skill ID if found, or null.
    /// </summary>
    /// <param name="manifestSkill">The manifest whose name to search for.</param>
    /// <returns>The existing skill ID, or null if no match found.</returns>
    public async Task<string?> FindExistingSkillAsync(ManifestSkill manifestSkill)
    {
        // JF-513.3: the expected name is pinned to en-US (a stable locale) instead of
        // FirstOrDefault, which is JSON insertion-order dependent - JF-513 moved ar-SA
        // to the front, and a future localized ar-SA display name would have matched
        // nothing in nameByLocale and created a DUPLICATE skill. Matching is against
        // the en-US entry of nameByLocale below, symmetrically.
        string expectedName = manifestSkill.Manifest.PublishingInformation?.Locales?
            .TryGetValue("en-US", out var enInfo) == true ? enInfo.Name ?? string.Empty : string.Empty;
        if (string.IsNullOrEmpty(expectedName))
        {
            _logger.LogDebug("Cannot search for existing skill: manifest has no en-US publishing name");
            return null;
        }

        try
        {
            string vendorId = await GetVendorIdAsync().ConfigureAwait(false);

            var httpClient = Plugin.HttpClient;
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.amazonalexa.com/v1/skills?vendorId={vendorId}");
            request.Headers.Add("Authorization", $"Bearer {_accessToken}");
            var response = await httpClient.SendAsync(request).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Skill listing returned {StatusCode}, skipping reuse check", (int)response.StatusCode);
                return null;
            }

            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var data = Newtonsoft.Json.Linq.JObject.Parse(json);
            var skills = data["skills"] as Newtonsoft.Json.Linq.JArray;
            if (skills == null)
            {
                return null;
            }

            foreach (var skill in skills)
            {
                var nameByLocale = skill["nameByLocale"] as Newtonsoft.Json.Linq.JObject;
                if (nameByLocale == null)
                {
                    continue;
                }

                if (nameByLocale!.TryGetValue("en-US", out var enName) && enName?.ToString() == expectedName)
                {
                    string? skillId = skill["skillId"]?.ToString();
                    _logger.LogInformation("Found existing skill matching en-US name '{Name}': {SkillId}", expectedName, skillId);
                    return skillId;
                }
            }

            _logger.LogDebug("No existing skill found with name '{Name}'", expectedName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to search for existing skills, will create new");
            return null;
        }
    }
}