#nullable enable
using System;
using System.Collections.Generic;
using Alexa.NET.Request.Type;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// Bare stop/cancel words keyed by locale, shared by every handler that can find itself
/// receiving a captured slot value while a Dialog.ElicitSlot is open: in that regime
/// Alexa captures the user's next utterance INTO the elicited slot instead of routing it
/// to AMAZON.Stop/CancelIntent, so handlers treat a bare cancel word as an implicit
/// cancel (live evidence 2026-08-28/29, FindSong and simulate-skill).
/// </summary>
internal static class CancelWords
{
    // Locale audit (JF-423, 2026-09-01; locale-keyed vocabulary JF-444, 2026-09-02):
    // the vocabulary was a single hardcoded English+Italian HashSet, so in the other
    // flow-reachable locales a bare localized cancel word captured during an open elicit
    // was searched as a title, matched nothing, and re-prompted (the elicitation-trap
    // loop). JF-444 restructured it to locale-keyed sets, vetted per locale against the
    // DEPLOYED model with the repo's standard tool (ask smapi profile-nlu on the live
    // skill, 2026-09-02): a word is added for a locale only when the probe routed the
    // bare utterance to AMAZON.StopIntent/CancelIntent. CARVE-OUT: the shared en-* set
    // below was probed in en-US and en-GB ONLY; en-AU/en-CA/en-IN inherit it without
    // their own probes (and "cancel" has en-US evidence only, no en-GB row). Probe
    // evidence (word -> routed intent; every NO_SELECTION re-probed once and confirmed
    // stable):
    //
    //   locale  word          routed intent          locale  word         routed intent
    //   ------- ------------- ----------------------  ------- ------------ ----------------------
    //   de-DE   stopp         AMAZON.StopIntent       pt-BR   para         AMAZON.StopIntent
    //   de-DE   abbrechen     AMAZON.CancelIntent     pt-BR   pare         AMAZON.StopIntent
    //   de-DE   beende        AMAZON.StopIntent       pt-BR   cancela      AMAZON.CancelIntent
    //   de-DE   stop          AMAZON.StopIntent       pt-BR   stop         AMAZON.StopIntent
    //   fr-FR   arrête        AMAZON.StopIntent       nl-NL   stop         AMAZON.StopIntent
    //   fr-FR   stoppe        AMAZON.StopIntent       nl-NL   stoppen      AMAZON.StopIntent
    //   fr-FR   annule        AMAZON.CancelIntent     nl-NL   annuleer     AMAZON.CancelIntent
    //   fr-FR   stop          AMAZON.StopIntent       hi-IN   रोको         AMAZON.StopIntent
    //   fr-CA   arrête        AMAZON.StopIntent       hi-IN   बंद करो      AMAZON.StopIntent
    //   fr-CA   stoppe        AMAZON.StopIntent       hi-IN   stop         AMAZON.StopIntent
    //   fr-CA   annule        AMAZON.CancelIntent     hi-IN   cancel       AMAZON.CancelIntent
    //   fr-CA   stop          AMAZON.StopIntent       ar-SA   إيقاف        AMAZON.StopIntent
    //   es-ES   para          AMAZON.StopIntent       ar-SA   stop         AMAZON.StopIntent
    //   es-ES   cancela       AMAZON.CancelIntent     it-IT   ferma        AMAZON.StopIntent
    //   es-ES   detén         AMAZON.StopIntent       it-IT   annulla      AMAZON.CancelIntent
    //   es-ES   stop          AMAZON.StopIntent       it-IT   basta        AMAZON.StopIntent
    //   es-MX   para          AMAZON.StopIntent       it-IT   cancella     AMAZON.CancelIntent
    //   es-MX   cancela       AMAZON.CancelIntent     it-IT   annullare    AMAZON.CancelIntent
    //   es-MX   detén         AMAZON.StopIntent       it-IT   stoppa       AMAZON.StopIntent
    //   es-MX   stop          AMAZON.StopIntent       it-IT   fermare      NO_SELECTION
    //   es-US   para          AMAZON.StopIntent       it-IT   arresta      NO_SELECTION
    //   es-US   cancela       AMAZON.CancelIntent     it-IT   fermo        ShowMoreIntent
    //   es-US   stop          AMAZON.StopIntent       en-US   stop         AMAZON.StopIntent
    //   en-GB   stop          AMAZON.StopIntent       en-US   cancel       AMAZON.CancelIntent
    //
    // Exclusions (probed, did NOT route, confirmed stable on re-probe; deliberately NOT
    // added even though they are common imperatives): es-* "alto" (NO_SELECTION in
    // es-ES/es-MX/es-US); es-US "detén" (routes in es-ES/es-MX only, so it stays out of
    // the es-US set); ar-SA "توقف" (NO_SELECTION twice; "إيقاف" carries the meaning);
    // "cancel" in 9 of the 10 own-set non-English locales (NO_SELECTION twice each in
    // de-DE, fr-FR, fr-CA, es-ES, es-MX, es-US, pt-BR, nl-NL and ar-SA, probed
    // 2026-09-03 because the pre-JF-444 shared set had carried "cancel" everywhere; the
    // drop stands as vetted there). hi-IN is the 10th: "cancel" DID route there
    // (AMAZON.CancelIntent, twice) and was added.
    //
    // Excluded locale: ja-JP has NO deployed model on the live skill (profile-nlu and
    // get-interaction-model both return HTTP 400; ja-JP is absent from manifest.json's
    // 12 locales), so probes cannot vet any Japanese word. ja-JP therefore has no
    // locale entry and falls through to the English fallback set; when a ja-JP model is
    // deployed, re-vet (candidates: とめて/ストップ) before adding an entry.
    //
    // it-IT keeps the pre-JF-444 legacy set (six it-IT template StopIntent samples per
    // the JF-402 deliberate exception, plus the other Italian imperatives from the
    // JF-423 live evidence). "fermare"/"arresta"/"fermo" do not route to Stop/Cancel as
    // standalone probes, but removing previously shipped escape words is a behavior
    // change beyond JF-444's add-only mandate; the probe rows above record the evidence
    // if a later task wants to tighten it. JF-445 (2026-09-03) tightened exactly this
    // for the FRESH (dialogState STARTED) regime only: see ProbedBareWordsByLocale.
    private static readonly HashSet<string> EnglishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "stop", "cancel",
    };

    private static readonly HashSet<string> ItalianWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "stop", "ferma", "fermare", "fermo", "ferma tutto", "ferma la musica", "ferma riproduzione", "ferma la riproduzione", "annulla", "annullare", "cancella", "basta", "stoppa", "arresta", "cancel",
    };

    private static readonly HashSet<string> GermanWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "stopp", "abbrechen", "beende", "stop",
    };

    private static readonly HashSet<string> FrenchWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "arrête", "stoppe", "annule", "stop",
    };

    private static readonly HashSet<string> SpanishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "para", "cancela", "detén", "stop",
    };

    private static readonly HashSet<string> SpanishUsWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // es-US only: identical to SpanishWords minus "detén", which the es-US model
        // does not route (see the probe table).
        "para", "cancela", "stop",
    };

    private static readonly HashSet<string> PortugueseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "para", "pare", "cancela", "stop",
    };

    private static readonly HashSet<string> DutchWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "stop", "stoppen", "annuleer",
    };

    private static readonly HashSet<string> HindiWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // "cancel" probed 2026-09-03: routes to AMAZON.CancelIntent on the deployed
        // hi-IN model (twice, stable), unlike the 9 other own-set locales.
        "रोको", "बंद करो", "stop", "cancel",
    };

    private static readonly HashSet<string> ArabicWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "إيقاف", "stop",
    };

    private static readonly Dictionary<string, HashSet<string>> WordsByLocale = new(StringComparer.OrdinalIgnoreCase)
    {
        // en-* variants share one set (probed en-US/en-GB ONLY; en-AU/en-CA/en-IN
        // inherit without their own probes, and "cancel" has en-US evidence only).
        ["en-US"] = EnglishWords,
        ["en-GB"] = EnglishWords,
        ["en-AU"] = EnglishWords,
        ["en-CA"] = EnglishWords,
        ["en-IN"] = EnglishWords,
        ["it-IT"] = ItalianWords,
        ["de-DE"] = GermanWords,
        // fr-FR/fr-CA share (probed identically in both).
        ["fr-FR"] = FrenchWords,
        ["fr-CA"] = FrenchWords,
        // es-ES/es-MX share; es-US drops "detén" (probe evidence).
        ["es-ES"] = SpanishWords,
        ["es-MX"] = SpanishWords,
        ["es-US"] = SpanishUsWords,
        ["pt-BR"] = PortugueseWords,
        ["nl-NL"] = DutchWords,
        ["hi-IN"] = HindiWords,
        ["ar-SA"] = ArabicWords,
    };

    /// <summary>
    /// Whether the captured slot text is exactly a stop/cancel word in the request's
    /// locale (trimmed, whole-value match only: a keyword phrase merely containing
    /// "stop" is a legitimate search, e.g. a song titled "Don't Stop Believin'").
    /// Locales with no vetted entry (today ja-JP) fall back to the English set: "stop"
    /// routed to a built-in in every deployable locale probed.
    /// </summary>
    /// <param name="slotValue">The raw captured slot value.</param>
    /// <param name="locale">The request locale (e.g. "de-DE").</param>
    /// <returns>True when the value is a bare cancel word for that locale.</returns>
    internal static bool IsCancelWord(string? slotValue, string locale)
    {
        if (string.IsNullOrWhiteSpace(slotValue))
        {
            return false;
        }

        return WordsFor(locale).Contains(slotValue.Trim());
    }

    /// <summary>The full vocabulary for a locale (English set as the fallback).</summary>
    private static HashSet<string> WordsFor(string locale)
        => WordsByLocale.TryGetValue(locale, out HashSet<string>? localeWords) ? localeWords : EnglishWords;

    /// <summary>
    /// Whether the request's dialog is still in progress, meaning the utterance arrived
    /// THROUGH an open Dialog.ElicitSlot/Delegate rather than as a fresh full-utterance
    /// match. The elicitation-trap escape hatches (FindSong, PlaySong, PlayAlbum) gate on
    /// this so a legitimate search for a title that happens to be a cancel word still
    /// runs (JF-423; code-review 2026-08-29).
    /// </summary>
    /// <param name="request">The incoming intent request.</param>
    /// <returns>True when dialogState is IN_PROGRESS.</returns>
    internal static bool IsDialogInProgress(IntentRequest request)
        => string.Equals(request.DialogState, "IN_PROGRESS", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether any slot of the incoming request carries a bare cancel word for the
    /// request's locale. The escape hatches must inspect EVERY slot, not just their
    /// primary one: a force-routed request can carry the captured word in any slot
    /// (e.g. "annulla" misrouted to a sibling intent lands in its musician slot), and
    /// searching that as an artist name reopens the elicitation-trap loop the hatches
    /// exist to close (JF-423).
    /// </summary>
    /// <param name="request">The incoming intent request.</param>
    /// <param name="locale">The request locale, for the vocabulary lookup.</param>
    /// <returns>True when any slot value is a bare cancel word.</returns>
    internal static bool AnySlotIsCancelWord(IntentRequest request, string locale)
    {
        if (request.Intent.Slots == null)
        {
            return false;
        }

        foreach (var slot in request.Intent.Slots.Values)
        {
            if (IsCancelWord(slot.Value, locale))
            {
                return true;
            }
        }

        return false;
    }

    // Whitespace separators for the bare-word (single-token) guard below.
    private static readonly char[] WhitespaceChars = { ' ', '\t', '\n', '\r' };

    // JF-445 review hardening (2026-09-03): the STARTED (fresh-dialog) leg of the
    // predicate below consults this NARROWER per-locale vocabulary instead of the
    // locale's full set. Only it-IT needs an entry: every other locale's set was
    // probe-vetted word-by-word by JF-444 (and its multi-word entries are already
    // excluded by the bare guard), while it-IT keeps the pre-JF-444 legacy set for the
    // IN_PROGRESS same-intent capture regime (JF-423 live evidence). In the STARTED
    // shape the word must be one the deployed model actually routes toward Stop/Cancel
    // as a standalone probe: exactly the single-token it-IT probe-table rows with a
    // routed intent (ferma, annulla, basta, cancella, annullare, stoppa). The legacy
    // single-token words WITHOUT a routing row stay out of the fresh regime:
    // "fermare"/"arresta" (NO_SELECTION), "fermo" (ShowMoreIntent), "stop"/"cancel"
    // (no it-IT probe row; "cancel" was NO_SELECTION in all 9 probed non-English
    // locales).
    private static readonly HashSet<string> ItalianProbedBareWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ferma", "annulla", "basta", "cancella", "annullare", "stoppa",
    };

    private static readonly Dictionary<string, HashSet<string>> ProbedBareWordsByLocale = new(StringComparer.OrdinalIgnoreCase)
    {
        ["it-IT"] = ItalianProbedBareWords,
    };

    /// <summary>
    /// The vocabulary for the FRESH-dialog (STARTED) leg of
    /// <see cref="IsForceRoutedCancelCapture"/>: the locale's probed bare-word set where
    /// one exists (today it-IT, whose full set is legacy), otherwise the locale's own
    /// probe-vetted set unchanged.
    /// </summary>
    private static HashSet<string> ProbedBareWordsFor(string locale)
        => ProbedBareWordsByLocale.TryGetValue(locale, out HashSet<string>? probedBare) ? probedBare : WordsFor(locale);

    /// <summary>
    /// Whether the request carries the force-routed sibling-misroute cancel shape:
    /// dialogState STARTED or IN_PROGRESS, with a BARE single-token cancel word in a
    /// slot OTHER than the flow's primary slot. Under manual dialog control Alexa sets
    /// dialogState to "STARTED (when the intent is invoked)" per the Dialog Interface
    /// Reference, so a cancel word the NLU resolves onto a SIBLING intent mid-flow
    /// (e.g. "annulla" landing in musician) arrives STARTED, a fresh invocation of that
    /// sibling's dialog; only replies captured into the SAME intent's open elicit arrive
    /// IN_PROGRESS (JF-422 observation; JF-411 recorded the STARTED half on-device).
    /// The word must sit in a NON-primary slot because a STARTED request carrying it in
    /// the primary slot is a fresh full-utterance search for a title that happens to be
    /// a cancel word ("trova la canzone basta"), and it must be BARE (single token,
    /// whole-value match) because a multi-word slot value is a search query naming a
    /// real artist ("band basta" for the artist Basta). The STARTED leg additionally
    /// consults the locale's probed bare-word vocabulary (<see cref="ProbedBareWordsFor"/>),
    /// a NARROWER set than <see cref="IsCancelWord"/> where the locale's full set is
    /// legacy (today it-IT only), so a word the deployed model does not route toward
    /// Stop/Cancel as a standalone probe cannot cancel a fresh sibling request.
    /// Callers must additionally gate this on their open-flow session state: the
    /// FindSongSessionData force-route is the only routing regime that delivers sibling
    /// requests into an open flow, which is why only FindSong consumes it (JF-445).
    /// </summary>
    /// <param name="request">The incoming intent request.</param>
    /// <param name="locale">The request locale, for the vocabulary lookup.</param>
    /// <param name="primarySlotName">The flow's primary slot (e.g. titleKeywords); in the STARTED shape a cancel word there is a fresh full-utterance search, not a misroute capture.</param>
    /// <returns>True when dialogState is STARTED or IN_PROGRESS and a non-primary slot holds a bare cancel word.</returns>
    internal static bool IsForceRoutedCancelCapture(IntentRequest request, string locale, string primarySlotName)
    {
        bool freshDialog = string.Equals(request.DialogState, "STARTED", StringComparison.OrdinalIgnoreCase);
        bool openDialog = freshDialog || IsDialogInProgress(request);
        if (!openDialog || request.Intent.Slots == null)
        {
            return false;
        }

        // STARTED consults the probe-vetted bare-word vocabulary; IN_PROGRESS keeps the
        // locale's full set, matching the same-intent capture regime (FindSong hatch
        // disjunct 1 uses AnySlotIsCancelWord on the same full set).
        HashSet<string> words = freshDialog ? ProbedBareWordsFor(locale) : WordsFor(locale);

        foreach (var slot in request.Intent.Slots)
        {
            // The dictionary key is the slot's canonical identity (how GetSlotValue reads it).
            if (string.Equals(slot.Key, primarySlotName, StringComparison.Ordinal))
            {
                continue;
            }

            string trimmed = slot.Value?.Value?.Trim() ?? string.Empty;
            if (trimmed.IndexOfAny(WhitespaceChars) < 0 && words.Contains(trimmed))
            {
                return true;
            }
        }

        return false;
    }
}
