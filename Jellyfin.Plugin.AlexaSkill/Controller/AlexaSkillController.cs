using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler.Intent;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;
using Jellyfin.Plugin.AlexaSkill.Controller.Handler;
using Jellyfin.Plugin.AlexaSkill.Diagnostics;
using Jellyfin.Plugin.AlexaSkill.Entities;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using RequestVerification = Alexa.NET.Request.RequestVerification;

namespace Jellyfin.Plugin.AlexaSkill.Controller;

/// <summary>
/// Controller class for api requests.
/// </summary>
[ApiController]
[Route("alexaskill/api/")]
public class AlexaSkillController : ControllerBase
{
    /// <summary>
    /// Uri of the plugin api.
    /// </summary>
    public const string ApiBaseUri = "alexaskill/api/";

    private readonly IUserManager _userManager;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<AlexaSkillController> _logger;
    private readonly RequestCounters _counters;
    private readonly RequestPipeline _pipeline;
    private readonly IEnumerable<BaseHandler> _handlers;

    // Lazy-cached handler references used for session-aware routing.
    private FindSongIntentHandler? _findSongHandler;
    private readonly object _handlerCacheLock = new();

    private readonly CsrfTokenHandler _csrfTokenHandler;

    /// <summary>
    /// Initializes a new instance of the <see cref="AlexaSkillController"/> class.
    /// </summary>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILogger{RequestController}"/> interface.</param>
    /// <param name="counters">Request counters for metrics.</param>
    /// <param name="pipeline">The request pipeline for interceptor-based request processing.</param>
    /// <param name="handlers">The registered intent/event handlers.</param>
    public AlexaSkillController(
        IUserManager userManager,
        ISessionManager sessionManager,
        ILoggerFactory loggerFactory,
        RequestCounters counters,
        RequestPipeline pipeline,
        IEnumerable<BaseHandler> handlers)
    {
        _userManager = userManager;
        _sessionManager = sessionManager;
        _logger = loggerFactory.CreateLogger<AlexaSkillController>();
        _counters = counters;
        _pipeline = pipeline;
        _handlers = handlers;

        _csrfTokenHandler = Plugin.Instance!.CsrfTokenHandler;
    }

    /// <summary>
    /// Get the account linking html page.
    /// </summary>
    /// <param name="clientId">The client id of the skill account linking request.</param>
    /// <param name="redirectUri">The redirect uri of the skill account linking request.</param>
    /// <param name="state">The state of the skill account linking request.</param>
    /// <param name="error">The error of the skill account linking request.</param>
    /// <returns>The account linking html page.</returns>
    [HttpGet("account-linking")]
    public ActionResult GetAccountLinking(
        [FromQuery(Name = "client_id")] string clientId,
        [FromQuery(Name = "redirect_uri")] string redirectUri,
        [FromQuery(Name = "state")] string state,
        [FromQuery(Name = "error")] string? error)
    {
        if (string.IsNullOrWhiteSpace(redirectUri) || string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(clientId))
        {
            return BadRequest("Missing required parameters: client_id, redirect_uri, and state are required.");
        }

        bool valid_redirect_uri = false;
        foreach (string url in Config.ValidRedirectUrls)
        {
            if (redirectUri.StartsWith(url, StringComparison.Ordinal))
            {
                valid_redirect_uri = true;
                break;
            }
        }

        if (!valid_redirect_uri)
        {
            _logger.LogError("Invalid redirect uri: {RedirectUri}", redirectUri);

            return new BadRequestResult();
        }

        if (!clientId.Equals(Plugin.Instance!.Configuration.AccountLinkingClientId, StringComparison.Ordinal))
        {
            _logger.LogError("Invalid client id: {ClientId}", clientId);

            return new BadRequestResult();
        }

        LogAccountLinkingRegion(redirectUri, "page requested");

        var assembly = typeof(Util).Assembly;
        Stream? resource = assembly.GetManifestResourceStream("Jellyfin.Plugin.AlexaSkill.Controller.Pages.account_linking.html");

        if (resource == null)
        {
            return StatusCode(500);
        }

        string page = new StreamReader(resource).ReadToEnd();

        page = page.Replace("{{ csrf_token }}", _csrfTokenHandler.GetNewCsrfToken().Token, StringComparison.Ordinal);

        if (error != null)
        {
            page = page.Replace("{{ error }}", System.Net.WebUtility.HtmlEncode(error), StringComparison.Ordinal);
        }
        else
        {
            page = page.Replace("{{ error }}", string.Empty, StringComparison.Ordinal);
        }

        return Content(page, "text/html");
    }

    /// <summary>
    /// Post the Jellyfin username and passwort for the account linking html page.
    /// </summary>
    /// <param name="csrfToken">The CSRF token to verify the account linking request.</param>
    /// <param name="username">The username of the Jellyfin account.</param>
    /// <param name="password">The password of the Jellyfin account.</param>
    /// <param name="clientId">The client id of the skill account linking request.</param>
    /// <param name="redirectUri">The redirect uri of the skill account linking request.</param>
    /// <param name="state">The state of the skill account linking request.</param>
    /// <returns>The html page.</returns>
    [HttpPost("account-linking")]
    public async Task<ActionResult> PostAccountLinking(
        [FromForm(Name = "csrf_token")] string csrfToken,
        [FromForm(Name = "username")] string username,
        [FromForm(Name = "password")] string password,
        [FromQuery(Name = "client_id")] string clientId,
        [FromQuery(Name = "redirect_uri")] string redirectUri,
        [FromQuery(Name = "state")] string state)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return BadRequest("Username and password are required.");
        }

        if (!_csrfTokenHandler.ValidateCsrfToken(csrfToken))
        {
            return Unauthorized();
        }

        bool valid_redirect_uri = false;
        foreach (string url in Config.ValidRedirectUrls)
        {
            if (redirectUri.StartsWith(url, StringComparison.Ordinal))
            {
                valid_redirect_uri = true;
                break;
            }
        }

        if (!valid_redirect_uri)
        {
            _logger.LogError("Invalid redirect uri: {RedirectUri}", redirectUri);

            return new BadRequestResult();
        }

        if (!clientId.Equals(Plugin.Instance!.Configuration.AccountLinkingClientId, StringComparison.Ordinal))
        {
            _logger.LogError("Invalid client id: {ClientId}", clientId);

            return new BadRequestResult();
        }

        AuthenticationRequest authenticationRequest = new AuthenticationRequest();
        authenticationRequest.Username = username;
        authenticationRequest.Password = password;
        authenticationRequest.AppVersion = Util.GetVersion();
        authenticationRequest.App = "Alexa Skill";
        authenticationRequest.DeviceId = "AlexaDevice";
        authenticationRequest.DeviceName = "Alexa enabled device";

        AuthenticationResult authenticationResult;
        try
        {
            authenticationResult = await _sessionManager.AuthenticateNewSession(authenticationRequest).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MediaBrowser.Controller.Authentication.AuthenticationException)
        {
            _logger.LogError(ex, "Failed to authenticate user");

            return Redirect("account-linking?error=invalid credentials&client_id=" + clientId + "&redirect_uri=" + redirectUri + "&state=" + state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Something went wrong during authenticate the user");

            return Redirect("account-linking?error=unknown error&client_id=" + clientId + "&redirect_uri=" + redirectUri + "&state=" + state);
        }

        Entities.User? user = Plugin.Instance.Configuration.GetUserById(authenticationResult.User.Id);
        if (user == null)
        {
            return Redirect("account-linking?error=this user have no user skill&client_id=" + clientId + "&redirect_uri=" + redirectUri + "&state=" + state);
        }

        user.JellyfinToken = authenticationResult.AccessToken;
        user.TryTransitionToReady();
        Plugin.Instance!.SaveConfiguration();

        // Implicit grant: the access token is returned in the URL fragment per the Alexa
        // implicit-grant flow. token_type MUST be "Bearer" (OAuth2 / Amazon spec), not "token".
        // Fragment values are application/x-www-form-urlencoded (RFC 6749 §4.2.2): percent-encode
        // them so an opaque Amazon `state` containing reserved chars (#, &, +, =) can't corrupt
        // the fragment or break the CSRF round-trip. Escaping is a no-op for the common (safe) case.
        string urlParams = $"access_token={Uri.EscapeDataString(user.Id.ToString())}&state={Uri.EscapeDataString(state)}&token_type=Bearer";

        // Use a temporary (302) redirect, NOT a permanent (301) one. RedirectPermanent (301) is
        // cacheable by the Alexa in-app webview and can cause account-linking loops on retry; 302
        // is the OAuth-standard redirect status and is never cached. (GitHub issue #11)
        LogAccountLinkingRegion(redirectUri, "completed via implicit grant");

        return Redirect(redirectUri + "#" + urlParams);
    }

    /// <summary>
    /// Logs the Amazon region (host) of an account-linking redirect_uri. The host is chosen by
    /// Amazon based on the user's account marketplace — not the skill locale — and is the decisive
    /// signal when a region looks wrong (e.g. an es-MX skill bouncing to alexa.amazon.co.jp means
    /// the Amazon account is JP-marketplace). Logged at Information so it shows in default logs
    /// without enabling Debug. Region only — the bearer token (== user id) is deliberately omitted.
    /// (GitHub issue #11)
    /// </summary>
    /// <param name="redirectUri">The redirect_uri Amazon provided.</param>
    /// <param name="action">Short phrase describing the linking stage (e.g. "page requested", "completed via implicit grant").</param>
    private void LogAccountLinkingRegion(string redirectUri, string action)
    {
        string amazonRegion = "unknown";
        try
        {
            amazonRegion = new Uri(redirectUri).Host;
        }
        catch (UriFormatException)
        {
            // redirect_uri is validated as a prefix of a known Amazon URL, so it should always
            // parse; fall back to "unknown" defensively.
        }

        _logger.LogInformation("Account linking {Action}; Amazon region {AmazonRegion}", action, amazonRegion);
    }

    /// <summary>
    /// Handle a Alexa skill request.
    /// </summary>
    /// <returns>A <see cref="ActionResult"/>.</returns>
    [HttpPost("alexa-request")]
    [Consumes("application/json")]
    public async Task<ActionResult> HandleIntentRequest()
    {
        try
        {
            using var reader = new StreamReader(Request.Body);
            string body = await reader.ReadToEndAsync().ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(body))
            {
                _logger.LogWarning("Received empty request body");
                return SkillResponseContent(ResponseBuilder.Empty());
            }

            if (!await VerifyAlexaSignature(body, TimeSpan.FromSeconds(3)).ConfigureAwait(false))
            {
                _logger.LogWarning("Alexa request signature verification failed");
                return SkillResponseContent(ResponseBuilder.Tell("Unable to verify request authenticity."));
            }

            SkillRequest? req = JsonConvert.DeserializeObject<SkillRequest>(body);

            // JF-311: Reject requests outside the 150-second timestamp window (replay protection).
            if (req?.Request?.Timestamp is DateTime ts && Math.Abs((DateTime.UtcNow - ts).TotalSeconds) > 150)
            {
                _logger.LogWarning("Rejected stale Alexa request (timestamp={Timestamp}, age={Age:F0}s)", ts, (DateTime.UtcNow - ts).TotalSeconds);
                return SkillResponseContent(ResponseBuilder.Empty());
            }
            if (req?.Context?.System?.User?.AccessToken == null
                && string.IsNullOrEmpty(req?.Context?.System?.Person?.PersonId))
            {
                _logger.LogWarning("Invalid skill request: missing access token and person ID");
                return SkillResponseContent(ResponseBuilder.Tell("Unable to process your request. Please try linking your account again."));
            }

            if (!Guid.TryParse(req.Context.System.User?.AccessToken, out Guid userId))
            {
                userId = Guid.Empty;
            }

            string requestId = req.Request?.RequestId ?? Guid.NewGuid().ToString("N")[..8];
            string deviceId = req.Context.System.Device?.DeviceID ?? "unknown";
            string requestType = req.Request?.Type ?? "unknown";

            _counters.IncrementRequests();
            _counters.IncrementType(requestType);

            using (_logger.BeginScope(new Dictionary<string, object>
            {
                ["RequestId"] = requestId,
                ["UserId"] = userId,
                ["DeviceId"] = deviceId,
                ["RequestType"] = requestType
            }))
            {
                _logger.LogInformation("Processing Alexa request of type: {RequestType}", requestType);
                _logger.LogDebug("Request body: {RequestBody}", body);

                Entities.User? user = Plugin.Instance!.Configuration.GetUserById(userId);

                // Fall back to voice-based identification
                if (user == null)
                {
                    string? personId = req.Context.System?.Person?.PersonId;
                    if (!string.IsNullOrEmpty(personId))
                    {
                        user = Plugin.Instance!.Configuration.GetUserByPersonId(personId);
                    }
                }

                if (user == null)
                {
                    _logger.LogError("User not found or invalid access token: {UserId}", userId);
                    return SkillResponseContent(ResponseBuilder.Tell("User not found. Please link your account in the Jellyfin Alexa plugin settings."));
                }

                if (req.Request == null)
                {
                    _logger.LogWarning("Received skill request with null request body");
                    return SkillResponseContent(ResponseBuilder.Empty());
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));

                // Session-aware routing: if a FindSong multi-turn dialog is active,
                // always route to FindSongIntentHandler regardless of what intent Alexa's
                // NLU assigned. Short replies like "family" often get misrouted by NLU
                // (e.g. to ShowMoreIntent or BrowseLibraryIntent) when the user is in
                // a multi-turn FindSong conversation.
                string intentName = req.Request is IntentRequest intentReq ? intentReq.Intent?.Name ?? "null" : "n/a";

                if (req.Session?.Attributes != null
                    && req.Session.Attributes.ContainsKey("FindSongSessionData"))
                {
                    var findSongHandler = _findSongHandler;
                    if (findSongHandler == null)
                    {
                        lock (_handlerCacheLock)
                        {
                            findSongHandler = _findSongHandler ??= _handlers.OfType<FindSongIntentHandler>().FirstOrDefault();
                        }
                    }

                    if (findSongHandler != null)
                    {
                        _logger.LogDebug("Routing to FindSongIntentHandler due to active FindSong session (NLU intent was {Intent})", intentName);
                        SkillResponse findSongResponse = await _pipeline.ExecuteAsync(findSongHandler, req.Request, req.Context, req.Session, cts.Token).ConfigureAwait(false);
                        return SkillResponseContent(findSongResponse);
                    }
                }

                foreach (BaseHandler h in _handlers)
                {
                    if (h.CanHandle(req.Request))
                    {
                        SkillResponse skillResponse = await _pipeline.ExecuteAsync(h, req.Request, req.Context, req.Session, cts.Token).ConfigureAwait(false);
                        return SkillResponseContent(skillResponse);
                    }
                }

                string locale = BaseHandler.GetLocalePublic(req.Request);
                _logger.LogWarning("Unhandled skill request: {RequestType} intent={IntentName} locale={Locale}", req.Request.Type, intentName, locale);
                return SkillResponseContent(ResponseBuilder.Tell(ResponseStrings.Get("CouldNotUnderstand", locale)));
            }
        }
        catch (OperationCanceledException)
        {
            _counters.IncrementErrors();
            _logger.LogWarning("Request processing timed out");
            return SkillResponseContent(ResponseBuilder.Tell("Sorry, that took too long. Please try again."));
        }
        catch (Exception ex)
        {
            _counters.IncrementErrors();
            string errorRef = Guid.NewGuid().ToString("N")[..8];
            _logger.LogError(ex, "Unhandled exception processing Alexa request [ErrorRef:{ErrorRef}]", errorRef);
            return SkillResponseContent(ResponseBuilder.Tell($"Something went wrong. Reference: {errorRef}"));
        }
    }

    private ContentResult SkillResponseContent(SkillResponse response)
    {
        string json = JsonConvert.SerializeObject(response);
        _counters.RecordResponseSize(json.Length);
        _logger.LogDebug("Skill response: {Len} bytes, directives: {Directives}", json.Length, response.Response.Directives.Count);

        return new ContentResult
        {
            Content = json,
            ContentType = "application/json"
        };
    }

    /// <summary>
    /// Verifies the Alexa request signature using the Signature and SignatureCertChainUrl headers.
    /// Uses a cached certificate with a short download timeout to avoid blocking the response.
    /// </summary>
    /// <param name="body">The raw request body.</param>
    /// <param name="timeout">Maximum time allowed for verification.</param>
    /// <returns>True if the signature is valid, false otherwise.</returns>
    private async Task<bool> VerifyAlexaSignature(string body, TimeSpan timeout)
    {
        string? signature = Request.Headers["Signature"].FirstOrDefault();
        string? certChainUrl = Request.Headers["SignatureCertChainUrl"].FirstOrDefault();

        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(certChainUrl))
        {
            _logger.LogWarning("Missing Signature or SignatureCertChainUrl header");
            return false;
        }

        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var verifyTask = RequestVerification.Verify(
                signature,
                new Uri(certChainUrl),
                body,
                GetCertificateWithCache);

            if (await Task.WhenAny(verifyTask, Task.Delay(timeout, cts.Token)).ConfigureAwait(false) != verifyTask)
            {
                _logger.LogWarning("Signature verification timed out after {Timeout}ms", timeout.TotalMilliseconds);
                _ = verifyTask.ContinueWith(
                    t => _logger.LogDebug(t.Exception, "Timed-out verification task faulted"),
                    TaskContinuationOptions.OnlyOnFaulted);
                return false;
            }

            return await verifyTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Alexa request signature verification");
            return false;
        }
    }

    private static readonly ConcurrentDictionary<string, X509Certificate2> CertificateCache = new();
    private static readonly HttpClient CertHttpClient = new() { Timeout = TimeSpan.FromSeconds(3) };

    private async Task<X509Certificate2> GetCertificateWithCache(Uri url)
    {
        var key = url.ToString();

        if (CertificateCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var response = await CertHttpClient.GetAsync(url).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
#pragma warning disable SYSLIB0057 // X509Certificate2(byte[]) obsolete in .NET 9
        var cert = new X509Certificate2(bytes);
#pragma warning restore SYSLIB0057
        CertificateCache[key] = cert;
        return cert;
    }

    [HttpGet("icon-small")]
    [AllowAnonymous]
    public ActionResult GetSmallIcon() => ServeIcon("icon-small");

    [HttpGet("icon-large")]
    [AllowAnonymous]
    public ActionResult GetLargeIcon() => ServeIcon("icon-large");

    private ActionResult ServeIcon(string size)
    {
        Stream? resource = typeof(AlexaSkillController).Assembly
            .GetManifestResourceStream($"Jellyfin.Plugin.AlexaSkill.{size}.png");
        return resource == null ? NotFound() : File(resource, "image/png");
    }
}