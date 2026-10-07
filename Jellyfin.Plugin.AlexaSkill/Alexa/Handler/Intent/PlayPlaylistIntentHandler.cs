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
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlayPlaylist intents.
/// </summary>
public class PlayPlaylistIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly DeviceQueueManager? _queueManager;
    private readonly IArtistIndex? _artistIndex;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayPlaylistIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="queueManager">Optional per-device queue manager for crash recovery.</param>
    /// <param name="artistIndex">Optional in-memory artist index (JF-808: the warming-gate stand-in; playlists have no index of their own, see the gate comment in <see cref="HandleAsync"/>).</param>
    public PlayPlaylistIntentHandler(
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
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.PlayPlaylist, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Play a playlist by its name. Delegates the shared playlist-play flow to
    /// <see cref="AlbumPlayService.BuildPlaylistPlayResponseAsync"/> (shuffle disabled).
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Play directive of the playlist.</returns>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;
        (_, string? playlistName) = ReadPlaylistSlot(intentRequest, IntentNames.Slots.Playlist, locale);
        // JF-550 (dead-mic sweep; JF-549 class): the empty-playlist prompt elicits
        // with the mic open (this caller's intent; the shared builder serves both
        // PlayPlaylistIntent and ShufflePlayIntent, so the elicit must name the
        // invoking one), and a captured cancel word ends the flow.
        if (BuildCancelDuringOpenElicit(intentRequest, locale, "PlayPlaylist") is { } elicitCancel)
        {
            return Task.FromResult(elicitCancel);
        }

        if (string.IsNullOrWhiteSpace(playlistName))
        {
            return Task.FromResult(BuildDialogElicitResponse("DidNotCatchPlaylistName", locale, "playlist", IntentNames.PlayPlaylist, Util.ElicitSlots.For(IntentNames.PlayPlaylist)));
        }

        // JF-808 Layer-1 gate: playlists have no in-memory index of their own
        // (neither the artist nor the song n-gram index serves Playlist items),
        // so this is the coarse artist-index stand-in for the shared cold
        // database (the PlayAlbum precedent, joined by books in JF-807), on the
        // SAME index the YesIntent playlist confirm arm gates so ask and confirm
        // answer identically in the warming window. The shared builder's cold
        // surface behind this gate: the SearchTerm playlist query on its
        // RetryAsync channel, the fuzzy fallback, and the GetManageableItems
        // whole-track resolution. Placement: AFTER the empty-slot elicit and the
        // cancel-word hatch (both must survive the warming window), BEFORE the
        // first cold query. This path has NO feature-flag gate (playlists are
        // cross-type always-allowed, the JF-806 decision), so the flag-gate
        // position discriminator does not apply; the path also sends no pre-query
        // "searching" announcement, so the gate-before-announcement contract
        // holds trivially.
        GuardIndexReady(_artistIndex);

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
            shuffle: false, rng: null, kanaOrigin, cancellationToken);
    }
}