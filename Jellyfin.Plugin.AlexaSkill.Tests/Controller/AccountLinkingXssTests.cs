using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Controller;
using Jellyfin.Plugin.AlexaSkill.Diagnostics;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Controller;

/// <summary>
/// JF-308 regression tests: the account-linking page must HTML-encode the
/// error query parameter so that malicious payloads cannot break out of the
/// JavaScript string literal or inject markup.
/// </summary>
[Collection("Plugin")]
public class AccountLinkingXssTests : PluginTestBase
{
    private readonly AlexaSkillController _controller;

    private const string TestClientId = "test-client-id";

    public AccountLinkingXssTests()
    {
        TestHelpers.EnsurePluginInstance(
            new PluginConfiguration { AccountLinkingClientId = TestClientId },
            NullLoggerFactory.Instance,
            c => c.AccountLinkingClientId = TestClientId,
            "alexa-xss-test");

        var userManager = new Mock<IUserManager>();
        var sessionManager = new Mock<ISessionManager>();
        var loggerFactory = NullLoggerFactory.Instance;

        _controller = new AlexaSkillController(
            userManager.Object,
            sessionManager.Object,
            loggerFactory,
            new RequestCounters(),
            new RequestPipeline(
                Enumerable.Empty<IRequestInterceptor>(),
                Enumerable.Empty<IResponseInterceptor>(),
                loggerFactory.CreateLogger<RequestPipeline>()),
            Enumerable.Empty<BaseHandler>());
    }

    /// <summary>
    /// JF-308 regression: a JS-breakout payload (&quot;};alert(…)) in the error
    /// param must be HTML-encoded so the double-quote becomes &amp;quot; and
    /// cannot terminate the JavaScript string literal.
    /// </summary>
    [Fact]
    public void GetAccountLinking_EncodesJsBreakoutPayload()
    {
        string error = "\"};alert(document.domain)//";
        string redirectUri = Config.ValidRedirectUrls[0] + "vendor123";

        ActionResult result = _controller.GetAccountLinking(
            TestClientId, redirectUri, "test-state", error);

        ContentResult contentResult = Assert.IsType<ContentResult>(result)!;
        string html = contentResult.Content ?? string.Empty;

        // The raw breakout sequence must NOT appear — it would close the JS string.
        Assert.DoesNotContain("\";alert(document.domain)//", html);

        // The encoded form must be present, proving HtmlEncode ran.
        Assert.Contains("&quot;};alert(document.domain)//", html);
    }

    /// <summary>
    /// JF-308 regression: an HTML-tag payload in the error param must be
    /// HTML-encoded so the browser never interprets it as markup.
    /// </summary>
    [Fact]
    public void GetAccountLinking_EncodesHtmlTagPayload()
    {
        string error = "<script>alert(1)</script>";
        string redirectUri = Config.ValidRedirectUrls[1] + "vendor123";

        ActionResult result = _controller.GetAccountLinking(
            TestClientId, redirectUri, "test-state", error);

        ContentResult contentResult = Assert.IsType<ContentResult>(result)!;
        string html = contentResult.Content ?? string.Empty;

        // The encoded form must be present in the var error line, proving HtmlEncode ran.
        Assert.Contains("var error = \"&lt;script&gt;alert(1)&lt;/script&gt;\";", html);

        // The raw tag must NOT appear inside the error string assignment —
        // a literal <script> inside a <script> block would prematurely close
        // the outer script tag in the HTML parser.
        Assert.DoesNotContain("var error = \"<script>alert(1)</script>\";", html);
    }

    /// <summary>
    /// JF-308 regression (AC #5): the no-error (success) path must render
    /// an empty JavaScript string and must not leave a leftover placeholder.
    /// </summary>
    [Fact]
    public void GetAccountLinking_NullError_RendersEmptyPlaceholder()
    {
        string redirectUri = Config.ValidRedirectUrls[2] + "vendor123";

        ActionResult result = _controller.GetAccountLinking(
            TestClientId, redirectUri, "test-state", null);

        ContentResult contentResult = Assert.IsType<ContentResult>(result)!;
        Assert.Equal("text/html", contentResult.ContentType);
        string html = contentResult.Content ?? string.Empty;

        // The placeholder must be replaced with an empty string.
        Assert.Contains("var error = \"\";", html);
        Assert.DoesNotContain("{{ error }}", html);
    }
}
