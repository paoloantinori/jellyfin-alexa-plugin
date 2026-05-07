#pragma warning disable CS8618

using System;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Lwa;

namespace Jellyfin.Plugin.AlexaSkill.Entities;

/// <summary>
/// Represents a skill user.
/// </summary>
public class User
{
    /// <summary>
    /// Gets or sets Id of the user, equal to the Jellyfin id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets the username of the user, equal to the Jellyfin username.
    /// </summary>
    public string Username
    {
        get
        {
            if (Id == Guid.Empty || Plugin.Instance == null)
            {
                return string.Empty;
            }

            return Plugin.Instance.UserManager.GetUserById(Id)?.Username ?? string.Empty;
        }
    }

    /// <summary>
    /// Gets or sets the token for the Jellyfin API.
    /// </summary>
    [JsonIgnore]
    public string? JellyfinToken { get; set; }

    /// <summary>
    /// Gets or sets the device token for accessing SMAPI.
    /// </summary>
    [JsonIgnore]
    public DeviceToken? SmapiDeviceToken { get; set; }

    /// <summary>
    /// Gets or sets the persisted SMAPI refresh token for restart recovery.
    /// </summary>
    public string? SmapiRefreshToken { get; set; }

    /// <summary>
    /// Gets or sets the user skill.
    /// </summary>
    public UserSkill? UserSkill { get; set; }

    /// <summary>
    /// Gets or sets the skill status.
    /// </summary>
    public UserSkillStatus? UserSkillStatus { get; set; }

    /// <summary>
    /// Gets or sets the invocation name.
    /// </summary>
    public string InvocationName { get; set; }

    /// <summary>
    /// Gets or sets the Alexa person ID for voice-based user identification.
    /// When a voice profile is recognized, this maps the speaker to this Jellyfin user.
    /// </summary>
    public string? AlexaPersonId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user has opted into proactive event notifications.
    /// Set to true when the skill receives a <c>ProactiveSubscriptionChangedRequest</c>
    /// indicating the user subscribed via the Alexa app.
    /// </summary>
    public bool ProactiveEventsEnabled { get; set; }

    /// <summary>
    /// Gets or sets the SMAPI catalog ID for the user's artist library.
    /// Null if catalog has not been created yet.
    /// </summary>
    public string? ArtistCatalogId { get; set; }

    /// <summary>
    /// Gets or sets the SMAPI catalog ID for the user's album library.
    /// Null if catalog has not been created yet.
    /// </summary>
    public string? AlbumCatalogId { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the last successful catalog sync.
    /// </summary>
    public DateTime? LastCatalogSync { get; set; }

    /// <summary>
    /// Gets the smapi Smapi Management object for this user.
    /// </summary>
    [JsonIgnore]
    public SmapiManagement? SmapiManagement
    {
        get
        {
            if (this.SmapiDeviceToken == null)
            {
                return null;
            }

            return new SmapiManagement(this.SmapiDeviceToken, Plugin.Instance!.LoggerFactory);
        }
    }
}