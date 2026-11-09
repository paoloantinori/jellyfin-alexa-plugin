using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Lyrics;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// JF-926 (maintainer-approved option 3 of the JF-925 decision): the opt-in
/// burned-in lyrics view. When the user asks to SEE the lyrics of the current
/// song on a screen device, this handler relaunches that one song through the
/// video-audio pipeline with the synced lyrics burned into the video by libass
/// (the JF-925 spike: the production in-container jellyfin-ffmpeg renders the
/// .ass at 1-2s encode per track, sync verified on organic lrc). Default
/// playback changes for NOBODY: normal music stays on AudioPlayer, the video-
/// audio music path stays parked (issue #40), and this view is entered only by
/// the explicit ask. v1 semantics: the song restarts from the beginning as a
/// one-song video; when it ends playback stops (no queue chaining through the
/// video path). The user asks to play again normally afterwards.
/// Rationale for the route: the Show's native player COVERS any skill APL
/// document during AudioPlayer playback (JF-624, device-proven), so no
/// skill-drawn lyrics surface exists on the audio path; the video-audio
/// pipeline is the only surface whose pixels the skill owns.
/// </summary>
public class ShowLyricsIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILyricManager? _lyricManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShowLyricsIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="lyricManager">The server's lyric manager (synced lrc sidecars). DI-REQUIRED on the handler (both shipping hosts register ILyricManager, so construction always resolves); the endpoint independently answers its own 404 when the manager is absent server-side.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public ShowLyricsIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        ILyricManager? lyricManager,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _lyricManager = lyricManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.ShowLyricsIntent, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolve the current song, verify it has SYNCED lyrics, and launch the
    /// burned-in lyrics video. Tell answers: nothing playing (the shared
    /// NoMediaPlaying), no synced lyrics or non-song media (LyricsNoSynced),
    /// screenless device (the launch chokepoint's VideoRequiresScreen). Any
    /// lyric-fetch failure speaks the same tell with the real cause logged: the
    /// feature degrades to "no lyrics", never to an unhandled error.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">Cancellation token for request timeout.</param>
    /// <returns>A skill response with media information or an error message.</returns>
    public override async Task<SkillResponse> HandleAsync(
        Request request,
        Context context,
        Entities.User user,
        SessionInfo session,
        CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);

        // The ONE current-item resolver (the MediaInfo precedent), tail refused:
        // lyrics are for the song playing NOW, and a stale ledger answer would
        // relaunch an hours-old track.
        BaseItem? current = Launch.ResolveCurrentPlayingItem(
            context, session, _libraryManager, queueManager: null, "ShowLyrics", allowLedgerTailAnswers: false);
        if (current is not MediaBrowser.Controller.Entities.Audio.Audio song)
        {
            Logger.LogInformation("ShowLyricsIntent: no current song to show lyrics for (resolved: {ResolvedType})", current?.GetType().Name ?? "none");
            return ResponseBuilder.Tell(ResponseStrings.Get(current == null ? "NoMediaPlaying" : "LyricsNoSynced", locale));
        }

        // The lyrics fetch must tolerate a host without the lyric manager.
        if (_lyricManager == null)
        {
            Logger.LogInformation("ShowLyricsIntent: no ILyricManager on this host, cannot serve lyrics");
            return ResponseBuilder.Tell(ResponseStrings.Get("LyricsNoSynced", locale));
        }

        MediaBrowser.Model.Lyrics.LyricDto? lyrics;
        try
        {
            lyrics = await _lyricManager.GetLyricsAsync(song, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Any lyric-fetch failure degrades to the no-lyrics tell with the real
            // cause logged (transient, cancelled, or provider error alike): the
            // feature is opt-in polish, never worth failing the intent over.
            Logger.LogWarning(ex, "ShowLyricsIntent: lyric lookup failed for {SongName}", song.Name);
            return ResponseBuilder.Tell(ResponseStrings.Get("LyricsNoSynced", locale));
        }

        // The synced gate IS the endpoint's: LyricsAssBuilder.TryBuild is the ONE
        // definition of "renderable synced lyrics" (>=2 non-blank synced lines), so
        // gating on it here guarantees the launch URL's endpoint accepts what the
        // announce already promised (no hand-rolled near-copy to drift).
        var lyricLines = lyrics?.Lyrics;
        if (lyricLines is null
            || !Alexa.Playback.LyricsAssBuilder.TryBuild(lyricLines, out _))
        {
            Logger.LogInformation("ShowLyricsIntent: no SYNCED lyrics for {SongName} (lines: {LineCount})", song.Name, lyricLines?.Count ?? 0);
            return ResponseBuilder.Tell(ResponseStrings.Get("LyricsNoSynced", locale));
        }

        // The lyrics playlist URL: the endpoint (unit 3) serves the burned-in HLS
        // for THIS item; the signed token follows the family contract (JF-309).
        // The ass script rides the URL hash-side: the endpoint re-builds the same
        // deterministic script from the same source, so only the identity key
        // needs to travel here.
        string url = Launch.GetLyricsStreamUrl(song.Id.ToString(), user);

        Logger.LogInformation("ShowLyricsIntent: launching the lyrics video for {SongName}", song.Name);
        // JF-501: intent-driven launch sites prefer the async progressive-announce
        // variant (the announce rides the progressive vehicle so the Show player
        // cannot cut it mid-sentence when it opens).
        return await Launch.BuildVideoAppLaunchResponseAsync(
            context,
            request,
            locale,
            url,
            song.Name,
            new global::Alexa.NET.Response.PlainTextOutputSpeech(ResponseStrings.Get("ShowLyricsLaunching", locale, song.Name))).ConfigureAwait(false);
    }
}
