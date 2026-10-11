using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
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

    // JF-624 SPIKE: the enhanced NowPlaying variant behind AplEnhancedScreens (default
    // false, OUT of the critical path by task mandate). Differences from the hand-rolled
    // baseline: (1) the alexa-layouts 1.7.0 import is BACK (removed 2026-05-10 in
    // 29d49e16 after the 1.5.0-era silent black screen; this is the compatibility
    // re-verification, one screen, one flag); (2) AlexaBackground with backgroundBlur
    // replaces the hand-rolled opacity-0.3 background Image; (3) AlexaProgressBar
    // replaces the hand-rolled Frame-width progress bar. The TouchWrapper transport row
    // and the jellyfinData datasource are UNCHANGED (they are the already-working
    // JF-582 wiring; the spike must not touch them).
    private static readonly string NowPlayingEnhancedTemplate = @"{
  ""type"": ""APL"",
  ""version"": ""1.7"",
  ""theme"": ""dark"",
  ""import"": [
    {
      ""name"": ""alexa-layouts"",
      ""version"": ""1.7.0""
    }
  ],
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
            ""type"": ""AlexaBackground"",
            ""backgroundImageSource"": ""${payload.jellyfinData.properties.backgroundUrl}"",
            ""backgroundBlur"": true,
            ""colorOverlay"": true
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
                ""type"": ""AlexaProgressBar"",
                ""id"": ""npProgressBar"",
                ""progressValue"": ""${payload.jellyfinData.properties.progressBaseMs}"",
                ""totalValue"": ""${payload.jellyfinData.properties.totalValue}"",
                ""width"": ""${Math.min(90, artSize / 3)}%"",
                ""paddingTop"": 15,
                ""paddingBottom"": 5,
                ""handleTick"": [
                  {
                    ""minimumDelay"": 1000,
                    ""commands"": [
                      {
                        ""type"": ""SetValue"",
                        ""componentId"": ""npProgressBar"",
                        ""property"": ""progressValue"",
                        ""value"": ""${Math.min(payload.jellyfinData.properties.progressBaseMs + elapsedTime, payload.jellyfinData.properties.totalValue)}""
                      }
                    ]
                  }
                ]
              },
              {
                ""type"": ""Text"",
                ""text"": ""${payload.jellyfinData.properties.elapsedTime} / ${payload.jellyfinData.properties.totalTime}"",
                ""fontSize"": ""${subtitleSize}"",
                ""color"": ""#B0B0B0"",
                ""textAlign"": ""center"",
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

    private static readonly JObject NowPlayingEnhancedDocument = JObject.Parse(NowPlayingEnhancedTemplate);

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

    // Photo slideshow APL template (JF-329): a full-screen Pager cycling the
    // album's images. The advance mechanism is the handleTick + command shape
    // already proven in this repo (the JF-624 enhanced NowPlaying tick handler,
    // verified in the Amazon web simulator runtime): every 8 seconds the
    // SetPage command moves the pager to the wrapped next page (modulo the
    // images array's own length, the one source of the count), and the same
    // expression also serves manual swipes (navigation "wrap"). An AutoPages
    // component would be fewer moving parts but is unproven on our devices.
    private static readonly string SlideshowTemplate = @"{
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
            ""type"": ""Pager"",
            ""id"": ""slideshowPager"",
            ""width"": ""100vw"",
            ""height"": ""100vh"",
            ""navigation"": ""wrap"",
            ""data"": ""${payload.slideshowData.properties.images}"",
            ""items"": {
              ""type"": ""Image"",
              ""source"": ""${data.url}"",
              ""width"": ""100vw"",
              ""height"": ""100vh"",
              ""scale"": ""best-fill""
            },
            ""handleTick"": [
              {
                ""minimumDelay"": 8000,
                ""commands"": [
                  {
                    ""type"": ""SetPage"",
                    ""componentId"": ""slideshowPager"",
                    ""position"": ""${(slideshowPager.currentPage + 1) % payload.slideshowData.properties.images.length}""
                  }
                ]
              }
            ]
          },
          {
            ""type"": ""Text"",
            ""text"": ""${payload.slideshowData.properties.title}"",
            ""fontSize"": 28,
            ""fontWeight"": ""bold"",
            ""color"": ""white"",
            ""paddingLeft"": 24,
            ""paddingTop"": 16,
            ""position"": ""absolute"",
            ""width"": ""100vw""
          }
        ]
      }
    ]
  }
}";

    private static readonly JObject SlideshowDocument = JObject.Parse(SlideshowTemplate);

    // JF-624 item 3: the tappable disambiguation text list (AlexaTextList). The
    // alexa-layouts 1.7.0 import is the same one the task's spike re-verified on
    // the live Show (the 2026-05-10 black screen does not reproduce at 1.7.0);
    // each NEW component still gets its own device verification per the task
    // discipline, so the render probe is on the maintainer's device list.
    // primaryAction uses the DOCUMENTED ${ordinal} binding (the number of the
    // selected row, 1-based; AlexaTextList docs, verified 2026-10-11): the tap
    // payload is [action, ordinal], and the AplUserEventHandler maps the ordinal
    // onto the yes/no disambiguation state by position. The action name rides
    // the datasource (the same payload-driven-action shape the hand-rolled
    // ListTemplate uses), so one document serves both state machines the
    // disambiguation family uses (disambig_* and the FindSong picker).
    private static readonly string DisambiguationListTemplate = @"{
  ""type"": ""APL"",
  ""version"": ""1.7"",
  ""theme"": ""dark"",
  ""import"": [
    {
      ""name"": ""alexa-layouts"",
      ""version"": ""1.7.0""
    }
  ],
  ""mainTemplate"": {
    ""parameters"": [""payload""],
    ""items"": [
      {
        ""type"": ""AlexaTextList"",
        ""theme"": ""dark"",
        ""headerTitle"": ""${payload.disambigData.properties.title}"",
        ""listItems"": ""${payload.disambigData.properties.listItems}"",
        ""primaryAction"": [
          {
            ""type"": ""SendEvent"",
            ""arguments"": [ ""${payload.disambigData.properties.action}"", ""${ordinal}"" ]
          }
        ]
      }
    ]
  }
}";

    private static readonly JObject DisambiguationListDocument = JObject.Parse(DisambiguationListTemplate);

    // JF-624 item 4: the tappable paginated picker (AlexaPaginatedList) for
    // chapter/episode lists. Property shape verified against the CURRENT Amazon
    // AlexaPaginatedList page (scraped 2026-10-11; page last updated 2023-11-28):
    // the template is full-screen with NATIVE pagination ("a list of items in a
    // series of pages" + the built-in page counter), every parameter except type
    // is optional, listItems rows use the documented AlexaImageListItem subset
    // (primaryText/secondaryText/tertiaryText/imageSource + the per-item
    // primaryAction override), and the documented primaryAction shape is exactly
    // SendEvent [action, "${ordinal}"] ("passes the number representing the
    // selected item"). Deliberately NOT set: every background/scrim knob (the
    // 2026-05-10 silent-black-screen class was a background-parameter mismatch,
    // and a picker needs no background), headerBackButton (a transient picker,
    // same decision as the item-3 text list), and listId (voice-based scrolling
    // needs Echo Show built-in intents this skill does not register).
    private static readonly string PaginatedListTemplate = @"{
  ""type"": ""APL"",
  ""version"": ""1.7"",
  ""theme"": ""dark"",
  ""import"": [
    {
      ""name"": ""alexa-layouts"",
      ""version"": ""1.7.0""
    }
  ],
  ""mainTemplate"": {
    ""parameters"": [""payload""],
    ""items"": [
      {
        ""type"": ""AlexaPaginatedList"",
        ""theme"": ""dark"",
        ""headerTitle"": ""${payload.paginatedData.properties.title}"",
        ""listItems"": ""${payload.paginatedData.properties.listItems}"",
        ""primaryAction"": [
          {
            ""type"": ""SendEvent"",
            ""arguments"": [ ""${payload.paginatedData.properties.action}"", ""${ordinal}"" ]
          }
        ]
      }
    ]
  }
}";

    private static readonly JObject PaginatedListDocument = JObject.Parse(PaginatedListTemplate);

    /// <summary>
    /// SendEvent action for a tap on a chapter-picker row: the GoToChapter entry
    /// (the tap's ordinal is the 1-based chapter number). JF-624 item 4.
    /// </summary>
    internal const string ChapterPickTapAction = "chapterPick";

    /// <summary>
    /// SendEvent action for a tap on an episode-picker row: the PlayEpisode entry
    /// (the row's per-item arguments carry season, episode, and series name).
    /// JF-624 item 4.
    /// </summary>
    internal const string EpisodePickTapAction = "episodePick";

    /// <summary>
    /// SendEvent action for a tap on a disambiguation text-list row: the yes/no
    /// disambiguation state machine (disambig_* session attributes). JF-624 item 3.
    /// </summary>
    internal const string DisambiguationTapAction = "disambigSelect";

    /// <summary>
    /// SendEvent action for a tap on a FindSong candidate row: the
    /// FindSongState.Disambiguating picker. JF-624 item 3.
    /// </summary>
    internal const string FindSongTapAction = "findSongSelect";

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

        // JF-624 spike gate: the enhanced (alexa-layouts) variant only when the
        // dedicated flag is on; the hand-rolled baseline stays the default and the
        // flag-off path is byte-identical to before.
        bool enhanced = Plugin.Instance?.Configuration?.AplEnhancedScreens == true;

        return new AplRenderDocumentDirective
        {
            Token = "nowPlaying",
            TimeoutType = TimeoutShort,
            Document = enhanced ? NowPlayingEnhancedDocument : NowPlayingDocument,
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
                        // JF-624 round 4: the render-time base for the live-advancing
                        // bar. elapsedTime (the APL document clock, ms since render)
                        // is added in the template binding and clamped to the total,
                        // so the bar fills in real time while the screen is visible.
                        // The hand-rolled baseline still reads plain progressValue.
                        ["progressBaseMs"] = progressMs,
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
    /// JF-624 item 3 gate: true when the enhanced tappable disambiguation text list
    /// (AlexaTextList) may attach. Requires the dedicated AplEnhancedScreens flag
    /// (default false, out of the critical path), APL visuals enabled, and an
    /// APL-capable device context. Flag off leaves every ask response byte-identical
    /// (no carousel swap, no new directive), the task's AC#2.
    /// </summary>
    /// <param name="context">The Alexa request context for APL capability detection, or null.</param>
    /// <returns>True when the disambiguation text list should attach.</returns>
    internal static bool DisambiguationListEnabled(Context? context)
    {
        return Plugin.Instance?.Configuration?.AplEnhancedScreens == true
            && VisualsEnabled
            && context != null
            && DeviceSupportsApl(context);
    }

    /// <summary>
    /// Build the tappable disambiguation text-list directive (JF-624 item 3):
    /// an AlexaTextList whose primaryAction SendEvent carries
    /// [<paramref name="action"/>, "${ordinal}"]. The row list must be ordered
    /// EXACTLY like the session state it disambiguates, because the tap handler
    /// maps the 1-based ordinal onto that state by position (a tap behaves like
    /// the spoken answer with the cursor moved to the tapped row).
    /// The <see cref="ListDisplayItem.Id"/> is not rendered: the layout's own
    /// ${ordinal} is the tap identity. Subtitle becomes secondaryText, ArtUrl
    /// the row thumbnail. The document declares no backstack extension and
    /// carries no presentation session: it is a transient picker whose flow
    /// continues by voice or tap, never by back navigation.
    /// </summary>
    /// <param name="title">Localized header shown above the list.</param>
    /// <param name="items">Rows in the same order as the disambiguation session state.</param>
    /// <param name="action">SendEvent action name (DisambiguationTapAction or FindSongTapAction).</param>
    /// <returns>An APL RenderDocument directive, or null if the item list is empty.</returns>
    internal static AplRenderDocumentDirective? BuildDisambiguationListDirective(
        string title,
        List<ListDisplayItem> items,
        string action)
    {
        if (items.Count == 0)
        {
            return null;
        }

        var itemArray = new JArray();
        foreach (var item in items)
        {
            var row = new JObject
            {
                ["primaryText"] = item.Title
            };

            if (!string.IsNullOrEmpty(item.Subtitle))
            {
                row["secondaryText"] = item.Subtitle;
            }

            if (!string.IsNullOrEmpty(item.ArtUrl))
            {
                row["imageThumbnailSource"] = item.ArtUrl;
            }

            itemArray.Add(row);
        }

        return new AplRenderDocumentDirective
        {
            Token = "disambiguationList",
            Document = DisambiguationListDocument,
            DataSources = new JObject
            {
                ["disambigData"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["title"] = title,
                        ["action"] = action,
                        ["listItems"] = itemArray
                    }
                }
            }
        };
    }

    /// <summary>
    /// The row cap for the paginated picker (JF-624 item 4): a defensive bound on
    /// the directive payload (datasource bytes ride the skill response), high
    /// above every real season/chapter list observed (the largest are low
    /// hundreds). The cap keeps a PREFIX of the rows, so the positional tap
    /// identity of both flavors stays exact for every rendered row: chapter rows
    /// keep ordinal == chapter number, and episode rows carry their own explicit
    /// season/episode arguments. Rows past the cap stay reachable by voice.
    /// </summary>
    internal const int PaginatedListRowCap = 100;

    /// <summary>
    /// Build the tappable paginated picker directive (JF-624 item 4): an
    /// AlexaPaginatedList whose template-level primaryAction SendEvent carries
    /// [<paramref name="action"/>, "${ordinal}"] (the documented shape). Rows with
    /// <see cref="PaginatedListRow.TapArguments"/> set override that with their own
    /// exact per-item SendEvent (the documented list-item mechanism) so a tap
    /// never depends on positional identity when the row's pick value is not its
    /// position. The timeout is MAX (the display-document shape the persistence
    /// dossier prescribes): the picker mounts on session-ending responses, and
    /// SHORT's ~30s idle dismissal would kill the list while the user is still
    /// paging; a tap or voice one-shot re-enters the skill regardless.
    /// </summary>
    /// <param name="title">Localized header (the item or series name; no new locale keys).</param>
    /// <param name="rows">Picker rows in pick order.</param>
    /// <param name="action">SendEvent action name for rows without explicit tap arguments.</param>
    /// <returns>An APL RenderDocument directive, or null if the row list is empty.</returns>
    internal static AplRenderDocumentDirective? BuildPaginatedListDirective(
        string title,
        List<PaginatedListRow> rows,
        string action)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        var itemArray = new JArray();
        foreach (var row in rows.Take(PaginatedListRowCap))
        {
            var rowObj = new JObject
            {
                ["primaryText"] = row.PrimaryText
            };

            if (!string.IsNullOrEmpty(row.SecondaryText))
            {
                rowObj["secondaryText"] = row.SecondaryText;
            }

            if (row.TapArguments is { Length: > 0 } args)
            {
                rowObj["primaryAction"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "SendEvent",
                        ["arguments"] = new JArray(args)
                    }
                };
            }

            itemArray.Add(rowObj);
        }

        return new AplRenderDocumentDirective
        {
            Token = "paginatedPicker",
            TimeoutType = "MAX",
            Document = PaginatedListDocument,
            DataSources = new JObject
            {
                ["paginatedData"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["title"] = title,
                        ["action"] = action,
                        ["listItems"] = itemArray
                    }
                }
            }
        };
    }

    /// <summary>
    /// Attach the tappable paginated picker (JF-624 item 4) when the enhanced
    /// gate is on; a no-op otherwise (null context, flag off, no APL, or visuals
    /// off), so every caller's flag-off response is byte-identical and pays
    /// nothing: the row projection runs only when the gate opens. Unlike the
    /// item-3 attach helper, no caller branches on the outcome (item 3's
    /// AskFirstMatch falls back to the carousel; both picker surfaces have no
    /// fallback document), hence void.
    /// The gate predicate is <see cref="DisambiguationListEnabled"/>, the ONE
    /// AplEnhancedScreens family gate (flag + visuals + APL device,
    /// screen-agnostic): it serves every enhanced screen, item 3's
    /// disambiguation lists AND item 4's pickers alike.
    /// </summary>
    /// <param name="response">The response to attach the picker document to.</param>
    /// <param name="rows">Lazy row projection (invoked only when the gate opens).</param>
    /// <param name="title">Localized header (the item or series name).</param>
    /// <param name="action">SendEvent action for rows without explicit tap arguments.</param>
    /// <param name="context">The Alexa request context, or null.</param>
    internal static void AttachPaginatedListIfEnabled(
        SkillResponse response,
        Func<List<PaginatedListRow>> rows,
        string title,
        string action,
        Context? context)
    {
        if (!DisambiguationListEnabled(context))
        {
            return;
        }

        var directive = BuildPaginatedListDirective(title, rows(), action);
        if (directive != null)
        {
            response.Response.Directives.Add(directive);
        }
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
    /// Build an APL photo-slideshow directive (JF-329): a full-screen pager that
    /// cycles one image every 8 seconds (the document's tick-handler
    /// minimumDelay) with the title overlaid. Display-only: the document
    /// carries no touch handlers, so the response it rides may end the session.
    /// </summary>
    /// <param name="title">Header text overlaid on the slideshow.</param>
    /// <param name="imageUrls">URLs of the photos to cycle, in play order.</param>
    /// <param name="context">Optional Alexa request context for backstack navigation.</param>
    /// <returns>An APL RenderDocument directive, or null when no images are given.</returns>
    public static AplRenderDocumentDirective? BuildSlideshowDirective(string title, List<string> imageUrls, Context? context = null)
    {
        if (imageUrls.Count == 0)
        {
            return null;
        }

        var images = new JArray();
        foreach (string url in imageUrls)
        {
            images.Add(new JObject { ["url"] = url });
        }

        return new AplRenderDocumentDirective
        {
            Token = "slideshow",
            // The dossier's persistence rule (research_apl_persistence_exhaustive
            // 2026-05-25): SHORT (the default) dismisses a no-interaction document
            // after ~30s, which would cut a 30-photo x 8s slideshow to ~4 photos;
            // MAX rides the device's max idle timeout (~5min on Show 5), the shape
            // the dossier prescribes for display documents meant to persist.
            TimeoutType = "MAX",
            Document = SlideshowDocument,
            DataSources = new JObject
            {
                ["slideshowData"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["title"] = title,
                        ["images"] = images
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
