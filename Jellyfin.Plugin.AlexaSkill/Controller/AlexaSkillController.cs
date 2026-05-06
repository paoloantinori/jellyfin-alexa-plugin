using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;
using Jellyfin.Plugin.AlexaSkill.Controller.Handler;
using Jellyfin.Plugin.AlexaSkill.Diagnostics;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
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
            page = page.Replace("{{ error }}", error, StringComparison.Ordinal);
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
        Plugin.Instance!.SaveConfiguration();

        string urlParams = $"access_token={user.Id.ToString()}&state={state}&token_type=token";

        return RedirectPermanent(redirectUri + "#" + urlParams);
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

            if (!await VerifyAlexaSignature(body).ConfigureAwait(false))
            {
                _logger.LogWarning("Alexa request signature verification failed");
                return SkillResponseContent(ResponseBuilder.Tell("Unable to verify request authenticity."));
            }

            SkillRequest? req = JsonConvert.DeserializeObject<SkillRequest>(body);
            if (req?.Context?.System?.User?.AccessToken == null
                && string.IsNullOrEmpty(req?.Context?.System?.Person?.PersonId))
            {
                _logger.LogWarning("Invalid skill request: missing access token and person ID");
                return SkillResponseContent(ResponseBuilder.Tell("Unable to process your request. Please try linking your account again."));
            }

            Guid.TryParse(req.Context.System.User?.AccessToken, out Guid userId);

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

                foreach (BaseHandler h in _handlers)
                {
                    if (h.CanHandle(req.Request))
                    {
                        SkillResponse skillResponse = await _pipeline.ExecuteAsync(h, req.Request, req.Context, req.Session, cts.Token).ConfigureAwait(false);
                        return SkillResponseContent(skillResponse);
                    }
                }

                _logger.LogWarning("Unhandled skill request: {RequestType}", req.Request.Type);
                return SkillResponseContent(ResponseBuilder.Empty());
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
        return new ContentResult
        {
            Content = JsonConvert.SerializeObject(response),
            ContentType = "application/json"
        };
    }

    /// <summary>
    /// Verifies the Alexa request signature using the Signature and SignatureCertChainUrl headers.
    /// </summary>
    /// <param name="body">The raw request body.</param>
    /// <returns>True if the signature is valid, false otherwise.</returns>
    private async Task<bool> VerifyAlexaSignature(string body)
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
            return await RequestVerification.Verify(signature, new Uri(certChainUrl), body).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Alexa request signature verification");
            return false;
        }
    }
}