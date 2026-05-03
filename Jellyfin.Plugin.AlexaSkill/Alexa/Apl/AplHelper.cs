using System;
using System.Collections.Generic;
using Alexa.NET.Request;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Apl;

/// <summary>
/// Helper for building APL (Alexa Presentation Language) directives
/// for screen-capable devices.
/// </summary>
internal static class AplHelper
{
    private const string AplInterfaceKey = "Alexa.Presentation.APL";

    private static readonly Lazy<JObject> NowPlayingDocument = new(() => JObject.Parse(@"{
  ""type"": ""APL"",
  ""version"": ""1.4"",
  ""theme"": ""dark"",
  ""import"": [
    {
      ""name"": ""alexa-layouts"",
      ""version"": ""1.4.0""
    }
  ],
  ""mainTemplate"": {
    ""parameters"": [
      ""payload""
    ],
    ""items"": [
      {
        ""type"": ""Container"",
        ""height"": ""100vh"",
        ""width"": ""100vw"",
        ""items"": [
          {
            ""type"": ""Image"",
            ""source"": ""${payload.backgroundUrl}"",
            ""scale"": ""bestFill"",
            ""width"": ""100vw"",
            ""height"": ""100vh"",
            ""position"": ""absolute"",
            ""filter"": [
              {
                ""type"": ""Blur"",
                ""radius"": 20
              },
              {
                ""type"": ""Grayscale"",
                ""amount"": 0.5
              }
            ],
            ""opacity"": 0.4
          },
          {
            ""type"": ""Container"",
            ""justifyContent"": ""center"",
            ""alignItems"": ""center"",
            ""height"": ""100vh"",
            ""width"": ""100vw"",
            ""paddingLeft"": ""5vw"",
            ""paddingRight"": ""5vw"",
            ""items"": [
              {
                ""type"": ""Image"",
                ""source"": ""${payload.artUrl}"",
                ""width"": ""40vh"",
                ""height"": ""40vh"",
                ""borderRadius"": 10,
                ""scale"": ""bestFill""
              },
              {
                ""type"": ""Text"",
                ""text"": ""${payload.title}"",
                ""fontSize"": 36,
                ""fontWeight"": ""bold"",
                ""color"": ""white"",
                ""textAlign"": ""center"",
                ""maxLines"": 2,
                ""paddingTop"": 20
              },
              {
                ""type"": ""Text"",
                ""text"": ""${payload.subtitle}"",
                ""fontSize"": 24,
                ""color"": ""#B0B0B0"",
                ""textAlign"": ""center"",
                ""maxLines"": 1,
                ""paddingTop"": 5
              }
            ]
          }
        ]
      }
    ]
  }
}"));

    private static readonly Lazy<JObject> QueueDocument = new(() => JObject.Parse(@"{
  ""type"": ""APL"",
  ""version"": ""1.4"",
  ""theme"": ""dark"",
  ""import"": [
    {
      ""name"": ""alexa-layouts"",
      ""version"": ""1.4.0""
    }
  ],
  ""mainTemplate"": {
    ""parameters"": [
      ""payload""
    ],
    ""items"": [
      {
        ""type"": ""Container"",
        ""height"": ""100vh"",
        ""width"": ""100vw"",
        ""backgroundColor"": ""#1A1A1A"",
        ""items"": [
          {
            ""type"": ""Text"",
            ""text"": ""Up Next"",
            ""fontSize"": 32,
            ""fontWeight"": ""bold"",
            ""color"": ""white"",
            ""paddingLeft"": 30,
            ""paddingTop"": 20,
            ""paddingBottom"": 10
          },
          {
            ""type"": ""Sequence"",
            ""scrollDirection"": ""vertical"",
            ""height"": ""85vh"",
            ""data"": ""${payload.tracks}"",
            ""items"": [
              {
                ""type"": ""Container"",
                ""direction"": ""row"",
                ""paddingLeft"": 30,
                ""paddingRight"": 30,
                ""paddingTop"": 8,
                ""paddingBottom"": 8,
                ""items"": [
                  {
                    ""type"": ""Image"",
                    ""source"": ""${data.artUrl}"",
                    ""width"": 60,
                    ""height"": 60,
                    ""borderRadius"": 5,
                    ""scale"": ""bestFill""
                  },
                  {
                    ""type"": ""Container"",
                    ""paddingLeft"": 15,
                    ""direction"": ""column"",
                    ""justifyContent"": ""center"",
                    ""items"": [
                      {
                        ""type"": ""Text"",
                        ""text"": ""${data.title}"",
                        ""fontSize"": 20,
                        ""color"": ""white"",
                        ""maxLines"": 1
                      },
                      {
                        ""type"": ""Text"",
                        ""text"": ""${data.artist}"",
                        ""fontSize"": 16,
                        ""color"": ""#B0B0B0"",
                        ""maxLines"": 1
                      }
                    ]
                  }
                ]
              }
            ]
          }
        ]
      }
    ]
  }
}"));

    /// <summary>
    /// Check if the requesting device supports APL rendering.
    /// </summary>
    public static bool DeviceSupportsApl(Context? context)
    {
        return context?.System?.Device?.SupportedInterfaces?.ContainsKey(AplInterfaceKey) == true;
    }

    /// <summary>
    /// Build an APL Now Playing screen directive showing track info and album art.
    /// </summary>
    /// <param name="item">The media item currently playing.</param>
    /// <param name="imageUrl">URL of the album/item art.</param>
    /// <param name="backgroundImageUrl">URL for the background image.</param>
    /// <returns>An APL RenderDocument directive, or null if item data is insufficient.</returns>
    public static AplRenderDocumentDirective? BuildNowPlayingDirective(BaseItem item, string imageUrl, string backgroundImageUrl)
    {
        var subtitle = GetSubtitle(item);
        if (string.IsNullOrEmpty(item.Name))
        {
            return null;
        }

        var document = NowPlayingDocument.Value;
        var datasources = BuildNowPlayingDatasources(item.Name, subtitle, imageUrl, backgroundImageUrl);

        return new AplRenderDocumentDirective
        {
            Token = "nowPlaying",
            Document = document,
            DataSources = datasources
        };
    }

    /// <summary>
    /// Build an APL queue list directive showing upcoming tracks.
    /// </summary>
    /// <param name="queueItems">List of queue entries with track info.</param>
    /// <returns>An APL RenderDocument directive for the queue screen.</returns>
    public static AplRenderDocumentDirective? BuildQueueDirective(List<QueueDisplayItem> queueItems)
    {
        if (queueItems.Count == 0)
        {
            return null;
        }

        var document = QueueDocument.Value;
        var datasources = BuildQueueDatasources(queueItems);

        return new AplRenderDocumentDirective
        {
            Token = "queue",
            Document = document,
            DataSources = datasources
        };
    }

    private static string GetSubtitle(BaseItem item)
    {
        if (item is Audio audio)
        {
            if (!string.IsNullOrEmpty(audio.Album) && audio.Artists != null && audio.Artists.Count > 0)
            {
                return $"{audio.Artists[0]} · {audio.Album}";
            }

            if (audio.Artists != null && audio.Artists.Count > 0)
            {
                return audio.Artists[0];
            }

            return audio.Album ?? string.Empty;
        }

        return string.Empty;
    }

    private static JObject BuildNowPlayingDatasources(string title, string subtitle, string artUrl, string backgroundUrl)
    {
        return new JObject
        {
            ["payload"] = new JObject
            {
                ["title"] = title,
                ["subtitle"] = subtitle,
                ["artUrl"] = artUrl,
                ["backgroundUrl"] = backgroundUrl
            }
        };
    }

    private static JObject BuildQueueDatasources(List<QueueDisplayItem> queueItems)
    {
        var tracks = new JArray();
        foreach (var item in queueItems)
        {
            tracks.Add(new JObject
            {
                ["title"] = item.Title,
                ["artist"] = item.Artist ?? string.Empty,
                ["artUrl"] = item.ArtUrl ?? string.Empty
            });
        }

        return new JObject
        {
            ["payload"] = new JObject
            {
                ["tracks"] = tracks
            }
        };
    }

}

/// <summary>
/// Display item for APL queue rendering.
/// </summary>
internal class QueueDisplayItem
{
    public string Title { get; set; } = string.Empty;
    public string? Artist { get; set; }
    public string? ArtUrl { get; set; }
}
