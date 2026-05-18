using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Apl;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class AplHelperTests
{
    [Fact]
    public void DeviceSupportsApl_NullContext_ReturnsFalse()
    {
        Assert.False(AplHelper.DeviceSupportsApl(null));
    }

    [Fact]
    public void DeviceSupportsApl_NoSupportedInterfaces_ReturnsFalse()
    {
        var context = new Context { System = new AlexaSystem() };
        Assert.False(AplHelper.DeviceSupportsApl(context));
    }

    [Fact]
    public void DeviceSupportsApl_WithAplInterface_ReturnsTrue()
    {
        var context = new Context
        {
            System = new AlexaSystem
            {
                Device = new Device
                {
                    SupportedInterfaces = new Dictionary<string, object?>
                    {
                        { "Alexa.Presentation.APL", null }
                    }
                }
            }
        };

        Assert.True(AplHelper.DeviceSupportsApl(context));
    }

    [Fact]
    public void BuildNowPlayingDirective_NullItemName_ReturnsNull()
    {
        var audio = new Audio { Name = string.Empty };
        var result = AplHelper.BuildNowPlayingDirective(audio, "http://art", "http://bg");
        Assert.Null(result);
    }

    [Fact]
    public void BuildNowPlayingDirective_ValidAudio_ReturnsDirectiveWithDatasources()
    {
        var audio = new Audio { Name = "Test Song" };
        audio.Artists = new List<string> { "Test Artist" };
        audio.Album = "Test Album";

        var result = AplHelper.BuildNowPlayingDirective(audio, "http://art", "http://bg");

        Assert.NotNull(result);
        Assert.Equal("nowPlaying", result.Token);
        Assert.NotNull(result.Document);
        Assert.NotNull(result.DataSources);

        // Document should contain binding expressions, not literal values
        string docStr = result.Document.ToString();
        Assert.Contains("${payload.jellyfinData.properties.title}", docStr);
        Assert.Contains("${payload.jellyfinData.properties.artUrl}", docStr);

        // Datasources should contain the actual values
        string dsStr = result.DataSources.ToString();
        Assert.Contains("Test Song", dsStr);
        Assert.Contains("http://art", dsStr);
        Assert.Contains("jellyfinData", dsStr);
    }

    [Fact]
    public void BuildNowPlayingDirective_Subtitle_CombinesArtistAndAlbum()
    {
        var audio = new Audio { Name = "Song" };
        audio.Artists = new List<string> { "Artist" };
        audio.Album = "Album";

        var result = AplHelper.BuildNowPlayingDirective(audio, "art", "bg");
        string dsStr = result!.DataSources!.ToString();

        Assert.Contains("Artist · Album", dsStr);
    }

    [Fact]
    public void BuildNowPlayingDirective_Subtitle_ArtistOnly()
    {
        var audio = new Audio { Name = "Song" };
        audio.Artists = new List<string> { "Artist" };

        var result = AplHelper.BuildNowPlayingDirective(audio, "art", "bg");
        string dsStr = result!.DataSources!.ToString();

        Assert.Contains("Artist", dsStr);
    }

    [Fact]
    public void BuildNowPlayingDirective_Subtitle_AlbumOnly()
    {
        var audio = new Audio { Name = "Song" };
        audio.Album = "Album";

        var result = AplHelper.BuildNowPlayingDirective(audio, "art", "bg");
        string dsStr = result!.DataSources!.ToString();

        Assert.Contains("Album", dsStr);
    }

    [Fact]
    public void BuildNowPlayingDirective_NonAudioItem_EmptySubtitle()
    {
        var video = new MediaBrowser.Controller.Entities.Video
        {
            Name = "Test Video"
        };

        var result = AplHelper.BuildNowPlayingDirective(video, "art", "bg");
        string dsStr = result!.DataSources!.ToString();

        Assert.Contains("Test Video", dsStr);
    }

    [Fact]
    public void BuildNowPlayingDirective_DocumentContainsControls()
    {
        var audio = new Audio { Name = "Song" };
        var result = AplHelper.BuildNowPlayingDirective(audio, "art", "bg");

        var docStr = result!.Document.ToString();
        Assert.Contains("TouchWrapper", docStr);
        Assert.Contains("prev", docStr);
        Assert.Contains("pause", docStr);
        Assert.Contains("next", docStr);
    }

    [Fact]
    public void BuildNowPlayingDirective_DocumentHasParameters()
    {
        var audio = new Audio { Name = "Song" };
        var result = AplHelper.BuildNowPlayingDirective(audio, "art", "bg");

        var mainTemplate = result!.Document!["mainTemplate"];
        Assert.NotNull(mainTemplate);
        var parameters = mainTemplate["parameters"] as JArray;
        Assert.NotNull(parameters);
        Assert.Equal("payload", parameters[0].ToString());
    }

    [Fact]
    public void BuildNowPlayingDirective_DocumentVersionIs17()
    {
        var audio = new Audio { Name = "Song" };
        var result = AplHelper.BuildNowPlayingDirective(audio, "art", "bg");

        var doc = result!.Document as JObject;
        Assert.Equal("1.7", doc?["version"]?.ToString());
    }

    [Fact]
    public void BuildNowPlayingDirective_DatasourcesHasObjectType()
    {
        var audio = new Audio { Name = "Song" };
        var result = AplHelper.BuildNowPlayingDirective(audio, "art", "bg");

        var ds = result!.DataSources!;
        Assert.Equal("object", ds["jellyfinData"]?["type"]?.ToString());
        Assert.NotNull(ds["jellyfinData"]?["properties"]);
    }

    [Fact]
    public void BuildQueueDirective_EmptyList_ReturnsNull()
    {
        var result = AplHelper.BuildQueueDirective(new List<QueueDisplayItem>());
        Assert.Null(result);
    }

    [Fact]
    public void BuildQueueDirective_SingleTrack_ReturnsDirectiveWithDatasources()
    {
        var items = new List<QueueDisplayItem>
        {
            new() { Title = "Track 1", Artist = "Artist 1", ArtUrl = "http://art1" }
        };

        var result = AplHelper.BuildQueueDirective(items);

        Assert.NotNull(result);
        Assert.Equal("queue", result.Token);
        Assert.NotNull(result.Document);
        Assert.NotNull(result.DataSources);

        // Datasources should contain item data
        string dsStr = result.DataSources.ToString();
        Assert.Contains("Track 1", dsStr);
        Assert.Contains("Artist 1", dsStr);
        Assert.Contains("Up Next", dsStr);
        Assert.Contains("playTrack", dsStr);
    }

    [Fact]
    public void BuildQueueDirective_MultipleTracks_AllInDatasources()
    {
        var items = new List<QueueDisplayItem>
        {
            new() { Title = "A" },
            new() { Title = "B" },
            new() { Title = "C" }
        };

        var result = AplHelper.BuildQueueDirective(items);
        string dsStr = result!.DataSources!.ToString();

        Assert.Contains("A", dsStr);
        Assert.Contains("B", dsStr);
        Assert.Contains("C", dsStr);
    }

    [Fact]
    public void BuildQueueDirective_NullArtist_NoSubtitleInData()
    {
        var items = new List<QueueDisplayItem>
        {
            new() { Title = "Track" }
        };

        var result = AplHelper.BuildQueueDirective(items);
        Assert.NotNull(result);
        Assert.NotNull(result.DataSources);
    }

    [Fact]
    public void BuildQueueDirective_DocumentContainsDataBindingForItems()
    {
        var items = new List<QueueDisplayItem> { new() { Title = "T" } };
        var result = AplHelper.BuildQueueDirective(items);

        var docStr = result!.Document.ToString();
        Assert.Contains("${data.title}", docStr);
        Assert.Contains("${data.id}", docStr);
        Assert.Contains("${payload.listData.properties.items}", docStr);
    }

    [Fact]
    public void GetTouchEventArgument_NonUserEventRequest_ReturnsNull()
    {
        var request = new IntentRequest { Type = "IntentRequest" };
        Assert.Null(AplHelper.GetTouchEventArgument(request));
    }

    [Fact]
    public void GetTouchEventArgument_UserEventWithoutArguments_ReturnsNull()
    {
        var request = new IntentRequest { Type = "Alexa.Presentation.APL.UserEvent" };
        Assert.Null(AplHelper.GetTouchEventArgument(request));
    }

    [Fact]
    public void BuildListDirective_EmbedsTitleInDatasources()
    {
        var items = new List<ListDisplayItem>
        {
            new("Track1", "id1", "Artist1", "http://img.example.com/1.jpg"),
            new("Track2", "id2")
        };

        var result = AplHelper.BuildListDirective(
            "Test Title", items, "testToken", "selectItem");

        Assert.NotNull(result);
        Assert.NotNull(result.DataSources);

        string dsStr = result.DataSources.ToString();
        Assert.Contains("Test Title", dsStr);
        Assert.Contains("Track1", dsStr);
        Assert.Contains("Artist1", dsStr);
        Assert.Contains("selectItem", dsStr);
    }

    [Fact]
    public void BuildListDirective_DocumentHasParameters()
    {
        var items = new List<ListDisplayItem>
        {
            new("Item", "id-1")
        };

        var result = AplHelper.BuildListDirective("Test", items, "test");

        var mainTemplate = result!.Document!["mainTemplate"];
        var parameters = mainTemplate?["parameters"] as JArray;
        Assert.NotNull(parameters);
        Assert.Equal("payload", parameters[0].ToString());
    }

    [Fact]
    public void BuildListDirective_ItemsWithOptionalFields_OmitSubtitleWhenNull()
    {
        var items = new List<ListDisplayItem>
        {
            new("Minimal Item", "min-id")
        };

        var result = AplHelper.BuildListDirective("Test", items, "test");

        Assert.NotNull(result);
        Assert.NotNull(result.DataSources);
        string dsStr = result.DataSources.ToString();
        Assert.Contains("Minimal Item", dsStr);
        // Item without subtitle should not have subtitle key
        var itemObj = result.DataSources["listData"]?["properties"]?["items"]?[0] as JObject;
        Assert.False(itemObj?.ContainsKey("subtitle"));
    }

    [Fact]
    public void BuildListDirective_DatasourcesHasObjectType()
    {
        var items = new List<ListDisplayItem>
        {
            new("Item", "id-1")
        };

        var result = AplHelper.BuildListDirective("Test", items, "test");

        var ds = result!.DataSources!;
        Assert.Equal("object", ds["listData"]?["type"]?.ToString());
        Assert.NotNull(ds["listData"]?["properties"]);
    }

    [Fact]
    public void BuildCarouselDirective_EmptyItems_ReturnsNull()
    {
        var result = AplHelper.BuildCarouselDirective("Header", new List<ListDisplayItem>());
        Assert.Null(result);
    }

    [Fact]
    public void BuildCarouselDirective_NonEmptyItems_ReturnsDirectiveWithCorrectToken()
    {
        var items = new List<ListDisplayItem>
        {
            new("Track 1", "id1")
        };

        var result = AplHelper.BuildCarouselDirective("Recently Played", items);

        Assert.NotNull(result);
        Assert.Equal("carousel", result.Token);
        Assert.NotNull(result.Document);
        Assert.NotNull(result.DataSources);
    }

    [Fact]
    public void BuildCarouselDirective_CustomToken_ReturnsDirectiveWithCustomToken()
    {
        var items = new List<ListDisplayItem>
        {
            new("Track 1", "id1")
        };

        var result = AplHelper.BuildCarouselDirective("Header", items, "myCarousel");

        Assert.NotNull(result);
        Assert.Equal("myCarousel", result.Token);
    }

    [Fact]
    public void BuildCarouselDirective_DatasourceContainsCorrectItemCount()
    {
        var items = new List<ListDisplayItem>
        {
            new("Track 1", "id1", "Artist 1", "http://art1"),
            new("Track 2", "id2", "Artist 2"),
            new("Track 3", "id3")
        };

        var result = AplHelper.BuildCarouselDirective("Recently Played", items);

        Assert.NotNull(result);
        var itemArray = result.DataSources!["carouselData"]?["properties"]?["items"] as JArray;
        Assert.NotNull(itemArray);
        Assert.Equal(3, itemArray.Count);
    }

    [Fact]
    public void BuildCarouselDirective_DatasourceContainsCorrectProperties()
    {
        var items = new List<ListDisplayItem>
        {
            new("Track 1", "id1", "Artist 1", "http://art1")
        };

        var result = AplHelper.BuildCarouselDirective("Recently Played", items);

        Assert.NotNull(result);
        string dsStr = result.DataSources!.ToString();
        Assert.Contains("Recently Played", dsStr);
        Assert.Contains("Track 1", dsStr);
        Assert.Contains("id1", dsStr);
        Assert.Contains("Artist 1", dsStr);
        Assert.Contains("http://art1", dsStr);
        Assert.Contains("carouselData", dsStr);
    }

    [Fact]
    public void BuildCarouselDirective_ItemsWithoutSubtitleOrArtUrl_StillWork()
    {
        var items = new List<ListDisplayItem>
        {
            new("Minimal Item", "min-id")
        };

        var result = AplHelper.BuildCarouselDirective("Header", items);

        Assert.NotNull(result);
        Assert.NotNull(result.DataSources);

        var itemObj = result.DataSources["carouselData"]?["properties"]?["items"]?[0] as JObject;
        Assert.NotNull(itemObj);
        Assert.Equal("Minimal Item", itemObj["title"]?.ToString());
        Assert.Equal("min-id", itemObj["id"]?.ToString());
        Assert.False(itemObj.ContainsKey("subtitle"));
        Assert.False(itemObj.ContainsKey("artUrl"));
    }

    [Fact]
    public void BuildCarouselDirective_ItemsWithArtUrlOnly_NoSubtitle()
    {
        var items = new List<ListDisplayItem>
        {
            new("Art Item", "art-id", null, "http://img.example.com/cover.jpg")
        };

        var result = AplHelper.BuildCarouselDirective("Header", items);

        Assert.NotNull(result);
        var itemObj = result.DataSources!["carouselData"]?["properties"]?["items"]?[0] as JObject;
        Assert.NotNull(itemObj);
        Assert.False(itemObj.ContainsKey("subtitle"));
        Assert.True(itemObj.ContainsKey("artUrl"));
        Assert.Equal("http://img.example.com/cover.jpg", itemObj["artUrl"]?.ToString());
    }

    [Fact]
    public void BuildCarouselDirective_DocumentContainsHorizontalSequence()
    {
        var items = new List<ListDisplayItem>
        {
            new("Track", "id1")
        };

        var result = AplHelper.BuildCarouselDirective("Header", items);

        Assert.NotNull(result);
        string docStr = result.Document!.ToString();
        Assert.Contains("horizontal", docStr);
        Assert.Contains("Sequence", docStr);
    }

    [Fact(Skip = "TouchWrapper tap handling temporarily removed from carousel template")]
    public void BuildCarouselDirective_DocumentContainsCarouselTapSendEvent()
    {
        var items = new List<ListDisplayItem>
        {
            new("Track", "id1")
        };

        var result = AplHelper.BuildCarouselDirective("Header", items);

        Assert.NotNull(result);
        string docStr = result.Document!.ToString();
        Assert.Contains("carouselTap", docStr);
        Assert.Contains("SendEvent", docStr);
        Assert.Contains("${data.id}", docStr);
    }

    [Fact]
    public void BuildCarouselDirective_DocumentHasParameters()
    {
        var items = new List<ListDisplayItem>
        {
            new("Track", "id1")
        };

        var result = AplHelper.BuildCarouselDirective("Header", items);

        var mainTemplate = result!.Document!["mainTemplate"];
        var parameters = mainTemplate?["parameters"] as JArray;
        Assert.NotNull(parameters);
        Assert.Equal("payload", parameters[0].ToString());
    }

    [Fact]
    public void BuildCarouselDirective_DocumentVersionIs17()
    {
        var items = new List<ListDisplayItem>
        {
            new("Track", "id1")
        };

        var result = AplHelper.BuildCarouselDirective("Header", items);

        var doc = result!.Document as JObject;
        Assert.Equal("1.7", doc?["version"]?.ToString());
        Assert.Equal("dark", doc?["theme"]?.ToString());
    }

    [Fact]
    public void BuildCarouselDirective_DatasourcesHasObjectType()
    {
        var items = new List<ListDisplayItem>
        {
            new("Track", "id1")
        };

        var result = AplHelper.BuildCarouselDirective("Header", items);

        var ds = result!.DataSources!;
        Assert.Equal("object", ds["carouselData"]?["type"]?.ToString());
        Assert.NotNull(ds["carouselData"]?["properties"]);
    }

    [Fact]
    public void BuildCarouselDirective_DocumentContainsDataBinding()
    {
        var items = new List<ListDisplayItem>
        {
            new("Track", "id1")
        };

        var result = AplHelper.BuildCarouselDirective("Header", items);

        string docStr = result!.Document!.ToString();
        Assert.Contains("${payload.carouselData.properties.headerText}", docStr);
        Assert.Contains("${payload.carouselData.properties.items}", docStr);
        Assert.Contains("${data.title}", docStr);
        Assert.Contains("${data.artUrl}", docStr);
    }
}

/// <summary>
/// Tests for BaseHandler.TryAttachCarouselDirective method.
/// </summary>
public class TryAttachCarouselDirectiveTests : IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TestCarouselHandler _handler;

    public TryAttachCarouselDirectiveTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
        _handler = new TestCarouselHandler(_sessionManagerMock.Object, _config, _loggerFactory);
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
    }

    [Fact]
    public void TryAttachCarouselDirective_AplSupported_VisualsEnabled_AttachesDirective()
    {
        // VisualsEnabled defaults to true when Plugin.Instance is null
        var context = AplTestHelper.CreateAplContext();
        var response = ResponseBuilder.Tell("test");
        var items = new List<ListDisplayItem>
        {
            new("Track 1", "id1", "Artist 1", "http://art1")
        };

        _handler.InvokeTryAttachCarouselDirective(response, context, "Recently Played", items);

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

        _handler.InvokeTryAttachCarouselDirective(response, context, "Recently Played", items);

        Assert.Empty(response.Response.Directives);
    }

    [Fact]
    public void TryAttachCarouselDirective_VisualsDisabled_NoDirective()
    {
        // Set up Plugin.Instance with visuals disabled
        TestHelpers.EnsurePluginInstance(
            _config,
            _loggerFactory,
            c => c.AplVisualsEnabled = false,
            nameof(TryAttachCarouselDirectiveTests));
        Plugin.Instance!.Configuration.AplVisualsEnabled = false;

        try
        {
            var context = AplTestHelper.CreateAplContext();
            var response = ResponseBuilder.Tell("test");
            var items = new List<ListDisplayItem>
            {
                new("Track 1", "id1")
            };

            _handler.InvokeTryAttachCarouselDirective(response, context, "Recently Played", items);

            Assert.Empty(response.Response.Directives);
        }
        finally
        {
            // Restore default so static state does not leak to other test classes
            Plugin.Instance!.Configuration.AplVisualsEnabled = true;
        }
    }

    /// <summary>
    /// Test subclass exposing the private protected TryAttachCarouselDirective method.
    /// </summary>
    private class TestCarouselHandler : BaseHandler
    {
        public TestCarouselHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
            : base(sessionManager, config, loggerFactory)
        {
        }

        public override bool CanHandle(Request request) => true;

        public override Task<SkillResponse> HandleAsync(Request request, Context context, Jellyfin.Plugin.AlexaSkill.Entities.User user, SessionInfo session, CancellationToken cancellationToken)
            => Task.FromResult(ResponseBuilder.Tell("test"));

        public void InvokeTryAttachCarouselDirective(
            SkillResponse response,
            Context? context,
            string title,
            List<ListDisplayItem> items,
            string token = "carousel")
            => TryAttachCarouselDirective(response, context, title, items, token);
    }

    private static class AplTestHelper
    {
        public static Context CreateAplContext()
        {
            return new Context
            {
                System = new AlexaSystem
                {
                    Device = new Device
                    {
                        SupportedInterfaces = new Dictionary<string, object?>
                        {
                            { "Alexa.Presentation.APL", null }
                        }
                    }
                }
            };
        }
    }
}
