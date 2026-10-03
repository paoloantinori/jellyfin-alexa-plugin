using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// JF-690: the multi-value ER arbitration gate, the ONE definition shared by the
/// musician-slot paths. When Alexa's entity resolution returns MORE THAN ONE
/// distinct canonical value for a slot (the JF-684 shared-first-word shape: the
/// live "pink" returning [P!nk, Pink Floyd] because both catalog entries carry a
/// "pink"-matching synonym), the plain single-value read
/// (<see cref="SlotValueHelper.GetCanonicalValue(Slot)"/>) keeps only Amazon's
/// rank #1 and the exact-name hit auto-plays through the JF-420.1 equality
/// bypass with no prompt, silently. This gate resolves EVERY candidate against
/// the user's library by exact-name equality and, when TWO OR MORE distinct
/// library artists resolve, fires the existing JF-420.2 yes/no disambiguation
/// (<see cref="DisambiguationHelper.AskMultipleArtists"/>) with real library ids
/// (the confirm leg resolves ids via GetItemById, so raw ER names without ids
/// are dead on "yes"). Candidates that no longer exist in the library (stale
/// catalog) drop out before the prompt, and an ambiguity that collapses to ONE
/// library artist returns that artist instead of letting the stale rank-#1
/// canonical drive a not-found. The gate closes without the in-memory index
/// (the cold/database path keeps today's rank-#1 behavior, the same accepted
/// trade-off class as the JF-420 alternative pool and the JF-652 near-tie pool)
/// and on single-value slots (the ordinary shape, byte-identical to before).
/// JF-715: this class is also the home of the gate-consume composite the three
/// song+musician sites share (<see cref="TryArbitrateOrSearchAsync"/>), of its
/// constraint-slot probe (<see cref="IsGenericSongConstraint"/>, which OWNS the
/// raw-slot normalization), and of the generic-music-word vocabulary the probe
/// tests (moved from PlaySongIntentHandler so no handler reaches into another
/// handler's statics: the twins referenced PlaySongIntentHandler
/// .IsGenericMusicQuery cross-handler before the fold).
/// </summary>
internal static class MultiValueErDisambiguation
{
    // Alexa's NLU can misalign slot boundaries, causing carrier phrases like
    // "la canzone" to bleed into the song slot value. Strip them before probing.
    // JF-715: moved from PlaySongIntentHandler (which keeps consuming it for its
    // own search path) because the constraint-slot probe normalizes from the RAW
    // slot and the ONE definition must live with the probe, not in a handler the
    // probe would reference cross-handler.
    private static readonly string[] SongCarrierPhrases = new[]
    {
        // Phrases that appear right before {song} in utterance templates.
        // Within each locale group, longer phrases come first (e.g. "the song called " before "the song ").
        // English
        "the song called ", "a song called ",
        "that song ", "the song ", "the track ", "a song ", "a track ",
        // Italian
        "la canzone ", "il brano ", "il pezzo ", "la traccia ",
        "una canzone ", "un brano ", "un pezzo ", "una traccia ",
        "canzone ", "brano ", "pezzo ", "traccia ",
        // German
        "das lied ", "das stück ", "den titel ",
        "ein lied ", "ein stück ",
        // Spanish
        "la canción ", "el tema ", "una canción ", "canción ",
        // French
        "la chanson ", "le titre ", "le morceau ", "une chanson ", "chanson ",
        // Dutch
        "het liedje ", "het nummer ", "liedje ", "nummer ",
        // Portuguese
        "a música ", "a faixa ", "música ",
    };

    // Generic words meaning "music/songs" across supported locales.
    // When Alexa captures one of these as the {song} slot alongside a {musician} slot,
    // the user means "play music by <artist>" not "play a song titled 'music'".
    // JF-715: moved from PlaySongIntentHandler verbatim (the shared-home fold).
    internal static readonly HashSet<string> GenericMusicWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // English
        "music", "songs", "song", "track", "tracks", "tune", "tunes",
        // Italian
        "musica", "canzoni", "canzone", "brani", "brano", "pezzo", "traccia",
        // German
        "musik", "lieder", "lied", "titel", "song",
        // Spanish
        "música", "musica", "canciones", "canción", "cancion", "tema", "temas",
        // French
        "chansons", "chanson", "musique", "morceau", "titre", "titres",
        // Dutch
        "muziek", "liedjes", "liedje", "nummer", "nummers",
        // Portuguese
        "canções", "cancoes", "músicas", "musicas", "faixa", "faixas",
    };

    // JF-697: leading articles of every language the GenericMusicWords set covers
    // (en, it, de, es, fr, nl, pt). ASR delivers the carrier WITH its article
    // ("la musica di pink" -> song slot "la musica"), so the generic-word gate
    // must test the article-stripped form. Membership-test only: the probe never
    // feeds the song search, so a real song titled with an article ("La Vie En
    // Rose") still matches by title. Simple articles only by design: partitive
    // and contracted forms ("della musica", "de la musique") are a deliberate
    // scope cap, not yet observed in a captured slot.
    // Sibling tables, deliberately independent: ArtistSearch.ItalianLeadingArticles
    // (it-only, shapes the MUSICIAN search input), KeywordMatcher.StopWords
    // (per-locale tokenizer vocabulary; its any-locale union over-strips real
    // titles, and no existing helper covers the l'/un' elisions), and this
    // class's own SongCarrierPhrases (carrier+NOUN phrases with a trailing
    // space, which miss the no-trailing-space slot shape this table covers).
    // JF-715: moved from PlaySongIntentHandler verbatim (the shared-home fold).
    private static readonly string[] GenericMusicLeadingArticles = new[]
    {
        // Italian
        "il", "lo", "la", "i", "gli", "le", "un", "una",
        // English
        "the", "a", "an",
        // German
        "der", "die", "das", "den", "dem", "ein", "eine",
        // Spanish
        "el", "los", "las",
        // French
        "le", "les", "une",
        // Portuguese
        "o", "os", "as", "um", "uma",
        // Dutch
        "de", "het", "een",
    };

    /// <summary>
    /// JF-697 membership test for the generic-music-word fallback gate: true when
    /// the song query is a generic music word either bare or after stripping one
    /// leading article of the covered languages ("la musica" -> "musica").
    /// The song search itself keeps the raw slot value.
    /// JF-715: moved from PlaySongIntentHandler and made PRIVATE: every caller
    /// now goes through <see cref="IsGenericSongConstraint"/> (the ONE raw-slot
    /// probe), so the membership level and the normalization level cannot
    /// diverge (simplify round: PlaySong's generic-word bypass used to re-derive
    /// the probe on its own normalized local, a second hand-synced derivation).
    /// </summary>
    private static bool IsGenericMusicQuery(string songQuery)
    {
        // When no article was stripped the probe returns the input unchanged, so
        // this single Contains covers the bare-word case too.
        return GenericMusicWords.Contains(StripGenericMusicLeadingArticle(songQuery));
    }

    /// <summary>
    /// Strips ONE leading space-separated article from the query; returns the
    /// input unchanged when no article is present (null/empty included, matching
    /// the null-safe HashSet.Contains this probe replaced). Safe against real
    /// titles by construction: the caller only consumes the result as a
    /// GenericMusicWords membership probe.
    /// JF-715: moved from PlaySongIntentHandler verbatim (the shared-home fold);
    /// private with its consumer since the fold.
    /// </summary>
    private static string StripGenericMusicLeadingArticle(string songQuery)
    {
        if (string.IsNullOrEmpty(songQuery))
        {
            return songQuery;
        }

        string trimmed = songQuery.TrimStart();
        int space = trimmed.IndexOf(' ');
        if (space > 0)
        {
            string first = trimmed[..space];
            foreach (string article in GenericMusicLeadingArticles)
            {
                if (string.Equals(first, article, StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed[(space + 1)..].Trim();
                }
            }
        }

        return songQuery;
    }

    /// <summary>
    /// Strips a leading song carrier phrase that the NLU's statistical fill
    /// bled into the slot value (the ONE single-cut CarrierPhrase primitive,
    /// JF-610; returns the ORIGINAL query on no cut, the stripped remainder on
    /// a cut, preemptive policy).
    /// JF-715: moved from PlaySongIntentHandler (which keeps consuming it for
    /// its own search-path normalization); the constraint-slot probe owns it
    /// here so the three song+musician sites cannot diverge on carrier-bleed
    /// generic shapes.
    /// </summary>
    internal static string StripSongCarrierPhrase(string query)
    {
        string trimmed = query.TrimStart();
        return CarrierPhrase.TryStripLeading(ref trimmed, SongCarrierPhrases) ? trimmed : query;
    }

    /// <summary>
    /// JF-715: the ONE constraint-slot normalization for the song+musician gate
    /// sites: true when the RAW song slot value normalizes to a generic music
    /// word, via the full pipeline (carrier-phrase strip, katakana
    /// romanization, then the article-stripping membership probe). The three
    /// sites (PlaySong, AddToQueue, PlayNext) historically probed diverging
    /// values (PlaySong its carrier-stripped/romanized local, the twins the raw
    /// slot; no reachable divergence existed only because the table is
    /// Latin-only and the article probe covered both forms); the pipeline lives
    /// HERE so a future generic word whose stripped form differs cannot fork
    /// the probe per site. The pipeline is also idempotent on an
    /// already-normalized value (PlaySong's bypass probes its raw slot through
    /// it; the strip and romanize steps are identity on Latin), so every
    /// consumer reads the ONE derivation. Membership-test only: the song
    /// searches keep the values they always consumed.
    /// </summary>
    /// <param name="rawSongSlot">The RAW song slot value (pre-normalization).</param>
    /// <returns>True when the slot carries no song constraint worth preserving.</returns>
    internal static bool IsGenericSongConstraint(string? rawSongSlot)
        => !string.IsNullOrWhiteSpace(rawSongSlot)
            && IsGenericMusicQuery(KatakanaRomanizer.Romanize(StripSongCarrierPhrase(rawSongSlot)));

    /// <summary>
    /// The gate's outcome: the ask to return verbatim when the ambiguity is REAL
    /// (two or more distinct library artists), otherwise the SINGLE library
    /// artist the ER ambiguity collapsed to (the one real match among stale
    /// catalog candidates), or null on every closed-gate leg. <see cref="Pool"/>
    /// carries the pinned view's artist list whenever the gate fetched it, so a
    /// caller with its own pool cache can seed it instead of re-materializing.
    /// </summary>
    internal readonly record struct ErArtistArbitration(
        SkillResponse? Ask,
        BaseItem? ResolvedArtist,
        IReadOnlyList<BaseItem>? Pool);

    /// <summary>
    /// Arbitrate a multi-value ER match on the musician slot
    /// (<see cref="IntentNames.Slots.Musician"/>): return the yes/no multi-artist
    /// ask when the ER ambiguity is REAL in the user's library (at least two
    /// distinct library artists exactly named by the ER candidates); the ask's
    /// match order is the ER rank order, so "yes" keeps Amazon's rank-#1 ARTIST
    /// and "no" explores the alternative. When the list collapses to exactly ONE
    /// library artist (the other values are stale catalog entries), return that
    /// artist: it is proven library evidence and outranks a search driven by the
    /// stale rank-#1 canonical, which today answers not-found with the survivor
    /// sitting resolved. All closed-gate legs (single ER value, no ready index,
    /// zero resolves) return the default struct: the caller proceeds on its
    /// existing single-value path byte-identically.
    /// </summary>
    /// <param name="request">The intent request carrying the slot's entity resolution.</param>
    /// <param name="user">The plugin user (library scope resolution).</param>
    /// <param name="artistIndex">The handler's artist index: a live index or an
    /// already-pinned view (capture is idempotent, JF-448), so a caller that pinned
    /// for its own search chain hands the same view down; null or not ready closes the gate.</param>
    /// <param name="libraryManager">The library manager (scope resolution).</param>
    /// <param name="logger">The handler's logger (the gate logs every leg, JF-690's observability).</param>
    /// <param name="locale">The request locale (the ask's strings).</param>
    /// <param name="rawConstraintSlot">JF-715: the RAW song slot value. Non-null
    /// enables the constraint restriction the three song+musician sites share:
    /// the gate opens only when the slot normalizes to a generic music word
    /// (<see cref="IsGenericSongConstraint"/> owns the normalization), because
    /// with a REAL title in hand the confirm leg cannot preserve the requested
    /// song and the title keeps driving today's rank-#1 scoped search. Null
    /// (the default) runs the gate unrestricted (the no-constraint sites).</param>
    /// <returns>The arbitration outcome (see summary).</returns>
    internal static ErArtistArbitration TryArbitrate(
        IntentRequest request,
        Entities.User user,
        IArtistIndex? artistIndex,
        ILibraryManager libraryManager,
        ILogger logger,
        string locale,
        string? rawConstraintSlot = null)
    {
        if (rawConstraintSlot != null && !IsGenericSongConstraint(rawConstraintSlot))
        {
            return default;
        }

        IReadOnlyList<string> candidates = SlotValueHelper.GetCanonicalValues(request, IntentNames.Slots.Musician);
        if (candidates.Count <= 1)
        {
            return default;
        }

        IArtistIndex? pinned = artistIndex?.IsReady == true ? artistIndex.Pin() : null;
        if (pinned == null)
        {
            logger.LogDebug(
                "MultiValueEr: {Count} ER values on the musician slot but no ready artist index; Amazon's top rank arbitrates (JF-690 cold path)",
                candidates.Count);
            return default;
        }

        Guid[]? topParentIds = LibraryFilter.ResolveForUser(user, libraryManager, logger);
        IReadOnlyList<BaseItem> pool = pinned.GetArtists(topParentIds);
        List<BaseItem> resolved = ArtistSearch.ResolveExactNameMatches(candidates, pool);
        if (resolved.Count == 1)
        {
            logger.LogInformation(
                "MultiValueEr: {Count} ER values on the musician slot collapse to the single library artist '{Artist}'; playing it (JF-690)",
                candidates.Count, resolved[0].Name);
            return new ErArtistArbitration(null, resolved[0], pool);
        }

        if (resolved.Count < 2)
        {
            logger.LogDebug(
                "MultiValueEr: {Count} ER values on the musician slot resolve to no library artist; proceeding on the single-value path (JF-690)",
                candidates.Count);
            return new ErArtistArbitration(null, null, pool);
        }

        logger.LogInformation(
            "MultiValueEr: ER returned {Count} values on the musician slot naming {Resolved} library artists ({Names}); asking which to play (JF-690)",
            candidates.Count, resolved.Count, string.Join(", ", resolved.Select(a => a.Name)));
        var matches = resolved
            .Select(a => new DisambiguationHelper.MatchInfo { Id = a.Id.ToString(), Name = a.Name })
            .ToList();
        return new ErArtistArbitration(DisambiguationHelper.AskMultipleArtists(matches, locale), null, pool);
    }

    /// <summary>
    /// JF-715: the gate-consume composite's outcome for the three song+musician
    /// sites. <see cref="Terminal"/> is the response to return verbatim (the
    /// multi-artist ask, or the artist not-found when the fall-through search
    /// found nothing); a null Terminal always comes with at least one artist id
    /// in <see cref="ArtistIds"/> and the representative match's display name in
    /// <see cref="MatchedArtistName"/> (the collapse survivor, or the search's
    /// full result list whose first entry is the representative).
    /// </summary>
    internal readonly record struct SongMusicianGateResult(
        SkillResponse? Terminal,
        List<Guid> ArtistIds,
        string? MatchedArtistName);

    /// <summary>
    /// JF-715: the gate-consume composite the three song+musician sites share
    /// (PlaySong, AddToQueue, PlayNext; the ~30-line sequence the JF-702
    /// adoption took from one instance to three, differing only in the retry
    /// label that rides <paramref name="retryLabel"/>). One shape: probe the
    /// constraint slot (the RAW song value; the normalization lives here, see
    /// <see cref="IsGenericSongConstraint"/>), arbitrate the multi-value ER,
    /// return the ask when the ambiguity is real, adopt the collapse survivor,
    /// and otherwise run the fall-through artist search SEEDED WITH THE GATE'S
    /// POOL (the JF-715 SearchAsync threading: the zero-resolve leg's scoped
    /// pool is consumed instead of re-materialized), speaking the artist
    /// not-found when the search finds nothing. The handler keeps only its
    /// adopt-vs-not-found tail (PlaySong's generic-word artist-play bypass, the
    /// twins' queue operations).
    /// <para>
    /// The composite pins the index ONCE at entry and hands the pinned view to
    /// both the gate and the search, so the gate's pool and the chain's phonetic
    /// codes resolve from ONE publish whatever the caller passes (a live index
    /// or an already-pinned view; Pin is idempotent, JF-448).
    /// </para>
    /// </summary>
    /// <param name="request">The intent request carrying the musician slot's ER.</param>
    /// <param name="user">The plugin user (thresholds, library scope).</param>
    /// <param name="artistIndex">The handler's artist index (live or pinned; the composite pins).</param>
    /// <param name="libraryManager">The library manager (scope resolution; also the retry channel's query target).</param>
    /// <param name="logger">The handler's logger.</param>
    /// <param name="locale">The request locale.</param>
    /// <param name="rawSongSlot">The RAW song slot value (the constraint probe normalizes it here).</param>
    /// <param name="searchMusician">The artist search query (the ER canonical when the slot resolved, the raw value otherwise; the JF-659 contract stays at the caller's slot read).</param>
    /// <param name="musicianSpeechValue">The musician value in the user's own words (the not-found speech; PlaySong passes its article-stripped local, the twins the raw slot).</param>
    /// <param name="retryLabel">The site's retry label for the fall-back database channel (the one axis the three sites differed on; the retry budget is the shared Alexa request budget every handler's RetryAsync passes).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The gate outcome (see <see cref="SongMusicianGateResult"/>).</returns>
    internal static async Task<SongMusicianGateResult> TryArbitrateOrSearchAsync(
        IntentRequest request,
        Entities.User user,
        IArtistIndex? artistIndex,
        ILibraryManager libraryManager,
        ILogger logger,
        string locale,
        string? rawSongSlot,
        string searchMusician,
        string musicianSpeechValue,
        string retryLabel,
        CancellationToken cancellationToken)
    {
        // The structural one-publish guarantee (simplify round): pin here, not
        // at the callers, so a caller that passes the live index cannot split
        // the gate's pool from the search's phonetic codes on a mid-request
        // refresh. The UNGUARDED Pin shape (the PlayAlbum precedent, code-review
        // round): a warming index pins to a NOT-READY view, so the search's
        // JF-419.2 EnsureReady choke point still throws the warming Tell (a
        // null-on-not-ready conversion would silently route ungated callers to
        // the cold database chain), while the gate below closes on a not-ready
        // view exactly as on null.
        IArtistIndex? pinnedArtistIndex = artistIndex?.Pin();

        ErArtistArbitration arbitration = TryArbitrate(
            request, user, pinnedArtistIndex, libraryManager, logger, locale, rawSongSlot);
        if (arbitration.Ask != null)
        {
            return new SongMusicianGateResult(arbitration.Ask, new List<Guid>(), null);
        }

        if (arbitration.ResolvedArtist is { } survivor)
        {
            return new SongMusicianGateResult(null, new List<Guid> { survivor.Id }, survivor.Name);
        }

        logger.LogDebug("MultiValueEr: gate closed ({Label}), searching for artist filter='{Musician}'", retryLabel, searchMusician);
        IReadOnlyList<BaseItem> artists = await ArtistSearch.SearchAsync(
            searchMusician, user, libraryManager, pinnedArtistIndex, logger,
            (q, ct) => RetryHelper.ExecuteWithRequestBudgetAsync(
                () => libraryManager.GetItemList(q), logger, retryLabel, cancellationToken: ct),
            locale, cancellationToken,
            preloadedPool: arbitration.Pool).ConfigureAwait(false);

        logger.LogDebug("MultiValueEr: ({Label}) artist search returned {Count} results for '{Musician}'", retryLabel, artists.Count, searchMusician);

        if (artists.Count == 0)
        {
            logger.LogDebug("MultiValueEr: artist search returned no result for '{Musician}'", searchMusician);
            return new SongMusicianGateResult(
                ResponseBuilder.Tell(ResponseStrings.Get("NotFoundSongByArtist", locale, musicianSpeechValue)),
                new List<Guid>(),
                null);
        }

        var artistIds = new List<Guid>(artists.Count);
        foreach (BaseItem artist in artists)
        {
            artistIds.Add(artist.Id);
        }

        return new SongMusicianGateResult(null, artistIds, artists[0].Name);
    }
}
