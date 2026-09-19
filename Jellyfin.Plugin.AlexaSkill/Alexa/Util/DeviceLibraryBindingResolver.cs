#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Alexa.NET.Request;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// Applies the per-device default library binding (JF-327 V1, library-only) to the
/// resolved request user. Precedence, per Paolo's 2026-09-19 decision: a RECOGNIZED
/// voice profile wins over the device binding (the person speaking outranks the
/// room); the device binding wins over the plain linked-account behavior. The
/// binding INTERSECTS with the user's own <see cref="Entities.User.AllowedLibraryIds"/>: it
/// can restrict further but never grants access the user does not have (AC#4).
/// </summary>
public static class DeviceLibraryBindingResolver
{
    /// <summary>
    /// Returns the effective user for the request: unchanged when no binding
    /// applies, or a request-scoped copy restricted to the bound library.
    /// </summary>
    /// <param name="context">The Alexa request context (PersonId + DeviceId source).</param>
    /// <param name="user">The user resolved by the controller (linking, or profile fallback).</param>
    /// <param name="config">The plugin configuration holding the bindings.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The effective user (the same instance when unrestricted).</returns>
    public static Entities.User Apply(Context? context, Entities.User user, PluginConfiguration config, ILogger logger)
    {
        // A recognized voice profile outranks the room: skip the binding entirely.
        string? personId = context?.System?.Person?.PersonId;
        if (!string.IsNullOrEmpty(personId) && config.GetUserByPersonId(personId) != null)
        {
            return user;
        }

        string? deviceId = context?.System?.Device?.DeviceID;
        DeviceLibraryBinding? binding = config.GetDeviceLibraryBinding(deviceId);
        if (binding == null)
        {
            return user;
        }

        // The binding intersects with the user's own restrictions (AC#4): a user
        // already scoped to other libraries, or explicitly not to this one, stays
        // barred - the device cannot grant what the user does not have.
        var userLibraries = user.AllowedLibraryIds;
        bool userHasAccess = userLibraries == null || userLibraries.Count == 0
            || userLibraries.Contains(binding.LibraryId, StringComparer.OrdinalIgnoreCase);
        var effective = new List<string> { binding.LibraryId };
        if (!userHasAccess)
        {
            // No overlap: nothing accessible on this device for this user. An empty
            // AllowedLibraryIds list means "all libraries" (the null contract), so
            // this must stay NON-empty to express "nothing"; use an impossible id.
            effective = new List<string> { Guid.Empty.ToString() };
            logger.LogWarning(
                "Device {DeviceName} is bound to library {LibraryId} which user {Username} cannot access; requests from this device will find nothing",
                binding.DeviceName, binding.LibraryId, user.Username);
        }

        Entities.User scoped = CloneWithLibraries(user, effective);
        logger.LogDebug(
            "Device {DeviceName} bound to library {LibraryId}: user {Username} scoped for this request",
            binding.DeviceName, binding.LibraryId, user.Username);
        return scoped;
    }

    /// <summary>
    /// Device-only form for event paths (AudioPlayer callbacks) that carry the
    /// Jellyfin session but no Alexa context: no voice profile can be recognized
    /// on an event, so the binding decision needs only the device id. Null-safe
    /// in and out (the callers' config re-fetch may legitimately miss).
    /// </summary>
    /// <param name="deviceId">The device id from the session.</param>
    /// <param name="user">The user just re-fetched from config, or null.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The scoped user, the same instance when unbound, or null in/null out.</returns>
    public static Entities.User? ApplyByDevice(string? deviceId, Entities.User? user, PluginConfiguration config, ILogger logger)
    {
        if (user == null)
        {
            return null;
        }

        DeviceLibraryBinding? binding = config.GetDeviceLibraryBinding(deviceId);
        if (binding == null)
        {
            return user;
        }

        // Same intersection semantics as Apply; duplicated inline because Apply's
        // Context-first shape does not fit a session-only call site.
        var userLibraries = user.AllowedLibraryIds;
        bool userHasAccess = userLibraries == null || userLibraries.Count == 0
            || userLibraries.Contains(binding.LibraryId, StringComparer.OrdinalIgnoreCase);
        var effective = userHasAccess
            ? new List<string> { binding.LibraryId }
            : new List<string> { Guid.Empty.ToString() };
        if (!userHasAccess)
        {
            logger.LogWarning(
                "Device {DeviceName} is bound to library {LibraryId} which the user cannot access; nothing will be found",
                binding.DeviceName, binding.LibraryId);
        }

        Entities.User scoped = CloneWithLibraries(user, effective);
        logger.LogDebug("Device {DeviceName} bound to library {LibraryId}: user scoped for this event", binding.DeviceName, binding.LibraryId);
        return scoped;
    }

    /// <summary>
    /// Request-scoped shallow copy of the user with a replaced library list. The
    /// config-owned instance must never be mutated per request (it is the persisted
    /// configuration object). Reflection keeps the clone complete as User gains
    /// fields; the cost is microseconds against a per-voice-request budget.
    /// </summary>
    /// <param name="user">The resolved user.</param>
    /// <param name="libraries">The effective library ids.</param>
    /// <returns>The cloned user.</returns>
    private static Entities.User CloneWithLibraries(Entities.User user, List<string> libraries)
    {
        var clone = new Entities.User();
        foreach (var prop in typeof(Entities.User).GetProperties())
        {
            if (prop.CanRead && prop.CanWrite)
            {
                prop.SetValue(clone, prop.GetValue(user));
            }
        }

        clone.AllowedLibraryIds = libraries;
        return clone;
    }
}
