using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Apl;
using Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Newtonsoft.Json;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Helper for search disambiguation with Yes/No dialogue.
/// Stores match state in Alexa session attributes.
/// </summary>
internal static class DisambiguationHelper
{
    internal const string AttrMatches = "disambig_matches";
    internal const string AttrIndex = "disambig_index";
    internal const string AttrType = "disambig_type";

    /// <summary>
    /// JF-363 cross-media decline keys: carry the original not-found request so
    /// NoIntentHandler can decline to the right "song/album not found" instead of
    /// the generic "no more matches" (single definition: writer BaseHandler,
    /// reader NoIntentHandler, flow-registry ConversationalFlows).
    /// </summary>
    internal const string AttrCrossmediaQuery = "crossmedia_notfound_query";
    internal const string AttrCrossmediaType = "crossmedia_notfound_type";

    public const string MediaTypeSong = "song";
    public const string MediaTypeAlbum = "album";
    public const string MediaTypeArtist = "artist";
    public const string MediaTypeVideo = "video";
    public const string MediaTypePlaylist = "playlist";
    public const string MediaTypePodcast = "podcast";

    /// <summary>
    /// Check if session attributes contain active disambiguation state.
    /// </summary>
    /// <param name="sessionAttributes">The session attributes dictionary.</param>
    /// <returns>True if active disambiguation state is present.</returns>
    public static bool HasDisambiguationState(Dictionary<string, object>? sessionAttributes)
    {
        return sessionAttributes != null
            && sessionAttributes.ContainsKey(AttrMatches)
            && sessionAttributes.ContainsKey(AttrType);
    }

    /// <summary>
    /// Build a disambiguation Ask response for the first match.
    /// JF-735 verdict: the STATE list is truncated at
    /// <see cref="FirstMatchStateCap"/> on purpose (no overflow hint, unlike
    /// AskMultipleArtists); the defense and the enforced caller census live on
    /// that constant. Do not widen the state or add a hint here without
    /// re-weighing there.
    /// </summary>
    /// <param name="matches">The list of candidate matches.</param>
    /// <param name="mediaType">The media type being disambiguated.</param>
    /// <param name="locale">The locale for localized responses.</param>
    /// <returns>A disambiguation Ask response.</returns>
    public static SkillResponse AskFirstMatch(
        List<(Guid Id, string Name)> matches,
        string mediaType,
        string locale)
    {
        var matchList = matches.Take(FirstMatchStateCap).Select(m => new MatchInfo { Id = m.Id.ToString(), Name = m.Name }).ToList();
        int index = 0;

        SkillResponse response = SpeechBuilder.AskLocalized(
            "DisambiguatePromptSsml", "DisambiguatePrompt", "DisambiguateReprompt", locale, matchList[index].Name);

        response.SessionAttributes = BuildAttributes(matchList, index, mediaType);

        // JF-398: activating the disambiguation flow supersedes any other flow's state.
        // JF-690 review: mark ONLY the keys this builder writes, so the
        // cross-media OFFER flavor's decline keys (same disambig_* family, set by
        // the JF-363 offer ask) are superseded with the rest of the other flows:
        // a stale crossmedia_notfound_* surviving a plain artist ask made "no"
        // answer the OLD song/album not-found instead of cycling.
        ConversationalFlows.MarkOthersInactive(
            response, AttrMatches, AttrIndex, AttrType);
        return response;
    }

    /// <summary>
    /// Build a disambiguation Ask response for the first match, with optional APL carousel.
    /// JF-735 verdict: the STATE list is truncated at
    /// <see cref="FirstMatchStateCap"/> on purpose (no overflow hint, unlike
    /// AskMultipleArtists); the defense and the enforced caller census live on
    /// that constant. Do not widen the state or add a hint here without
    /// re-weighing there.
    /// </summary>
    /// <param name="matches">The list of candidate matches with optional art URLs.</param>
    /// <param name="mediaType">The media type being disambiguated.</param>
    /// <param name="locale">The locale for localized responses.</param>
    /// <param name="context">The Alexa request context for APL capability detection, or null.</param>
    /// <returns>A disambiguation Ask response, with carousel directive if APL is supported.</returns>
    public static SkillResponse AskFirstMatch(
        List<(Guid Id, string Name, string? ArtUrl)> matches,
        string mediaType,
        string locale,
        Context? context = null)
    {
        var matchList = matches.Take(FirstMatchStateCap).Select(m => new MatchInfo { Id = m.Id.ToString(), Name = m.Name, ArtUrl = m.ArtUrl }).ToList();
        int index = 0;

        SkillResponse response = SpeechBuilder.AskLocalized(
            "DisambiguatePromptSsml", "DisambiguatePrompt", "DisambiguateReprompt", locale, matchList[index].Name);

        response.SessionAttributes = BuildAttributes(matchList, index, mediaType);

        // JF-398: activating the disambiguation flow supersedes any other flow's state.
        // JF-690 review: mark ONLY the keys this builder writes, so the
        // cross-media OFFER flavor's decline keys (same disambig_* family, set by
        // the JF-363 offer ask) are superseded with the rest of the other flows:
        // a stale crossmedia_notfound_* surviving a plain artist ask made "no"
        // answer the OLD song/album not-found instead of cycling.
        ConversationalFlows.MarkOthersInactive(
            response, AttrMatches, AttrIndex, AttrType);

        if (context != null && AplHelper.DeviceSupportsApl(context) && AplHelper.VisualsEnabled)
        {
            var carouselItems = matchList
                .Select(m => new ListDisplayItem(m.Name, m.Id, null, m.ArtUrl))
                .ToList();

            var directive = AplHelper.BuildCarouselDirective(
                ResponseStrings.Get("DisambiguateCarouselTitle", locale),
                carouselItems,
                "disambiguation",
                context);

            if (directive != null)
            {
                response.Response.Directives.Add(directive);
            }
        }

        return response;
    }

    /// <summary>
    /// Build a disambiguation Ask response for the next match (after No).
    /// </summary>
    /// <param name="matches">The list of candidate matches.</param>
    /// <param name="nextIndex">The index of the next match to present.</param>
    /// <param name="mediaType">The media type being disambiguated.</param>
    /// <param name="locale">The locale for localized responses.</param>
    /// <returns>A disambiguation Ask response.</returns>
    public static SkillResponse AskNextMatch(
        List<MatchInfo> matches,
        int nextIndex,
        string mediaType,
        string locale)
    {
        SkillResponse response = SpeechBuilder.AskLocalized(
            "DisambiguateNextSsml", "DisambiguateNext", "DisambiguateReprompt", locale, matches[nextIndex].Name);

        response.SessionAttributes = BuildAttributes(matches, nextIndex, mediaType);

        // JF-398: activating the disambiguation flow supersedes any other flow's state.
        // JF-690 review: mark ONLY the keys this builder writes, so the
        // cross-media OFFER flavor's decline keys (same disambig_* family, set by
        // the JF-363 offer ask) are superseded with the rest of the other flows:
        // a stale crossmedia_notfound_* surviving a plain artist ask made "no"
        // answer the OLD song/album not-found instead of cycling.
        ConversationalFlows.MarkOthersInactive(
            response, AttrMatches, AttrIndex, AttrType);
        return response;
    }

    /// <summary>
    /// Build a "no more matches" Tell response.
    /// </summary>
    /// <param name="locale">The locale for localized responses.</param>
    /// <returns>A Tell response indicating no more matches.</returns>
    public static SkillResponse NoMoreMatches(string locale)
    {
        return ResponseBuilder.Tell(ResponseStrings.Get("NoMoreMatches", locale));
    }

    /// <summary>
    /// Read disambiguation state from session attributes.
    /// </summary>
    /// <param name="sessionAttributes">The session attributes dictionary.</param>
    /// <returns>The disambiguation state tuple, or null if not present.</returns>
    public static (List<MatchInfo> Matches, int Index, string MediaType)? ReadState(Dictionary<string, object>? sessionAttributes)
    {
        if (!HasDisambiguationState(sessionAttributes))
        {
            return null;
        }

        string matchesJson = sessionAttributes![AttrMatches]?.ToString() ?? "[]";
        var matches = JsonConvert.DeserializeObject<List<MatchInfo>>(matchesJson) ?? new List<MatchInfo>();
        int index = Convert.ToInt32(sessionAttributes[AttrIndex], CultureInfo.InvariantCulture);
        string mediaType = sessionAttributes[AttrType]?.ToString() ?? string.Empty;

        return (matches, index, mediaType);
    }

    /// <summary>
    /// Build the disambiguation session-attribute dictionary: the serialized match list,
    /// the cursor index, and the media type, plus optional extra entries (e.g. the JF-363
    /// cross-media decline keys). Single definition of the attribute shape that call
    /// sites used to hand-copy (JF-430).
    /// </summary>
    /// <param name="matches">The candidate matches to store.</param>
    /// <param name="index">The cursor index of the match being presented.</param>
    /// <param name="mediaType">The media type being disambiguated.</param>
    /// <param name="extraEntries">Additional session entries layered on top of the disambiguation state.</param>
    /// <returns>The session-attributes dictionary.</returns>
    internal static Dictionary<string, object> BuildAttributes(
        List<MatchInfo> matches,
        int index,
        string mediaType,
        params (string Key, object Value)[] extraEntries)
    {
        var attributes = new Dictionary<string, object>
        {
            [AttrMatches] = JsonConvert.SerializeObject(matches),
            [AttrIndex] = index,
            [AttrType] = mediaType
        };

        foreach ((string key, object value) in extraEntries)
        {
            attributes[key] = value;
        }

        return attributes;
    }

    /// <summary>
    /// The maximum number of candidate names the multi-artist ask speaks in one
    /// breath (JF-707): the DisambiguateMultipleArtists string was designed for
    /// the two-name shape, and a long same-first-word family in one breath is
    /// incomprehensible. Only the SPOKEN list is capped: the cycling state
    /// keeps every match, so a rank beyond the cap is not spoken up front but
    /// stays reachable via DisambiguateNext, which names it at its own turn.
    /// AskFirstMatch instead truncates its STATE at the same count (3); the
    /// two constants are deliberately NOT shared, and that asymmetry was
    /// re-weighed and kept in JF-735 (the full defense and the enforced caller
    /// census live on <see cref="FirstMatchStateCap"/>).
    /// </summary>
    internal const int MultipleArtistsSpeakCap = 3;

    /// <summary>
    /// The maximum number of candidates AskFirstMatch keeps in its
    /// disambiguation STATE: a rank past it is never spoken, never reachable
    /// through the yes/no cycle, and never hinted. That is DELIBERATE,
    /// re-weighed and kept in JF-735, and it is the intentional opposite of
    /// <see cref="MultipleArtistsSpeakCap"/>, which caps only SPEECH: that
    /// builder's unbounded caller (the JF-690 ER gate) feeds exact
    /// library-name resolutions where every entry is a real candidate, so rank
    /// 4+ must stay reachable and hinted (JF-729), while AskFirstMatch's
    /// multi-candidate callers are the opposite shape and the defense is
    /// structural, not a taste call: every one of them except PlayAlbum's
    /// direct-search leg reaches the ask only through
    /// BaseHandler.HandleFuzzyMiss's NotFound outcome, which fires when the
    /// best SCORED candidate is below SuggestionThreshold (40) or nothing was
    /// scored at all. FuzzyMatcher.FindBestMatchWithScore returns 100 for an
    /// exact name and ContainmentScore (90) whenever either string contains
    /// the other, in BOTH directions (the JF-377 root-cause lock, pinned in
    /// FuzzyMatcherTests), so a genuine exact or containment candidate the
    /// scorer SCORES diverts the flow to auto-play or the Confirm ask: among
    /// scored candidates, a NotFound list's rank-4+ tail is sub-40 noise by
    /// construction. The narrow exception is the scorer's length band
    /// (maxLenDiff): an UNSCORED containment-class candidate, a very short
    /// query inside names longer than the query by more than
    /// max(2 x query length, 15), can sit in a NotFound list. The verdict
    /// keeps despite that corner because the band is pre-existing
    /// FuzzyMatcher recall behavior shared by every acceptance surface (not
    /// something this cap adds), and at nine of the ten sites the binding
    /// truncation is the caller's own Take(3) regardless. Nine of the ten
    /// multi-candidate callers additionally pre-truncate with their own
    /// Take(3) at the call site, so this cap is a backstop (the one
    /// untruncated caller: PlayPodcastIntentHandler's multi-candidate leg).
    /// That caller census is ENFORCED by AskFirstMatchCallerCensusTests (the
    /// WarmingGateCoverageTests pattern): a new AskFirstMatch caller fails
    /// that test's roster until the census is re-weighed here. The two
    /// caller-side legs where a dropped rank-4+ candidate is user-relevant
    /// despite all that (PlayAlbum's alphabetically ordered direct search
    /// hits, where the exact album name can sit past rank 3, and PlayPodcast's
    /// search-relevant sub-40 tail) are filed as JF-743, as is the band-corner
    /// shape above: a helper-side state change cannot reach a caller that
    /// never passes rank 4 past its own Take(3).
    /// </summary>
    internal const int FirstMatchStateCap = 3;

    /// <summary>
    /// Build the multi-artist disambiguation Ask (JF-420.2 shape: plain name list,
    /// yes/no cycling via DisambiguateNext, the family's yes/no reprompt). One
    /// construction for every site that fires the multi-artist prompt: the JF-420
    /// containment-vs-alternative gate (the extraction SOURCE), the JF-652 kana
    /// near-tie block in PlayArtistSongsIntentHandler, the JF-652 kana near-tie
    /// wrapper in CrossMediaFallback.ResolveKanaOriginTie, and the JF-690
    /// multi-value ER gate in MultiValueErDisambiguation (the only caller whose
    /// list is unbounded). The spoken list is capped at
    /// <see cref="MultipleArtistsSpeakCap"/> (JF-707); the session state keeps
    /// the FULL resolved list so no artist the gate resolved becomes unaskable.
    /// The first match is the one "yes" plays. Above the cap the speech also
    /// carries the overflow hint (JF-729): a separate
    /// DisambiguateMultipleArtistsMore sentence naming how many candidates were
    /// NOT spoken, so the capped list no longer reads as an exhaustive found-set
    /// (the reprompt's "say no for the next" tail stays the only cycling
    /// instruction; the hint only says that more exist and how many). A separate
    /// appended string rather than a second format arg on the main key keeps the
    /// at/below-cap speech byte-identical to the pre-JF-729 shape. The hint
    /// APPENDS after the yes/no question (the task's decided shape): the
    /// reprompt, not the closing sentence, is the mic-open call to action, and
    /// the count lands as the final beat before the user answers. Locale
    /// phrasings are count-invariant by design: the most common overflow is
    /// exactly 1 unspoken, so no locale's hint inflects for the numeral
    /// (Arabic uses the count-predicate form, Hindi a verbless form; the
    /// JF-487 singular/plural key split is not needed).
    /// </summary>
    /// <param name="matches">The candidates in spoken order (winner first).</param>
    /// <param name="locale">The request locale.</param>
    /// <returns>The Ask response carrying the disambiguation session state.</returns>
    internal static SkillResponse AskMultipleArtists(List<MatchInfo> matches, string locale)
    {
        // Plain name list: the flow is yes/no cycling (yes plays the first, no
        // advances via DisambiguateNext), so no numbering.
        string matchList = string.Join(", ", matches.Take(MultipleArtistsSpeakCap).Select(m => m.Name));
        string speech = ResponseStrings.Get("DisambiguateMultipleArtists", locale, matchList);

        // JF-729: above the cap, speak the UNSPOKEN count (what cycling through
        // "no" can still reach); at or below the cap the hint is absent.
        int unspoken = matches.Count - MultipleArtistsSpeakCap;
        if (unspoken > 0)
        {
            speech += " " + ResponseStrings.Get("DisambiguateMultipleArtistsMore", locale, unspoken);
        }

        var response = ResponseBuilder.Ask(
            speech,
            new Reprompt(ResponseStrings.Get("DisambiguateReprompt", locale)));
        response.SessionAttributes = BuildAttributes(matches, 0, MediaTypeArtist);
        // JF-690 review: mark ONLY the keys this builder writes, so the
        // cross-media OFFER flavor's decline keys (same disambig_* family, set by
        // the JF-363 offer ask) are superseded with the rest of the other flows:
        // a stale crossmedia_notfound_* surviving a plain artist ask made "no"
        // answer the OLD song/album not-found instead of cycling.
        ConversationalFlows.MarkOthersInactive(
            response, AttrMatches, AttrIndex, AttrType);
        return response;
    }

    /// <summary>
    /// Serializable match info stored in session attributes.
    /// </summary>
    public class MatchInfo
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("artUrl")]
        public string? ArtUrl { get; set; }
    }

    // ========== Shared pick-words machinery (JF-407 item 2, moved from FindSongIntentHandler) ==========
    // Cardinal/ordinal answer words, the JF-395 negative-exit, and the numbered-candidate
    // resolver for DisambiguationHelper-based pickers. Candidate-agnostic since JF-524:
    // callers pass the candidate NAMES, so any picker (song, album, artist, ...) can use it.
    /// <summary>
    /// Cardinal pick words for the candidate picker, all supported locales (JF-396).
    /// Maps the spoken count word to the 0-based candidate index.
    /// </summary>
    private static readonly Dictionary<string, int> CardinalPickWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // en
        ["one"] = 0, ["two"] = 1, ["three"] = 2, ["four"] = 3,
        // it
        ["uno"] = 0, ["due"] = 1, ["tre"] = 2, ["quattro"] = 3,
        // de
        ["eins"] = 0, ["zwei"] = 1, ["drei"] = 2, ["vier"] = 3,
        // fr
        ["un"] = 0, ["une"] = 0, ["deux"] = 1, ["trois"] = 2, ["quatre"] = 3,
        // es (uno/dos/tres/cuatro)
        ["dos"] = 1, ["tres"] = 2, ["cuatro"] = 3,
        // pt (um/uma, dois/duas, três, quatro)
        ["um"] = 0, ["uma"] = 0, ["dois"] = 1, ["duas"] = 1, ["três"] = 2, ["quatro"] = 3,
        // nl (een/twee/drie; "vier" already covered by the German entry, same index)
        ["een"] = 0, ["twee"] = 1, ["drie"] = 2
    };

    /// <summary>
    /// Ordinal word stems per rank (1st..4th) across the supported locales (JF-396).
    /// Substring match, so gendered variants ("segunda"/"segundo") share a stem where
    /// possible; "quarto" (it) and "cuarto"/"quarta" (es/pt) are listed separately.
    /// </summary>
    private static readonly string[][] OrdinalStemsByRank = new[]
    {
        new[] { "first", "primo", "erste", "premier", "primera", "primeira" },
        new[] { "second", "secondo", "zweite", "deuxième", "segund", "tweede" },
        new[] { "third", "terzo", "dritte", "troisième", "tercer", "terceir", "derde" },
        new[] { "fourth", "quarto", "cuarto", "quarta", "vierte", "quatrième", "vierde" }
    };

    /// <summary>
    /// Negative answer words for the disambiguation picker, across the supported locales
    /// (JF-395). A negative answer is a clean exit from the candidate list; the user cannot
    /// otherwise say "none of them" (the picker looped on FindSongInvalidPick forever).
    /// </summary>
    private static readonly HashSet<string> NegativeAnswerWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // en
        "no", "nope", "none", "neither", "nor",
        // it
        "no", "nessuna", "nessuno", "nessun",
        // de
        "nein", "kein", "keine",
        // fr
        "non", "aucun", "aucune",
        // es
        "ninguna", "ninguno", "ningún",
        // pt
        "não", "nenhuma", "nenhum",
        // nl
        "nee", "geen",
        // ja / hi / ar
        "いいえ", "नहीं", "لا"
    };

    /// <summary>
    /// True when the utterance is a negative answer to the candidate picker: either a
    /// single negative word, or a short phrase (up to 4 tokens) that STARTS with one
    /// ("none of them", "nessuno di questi"). Longer utterances are treated as attempted
    /// picks, not exits.
    /// </summary>
    /// <param name="input">The trimmed user input from the disambiguation turn.</param>
    internal static bool IsNegativeAnswer(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string[] tokens = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || tokens.Length > 4)
        {
            return false;
        }

        return NegativeAnswerWords.Contains(tokens[0].Trim('?', '.', '!', ','));
    }

    /// <summary>
    /// Resolve which candidate the user picked by number, ordinal word, or partial title match.
    /// Returns a 0-based index into <paramref name="candidateNames"/>, or null if no match.
    /// </summary>
    internal static int? ResolvePick(string input, IReadOnlyList<string> candidateNames, string locale)
    {
        if (string.IsNullOrWhiteSpace(input) || candidateNames.Count == 0)
        {
            return null;
        }
        string trimmed = input.Trim();
        string[] tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // 1. Direct digits: "1", "2" (ranks 1-4, the extent of the pick-word tables
        // below; title matching is NOT rank-capped).
        if (int.TryParse(trimmed, out int num) && num >= 1 && num <= 4)
        {
            return num - 1;
        }

        // 2. SINGLE-token cardinal ("two", "dos", "dois") or ordinal word ("second",
        // "segunda"): a lone pick word is a rank, never a title.
        if (tokens.Length == 1)
        {
            int? single = TryParseCardinalWord(trimmed) ?? TryParseOrdinalStem(trimmed);
            if (single.HasValue)
            {
                return single;
            }
        }

        // 3. Title match BEFORE ordinal phrases: ordinal stems substring-match, so a
        // multi-token answer that matches a candidate title ("Second Chance") must win
        // over the rank the stem would otherwise hijack it to (review finding: title
        // picks containing ordinal words resolved as ranks).
        int? titlePick = TryMatchByTitle(trimmed, candidateNames);
        if (titlePick.HasValue)
        {
            return titlePick;
        }

        // 4. Ordinal phrases LAST: "the second one", "il primo", "le deuxième".
        return TryParseOrdinalStem(trimmed);
    }

    /// <summary>
    /// Match an ordinal phrase ("the second one", "la segunda") to its rank via the
    /// per-locale stems. Substring-based, so it must run AFTER title matching.
    /// </summary>
    private static int? TryParseOrdinalStem(string input)
    {
        string lower = input.ToLowerInvariant();

        for (int rank = 0; rank < OrdinalStemsByRank.Length; rank++)
        {
            foreach (string stem in OrdinalStemsByRank[rank])
            {
                if (lower.Contains(stem, StringComparison.Ordinal))
                {
                    return rank;
                }
            }
        }

        return null;
    }

    private static int? TryParseCardinalWord(string input)
    {
        string lower = input.ToLowerInvariant().Trim();

        return CardinalPickWords.TryGetValue(lower, out int index) ? index : null;
    }

    private static int? TryMatchByTitle(string input, IReadOnlyList<string> candidateNames)
    {
        string lower = input.ToLowerInvariant();

        for (int i = 0; i < candidateNames.Count; i++)
        {
            if (!string.IsNullOrEmpty(candidateNames[i])
                && candidateNames[i].Contains(lower, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        // Also try: does the input contain words from the candidate name?
        var inputWords = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (inputWords.Length > 0)
        {
            for (int i = 0; i < candidateNames.Count; i++)
            {
                if (string.IsNullOrEmpty(candidateNames[i]))
                {
                    continue;
                }

                string candidateLower = candidateNames[i].ToLowerInvariant();
                if (inputWords.Any(w => w.Length >= 3 && candidateLower.Contains(w, StringComparison.OrdinalIgnoreCase)))
                {
                    return i;
                }
            }
        }

        return null;
    }
}
