using System.Collections.Generic;
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
    public void BuildNowPlayingDirective_ValidAudio_ReturnsDirective()
    {
        var audio = new Audio { Name = "Test Song" };
        audio.Artists = new List<string> { "Test Artist" };
        audio.Album = "Test Album";

        var result = AplHelper.BuildNowPlayingDirective(audio, "http://art", "http://bg");

        Assert.NotNull(result);
        Assert.Equal("nowPlaying", result.Token);
        Assert.NotNull(result.Document);
        Assert.NotNull(result.DataSources);

        var payload = result.DataSources["payload"] as JObject;
        Assert.NotNull(payload);
        Assert.Equal("Test Song", payload["title"]?.ToString());
        Assert.Equal("http://art", payload["artUrl"]?.ToString());
    }

    [Fact]
    public void BuildNowPlayingDirective_Subtitle_CombinesArtistAndAlbum()
    {
        var audio = new Audio { Name = "Song" };
        audio.Artists = new List<string> { "Artist" };
        audio.Album = "Album";

        var result = AplHelper.BuildNowPlayingDirective(audio, "art", "bg");
        var payload = result!.DataSources["payload"] as JObject;

        Assert.Equal("Artist · Album", payload?["subtitle"]?.ToString());
    }

    [Fact]
    public void BuildNowPlayingDirective_Subtitle_ArtistOnly()
    {
        var audio = new Audio { Name = "Song" };
        audio.Artists = new List<string> { "Artist" };

        var result = AplHelper.BuildNowPlayingDirective(audio, "art", "bg");
        var payload = result!.DataSources["payload"] as JObject;

        Assert.Equal("Artist", payload?["subtitle"]?.ToString());
    }

    [Fact]
    public void BuildNowPlayingDirective_Subtitle_AlbumOnly()
    {
        var audio = new Audio { Name = "Song" };
        audio.Album = "Album";

        var result = AplHelper.BuildNowPlayingDirective(audio, "art", "bg");
        var payload = result!.DataSources["payload"] as JObject;

        Assert.Equal("Album", payload?["subtitle"]?.ToString());
    }

    [Fact]
    public void BuildNowPlayingDirective_NonAudioItem_EmptySubtitle()
    {
        var video = new MediaBrowser.Controller.Entities.Video
        {
            Name = "Test Video"
        };

        var result = AplHelper.BuildNowPlayingDirective(video, "art", "bg");
        var payload = result!.DataSources["payload"] as JObject;

        Assert.Equal(string.Empty, payload?["subtitle"]?.ToString());
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
    public void BuildNowPlayingDirective_DocumentVersionIs17()
    {
        var audio = new Audio { Name = "Song" };
        var result = AplHelper.BuildNowPlayingDirective(audio, "art", "bg");

        var doc = result!.Document as JObject;
        Assert.Equal("1.7", doc?["version"]?.ToString());
    }

    [Fact]
    public void BuildQueueDirective_EmptyList_ReturnsNull()
    {
        var result = AplHelper.BuildQueueDirective(new List<QueueDisplayItem>());
        Assert.Null(result);
    }

    [Fact]
    public void BuildQueueDirective_SingleTrack_ReturnsDirective()
    {
        var items = new List<QueueDisplayItem>
        {
            new() { Title = "Track 1", Artist = "Artist 1", ArtUrl = "http://art1" }
        };

        var result = AplHelper.BuildQueueDirective(items);

        Assert.NotNull(result);
        Assert.Equal("queue", result.Token);

        var payload = result.DataSources["payload"] as JObject;
        var dataItems = payload?["items"] as JArray;
        Assert.NotNull(dataItems);
        Assert.Single(dataItems);
        Assert.Equal("Track 1", dataItems[0]["title"]?.ToString());
        Assert.Equal("Artist 1", dataItems[0]["subtitle"]?.ToString());
        Assert.Equal("0", dataItems[0]["id"]?.ToString());
        Assert.Equal("Up Next", payload?["title"]?.ToString());
        Assert.Equal("playTrack", payload?["action"]?.ToString());
    }

    [Fact]
    public void BuildQueueDirective_MultipleTracks_IndicesAssigned()
    {
        var items = new List<QueueDisplayItem>
        {
            new() { Title = "A" },
            new() { Title = "B" },
            new() { Title = "C" }
        };

        var result = AplHelper.BuildQueueDirective(items);
        var dataItems = (result!.DataSources["payload"] as JObject)?["items"] as JArray;

        Assert.Equal(3, dataItems!.Count);
        Assert.Equal("0", dataItems[0]["id"]?.ToString());
        Assert.Equal("1", dataItems[1]["id"]?.ToString());
        Assert.Equal("2", dataItems[2]["id"]?.ToString());
    }

    [Fact]
    public void BuildQueueDirective_NullArtistAndArtUrl_DefaultToEmpty()
    {
        var items = new List<QueueDisplayItem>
        {
            new() { Title = "Track" }
        };

        var result = AplHelper.BuildQueueDirective(items);
        var dataItems = (result!.DataSources["payload"] as JObject)?["items"] as JArray;

        Assert.Equal(string.Empty, dataItems![0]["subtitle"]?.ToString());
        Assert.Equal(string.Empty, dataItems[0]["artUrl"]?.ToString());
    }

    [Fact]
    public void BuildQueueDirective_DocumentContainsTouchHandlers()
    {
        var items = new List<QueueDisplayItem> { new() { Title = "T" } };
        var result = AplHelper.BuildQueueDirective(items);

        var docStr = result!.Document.ToString();
        Assert.Contains("TouchWrapper", docStr);
        // The action name is data-bound via ${payload.action}, not hardcoded in the template
        var payload = result.DataSources["payload"] as JObject;
        Assert.Equal("playTrack", payload?["action"]?.ToString());
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
        // Simulate a UserEvent request — Alexa.NET doesn't have a native type,
        // so we use a generic Request subclass if available, or skip if not testable.
        var request = new IntentRequest { Type = "Alexa.Presentation.APL.UserEvent" };

        // The method should return null since IntentRequest doesn't carry extension data
        Assert.Null(AplHelper.GetTouchEventArgument(request));
    }
}
