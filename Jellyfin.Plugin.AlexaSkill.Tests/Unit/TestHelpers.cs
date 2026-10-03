using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Jellyfin.Plugin.AlexaSkill.Lwa;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text.Json;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// The ONE shared fuzzy-match candidate shape (JF-573; replaces the four
/// private per-file copies: two positional records, one mutable class, one
/// new record). Callers construct positionally or via object initializer.
/// </summary>
internal record TestCandidate(string Name, Guid Id);

internal static class TestHelpers
{
    internal static Entities.User CreateTestUser(
        Guid? id = null,
        string invocationName = "test",
        string jellyfinToken = "test-token",
        IReadOnlyList<string>? allowedLibraryIds = null,
        SearchResponseMode? searchResponseMode = null)
    {
        return new Entities.User
        {
            Id = id ?? Guid.NewGuid(),
            InvocationName = invocationName,
            JellyfinToken = jellyfinToken,
            AllowedLibraryIds = allowedLibraryIds?.ToList(),
            SearchResponseMode = searchResponseMode
        };
    }

    /// <summary>
    /// The ONE SMAPI-capable plugin-user factory for catalog-sync suites: the
    /// <see cref="CreateTestUser"/> shape plus the device token, skill id and
    /// vendor id the sync entry gates require. Hoisted here on the third
    /// identical private construction (JF-695; the CreateLaunchBuilder
    /// convention). Tests needing an expired token overwrite
    /// <c>SmapiDeviceToken</c> after construction.
    /// </summary>
    internal static Entities.User CreateSyncUser(
        Guid? id = null,
        IReadOnlyList<string>? allowedLibraryIds = null)
    {
        return new Entities.User
        {
            Id = id ?? Guid.NewGuid(),
            InvocationName = "test",
            JellyfinToken = "test-token",
            SmapiDeviceToken = CreateTestDeviceToken(
                accessToken: "access-token", refreshToken: "refresh-token", expireTimestamp: 9999999999),
            UserSkill = new UserSkill { SkillId = "amzn1.ask.skill.test-id" },
            VendorId = "test-vendor-id",
            AllowedLibraryIds = allowedLibraryIds?.ToList()
        };
    }

    /// <summary>
    /// The ONE Jellyfin-user factory for tests that need a server-side user value
    /// (the JF-571 batch migrated all raw constructions here; the optional
    /// parameters absorb the non-default name/provider sites).
    /// </summary>
    internal static Jellyfin.Database.Implementations.Entities.User CreateJellyfinUser(
        string name = "testuser",
        string authProviderId = "test",
        string passwordProviderId = "test",
        Guid? id = null)
    {
        var user = new Jellyfin.Database.Implementations.Entities.User(name, authProviderId, passwordProviderId);
        if (id.HasValue)
        {
            user.Id = id.Value;
        }

        return user;
    }

    /// <summary>
    /// The ONE direct-construction PlaybackLaunchBuilder factory for the suites that
    /// test builder members without a handler host: the caller's config (suites mutate
    /// it before construction), an empty logger, and the truthful progressive-send
    /// delegate stand-in (the JF-501 virtual seam; no member these suites exercise
    /// sends a progressive response). Hoisted here on the third identical private
    /// construction (the CreateSong convention, JF-315 batch 9).
    /// </summary>
    internal static PlaybackLaunchBuilder CreateLaunchBuilder(PluginConfiguration config)
        => new(config, LoggerFactory.Create(b => { }).CreateLogger<PlaybackLaunchBuilder>(), (_, _, _) => Task.FromResult(true));

    /// <summary>
    /// The ONE interaction-model type-node extractor for the catalog suites'
    /// PUT-body assertions (JF-717 hoist of LegIsolationTests' GetTypeNode,
    /// the CreateSyncUser convention on the third private copy): parses a raw
    /// interaction-model JSON body and returns the language-model slot-type
    /// node with the given name. Clone() keeps the element valid after the
    /// owning document is disposed.
    /// </summary>
    internal static JsonElement GetModelTypeNode(string modelJson, string typeName)
    {
        using var doc = JsonDocument.Parse(modelJson);
        return doc.RootElement
            .GetProperty("interactionModel")
            .GetProperty("languageModel")
            .GetProperty("types")
            .EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == typeName)
            .Clone();
    }

    internal static DeviceToken CreateTestDeviceToken(
        string accessToken = "access",
        string refreshToken = "refresh",
        string tokenType = "Bearer",
        long expireTimestamp = 12345)
    {
        return new DeviceToken(accessToken, refreshToken, tokenType, expireTimestamp);
    }

    internal static void SetServerAddress(PluginConfiguration config, string address)
    {
        config.ServerAddress = address;
    }

    internal static SessionInfo CreateTestSession(ISessionManager sessionManager, ILoggerFactory loggerFactory)
    {
        return new SessionInfo(sessionManager, loggerFactory.CreateLogger<SessionInfo>());
    }

    /// <summary>
    /// Non-blocking form of <c>task.Wait(delay)</c>: true when the task
    /// completed within the delay. Used by the concurrency tests for the
    /// "cannot have completed yet" assertions (JF-449).
    /// </summary>
    /// <param name="task">The task to observe.</param>
    /// <param name="delay">How long to observe it.</param>
    /// <returns>True when the task completed within the delay.</returns>
    internal static async Task<bool> CompletedWithinAsync(Task task, TimeSpan delay)
        => await Task.WhenAny(task, Task.Delay(delay)).ConfigureAwait(false) == task;

    internal static Context CreateTestContext(string deviceId = "test-device")
    {
        return new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { AccessToken = Guid.NewGuid().ToString() },
                Device = new Device { DeviceID = deviceId }
            }
        };
    }

    internal static Context CreateContextWithApl()
    {
        return new Context
        {
            System = new AlexaSystem
            {
                Device = new Device
                {
                    DeviceID = "test-device",
                    SupportedInterfaces = new Dictionary<string, object>
                    {
                        { "Alexa.Presentation.APL", new { } }
                    }
                },
                ApiAccessToken = "test-token",
                Application = new Application { ApplicationId = "test-app" }
            }
        };
    }

    /// <summary>
    /// The ONE Audio factory for play-response suites (name + optional id; the
    /// JF-315 batch-4 reuse pass hoisted the third private copy here).
    /// </summary>
    internal static MediaBrowser.Controller.Entities.Audio.Audio CreateSong(
        string name = "Test Song", Guid? id = null, string[]? genres = null)
    {
        var audio = new MediaBrowser.Controller.Entities.Audio.Audio { Name = name, Id = id ?? Guid.NewGuid() };
        if (genres != null)
        {
            audio.Genres = genres;
        }

        return audio;
    }

    internal static Context CreateContextWithoutApl()
    {
        return new Context
        {
            System = new AlexaSystem
            {
                Device = new Device
                {
                    DeviceID = "test-device",
                    SupportedInterfaces = new Dictionary<string, object>()
                },
                ApiAccessToken = "test-token",
                Application = new Application { ApplicationId = "test-app" }
            }
        };
    }

    /// <summary>
    /// JF-505: a screen-capable device (SupportedInterfaces reports the VideoApp key,
    /// the Echo Show shape).
    /// </summary>
    internal static Context CreateContextWithVideoApp(string deviceId = "echo-show")
    {
        return new Context
        {
            System = new AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { AccessToken = Guid.NewGuid().ToString() },
                Device = new Device
                {
                    DeviceID = deviceId,
                    SupportedInterfaces = new Dictionary<string, object> { { "VideoApp", new { } } }
                }
            }
        };
    }

    /// <summary>
    /// JF-505: a screenless device (it reports interfaces but NOT VideoApp, the Echo
    /// Dot shape that rejects VideoApp.Launch with an audible platform error).
    /// </summary>
    internal static Context CreateScreenlessContext(string deviceId = "echo-dot")
    {
        return new Context
        {
            System = new AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { AccessToken = Guid.NewGuid().ToString() },
                Device = new Device
                {
                    DeviceID = deviceId,
                    SupportedInterfaces = new Dictionary<string, object> { { "AudioPlayer", new { } } }
                }
            }
        };
    }

    /// <summary>
    /// The first AudioPlayer.Play directive of a response, or null (shared by the
    /// cross-media fallback test suites).
    /// </summary>
    internal static AudioPlayerPlayDirective? GetPlayDirective(SkillResponse response)
        => response.Response?.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;

    /// <summary>
    /// A slot carrying an ER authority: an ER_SUCCESS_MATCH resolving
    /// <paramref name="rawValue"/> to <paramref name="canonical"/> by default, or
    /// any other status code for the no-match shapes (JF-659 hoist; the shared
    /// builder of the JF-642/JF-659 suites). JF-653 folded the last four per-class
    /// ER-graph builders in here (the speed, episode-position and genre suites):
    /// a null <paramref name="canonical"/> stays the bare raw-value slot, and
    /// <paramref name="id"/> rides the authority value's Id next to the canonical
    /// name (never a graph trigger on its own; no suite mints an id-only match).
    /// </summary>
    /// <param name="slotName">The slot's name; defaults to the musician slot the
    /// JF-659 suites exercise.</param>
    internal static Slot ResolvedSlot(string rawValue, string? canonical, string statusCode = "ER_SUCCESS_MATCH", string slotName = "musician", string? id = null)
    {
        var slot = new Slot { Name = slotName, Value = rawValue };
        if (canonical != null)
        {
            slot.Resolution = new Resolution
            {
                Authorities = new[]
                {
                    new ResolutionAuthority
                    {
                        Status = new ResolutionStatus { Code = statusCode },
                        Values = new[]
                        {
                            new ResolutionValueContainer
                            {
                                Value = new ResolutionValue { Name = canonical, Id = id }
                            }
                        }
                    }
                }
            };
        }

        return slot;
    }

    /// <summary>
    /// JF-690: a slot carrying a MULTI-VALUE ER authority: an ER_SUCCESS_MATCH
    /// listing several canonical values in Amazon's likelihood-rank order (the
    /// shared-first-word catalog-synonym shape, live: raw "pink" resolving to
    /// [P!nk, Pink Floyd]). The single-value <see cref="ResolvedSlot"/> shape
    /// cannot express it; this builder mirrors its construction otherwise.
    /// </summary>
    /// <param name="rawValue">The raw spoken slot value.</param>
    /// <param name="canonicalValues">The matched canonical names, rank #1 first.</param>
    internal static Slot ResolvedSlotMultiValue(string rawValue, params string[] canonicalValues)
    {
        var slot = new Slot { Name = "musician", Value = rawValue };
        slot.Resolution = new Resolution
        {
            Authorities = new[]
            {
                new ResolutionAuthority
                {
                    Status = new ResolutionStatus { Code = "ER_SUCCESS_MATCH" },
                    Values = canonicalValues
                        .Select(v => new ResolutionValueContainer { Value = new ResolutionValue { Name = v } })
                        .ToArray()
                }
            }
        };
        return slot;
    }

    /// <summary>
    /// JF-562/JF-564: a context whose AudioPlayer carries the given stream token on a
    /// PLAYING device (the token-vs-ledger displacement shape the transport suites
    /// exercise; previously one private copy per suite). JF-315 batch 5: the player
    /// activity is parameterizable (the IsActivelyPlaying theory pins every activity
    /// state) and the token nullable (activity-only shapes).
    /// </summary>
    internal static Context CreateContextWithToken(string? token, string deviceId = "test-device", string playerActivity = "PLAYING")
    {
        var context = CreateTestContext(deviceId);
        context.AudioPlayer = new PlaybackState
        {
            Token = token,
            OffsetInMilliseconds = 90_000,
            PlayerActivity = playerActivity
        };
        return context;
    }

    /// <summary>
    /// JF-562/JF-564: asserts the response carries NO AudioPlayer.Play directive (the
    /// honest-refusal shape; a mid-video audio launch is the JF-564 bug class).
    /// </summary>
    internal static void AssertNoAudioPlayDirective(SkillResponse response)
        => Assert.DoesNotContain(
            response.Response.Directives ?? new List<IDirective>(),
            d => d is AudioPlayerPlayDirective);

    /// <summary>
    /// JF-420.2/JF-707: the stored-state contract of a multi-artist ask: the
    /// FULL resolved list (ids and names in rank order, winner first, the
    /// entry a plain "yes" plays), type artist, cursor 0. The ask's SPOKEN
    /// prompt names only the top
    /// <see cref="DisambiguationHelper.MultipleArtistsSpeakCap"/> matches
    /// while this state keeps every match (the yes/no cycling walks the full
    /// list); <see cref="AssertMultiArtistAsk"/> composes this contract with
    /// the cap-aware speech asserts.
    /// </summary>
    internal static void AssertStoredArtistMatches(SkillResponse response, params BaseItem[] expected)
    {
        var state = DisambiguationHelper.ReadState(response.SessionAttributes);
        Assert.NotNull(state);
        Assert.Equal(expected.Select(a => a.Id.ToString()), state!.Value.Matches.Select(m => m.Id));
        Assert.Equal(expected.Select(a => a.Name), state.Value.Matches.Select(m => m.Name));
        Assert.Equal(DisambiguationHelper.MediaTypeArtist, state.Value.MediaType);
        Assert.Equal(0, state.Value.Index);
    }

    /// <summary>
    /// JF-690: asserts a response carries NO disambiguation session state (the
    /// complement of <see cref="AssertMultiArtistAsk"/>; hoisted from the
    /// twin private copies in both MusicianMultiValueEr suites, which had
    /// drifted to different key subsets). Checks both state keys: the primary
    /// match list and the type marker.
    /// </summary>
    internal static void AssertNoDisambiguationState(SkillResponse response)
    {
        Assert.Null(response.SessionAttributes?.GetValueOrDefault(DisambiguationHelper.AttrMatches));
        Assert.Null(response.SessionAttributes?.GetValueOrDefault(DisambiguationHelper.AttrType));
    }

    /// <summary>
    /// JF-690/JF-707: the ONE oracle for the multi-artist disambiguation ask
    /// (hoisted from the twin private copies in both MusicianMultiValueEr
    /// suites): open session, no play directive, the stored FULL resolved list
    /// via <see cref="AssertStoredArtistMatches"/>, and CAP-AWARE speech
    /// asserts: the top <see cref="DisambiguationHelper.MultipleArtistsSpeakCap"/>
    /// candidates must be spoken, every candidate beyond the cap must NOT be
    /// (JF-707), so a leg with more than three expected artists pins the
    /// capped breath instead of failing on correct behavior. Beware substring
    /// fixtures: a tail name contained in a spoken head name would trip the
    /// DoesNotContain pass.
    /// </summary>
    internal static void AssertMultiArtistAsk(SkillResponse response, params BaseItem[] expected)
    {
        AssertSessionOpen(response, "the ask keeps the session open");
        Assert.Null(GetPlayDirective(response));
        AssertStoredArtistMatches(response, expected);
        string speech = GetSpeechText(response);
        foreach (BaseItem artist in expected.Take(DisambiguationHelper.MultipleArtistsSpeakCap))
        {
            Assert.Contains(artist.Name, speech, StringComparison.OrdinalIgnoreCase);
        }

        foreach (BaseItem artist in expected.Skip(DisambiguationHelper.MultipleArtistsSpeakCap))
        {
            Assert.DoesNotContain(artist.Name, speech, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// JF-687/JF-693: the ONE oracle for the empty-StreamTokenSecret refusal Tell:
    /// no playback directive of either kind (the production predicate), the response
    /// is a session-ending Tell, and the speech is the localized
    /// <c>StreamTokenNotConfigured</c> string (hoisted from the builder suite when
    /// the JF-693 handler pins started re-inlining it; the en-US flavor is the
    /// default-locale call).
    /// </summary>
    internal static void AssertStreamTokenRefusalTell(SkillResponse response, string locale = "en-US")
    {
        Assert.False(
            PlaybackLaunchBuilder.HasLaunchDirective(response),
            "a refused launch must not deliver any playback directive");
        Assert.True(response.Response.ShouldEndSession, "the configuration answer is a Tell");
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Equal(ResponseStrings.Get("StreamTokenNotConfigured", locale), speech.Text);
    }

    /// <summary>
    /// JF-564: asserts the response carries the AudioPlayer.Stop directive (the
    /// audio-stop invariant every pause/stop/cancel path must keep).
    /// </summary>
    internal static void AssertHasAudioPlayerStopDirective(SkillResponse response)
    {
        Assert.NotNull(response.Response.Directives);
        Assert.Contains(response.Response.Directives, d => d is StopDirective);
    }

    /// <summary>
    /// The speech text of a response whose OutputSpeech may legitimately be null (a
    /// silent AudioPlayer start): null means no speech at all, which is exactly what
    /// the announce-off tests want to prove. SSML announcements count as speech: the
    /// markup is stripped so content assertions see the spoken words.
    /// </summary>
    internal static string? GetSpeechTextOrNull(SkillResponse response)
        => response.Response?.OutputSpeech switch
        {
            PlainTextOutputSpeech plain => plain.Text,
            SsmlOutputSpeech ssml => GetSpeechText(response),
            _ => null,
        };

    /// <summary>
    /// JF-358/JF-667 shared pin: Jellyfin initializes MediaTypes to an empty array,
    /// so the contract is "no MediaTypes filter", not the field's exact null/empty
    /// shape.
    /// </summary>
    internal static void AssertNoMediaTypesFilter(InternalItemsQuery query, string what)
        => Assert.True(
            query.MediaTypes == null || query.MediaTypes.Length == 0,
            $"{what} must not filter via MediaTypes (JF-358/JF-667)");

    /// <summary>
    /// Extract speech text from a SkillResponse, handling both plain text and SSML output.
    /// Strips SSML markup for content assertions.
    /// </summary>
    internal static string GetSpeechText(SkillResponse response)
    {
        if (response.Response.OutputSpeech is SsmlOutputSpeech ssml)
        {
            string raw = ssml.Ssml;
            raw = raw.Replace("<speak>", string.Empty).Replace("</speak>", string.Empty);
            raw = Regex.Replace(raw, "<break[^>]*>", " ");
            raw = Regex.Replace(raw, "<say-as[^>]*>", string.Empty);
            raw = raw.Replace("</say-as>", string.Empty);
            raw = Regex.Replace(raw, "<prosody[^>]*>", string.Empty);
            raw = raw.Replace("</prosody>", string.Empty);
            raw = Regex.Replace(raw, "<w[^>]*>", string.Empty);
            raw = raw.Replace("</w>", string.Empty);
            raw = Regex.Replace(raw, @"\s+", " ").Trim();
            return raw;
        }

        var speech = global::Xunit.Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        return speech.Text;
    }

    /// <summary>
    /// Asserts that the response keeps the session open (ShouldEndSession is explicitly false).
    /// This is more precise than checking null || false — ResponseBuilder.Ask() always sets false.
    /// </summary>
    internal static void AssertSessionOpen(SkillResponse response, string message = "Session should remain open")
    {
        global::Xunit.Assert.NotNull(response);
        global::Xunit.Assert.False(response.Response.ShouldEndSession ?? true, message);
    }

    /// <summary>
    /// Asserts the elicit-response shape (JF-549/JF-550): open session, a
    /// Dialog.ElicitSlot directive targeting the given slot of the given intent,
    /// and a reprompt. The one shared assertion for every dead-mic sweep pin
    /// (was eight per-file copies).
    /// </summary>
    internal static void AssertElicitsSlot(SkillResponse response, string slotToElicit, string intentName, IEnumerable<string>? expectedSlotSet = null)
    {
        AssertSessionOpen(response, "a question must keep the session open or the mic never listens");
        global::Xunit.Assert.NotNull(response.Response.Reprompt);
        var elicit = response.Response.Directives?.FirstOrDefault(d => d.Type == "Dialog.ElicitSlot")
            as Jellyfin.Plugin.AlexaSkill.Alexa.Directive.ElicitSlotDirective;
        global::Xunit.Assert.NotNull(elicit);
        global::Xunit.Assert.Equal(slotToElicit, elicit!.SlotToElicit);
        global::Xunit.Assert.Equal(intentName, elicit.UpdatedIntent.Name);
        if (expectedSlotSet != null)
        {
            // JF-612 review: the updatedIntent slot set is the live mirror of the
            // allSlotNames the Phase 8 checker validates; a drift between them
            // must fail here, not only in CI.
            var actual = elicit.UpdatedIntent.Slots?.Keys.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
            global::Xunit.Assert.Equal(expectedSlotSet.ToHashSet(StringComparer.Ordinal), actual);
        }
    }

    /// <summary>
    /// Ensures a real Plugin instance exists (funnel/controller tests that read
    /// Plugin.Instance.Configuration deep inside the request path). Thin wrapper
    /// over <see cref="EnsurePluginInstance"/>: the construction block has ONE
    /// owner; this only pins the ServerAddress the funnel's session lookup reads.
    /// </summary>
    internal static void EnsureRealPlugin()
        => EnsurePluginInstance(
            new PluginConfiguration(),
            LoggerFactory.Create(b => { }),
            c => c.ServerAddress = "http://localhost:8096",
            "alexa-skill-test");

    /// <summary>
    /// Mints <c>&lt;suffix&gt;-&lt;guid&gt;</c> under the temp path, creates it, and
    /// registers it with PluginTempDirCleanup for deletion at process exit
    /// (JF-453/JF-486). Test code minting GUID temp dirs MUST go through this
    /// helper so the Register call cannot be forgotten.
    /// </summary>
    internal static string CreateRegisteredTempDir(string suffix)
    {
        var dir = Path.Combine(Path.GetTempPath(), suffix + "-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        PluginTempDirCleanup.Shared.Register(dir);
        return dir;
    }

    /// <summary>
    /// A real <see cref="AudiobookPositionTracker"/> on a registered temp dir (the class
    /// is sealed, so tests use the real thing). Disposal: via
    /// <see cref="SwapPluginPositionTracker"/> when the tracker is swapped onto the
    /// plugin (the scope owns it), or manually (Dispose in a finally) when used
    /// off-plugin; the swap scope disposes ONLY swapped-in instances.
    /// </summary>
    internal static AudiobookPositionTracker CreatePositionTracker(string nameSuffix)
        => new(
            CreateRegisteredTempDir(nameSuffix + "-abt"),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AudiobookPositionTracker>.Instance);

    /// <summary>
    /// JF-540: the ONE DeviceQueueManager factory on its own registered temp dir
    /// (<paramref name="nameSuffix"/> + "-dq"). The ctor loads every queue_*.json in
    /// its data dir, so a shared root makes each fixture cross-load (and
    /// <c>PersistAll</c> rewrite) the whole accumulating pool at every teardown; the
    /// registered dir sweeps at process exit. Disposal stays the caller's belt (the
    /// 2s debounce teardown, JF-535).
    /// </summary>
    internal static DeviceQueueManager CreateDeviceQueueManager(
        string nameSuffix,
        ILogger<DeviceQueueManager>? logger = null)
        => new DeviceQueueManager(
            CreateRegisteredTempDir(nameSuffix + "-dq"),
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DeviceQueueManager>.Instance);

    /// <summary>
    /// JF-630: the ONE Plugin.Instance.DeviceQueueManager swap scope (was four
    /// suite-level capture/assign/restore/dispose copies plus two method-level
    /// swap-to-null pairs). Contract lives on the shared core
    /// <see cref="PluginInstanceSwap{T}"/>: restore-before-dispose, scope-owned
    /// disposal, and restore-previous honestly covers the former swap-to-null sites
    /// (the test host's plugin instance is minted per test class and only
    /// SkillStartup, which never runs here, assigns the manager, so the captured
    /// previous value IS null there).
    /// </summary>
    internal static IDisposable SwapPluginQueueManager(DeviceQueueManager manager)
        => new PluginInstanceSwap<DeviceQueueManager>(
            manager, p => p.DeviceQueueManager, (p, v) => p.DeviceQueueManager = v);

    /// <summary>
    /// JF-633: the ONE Plugin.Instance.AudiobookPositionTracker swap scope (was ten
    /// method-level assign / restore-null / dispose finallys). Same shared-core
    /// contract as <see cref="SwapPluginQueueManager"/>: restore-before-dispose and
    /// scope-owned tracker disposal (the persist debounce teardown), with
    /// restore-previous honestly covering the former restore-to-null sites (only
    /// SkillStartup.StartAsync, which never runs in the test host, assigns the
    /// tracker in production, so the captured previous value IS null here too).
    /// </summary>
    internal static IDisposable SwapPluginPositionTracker(AudiobookPositionTracker tracker)
        => new PluginInstanceSwap<AudiobookPositionTracker>(
            tracker, p => p.AudiobookPositionTracker, (p, v) => p.AudiobookPositionTracker = v);

    /// <summary>
    /// The ONE BaseItem statics stub scope (JF-713 hoist, the CreateSong
    /// convention on the third identical private construction): the playlist
    /// suites' visibility filter and linked-child resolution walk the STATIC
    /// <c>BaseItem.LibraryManager</c>/<c>BaseItem.Logger</c>, unset in the
    /// unit-test host. Wires <paramref name="libraryManagerMock"/>'s
    /// GetCollectionFolders to empty, installs the mock plus a null logger, and
    /// restores both previous values on Dispose. The suite runs sequentially
    /// (the "[Collection(\"Plugin\")]" classes), so the transient static mutation
    /// cannot race. Former per-file twins: PlayPlaylistIntentHandlerTests,
    /// KanaOriginPlaylistSurfaceTests, AlbumPlayServicePlaylistShuffleTests.
    /// </summary>
    internal static IDisposable StubBaseItemStatics(Mock<ILibraryManager> libraryManagerMock)
    {
        ILibraryManager? prevLibraryManager = BaseItem.LibraryManager;
        ILogger<BaseItem>? prevLogger = BaseItem.Logger;

        libraryManagerMock.Setup(l => l.GetCollectionFolders(It.IsAny<BaseItem>()))
            .Returns(new List<Folder>());
        BaseItem.LibraryManager = libraryManagerMock.Object;
        BaseItem.Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<BaseItem>.Instance;

        return new RestoreBaseItemStatics(prevLibraryManager, prevLogger);
    }

    private sealed class RestoreBaseItemStatics : IDisposable
    {
        private readonly ILibraryManager? _libraryManager;
        private readonly ILogger<BaseItem>? _logger;

        internal RestoreBaseItemStatics(ILibraryManager? libraryManager, ILogger<BaseItem>? logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
        }

        public void Dispose()
        {
            BaseItem.LibraryManager = _libraryManager;
            BaseItem.Logger = _logger;
        }
    }

    /// <summary>
    /// The ONE Plugin.Instance swap core behind <see cref="SwapPluginQueueManager"/>
    /// and <see cref="SwapPluginPositionTracker"/> (JF-633: the ordering invariant
    /// lives in exactly one place, not one hand-copied scope class per property).
    /// Captures the previous value via <paramref name="get"/>, assigns
    /// <paramref name="swappedIn"/> via <paramref name="assign"/>, and on Dispose
    /// restores the captured previous value BEFORE disposing the swapped-in instance
    /// (nothing may read a disposed instance through the plugin). The scope owns the
    /// swapped-in instance's disposal: pass one whose lifetime ends with the scope
    /// (every site creates a fresh one for exactly this shape).
    /// </summary>
    private sealed class PluginInstanceSwap<T> : IDisposable
        where T : class, IDisposable
    {
        private readonly Action<Plugin, T?> _assign;
        private readonly T? _previous;
        private readonly T _swappedIn;

        internal PluginInstanceSwap(T swappedIn, Func<Plugin, T?> get, Action<Plugin, T?> assign)
        {
            // Loud by design (the JF-630 review's call): a swap issued before
            // EnsurePluginInstance must THROW, not silently skip the assign while
            // Dispose still disposes the instance (a green test never exercising the
            // swapped value). Every call site runs EnsurePluginInstance first.
            Plugin plugin = Plugin.Instance!;
            _previous = get(plugin);
            _swappedIn = swappedIn;
            _assign = assign;

            // The restore contract's assumption made LOUD (the JF-633 review): every
            // swap site starts from a null previous (SkillStartup, the only production
            // assigner, never runs in the test host). A future test that installs one
            // of these properties without this scope would make the NEXT swap capture
            // it as previous and re-install it at teardown, possibly disposed; this
            // assertion fires at the moment that first happens instead.
            if (_previous is not null)
            {
                throw new InvalidOperationException(
                    $"Plugin.Instance already holds a {_previous.GetType().Name} (a non-swap assignment leaked). The swap scopes assume a null previous; assign via the swap helpers only.");
            }

            assign(plugin, swappedIn);
        }

        public void Dispose()
        {
            // The dispose is guaranteed even when the restore throws (the JF-633
            // review: a null Instance at teardown previously skipped it, leaking the
            // armed persist debounce the scope exists to tear down).
            try
            {
                _assign(Plugin.Instance!, _previous);
            }
            finally
            {
                _swappedIn.Dispose();
            }
        }
    }

    /// <summary>
    /// The SANCTIONED EXCEPTION to the swap core (JF-527 harness,
    /// EventHandlerTests.RecordPreviousPlayOnHarnessDevice): that site deliberately
    /// assigns and disposes a manager in place, leaving the disposed-but-readable
    /// instance attached for the handler under test. It must NOT convert to the
    /// swap scopes (restore-before-dispose would unplug it before the handler
    /// reads); any new swap call in its class will trip the null-previous
    /// assertion above, pointing here.
    /// </summary>

    /// <summary>
    /// Sets Plugin.Instance with the provided configuration so IfFeatureDisabled
    /// can read from Plugin.Instance.Configuration. When the instance already exists,
    /// only the specific flag is synced via <paramref name="syncFlag"/>. The temp
    /// dir minted for the mocked paths is registered with PluginTempDirCleanup for
    /// deletion at process exit (JF-453).
    /// </summary>
    internal static void EnsurePluginInstance(
        PluginConfiguration config,
        ILoggerFactory loggerFactory,
        Action<PluginConfiguration> syncFlag,
        string tempDirSuffix)
    {
        if (Plugin.Instance != null)
        {
            syncFlag(Plugin.Instance.Configuration);
            return;
        }

        var tmpDir = CreateRegisteredTempDir(tempDirSuffix);

        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(p => p.PluginsPath).Returns(tmpDir);
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(tmpDir);
        appPaths.Setup(p => p.DataPath).Returns(tmpDir);
        appPaths.Setup(p => p.CachePath).Returns(tmpDir);
        appPaths.Setup(p => p.LogDirectoryPath).Returns(tmpDir);
        appPaths.Setup(p => p.ConfigurationDirectoryPath).Returns(tmpDir);
        appPaths.Setup(p => p.SystemConfigurationFilePath).Returns(Path.Combine(tmpDir, "system.xml"));
        appPaths.Setup(p => p.ProgramDataPath).Returns(tmpDir);
        appPaths.Setup(p => p.ProgramSystemPath).Returns(tmpDir);
        appPaths.Setup(p => p.TempDirectory).Returns(tmpDir);
        appPaths.Setup(p => p.VirtualDataPath).Returns(tmpDir);

        var xmlSerializer = new Mock<IXmlSerializer>();
        xmlSerializer
            .Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>()))
            .Returns(config);

        var userManager = new Mock<IUserManager>();

        var plugin = new Plugin(
            appPaths.Object,
            xmlSerializer.Object,
            loggerFactory,
            userManager.Object);

        plugin.Configuration.ServerAddress = "http://localhost:8096";
    }

    /// <summary>
    /// Polls <paramref name="condition"/> until it returns true or the timeout
    /// elapses (JF-419.3: shared by the index services' timing tests instead of
    /// duplicating the deadline/while loop).
    /// </summary>
    /// <returns>True when the condition was met before the timeout.</returns>
    internal static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, int pollMs = 25)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(pollMs).ConfigureAwait(false);
        }

        return condition();
    }

    /// <summary>
    /// The ONE hardware Play-button request (PlaybackControllerRequest's request
    /// type is read-only, so it is deserialized from the wire JSON). Was an inline
    /// literal in PauseResumeStateTests and DispatchRoutingTests (JF-433 simplify).
    /// </summary>
    internal static global::Alexa.NET.Request.Type.PlaybackControllerRequest CreatePlayCommand()
    {
        const string json = @"{""requestId"":""test"",""type"":""PlaybackController.PlayCommandIssued"",""timestamp"":""2024-01-01T00:00:00Z"",""locale"":""en-US"",""playbackRequestMethod"":""PLAY""}";
        return Newtonsoft.Json.JsonConvert.DeserializeObject<global::Alexa.NET.Request.Type.PlaybackControllerRequest>(json)!;
    }

    /// <summary>
    /// JF-440: the ONE song-index fake (was duplicated as FakeSongIndex in
    /// PlayArtistSongsIntentHandlerTests and FakeNgramIndex in PlaySongTitleFallbackTests).
    /// Returns the fixed scored set from both stages (no test distinguishes them).
    /// </summary>
    internal sealed class FakeSongIndex : ISongNgramIndex
    {
        private readonly List<(BaseItem Item, double Score)> _results;
        public bool IsReady => true;
        public bool IsDisabled => false;
        public int SongCount => _results.Count;
        public int NgramCount => _results.Count;

        public FakeSongIndex(params (BaseItem Item, double Score)[] results) => _results = results.ToList();

        public List<(BaseItem Item, double Score)> Search(string[] keywordTokens, string locale, Guid[]? topParentIds = null) => _results;
        public List<(BaseItem Item, double Score)> SearchPhonetic(string[] keywordTokens, string locale, Guid[]? topParentIds = null) => _results;
    }

    /// <summary>
    /// The ONE MediaStream factory (JF-520): was a private <c>Stream(...)</c> copy in
    /// the resume suites; the other seam users built <c>new MediaStream {...}</c>
    /// inline to the same shape.
    /// </summary>
    internal static MediaStream TestStream(MediaStreamType type, string codec) => new() { Type = type, Codec = codec };

    /// <summary>
    /// JF-520: the ONE <c>BaseItem.GetMediaStreams()</c> override seam (Episode twin).
    /// Overriding the streams lets wiring tests exercise the REAL codec probe +
    /// routing decision (under the test host there is no statically injected
    /// MediaSourceManager, so a plain item's probe degrades to unknown codec and
    /// keeps the static URL). Was 3 private <c>EpisodeWithStreams</c> copies across
    /// the resume/yes suites; the Movie twin is <see cref="TestMovieWithStreams"/>.
    /// </summary>
    internal sealed class TestEpisodeWithStreams : MediaBrowser.Controller.Entities.TV.Episode
    {
        private readonly List<MediaStream> _streams;

        public TestEpisodeWithStreams(string name, Guid id, params MediaStream[] streams)
        {
            Name = name;
            Id = id;
            _streams = streams.ToList();
        }

        public override IReadOnlyList<MediaStream> GetMediaStreams() => _streams;
    }

    /// <summary>
    /// The ONE remux-eligible episode fixture (h264 video + EAC3 audio, one hour of
    /// runtime, the JF-565 clamp's resumable shape): routes to the episode HLS remux on
    /// the VideoApp path and to the audio-only transcode on the AudioPlayer path. Was 2
    /// private RemuxEpisode copies (PlaybackLaunchBuilderEpisodeResumeTests, the JF-687
    /// stream-token-secret suite).
    /// </summary>
    internal static TestEpisodeWithStreams RemuxEpisode(Guid? id = null)
        => new(
            "The Convention",
            id ?? Guid.NewGuid(),
            TestStream(MediaStreamType.Video, "h264"),
            TestStream(MediaStreamType.Audio, "eac3"))
        {
            RunTimeTicks = TimeSpan.FromMinutes(60).Ticks
        };

    /// <summary>
    /// Movie twin of <see cref="TestEpisodeWithStreams"/> (ResolveAudioLaunchSource and
    /// the VideoApp launch routing match Movie and Episode). Was 2 private copies
    /// (MovieWithStreams in PlayVideoIntentHandlerTests, the awkwardly-named
    /// EpisodeWithStreamsTestMovie in ResumeIntentAudioVariantOffsetTests).
    /// </summary>
    internal sealed class TestMovieWithStreams : MediaBrowser.Controller.Entities.Movies.Movie
    {
        private readonly List<MediaStream> _streams;

        public TestMovieWithStreams(string name, Guid id, params MediaStream[] streams)
        {
            Name = name;
            Id = id;
            _streams = streams.ToList();
        }

        public override IReadOnlyList<MediaStream> GetMediaStreams() => _streams;
    }
}

/// <summary>
/// JF-446: the ONE ready artist-index fake. History: one private copy lived in
/// ArtistSearchTests at HEAD and was hoisted here (unchanged shape) so the new
/// JF-446 gate tests could share it. Ready by default (pass
/// <c>isReady: false</c> for warming-gate shapes); phonetic codes are optional so a
/// test can exercise either the phonetic or the plain FuzzyMatcher overload.
/// </summary>
internal sealed class FakeArtistIndex : IArtistIndex
{
    private readonly IReadOnlyList<BaseItem> _artists;
    private readonly Dictionary<Guid, (string Primary, string? Alternate)> _phoneticCodes;
    private readonly bool _isReady;

    public FakeArtistIndex(
        IEnumerable<BaseItem> artists,
        Dictionary<Guid, (string Primary, string? Alternate)>? phoneticCodes = null,
        bool isReady = true)
    {
        _artists = artists.ToList();
        _phoneticCodes = phoneticCodes ?? new Dictionary<Guid, (string Primary, string? Alternate)>();
        _isReady = isReady;
    }

    public bool IsReady => _isReady;
    public bool IsDisabled => false;
    public int Count => _artists.Count;

    public IReadOnlyList<BaseItem> GetArtists(Guid[]? topParentIds = null) => _artists;

    public bool TryGetPhoneticCode(Guid artistId, out (string Primary, string? Alternate) codes)
    {
        codes = default;
        return _phoneticCodes.TryGetValue(artistId, out codes);
    }

    // The fake's state is fixed per instance, so it is already pinned: capture is the
    // identity (the same contract ArtistIndexService.SnapshotView honors).
    public IArtistIndex CaptureSnapshot() => this;

    /// <summary>
    /// Phonetic-code table for a <see cref="FakeArtistIndex"/> from the artists'
    /// own names, via the production encoder (the KN collision between Queen and
    /// Keane is the live tie shape, not a hand-written assumption). Hoisted here
    /// (JF-660) from the kana test family; the two pre-existing private copies
    /// (KanaOriginAcceptanceTests, MusicianErCanonicalTests) fold in on their next
    /// edit.
    /// </summary>
    public static Dictionary<Guid, (string Primary, string? Alternate)> CodesFromArtistNames(params BaseItem[] artists)
        => artists.ToDictionary(a => a.Id, a => DoubleMetaphone.Encode(a.Name!));
}

/// <summary>
/// JF-465: the ONE handler-test fixture (the six standard mocks + config +
/// logger factory, and the SetupUserMock/CreateSession/CreateContext/CreateUser
/// builders). History: the block was copy-pasted across ~33 handler test classes
/// (seed: PlayByGenreFallbackTests vs PlayByGenreIntentHandlerTests, near
/// line-for-line) so every optional-ctor-dep edit touched every copy. Test classes
/// hold one instance and compose it, keeping their handler-specific CreateHandler
/// ctor call local. Handler subsets beyond these six mocks (extra indexes, queue
/// managers) stay as local fields in the owning test class.
/// </summary>
internal sealed class HandlerTestFixture
{
    internal Mock<ISessionManager> SessionManager { get; }
    internal Mock<ILibraryManager> LibraryManager { get; }
    internal Mock<IUserManager> UserManager { get; }
    internal Mock<IUserDataManager> UserDataManager { get; }
    internal Mock<ITVSeriesManager> TvSeriesManager { get; }
    internal PluginConfiguration Config { get; }
    internal ILoggerFactory LoggerFactory { get; }

    /// <summary>
    /// <paramref name="serverAddress"/> defaults to the shared test address; the few
    /// suites that pin localhost pass it explicitly, and passing null leaves the
    /// address untouched (the no-address suites). <paramref name="configure"/> runs
    /// BEFORE the address is set so a suite can pin any other flag it needs.
    /// </summary>
    internal HandlerTestFixture(
        string? serverAddress = "https://test.example.com",
        Action<PluginConfiguration>? configure = null)
    {
        SessionManager = new Mock<ISessionManager>();
        LibraryManager = new Mock<ILibraryManager>();
        UserManager = new Mock<IUserManager>();
        UserDataManager = new Mock<IUserDataManager>();
        TvSeriesManager = new Mock<ITVSeriesManager>();
        Config = new PluginConfiguration();
        configure?.Invoke(Config);
        if (serverAddress != null)
        {
            TestHelpers.SetServerAddress(Config, serverAddress);
        }

        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => { });
    }

    /// <summary>
    /// The standard GetUserById stub: a Jellyfin user named "testuser", so
    /// ResolveJellyfinUser-style paths have someone to resolve.
    /// </summary>
    internal void SetupUserMock()
        => UserManager.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(TestHelpers.CreateJellyfinUser());

    /// <summary>
    /// Mocks the JF-411 indefinite album-by-artist flow on the fixture's LibraryManager:
    /// artist lookup, the artist's albums in the given (insertion) order, per-album
    /// track counts for the JF-443 COUNT queries, and per-album playback results. The
    /// count semantics mirror the TWO server mechanisms (Jellyfin BaseItemRepository
    /// 10.11.8/10.11.11): ParentId answers by entity link (well-formed albums),
    /// AlbumIds answers by matching the track's RAW Album tag against the album
    /// entity's Name (f.Name == e.Album; the JF-338 malformed-folder shape). Queries
    /// are recorded into <paramref name="queries"/> (when given) for assertions.
    /// Hoisted from PlayAlbumIntentHandlerTests for the DialogDelegation slim
    /// (JF-442); pair it with the caller's own GetPlayedTrackToken-style extraction.
    /// </summary>
    internal void SetupIndefiniteAlbumCatalog(
        BaseItem artist,
        List<BaseItem> artistAlbums,
        List<BaseItem> allTracks,
        IReadOnlyDictionary<Guid, BaseItem> firstTrackByAlbumId,
        List<InternalItemsQuery>? queries = null)
    {
        var albumNameById = artistAlbums.ToDictionary(a => a.Id, a => a.Name);

        LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
            {
                queries?.Add(q);
                if (q.IncludeItemTypes?.Contains(BaseItemKind.MusicArtist) == true)
                {
                    return new List<BaseItem> { artist };
                }

                if (q.IncludeItemTypes?.Contains(BaseItemKind.MusicAlbum) == true)
                {
                    return artistAlbums;
                }

                return new List<BaseItem>();
            });

        LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
            {
                queries?.Add(q);
                // JF-443 count queries are COUNT-only (Limit=0): the ParentId primary
                // counts by entity link, the AlbumIds fallback by raw-tag name match.
                if (q.Limit == 0)
                {
                    int count = q.ParentId != Guid.Empty
                        ? allTracks.Count(t => t.ParentId == q.ParentId)
                        : allTracks.Count(t => q.AlbumIds is { Length: > 0 }
                            && albumNameById.TryGetValue(q.AlbumIds[0], out string? albumName)
                            && string.Equals(t.Album, albumName, StringComparison.Ordinal));
                    return new QueryResult<BaseItem>
                    {
                        Items = new List<BaseItem>(),
                        TotalRecordCount = count
                    };
                }

                // Playback page queries (nonzero Limit): ParentId first, then the JF-338
                // AlbumIds retry when the folder link finds nothing.
                Guid playKey = q.ParentId != Guid.Empty
                    ? q.ParentId
                    : q.AlbumIds is { Length: > 0 } ? q.AlbumIds[0] : Guid.Empty;
                return firstTrackByAlbumId.TryGetValue(playKey, out BaseItem? track)
                    ? new QueryResult<BaseItem> { Items = new[] { track }, TotalRecordCount = 1 }
                    : new QueryResult<BaseItem> { Items = new List<BaseItem>(), TotalRecordCount = 0 };
            });
    }

    internal SessionInfo CreateSession() => TestHelpers.CreateTestSession(SessionManager.Object, LoggerFactory);

    internal Context CreateContext() => TestHelpers.CreateTestContext();

    internal Entities.User CreateUser() => TestHelpers.CreateTestUser();
}

/// <summary>
/// JF-467: the ONE shared-gate probe handler. History: a private copy lived in
/// CrossMediaFallbackMusicGateTests at HEAD and was hoisted here (plus the album
/// cascade accessor) so MusicPrimaryPathGateTests could share it. Minimal concrete
/// BaseHandler exposing the shared cross-media gates for direct testing (same
/// pattern as ContentAccessTests' TestMediaTypeHandler): the album cascade is
/// still a protected BaseHandler member, while the entity fallback moved to the
/// CrossMediaFallback collaborator (JF-315 batch 7) and is reached through the
/// composed <c>CrossMedia</c> property.
/// </summary>
internal sealed class SharedGateProbeHandler : BaseHandler
{
    public SharedGateProbeHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
        : base(sessionManager, config, loggerFactory)
    {
    }

    public override bool CanHandle(Request request) => true;

    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
        => Task.FromResult(ResponseBuilder.Tell("test"));

    public Task<SkillResponse?> CallTryEntityFallbackAsync(
        string slotText,
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        Entities.User user,
        SessionInfo session,
        Context context,
        string locale,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        string logLabel,
        IArtistIndex? artistIndex,
        CancellationToken cancellationToken,
        string? notFoundMediaType = null,
        bool? kanaOrigin = null)
        => CrossMedia.TryEntityFallbackAsync(
            slotText, jellyfinUser, user, session, context, locale,
            libraryManager, userDataManager, null, artistIndex, logLabel, cancellationToken,
            notFoundMediaType, kanaOrigin);

    /// <summary>
    /// JF-652: direct access to the JF-471 acceptance gate for the kana-bar tests
    /// (the kanaOrigin flag is only reachable through this signature).
    /// </summary>
    public bool CallPassesArtistMatchAcceptance(
        BaseItem artist,
        string query,
        Entities.User user,
        IArtistIndex? artistIndex,
        out int score,
        bool kanaOrigin = false)
        => CrossMedia.PassesArtistMatchAcceptance(artist, query, user, artistIndex, out score, kanaOrigin);

    /// <summary>
    /// JF-654: direct access to the inverse song fallback for the song-side
    /// kana-bar tests (the kanaOrigin flag is threaded through this signature).
    /// </summary>
    public SkillResponse? CallTrySongFallback(
        string musician,
        Entities.User user,
        SessionInfo session,
        Context context,
        string locale,
        ISongNgramIndex? songIndex,
        ILibraryManager libraryManager,
        CancellationToken cancellationToken,
        bool kanaOrigin = false)
        => CrossMedia.TrySongFallback(
            musician, user, session, context, locale, songIndex, libraryManager, "kana song probe", cancellationToken, kanaOrigin);

    public Task<SkillResponse?> CallTryAlbumFallbackAsync(
        string slotText,
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        Entities.User user,
        SessionInfo session,
        Context context,
        string locale,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        string logLabel,
        CancellationToken cancellationToken,
        bool? kanaOrigin = null)
        => AlbumPlay.TryAlbumFallbackAsync(
            slotText, jellyfinUser, user, session, context, locale,
            libraryManager, userDataManager, null, logLabel,
            request: null, cancellationToken: cancellationToken, kanaOrigin: kanaOrigin);

    /// <summary>
    /// JF-505: direct access to the shared VideoApp launch chokepoint for the
    /// capability-gate tests (gate present/absent/fail-open shapes).
    /// </summary>
    public SkillResponse CallBuildVideoAppLaunchResponse(
        Context? context,
        string locale,
        string sourceUrl,
        string title,
        IOutputSpeech? outputSpeech = null)
        => Launch.BuildVideoAppLaunchResponse(context, locale, sourceUrl, title, outputSpeech);
}
