using System;
using System.Collections.Generic;
using System.Linq;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// The JF-315 batch-4 playback-launch collaborator (extracted from BaseHandler,
/// 2026-09-15): everything that builds an AUDIO-SHAPED launch for an item - the
/// stream/image URL vocabulary derived from plugin config, the
/// <see cref="AudioLaunchSource"/> resolution family (static-vs-transcode routing,
/// resume rebasing, the launch-scope reads), the AudioPlayer.Play response
/// chokepoint (with its device last-played ledger and launch-scope records,
/// metadata, seek card, and the gated music announce), the VideoApp-for-audio
/// response (native controls) including its screenless degradation back to
/// AudioPlayer, and the APL now-playing attacher that rides play responses.
/// STATELESS by construction (readonly config + logger only), so the
/// singleton-handlers constraint BaseHandler documents is preserved.
/// COMPOSITION DECISION (the census's ctor-injected design, adapted so the 61
/// handlers keep compiling without ctor churn): BaseHandler constructs one
/// instance per handler in its own ctor and exposes it as the protected-internal
/// readonly <c>Launch</c> property; handlers consume it through that inherited
/// get-only property. The per-call <c>queueManager ?? Plugin.Instance?.DeviceQueueManager</c>
/// idiom is kept verbatim (the queue manager is deliberately parameterized per
/// call so unit tests stay off the shared plugin instance, JF-522).
/// The VideoApp LAUNCH family (BuildVideoAppLaunchResponse(Async), the channel
/// launch, the medium classification, the progressive announce) stays in
/// BaseHandler for the next batch: those members route through the virtual
/// SendProgressiveResponse seam that handler test harnesses override.
/// </summary>
public sealed class PlaybackLaunchBuilder
{
    private readonly PluginConfiguration _config;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackLaunchBuilder"/> class.
    /// </summary>
    /// <param name="config">The plugin configuration (server address, token secret, feature toggles).</param>
    /// <param name="logger">The logger (BaseHandler passes its own instance so moved log statements keep their pre-extraction category).</param>
    public PlaybackLaunchBuilder(PluginConfiguration config, ILogger logger)
    {
        _config = config;
        _logger = logger;
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
        => new Uri(new Uri(_config.ServerAddress), $"alexaskill/api/video-audio/audiobook/{parentId}/stream.m3u8?token={StreamTokenHelper.Mint(parentId, _config.StreamTokenSecret)}").ToString();

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
    /// NOT the video/book GetAnnounceNowPlaying toggle that stays in BaseHandler with the VideoApp
    /// launch family. When offsetMs &gt; 0 the announce is a
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
    /// precedence. Video/book launches use the GetAnnounceNowPlaying resolver in BaseHandler instead.
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
    /// <returns>A SkillResponse containing the AudioPlayer directive.</returns>
    public SkillResponse BuildAudioPlayerResponse(PlayBehavior playBehavior, string streamUrl, string itemId, MediaBrowser.Controller.Entities.BaseItem? item, Entities.User user, Context? context, int offsetInMilliseconds = 0, string? announceLocale = null, DeviceQueueManager? queueManager = null, long launchBaseMs = 0)
    {
        // Record the last user-initiated play for this device (ReplaceAll = a new item starts).
        // This is the universal chokepoint: every play path flows through here, including APL
        // carousel taps and resume confirmations that bypass SetQueue. Captures VideoApp.Launch
        // plays too (which don't update context.AudioPlayer.Token), giving LaunchRequestHandler
        // a reliable device-specific "what did this Echo last play" signal.
        string? deviceId = context?.System?.Device?.DeviceID;
        if (playBehavior == PlayBehavior.ReplaceAll && !string.IsNullOrEmpty(deviceId))
        {
            (queueManager ?? Plugin.Instance?.DeviceQueueManager)?.RecordLastPlayed(deviceId, itemId);
        }

        // Route initial playback through VideoApp when native controls are enabled for the
        // item's category. Enqueue/ReplaceEnqueued stay as AudioPlayer for queue building.
        // Resume (offset > 0) also stays as AudioPlayer since VideoApp has no offset support
        // (audiobook resume is handled separately via a resume-aware HLS playlist).
        // AudioBook items use a special concat HLS endpoint that joins all chapters into
        // one continuous stream, giving the full book duration in the seek bar.
        if (playBehavior == PlayBehavior.ReplaceAll && offsetInMilliseconds == 0)
        {
            bool wantsNativeControls = false;
            if (item != null)
            {
                if (item.GetType().Name.Equals("AudioBook", StringComparison.Ordinal))
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
                return BuildVideoAppAudioResponse(itemId, item, user, announceLocale, context);
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
                playBehavior is PlayBehavior.Enqueue or PlayBehavior.ReplaceEnqueued);
        }

        _logger.LogDebug("BuildAudioPlayerResponse: itemId={ItemId}, behavior={Behavior}, offsetMs={OffsetMs}, title={Title}, streamUrl={StreamUrl}",
            itemId, playBehavior, offsetInMilliseconds, item?.Name, RequestLogRedactor.RedactUrl(streamUrl));
        string imageUrl = item != null ? GetImageUrl(itemId, user) : string.Empty;
        var imageSources = new AudioItemSources
        {
            Sources = new List<AudioItemSource> { new() { Url = imageUrl } }
        };

        var stream = new AudioItemStream
        {
            Url = streamUrl,
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
        DeviceQueueManager? queueManager = null)
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
            source.LaunchBaseMs);

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
    public SkillResponse BuildVideoAppAudioResponse(string itemId, BaseItem? item, Entities.User user, string? announceLocale = null, Context? context = null)
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
        // from the ledger the launch resume-offer reads. Idempotent where the chokepoint
        // delegation also records (RecordLastPlayed short-circuits an unchanged item);
        // the screenless degrade above records through the chokepoint as before.
        string? ledgerDeviceId = context?.System?.Device?.DeviceID;
        if (!string.IsNullOrEmpty(ledgerDeviceId))
        {
            (Plugin.Instance?.DeviceQueueManager)?.RecordLastPlayed(ledgerDeviceId, itemId);
        }

        bool isAudioBook = item != null && item.GetType().Name.Equals("AudioBook", StringComparison.Ordinal);

        string videoAudioUrl;
        if (isAudioBook && item!.ParentId != Guid.Empty)
        {
            // Multi-chapter audiobook: use concat HLS endpoint keyed by parent book ID.
            // The endpoint concatenates all chapters into one continuous HLS stream,
            // giving the full book duration in the Echo Show seek bar.
            videoAudioUrl = GetAudiobookVideoAudioUrl(item.ParentId.ToString());
            _logger.LogDebug("BuildVideoAppAudioResponse: itemId={ItemId}, parentId={ParentId}, title={Title}, url={Url} (audiobook concat)", itemId, item.ParentId, item.Name, videoAudioUrl);
        }
        else
        {
            videoAudioUrl = GetVideoAudioUrl(itemId);
            _logger.LogDebug("BuildVideoAppAudioResponse: itemId={ItemId}, title={Title}, url={Url}", itemId, item?.Name, videoAudioUrl);
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
    /// <returns>The launch source for the AudioPlayer.Play directive (URL + directive offset + launch base).</returns>
    public AudioLaunchSource ResolveAudioLaunchSource(BaseItem? item, string itemId, Entities.User user, int offsetMs, string? knownAudioCodec = null)
    {
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
        if (offsetIsStreamRelative && offsetMs > 0 && RoutesToAudioTranscode(item, out probedCodec))
        {
            long? transcodeBaseMs = GetActiveLaunchBaseMs(deviceId, itemId, queueManager);
            if (transcodeBaseMs.HasValue)
            {
                long composedMs = Math.Min(transcodeBaseMs.Value + offsetMs, int.MaxValue);
                // JF-521 hard clamp: a legitimate composition can never reach the item's
                // runtime (the stream-relative offset counts at most the REMAINING
                // runtime past the base), so a composed ?start= at or beyond it means a
                // stale base or a foreign position is in play (the JF-520 review's F1/F2
                // shapes). Never mint it; the caller's raw offset is the more
                // conservative truth. Also bounds the F2 retry walk (a resolve whose
                // directive never played advances the launch scope while the offset
                // source stays frozen): once a walk's composition reaches the runtime it
                // clamps and stops growing.
                long? runtimeTicks = item?.RunTimeTicks;
                if (runtimeTicks is > 0 && composedMs * TimeSpan.TicksPerMillisecond >= runtimeTicks.Value)
                {
                    // effectiveOffsetMs keeps the initializer's raw offsetMs: the raw
                    // offset is the conservative truth when the composition is stale.
                    _logger.LogInformation(
                        "{Label}: composed ?start= of {ComposedMs}ms (launch base {BaseMs}ms + stream-relative offset {OffsetMs}ms) for item {ItemId} reaches or exceeds the item runtime ({RuntimeMs}ms); a legitimate composition cannot, so a stale base or foreign position is in play; clamping to the raw {OffsetMs}ms offset as the more conservative truth",
                        logLabel, composedMs, transcodeBaseMs.Value, offsetMs, itemId, runtimeTicks.Value / TimeSpan.TicksPerMillisecond, offsetMs);
                }
                else
                {
                    effectiveOffsetMs = (int)composedMs;
                    _logger.LogInformation(
                        "{Label}: item {ItemId} routes to the audio-only transcode and the resume offset ({OffsetMs}ms) is device-derived (stream-relative); recorded launch base {BaseMs}ms, minting ?start={StartMs}ms (item-absolute)",
                        logLabel, itemId, offsetMs, transcodeBaseMs.Value, effectiveOffsetMs);
                }
            }
            else
            {
                effectiveOffsetMs = 0;
                _logger.LogInformation(
                    "{Label}: item {ItemId} routes to the audio-only transcode but the resume offset ({OffsetMs}ms) is device-derived (stream-relative) and no launch base is recorded for device {DeviceId}; dropping it so playback restarts instead of minting a false ?start=",
                    logLabel, itemId, offsetMs, deviceId);
            }
        }

        // probedCodec (null on the fail-open path, where the resolve re-probes) saves
        // the resolve's second media-streams DB read (JF-520 simplify finding E1).
        return ResolveAudioLaunchSource(item, itemId, user, effectiveOffsetMs, probedCodec);
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
        Context? context)
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

        string imageUrl = GetImageUrl(itemId, user);
        var directive = Apl.AplHelper.BuildNowPlayingDirective(item, imageUrl, imageUrl, context);
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
/// <param name="Url">The stream URL (raw static or the audio-only episode transcode).</param>
/// <param name="OffsetMs">The offsetInMilliseconds the AudioPlayer.Play directive must carry with that URL (0 on the transcode route, where the seek lives in the URL).</param>
/// <param name="LaunchBaseMs">The item-absolute base of the resolved stream (the minted <c>?start=</c> on the transcode route, 0 on the raw-static route).</param>
public readonly record struct AudioLaunchSource(string Url, int OffsetMs, long LaunchBaseMs);
