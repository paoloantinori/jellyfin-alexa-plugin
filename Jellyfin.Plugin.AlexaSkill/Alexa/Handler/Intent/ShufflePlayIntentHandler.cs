using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for ShufflePlayIntent: start a playlist or album already shuffled (first
/// track random). The playlist slot shares the playlist-play flow with
/// <see cref="PlayPlaylistIntentHandler"/> via
/// <see cref="AlbumPlayService.BuildPlaylistPlayResponseAsync"/> (shuffle enabled); the
/// album slot (JF-974, the en-US 'shuffle the album {album}' row) routes to
/// <see cref="AlbumPlayService.BuildAlbumShufflePlayResponseAsync"/>, the album sibling
/// of that flow's shuffle arm.
/// </summary>
public class ShufflePlayIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly DeviceQueueManager? _queueManager;
    private readonly IArtistIndex? _artistIndex;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShufflePlayIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="queueManager">Optional per-device queue manager for crash recovery.</param>
    /// <param name="artistIndex">Optional in-memory artist index (JF-808: the warming-gate stand-in; playlists have no index of their own, see the gate comment in <see cref="HandleAsync"/>).</param>
    public ShufflePlayIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILoggerFactory loggerFactory,
        DeviceQueueManager? queueManager = null,
        IArtistIndex? artistIndex = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _queueManager = queueManager;
        _artistIndex = artistIndex;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.ShufflePlay, StringComparison.Ordinal);
    }

    /// <summary>
    /// Play a playlist or album by its name, shuffled. The album slot (JF-974) delegates
    /// to <see cref="AlbumPlayService.BuildAlbumShufflePlayResponseAsync"/>; the playlist
    /// slot delegates to the shared playlist-play flow
    /// (<see cref="AlbumPlayService.BuildPlaylistPlayResponseAsync"/> with shuffle
    /// enabled).
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Play directive of the shuffled playlist or album.</returns>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;
        (_, string? playlistName) = ReadPlaylistSlot(intentRequest, IntentNames.Slots.Playlist, locale);
        string? albumName = GetSlotValue(intentRequest, IntentNames.Slots.Album);
        // JF-550 (dead-mic sweep; JF-549 class): the empty-playlist prompt elicits
        // with the mic open (this caller's intent; the shared builder serves both
        // PlayPlaylistIntent and ShufflePlayIntent, so the elicit must name the
        // invoking one), and a captured cancel word ends the flow. The BOTH-empty
        // elicit stays the PLAYLIST one (the task card's ROUTING contract): the
        // model dialog and the cross-locale ElicitSlots table DO carry the album
        // slot since this change (slot parity is forced cross-locale), but the
        // album WORDING is en-US-only (JF-963), so an album-slot elicit would
        // prompt for a slot the other 16 locales never route; the playlist
        // prompt is the one every locale's ShufflePlayIntent can answer.
        if (BuildCancelDuringOpenElicit(intentRequest, locale, "ShufflePlay") is { } elicitCancel)
        {
            return Task.FromResult(elicitCancel);
        }

        if (string.IsNullOrWhiteSpace(playlistName) && string.IsNullOrWhiteSpace(albumName))
        {
            return Task.FromResult(BuildDialogElicitResponse("DidNotCatchPlaylistName", locale, "playlist", IntentNames.ShufflePlay, Util.ElicitSlots.For(IntentNames.ShufflePlay)));
        }

        // JF-808 Layer-1 gate, the PlayPlaylistIntentHandler twin verbatim: this
        // handler shares the playlist builder and its whole cold surface (only
        // the shuffle flag differs), so it shares the gate and the placement
        // (after the empty-slot elicit and the cancel hatch, before the first
        // cold query; see that handler's comment for the index-choice record).
        // The JF-974 album branch rides the same gate: its album SearchTerm
        // query and fuzzy fallback hit the same cold database.
        GuardIndexReady(_artistIndex);

        // JF-974: the album branch. The music gate is the PlayAlbum contract
        // (IfMediaTypeDisabled, AFTER the empty-slot prompt and BEFORE the first
        // query, the CLAUDE.md placement rule; this branch is a music-only path,
        // while the playlist branch below stays cross-type ungated per JF-806).
        if (!string.IsNullOrWhiteSpace(albumName))
        {
            SkillResponse? musicDisabled = IfMediaTypeDisabled(c => c.MusicEnabled, request);
            if (musicDisabled != null)
            {
                return Task.FromResult(musicDisabled);
            }

            // JF-661: the kana-origin flag on the PRE-romanization album slot
            // value, the playlist branch's pinning convention (the builder
            // romanizes; the flag must survive it).
            bool albumKanaOrigin = Util.ArtistSearch.IsKanaOriginQuery(null, albumName);
            return AlbumPlay.BuildAlbumShufflePlayResponseAsync(
                _libraryManager, _userManager, _queueManager,
                albumName, context, user, session, locale,
                rng: null, albumKanaOrigin, cancellationToken);
        }

        // JF-663: the kana-origin flag is captured on the post-strip,
        // pre-romanization name, the string the builder matches: kana in the
        // NAME is the lossy-transliteration evidence the bar keys on, while
        // kana living only in a stripped ja carrier (という) is not. Pinned per
        // the family convention so a future upstream romanization cannot
        // silently defuse the bar (the JF-660 lesson).
        bool kanaOrigin = Util.ArtistSearch.IsKanaOriginQuery(null, playlistName);
        return AlbumPlay.BuildPlaylistPlayResponseAsync(
            _libraryManager, _userManager, _queueManager,
            playlistName ?? string.Empty, context, user, session, locale,
            shuffle: true, rng: null, kanaOrigin, cancellationToken);
    }
}
