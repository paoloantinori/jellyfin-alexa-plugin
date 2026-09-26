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
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// The JF-315 playback-launch collaborator (extracted from BaseHandler; batch 4
/// 2026-09-15, VideoApp launch family batch 5 same day, video-launch announce
/// speech batch 9): everything that builds a
/// launch response for an item - the stream/image URL vocabulary derived from plugin
/// config, the <see cref="AudioLaunchSource"/> resolution family (static-vs-transcode
/// routing, resume rebasing, the launch-scope reads), the AudioPlayer.Play response
/// chokepoint (with its device last-played ledger and launch-scope records,
/// metadata, seek card, and the gated music announce), the VideoApp-for-audio
/// response (native controls) including its screenless degradation back to
/// AudioPlayer, the VideoApp LAUNCH family (the codec-routed movie/episode URL with
/// the JF-565 episode resume slice it can mint, the
/// launch response chokepoints with their capability gates, the live-TV channel
/// launch, the audiobook resume, the JF-501 progressive announce, and the
/// resume-aware announce speech pair that feeds it), the
/// JF-564 medium classification the transport intents answer from, the JF-626
/// current-item resolver the item-needing intents (Repeat, RateItem, the
/// playlist-edit family) resolve through, and the APL
/// now-playing attacher that rides play responses.
/// STATELESS by construction (readonly config + logger + the composition-time
/// progressive-send delegate), so the singleton-handlers constraint BaseHandler
/// documents is preserved.
/// COMPOSITION DECISION (the census's ctor-injected design, adapted so the 61
/// handlers keep compiling without ctor churn): BaseHandler constructs one
/// instance per handler in its own ctor and exposes it as the protected-internal
/// readonly <c>Launch</c> property; handlers consume it through that inherited
/// get-only property. The per-call <c>queueManager ?? Plugin.Instance?.DeviceQueueManager</c>
/// idiom is kept verbatim (the queue manager is deliberately parameterized per
/// call so unit tests stay off the shared plugin instance, JF-522).
/// THE PROGRESSIVE SEAM: the JF-501 progressive announce send stays VIRTUAL on
/// BaseHandler (SendProgressiveResponse, the test seam ~15 harnesses override to
/// capture speech); this builder receives the handler's own method as a delegate
/// at composition time, so every override keeps capturing (see the constructor
/// for the mechanism). The JF-564 medium classification is transport-answer
/// policy rather than launch building; it lives here with the launch family it
/// shares state reads with and may earn its own collaborator in a later batch.
/// </summary>
public sealed class PlaybackLaunchBuilder
{
    private readonly PluginConfiguration _config;
    private readonly ILogger _logger;
    private readonly Func<Context, Request, string, Task<bool>> _sendProgressiveResponse;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackLaunchBuilder"/> class.
    /// </summary>
    /// <param name="config">The plugin configuration (server address, token secret, feature toggles).</param>
    /// <param name="logger">The logger (BaseHandler passes its own instance so moved log statements keep their pre-extraction category).</param>
    /// <param name="sendProgressiveResponse">The handler's own <c>SendProgressiveResponse</c> method, wired as a DELEGATE at composition time (JF-315 batch 5). The method stays virtual on BaseHandler because ~15 test harnesses override it to capture progressive speech; passing the method group (rather than re-implementing the send here) keeps every override in the chain: a delegate created from a virtual method dispatches virtually to the most-derived override at invocation time (probe-verified 2026-09-15: a delegate created inside the base constructor still reaches the derived override). The delegate's target is the handler instance itself, so it captures nothing request-scoped; handlers are singleton-lifetime, so the capture pins nothing the handler does not already own.</param>
    public PlaybackLaunchBuilder(PluginConfiguration config, ILogger logger, Func<Context, Request, string, Task<bool>> sendProgressiveResponse)
    {
        _config = config;
        _logger = logger;
        _sendProgressiveResponse = sendProgressiveResponse;
    }

    /// <summary>
    /// Get a stream url for the given item.
    /// </summary>
    /// <param name="itemId">Id of the item to stream.</param>
    /// <param name="user">The user for which the item should be played.</param>
    /// <returns>Streamable url of the requested item.</returns>
    public string GetStreamUrl(string itemId, Entities.User user)
        => BuildStreamUrl("Audio/", itemId, user);

    /// <summary>
    /// Get a video stream URL for the given item.
    /// </summary>
    /// <param name="itemId">Id of the item to stream.</param>
    /// <param name="user">The user for which the item should be played.</param>
    /// <returns>Streamable url of the requested item.</returns>
    public string GetVideoStreamUrl(string itemId, Entities.User user)
        => BuildStreamUrl("Videos/", itemId, user);

    /// <summary>
    /// Get a resume-aware audiobook HLS URL with a start-position hint. The endpoint reads
    /// <c>?start=&lt;ticks&gt;</c> and slices the playlist at position (<c>?start=</c>, the active mechanism; ExoPlayer ignores <c>#EXT-X-START</c>, hardware-verified) so VideoApp
    /// can resume (VideoApp.Launch has no offset parameter of its own).
    /// Moved here from BaseHandler (JF-315 batch 5) next to the audiobook concat URL it
    /// siblings (<see cref="GetAudiobookVideoAudioUrl"/>): both read the same
    /// config-derived server address and token secret.
    /// </summary>
    /// <param name="parentId">Id of the audiobook parent folder.</param>
    /// <param name="startTicks">Resume position in .NET ticks.</param>
    /// <returns>URL to the resume-aware audiobook HLS endpoint.</returns>
    internal string GetAudiobookResumeUrl(string parentId, long startTicks)
    {
        // JF-584: the ONE audiobook concat URL builder (the fresh-launch sibling
        // delegates here with startTicks 0); a route/token-shape change lands once.
        string token = StreamTokenHelper.Mint(parentId, _config.StreamTokenSecret);
        string query = startTicks > 0 ? $"?start={startTicks}&token={token}" : $"?token={token}";
        return new Uri(new Uri(_config.ServerAddress), $"alexaskill/api/video-audio/audiobook/{parentId}/stream.m3u8{query}").ToString();
    }

    /// <summary>
    /// Get the video-audio URL for the EPISODE HLS REMUX (JF-498): video stream copy +
    /// audio AAC transcode into MPEG-TS segments, for video items whose audio codec has
    /// no decoder on the Echo Show (eac3/ac3/truehd/dts) so the static stream never
    /// starts. Serves movies too: it is the one remux endpoint for every Movie/Episode
    /// launch that <see cref="GetVideoAppLaunchUrl(BaseItem, Entities.User, long)"/> routes here. Segments are served
    /// by the existing segment endpoint, keyed by the item GUID; the token is the same
    /// item-scoped HMAC as the other video-audio endpoints (JF-309).
    /// Optional <paramref name="startTicks"/> mints the JF-499 resume slice
    /// (<c>?start=</c>): the endpoint serves an EXTINF-accurate sliced playlist, which
    /// is the ONLY resume mechanism a VideoApp launch has (VideoApp.Launch has no
    /// offset parameter).
    /// Moved here from BaseHandler (JF-315 batch 5) beside the audio-only episode URL:
    /// the VideoApp remux and the AudioPlayer transcode are the two codec-routed
    /// escape hatches from the static stream.
    /// </summary>
    /// <param name="itemId">Id of the video item.</param>
    /// <param name="startTicks">Resume position in .NET ticks (0 plays from the start).</param>
    /// <returns>URL to the episode remux HLS endpoint.</returns>
    internal string GetEpisodeVideoAudioUrl(string itemId, long startTicks = 0)
    {
        string token = StreamTokenHelper.Mint(itemId, _config.StreamTokenSecret);
        string query = startTicks > 0 ? $"?start={startTicks}&token={token}" : $"?token={token}";
        return new Uri(new Uri(_config.ServerAddress), $"alexaskill/api/video-audio/episode/{itemId}/stream.m3u8{query}").ToString();
    }

    /// <summary>
    /// Get the PLAYBACK-SPEED atempo HLS URL (JF-636): the item's audio
    /// time-stretched server-side (<c>ffmpeg -af atempo</c>, pitch-preserving)
    /// for the AudioPlayer path, which has no native rate control (the same
    /// MSAPI-only platform limit as the scrubber). The <c>?start=</c> ticks are
    /// CONTENT-relative: the endpoint input-seeks the source there (<c>-ss</c>)
    /// and atempo runs over the remainder, so the served output timeline starts
    /// at position 0 (= content position <paramref name="startTicks"/>) and the
    /// directive offset is always 0 for it; the content position rides out as
    /// the launch base instead. The token is the same item-scoped JF-309 HMAC
    /// as every other alexaskill stream endpoint.
    /// </summary>
    /// <param name="itemId">Id of the item to stream.</param>
    /// <param name="ratePerMille">Playback rate in per-mille form (750..2000; the endpoint validates against the same table).</param>
    /// <param name="startTicks">CONTENT resume position in .NET ticks (0 plays from the start).</param>
    /// <returns>URL to the audio-speed HLS endpoint.</returns>
    internal string GetAudioSpeedUrl(string itemId, int ratePerMille, long startTicks = 0)
    {
        string token = StreamTokenHelper.Mint(itemId, _config.StreamTokenSecret);
        string query = startTicks > 0 ? $"?start={startTicks}&token={token}" : $"?token={token}";
        return new Uri(new Uri(_config.ServerAddress), $"alexaskill/api/audio-speed/{itemId}/{ratePerMille}/stream.m3u8{query}").ToString();
    }

    /// <summary>
    /// The JF-565 fail-closed resume clamp, defined ONCE: a position at or beyond
    /// the runtime cannot be a legitimate mid-item resume (only stale state
    /// reaches it), and an UNKNOWN runtime cannot prove the position is within
    /// the content (the zero-runtime .strm shape), so both fail closed to a
    /// fresh start. Both launch routes (the VideoApp slice and the screenless
    /// audio degrade) clamp through this so the spoken claim and the delivered
    /// offset cannot diverge (the JF-586 /simplify R1 finding).
    /// </summary>
    internal long ClampResumeTicksToRuntime(BaseItem item, long resumeTicks, string logLabel)
    {
        long? runtimeTicks = item.RunTimeTicks;
        if (resumeTicks > 0 && (runtimeTicks is not > 0 || resumeTicks >= runtimeTicks.Value))
        {
            _logger.LogInformation(
                "{Label} of '{ItemName}' ({ItemId}): resume position {ResumeTicks} ticks cannot be proven within the item runtime ({RuntimeTicks} ticks); starting from the beginning",
                logLabel, item.Name, item.Id, resumeTicks, runtimeTicks);
            return 0;
        }

        return resumeTicks;
    }

    /// <summary>
    /// Resolve the static-vs-HLS-remux decision for a VideoApp launch by probing the
    /// item's media streams (JF-498). Extracted as its own step so the policy itself
    /// (<see cref="VideoAppStreamPolicy.Decide"/>) stays a pure function.
    /// Moved here from BaseHandler (JF-315 batch 5), beside <see cref="TryResolveAudioCodec"/>
    /// (its AudioPlayer-side mirror, the same fail-open media-streams probe).
    /// </summary>
    /// <param name="item">The Movie/Episode item about to be launched.</param>
    /// <returns>The launch decision with a log-ready reason.</returns>
    private VideoAppStreamDecision ResolveVideoAppStreamDecision(BaseItem item)
    {
        try
        {
            (string? videoCodec, string? audioCodec) = VideoAppStreamPolicy.ExtractCodecs(item.GetMediaStreams());
            return VideoAppStreamPolicy.Decide(videoCodec, audioCodec, item.Container);
        }
        catch (Exception ex)
        {
            // GetMediaStreams() goes through BaseItem's statically injected
            // MediaSourceManager (null under unit tests) and a database read. Any
            // failure keeps the static stream: the probe may only ADD the remux
            // route, never break the launch path.
            _logger.LogDebug(ex, "VideoApp stream decision: could not read media streams for item {ItemId}; keeping the static stream", item.Id);
            return VideoAppStreamPolicy.Decide(videoCodec: null, audioCodec: null);
        }
    }

    /// <summary>
    /// Get the VideoApp.Launch source URL for a MOVIE or EPISODE item, routed by codec
    /// compatibility (JF-498): Echo-decodable sources (h264 video + aac/mp3/... audio)
    /// keep the static <c>/Videos/{id}/stream?static=true</c> URL; sources whose audio
    /// has no Echo decoder (eac3/ac3/truehd/dts) get the HLS remux URL instead; sources
    /// whose VIDEO codec the Echo cannot decode (hevc/av1, JF-500) get the same HLS
    /// endpoint URL (the endpoint re-probes and re-encodes the video). Every VideoApp
    /// launch site that launches a Movie or Episode item must go through this helper
    /// so the routing cannot drift between handlers (live incident 2026-09-05
    /// corr=d9f848a7: the whole PlayNextEpisode chain was correct and the video never
    /// started because the static URL served raw EAC3 bytes). JF-586/JF-587: an EPISODE
    /// launch site must call BuildEpisodeLaunchResponseAsync instead, which routes
    /// through this helper on capable devices and degrades to the audio-only
    /// AudioPlayer route on a screenless one; every Movie/Episode launch site now
    /// does (the remaining direct callers are Movie-only or LiveTvChannel).
    /// JF-565: an EPISODE launch may pass <paramref name="startTicks"/> to mint the
    /// resume slice (<c>?start=</c>) on the remux URL - VideoApp.Launch has no offset
    /// parameter, so the slice IS the episode resume mechanism. The position applies
    /// EPISODE-only by task scope: a Movie launch ignores it (movie resume keeps the
    /// announced-position-only shape), and the Static route ignores it too (the static
    /// stream has no seek mechanism at all; that platform limit is why the slice
    /// exists).
    /// Moved here from BaseHandler (JF-315 batch 5).
    /// </summary>
    /// <param name="item">The Movie/Episode item to launch.</param>
    /// <param name="user">The user for the static stream URL (api_key).</param>
    /// <param name="startTicks">Resume position in .NET ticks for an EPISODE launch (0/fresh sites pass nothing).</param>
    /// <returns>The VideoApp source URL (static or an episode HLS tier).</returns>
    public string GetVideoAppLaunchUrl(BaseItem item, Entities.User user, long startTicks = 0)
        => GetVideoAppLaunchUrl(item, user, startTicks, out _);

    /// <summary>
    /// The delivery-aware form (JF-565 review finding): <paramref name="resumeDelivered"/>
    /// tells the caller whether the position actually reached the launch URL, so the
    /// spoken announce cannot claim a resume the device will not deliver. False when
    /// the route is Static (no seek mechanism), when the item is not an Episode
    /// (task scope), or when the clamp degraded the slice to a fresh start.
    /// </summary>
    public string GetVideoAppLaunchUrl(BaseItem item, Entities.User user, long startTicks, out bool resumeDelivered)
    {
        VideoAppStreamDecision decision = ResolveVideoAppStreamDecision(item);
        _logger.LogDebug("VideoApp launch routing for '{ItemName}' ({ItemId}): {Reason}", item.Name, item.Id, decision.Reason);

        if (decision.Route == VideoAppStreamRoute.Static)
        {
            resumeDelivered = false;
            return GetVideoStreamUrl(item.Id.ToString(), user);
        }

        // JF-521 clamp, the episode-slice mirror: a stored position at or beyond the
        // runtime cannot be a legitimate mid-episode resume (only stale state reaches
        // it), and the slice would serve a zero-length playlist, so the fresh start
        // is the conservative truth. An UNKNOWN runtime cannot prove the position is
        // within the content (the zero-runtime .strm shape), so it fails closed to a
        // fresh start too (review finding: the fail-open form could mint an
        // unclamped slice that serves an empty playlist).
        long sliceTicks = item is MediaBrowser.Controller.Entities.TV.Episode
            ? ClampResumeTicksToRuntime(item, startTicks, "VideoApp launch")
            : 0;

        resumeDelivered = sliceTicks > 0;
        return GetEpisodeVideoAudioUrl(item.Id.ToString(), sliceTicks);
    }

    /// <summary>
    /// The ONE classification of "item kinds that ride the VideoApp launch path"
    /// (JF-505 simplify: the predicate had drifted between the resume-offer gate,
    /// which included LiveTvChannel, and the ResumeIntent router, which did not).
    /// Every site deciding whether an item is launched/gated/offered as VIDEO
    /// consumes this predicate; do not hand-write the type list again.
    /// Moved here from BaseHandler (JF-315 batch 5).
    /// </summary>
    /// <param name="item">The item to classify.</param>
    /// <returns>True when the item launches via VideoApp on a capable device.</returns>
    public static bool IsVideoAppLaunchItem(BaseItem? item)
        => item is MediaBrowser.Controller.Entities.Movies.Movie
            or MediaBrowser.Controller.Entities.TV.Episode
            or MediaBrowser.Controller.LiveTv.LiveTvChannel;

    /// <summary>
    /// The medium a device is most recently known to be playing (JF-564). The transport
    /// intents that Alexa routes to the skill without the invocation name (pause, stop,
    /// next, previous) must answer differently per medium: only <see cref="PlayingMedium.Audio"/>
    /// can honestly be controlled via AudioPlayer directives; the VideoApp family cannot
    /// (no VideoApp.Stop exists and AudioPlayer.Play mid-video is a misdirect).
    /// Moved here from BaseHandler (JF-315 batch 5) with the resolver and the refusal.
    /// </summary>
    internal enum PlayingMedium
    {
        /// <summary>Nothing recorded (cold device, deleted item, or no manager wired):
        /// callers keep their pre-JF-564 behavior, so a cold handler never changes its
        /// music semantics.</summary>
        Unknown,

        /// <summary>The AudioPlayer pipeline owns the item (music, an audio-transcode
        /// launch of a movie/episode, or a book on the flat audio path): transport
        /// directives genuinely work.</summary>
        Audio,

        /// <summary>A Movie/Episode launched via VideoApp.</summary>
        Video,

        /// <summary>A live TV channel launched via VideoApp.</summary>
        LiveTv,

        /// <summary>An audiobook riding the VideoApp HLS path (NativeControlsForBooks).</summary>
        VideoAppAudiobook,

        /// <summary>Music riding the VideoApp HLS path (the JF-625 seek mode: a song
        /// or a whole-album concat). AudioPlayer directives cannot touch the stream and
        /// track navigation is seek-only, so the transport intents answer the honest
        /// refusal instead of emitting a parallel AudioPlayer.Play over it.</summary>
        VideoAppAudio,
    }

    /// <summary>
    /// Whether the medium is one the skill launched via VideoApp.Launch (and therefore
    /// one it cannot control with AudioPlayer directives).
    /// </summary>
    /// <param name="medium">The classified medium.</param>
    /// <returns>True for Video, LiveTv and VideoAppAudiobook.</returns>
    internal static bool IsVideoAppMedium(PlayingMedium medium)
        => medium is PlayingMedium.Video or PlayingMedium.LiveTv or PlayingMedium.VideoAppAudiobook or PlayingMedium.VideoAppAudio;

    /// <summary>
    /// Whether the request context reports the stream as actively playing: the
    /// device's last-reported <c>playerActivity</c> (<c>context.AudioPlayer</c>) is
    /// PLAYING or BUFFER_UNDERRUN. BUFFER_UNDERRUN counts as playing because it is
    /// transient mid-playback rebuffering of the still-current stream, not a stopped
    /// one. Null-tolerant: a context carrying no AudioPlayer object at all reports
    /// false. Shared by PlayRadio's seed decision (JF-480) and
    /// PlaybackFinishedEventHandler's hasQueuedNext; ResumeIntentHandler deliberately
    /// keeps its own narrower PLAYING-only check and must NOT reuse this helper:
    /// resume should still act on an underrun-stalled stream (restart it), not treat
    /// it as "already playing, nothing to do".
    /// Moved here from BaseHandler (JF-315 batch 5) with the medium family it feeds.
    /// </summary>
    /// <param name="context">The Alexa request context (may carry no AudioPlayer object).</param>
    /// <returns>True when the device last reported active playback.</returns>
    internal static bool IsActivelyPlaying(Context? context)
    {
        string? activity = context?.AudioPlayer?.PlayerActivity;
        return string.Equals(activity, "PLAYING", StringComparison.Ordinal)
            || string.Equals(activity, "BUFFER_UNDERRUN", StringComparison.Ordinal);
    }

    /// <summary>
    /// Classify what a device is playing from the JF-563 device last-played ledger plus
    /// the AudioPlayer token (JF-564) and the JF-568 recorded launch route. The ledger
    /// is the only record a VideoApp launch leaves (those launches never touch
    /// <c>context.AudioPlayer.Token</c>), and it is written by every launch site: the
    /// BuildAudioPlayerResponse chokepoint, the LastPlayedResponseInterceptor
    /// (movie/episode directives), the VideoApp builders (channel, video-audio,
    /// audiobook) and the sleep-timer re-issue (JF-628, the one AudioPlayer.Play
    /// minted outside the chokepoint), each recording its route beside the item.
    /// Classification rules,
    /// in order: an EMPTY ledger (or an unresolvable item) yields
    /// <see cref="PlayingMedium.Unknown"/> so a cold handler keeps its existing
    /// behavior; a token naming the ledger item yields <see cref="PlayingMedium.Audio"/>
    /// whatever the item kind (the audio pipeline owns it: a Movie can ride the
    /// audio-only transcode and a book the flat audio path, and on both the transport
    /// directives work) and skips the item resolve entirely; a ledger entry recorded
    /// on the AUDIO route yields <see cref="PlayingMedium.Audio"/> whatever the kind
    /// for the same reason and also skips the resolve (JF-568: a video-KIND item the
    /// skill launched through AudioPlayer, whose token a radio enqueue then moved
    /// without recording, is the ordinary queue-advance shape, not VideoApp
    /// displacement; classification is route-driven for recorded launches);
    /// otherwise the ledger item's kind decides via <see cref="IsVideoAppLaunchItem"/>
    /// (channel vs other video) and the AudioBook test, and any remaining item kind
    /// (music) is audio whose token merely moved with the queue advance. A NULL route
    /// (a queue persisted before JF-568) falls through to the kind rules exactly as
    /// before, so legacy files keep the pre-JF-568 classification. Handlers that need
    /// the resolved ITEM back (not just the medium) call
    /// <see cref="ResolveCurrentPlayingItem"/>, the item-returning sibling of this
    /// classification.
    /// Moved here from BaseHandler (JF-315 batch 5).
    /// </summary>
    /// <param name="context">The Alexa context (device id for the ledger read, AudioPlayer token).</param>
    /// <param name="libraryManager">The library manager, to resolve the ledger item id. Null yields Unknown.</param>
    /// <param name="queueManager">The caller's device queue manager (tests pass theirs); null falls back to <c>Plugin.Instance</c>'s.</param>
    /// <returns>The classified medium; Unknown when nothing is recorded.</returns>
    internal PlayingMedium ResolvePlayingMedium(Context? context, ILibraryManager? libraryManager, DeviceQueueManager? queueManager = null)
    {
        if (libraryManager == null)
        {
            return PlayingMedium.Unknown;
        }

        string? deviceId = context?.System?.Device?.DeviceID;
        DeviceQueueManager? ledgerManager = deviceId != null
            ? queueManager ?? Plugin.Instance?.DeviceQueueManager
            : null;
        (string? lastPlayedId, DeviceQueueManager.LaunchRoute? recordedRoute) =
            ledgerManager?.GetLastPlayedSnapshot(deviceId!)
            ?? (null, null);
        if (!Guid.TryParse(lastPlayedId, out Guid lastPlayedItemId))
        {
            return PlayingMedium.Unknown;
        }

        // Sleep-suffixed tokens still carry the item id (StreamTokenCodec), so the
        // ownership check survives the composite form. It runs BEFORE the item
        // resolve: when the audio pipeline owns the ledger item (the modal music
        // shape), the answer is Audio whatever the kind and the DB read is skipped.
        if (StreamTokenCodec.TryGetItemId(context?.AudioPlayer?.Token, out Guid tokenItemId)
            && tokenItemId == lastPlayedItemId)
        {
            return PlayingMedium.Audio;
        }

        // JF-568: the recorded route owns the classification for recorded launches.
        // Route Audio means the AUDIO pipeline launched the ledger item (a Movie or
        // Episode on the JF-507 audio-only transcode, an audio-route episode since
        // JF-589, a flat-audio book), so a token that moved on is the ordinary
        // queue-advance/radio-enqueue shape, not VideoApp displacement; the answer
        // is Audio whatever the item kind and the resolve is skipped, mirroring the
        // token-ownership arm above. Only a VideoApp-routed (or legacy null-routed)
        // entry falls through to the kind-based rules below.
        if (recordedRoute == DeviceQueueManager.LaunchRoute.Audio)
        {
            return PlayingMedium.Audio;
        }

        BaseItem? item = libraryManager.GetItemById(lastPlayedItemId);
        if (item == null)
        {
            return PlayingMedium.Unknown;
        }

        return ClassifyLedgerItemKind(item, recordedRoute);
    }

    /// <summary>
    /// The ONE ledger-item kind ladder (JF-626), the shared kernel of
    /// <see cref="ResolvePlayingMedium"/> and the displacement predicate in
    /// <see cref="ResolveCurrentPlayingItem"/>: given a resolved ledger item and
    /// its recorded launch route, which VideoApp-family medium is it? The JF-625
    /// lesson lives here: the VideoAppAudio arm was added to the classifier and to
    /// one of the (then three) hand-rolled item resolvers but missed the second,
    /// proving cross-reference comments do not keep parallel ladders in sync; a
    /// future launch kind lands here once and both readers follow.
    /// </summary>
    /// <param name="item">The resolved ledger item.</param>
    /// <param name="recordedRoute">The route recorded beside it (null = pre-JF-568 legacy).</param>
    /// <returns>The medium the item's kind and route classify as.</returns>
    private static PlayingMedium ClassifyLedgerItemKind(BaseItem item, DeviceQueueManager.LaunchRoute? recordedRoute)
    {
        // The ONE VideoApp kind predicate (JF-505: "do not hand-write the type list
        // again"), so a future launch kind added there classifies correctly here
        // instead of silently falling through to Audio; LiveTvChannel needs its own
        // medium arm first.
        if (IsVideoAppLaunchItem(item))
        {
            return item is MediaBrowser.Controller.LiveTv.LiveTvChannel
                ? PlayingMedium.LiveTv
                : PlayingMedium.Video;
        }

        if (AudiobookItems.IsAudioBook(item))
        {
            return PlayingMedium.VideoAppAudiobook;
        }

        // JF-625 seek mode: an EXPLICITLY VideoApp-routed ledger entry that resolves to
        // a plain Audio track is the video-audio launch (a song or the album concat);
        // it must NOT fall through to Audio, whose queue-advance emits an
        // AudioPlayer.Play the platform cannot stop (double audio; no VideoApp.Stop
        // exists). A NULL route is the pre-JF-568 legacy shape, when music only ever
        // played on AudioPlayer: those keep Audio.
        if (recordedRoute == DeviceQueueManager.LaunchRoute.VideoApp
            && item is MediaBrowser.Controller.Entities.Audio.Audio)
        {
            return PlayingMedium.VideoAppAudio;
        }

        // Music (and any non-VideoApp-kind item): RecordLastPlayed pins the
        // user-initiated play while Enqueue-advanced queues move only the token, so
        // a token mismatch here is the ordinary queue-advance shape, not
        // displacement (the RepeatIntentHandler precedent).
        return PlayingMedium.Audio;
    }

    /// <summary>
    /// The ONE "resolve the currently playing item" resolver (JF-626), the
    /// item-returning sibling of <see cref="ResolvePlayingMedium"/>: it answers
    /// WHICH library item is current, not which medium, and every intent handler
    /// that needs the item (Repeat, RateItem, the playlist-edit family) consumes
    /// this instead of hand-rolling the arbitration (three drifted copies preceded
    /// it). Resolution order, mirroring the classifier's policy: the device
    /// last-played ledger DISPLACEMENT arm first (see below), then the AudioPlayer
    /// token via <see cref="StreamTokenCodec"/>, then the session's
    /// <c>FullNowPlayingItem</c> (a full <see cref="BaseItem"/> the session already
    /// holds: free, never re-resolved by id, JF-626), then the session's
    /// now-playing DTO (re-resolved by id: the server can report the DTO without
    /// the full item), then the ledger's last-played item as the final fallback.
    /// THE DISPLACEMENT ARM (one predicate, one home): the ledger is the only
    /// record a VideoApp launch leaves (those launches never touch
    /// <c>context.AudioPlayer.Token</c>), so when a NON-empty token DIFFERS from
    /// the ledger id and the recorded route is not Audio, the ledger item decides
    /// through the ONE kind kernel (<see cref="ClassifyLedgerItemKind"/>, shared
    /// with <see cref="ResolvePlayingMedium"/>): any VideoApp-family medium means
    /// the launch displaced the audio and IS what is playing (video-kind items and
    /// audiobooks, including the legacy null-route shape, plus the VideoApp-routed
    /// plain Audio track: the JF-625 seek-mode launch whose stale token names the
    /// pre-launch track), so the stale token must lose. Any other mismatch is the
    /// ordinary queue-advance shape (RecordLastPlayed pins the user-initiated play;
    /// the Enqueue directives that advance the queue never record), where the
    /// fresher token wins. The cheap displacement guards (token present, mismatched,
    /// route not Audio) run BEFORE the ledger item resolve (the
    /// ResolvePlayingMedium doctrine), so the modal music path pays one item
    /// resolve, not two.
    /// The ledger arm requires the caller's <paramref name="queueManager"/>: null
    /// DISABLES it (deliberately NOT the <c>Plugin.Instance</c> fallback this
    /// class's other queue reads use, because the playlist-edit family holds no
    /// device queue and keeps its token+session-only semantics).
    /// </summary>
    /// <param name="context">The Alexa context (device id for the ledger read, AudioPlayer token).</param>
    /// <param name="session">The Jellyfin session (full now-playing item first, DTO second).</param>
    /// <param name="libraryManager">The library manager, to resolve item ids.</param>
    /// <param name="queueManager">The caller's device queue manager (Repeat, RateItem pass theirs); the parameter has NO default so every caller states its choice: null deliberately disables the ledger arms (the playlist-edit family).</param>
    /// <param name="logLabel">Caller identity for the displacement log line.</param>
    /// <returns>The currently playing item, or null when nothing is resolvable.</returns>
    internal BaseItem? ResolveCurrentPlayingItem(
        Context? context,
        SessionInfo? session,
        ILibraryManager libraryManager,
        DeviceQueueManager? queueManager,
        string logLabel = "CurrentItem")
    {
        string? deviceId = context?.System?.Device?.DeviceID;
        (string? lastPlayedId, DeviceQueueManager.LaunchRoute? recordedRoute) =
            deviceId != null && queueManager != null
                ? queueManager.GetLastPlayedSnapshot(deviceId)
                : (null, null);
        string? token = context?.AudioPlayer?.Token;

        BaseItem? ResolveId(string? id) =>
            !string.IsNullOrEmpty(id) && Guid.TryParse(id, out Guid guid)
                ? libraryManager.GetItemById(guid)
                : null;

        // The three data-in-hand guards before the ledger item's kind can matter:
        // when any fails (the modal music shape: audio-routed ledger, resolvable
        // token) the resolve is skipped, mirroring ResolvePlayingMedium. The
        // ownership compare is CODEC-safe (the same shape ResolvePlayingMedium's
        // ownership arm uses, review finding on the first JF-626 cut): a composite
        // sleep token naming the ledger item reads as OWNED, not as a mismatch, so
        // a re-armed token cannot manufacture a displacement verdict (and its
        // wasted ledger resolve plus misleading displacement log line).
        bool tokenNamesLedgerItem =
            StreamTokenCodec.TryGetItemId(token, out Guid tokenItemId)
            && Guid.TryParse(lastPlayedId, out Guid ledgerGuid)
            && tokenItemId == ledgerGuid;

        bool displacementPossible =
            queueManager != null
            && !string.IsNullOrEmpty(token)
            && !tokenNamesLedgerItem
            && recordedRoute != DeviceQueueManager.LaunchRoute.Audio;

        BaseItem? ledgerItem = displacementPossible ? ResolveId(lastPlayedId) : null;
        // The classifier's kind kernel decides (JF-626: the one ladder, shared with
        // ResolvePlayingMedium): anything the ledger classifies as a VideoApp-family
        // medium displaced the stale audio token; Audio (the queue-advance shape)
        // did not.
        bool videoDisplacedAudio =
            ledgerItem != null
            && ClassifyLedgerItemKind(ledgerItem, recordedRoute) != PlayingMedium.Audio;

        if (videoDisplacedAudio)
        {
            _logger.LogInformation(
                "{Label}: AudioPlayer token {Token} was displaced by the {Route} launch of '{ItemName}' ({ItemId}); the ledger item is current",
                logLabel, token, recordedRoute, ledgerItem!.Name, lastPlayedId);
            return ledgerItem;
        }

        // The shared codec, not raw Guid.TryParse: tokens can be composite
        // ("{guid}|sleep:{ticks}", JF-447), and a raw parse would silently
        // decline to the session fallback while a sleep timer is armed.
        if (StreamTokenCodec.TryGetItemId(token, out Guid tokenId))
        {
            BaseItem? tokenItem = libraryManager.GetItemById(tokenId);
            if (tokenItem != null)
            {
                return tokenItem;
            }
        }

        // A full BaseItem the session already holds; no re-resolve by id (JF-626:
        // the playlist-edit family used to re-resolve this same item by id).
        if (session?.FullNowPlayingItem is { } sessionItem)
        {
            return sessionItem;
        }

        // The DTO tail: the server can report a now-playing DTO without the full
        // item (the shape the playlist-edit family has always covered); the id is
        // the only handle, so this leg re-resolves through the library.
        if (session?.NowPlayingItem is { } nowPlayingDto && nowPlayingDto.Id != Guid.Empty)
        {
            BaseItem? dtoItem = libraryManager.GetItemById(nowPlayingDto.Id);
            if (dtoItem != null)
            {
                return dtoItem;
            }
        }

        // The displacement-path resolve already missed this id (deleted/stale ledger
        // item); re-resolving the same known-absent GUID is a second DB miss for
        // nothing. Only the non-displacement tail (audio-routed ledger, no token,
        // no session item) resolves here.
        BaseItem? resolved = ledgerItem ?? (displacementPossible ? null : ResolveId(lastPlayedId));
        if (resolved is null)
        {
            _logger.LogDebug(
                "{Label}: no resolvable current item (token={Token}, lastPlayed={LastPlayed}, sessionItem={SessionItem})",
                logLabel, token, lastPlayedId, session?.FullNowPlayingItem?.Id);
        }

        return resolved;
    }

    /// <summary>
    /// The ONE per-medium answer for the queue-navigation transport intents during a
    /// VideoApp-family medium (JF-564), shared by the Next and Previous entries so the
    /// policy cannot drift between the twins. A VideoApp launch (movie/episode/live
    /// TV/book) does not replace the session's music queue, so the queue-advance logic
    /// would either return a silent Empty (the video item is not in the queue) or, with
    /// a stale music queue still pinned to the pre-video track, emit a misdirected
    /// AudioPlayer.Play mid-video. Video and live TV answer the honest navigate line;
    /// a VideoApp book keeps the silent Empty (chapter navigation is its own feature).
    /// Pause is NOT a caller (its response keeps the AudioPlayer.Stop directive and a
    /// different line); Audio and Unknown return null so the caller keeps its existing
    /// behavior.
    /// Moved here from BaseHandler (JF-315 batch 5). The two Tells stay hand-written
    /// (plain <c>ResponseStrings.Get</c> + <c>ResponseBuilder.Tell</c>): both keys are
    /// plain-text-only in every locale (no SSml twin), so the SpeechBuilder.TellLocalized
    /// SSML/plain ternary does not fit and reintroducing a zero-caller helper is banned.
    /// </summary>
    /// <param name="medium">The classified playing medium.</param>
    /// <param name="locale">The request locale, for the honest strings.</param>
    /// <returns>The transport refusal response, or null when the medium is controllable.</returns>
    internal static SkillResponse? BuildVideoAppTransportRefusal(PlayingMedium medium, string locale)
        => medium switch
        {
            PlayingMedium.Video => ResponseBuilder.Tell(ResponseStrings.Get("CannotNavigateVideoByVoice", locale)),
            PlayingMedium.LiveTv => ResponseBuilder.Tell(ResponseStrings.Get("CannotNavigateLiveTvByVoice", locale)),
            PlayingMedium.VideoAppAudiobook => ResponseBuilder.Empty(),
            PlayingMedium.VideoAppAudio => ResponseBuilder.Tell(ResponseStrings.Get("CannotNavigateMusicByVoice", locale)),
            _ => null,
        };

    /// <summary>
    /// Build the canonical VideoApp.Launch response (JF-505 shared launch chokepoint).
    /// Every Movie/Episode/live-TV launch site must build its response through this
    /// helper so the screenless-device capability gate cannot drift between handlers:
    /// a VideoApp.Launch sent to a device whose SupportedInterfaces lacks VideoApp (an
    /// Echo Dot) is rejected by the platform with an audible directive error (device
    /// evidence 2026-09-06), so without the interface NO directive is emitted and the
    /// localized <c>VideoRequiresScreen</c> Tell answers instead.
    /// JF-501: intent-driven launch sites must prefer the progressive-announce variant
    /// <see cref="BuildVideoAppLaunchResponseAsync"/>; this sync builder is the shape
    /// for non-intent requests (APL carousel taps), where the announce keeps riding the
    /// final response. The HandleFuzzyMiss auto-play delegate went async in JF-538, so
    /// it is no longer bound to this sync shape.
    /// Moved here from BaseHandler (JF-315 batch 5) beside
    /// <see cref="BuildVideoAppAudioResponse"/> (its audio-content mirror).
    /// </summary>
    /// <param name="context">The Alexa context, for device capability detection.</param>
    /// <param name="locale">The request locale, for the capability Tell string.</param>
    /// <param name="sourceUrl">The VideoApp source URL (from <see cref="GetVideoAppLaunchUrl(BaseItem, Entities.User, long)"/> or the live-TV resolver).</param>
    /// <param name="title">The video item metadata title.</param>
    /// <param name="outputSpeech">Optional now-playing announce.</param>
    /// <returns>The VideoApp.Launch response, or the VideoRequiresScreen Tell on a device without the VideoApp interface.</returns>
    internal SkillResponse BuildVideoAppLaunchResponse(
        Context? context,
        string locale,
        string sourceUrl,
        string title,
        IOutputSpeech? outputSpeech = null)
    {
        if (!Interface.VideoAppCapabilities.DeviceSupportsVideoApp(context))
        {
            _logger.LogDebug(
                "VideoApp launch of '{Title}' skipped: device {DeviceId} does not support the VideoApp interface",
                title,
                context?.System?.Device?.DeviceID ?? "unknown");
            return ResponseBuilder.Tell(ResponseStrings.Get("VideoRequiresScreen", locale));
        }

        return new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                // VideoApp.Launch must NOT include shouldEndSession; Alexa rejects it.
                ShouldEndSession = null,
                OutputSpeech = outputSpeech,
                Directives = new List<IDirective>
                {
                    new Directive.VideoAppLaunchDirective
                    {
                        VideoItem = new Directive.VideoItem
                        {
                            Source = sourceUrl,
                            Metadata = new Directive.VideoItemMetadata
                            {
                                Title = title
                            }
                        }
                    }
                }
            }
        };
    }

    /// <summary>
    /// JF-501 progressive-announce variant of <see cref="BuildVideoAppLaunchResponse"/>:
    /// the announce, when the request can carry one, is spoken as an awaited
    /// progressive response via <see cref="SpeakVideoLaunchAnnounceAsync"/> and the
    /// returned launch response carries the VideoApp.Launch directive only (no
    /// OutputSpeech), so the Echo Show player cannot cut the announcement
    /// mid-sentence when it opens. Every capability and gate of the sync builder is
    /// preserved: the announce decision runs BEFORE the launch build, so a
    /// screenless device never hears a progressive announce followed by the
    /// capability Tell, and a null announce (toggle off) keeps today's silent shape.
    /// Moved here from BaseHandler (JF-315 batch 5); the progressive send still
    /// reaches the handler's virtual SendProgressiveResponse through the
    /// composition-time delegate.
    /// </summary>
    /// <param name="context">The Alexa context, for device capability detection.</param>
    /// <param name="request">The skill request, for the progressive-response vehicle.</param>
    /// <param name="locale">The request locale, for the capability Tell string.</param>
    /// <param name="sourceUrl">The VideoApp source URL (from <see cref="GetVideoAppLaunchUrl(BaseItem, Entities.User, long)"/> or the live-TV resolver).</param>
    /// <param name="title">The video item metadata title.</param>
    /// <param name="outputSpeech">Optional now-playing announce; spoken progressively when the request type allows, else attached to the final response.</param>
    /// <returns>The VideoApp.Launch response, or the VideoRequiresScreen Tell on a device without the VideoApp interface.</returns>
    internal async Task<SkillResponse> BuildVideoAppLaunchResponseAsync(
        Context? context,
        Request? request,
        string locale,
        string sourceUrl,
        string title,
        IOutputSpeech? outputSpeech = null)
    {
        outputSpeech = await SpeakVideoLaunchAnnounceAsync(context, request, outputSpeech).ConfigureAwait(false);
        return BuildVideoAppLaunchResponse(context, locale, sourceUrl, title, outputSpeech);
    }

    /// <summary>
    /// The JF-586 EPISODE launch chokepoint: a Movie/Episode launch through the shared
    /// VideoApp family that lands on a SCREENLESS device (an Echo Dot, the Alexa web
    /// simulator) degrades to the AudioPlayer audio-only launch instead of the
    /// <c>VideoRequiresScreen</c> refusal, because an episode is AUDIO content a
    /// speaker can still play (the <see cref="BuildVideoAppAudioResponse"/> degrade
    /// precedent, the JF-505 family). The degrade rides
    /// <see cref="ResolveAudioLaunchSource"/>
    /// (JF-507): an episode whose audio codec has no Echo decoder (eac3/ac3/truehd/
    /// dts) routes to the audio-only episode HLS transcode with the resume position
    /// minted as <c>?start=</c> (directive offset 0); a decodable episode keeps the
    /// static <c>/Audio/{id}/stream</c> URL with the resume offset on the DIRECTIVE
    /// (AudioPlayer can seek a static stream; the VideoApp Static route cannot, which
    /// is why the same episode refuses-to-resume there). The stored position is
    /// clamped by the same JF-565 rule the VideoApp slice applies: a position at or
    /// beyond the runtime (or an UNKNOWN runtime, the zero-tick .strm shape) fails
    /// closed to a fresh start, so the degrade never mints an offset the stream
    /// cannot serve. The caller-chosen announce rides the FINAL response on the
    /// degrade (AudioPlayer playback does not steal the audio channel the way a
    /// fast-start VideoApp player does, the JF-501 observation; on that route the
    /// announce stays progressive inside <see cref="BuildVideoAppLaunchResponseAsync"/>
    /// exactly as today). On a VideoApp-capable device this builder is a pure
    /// pass-through to that method, so the capable path (codec-routed URL, progressive
    /// announce, shouldEndSession omitted) is byte-identical.
    /// SCOPE (JF-586): only an EPISODE degrades; a Movie (or any other item routed
    /// here by <see cref="IsVideoAppLaunchItem"/>) keeps the capability refusal on a
    /// screenless device, the pre-existing behavior. The caller's
    /// <paramref name="sourceUrl"/> stays what
    /// <see cref="GetVideoAppLaunchUrl(BaseItem, Entities.User, long)"/> resolved:
    /// the URL-first announce gate (JF-565) runs at the caller before this call, and
    /// the URL itself is consumed on the capable route only.
    /// </summary>
    /// <param name="context">The Alexa context, for the JF-505 screenless-device check.</param>
    /// <param name="request">The skill request (JF-501 progressive announce vehicle on the capable route).</param>
    /// <param name="locale">The request locale, for the capability Tell string a non-Episode keeps.</param>
    /// <param name="item">The Movie/Episode item being launched; only an Episode degrades.</param>
    /// <param name="user">The plugin user (static stream URL on the degrade).</param>
    /// <param name="sourceUrl">The VideoApp source URL the caller already resolved (capable route only).</param>
    /// <param name="resumeTicks">The resume position the caller resolved (the JF-565 slice input; feeds the degrade's offset).</param>
    /// <remarks>
    /// JF-589 AUDIO-SHAPE ROUTE: an EPISODE whose media streams positively prove
    /// AUDIO-ONLY content (at least one audio stream and NO video stream, the
    /// .strm podcast shape of the 2026-09-18 incident) routes to the AudioPlayer
    /// degrade EVEN on a VideoApp-capable device. VideoApp playback is
    /// POSITION-BLIND (no AudioPlayer context updates, no events on stop), so a
    /// VideoApp listen of a podcast loses its position entirely and the next
    /// resume lands BEHIND where the user actually listened (device-captured
    /// 2026-09-18: 81 seconds of listening, the stop's context still reported the
    /// pre-launch offset). AudioPlayer tracks the position correctly, so position
    /// tracking wins over the seek bar for audio content: the trade-off is that
    /// these items lose the VideoApp scrubber they technically could have had.
    /// A REAL TV episode (a video stream present) keeps the VideoApp path
    /// unchanged, and the probe FAILS OPEN: a thrown probe or a shape that proves
    /// nothing (no streams, no audio stream) keeps today's VideoApp behavior on
    /// capable devices - the audio-route only fires on positive audio-only
    /// evidence. Probe cost: one <c>GetMediaStreams</c> read on the Episode
    /// capable path, a second in-memory read of the streams the caller's codec routing already fetched
    /// once for the VideoApp URL; no new network I/O.
    /// </remarks>
    /// <param name="outputSpeech">The caller-chosen announce (fresh play keeps the now-playing/next/latest wording; the caller's resumeDelivered gate already picked the position-bearing form where the caller runs one (the arms that pass ungated ticks, e.g. PlayVideo, keep the informational-position-only semantics their capable route has always had) where the VideoApp route delivers it).</param>
    /// <returns>The VideoApp.Launch response on a capable device; the AudioPlayer.Play degrade for an Episode on a screenless one; the VideoRequiresScreen Tell for every other item on a screenless one.</returns>
    internal async Task<SkillResponse> BuildEpisodeLaunchResponseAsync(
        Context? context,
        Request? request,
        string locale,
        BaseItem item,
        Entities.User user,
        string sourceUrl,
        long resumeTicks,
        IOutputSpeech? outputSpeech = null)
    {
        bool capable = Interface.VideoAppCapabilities.DeviceSupportsVideoApp(context);
        bool isEpisode = item is MediaBrowser.Controller.Entities.TV.Episode;
        if (isEpisode && !capable)
        {
            _logger.LogDebug(
                "Episode launch of '{Title}' on device {DeviceId} without the VideoApp interface: degrading to the AudioPlayer audio-only route (JF-586)",
                item.Name,
                context?.System?.Device?.DeviceID ?? "unknown");
            return BuildEpisodeAudioDegrade(item, user, context, resumeTicks, outputSpeech);
        }

        // JF-589: audio-only content (the .strm podcast shape) must NOT ride the
        // position-blind VideoApp player even on a capable device; see the remarks
        // on this method's doc. The degrade body is the JF-586 one, verbatim.
        if (isEpisode && capable && IsAudioOnlyEpisode(item))
        {
            _logger.LogInformation(
                "Episode launch of '{Title}' ({ItemId}) on device {DeviceId} is audio-only content: routing AudioPlayer for position tracking instead of the position-blind VideoApp player (JF-589)",
                item.Name,
                item.Id,
                context?.System?.Device?.DeviceID ?? "unknown");
            return BuildEpisodeAudioDegrade(item, user, context, resumeTicks, outputSpeech);
        }

        return await BuildVideoAppLaunchResponseAsync(context, request, locale, sourceUrl, item.Name, outputSpeech).ConfigureAwait(false);
    }

    /// <summary>
    /// The JF-586/JF-589 episode AudioPlayer degrade body, shared by the two entry
    /// conditions (screenless device, audio-only shape on a capable device) so the
    /// clamp, the audio-source resolution and the announce ride cannot drift.
    /// </summary>
    private SkillResponse BuildEpisodeAudioDegrade(
        BaseItem item,
        Entities.User user,
        Context? context,
        long resumeTicks,
        IOutputSpeech? outputSpeech)
    {
        // The JF-565 clamp, audio-route mirror: a stored position at or beyond
        // the runtime cannot be a legitimate mid-episode resume (only stale
        // state reaches it), and an UNKNOWN runtime cannot prove the position
        // is within the content (the zero-runtime .strm shape), so both fail
        // closed to a fresh start rather than minting an offset the stream
        // cannot serve.
        long safeTicks = ClampResumeTicksToRuntime(item, resumeTicks, "Episode audio degrade");
        int offsetMs = (int)Math.Min(TimeSpan.FromTicks(safeTicks).TotalMilliseconds, int.MaxValue);
        string itemId = item.Id.ToString();
        AudioLaunchSource source = ResolveAudioLaunchSource(item, itemId, user, offsetMs);
        SkillResponse response = BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll,
            source,
            itemId,
            item,
            user,
            context);

        // The caller-chosen announce rides the final response: the wording gate
        // already ran at the caller against the VideoApp delivery verdict, and
        // on this route the transcode ?start= delivers exactly what a
        // position-bearing announce claims while the static directive offset can
        // only resume further than a fresh-play wording admits, never less.
        response.Response.OutputSpeech = outputSpeech;
        return response;
    }

    /// <summary>
    /// The JF-589 audio-shape probe: positive evidence that an Episode's content is
    /// AUDIO-ONLY, namely the media streams are readable and carry at least one
    /// audio stream and NO video stream (the .strm podcast shape). Any other shape
    /// (a video stream present = a real TV episode, zero streams = unknown, no
    /// audio stream = unknown) and any probe failure are NOT audio-only: the caller
    /// keeps today's VideoApp behavior, because the route may only change on
    /// positive content-truth evidence, never on a guess (fail-open contract).
    /// </summary>
    private bool IsAudioOnlyEpisode(BaseItem item)
    {
        try
        {
            IReadOnlyList<MediaBrowser.Model.Entities.MediaStream> streams = item.GetMediaStreams();
            if (streams.Count == 0)
            {
                return false;
            }

            return !streams.Any(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Video)
                && streams.Any(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Audio);
        }
        catch (Exception ex)
        {
            // Same fail-open direction as ResolveVideoAppStreamDecision: the probe
            // may only MOVE the audio route, never break the launch path.
            _logger.LogDebug(
                ex,
                "JF-589 audio-shape probe could not read media streams for item {ItemId}; keeping the VideoApp route",
                item.Id);
            return false;
        }
    }

    /// <summary>
    /// Launch a live-TV channel (the shared launch block of PlayChannelIntentHandler
    /// and PlayRadioIntentHandler's channel tier, extracted by JF-483 so the two paths
    /// cannot drift: a resolver change or launch-directive fix lands once). Live TV
    /// must launch via VideoApp.Launch (like movies/episodes) so it plays on Echo Show:
    /// the AudioPlayer static stream URL used by music playback 500s for a live source.
    /// The resolver picks the correct URL via Jellyfin's PlaybackInfo (direct-remote HLS
    /// or the transcode fallback); an unresolvable stream yields the not-available Tell.
    /// Moved here from BaseHandler (JF-315 batch 5) with the rest of the VideoApp
    /// launch family.
    /// </summary>
    /// <param name="streamResolver">The live-TV stream resolver (PlaybackInfo URL).</param>
    /// <param name="channel">The LiveTvChannel item to launch.</param>
    /// <param name="context">The Alexa context (device id for the queue record).</param>
    /// <param name="request">The skill request (JF-501 progressive announce vehicle).</param>
    /// <param name="user">The plugin user (stream resolution + announce toggles).</param>
    /// <param name="session">The Jellyfin session (queue + now-playing).</param>
    /// <param name="locale">The request locale, for response strings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The VideoApp.Launch response, or the not-available Tell when the stream cannot be resolved.</returns>
    internal async Task<SkillResponse> BuildChannelLaunchResponseAsync(
        ILiveTvStreamResolver streamResolver,
        BaseItem channel,
        Context context,
        Request request,
        Entities.User user,
        SessionInfo session,
        string locale,
        CancellationToken cancellationToken)
    {
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = channel.Id } };
        session.FullNowPlayingItem = channel;

        // JF-505 simplify: the capability gate runs FIRST, before the stream resolver's
        // bounded-5s PlaybackInfo round-trip: on a screenless device the whole resolution
        // would be spent on a launch the shared builder then refuses. Refusing early also
        // makes the last-played record below unconditional-for-capable-devices (a refused
        // channel can no longer be recorded and later offered as an unplayable resume).
        if (!Interface.VideoAppCapabilities.DeviceSupportsVideoApp(context))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("VideoRequiresScreen", locale));
        }

        LiveTvStream? stream = await streamResolver.ResolveAsync(channel, user, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaTypeNotAvailable", locale));
        }

        // Record the last-played channel for this device (the resume / continue-watching signal).
        // Mirrors the chokepoint in BuildAudioPlayerResponse; needed here because the
        // direct-remote stream URL has no /Videos/ segment for LastPlayedResponseInterceptor to parse.
        string? deviceId = context?.System?.Device?.DeviceID;
        if (!string.IsNullOrEmpty(deviceId))
        {
            Plugin.Instance?.DeviceQueueManager?.RecordLastPlayed(deviceId, channel.Id.ToString(), DeviceQueueManager.LaunchRoute.VideoApp);
        }

        return await BuildVideoAppLaunchResponseAsync(
            context,
            request,
            locale,
            stream.Url,
            channel.Name,
            SpeechBuilder.BuildNowPlayingSpeech(channel.Name, locale, GetAnnounceNowPlaying(user))).ConfigureAwait(false);
    }

    /// <summary>
    /// Build a VideoApp.Launch response for an audiobook RESUME, pointing at the resume-aware
    /// HLS playlist (<c>?start=&lt;ticks&gt;</c>). The position slices the playlist
    /// (<c>?start=</c>, the active mechanism; ExoPlayer ignores <c>#EXT-X-START</c>, hardware-verified);
    /// VideoApp.Launch has no offset parameter, so this keeps the seek bar AND resumes at position. Use the book's parent-folder ID for the concat stream.
    /// JF-505: on a device without the VideoApp interface the directive is rejected by
    /// the platform; audiobooks are audio, so the builder degrades to the AudioPlayer
    /// resume (which supports an offset) instead of failing the play.
    /// Moved here from BaseHandler (JF-315 batch 5) beside
    /// <see cref="BuildVideoAppAudioResponse"/> (the fresh-play audiobook sibling).
    /// </summary>
    /// <param name="item">An audiobook chapter item (its ParentId is the book folder).</param>
    /// <param name="startTicks">Resume position in .NET ticks.</param>
    /// <param name="user">The plugin user (fallback audio stream URL on screenless devices).</param>
    /// <param name="context">The Alexa context, for the JF-505 screenless-device check. Null (or a context without capability data) keeps the VideoApp path.</param>
    /// <returns>A VideoApp.Launch SkillResponse targeting the resume playlist, or an AudioPlayer resume on a screenless device.</returns>
    internal SkillResponse BuildAudiobookResumeResponse(
        MediaBrowser.Controller.Entities.BaseItem item,
        long startTicks,
        Entities.User user,
        Context? context)
    {
        if (!Interface.VideoAppCapabilities.DeviceSupportsVideoApp(context))
        {
            _logger.LogDebug(
                "BuildAudiobookResumeResponse: device {DeviceId} has no VideoApp interface, book item {ItemId} degrades to AudioPlayer resume",
                context?.System?.Device?.DeviceID ?? "unknown", item.Id);
            // The degrade plays the SINGLE chapter flat, but startTicks may count the
            // whole-book concat timeline (tracker-first resolution): clamp to the
            // chapter's runtime (when known) so the directive never carries an offset
            // past the end of the stream it plays.
            long clampedTicks = Math.Max(startTicks, 0);
            long runTimeTicks = item.RunTimeTicks ?? 0;
            if (runTimeTicks > 0)
            {
                clampedTicks = Math.Min(clampedTicks, runTimeTicks);
            }

            int offsetMs = (int)Math.Min(TimeSpan.FromTicks(clampedTicks).TotalMilliseconds, int.MaxValue);
            return BuildAudioPlayerResponse(
                PlayBehavior.ReplaceAll,
                GetStreamUrl(item.Id.ToString(), user),
                item.Id.ToString(),
                item,
                user,
                context,
                offsetMs);
        }

        // JF-563 review: record the device last-played ledger here (this VideoApp launch
        // bypasses the BuildAudioPlayerResponse chokepoint that owns the record, and the
        // response interceptor skips audiobook-concat URLs; see BuildVideoAppAudioResponse).
        string? ledgerDeviceId = context?.System?.Device?.DeviceID;
        if (!string.IsNullOrEmpty(ledgerDeviceId))
        {
            (Plugin.Instance?.DeviceQueueManager)?.RecordLastPlayed(ledgerDeviceId, item.Id.ToString(), DeviceQueueManager.LaunchRoute.VideoApp);
        }

        // JF-567: this GUID feeds a URL path segment, so it keeps the default dashed
        // Guid format. It is NOT the tracker bookKey (ResumeMath.GetAudiobookBookKey's
        // "N" string); AudiobookPositionTracker.NormalizeKey canonicalizes both shapes,
        // so the segment-record path (URL dashed) and the resume-read path ("N") agree
        // in the tracker dictionary.
        Guid parentId = item.ParentId != Guid.Empty ? item.ParentId : item.Id;
        string videoAudioUrl = GetAudiobookResumeUrl(parentId.ToString(), startTicks);

        _logger.LogDebug(
            "BuildAudiobookResumeResponse: itemId={ItemId}, parentId={ParentId}, startTicks={Ticks}, url={Url}",
            item.Id, parentId, startTicks, videoAudioUrl);

        return new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                // VideoApp.Launch must NOT include shouldEndSession.
                ShouldEndSession = null,
                Directives = new List<IDirective>
                {
                    new Directive.VideoAppLaunchDirective
                    {
                        VideoItem = new Directive.VideoItem
                        {
                            Source = videoAudioUrl,
                            Metadata = new Directive.VideoItemMetadata
                            {
                                Title = item.Name ?? string.Empty,
                                Subtitle = GetSubtitle(item)
                            }
                        }
                    }
                }
            }
        };
    }

    /// <summary>
    /// The shared audiobook VideoApp fresh-launch composition (JF-567): the launch build
    /// plus the JF-501 progressive-announce attachment. One home for the
    /// screenless-degradation rule (SpeakVideoLaunchAnnounceAsync degrades to riding the
    /// final response on a device without the VideoApp interface, where the AudioPlayer
    /// fallback has no progressive cut). PlayBook fresh-start, YesIntent's JF-361
    /// PlayBook confirmation, and StartOver's book restart differ only in the announce
    /// speech and the item they launch.
    /// </summary>
    /// <param name="itemId">The launch item id (stream URL + AudioPlayer token shape).</param>
    /// <param name="item">The audiobook item to launch.</param>
    /// <param name="announce">The announce speech (book title now-playing or restart notice).</param>
    /// <param name="user">The plugin user (announce toggles + stream URLs).</param>
    /// <param name="context">The Alexa context, for device capability detection.</param>
    /// <param name="request">The skill request, for the progressive-response vehicle.</param>
    /// <returns>The VideoApp.Launch response with the announce attached.</returns>
    internal async Task<SkillResponse> BuildAudiobookVideoAppLaunchResponseAsync(
        string itemId,
        MediaBrowser.Controller.Entities.BaseItem item,
        IOutputSpeech? announce,
        Entities.User user,
        Context? context,
        Request? request)
    {
        SkillResponse response = BuildVideoAppAudioResponse(itemId, item, user, context: context);
        // JF-501: the announce rides the progressive vehicle on a VideoApp launch; a
        // screenless device degrades to AudioPlayer, where it stays on the final response.
        response.Response.OutputSpeech = await SpeakVideoLaunchAnnounceAsync(context, request, announce).ConfigureAwait(false);
        return response;
    }

    private string BuildStreamUrl(string pathSegment, string itemId, Entities.User user)
        => new Uri(new Uri(_config.ServerAddress), $"{pathSegment}{itemId}/stream?static=true&api_key={user.JellyfinToken}").ToString();

    /// <summary>
    /// Get a cover art image URL for the given item.
    /// </summary>
    /// <param name="itemId">Id of the item.</param>
    /// <param name="user">The user for authentication.</param>
    /// <returns>URL of the item's primary image.</returns>
    public string GetImageUrl(string itemId, Entities.User user)
    {
        return new Uri(new Uri(_config.ServerAddress), "Items/" + itemId + "/Images/Primary?api_key=" + user.JellyfinToken).ToString();
    }

    /// <summary>
    /// Get a video-audio URL that combines album art with audio into an HLS stream
    /// for Echo Show VideoApp playback with native progress bar controls.
    /// HLS provides correct duration and seek support from the very first play.
    /// </summary>
    /// <param name="itemId">Id of the audio item.</param>
    /// <returns>URL to the HLS video-audio endpoint.</returns>
    internal string GetVideoAudioUrl(string itemId)
        => new Uri(new Uri(_config.ServerAddress), $"alexaskill/api/video-audio/{itemId}/stream.m3u8?token={StreamTokenHelper.Mint(itemId, _config.StreamTokenSecret)}").ToString();

    /// <summary>
    /// Get a video-audio URL for an audiobook that concatenates all chapters into
    /// one continuous HLS stream. The parent ID is the book folder containing all
    /// AudioBook chapter items. Segments are served by the existing segment endpoint
    /// using the parent GUID as the cache key (no collision with single-item entries).
    /// </summary>
    /// <param name="parentId">Id of the audiobook parent folder.</param>
    /// <returns>URL to the audiobook HLS concat endpoint.</returns>
    private string GetAudiobookVideoAudioUrl(string parentId)
        => GetAudiobookResumeUrl(parentId, startTicks: 0);

    /// <summary>
    /// Get the video-audio URL for the AUDIO-ONLY episode transcode (JF-507): the item's
    /// audio track alone (-map 0:a:0, AAC stereo) as an HLS stream, for AudioPlayer
    /// launches of video items whose audio codec has no Echo decoder (the raw
    /// <c>/Audio/{id}/stream?static=true</c> URL serves the undecodable bytes and the Dot
    /// dies at 1ms). Optional <paramref name="startTicks"/> seeks the encode (ffmpeg -ss):
    /// the returned stream's timeline starts AT that position, so the AudioPlayer.Play
    /// directive must carry offset 0 for it. Token is the same item-scoped HMAC as the
    /// other video-audio endpoints (JF-309).
    /// </summary>
    /// <param name="itemId">Id of the video item.</param>
    /// <param name="startTicks">Resume position in .NET ticks (0 to play from the start).</param>
    /// <returns>URL to the audio-only episode HLS endpoint.</returns>
    private string GetEpisodeAudioUrl(string itemId, long startTicks = 0)
    {
        string query = startTicks > 0
            ? $"?start={startTicks}&token={StreamTokenHelper.Mint(itemId, _config.StreamTokenSecret)}"
            : $"?token={StreamTokenHelper.Mint(itemId, _config.StreamTokenSecret)}";
        return new Uri(new Uri(_config.ServerAddress), $"alexaskill/api/video-audio/episode/{itemId}/audio.m3u8{query}").ToString();
    }

    /// <summary>
    /// Build a subtitle string from item metadata for display on Echo Show/Fire TV.
    /// </summary>
    internal static string GetSubtitle(BaseItem? item)
    {
        if (item is MediaBrowser.Controller.Entities.Audio.Audio audio)
        {
            return audio.Artists?.Count > 0
                ? audio.Artists[0]
                : audio.Album ?? string.Empty;
        }

        if (item is MediaBrowser.Controller.Entities.TV.Episode episode)
        {
            return episode.SeriesName ?? string.Empty;
        }

        return string.Empty;
    }

    /// <summary>
    /// Attach the gated now-playing announce to a MUSIC play response when the caller passes a
    /// locale and the audio-announce toggle is on. Only music handlers pass announceLocale, so the
    /// gate is <see cref="GetAnnounceAudioPlays"/> (opt-in, default false per JF-352.4), which is
    /// NOT the video/book <see cref="GetAnnounceNowPlaying"/> toggle. When offsetMs &gt; 0 the announce is a
    /// resume ("Resuming X") rather than a fresh "Now playing X". The OutputSpeech-occupied guard
    /// is idempotency only: callers that set a more specific announcement (e.g. FoundAlbumInstead)
    /// do so AFTER this call and overwrite it themselves.
    /// </summary>
    private void AttachAnnounceIfEnabled(SkillResponse response, MediaBrowser.Controller.Entities.BaseItem? item, Entities.User? user, string? announceLocale, int offsetInMilliseconds = 0)
    {
        if (string.IsNullOrEmpty(announceLocale) || item is null || response.Response.OutputSpeech is not null)
        {
            return;
        }

        if (!GetAnnounceAudioPlays(user))
        {
            return;
        }

        response.Response.OutputSpeech = offsetInMilliseconds > 0
            ? SpeechBuilder.BuildOutputSpeech("ResumingSsml", "Resuming", announceLocale, item.Name)
            : SpeechBuilder.BuildNowPlayingSpeech(item.Name, announceLocale, announceOn: true);
    }

    /// <summary>
    /// Gets the effective "speak the now-playing announce on MUSIC plays" preference for a user,
    /// falling back to the global <see cref="Configuration.PluginConfiguration.AnnounceAudioPlays"/>
    /// default (false, i.e. audio plays are silent by default, JF-352.4). Per-user setting takes
    /// precedence. Video/book launches use the <see cref="GetAnnounceNowPlaying"/> resolver instead.
    /// </summary>
    private bool GetAnnounceAudioPlays(Entities.User? user)
    {
        if (user?.AnnounceAudioPlays is { } userPref)
        {
            _logger.LogDebug("AnnounceAudioPlays: user={UserId} on={On} source=PerUser", user.Id, userPref);
            return userPref;
        }

        _logger.LogDebug("AnnounceAudioPlays: user={UserId} on={On} source=GlobalDefault", user?.Id, _config.AnnounceAudioPlays);
        return _config.AnnounceAudioPlays;
    }

    /// <summary>
    /// Gets the effective "speak the now-playing announce on launch" preference for a user,
    /// falling back to the global default. Per-user setting (when explicitly set) takes precedence.
    /// Moved here from BaseHandler (JF-315 batch 5) next to <see cref="GetAnnounceAudioPlays"/>
    /// (the MUSIC-plays sibling, JF-352.4): both are per-user-override-then-global resolvers
    /// feeding the same now-playing speech.
    /// </summary>
    internal bool GetAnnounceNowPlaying(Entities.User? user)
    {
        if (user?.AnnounceNowPlaying is { } userPref)
        {
            _logger.LogDebug("AnnounceNowPlaying: user={UserId} on={On} source=PerUser", user.Id, userPref);
            return userPref;
        }

        _logger.LogDebug("AnnounceNowPlaying: user={UserId} on={On} source=GlobalDefault", user?.Id, _config.DefaultAnnounceNowPlaying);
        return _config.DefaultAnnounceNowPlaying;
    }

    /// <summary>
    /// Gets the effective "play music via VideoApp" preference for a user, falling back to the
    /// global <see cref="Configuration.PluginConfiguration.NativeControlsForAudio"/> default.
    /// Per-user setting (when explicitly set, i.e. non-null) takes precedence. When true, music
    /// (Audio items) is routed through VideoApp.Launch (native seek bar, ffmpeg video-audio
    /// encode); when false, music uses plain AudioPlayer.Play with the raw stream URL. Audiobooks
    /// are governed by <c>NativeControlsForBooks</c> and are not affected by this resolver.
    /// </summary>
    internal bool GetVideoAppForAudio(Entities.User? user)
    {
        if (user?.VideoAppForAudio.HasValue == true)
        {
            _logger.LogDebug("VideoAppForAudio: user={UserId} value={Value} source=PerUser", user.Id, user.VideoAppForAudio.Value);
            return user.VideoAppForAudio.Value;
        }

        bool global = _config.NativeControlsForAudio;
        _logger.LogDebug("VideoAppForAudio: user={UserId} value={Value} source=GlobalDefault", user?.Id, global);
        return global;
    }

    /// <summary>
    /// JF-501 delivery vehicle for the video-launch announce: speak it as an awaited
    /// progressive response AFTER the launch URL is resolved and BEFORE the final launch
    /// response returns (the send is strictly between the two: the URL is a call argument,
    /// so it has evaluated by the time the handler reaches this method, and the device only
    /// plays a progressive response that arrives before the full response), so the final
    /// VideoApp.Launch response carries the directive only (no OutputSpeech). Device
    /// evidence (JF-498 verification 2026-09-06): when the announce rides the final
    /// response, the VideoApp player takes the audio channel before the TTS finishes on
    /// fast-start HLS routes (~0.6s playlist), cutting the announcement mid-sentence; a
    /// progressive response is spoken BEFORE the final response reaches the device, so the
    /// announce completes before the player opens. Amazon contracts this relies on ("Send
    /// the User a Progressive Response"): progressive responses exist only for
    /// IntentRequest and LaunchRequest, the speech must be valid SSML wrapped in speak
    /// tags, and the device only plays a progressive response that arrives before the full
    /// response, hence the awaited send. A FAILED send (2s timeout, auth rejection,
    /// network error) falls back to the announce riding the final response, the pre-JF-501
    /// shape, so a progressive failure can no longer lose the announce. The guard set
    /// mirrors the launch shapes that cannot use the vehicle and keep the announce on the
    /// final response instead: a null announce (toggle off; today's silent shape), a
    /// request type the progressive API cannot serve (UserEvent carousel taps), a null
    /// context (DeviceSupportsVideoApp fails OPEN on absent capability data, so the null
    /// case needs its own gate to keep the send from dereferencing it), and a device
    /// without the VideoApp interface (the launch degrades to a capability Tell or to
    /// AudioPlayer, whose audio announce has no such cut).
    /// Moved here from BaseHandler (JF-315 batch 5): the progressive send itself stays
    /// VIRTUAL on BaseHandler (the JF-501 test seam ~15 harnesses override); this
    /// builder reaches it through the composition-time delegate (see the constructor).
    /// </summary>
    /// <param name="context">The Alexa context (device capability check).</param>
    /// <param name="request">The skill request; null or a non-intent/launch request keeps the classic final-response announce.</param>
    /// <param name="announce">The launch announce to speak, or null when the announce toggle is off.</param>
    /// <returns>The OutputSpeech the final response should carry: null when the announce was
    /// spoken progressively and the send succeeded; the announce itself when the vehicle was
    /// unusable or the send failed (it rides the final response).</returns>
    internal async Task<IOutputSpeech?> SpeakVideoLaunchAnnounceAsync(Context? context, Request? request, IOutputSpeech? announce)
    {
        if (announce is null
            || request is not (IntentRequest or LaunchRequest)
            || context is null // DeviceSupportsVideoApp fails OPEN on absent capability data; the send would NRE.
            || !Interface.VideoAppCapabilities.DeviceSupportsVideoApp(context))
        {
            return announce;
        }

        // SSML speech is already speak-wrapped; plain text must be wrapped (and
        // XML-escaped) per the progressive-response contract, unlike OutputSpeech,
        // which accepts bare text.
        string? progressiveSpeech = announce switch
        {
            SsmlOutputSpeech ssml => ssml.Ssml,
            PlainTextOutputSpeech plain when !string.IsNullOrWhiteSpace(plain.Text) => $"<speak>{SpeechBuilder.EscapeXml(plain.Text)}</speak>",
            _ => null
        };
        if (progressiveSpeech is null)
        {
            return announce;
        }

        // Awaited, unlike the fire-and-forget SearchingMedia ping: the device only
        // plays a progressive response that arrives before the full response. A failed
        // send falls back to the announce riding the final response (the pre-JF-501
        // shape) instead of being lost.
        bool sent = await _sendProgressiveResponse(context, request, progressiveSpeech).ConfigureAwait(false);
        return sent ? null : announce;
    }

    /// <summary>
    /// Resume-aware video-launch announce: "Resuming X from Y" when the user has playback
    /// progress, else the now-playing announce. VideoApp.Launch cannot honor the offset, so
    /// this only informs the user where they left off (playback still starts from the
    /// beginning) - except where JF-565 applies: a caller that passes the position into
    /// <see cref="GetVideoAppLaunchUrl(BaseItem, Entities.User, long)"/> for an EPISODE slices the playlist and really
    /// resumes (TvNextUpService); the movie-shaped and fresh-play callers keep the
    /// informational-only meaning.
    /// The fresh-play announce (resumeTicks == 0) is suppressed when announceOn is false; the
    /// resume announce is always spoken (position info, not the now-playing readout).
    /// Moved here from BaseHandler (JF-315 batch 9): the announce speech pair the
    /// launch chokepoints consume (progressively via
    /// <see cref="SpeakVideoLaunchAnnounceAsync"/> on intent/launch paths). Instance
    /// (not static) to match the announce family's shape; the bodies use no instance state.
    /// </summary>
    internal IOutputSpeech? BuildVideoLaunchSpeech(BaseItem item, string locale, long resumeTicks, bool announceOn)
    {
        // TASK-HIGH.1: it-IT daily-podcast episodes speak the date instead of the raw
        // "Ep. N" name (the number would be read twice); every other case is null and
        // keeps item.Name. Display/APL metadata elsewhere keeps the raw name.
        string speechTitle = SpeechBuilder.FormatEpisodeAnnounceTitle(item, locale) ?? item.Name;
        if (resumeTicks > 0)
        {
            return new PlainTextOutputSpeech(ResponseStrings.Get("ResumingVideo", locale, speechTitle, ResumeMath.FormatPosition(resumeTicks)));
        }

        return SpeechBuilder.BuildNowPlayingSpeech(speechTitle, locale, announceOn);
    }

    /// <summary>
    /// Resume-aware video-launch announce that fetches the playback position itself. Falls back
    /// to the (gated) now-playing announce if the deps are unavailable.
    /// Moved here from BaseHandler (JF-315 batch 9) with its ticks sibling.
    /// </summary>
    internal IOutputSpeech? BuildVideoLaunchSpeech(BaseItem item, string locale, IUserDataManager? userDataManager, Jellyfin.Database.Implementations.Entities.User? jellyfinUser, bool announceOn)
    {
        long resumeTicks = (userDataManager is not null && jellyfinUser is not null)
            ? (userDataManager.GetUserData(jellyfinUser, item)?.PlaybackPositionTicks ?? 0)
            : 0;
        return BuildVideoLaunchSpeech(item, locale, resumeTicks, announceOn);
    }

    /// <summary>
    /// Build an AudioPlayer response with cover art metadata.
    /// </summary>
    /// <param name="playBehavior">The play behavior (ReplaceAll, Enqueue, ReplaceEnqueued).</param>
    /// <param name="streamUrl">The audio stream URL.</param>
    /// <param name="itemId">The item ID used as the stream token.</param>
    /// <param name="item">The media item for metadata (title, art), or null.</param>
    /// <param name="user">The user for building the image URL.</param>
    /// <param name="offsetInMilliseconds">Resume offset in milliseconds (default 0).</param>
    /// <returns>A SkillResponse containing the AudioPlayer directive with metadata.</returns>
    /// <summary>
    /// Context-less convenience overload (JF-505 simplify note): PRODUCTION-DEAD since the
    /// capability gate landed (every production caller passes the context so the
    /// native-controls delegation gate evaluates); only pre-existing tests use it. The
    /// hard-coded context:null means FAIL-OPEN (the VideoApp gate is skipped): do NOT
    /// call this from production code, thread the context overload instead.
    /// </summary>
    public SkillResponse BuildAudioPlayerResponse(PlayBehavior playBehavior, string streamUrl, string itemId, MediaBrowser.Controller.Entities.BaseItem? item, Entities.User user, int offsetInMilliseconds = 0)
    {
        return BuildAudioPlayerResponse(playBehavior, streamUrl, itemId, item, user, null, offsetInMilliseconds, announceLocale: null);
    }

    /// <summary>
    /// Build an AudioPlayer response with cover art metadata.
    /// </summary>
    /// <param name="playBehavior">The play behavior (ReplaceAll, Enqueue, ReplaceEnqueued).</param>
    /// <param name="streamUrl">The audio stream URL.</param>
    /// <param name="itemId">The item ID used as the stream token.</param>
    /// <param name="item">The media item for metadata (title, art), or null.</param>
    /// <param name="user">The user for building the image URL.</param>
    /// <param name="context">Optional Alexa context for enqueue previous-token tracking.</param>
    /// <param name="offsetInMilliseconds">Resume offset in milliseconds (default 0).</param>
    /// <param name="announceLocale">Optional locale for the now-playing announce.</param>
    /// <param name="queueManager">Optional per-device queue manager holding the launch-scope store (JF-522); null falls back to <c>Plugin.Instance</c>'s (pass one explicitly to keep unit tests off the shared plugin instance).</param>
    /// <param name="launchBaseMs">The item-absolute launch base of the stream this directive plays (<see cref="AudioLaunchSource.LaunchBaseMs"/>; 0 for raw-static/precomputed launches). Recorded at this chokepoint so the playback event writers can persist item-absolute positions (JF-522).</param>
    /// <param name="ratePerMille">The stream's playback rate in per-mille form (JF-636: 1000 = identity). Recorded beside the launch base so the event writers scale the stream's raw offsets; also disables the native-controls VideoApp delegation, which has no rate support and would silently drop the speed.</param>
    /// <returns>A SkillResponse containing the AudioPlayer directive.</returns>
    public SkillResponse BuildAudioPlayerResponse(PlayBehavior playBehavior, string streamUrl, string itemId, MediaBrowser.Controller.Entities.BaseItem? item, Entities.User user, Context? context, int offsetInMilliseconds = 0, string? announceLocale = null, DeviceQueueManager? queueManager = null, long launchBaseMs = 0, Guid? collectionParentId = null, long collectionStartTicks = 0, int ratePerMille = 1000)
    {
        // Record the last user-initiated play for this device (ReplaceAll = a new item starts).
        // This is the universal chokepoint: every play path flows through here, including APL
        // carousel taps and resume confirmations that bypass SetQueue. Captures VideoApp.Launch
        // plays too (which don't update context.AudioPlayer.Token), giving LaunchRequestHandler
        // a reliable device-specific "what did this Echo last play" signal.
        // JF-568: records the AUDIO route. The native-controls delegation below re-records
        // with the VideoApp route when it actually builds a VideoApp.Launch for the item, so
        // the route always names the directive that went out, not the builder that started.
        string? deviceId = context?.System?.Device?.DeviceID;
        if (playBehavior == PlayBehavior.ReplaceAll && !string.IsNullOrEmpty(deviceId))
        {
            (queueManager ?? Plugin.Instance?.DeviceQueueManager)?.RecordLastPlayed(deviceId, itemId, DeviceQueueManager.LaunchRoute.Audio);
        }

        // Route initial playback through VideoApp when native controls are enabled for the
        // item's category. Enqueue/ReplaceEnqueued stay as AudioPlayer for queue building.
        // Resume (offset > 0) also stays as AudioPlayer since VideoApp has no offset support
        // (audiobook resume is handled separately via a resume-aware HLS playlist).
        // AudioBook items use a special concat HLS endpoint that joins all chapters into
        // one continuous stream, giving the full book duration in the seek bar.
        // JF-636: a non-identity rate stays on AudioPlayer too. The atempo stream is
        // an AudioPlayer re-launch by design, and the VideoApp delegation below would
        // rebuild the URL from the plain video-audio endpoint, silently dropping the
        // speed (speed on the VideoApp seek path is refused honestly by the
        // SetPlaybackSpeed handler instead of being dropped here).
        if (playBehavior == PlayBehavior.ReplaceAll && offsetInMilliseconds == 0 && ratePerMille == 1000)
        {
            bool wantsNativeControls = false;
            if (item != null)
            {
                if (AudiobookItems.IsAudioBook(item))
                {
                    wantsNativeControls = Plugin.Instance?.Configuration?.NativeControlsForBooks == true;
                }
                else if (item is MediaBrowser.Controller.Entities.Audio.Audio)
                {
                    wantsNativeControls = GetVideoAppForAudio(user);
                }
            }

            // JF-505: native controls need the VideoApp interface; on a screenless device
            // (no VideoApp in SupportedInterfaces) fall through to the plain AudioPlayer
            // build below, which that device CAN play. This check is also the recursion
            // break for BuildVideoAppAudioResponse's own screenless fallback.
            if (wantsNativeControls && Interface.VideoAppCapabilities.DeviceSupportsVideoApp(context))
            {
                return BuildVideoAppAudioResponse(itemId, item, user, announceLocale, context, collectionParentId, collectionStartTicks);
            }
        }

        // JF-522 launch-scope capture: the directive below is what actually creates the
        // device stream, so ITS base is the one the playback event writers must add to
        // raw device offsets. Recorded AFTER the native-controls delegation (a VideoApp
        // launch creates no AudioPlayer stream to scope) and split active/pending by
        // behavior: an Enqueue's stream has not started yet, and on a wrapped/repeat-one
        // queue it is the SAME item still playing, whose terminal events must keep
        // composing with the running stream's base until PlaybackStarted promotes.
        if (!string.IsNullOrEmpty(deviceId))
        {
            (queueManager ?? Plugin.Instance?.DeviceQueueManager)?.RecordLaunchBase(
                deviceId,
                itemId,
                launchBaseMs,
                playBehavior is PlayBehavior.Enqueue or PlayBehavior.ReplaceEnqueued,
                ratePerMille);
        }

        _logger.LogDebug("BuildAudioPlayerResponse: itemId={ItemId}, behavior={Behavior}, offsetMs={OffsetMs}, title={Title}, streamUrl={StreamUrl}",
            itemId, playBehavior, offsetInMilliseconds, item?.Name, RequestLogRedactor.RedactUrl(streamUrl));
        string imageUrl = item != null ? GetImageUrl(itemId, user) : string.Empty;
        var imageSources = new AudioItemSources
        {
            Sources = new List<AudioItemSource> { new() { Url = imageUrl } }
        };

        // JF-636: a speed-stream URL carries the launching device as a ?d= hint so
        // the atempo endpoint can supersede only THIS device's abandoned variants
        // (tokens are item-scoped, queues per-device: another Echo may be consuming
        // another variant of the same item). Opaque, non-secret, and only ever
        // appended to atempo URLs.
        string directiveUrl = streamUrl;
        if (ratePerMille != 1000 && !string.IsNullOrEmpty(deviceId)
            && !streamUrl.Contains("?d=", StringComparison.Ordinal) && !streamUrl.Contains("&d=", StringComparison.Ordinal))
        {
            directiveUrl = streamUrl + (streamUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "d=" + Uri.EscapeDataString(deviceId);
        }

        var stream = new AudioItemStream
        {
            Url = directiveUrl,
            Token = itemId,
            OffsetInMilliseconds = offsetInMilliseconds
        };

        if (playBehavior == PlayBehavior.Enqueue && context?.AudioPlayer?.Token != null)
        {
            stream.ExpectedPreviousToken = context.AudioPlayer.Token;
        }

        var directive = new AudioPlayerPlayDirective
        {
            PlayBehavior = playBehavior,
            AudioItem = new AudioItem
            {
                Stream = stream,
                Metadata = new AudioItemMetadata
                {
                    Title = item?.Name ?? string.Empty,
                    Subtitle = GetSubtitle(item),
                    Art = imageSources,
                    BackgroundImage = imageSources
                }
            }
        };

        var response = new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                ShouldEndSession = true,
                Directives = new List<IDirective> { directive }
            }
        };

        if (Plugin.Instance?.Configuration?.SeekEnabled == true && item != null
            && playBehavior != PlayBehavior.Enqueue)
        {
            string cardTitle = item.Name ?? string.Empty;
            var parts = new List<string>();

            if (item is MediaBrowser.Controller.Entities.Audio.Audio audio)
            {
                string? artist = audio.Artists?.Count > 0 ? audio.Artists[0] : null;
                if (!string.IsNullOrEmpty(artist))
                {
                    parts.Add(artist);
                }

                if (!string.IsNullOrEmpty(audio.Album))
                {
                    string album = audio.Album;
                    if (audio.IndexNumber.HasValue)
                    {
                        album = $"#{audio.IndexNumber.Value} — {album}";
                    }

                    parts.Add(album);
                }
                else if (audio.IndexNumber.HasValue)
                {
                    parts.Add($"Track #{audio.IndexNumber.Value}");
                }
            }

            string cardContent = parts.Count > 0
                ? $"{cardTitle}\n{string.Join("\n", parts)}"
                : cardTitle;

            long runTimeTicks = item.RunTimeTicks ?? 0;
            if (runTimeTicks > 0)
            {
                string total = ResumeMath.FormatPosition(runTimeTicks);
                if (offsetInMilliseconds > 0)
                {
                    long posTicks = (long)offsetInMilliseconds * TimeSpan.TicksPerMillisecond;
                    cardContent += $"\n{ResumeMath.FormatPosition(posTicks)} / {total}";
                }
                else
                {
                    cardContent += $"\n0:00 / {total}";
                }
            }

            response.Response.Card = new StandardCard
            {
                Title = cardTitle,
                Content = cardContent
            };
        }

        AttachAnnounceIfEnabled(response, item, user, announceLocale, offsetInMilliseconds);

        // JF-623 (live incident 2026-09-23): a fresh ReplaceAll play renders our
        // NowPlaying APL screen on capable devices. The Echo Show's own full-screen
        // player persists the LAST metadata it ever saw when nothing replaces it, so
        // without this the screen keeps showing a previous session's track while the
        // audio changes underneath. ReplaceAll only: an Enqueue must not re-render
        // the screen over the track that is still playing.
        if (playBehavior == PlayBehavior.ReplaceAll && item != null)
        {
            // JF-624 round 3: the bar needs REAL values. With the defaults (0/0) the
            // determinate AlexaProgressBar cannot compute a fill fraction and renders
            // the sweeping activity animation instead (live report: "un effetto che
            // scorre da sx a dx"). Thread the directive offset and the item runtime.
            long durationMs = (item.RunTimeTicks ?? 0) / TimeSpan.TicksPerMillisecond;
            TryAttachNowPlayingDirective(response, item, itemId, user, context, offsetInMilliseconds, durationMs);

            // JF-624 round 7 verdict (live experiment 2026-09-24): the Echo Show's native
            // now-playing surface (CardRenderer.PlayerInfo, synthesized at PlaybackStarted)
            // builds from the AudioItem METADATA alone; dropping the StandardCard from
            // APL-carrying responses changed nothing on the device (the document still only
            // flashes before the native player covers it for the whole playback). The card
            // stays: it serves the Alexa app on every device and costs nothing on screen
            // devices. Platform constraint documented in CLAUDE.md.
        }

        return response;
    }

    /// <summary>
    /// JF-522 structural pairing overload for callers holding a resolved
    /// <see cref="AudioLaunchSource"/>: the directive is built from the source's
    /// URL/offset/base as one unsplittable triple, so a transcode launch minted with a
    /// non-zero base can never be recorded into the launch-scope store as base 0 by a
    /// caller that forgot to thread the value. Callers building a directive from a bare
    /// URL (plain plays, the precompute cache hit, whose base is genuinely 0) keep the
    /// string overload.
    /// </summary>
    /// <param name="playBehavior">The play behavior (ReplaceAll, Enqueue, ReplaceEnqueued).</param>
    /// <param name="source">The resolved launch source (URL + directive offset + launch base).</param>
    /// <param name="itemId">The item ID used as the stream token.</param>
    /// <param name="item">The media item for metadata (title, art), or null.</param>
    /// <param name="user">The user for building the image URL.</param>
    /// <param name="context">Optional Alexa context for enqueue previous-token tracking.</param>
    /// <param name="announceLocale">Optional locale for the now-playing announce.</param>
    /// <param name="queueManager">Optional per-device queue manager holding the launch-scope store; null falls back to <c>Plugin.Instance</c>'s.</param>
    /// <returns>A SkillResponse containing the AudioPlayer directive.</returns>
    public SkillResponse BuildAudioPlayerResponse(
        PlayBehavior playBehavior,
        AudioLaunchSource source,
        string itemId,
        MediaBrowser.Controller.Entities.BaseItem? item,
        Entities.User user,
        Context? context,
        string? announceLocale = null,
        DeviceQueueManager? queueManager = null,
        Guid? collectionParentId = null,
        long collectionStartTicks = 0)
        => BuildAudioPlayerResponse(
            playBehavior,
            source.Url,
            itemId,
            item,
            user,
            context,
            source.OffsetMs,
            announceLocale,
            queueManager,
            source.LaunchBaseMs,
            collectionParentId,
            collectionStartTicks,
            source.RatePerMille);

    /// <summary>
    /// Build a VideoApp.Launch response for audio playback using the video-audio
    /// endpoint, which combines album art with audio into a streamable MP4.
    /// Gives native progress bar / scrubber on Echo Show.
    /// For AudioBook items, uses a special concat HLS endpoint that joins all chapters
    /// into one continuous stream so the seek bar shows the full book duration.
    /// JF-505: on a device without the VideoApp interface the directive is rejected by
    /// the platform, and the content here is AUDIO, which a screenless speaker can
    /// still play, so the builder degrades to the plain AudioPlayer response instead
    /// of failing the play.
    /// </summary>
    /// <param name="itemId">The item ID to play.</param>
    /// <param name="item">The media item for metadata.</param>
    /// <param name="user">The user for the stream URL.</param>
    /// <param name="announceLocale">Optional locale for the now-playing announce.</param>
    /// <param name="context">The Alexa context, for the screenless-device check. Null (or a context without capability data) keeps the VideoApp path.</param>
    /// <returns>A VideoApp.Launch response, or an AudioPlayer response on a screenless device.</returns>
    public SkillResponse BuildVideoAppAudioResponse(string itemId, BaseItem? item, Entities.User user, string? announceLocale = null, Context? context = null, Guid? collectionParentId = null, long collectionStartTicks = 0)
    {
        if (!Interface.VideoAppCapabilities.DeviceSupportsVideoApp(context))
        {
            _logger.LogDebug(
                "BuildVideoAppAudioResponse: device {DeviceId} has no VideoApp interface, item {ItemId} degrades to AudioPlayer",
                context?.System?.Device?.DeviceID ?? "unknown", itemId);
            AudioLaunchSource source = ResolveAudioLaunchSource(item, itemId, user, 0);
            return BuildAudioPlayerResponse(
                PlayBehavior.ReplaceAll,
                source,
                itemId,
                item,
                user,
                context,
                announceLocale);
        }

        // JF-563 review: the device last-played ledger records HERE too. Direct VideoApp
        // callers bypass the BuildAudioPlayerResponse chokepoint that owns the record,
        // and the response interceptor deliberately skips audio-via-VideoApp and
        // audiobook-concat URLs, so without this a VideoApp-launched play would vanish
        // from the ledger the launch resume-offer reads. NOT idempotent under the JF-568
        // route-marked ledger: when this builder runs after the chokepoint recorded
        // the same item with route Audio (the delegation shape), RecordLastPlayed's
        // item-AND-route short-circuit deliberately does NOT fire and the route
        // FLIPS to VideoApp, which is the truth for this launch; the screenless
        // degrade above records through the chokepoint (route Audio) as before.
        string? ledgerDeviceId = context?.System?.Device?.DeviceID;
        if (!string.IsNullOrEmpty(ledgerDeviceId))
        {
            (Plugin.Instance?.DeviceQueueManager)?.RecordLastPlayed(ledgerDeviceId, itemId, DeviceQueueManager.LaunchRoute.VideoApp);
        }

        bool isAudioBook = AudiobookItems.IsAudioBook(item);

        string videoAudioUrl;
        if (isAudioBook && item!.ParentId != Guid.Empty)
        {
            // Multi-chapter audiobook: use concat HLS endpoint keyed by parent book ID.
            // The endpoint concatenates all chapters into one continuous HLS stream,
            // giving the full book duration in the Echo Show seek bar.
            videoAudioUrl = GetAudiobookVideoAudioUrl(item.ParentId.ToString());
            // JF-580: the URL carries the signed JF-309 stream token; log it masked.
            _logger.LogDebug("BuildVideoAppAudioResponse: itemId={ItemId}, parentId={ParentId}, title={Title}, url={Url} (audiobook concat)", itemId, item.ParentId, item.Name, RequestLogRedactor.RedactUrl(videoAudioUrl));
        }
        else if (collectionParentId is Guid concatParent)
        {
            // JF-625 queue-as-concat: a music-album play in seek mode launches the
            // WHOLE album as one continuous video-audio stream (the audiobook chapter
            // shape with tracks), keyed by the album GUID. The seek bar spans the full
            // album; collectionStartTicks is the album-level resume offset (the summed
            // runtime of the tracks before the resume track) threaded by the album play
            // service.
            videoAudioUrl = GetAudiobookResumeUrl(concatParent.ToString(), collectionStartTicks);
            _logger.LogDebug("BuildVideoAppAudioResponse: itemId={ItemId}, collectionParent={ParentId}, startTicks={StartTicks}, title={Title}, url={Url} (album concat)", itemId, concatParent, collectionStartTicks, item?.Name, RequestLogRedactor.RedactUrl(videoAudioUrl));
        }
        else
        {
            videoAudioUrl = GetVideoAudioUrl(itemId);
            // JF-580: the URL carries the signed JF-309 stream token; log it masked.
            _logger.LogDebug("BuildVideoAppAudioResponse: itemId={ItemId}, title={Title}, url={Url}", itemId, item?.Name, RequestLogRedactor.RedactUrl(videoAudioUrl));
        }

        var response = new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                // VideoApp.Launch must NOT include shouldEndSession; Alexa rejects it.
                // Null omits the field from JSON serialization.
                ShouldEndSession = null,
                Directives = new List<IDirective>
                {
                    new Directive.VideoAppLaunchDirective
                    {
                        VideoItem = new Directive.VideoItem
                        {
                            Source = videoAudioUrl,
                            Metadata = new Directive.VideoItemMetadata
                            {
                                Title = item?.Name ?? string.Empty,
                                Subtitle = GetSubtitle(item)
                            }
                        }
                    }
                }
            }
        };

        AttachAnnounceIfEnabled(response, item, user, announceLocale);
        return response;
    }

    /// <summary>
    /// JF-507 decision point for every AUDIO-SHAPED launch of an item: which stream URL
    /// the AudioPlayer.Play directive should point at, and the offset to pair with it.
    /// A VIDEO item (Movie/Episode) whose audio codec has no Echo decoder (eac3/ac3/
    /// truehd/dts) cannot ride the raw static <c>/Audio/{id}/stream?static=true</c> URL:
    /// that endpoint serves the source bytes (here the whole MKV with its EAC3 track)
    /// and the Dot's AudioPlayer dies at 1ms (live incident 2026-09-06 corr=f0240020,
    /// MEDIA_ERROR_SERVICE_UNAVAILABLE). Those items route to the audio-only episode
    /// HLS transcode instead, and the offset moves into the URL (<c>?start=</c>, an
    /// ffmpeg -ss seek at encode time: a from-zero encode runs at ~49x realtime, so the
    /// segment a deep offset maps to does not exist on the first fetch), so the paired
    /// offset is 0. Everything else (audio items, video items with decodable audio)
    /// keeps today's raw static URL and the caller's offset unchanged.
    /// Sits next to <see cref="BuildVideoAppAudioResponse"/>'s screenless degradation
    /// (its mirror case: a VideoApp-shaped launch of audio content degrading to
    /// AudioPlayer) so every audio-shaped launch of a video item benefits; the wired
    /// sites are the resume-yes path, the ResumeIntent tail, the session-queue resume,
    /// JumpToPosition (absolute target), the degradation itself and the RepeatIntent
    /// music restart (JF-562).
    /// JF-522: the resolve is a PURE decision (no store writes). The launch base it
    /// computes rides out on <see cref="AudioLaunchSource.LaunchBaseMs"/> and is
    /// recorded in the device's launch-SCOPE store only when the caller issues the
    /// directive (the BuildAudioPlayerResponse chokepoint) - a precompute that never
    /// directs must not touch the scope the running stream's events compose with
    /// (the wrapped-queue clobber the JF-521 rejection documented).
    /// </summary>
    /// <param name="item">The item being launched (null keeps the raw static URL).</param>
    /// <param name="itemId">The item ID (stream token and URL path).</param>
    /// <param name="user">The user, for the raw static URL's api_key.</param>
    /// <param name="offsetMs">The resume offset the caller wants (item-relative). MUST be known item-absolute when the caller suspects the item routes to the transcode: a device/stream-relative offset minted into <c>?start=</c> seeks the wrong position (resume callers holding a stream-relative offset go through <see cref="ResolveResumedAudioLaunch"/> instead).</param>
    /// <param name="knownAudioCodec">A codec the caller already resolved (JF-520: <see cref="ResolveResumedAudioLaunch"/> probes before delegating); null probes here. A legitimately-null fail-open probe result also arrives as null and re-probes - one extra read only in that rare case.</param>
    /// <param name="ratePerMille">Playback rate in per-mille form (JF-636: 1000 keeps the codec-routed/static decision unchanged; any other served rate routes to the atempo speed endpoint, which supersedes the codec decision because atempo requires re-encoding anyway).</param>
    /// <returns>The launch source for the AudioPlayer.Play directive (URL + directive offset + launch base).</returns>
    public AudioLaunchSource ResolveAudioLaunchSource(BaseItem? item, string itemId, Entities.User user, int offsetMs, string? knownAudioCodec = null, int ratePerMille = 1000)
    {
        // JF-636 speed route FIRST: a rate other than identity redirects the launch
        // to the atempo endpoint regardless of item kind or codec (the filter
        // re-encodes to AAC, so the EAC3-family concern the JF-507 branch solves is
        // solved here too). The caller's offset is item-absolute CONTENT position:
        // it moves into the URL (?start=, an input seek) so a progressive encode
        // never stalls on a deep offset, and the directive offset is 0 (the served
        // output timeline starts at the seek point).
        if (ratePerMille != 1000 && Util.PlaybackSpeed.IsValidPerMille(ratePerMille))
        {
            long startTicks = Math.Max((long)offsetMs * TimeSpan.TicksPerMillisecond, 0);
            _logger.LogDebug(
                "Audio launch of item {ItemId} at rate {RatePerMille}/1000: routing to the atempo speed endpoint (start={StartTicks} content ticks)",
                itemId, ratePerMille, startTicks);
            return new AudioLaunchSource(GetAudioSpeedUrl(itemId, ratePerMille, startTicks), 0, LaunchBaseMs: offsetMs, RatePerMille: ratePerMille);
        }

        if (item is MediaBrowser.Controller.Entities.Movies.Movie
            or MediaBrowser.Controller.Entities.TV.Episode)
        {
            string? audioCodec = knownAudioCodec;
            if (audioCodec is null ? RoutesToAudioTranscode(item, out audioCodec) : VideoAppStreamPolicy.AudioRequiresTranscode(audioCodec))
            {
                long startTicks = Math.Max((long)offsetMs * TimeSpan.TicksPerMillisecond, 0);
                _logger.LogDebug(
                    "Audio launch of video item {ItemId}: audio codec '{AudioCodec}' has no Echo decoder, routing to the audio-only HLS transcode (start={StartTicks} ticks)",
                    itemId, audioCodec, startTicks);
                return new AudioLaunchSource(GetEpisodeAudioUrl(itemId, startTicks), 0, LaunchBaseMs: offsetMs);
            }
        }

        // Raw-static route (audio items, Echo-decodable video, and a Movie/Episode
        // whose codec the Dot plays): the output timeline IS the item timeline, so
        // the launch base is 0.
        return new AudioLaunchSource(GetStreamUrl(itemId, user), offsetMs, LaunchBaseMs: 0);
    }

    /// <summary>
    /// JF-520: the ONE resume-offset resolver, wrapping <see cref="ResolveAudioLaunchSource"/>
    /// with the JF-514 provenance correction so every resume path (the Yes-side confirm of a
    /// resume offer, the ResumeIntent tail) applies it identically. The caller states what it
    /// knows about the offset's timeline: <paramref name="offsetIsStreamRelative"/> true means
    /// device-derived (relative to the previous playback's OUTPUT timeline, which for a
    /// transcode-routed Movie/Episode starts at that stream's <c>?start=</c> base), in which
    /// case the stream's launch-scoped base is ADDED (minted <c>?start=</c> = base + offset,
    /// both terms item-absolute) and a MISSING scope drops the offset to 0 (never mint a
    /// stream-relative value silently). False means item-absolute: pass through unchanged.
    /// Raw-static launches keep the caller's offset on every path (the correction is
    /// transcode-routed only).
    /// JF-522: the base read is the LAUNCH-SCOPED store (active at the directive
    /// chokepoint), not the JF-514 last-resolve ledger it replaces: under that ledger
    /// a precompute or a wrapped-queue resolve could rewrite the entry for the same
    /// item mid-playback without any stream change, while the stream-relative offset
    /// counts the base of the stream that actually played.
    /// JF-521 hard clamp: when the item's runtime is known, a composed base+offset that
    /// reaches or exceeds it is never minted (the raw offset wins); a legitimate
    /// composition cannot get there, so that shape means a stale base or a foreign
    /// position (the JF-520 review's F1 residual and F2 retry walk).
    /// STRUCTURAL ORDERING (JF-520, was comment-enforced at the callers): the base read
    /// happens INSIDE this helper, before the resolve/directive that overwrites it. Callers
    /// must not read the launch base around this call: a read after it would observe
    /// the base this very launch just recorded (deviceId/queueManager feed exactly
    /// this read).
    /// </summary>
    /// <param name="item">The item to resume (probe + resolve target).</param>
    /// <param name="itemId">The item ID (launch-scope key + stream token).</param>
    /// <param name="user">The user, for the raw static URL's api_key.</param>
    /// <param name="offsetMs">The caller's resume offset (interpretation per <paramref name="offsetIsStreamRelative"/>).</param>
    /// <param name="offsetIsStreamRelative">True when the offset counts the previous playback's output timeline.</param>
    /// <param name="deviceId">The Alexa device ID (launch-scope key). Null skips the scope entirely.</param>
    /// <param name="queueManager">The per-device queue manager holding the launch-scope store; null falls back to <c>Plugin.Instance</c>'s (tests pass theirs).</param>
    /// <param name="logLabel">Caller identity for the rebase/drop log lines (the existing provenance-logging style).</param>
    /// <returns>The resolved launch source; the caller's directive records the (possibly rebased) launch base at the BuildAudioPlayerResponse chokepoint.</returns>
    public AudioLaunchSource ResolveResumedAudioLaunch(
        BaseItem? item,
        string itemId,
        Entities.User user,
        int offsetMs,
        bool offsetIsStreamRelative,
        string? deviceId = null,
        DeviceQueueManager? queueManager = null,
        string logLabel = "Resume")
    {
        int effectiveOffsetMs = offsetMs;
        string? probedCodec = null;

        // JF-636: the launch scope's rate decides whether the device-derived offset
        // counts a rate-adjusted output timeline (an atempo stream) that must scale
        // BEFORE the base composes, and whether the resume itself relaunches at that
        // rate (the item continues at whatever rate this device last played it).
        // Base and rate are read as ONE snapshot (a concurrent record must not pair
        // base1 with rate2).
        (long? scopeBaseMs, int? scopeRate) = GetActiveLaunchScope(deviceId, itemId, queueManager);
        int activeRate = scopeRate ?? Util.PlaybackSpeed.NormalPerMille;
        bool rateAdjustedScope = activeRate != Util.PlaybackSpeed.NormalPerMille;
        long scaledOffsetMs = Util.PlaybackSpeed.StreamMsToContent(offsetMs, activeRate);

        if (offsetIsStreamRelative && offsetMs > 0
            && (rateAdjustedScope || RoutesToAudioTranscode(item, out probedCodec)))
        {
            if (scopeBaseMs.HasValue)
            {
                long composedMs = Math.Min(scopeBaseMs.Value + scaledOffsetMs, int.MaxValue);
                // JF-521 hard clamp: a legitimate composition can never reach the item's
                // runtime (the stream-relative offset counts at most the REMAINING
                // runtime past the base), so a composed ?start= at or beyond it means a
                // stale base or a foreign position is in play (the JF-520 review's F1/F2
                // shapes). Never mint it; the rate-adjusted offset WITHOUT the base is
                // the more conservative truth (JF-636 review: the unscaled raw offset is
                // not in content units on an atempo stream, and below 1x it is LARGER
                // than the composition being guarded, which could mint a past-runtime
                // seek). Also bounds the F2 retry walk (a resolve whose directive never
                // played advances the launch scope while the offset source stays
                // frozen): once a walk's composition reaches the runtime it clamps and
                // stops growing.
                long? runtimeTicks = item?.RunTimeTicks;
                if (runtimeTicks is > 0 && composedMs * TimeSpan.TicksPerMillisecond >= runtimeTicks.Value)
                {
                    effectiveOffsetMs = (int)Math.Min(scaledOffsetMs, int.MaxValue);
                    _logger.LogInformation(
                        "{Label}: composed ?start= of {ComposedMs}ms (launch base {BaseMs}ms + stream-relative offset {OffsetMs}ms at rate {RatePerMille}/1000) for item {ItemId} reaches or exceeds the item runtime ({RuntimeMs}ms); a legitimate composition cannot, so a stale base or foreign position is in play; clamping to the rate-adjusted {ScaledMs}ms offset without the base as the more conservative truth",
                        logLabel, composedMs, scopeBaseMs.Value, offsetMs, activeRate, itemId, runtimeTicks.Value / TimeSpan.TicksPerMillisecond, effectiveOffsetMs);
                }
                else
                {
                    effectiveOffsetMs = (int)composedMs;
                    _logger.LogInformation(
                        "{Label}: item {ItemId} resume offset ({OffsetMs}ms) is device-derived (stream-relative at rate {RatePerMille}/1000); recorded launch base {BaseMs}ms, minting ?start={StartMs}ms (item-absolute)",
                        logLabel, itemId, offsetMs, activeRate, scopeBaseMs.Value, effectiveOffsetMs);
                }
            }
            else
            {
                effectiveOffsetMs = 0;
                _logger.LogInformation(
                    "{Label}: item {ItemId} has a transcode/speed-routed resume offset ({OffsetMs}ms) that is device-derived (stream-relative) but no launch base is recorded for device {DeviceId}; dropping it so playback restarts instead of minting a false ?start=",
                    logLabel, itemId, offsetMs, deviceId);
            }
        }

        // probedCodec (null on the fail-open path, where the resolve re-probes) saves
        // the resolve's second media-streams DB read (JF-520 simplify finding E1).
        return ResolveAudioLaunchSource(
            item, itemId, user, effectiveOffsetMs, probedCodec,
            rateAdjustedScope ? activeRate : Util.PlaybackSpeed.NormalPerMille);
    }

    /// <summary>
    /// Same probe, handing back the resolved codec so callers that log it (the
    /// resolve branch) do not pay a second media-streams DB read (JF-514 review).
    /// </summary>
    private bool RoutesToAudioTranscode(BaseItem? item, out string? audioCodec)
    {
        audioCodec = null;
        if (item is not (MediaBrowser.Controller.Entities.Movies.Movie
            or MediaBrowser.Controller.Entities.TV.Episode))
        {
            return false;
        }

        audioCodec = TryResolveAudioCodec(item);
        return VideoAppStreamPolicy.AudioRequiresTranscode(audioCodec);
    }

    /// <summary>
    /// JF-522 read side of the launch-scope store: the ACTIVE launch base for an item
    /// on a device (the base of the stream the device is actually playing), or null
    /// when none is recorded. Wrapper shape mirrors the RecordLastPlayed chokepoint
    /// idiom (null device reads null; manager falls back to <c>Plugin.Instance</c>'s).
    /// </summary>
    /// <param name="deviceId">The Alexa device ID, or null to read null.</param>
    /// <param name="itemId">The item ID in any GUID format.</param>
    /// <param name="queueManager">The caller's queue manager, or null to use <c>Plugin.Instance</c>'s.</param>
    /// <returns>The active launch base in milliseconds, or null when none is recorded.</returns>
    internal long? GetActiveLaunchBaseMs(string? deviceId, string itemId, DeviceQueueManager? queueManager = null)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return null;
        }

        return (queueManager ?? Plugin.Instance?.DeviceQueueManager)?.GetActiveLaunchBase(deviceId, itemId);
    }

    /// <summary>
    /// JF-636 rate half of the launch-scope read side: the ACTIVE playback rate
    /// for an item on a device (the rate of the stream whose device offsets the
    /// callers are about to interpret), normal-null when no scope is recorded.
    /// Wrapper shape mirrors <see cref="GetActiveLaunchBaseMs"/>; the two reads
    /// always describe the SAME launch scope.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID, or null to read null.</param>
    /// <param name="itemId">The item ID in any GUID format.</param>
    /// <param name="queueManager">The caller's queue manager, or null to use <c>Plugin.Instance</c>'s.</param>
    /// <returns>The active playback rate in per-mille form, or null when none is recorded.</returns>
    internal int? GetActivePlaybackRate(string? deviceId, string itemId, DeviceQueueManager? queueManager = null)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return null;
        }

        return (queueManager ?? Plugin.Instance?.DeviceQueueManager)?.GetActivePlaybackRate(deviceId, itemId);
    }

    /// <summary>
    /// JF-636 combined launch-scope read: base AND rate in one lock acquisition,
    /// for every caller that uses both (the event-side composition, the resume
    /// rebase). Two individual reads can straddle a concurrent
    /// <see cref="DeviceQueueManager.RecordLaunchBase"/> and pair base1 with
    /// rate2; this snapshot cannot.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID, or null to read nulls.</param>
    /// <param name="itemId">The item ID in any GUID format.</param>
    /// <param name="queueManager">The caller's queue manager, or null to use <c>Plugin.Instance</c>'s.</param>
    /// <returns>The active launch base in milliseconds and the active playback rate in per-mille form, null when not recorded.</returns>
    internal (long? BaseMs, int? RatePerMille) GetActiveLaunchScope(string? deviceId, string itemId, DeviceQueueManager? queueManager = null)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return (null, null);
        }

        return (queueManager ?? Plugin.Instance?.DeviceQueueManager)?.GetActiveLaunchScope(deviceId, itemId)
            ?? (null, null);
    }

    /// <summary>
    /// Handler-side probe of an item's first audio stream codec (JF-507). Fail-open:
    /// any failure (no statically injected MediaSourceManager under tests, DB read
    /// error) yields null so the launch keeps the raw static URL; the transcode
    /// endpoint re-probes server-side and picks its own ffmpeg arguments.
    /// </summary>
    /// <param name="item">The item to probe.</param>
    /// <returns>Lowercase audio codec, or null when unavailable.</returns>
    private string? TryResolveAudioCodec(BaseItem item)
    {
        try
        {
            return VideoAppStreamPolicy.ExtractCodecs(item.GetMediaStreams()).AudioCodec;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Audio launch codec probe: could not read media streams for item {ItemId}", item.Id);
            return null;
        }
    }

    /// <summary>
    /// Attach an APL NowPlaying screen directive to a response when the device supports APL.
    /// No-op on non-APL devices, when visuals are disabled, or when the response has no
    /// AudioPlayer directive (e.g. VideoApp path).
    /// Moved here from BaseHandler (JF-315 batch 4, closing the batch-3 deferral):
    /// unlike the list/carousel attachers in AplDirectiveAttacher, it resolves the
    /// cover-art URL via <see cref="GetImageUrl"/>, which reads plugin config (server
    /// address + user token), so it is not Logger-only state and belongs with the
    /// launch builder that owns the URL vocabulary.
    /// </summary>
    public void TryAttachNowPlayingDirective(
        SkillResponse response,
        MediaBrowser.Controller.Entities.BaseItem item,
        string itemId,
        Entities.User user,
        Context? context,
        long progressMs = 0,
        long durationMs = 0)
    {
        if (!Apl.AplHelper.VisualsEnabled || !Apl.AplHelper.DeviceSupportsApl(context))
        {
            return;
        }

        // Only attach when the response carries an AudioPlayer.Play directive.
        // VideoApp responses render their own UI and don't need APL.
        if (!response.Response.Directives.Any(d => d is AudioPlayerPlayDirective))
        {
            return;
        }

        AttachNowPlayingCore(response, item, itemId, user, context, progressMs, durationMs);
    }

    /// <summary>
    /// JF-635 (live 2026-09-25): attach the NowPlaying APL document to a Q&amp;A/writer
    /// Tell that answers DURING VideoApp seek-mode playback. Device evidence: a one-shot
    /// Tell with no visual directive dismisses the playing video surface (RateItem's
    /// plain Tell closed the album), while a Tell carrying a RenderDocument (MediaInfo)
    /// left the audio stream running. The doc is the screen-content keep-alive; the
    /// attach is medium-gated by the caller (only when a VideoApp-routed ledger entry
    /// says a video-audio stream owns the screen).
    /// </summary>
    public void AttachNowPlayingKeepAlive(
        SkillResponse response,
        MediaBrowser.Controller.Entities.BaseItem item,
        string itemId,
        Entities.User user,
        Context? context)
    {
        if (!Apl.AplHelper.VisualsEnabled || !Apl.AplHelper.DeviceSupportsApl(context))
        {
            return;
        }

        AttachNowPlayingCore(response, item, itemId, user, context, 0, 0);
    }

    private void AttachNowPlayingCore(
        SkillResponse response,
        MediaBrowser.Controller.Entities.BaseItem item,
        string itemId,
        Entities.User user,
        Context? context,
        long progressMs,
        long durationMs)
    {
        string imageUrl = GetImageUrl(itemId, user);
        var directive = Apl.AplHelper.BuildNowPlayingDirective(item, imageUrl, imageUrl, context, progressMs, durationMs);
        if (directive != null)
        {
            response.Response.Directives.Add(directive);
        }
        else
        {
            _logger.LogDebug("APL BuildNowPlayingDirective returned null for item '{ItemName}'", item.Name);
        }
    }
}

/// <summary>
/// The resolved stream source of an AUDIO-SHAPED launch (AudioPlayer.Play): the URL
/// plus the offset the directive must carry for it (JF-507). The triple is
/// unsplittable by design (JF-522): a caller holding a source cannot issue the
/// directive without its launch base reaching the launch-scope store - pass the
/// whole struct to the AudioLaunchSource overload of BuildAudioPlayerResponse.
/// </summary>
/// <param name="Url">The stream URL (raw static, the audio-only episode transcode, or the JF-636 atempo speed stream).</param>
/// <param name="OffsetMs">The offsetInMilliseconds the AudioPlayer.Play directive must carry with that URL (0 on the transcode route, where the seek lives in the URL).</param>
/// <param name="LaunchBaseMs">The item-absolute base of the resolved stream (the minted <c>?start=</c> on the transcode route, 0 on the raw-static route).</param>
/// <param name="RatePerMille">The stream's playback rate in per-mille form (JF-636: 1000 = identity; an atempo stream's device offsets scale by this at every playback event, so the launch-scope write the chokepoint performs carries it).</param>
public readonly record struct AudioLaunchSource(string Url, int OffsetMs, long LaunchBaseMs, int RatePerMille = 1000);
