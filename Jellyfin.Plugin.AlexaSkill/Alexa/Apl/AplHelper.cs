using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Apl;

/// <summary>
/// Helper for building APL (Alexa Presentation Language) directives
/// for screen-capable devices. Uses proper datasource binding:
/// mainTemplate.parameters declares "payload", datasources provides
/// the data, and ${payload.xxx.properties.yyy} expressions resolve values.
/// </summary>
internal static class AplHelper
{
    private const string AplInterfaceKey = "Alexa.Presentation.APL";
    private const string BackstackExtensionUri = "aplext:backstack:10";
    private const string TimeoutShort = "SHORT";

    // NowPlaying APL template with proper datasource binding.
    // ${payload.jellyfinData.properties.xxx} resolves via datasources.
    // ${artSize}, ${titleSize} etc. resolve from the resources section.
    private static readonly string NowPlayingTemplate = @"{
  ""type"": ""APL"",
  ""version"": ""1.7"",
  ""theme"": ""dark"",
  ""extensions"": [
    {
      ""name"": ""Back"",
      ""uri"": ""aplext:backstack:10""
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
      ""when"": ""${viewport.shape == 'round'}"",
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
    ""parameters"": [""payload""],
    ""items"": [
      {
        ""type"": ""Container"",
        ""height"": ""100vh"",
        ""width"": ""100vw"",
        ""items"": [
          {
            ""type"": ""Image"",
            ""source"": ""${payload.jellyfinData.properties.backgroundUrl}"",
            ""scale"": ""best-fill"",
            ""width"": ""100vw"",
            ""height"": ""100vh"",
            ""position"": ""absolute"",
            ""opacity"": 0.3
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
                ""source"": ""${payload.jellyfinData.properties.artUrl}"",
                ""width"": ""${artSize}"",
                ""height"": ""${artSize}"",
                ""borderRadius"": 10,
                ""scale"": ""best-fill""
              },
              {
                ""type"": ""Text"",
                ""text"": ""${payload.jellyfinData.properties.title}"",
                ""fontSize"": ""${titleSize}"",
                ""fontWeight"": ""bold"",
                ""color"": ""white"",
                ""textAlign"": ""center"",
                ""maxLines"": 2,
                ""paddingTop"": 20
              },
              {
                ""type"": ""Text"",
                ""text"": ""${payload.jellyfinData.properties.subtitle}"",
                ""fontSize"": ""${subtitleSize}"",
                ""color"": ""#B0B0B0"",
                ""textAlign"": ""center"",
                ""maxLines"": 1,
                ""paddingTop"": 5
              },
              {
                ""type"": ""Text"",
                ""text"": ""${payload.jellyfinData.properties.elapsedTime} / ${payload.jellyfinData.properties.totalTime}"",
                ""fontSize"": ""${subtitleSize}"",
                ""color"": ""#B0B0B0"",
                ""textAlign"": ""center"",
                ""maxLines"": 1,
                ""paddingTop"": 10
              },
              {
                ""type"": ""Container"",
                ""width"": ""80%"",
                ""height"": 6,
                ""borderRadius"": 3,
                ""backgroundColor"": ""#444444"",
                ""marginTop"": 10,
                ""items"": [
                  {
                    ""type"": ""Frame"",
                    ""width"": ""${Math.min(1, Math.max(0, payload.jellyfinData.properties.progressValue / Math.max(1, payload.jellyfinData.properties.totalValue))) * 100}%"",
                    ""height"": ""100%"",
                    ""borderRadius"": 3,
                    ""backgroundColor"": ""#00A4DC""
                  }
                ]
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
                    ""onPress"": [{ ""type"": ""SendEvent"", ""arguments"": [ ""prev"" ] }],
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
                    ""onPress"": [{ ""type"": ""SendEvent"", ""arguments"": [ ""pause"" ] }],
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
                    ""onPress"": [{ ""type"": ""SendEvent"", ""arguments"": [ ""next"" ] }],
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
}";

    private static readonly JObject NowPlayingDocument = JObject.Parse(NowPlayingTemplate);

    // List APL template with datasource binding and data array iteration.
    // Sequence.data iterates over ${payload.listData.properties.items},
    // creating a ${data} context for each element.
    private static readonly string ListTemplate = @"{
  ""type"": ""APL"",
  ""version"": ""1.7"",
  ""theme"": ""dark"",
  ""extensions"": [
    {
      ""name"": ""Back"",
      ""uri"": ""aplext:backstack:10""
    }
  ],
  ""mainTemplate"": {
    ""parameters"": [""payload""],
    ""items"": [
      {
        ""type"": ""Container"",
        ""height"": ""100vh"",
        ""width"": ""100vw"",
        ""paddingTop"": 20,
        ""items"": [
          {
            ""type"": ""Text"",
            ""text"": ""${payload.listData.properties.title}"",
            ""fontSize"": 30,
            ""fontWeight"": ""bold"",
            ""color"": ""white"",
            ""paddingLeft"": 16,
            ""paddingBottom"": 16
          },
          {
            ""type"": ""Sequence"",
            ""scrollDirection"": ""vertical"",
            ""grow"": 1,
            ""data"": ""${payload.listData.properties.items}"",
            ""items"": {
              ""type"": ""TouchWrapper"",
              ""onPress"": [{ ""type"": ""SendEvent"", ""arguments"": [ ""${payload.listData.properties.action}"", ""${data.id}"" ] }],
              ""item"": {
                ""type"": ""Container"",
                ""paddingTop"": 8,
                ""paddingBottom"": 8,
                ""paddingLeft"": 16,
                ""paddingRight"": 16,
                ""items"": [
                  {
                    ""type"": ""Text"",
                    ""text"": ""${data.title}"",
                    ""fontSize"": 24,
                    ""color"": ""white"",
                    ""maxLines"": 1
                  },
                  {
                    ""type"": ""Text"",
                    ""text"": ""${data.subtitle}"",
                    ""fontSize"": 18,
                    ""color"": ""#B0B0B0"",
                    ""maxLines"": 1,
                    ""paddingTop"": 2,
                    ""when"": ""${data.subtitle}""
                  }
                ]
              }
            }
          },
{
  ""type"": ""TouchWrapper"",
  ""when"": ""${payload.listData.properties.hasMore}"",
  ""onPress"": [{ ""type"": ""SendEvent"", ""arguments"": [""show more""] }],
  ""item"": {
    ""type"": ""Container"",
    ""justifyContent"": ""center"",
    ""alignItems"": ""center"",
    ""paddingTop"": 12,
    ""paddingBottom"": 16,
    ""items"": [
      {
        ""type"": ""Text"",
        ""text"": ""Show More ▸"",
        ""fontSize"": 20,
        ""fontWeight"": ""bold"",
        ""color"": ""#AA5CC3"",
        ""textAlign"": ""center""
      }
    ]
  }
}
        ]
      }
    ]
  }
}";

    private static readonly JObject ListDocument = JObject.Parse(ListTemplate);

    // Carousel APL template with horizontal scrolling image cards.
    // Sequence.data iterates over ${payload.carouselData.properties.items},
    // creating a ${data} context for each card. TouchWrapper sends
    // "carouselTap" with the item id on press.
    private static readonly string CarouselTemplate = @"{
  ""type"": ""APL"",
  ""version"": ""1.7"",
  ""theme"": ""dark"",
  ""extensions"": [
    {
      ""name"": ""Back"",
      ""uri"": ""aplext:backstack:10""
    }
  ],
  ""mainTemplate"": {
    ""parameters"": [""payload""],
    ""items"": [
      {
        ""type"": ""Container"",
        ""height"": ""100vh"",
        ""width"": ""100vw"",
        ""items"": [
          {
            ""type"": ""Text"",
            ""text"": ""${payload.carouselData.properties.headerText}"",
            ""fontSize"": 32,
            ""fontWeight"": ""700"",
            ""color"": ""white"",
            ""paddingLeft"": 24,
            ""paddingTop"": 16,
            ""paddingBottom"": 12
          },
          {
            ""type"": ""Sequence"",
            ""scrollDirection"": ""horizontal"",
            ""width"": ""100%"",
            ""grow"": 1,
            ""data"": ""${payload.carouselData.properties.items}"",
            ""items"": {
              ""type"": ""TouchWrapper"",
              ""onPress"": [{ ""type"": ""SendEvent"", ""arguments"": [ ""carouselTap"", ""${data.id}"" ] }],
              ""item"": {
                ""type"": ""Container"",
                ""width"": ""28vw"",
                ""paddingLeft"": 10,
                ""paddingRight"": 10,
                ""items"": [
                  {
                    ""type"": ""Frame"",
                    ""width"": ""100%"",
                    ""height"": ""28vw"",
                    ""borderRadius"": 12,
                    ""backgroundColor"": ""#2A2A2A"",
                    ""items"": [
                      {
                        ""type"": ""Image"",
                        ""source"": ""${data.artUrl}"",
                        ""width"": ""100%"",
                        ""height"": ""100%"",
                        ""scale"": ""best-fill"",
                        ""borderRadius"": 12,
                        ""when"": ""${data.artUrl}""
                      },
                      {
                        ""type"": ""Text"",
                        ""text"": ""♪"",
                        ""fontSize"": 56,
                        ""color"": ""#555555"",
                        ""textAlign"": ""center"",
                        ""textAlignVertical"": ""center"",
                        ""width"": ""100%"",
                        ""height"": ""100%"",
                        ""when"": ""${!data.artUrl}""
                      }
                    ]
                  },
                  {
                    ""type"": ""Text"",
                    ""text"": ""${data.title}"",
                    ""fontSize"": 20,
                    ""color"": ""white"",
                    ""maxLines"": 2,
                    ""width"": ""100%"",
                    ""paddingTop"": 8,
                    ""paddingLeft"": 2,
                    ""paddingRight"": 2
                  },
                  {
                    ""type"": ""Text"",
                    ""text"": ""${data.subtitle}"",
                    ""fontSize"": 15,
                    ""color"": ""#B0B0B0"",
                    ""maxLines"": 1,
                    ""width"": ""100%"",
                    ""paddingTop"": 2,
                    ""paddingLeft"": 2,
                    ""paddingRight"": 2,
                    ""when"": ""${data.subtitle}""
                  }
                ]
              }
            }
          }
        ]
      }
    ]
  }
}";

    private static readonly JObject CarouselDocument = JObject.Parse(CarouselTemplate);

    // Welcome/splash APL template for skill launch. Always renders on APL devices
    // so users see branded feedback even on first launch with no playback history.
    // The carousel card layout mirrors CarouselTemplate — keep them in sync.
    private static readonly string WelcomeTemplate = @"{
  ""type"": ""APL"",
  ""version"": ""1.7"",
  ""theme"": ""dark"",
  ""extensions"": [{ ""name"": ""Back"", ""uri"": ""aplext:backstack:10"" }],
  ""mainTemplate"": {
    ""parameters"": [""payload""],
    ""items"": [
      {
        ""type"": ""Container"",
        ""height"": ""100vh"",
        ""width"": ""100vw"",
        ""items"": [
          {
            ""type"": ""Container"",
            ""grow"": 1,
            ""justifyContent"": ""center"",
            ""alignItems"": ""center"",
            ""paddingLeft"": 24,
            ""paddingRight"": 24,
            ""items"": [
              {
                ""type"": ""Text"",
                ""text"": ""Jellyfin"",
                ""fontSize"": 44,
                ""fontWeight"": ""bold"",
                ""color"": ""white"",
                ""textAlign"": ""center""
              },
              {
                ""type"": ""Frame"",
                ""width"": 60,
                ""height"": 3,
                ""backgroundColor"": ""#AA5CC3"",
                ""borderRadius"": 2,
                ""marginTop"": 10,
                ""marginBottom"": 14
              },
              {
                ""type"": ""Text"",
                ""text"": ""${payload.welcomeData.properties.greeting}"",
                ""fontSize"": 20,
                ""color"": ""#B0B0B0"",
                ""textAlign"": ""center"",
                ""when"": ""${payload.welcomeData.properties.greeting}""
              }
            ]
          },
          {
            ""type"": ""Container"",
            ""when"": ""${payload.welcomeData.properties.hasItems}"",
            ""grow"": 1,
            ""items"": [
              {
                ""type"": ""Text"",
                ""text"": ""${payload.welcomeData.properties.recentlyPlayedLabel}"",
                ""fontSize"": 20,
                ""fontWeight"": ""600"",
                ""color"": ""white"",
                ""paddingLeft"": 24,
                ""paddingBottom"": 8
              },
              {
                ""type"": ""Sequence"",
                ""scrollDirection"": ""horizontal"",
                ""width"": ""100%"",
                ""grow"": 1,
                ""data"": ""${payload.welcomeData.properties.items}"",
                ""items"": {
                  ""type"": ""TouchWrapper"",
                  ""onPress"": [{ ""type"": ""SendEvent"", ""arguments"": [ ""carouselTap"", ""${data.id}"" ] }],
                  ""item"": {
                    ""type"": ""Container"",
                    ""width"": ""28vw"",
                    ""paddingLeft"": 10,
                    ""paddingRight"": 10,
                    ""items"": [
                      {
                        ""type"": ""Frame"",
                        ""width"": ""100%"",
                        ""height"": ""28vw"",
                        ""borderRadius"": 12,
                        ""backgroundColor"": ""#2A2A2A"",
                        ""items"": [
                          {
                            ""type"": ""Image"",
                            ""source"": ""${data.artUrl}"",
                            ""width"": ""100%"",
                            ""height"": ""100%"",
                            ""scale"": ""best-fill"",
                            ""borderRadius"": 12,
                            ""when"": ""${data.artUrl}""
                          },
                          {
                            ""type"": ""Text"",
                            ""text"": ""♪"",
                            ""fontSize"": 56,
                            ""color"": ""#555555"",
                            ""textAlign"": ""center"",
                            ""textAlignVertical"": ""center"",
                            ""width"": ""100%"",
                            ""height"": ""100%"",
                            ""when"": ""${!data.artUrl}""
                          }
                        ]
                      },
                      {
                        ""type"": ""Text"",
                        ""text"": ""${data.title}"",
                        ""fontSize"": 18,
                        ""color"": ""white"",
                        ""maxLines"": 2,
                        ""width"": ""100%"",
                        ""paddingTop"": 8,
                        ""paddingLeft"": 2,
                        ""paddingRight"": 2
                      },
                      {
                        ""type"": ""Text"",
                        ""text"": ""${data.subtitle}"",
                        ""fontSize"": 14,
                        ""color"": ""#B0B0B0"",
                        ""maxLines"": 1,
                        ""width"": ""100%"",
                        ""paddingTop"": 2,
                        ""paddingLeft"": 2,
                        ""paddingRight"": 2,
                        ""when"": ""${data.subtitle}""
                      }
                    ]
                  }
                }
              }
            ]
          },
          {
            ""type"": ""Container"",
            ""paddingBottom"": 24,
            ""paddingTop"": 8,
            ""paddingLeft"": 24,
            ""paddingRight"": 24,
            ""justifyContent"": ""center"",
            ""alignItems"": ""center"",
            ""items"": [
              {
                ""type"": ""Text"",
                ""text"": ""${payload.welcomeData.properties.prompt}"",
                ""fontSize"": 16,
                ""color"": ""#707070"",
                ""textAlign"": ""center"",
                ""maxLines"": 2
              }
            ]
          }
        ]
      }
    ]
  }
}";

    private static readonly JObject WelcomeDocument = JObject.Parse(WelcomeTemplate);

    // Resume offer APL template showing the content that was previously playing,
    // with artwork, title, and a "resume?" prompt for Echo Show devices.
    private static readonly string ResumeOfferTemplate = @"{
  ""type"": ""APL"",
  ""version"": ""1.7"",
  ""theme"": ""dark"",
  ""extensions"": [{ ""name"": ""Back"", ""uri"": ""aplext:backstack:10"" }],
  ""resources"": [
    {
      ""dimensions"": {
        ""artSize"": 240,
        ""titleSize"": 32,
        ""subtitleSize"": 22,
        ""promptSize"": 18
      }
    },
    {
      ""when"": ""${viewport.shape == 'round'}"",
      ""dimensions"": {
        ""artSize"": 180,
        ""titleSize"": 24,
        ""subtitleSize"": 18,
        ""promptSize"": 14
      }
    }
  ],
  ""mainTemplate"": {
    ""parameters"": [""payload""],
    ""items"": [
      {
        ""type"": ""Container"",
        ""height"": ""100vh"",
        ""width"": ""100vw"",
        ""items"": [
          {
            ""type"": ""Image"",
            ""source"": ""${payload.resumeData.properties.backgroundUrl}"",
            ""scale"": ""best-fill"",
            ""width"": ""100vw"",
            ""height"": ""100vh"",
            ""position"": ""absolute"",
            ""opacity"": 0.3
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
                ""source"": ""${payload.resumeData.properties.artUrl}"",
                ""width"": ""${artSize}"",
                ""height"": ""${artSize}"",
                ""borderRadius"": 10,
                ""scale"": ""best-fill""
              },
              {
                ""type"": ""Text"",
                ""text"": ""${payload.resumeData.properties.title}"",
                ""fontSize"": ""${titleSize}"",
                ""fontWeight"": ""bold"",
                ""color"": ""white"",
                ""textAlign"": ""center"",
                ""maxLines"": 2,
                ""paddingTop"": 20
              },
              {
                ""type"": ""Text"",
                ""text"": ""${payload.resumeData.properties.subtitle}"",
                ""fontSize"": ""${subtitleSize}"",
                ""color"": ""#B0B0B0"",
                ""textAlign"": ""center"",
                ""maxLines"": 1,
                ""paddingTop"": 5,
                ""when"": ""${payload.resumeData.properties.subtitle}""
              },
              {
                ""type"": ""Text"",
                ""text"": ""${payload.resumeData.properties.prompt}"",
                ""fontSize"": ""${promptSize}"",
                ""color"": ""#707070"",
                ""textAlign"": ""center"",
                ""maxLines"": 2,
                ""paddingTop"": 24
              }
            ]
          }
        ]
      }
    ]
  }
}";

    private static readonly JObject ResumeOfferDocument = JObject.Parse(ResumeOfferTemplate);

    /// <summary>
    /// Check if the requesting device supports APL rendering.
    /// </summary>
    /// <param name="context">The Alexa request context containing device capabilities.</param>
    /// <returns>True if the device supports APL, false otherwise.</returns>
    public static bool DeviceSupportsApl(Context? context)
    {
        return context?.System?.Device?.SupportedInterfaces?.ContainsKey(AplInterfaceKey) == true;
    }

    /// <summary>
    /// Gets a value indicating whether APL visual templates are enabled in plugin configuration.
    /// Reads live config so config page changes take effect immediately.
    /// </summary>
    public static bool VisualsEnabled => Plugin.Instance?.Configuration?.AplVisualsEnabled ?? true;

    /// <summary>
    /// Build an APL Now Playing screen directive showing track info, album art,
    /// progress bar with elapsed/total time, and interactive playback controls
    /// (prev, pause, next) for Echo Show devices.
    /// </summary>
    /// <param name="item">The media item to display.</param>
    /// <param name="imageUrl">The URL for the album art image.</param>
    /// <param name="backgroundImageUrl">The URL for the background image.</param>
    /// <param name="context">Optional Alexa request context for backstack navigation.</param>
    /// <param name="progressMs">Current playback position in milliseconds.</param>
    /// <param name="durationMs">Total duration of the item in milliseconds.</param>
    /// <returns>An APL RenderDocument directive, or null if the item has no name.</returns>
    public static AplRenderDocumentDirective? BuildNowPlayingDirective(BaseItem item, string imageUrl, string backgroundImageUrl, Context? context = null, long progressMs = 0, long durationMs = 0)
    {
        if (string.IsNullOrEmpty(item.Name))
        {
            return null;
        }

        var subtitle = GetSubtitle(item);

        return new AplRenderDocumentDirective
        {
            Token = "nowPlaying",
            TimeoutType = TimeoutShort,
            Document = NowPlayingDocument,
            DataSources = new JObject
            {
                ["jellyfinData"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["title"] = item.Name,
                        ["subtitle"] = subtitle,
                        ["artUrl"] = imageUrl,
                        ["backgroundUrl"] = backgroundImageUrl,
                        ["progressValue"] = progressMs,
                        ["totalValue"] = durationMs,
                        ["elapsedTime"] = FormatTime(progressMs),
                        ["totalTime"] = FormatTime(durationMs)
                    }
                }
            },
            PresentationSession = BuildPresentationSession(context)
        };
    }

    /// <summary>
    /// Build an APL queue list directive showing upcoming tracks with
    /// tap-to-play touch handlers for each item.
    /// </summary>
    /// <param name="queueItems">The queue items to display.</param>
    /// <param name="context">Optional Alexa request context for backstack navigation.</param>
    /// <returns>An APL RenderDocument directive, or null if the queue is empty.</returns>
    public static AplRenderDocumentDirective? BuildQueueDirective(List<QueueDisplayItem> queueItems, Context? context = null)
    {
        if (queueItems.Count == 0)
        {
            return null;
        }

        var items = queueItems.Select((q, i) => new ListDisplayItem(q.Title, i.ToString(CultureInfo.InvariantCulture), q.Artist, q.ArtUrl)).ToList();
        return BuildListDirective("Up Next", items, "queue", "playTrack", context);
    }

    /// <summary>
    /// Build a reusable APL list directive showing selectable items with
    /// titles and subtitles for Echo Show devices.
    /// </summary>
    /// <param name="title">Header text displayed above the list.</param>
    /// <param name="items">Items to display in the list.</param>
    /// <param name="token">Token identifying this APL document for subsequent commands.</param>
    /// <param name="action">SendEvent action name fired when a list item is tapped (default: "selectItem").</param>
    /// <param name="context">Optional Alexa request context for backstack navigation.</param>
    /// <returns>An APL RenderDocument directive, or null if the item list is empty.</returns>
    public static AplRenderDocumentDirective? BuildListDirective(string title, List<ListDisplayItem> items, string token, string action = "selectItem", Context? context = null, bool hasMore = false)
    {
        if (items.Count == 0)
        {
            return null;
        }

        var itemArray = new JArray();
        foreach (var item in items)
        {
            var itemObj = new JObject
            {
                ["title"] = item.Title,
                ["id"] = item.Id
            };

            if (!string.IsNullOrEmpty(item.Subtitle))
            {
                itemObj["subtitle"] = item.Subtitle;
            }

            itemArray.Add(itemObj);
        }

        return new AplRenderDocumentDirective
        {
            Token = token,
            Document = ListDocument,
            DataSources = new JObject
            {
                ["listData"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["title"] = title,
                        ["action"] = action,
                        ["items"] = itemArray,
                        ["hasMore"] = hasMore
                    }
                }
            },
            PresentationSession = BuildPresentationSession(context)
        };
    }

    /// <summary>
    /// Build an APL carousel directive showing horizontally scrolling image cards
    /// with tap-to-select touch handlers for each item.
    /// </summary>
    /// <param name="headerText">Header text displayed above the carousel.</param>
    /// <param name="items">Items to display as carousel cards.</param>
    /// <param name="token">Token identifying this APL document for subsequent commands.</param>
    /// <param name="context">Optional Alexa request context for backstack navigation.</param>
    /// <returns>An APL RenderDocument directive, or null if the item list is empty.</returns>
    public static AplRenderDocumentDirective? BuildCarouselDirective(string headerText, List<ListDisplayItem> items, string token = "carousel", Context? context = null)
    {
        if (items.Count == 0)
        {
            return null;
        }

        return new AplRenderDocumentDirective
        {
            Token = token,
            Document = CarouselDocument,
            DataSources = new JObject
            {
                ["carouselData"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["headerText"] = headerText,
                        ["items"] = BuildItemArray(items)
                    }
                }
            },
            PresentationSession = BuildPresentationSession(context)
        };
    }

    /// <summary>
    /// Build an APL welcome/splash screen directive for skill launch.
    /// Always returns a directive (even without carousel items) showing
    /// Jellyfin branding, optional greeting, and a prompt.
    /// When items are provided, a "Recently Played" carousel is shown.
    /// </summary>
    /// <param name="greeting">Personalized greeting text (e.g. "Welcome, Paolo!"), or empty string for no greeting.</param>
    /// <param name="prompt">Bottom prompt text (e.g. "What would you like to hear?").</param>
    /// <param name="recentlyPlayedLabel">Header for the carousel section.</param>
    /// <param name="items">Recently played items for the carousel. Empty = no carousel section.</param>
    /// <param name="context">Optional Alexa request context for backstack navigation.</param>
    /// <returns>An APL RenderDocument directive for the welcome screen.</returns>
    public static AplRenderDocumentDirective BuildWelcomeDirective(
        string greeting,
        string prompt,
        string recentlyPlayedLabel,
        List<ListDisplayItem> items,
        Context? context = null)
    {
        var properties = new JObject
        {
            ["greeting"] = greeting,
            ["prompt"] = prompt,
            ["hasItems"] = items.Count > 0,
            ["recentlyPlayedLabel"] = recentlyPlayedLabel,
            ["items"] = BuildItemArray(items)
        };

        return new AplRenderDocumentDirective
        {
            Token = "welcome",
            Document = WelcomeDocument,
            DataSources = new JObject
            {
                ["welcomeData"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = properties
                }
            },
            PresentationSession = BuildPresentationSession(context)
        };
    }

    /// <summary>
    /// Build an APL resume-offer screen directive showing the previously playing
    /// content with its artwork, title, and a prompt asking the user to confirm resume.
    /// </summary>
    /// <param name="item">The media item that was previously playing.</param>
    /// <param name="imageUrl">The URL for the item's primary image.</param>
    /// <param name="backgroundImageUrl">The URL for the blurred background image.</param>
    /// <param name="locale">The user's locale for localized prompt text.</param>
    /// <param name="context">Optional Alexa request context for backstack navigation.</param>
    /// <returns>An APL RenderDocument directive, or null if the item has no name.</returns>
    public static AplRenderDocumentDirective? BuildResumeOfferDirective(
        BaseItem item,
        string imageUrl,
        string backgroundImageUrl,
        string locale,
        Context? context = null)
    {
        if (string.IsNullOrEmpty(item.Name))
        {
            return null;
        }

        var subtitle = GetSubtitle(item);
        string prompt = ResponseStrings.Get("ResumeOfferPrompt", locale);

        return new AplRenderDocumentDirective
        {
            Token = "resumeOffer",
            TimeoutType = TimeoutShort,
            Document = ResumeOfferDocument,
            DataSources = new JObject
            {
                ["resumeData"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["title"] = item.Name,
                        ["subtitle"] = subtitle,
                        ["artUrl"] = imageUrl,
                        ["backgroundUrl"] = backgroundImageUrl,
                        ["prompt"] = prompt
                    }
                }
            },
            PresentationSession = BuildPresentationSession(context)
        };
    }

    /// <summary>
    /// Convert a list of display items into a JArray suitable for APL data binding.
    /// </summary>
    private static JArray BuildItemArray(List<ListDisplayItem> items)
    {
        var array = new JArray();
        foreach (var item in items)
        {
            var obj = new JObject
            {
                ["title"] = item.Title,
                ["id"] = item.Id
            };

            if (!string.IsNullOrEmpty(item.Subtitle))
            {
                obj["subtitle"] = item.Subtitle;
            }

            if (!string.IsNullOrEmpty(item.ArtUrl))
            {
                obj["artUrl"] = item.ArtUrl;
            }

            array.Add(obj);
        }

        return array;
    }

    /// <summary>
    /// Build a presentation session object for APL backstack navigation.
    /// Returns null if no device ID is available (backstack disabled).
    /// </summary>
    private static PresentationSession? BuildPresentationSession(Context? context)
    {
        string? deviceId = context?.System?.Device?.DeviceID;
        if (string.IsNullOrEmpty(deviceId))
        {
            return null;
        }

        var session = new PresentationSession
        {
            Id = deviceId!,
            GrantedExtensions = new List<GrantedExtension>
            {
                new() { Uri = BackstackExtensionUri }
            }
        };

        string? skillId = context!.System?.Application?.ApplicationId;
        if (!string.IsNullOrEmpty(skillId))
        {
            session.SkillId = skillId;
        }

        return session;
    }

    /// <summary>
    /// Extract an APL touch event argument from a UserEvent request.
    /// Returns the first argument (e.g., "prev", "pause", "next", "playTrack", "selectItem")
    /// or null if the request is not an APL UserEvent.
    /// </summary>
    /// <param name="request">The Alexa request to extract the argument from.</param>
    /// <returns>The first touch event argument, or null if not an APL UserEvent.</returns>
    public static string? GetTouchEventArgument(Request request)
    {
        if (request is AplUserEventRequest aplEvent)
        {
            return aplEvent.Arguments?.FirstOrDefault()?.ToString();
        }

        return null;
    }

    /// <summary>
    /// Extract all APL touch event arguments from a UserEvent request.
    /// Returns the arguments array (e.g., ["selectItem", "itemId"]) or null.
    /// </summary>
    public static JArray? GetTouchEventArguments(Request request)
    {
        if (request is AplUserEventRequest aplEvent)
        {
            return aplEvent.Arguments;
        }

        return null;
    }

    /// <summary>
    /// Format a millisecond duration as a human-readable time string.
    /// Returns "M:mm:ss" for durations over 1 hour, "m:ss" otherwise.
    /// Returns "0:00" for zero or negative values.
    /// </summary>
    /// <param name="ms">Duration in milliseconds.</param>
    /// <returns>Formatted time string.</returns>
    internal static string FormatTime(long ms)
    {
        if (ms <= 0)
        {
            return "0:00";
        }

        var ts = TimeSpan.FromMilliseconds(ms);
        if (ts.TotalHours >= 1)
        {
            return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        }

        return $"{ts.Minutes}:{ts.Seconds:D2}";
    }

    /// <summary>
    /// Build a subtitle string from item metadata for APL display.
    /// Audio items show "Artist · Album", episodes show the series name.
    /// </summary>
    /// <param name="item">The media item.</param>
    /// <returns>A subtitle string suitable for APL rendering.</returns>
    internal static string GetSubtitle(BaseItem item)
    {
        if (item is Audio audio)
        {
            var firstArtist = audio.Artists?.FirstOrDefault();
            if (firstArtist is not null && !string.IsNullOrEmpty(audio.Album))
            {
                return $"{firstArtist} · {audio.Album}";
            }

            return firstArtist ?? audio.Album ?? string.Empty;
        }

        if (item is Episode episode)
        {
            return episode.SeriesName ?? string.Empty;
        }

        return string.Empty;
    }
}
