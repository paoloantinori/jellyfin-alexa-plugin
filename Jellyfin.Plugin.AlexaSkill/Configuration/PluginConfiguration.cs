using System;
using System.Collections.Generic;
using Alexa.NET.Management;
using Jellyfin.Plugin.AlexaSkill.Alexa.Manifest;
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
        LwaClientId = string.Empty;
        LwaClientSecret = string.Empty;

        serverAddress = string.Empty;
        AccountLinkingClientId = Guid.NewGuid().ToString();

        Users = new List<User>();
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
    /// Gets or sets the client id for LWA.
    /// </summary>
    public string LwaClientId { get; set; }

    /// <summary>
    /// Gets or sets the client secret for LWA.
    /// </summary>
    public string LwaClientSecret { get; set; }

    /// <summary>
    /// Gets or sets the account linking client id.
    /// </summary>
    public string AccountLinkingClientId { get; set; }

    /// <summary>
    /// Gets or sets the list of users.
    /// </summary>
    public List<User> Users { get; set; }

    /// <summary>
    /// Validate the configuration and return a list of error messages.
    /// </summary>
    /// <returns>A list of validation error messages. Empty if valid.</returns>
    public List<string> Validate()
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

        if (Plugin.Instance.ManifestSkill == null)
        {
            Plugin.Instance.ManifestSkill = new ManifestSkill("Jellyfin.Plugin.AlexaSkill.Alexa.Manifest.manifest.json", serverAddress, sslCertType);
        }
        else
        {
            Plugin.Instance.ManifestSkill.SetApiEndpoint(serverAddress, sslCertType);
        }
    }

    /// <summary>
    /// Add a user to the list of users.
    /// </summary>
    /// <param name="user">The user to add.</param>
    public void AddUser(User user)
    {
        // check if the user is already inside the list
        foreach (User u in Users)
        {
            if (user.Id == u.Id)
            {
                throw new ArgumentException("User already inside list");
            }
        }

        Users.Add(user);
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
        foreach (User u in Users)
        {
            if (guid == u.Id)
            {
                return Users.Remove(u);
            }
        }

        return false;
    }
}