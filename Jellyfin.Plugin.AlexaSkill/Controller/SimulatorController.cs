using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Jellyfin.Plugin.AlexaSkill.Controller;

/// <summary>
/// Controller for testing intents without an actual Alexa device.
/// Routes synthetic requests through the same handler pipeline as real Alexa requests.
/// </summary>
[ApiController]
[Route("Plugins/AlexaSkill/Simulator")]
public class SimulatorController : ControllerBase
{
    private readonly IEnumerable<BaseHandler> _handlers;
    private readonly RequestPipeline _pipeline;
    private readonly ILogger<SimulatorController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulatorController"/> class.
    /// </summary>
    /// <param name="handlers">The registered intent/event handlers.</param>
    /// <param name="pipeline">The request pipeline for interceptor-based request processing.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public SimulatorController(
        IEnumerable<BaseHandler> handlers,
        RequestPipeline pipeline,
        ILoggerFactory loggerFactory)
    {
        _handlers = handlers;
        _pipeline = pipeline;
        _logger = loggerFactory.CreateLogger<SimulatorController>();
    }

    /// <summary>
    /// Get the simulator status (enabled/disabled).
    /// </summary>
    /// <returns>A JSON object indicating whether the simulator is enabled.</returns>
    [HttpGet("Status")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult GetStatus()
    {
        bool enabled = Plugin.Instance?.Configuration.SimulatorEnabled ?? false;
        return new JsonResult(new { enabled });
    }

    /// <summary>
    /// Get the list of available intent names that can be tested.
    /// </summary>
    /// <returns>A JSON array of intent names.</returns>
    [HttpGet("Intents")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult GetIntents()
    {
        if (!IsSimulatorEnabled())
        {
            return NotFound(new { error = "Simulator is disabled. Enable it in plugin configuration." });
        }

        // Collect intent names from handlers by inspecting what intents they declare via CanHandle.
        // Instead of reflection, return a curated list from the known intent names.
        var intentNames = GetKnownIntentNames();
        return new JsonResult(new { intents = intentNames });
    }

    /// <summary>
    /// Execute an intent and return the full skill response.
    /// </summary>
    /// <param name="request">The simulator request containing intent name, slots, locale, and optional device ID.</param>
    /// <returns>The full skill response as JSON.</returns>
    [HttpPost("Intent")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> ExecuteIntent([FromBody] SimulatorRequest request)
    {
        if (!IsSimulatorEnabled())
        {
            return NotFound(new { error = "Simulator is disabled. Enable it in plugin configuration." });
        }

        if (string.IsNullOrWhiteSpace(request?.IntentName))
        {
            return BadRequest(new { error = "intentName is required." });
        }

        var config = Plugin.Instance!.Configuration;

        // Require at least one configured user to resolve library access
        var user = config.Users.FirstOrDefault();
        if (user == null)
        {
            return BadRequest(new { error = "No configured users found. Add a user in plugin configuration first." });
        }

        string locale = string.IsNullOrWhiteSpace(request.Locale) ? "en-US" : request.Locale;
        string deviceId = string.IsNullOrWhiteSpace(request.DeviceId) ? "simulator-device" : request.DeviceId;

        try
        {
            // Build a synthetic SkillRequest in Alexa format
            var skillRequest = BuildSkillRequest(request.IntentName, request.Slots, locale, deviceId, user.Id);

            // Find the matching handler
            BaseHandler? matchingHandler = null;
            foreach (BaseHandler h in _handlers)
            {
                if (h.CanHandle(skillRequest.Request))
                {
                    matchingHandler = h;
                    break;
                }
            }

            if (matchingHandler == null)
            {
                _logger.LogWarning("No handler found for intent: {IntentName}", request.IntentName);
                return new JsonResult(new
                {
                    error = $"No handler found for intent: {request.IntentName}",
                    availableIntents = GetKnownIntentNames()
                })
                {
                    StatusCode = 404
                };
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            _logger.LogInformation("Simulator executing intent {IntentName} with locale {Locale}", request.IntentName, locale);

            SkillResponse skillResponse = await _pipeline.ExecuteAsync(
                matchingHandler,
                skillRequest.Request,
                skillRequest.Context,
                skillRequest.Session,
                cts.Token).ConfigureAwait(false);

            string responseJson = JsonConvert.SerializeObject(skillResponse, Formatting.Indented);

            return Content(responseJson, "application/json");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Simulator request timed out for intent: {IntentName}", request.IntentName);
            return StatusCode(504, new { error = "Request timed out." });
        }
        catch (Exception ex)
        {
            string errorRef = Guid.NewGuid().ToString("N")[..8];
            _logger.LogError(ex, "Simulator error executing intent {IntentName} [ErrorRef:{ErrorRef}]", request.IntentName, errorRef);
            return StatusCode(500, new { error = $"Internal error. Reference: {errorRef}" });
        }
    }

    /// <summary>
    /// Check if the simulator is enabled in plugin configuration.
    /// </summary>
    /// <returns>True if enabled, false otherwise.</returns>
    private bool IsSimulatorEnabled()
    {
        return Plugin.Instance?.Configuration.SimulatorEnabled ?? false;
    }

    /// <summary>
    /// Build a synthetic Alexa SkillRequest by constructing JSON and deserializing.
    /// This avoids namespace collisions between Alexa.NET types and plugin entity types.
    /// </summary>
    /// <param name="intentName">The intent name to invoke.</param>
    /// <param name="slots">Optional slot key-value pairs.</param>
    /// <param name="locale">The locale (e.g. "en-US").</param>
    /// <param name="deviceId">A synthetic device identifier.</param>
    /// <param name="userId">The plugin user ID to use as the access token.</param>
    /// <returns>A fully constructed SkillRequest.</returns>
    private static SkillRequest BuildSkillRequest(
        string intentName,
        Dictionary<string, string>? slots,
        string locale,
        string deviceId,
        Guid userId)
    {
        string requestId = $"simulator-{Guid.NewGuid():N}";
        string sessionId = $"simulator-session-{Guid.NewGuid():N}";
        string timestamp = DateTime.UtcNow.ToString("o");

        // Build slot JSON
        string slotsJson = "{}";
        if (slots != null && slots.Count > 0)
        {
            var slotParts = new List<string>();
            foreach (var kvp in slots)
            {
                slotParts.Add($"\"{kvp.Key}\": {{\"name\": \"{kvp.Key}\", \"value\": \"{EscapeJson(kvp.Value)}\"}}");
            }

            slotsJson = "{" + string.Join(",", slotParts) + "}";
        }

        string json = $$"""
        {
            "version": "1.0",
            "session": {
                "new": true,
                "sessionId": "{{sessionId}}",
                "application": {
                    "applicationId": "amzn1.ask.skill.simulator"
                },
                "user": {
                    "userId": "amzn1.ask.account.simulator",
                    "accessToken": "{{userId}}"
                },
                "attributes": {}
            },
            "context": {
                "System": {
                    "application": {
                        "applicationId": "amzn1.ask.skill.simulator"
                    },
                    "user": {
                        "userId": "amzn1.ask.account.simulator",
                        "accessToken": "{{userId}}"
                    },
                    "device": {
                        "deviceId": "{{EscapeJson(deviceId)}}",
                        "supportedInterfaces": {}
                    },
                    "apiEndpoint": "https://api.amazonalexa.com",
                    "apiAccessToken": "simulator-token"
                },
                "AudioPlayer": {
                    "token": "",
                    "offsetInMilliseconds": 0,
                    "playerActivity": "IDLE"
                }
            },
            "request": {
                "type": "IntentRequest",
                "requestId": "{{requestId}}",
                "timestamp": "{{timestamp}}",
                "locale": "{{locale}}",
                "intent": {
                    "name": "{{EscapeJson(intentName)}}",
                    "slots": {{slotsJson}}
                }
            }
        }
        """;

        return JsonConvert.DeserializeObject<SkillRequest>(json)
            ?? throw new InvalidOperationException("Failed to deserialize synthetic skill request");
    }

    /// <summary>
    /// Escape a string for safe inclusion in a JSON string literal.
    /// </summary>
    private static string EscapeJson(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
    }

    /// <summary>
    /// Return the known list of intent names that can be simulated.
    /// </summary>
    /// <returns>A sorted list of intent name strings.</returns>
    private static List<string> GetKnownIntentNames()
    {
        return new List<string>
        {
            // Custom intents
            "MarkFavoriteIntent",
            "UnmarkFavoriteIntent",
            "MediaInfoIntent",
            "PlayFavoritesIntent",
            "PlayAlbumIntent",
            "PlayArtistSongsIntent",
            "PlayChannelIntent",
            "PlayIntent",
            "PlayLastAddedIntent",
            "PlayPlaylistIntent",
            "PlaySongIntent",
            "PlayVideoIntent",
            "PlayRandomIntent",
            "PlayByGenreIntent",
            "PlayByDecadeIntent",
            "PlayMoodMusicIntent",
            "ContinueWatchingIntent",
            "GoToChapterIntent",
            "InProgressMediaListIntent",
            "BrowseLibraryIntent",
            "RecommendIntent",
            "SleepTimerIntent",
            "PlayEpisodeIntent",
            "LoopSongOnIntent",
            "AddToQueueIntent",
            "PlayNextIntent",
            "ClearQueueIntent",
            "ListQueueIntent",
            "PlayRadioIntent",
            "TurnRadioOnIntent",
            "TurnRadioOffIntent",
            "LearnMyVoiceIntent",
            "WhoAmIIntent",
            "QueryArtistLibraryIntent",
            "PlayPodcastIntent",
            "SearchMediaIntent",
            "SetReminderIntent",
            "QueryRecentlyAddedIntent",

            // Built-in Amazon intents
            "AMAZON.FallbackIntent",
            "AMAZON.LoopOffIntent",
            "AMAZON.LoopOnIntent",
            "AMAZON.NextIntent",
            "AMAZON.PauseIntent",
            "AMAZON.StopIntent",
            "AMAZON.CancelIntent",
            "AMAZON.PreviousIntent",
            "AMAZON.ResumeIntent",
            "AMAZON.ShuffleOffIntent",
            "AMAZON.ShuffleOnIntent",
            "AMAZON.StartOverIntent",
            "AMAZON.YesIntent",
            "AMAZON.NoIntent"
        };
    }
}

/// <summary>
/// Request body for the simulator Intent endpoint.
/// </summary>
public class SimulatorRequest
{
    /// <summary>
    /// Gets or sets the intent name to execute (e.g. "PlaySongIntent").
    /// </summary>
    [JsonProperty("intentName")]
    public string? IntentName { get; set; }

    /// <summary>
    /// Gets or sets the slot values as key-value pairs (e.g. { "songName": "Bohemian Rhapsody" }).
    /// </summary>
    [JsonProperty("slots")]
    public Dictionary<string, string>? Slots { get; set; }

    /// <summary>
    /// Gets or sets the locale for the request (defaults to "en-US").
    /// </summary>
    [JsonProperty("locale")]
    public string? Locale { get; set; }

    /// <summary>
    /// Gets or sets a synthetic device ID (defaults to "simulator-device").
    /// </summary>
    [JsonProperty("deviceId")]
    public string? DeviceId { get; set; }
}
