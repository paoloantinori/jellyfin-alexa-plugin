using System;
using System.Collections.Generic;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Apl;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-315 batch 3 characterization suite for the APL attach members
/// (TryAttachListDirective, TryAttachCarouselDirective). Written BEFORE the
/// extraction and run green on the pre-refactor BaseHandler code via a probe
/// subclass, then retargeted to AplDirectiveAttacher when the members moved
/// (receiver swap only); expectations pin CURRENT behavior (visuals/device
/// gating, directive attach, the interactive-APL session flip and reprompt
/// injection, and the empty-items null-build path) and were NOT edited by
/// the move. The three carousel facts at the bottom moved verbatim from
/// Unit/AplHelperTests (where they tested the BaseHandler member).
/// </summary>
[Collection("Plugin")]
public class AplDirectiveAttacherTests : PluginTestBase, IDisposable
{
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(b => { });
    private readonly ILogger _logger;

    public AplDirectiveAttacherTests()
    {
        _logger = _loggerFactory.CreateLogger<AplDirectiveAttacherTests>();
    }

    public void Dispose() => _loggerFactory.Dispose();

    private static List<ListDisplayItem> SingleItem() => new() { new("Track 1", "id1", "Artist 1", "http://art1") };

    // ---- TryAttachListDirective: gate + attach behavior ----

    [Fact]
    public void List_AplDevice_VisualsEnabled_AttachesRenderDocument()
    {
        // PluginTestBase resets Plugin.Instance, so VisualsEnabled defaults to true.
        var context = TestHelpers.CreateContextWithApl();
        var response = ResponseBuilder.Tell("test");

        AplDirectiveAttacher.TryAttachListDirective(_logger, response, context, "Recently Added", SingleItem(), "recentlyAdded");

        var directive = Assert.Single(response.Response.Directives);
        Assert.IsType<AplRenderDocumentDirective>(directive);
    }

    [Fact]
    public void List_NonAplDevice_NoDirective()
    {
        var context = new Context(); // no SupportedInterfaces
        var response = ResponseBuilder.Tell("test");

        AplDirectiveAttacher.TryAttachListDirective(_logger, response, context, "Recently Added", SingleItem(), "recentlyAdded");

        Assert.Empty(response.Response.Directives);
    }

    [Fact]
    public void List_VisualsDisabled_NoDirective()
    {
        TestHelpers.EnsurePluginInstance(
            new PluginConfiguration(),
            _loggerFactory,
            c => c.AplVisualsEnabled = false,
            nameof(AplDirectiveAttacherTests));
        Plugin.Instance!.Configuration.AplVisualsEnabled = false;

        try
        {
            var context = TestHelpers.CreateContextWithApl();
            var response = ResponseBuilder.Tell("test");

            AplDirectiveAttacher.TryAttachListDirective(_logger, response, context, "Recently Added", SingleItem(), "recentlyAdded");

            Assert.Empty(response.Response.Directives);
        }
        finally
        {
            // Restore default so static state does not leak to other test classes
            Plugin.Instance!.Configuration.AplVisualsEnabled = true;
        }
    }

    [Fact]
    public void List_EmptyItems_NullBuild_NoDirective()
    {
        // BuildListDirective returns null for an empty item list: the attacher must
        // skip the directive (and log the warning) without touching anything else.
        var context = TestHelpers.CreateContextWithApl();
        var response = ResponseBuilder.Tell("test");

        AplDirectiveAttacher.TryAttachListDirective(_logger, response, context, "Recently Added", new List<ListDisplayItem>(), "recentlyAdded");

        Assert.Empty(response.Response.Directives);
    }

    [Fact]
    public void List_HasMoreTrue_StillAttaches()
    {
        var context = TestHelpers.CreateContextWithApl();
        var response = ResponseBuilder.Tell("test");

        AplDirectiveAttacher.TryAttachListDirective(_logger, response, context, "In Progress", SingleItem(), "inProgress", hasMore: true);

        Assert.Single(response.Response.Directives);
    }

    // ---- TryAttachCarouselDirective: gate, attach, and the interactive-session flip ----

    [Fact]
    public void Carousel_TellResponse_FlipsSessionOpen_AndInjectsReprompt()
    {
        // Interactive APL directives require an open session to receive SendEvent
        // callbacks: a session-ending response must flip to open + get the
        // CarouselReprompt reprompt (en-US default locale).
        var context = TestHelpers.CreateContextWithApl();
        var response = ResponseBuilder.Tell("test"); // ShouldEndSession = true

        AplDirectiveAttacher.TryAttachCarouselDirective(_logger, response, context, "Artists", SingleItem(), "queryArtist", "en-US");

        Assert.Single(response.Response.Directives);
        Assert.False(response.Response.ShouldEndSession ?? true);
        Assert.NotNull(response.Response.Reprompt);
        var reprompt = Assert.IsType<PlainTextOutputSpeech>(response.Response.Reprompt.OutputSpeech);
        Assert.Equal("What would you like to hear?", reprompt.Text);
    }

    [Fact]
    public void Carousel_TellResponseWithExistingReprompt_PreservesIt()
    {
        var context = TestHelpers.CreateContextWithApl();
        var response = ResponseBuilder.Tell("test");
        response.Response.Reprompt = new Reprompt("existing reprompt");

        AplDirectiveAttacher.TryAttachCarouselDirective(_logger, response, context, "Artists", SingleItem(), "queryArtist", "en-US");

        Assert.Single(response.Response.Directives);
        Assert.False(response.Response.ShouldEndSession ?? true);
        var reprompt = Assert.IsType<PlainTextOutputSpeech>(response.Response.Reprompt.OutputSpeech);
        Assert.Equal("existing reprompt", reprompt.Text);
    }

    [Fact]
    public void Carousel_OpenSessionWithoutReprompt_AttachesWithoutInjection()
    {
        // An already-open session is NOT flipped and does NOT get the reprompt:
        // the injection lives inside the ShouldEndSession==true branch only.
        var context = TestHelpers.CreateContextWithApl();
        var response = ResponseBuilder.Ask("test", new Reprompt("ask reprompt"));
        response.Response.Reprompt = null;

        AplDirectiveAttacher.TryAttachCarouselDirective(_logger, response, context, "Artists", SingleItem(), "queryArtist", "en-US");

        Assert.Single(response.Response.Directives);
        Assert.False(response.Response.ShouldEndSession ?? true);
        Assert.Null(response.Response.Reprompt);
    }

    [Fact]
    public void Carousel_EmptyItems_NoDirective_NoSessionFlip()
    {
        // Empty items -> BuildCarouselDirective null -> no attach AND no flip:
        // the session must keep its session-ending shape.
        var context = TestHelpers.CreateContextWithApl();
        var response = ResponseBuilder.Tell("test");

        AplDirectiveAttacher.TryAttachCarouselDirective(_logger, response, context, "Artists", new List<ListDisplayItem>(), "queryArtist", "en-US");

        Assert.Empty(response.Response.Directives);
        Assert.True(response.Response.ShouldEndSession ?? false);
        Assert.Null(response.Response.Reprompt);
    }

    [Fact]
    public void Carousel_NonAplDevice_NoDirective_NoSessionFlip()
    {
        var context = new Context();
        var response = ResponseBuilder.Tell("test");

        AplDirectiveAttacher.TryAttachCarouselDirective(_logger, response, context, "Artists", SingleItem(), "queryArtist", "en-US");

        Assert.Empty(response.Response.Directives);
        Assert.True(response.Response.ShouldEndSession ?? false);
        Assert.Null(response.Response.Reprompt);
    }

    [Fact]
    public void Carousel_NullContext_NoDirective()
    {
        Context? context = null;
        var response = ResponseBuilder.Tell("test");

        AplDirectiveAttacher.TryAttachCarouselDirective(_logger, response, context, "Artists", SingleItem(), "queryArtist", "en-US");

        Assert.Empty(response.Response.Directives);
    }

    // ---- Carousel facts moved from Unit/AplHelperTests (invocation retargeted;
    // the old class-held config field became an inline new PluginConfiguration()) ----

    [Fact]
    public void TryAttachCarouselDirective_AplSupported_VisualsEnabled_AttachesDirective()
    {
        // VisualsEnabled defaults to true when Plugin.Instance is null
        var context = TestHelpers.CreateContextWithApl();
        var response = ResponseBuilder.Tell("test");
        var items = new List<ListDisplayItem>
        {
            new("Track 1", "id1", "Artist 1", "http://art1")
        };

        AplDirectiveAttacher.TryAttachCarouselDirective(_logger, response, context, "Recently Played", items);

        var directive = Assert.Single(response.Response.Directives);
        Assert.IsType<AplRenderDocumentDirective>(directive);
    }

    [Fact]
    public void TryAttachCarouselDirective_AplNotSupported_NoDirective()
    {
        // VisualsEnabled defaults to true when Plugin.Instance is null
        var context = new Context(); // no APL support
        var response = ResponseBuilder.Tell("test");
        var items = new List<ListDisplayItem>
        {
            new("Track 1", "id1")
        };

        AplDirectiveAttacher.TryAttachCarouselDirective(_logger, response, context, "Recently Played", items);

        Assert.Empty(response.Response.Directives);
    }

    [Fact]
    public void TryAttachCarouselDirective_VisualsDisabled_NoDirective()
    {
        // Set up Plugin.Instance with visuals disabled
        TestHelpers.EnsurePluginInstance(
            new PluginConfiguration(),
            _loggerFactory,
            c => c.AplVisualsEnabled = false,
            nameof(AplDirectiveAttacherTests));
        Plugin.Instance!.Configuration.AplVisualsEnabled = false;

        try
        {
            var context = TestHelpers.CreateContextWithApl();
            var response = ResponseBuilder.Tell("test");
            var items = new List<ListDisplayItem>
            {
                new("Track 1", "id1")
            };

            AplDirectiveAttacher.TryAttachCarouselDirective(_logger, response, context, "Recently Played", items);

            Assert.Empty(response.Response.Directives);
        }
        finally
        {
            // Restore default so static state does not leak to other test classes
            Plugin.Instance!.Configuration.AplVisualsEnabled = true;
        }
    }
}
