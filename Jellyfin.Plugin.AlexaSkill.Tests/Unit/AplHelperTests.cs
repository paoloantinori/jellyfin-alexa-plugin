using System.Collections.Generic;
using System.Linq;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Jellyfin.Plugin.AlexaSkill.Alexa.Apl;
using MediaBrowser.Controller.Entities.Audio;
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
}
