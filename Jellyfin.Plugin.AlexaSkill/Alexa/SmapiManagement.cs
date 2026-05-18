using System;
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

        foreach (var interactionModel in interactionModels)
        {
            try
            {
                await this.InteractionModel.Update(skillId.Id, SkillStage.Development, interactionModel.Locale, interactionModel).ConfigureAwait(false);
                _logger.LogInformation("Interaction model updated for locale {Locale}", interactionModel.Locale);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update interaction model for locale {Locale}", interactionModel.Locale);
            }
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
    public async Task UpdateSkillAsync(string skillId, ManifestSkill manifestSkill, Collection<SkillInteractionModel> interactionModels)
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

        foreach (var interactionModel in interactionModels)
        {
            try
            {
                await this.InteractionModel.Update(skillId, SkillStage.Development, interactionModel.Locale, interactionModel).ConfigureAwait(false);
                _logger.LogInformation("Interaction model updated for locale {Locale}", interactionModel.Locale);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update interaction model for locale {Locale}", interactionModel.Locale);
            }
        }

        _logger.LogInformation("Skill updated successfully: {SkillId}", skillId);
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
    public async Task<SkillStatus> GetSkillStatusAsync(string skillId)
    {
        return await this.Skills.Status(skillId).ConfigureAwait(false);
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
        string expectedName = manifestSkill.Manifest.PublishingInformation?.Locales?.Values.FirstOrDefault()?.Name ?? string.Empty;
        if (string.IsNullOrEmpty(expectedName))
        {
            _logger.LogDebug("Cannot search for existing skill: manifest has no publishing name");
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

                string? skillName = nameByLocale.Values<string>().FirstOrDefault(n => n == expectedName);
                if (skillName != null)
                {
                    string? skillId = skill["skillId"]?.ToString();
                    _logger.LogInformation("Found existing skill matching name '{Name}': {SkillId}", expectedName, skillId);
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