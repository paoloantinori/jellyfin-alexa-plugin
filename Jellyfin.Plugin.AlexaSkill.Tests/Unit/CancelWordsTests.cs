#nullable enable
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class CancelWordsTests
{
    // JF-444: the vocabulary is locale-keyed and every entry is vetted against the
    // deployed model via ask smapi profile-nlu (probe table in CancelWords.cs). These
    // tests pin the per-locale sets, the exclusions, and the shared-set fallback.

    [Theory]
    [InlineData("stop", "en-US", true)]
    [InlineData("cancel", "en-US", true)]
    [InlineData("stop", "en-GB", true)]
    [InlineData("stop", "en-AU", true)]
    [InlineData("stop", "en-CA", true)]
    [InlineData("stop", "en-IN", true)]
    [InlineData("stopp", "de-DE", true)]
    [InlineData("abbrechen", "de-DE", true)]
    [InlineData("beende", "de-DE", true)]
    [InlineData("stop", "de-DE", true)]
    [InlineData("arrête", "fr-FR", true)]
    [InlineData("stoppe", "fr-FR", true)]
    [InlineData("annule", "fr-FR", true)]
    [InlineData("arrête", "fr-CA", true)]
    [InlineData("stoppe", "fr-CA", true)]
    [InlineData("annule", "fr-CA", true)]
    [InlineData("stop", "fr-CA", true)]
    [InlineData("para", "es-ES", true)]
    [InlineData("cancela", "es-ES", true)]
    [InlineData("detén", "es-ES", true)]
    [InlineData("stop", "es-ES", true)]
    [InlineData("detén", "es-MX", true)]
    [InlineData("para", "es-US", true)]
    [InlineData("cancela", "es-US", true)]
    [InlineData("stop", "es-US", true)]
    [InlineData("para", "pt-BR", true)]
    [InlineData("pare", "pt-BR", true)]
    [InlineData("cancela", "pt-BR", true)]
    [InlineData("stop", "pt-BR", true)]
    [InlineData("stop", "nl-NL", true)]
    [InlineData("stoppen", "nl-NL", true)]
    [InlineData("annuleer", "nl-NL", true)]
    [InlineData("रोको", "hi-IN", true)]
    [InlineData("बंद करो", "hi-IN", true)]
    [InlineData("stop", "hi-IN", true)]
    [InlineData("cancel", "hi-IN", true)] // probed 2026-09-03: routes to AMAZON.CancelIntent (twice, stable)
    [InlineData("إيقاف", "ar-SA", true)]
    [InlineData("stop", "ar-SA", true)]
    [InlineData("ferma", "it-IT", true)]
    [InlineData("annulla", "it-IT", true)]
    [InlineData("basta", "it-IT", true)]
    public void IsCancelWord_VettedLocaleWord_IsCancel(string word, string locale, bool expected)
    {
        Assert.Equal(expected, CancelWords.IsCancelWord(word, locale));
    }

    [Theory]
    // Probe-vetted exclusions: common imperatives whose probes did NOT route to
    // AMAZON.Stop/CancelIntent on the deployed model (re-probed, stable).
    [InlineData("alto", "es-ES")]
    [InlineData("alto", "es-MX")]
    [InlineData("alto", "es-US")]
    [InlineData("detén", "es-US")] // routes in es-ES/es-MX only
    [InlineData("توقف", "ar-SA")]
    // "cancel" vetting (2026-09-03, twice each, stable): the pre-JF-444 shared set
    // carried it everywhere, but a bare "cancel" does NOT route to Stop/Cancel in 9 of
    // the 10 own-set non-English locales (it routes in hi-IN only, and stays in the
    // en-* and it-IT legacy sets).
    [InlineData("cancel", "de-DE")]
    [InlineData("cancel", "fr-FR")]
    [InlineData("cancel", "fr-CA")]
    [InlineData("cancel", "es-ES")]
    [InlineData("cancel", "es-MX")]
    [InlineData("cancel", "es-US")]
    [InlineData("cancel", "pt-BR")]
    [InlineData("cancel", "nl-NL")]
    [InlineData("cancel", "ar-SA")]
    // Cross-locale leakage: a word vetted for one locale must not leak into another.
    [InlineData("basta", "en-US")]
    [InlineData("ferma", "de-DE")]
    [InlineData("abbrechen", "fr-FR")]
    [InlineData("annulla", "pt-BR")]
    public void IsCancelWord_ExcludedOrForeignWord_IsNotCancel(string word, string locale)
    {
        Assert.False(CancelWords.IsCancelWord(word, locale));
    }

    [Fact]
    public void IsCancelWord_UndeployedLocale_FallsBackToEnglishSet()
    {
        // ja-JP has no deployed model on the live skill (profile-nlu/get-interaction-model
        // return 400), so nothing could be vetted for it; it falls back to the English set
        // ("stop" routed in every deployable locale probed).
        Assert.True(CancelWords.IsCancelWord("stop", "ja-JP"));
        Assert.True(CancelWords.IsCancelWord("cancel", "ja-JP"));
        Assert.False(CancelWords.IsCancelWord("ferma", "ja-JP"));
    }

    [Theory]
    [InlineData(null, "en-US")]
    [InlineData("", "en-US")]
    [InlineData("   ", "en-US")]
    public void IsCancelWord_NullOrWhitespace_IsNotCancel(string? value, string locale)
    {
        Assert.False(CancelWords.IsCancelWord(value, locale));
    }

    [Theory]
    [InlineData("  stop  ", "en-US")] // trimmed whole-value match
    [InlineData("STOP", "en-US")] // case-insensitive
    [InlineData("Stopp", "de-DE")]
    public void IsCancelWord_TrimsAndIgnoresCase(string value, string locale)
    {
        Assert.True(CancelWords.IsCancelWord(value, locale));
    }

    [Theory]
    [InlineData("don't stop believin'", "en-US")] // whole-value match only (doc example)
    [InlineData("stopping", "en-US")]
    [InlineData("ferma la musica", "de-DE")] // multi-word it phrase is it-IT only
    public void IsCancelWord_PhraseContainingCancelWord_IsNotCancel(string value, string locale)
    {
        Assert.False(CancelWords.IsCancelWord(value, locale));
    }

    [Fact]
    public void AnySlotIsCancelWord_ChecksEverySlotWithLocale()
    {
        // JF-423: a force-routed request can carry the captured word in ANY slot; JF-444:
        // the vocabulary lookup uses the request locale.
        var intent = new Intent { Name = "PlaySongIntent" };
        intent.Slots = new System.Collections.Generic.Dictionary<string, Slot>
        {
            ["song"] = new Slot { Name = "song", Value = "wish you were here" },
            ["musician"] = new Slot { Name = "musician", Value = "abbrechen" }
        };
        var request = new IntentRequest { Intent = intent, Locale = "de-DE" };

        Assert.True(CancelWords.AnySlotIsCancelWord(request, "de-DE"));
        Assert.False(CancelWords.AnySlotIsCancelWord(request, "it-IT"), "German word must not cancel under the it-IT vocabulary");
    }

    [Fact]
    public void AnySlotIsCancelWord_NoSlots_IsFalse()
    {
        var intent = new Intent { Name = "PlaySongIntent" };
        var request = new IntentRequest { Intent = intent, Locale = "en-US" };

        Assert.False(CancelWords.AnySlotIsCancelWord(request, "en-US"));
    }

    // JF-445: the force-routed sibling-misroute predicate. A cancel word resolved onto a
    // sibling intent arrives dialogState STARTED (a fresh invocation of that sibling's
    // dialog, per the Alexa Dialog Interface Reference), so this predicate accepts
    // STARTED (and IN_PROGRESS for self-containment) when the word is BARE (single
    // token) and in a NON-primary slot.

    private static IntentRequest BuildMisrouteRequest(string? musicianValue, string? titleKeywordsValue = null)
    {
        var intent = new Intent { Name = "PlaySongIntent" };
        var slots = new System.Collections.Generic.Dictionary<string, Slot>();
        if (musicianValue != null)
        {
            slots["musician"] = new Slot { Name = "musician", Value = musicianValue };
        }

        if (titleKeywordsValue != null)
        {
            slots["titleKeywords"] = new Slot { Name = "titleKeywords", Value = titleKeywordsValue };
        }

        intent.Slots = slots;
        return new IntentRequest { Intent = intent, Locale = "it-IT" };
    }

    [Theory]
    [InlineData("STARTED")]
    [InlineData("IN_PROGRESS")]
    public void IsForceRoutedCancelCapture_BareWordInNonPrimarySlot_MidConversation_IsTrue(string dialogState)
    {
        var request = BuildMisrouteRequest("annulla");
        request.DialogState = dialogState;

        Assert.True(CancelWords.IsForceRoutedCancelCapture(request, "it-IT", "titleKeywords"));
    }

    [Fact]
    public void IsForceRoutedCancelCapture_BareWordInPrimarySlot_IsFalse()
    {
        // A STARTED request carrying the word in the flow's primary slot is a fresh
        // full-utterance search ("trova la canzone basta"), not a misroute capture.
        var request = BuildMisrouteRequest(null, "basta");
        request.DialogState = "STARTED";

        Assert.False(CancelWords.IsForceRoutedCancelCapture(request, "it-IT", "titleKeywords"));
    }

    [Theory]
    [InlineData("band basta")] // real-artist search shape ("band" carrier + name)
    [InlineData("dimmi basta")] // qualified command, not a bare word
    [InlineData("ferma tutto")] // a multi-word cancel PHRASE: bare words only here
    public void IsForceRoutedCancelCapture_MultiWordSlotValue_IsFalse(string value)
    {
        var request = BuildMisrouteRequest(value);
        request.DialogState = "STARTED";

        Assert.False(CancelWords.IsForceRoutedCancelCapture(request, "it-IT", "titleKeywords"));
    }

    [Theory]
    [InlineData("COMPLETED")]
    [InlineData(null)]
    public void IsForceRoutedCancelCapture_DialogNotMidConversation_IsFalse(string? dialogState)
    {
        var request = BuildMisrouteRequest("annulla");
        request.DialogState = dialogState;

        Assert.False(CancelWords.IsForceRoutedCancelCapture(request, "it-IT", "titleKeywords"));
    }

    [Fact]
    public void IsForceRoutedCancelCapture_LocaleMismatch_IsFalse()
    {
        // The vocabulary lookup is locale-keyed (JF-444): a German bare word must not
        // trigger the it-IT capture predicate.
        var request = BuildMisrouteRequest("abbrechen");
        request.DialogState = "STARTED";

        Assert.False(CancelWords.IsForceRoutedCancelCapture(request, "it-IT", "titleKeywords"));
        Assert.True(CancelWords.IsForceRoutedCancelCapture(request, "de-DE", "titleKeywords"));
    }

    [Fact]
    public void IsForceRoutedCancelCapture_NoSlots_IsFalse()
    {
        var intent = new Intent { Name = "PlaySongIntent" };
        var request = new IntentRequest { Intent = intent, Locale = "it-IT", DialogState = "STARTED" };

        Assert.False(CancelWords.IsForceRoutedCancelCapture(request, "it-IT", "titleKeywords"));
    }

    [Fact]
    public void IsForceRoutedCancelCapture_TrimsTheSlotValue()
    {
        var request = BuildMisrouteRequest("  annulla  ");
        request.DialogState = "STARTED";

        Assert.True(CancelWords.IsForceRoutedCancelCapture(request, "it-IT", "titleKeywords"));
    }

    // JF-445 review hardening (2026-09-03): the STARTED (fresh-dialog) leg consults the
    // it-IT probe-vetted bare-word set, NOT the legacy full set, so single-token legacy
    // words the deployed model does not route toward Stop/Cancel as standalone probes
    // cannot cancel a fresh sibling request.

    [Theory]
    [InlineData("fermo")]    // probe: ShowMoreIntent
    [InlineData("fermare")]  // probe: NO_SELECTION
    [InlineData("arresta")]  // probe: NO_SELECTION
    [InlineData("stop")]     // no it-IT probe row in the table
    [InlineData("cancel")]   // no it-IT probe row (NO_SELECTION in all 9 probed non-English locales)
    public void IsForceRoutedCancelCapture_StartedLegacyWordWithoutProbeRow_IsFalse(string word)
    {
        var request = BuildMisrouteRequest(word);
        request.DialogState = "STARTED";

        Assert.False(CancelWords.IsForceRoutedCancelCapture(request, "it-IT", "titleKeywords"));
    }

    [Theory]
    [InlineData("ferma")]
    [InlineData("annulla")]
    [InlineData("basta")]
    [InlineData("cancella")]
    [InlineData("annullare")]
    [InlineData("stoppa")]
    public void IsForceRoutedCancelCapture_StartedProbedBareWord_IsTrue(string word)
    {
        // Exactly the single-token it-IT probe-table rows with a routed Stop/Cancel
        // intent keep cancelling in the fresh regime.
        var request = BuildMisrouteRequest(word);
        request.DialogState = "STARTED";

        Assert.True(CancelWords.IsForceRoutedCancelCapture(request, "it-IT", "titleKeywords"));
    }

    [Fact]
    public void IsForceRoutedCancelCapture_InProgressLegacyWord_StillTrue()
    {
        // The trim is fresh-regime ONLY: the IN_PROGRESS leg keeps the full legacy set
        // (the JF-423 same-intent capture regime has live evidence for those words, and
        // FindSong hatch disjunct 1 uses AnySlotIsCancelWord on the same full set).
        var request = BuildMisrouteRequest("fermo");
        request.DialogState = "IN_PROGRESS";

        Assert.True(CancelWords.IsForceRoutedCancelCapture(request, "it-IT", "titleKeywords"));
    }
}
