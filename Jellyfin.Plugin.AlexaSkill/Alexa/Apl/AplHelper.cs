using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Apl;

/// <summary>
/// Helper for building APL (Alexa Presentation Language) directives
/// for screen-capable devices.
/// </summary>
internal static class AplHelper
{
    private const string AplInterfaceKey = "Alexa.Presentation.APL";

    private static readonly PropertyInfo? ExtensionsProperty =
        typeof(Request).GetProperty("Extensions", BindingFlags.Public | BindingFlags.Instance);

    private static readonly Lazy<JObject> NowPlayingDocument = new(() => JObject.Parse(@"{
  ""type"": ""APL"",
  ""version"": ""1.9"",
  ""theme"": ""dark"",
  ""import"": [
    {
      ""name"": ""alexa-layouts"",
      ""version"": ""1.5.0""
    }
  ],
  ""resources"": [
    {
      ""dimensions"": {
        ""artSize"": 280,
        ""titleSize"": 36,
        ""subtitleSize"": 24,
        ""controlSize"": 56,
        ""controlSpacing"": 40
      }
    },
    {
      ""when"": ""${@viewportProfile == @viewportProfileRound}"",
      ""dimensions"": {
        ""artSize"": 200,
        ""titleSize"": 28,
        ""subtitleSize"": 20,
        ""controlSize"": 48,
        ""controlSpacing"": 30
      }
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
                ""width"": ""${artSize}"",
                ""height"": ""${artSize}"",
                ""borderRadius"": 10,
                ""scale"": ""bestFill""
              },
              {
                ""type"": ""Text"",
                ""text"": ""${payload.title}"",
                ""fontSize"": ""${titleSize}"",
                ""fontWeight"": ""bold"",
                ""color"": ""white"",
                ""textAlign"": ""center"",
                ""maxLines"": 2,
                ""paddingTop"": 20
              },
              {
                ""type"": ""Text"",
                ""text"": ""${payload.subtitle}"",
                ""fontSize"": ""${subtitleSize}"",
                ""color"": ""#B0B0B0"",
                ""textAlign"": ""center"",
                ""maxLines"": 1,
                ""paddingTop"": 5
              },
              {
                ""type"": ""Container"",
                ""direction"": ""row"",
                ""justifyContent"": ""center"",
                ""alignItems"": ""center"",
                ""paddingTop"": 20,
                ""items"": [
                  {
                    ""type"": ""TouchWrapper"",
                    ""onPress"": {
                      ""type"": ""SendEvent"",
                      ""arguments"": [
                        ""prev""
                      ]
                    },
                    ""width"": ""${controlSize}"",
                    ""height"": ""${controlSize}"",
                    ""item"": {
                      ""type"": ""Text"",
                      ""text"": ""⏮"",
                      ""fontSize"": ""${controlSize}"",
                      ""textAlign"": ""center"",
                      ""color"": ""white""
                    }
                  },
                  {
                    ""type"": ""TouchWrapper"",
                    ""onPress"": {
                      ""type"": ""SendEvent"",
                      ""arguments"": [
                        ""pause""
                      ]
                    },
                    ""width"": ""${controlSize}"",
                    ""height"": ""${controlSize}"",
                    ""item"": {
                      ""type"": ""Text"",
                      ""text"": ""⏸"",
                      ""fontSize"": ""${controlSize}"",
                      ""textAlign"": ""center"",
                      ""color"": ""white""
                    }
                  },
                  {
                    ""type"": ""TouchWrapper"",
                    ""onPress"": {
                      ""type"": ""SendEvent"",
                      ""arguments"": [
                        ""next""
                      ]
                    },
                    ""width"": ""${controlSize}"",
                    ""height"": ""${controlSize}"",
                    ""item"": {
                      ""type"": ""Text"",
                      ""text"": ""⏭"",
                      ""fontSize"": ""${controlSize}"",
                      ""textAlign"": ""center"",
                      ""color"": ""white""
                    }
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

    private static readonly Lazy<JObject> QueueDocument = new(() => JObject.Parse(@"{
  ""type"": ""APL"",
  ""version"": ""1.9"",
  ""theme"": ""dark"",
  ""import"": [
    {
      ""name"": ""alexa-layouts"",
      ""version"": ""1.5.0""
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
                ""type"": ""TouchWrapper"",
                ""onPress"": {
                  ""type"": ""SendEvent"",
                  ""arguments"": [
                    ""playTrack"",
                    ""${data.index}""
                  ]
                },
                ""item"": {
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
    /// Build an APL Now Playing screen directive showing track info, album art,
    /// and interactive playback controls (prev, pause, next) for Echo Show devices.
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
    /// Build an APL queue list directive showing upcoming tracks with
    /// tap-to-play touch handlers for each item.
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

    /// <summary>
    /// Extract an APL touch event argument from a UserEvent request.
    /// Returns the first argument (e.g., "prev", "pause", "next", "playTrack")
    /// or null if the request is not an APL UserEvent.
    /// </summary>
    public static string? GetTouchEventArgument(Request request)
    {
        if (!string.Equals(request.Type, "Alexa.Presentation.APL.UserEvent", StringComparison.Ordinal))
        {
            return null;
        }

        if (ExtensionsProperty?.GetValue(request) is IDictionary<string, JToken> extData
            && extData.TryGetValue("arguments", out var argsToken))
        {
            return (argsToken as JArray)?.FirstOrDefault()?.ToString();
        }

        return null;
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

        if (item is Episode episode)
        {
            return episode.SeriesName ?? string.Empty;
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
        for (int i = 0; i < queueItems.Count; i++)
        {
            var item = queueItems[i];
            tracks.Add(new JObject
            {
                ["title"] = item.Title,
                ["artist"] = item.Artist ?? string.Empty,
                ["artUrl"] = item.ArtUrl ?? string.Empty,
                ["index"] = i
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
