---
id: JF-659
title: >-
  JF-659 - read the ER canonical for musician slots at the artist entry points
  (the genre-canonical pattern): an ER-resolved クイーン should play Queen directly,
  not hit the Keane/Queen tie ask
status: Done
assignee: []
created_date: '2026-09-28 04:29'
updated_date: '2026-09-28 10:23'
labels:
  - nlu
  - search
  - i18n
  - ja-JP
dependencies: []
references:
  - >-
    backlog/tasks/jf-646 -
    JF-646-catalog-side-katakana-synonyms-Latin-artist-album-names-get-kana-variants-in-the-ja-catalog-upload-so-NLU-selection-resolves-naturalized-ja-JP-voice-the-routing-layer-complement-to-JF-643.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-28 from the JF-646 live verification battery (the arc's final state; everything else passed).

THE FINDING: naturalized ja artist voice now SELECTS PlayArtistSongs with ER_SUCCESS_MATCH on the JellyfinArtist catalog (クイーン の曲を再生して verified, deterministic), but the HANDLER still resolves from the RAW musician slot (クイーン), romanizes to 'kuin', and hits the Queen/Keane DM tie -> the multi-artist disambiguation ask fires even when Amazon's ER already resolved クイーン to the canonical 'Queen'. One extra conversational turn on every tie-shaped name despite a resolved entity.

THE WORK: read the ER canonical at the artist entry points, mirroring the genre canonical read JF-642 shipped for the genre slots (SlotValueHelper.GetCanonicalValue(slot) ?? raw). Sites: PlayArtistSongsIntentHandler's musician slot, the JF-471 album-by-artist acceptance, CrossMediaFallback's artist fallback entry (where the slot object is available), and PlaySong/PlayVideo/PlayBook/PlayPodcast musician slots (same one-line shape; raw kept for speech per the JF-642 F-1 lesson). With a canonical in hand the JF-652 acceptance resolves directly (exact tag match; no tie scan needed).

VERIFICATION BAR: the simulator-equivalent shape through a slot WITH resolution resolves to the canonical artist without the tie ask (unit-test the handler with an ER-carrying slot); the raw-slot path (simulator, no ER) keeps today's honest ask; the Latin matrix unchanged.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-28 worker round (landed on the worktree branch):

SITES WIRED (the N-sites grep over every musician-slot reader; the dispatch's
PlayVideo/PlayBook/PlayPodcast have NO musician slot, their slots are
title/book/podcast_name, so the grep rule governed the roster). All 7 handlers
that read a musician slot are wired, 8 read sites total:
- PlayArtistSongsIntentHandler: the full query-local split. musicianQuery
  (canonical ?? romanized raw) feeds every tier, the JF-377/JF-420 shape gates,
  HandleFuzzyMiss, the Fast-mode pick and the TrySongFallback; `musician` keeps
  the NotFoundArtist speech and the kana-gated acceptance param (equal to
  musicianQuery on every path that reaches it).
- PlayAlbumIntentHandler: musicianSearch (hoisted after the JF-489 retry block)
  at the artist filter, the JF-471 acceptance and the JF-473 gate; the
  JF-489/JF-492 title retries and every not-found stay raw.
- PlaySongIntentHandler: searchMusician local at the artist filter; speech keeps
  musicianQuery.
- QueryArtistLibraryIntentHandler: musicianSearch at SearchAsync and
  TrySongFallback; speech keeps musician.
- AddToQueue + PlayNext: canonical ?? musicianQuery at the artist filter;
  speech keeps musicianQuery.
- FindSong x2 (first invocation + AwaitingArtist, the latter canonical only on
  the musician leg; the pick-fallback and keywords-fallback reads stay raw,
  they are not artist searches).

Slot typing (verified against all 17 models): musician is the static catalog
type JellyfinArtist in 8 locales (en family + it-IT) and AMAZON.Musician in the
other 9 INCLUDING ja-JP, where the live ER of the JF-646 battery arrives through
the catalog/dynamic-entity kana-variant wiring rather than a static type. The
handler-side read is slot-type-agnostic (any ER_SUCCESS_MATCH authority), so the
wiring covers both shapes and is inert where nothing resolves.

CrossMediaFallback EVALUATION (dispatch item 3): TryEntityFallbackAsync stays
string-only, no caller change. Every caller passes a NON-musician slot's text
(PlayByGenre genre, PlayAlbum album, PlayMoodMusic mood, PlaySong song title,
FindSong keywords); where those slots carry ER canonicals at all, the canonical
names the slot's OWN entity type (a genre, a mood, an album), not an artist, so
it is not "the best artist query"; the misrouted-artist shapes the fallback
exists for arrive exactly as the unmatched raw text. No musician text ever
reaches it (PlaySong's call site is the no-musician branch by construction).
The JF-652 kana bar inside is therefore untouched: kanaOrigin computes from the
passed string, and a canonical-bearing string (Latin by construction) would
compute false making the bar inert, but no such string is ever passed.

KANA-BAR REASONING (dispatch item, verified against the code and now owned by
ArtistSearch.IsKanaOriginQuery's doc, the one definition): a canonical-bearing
query keeps kanaOrigin false, so the JF-652 acceptance bar is inert for it; the
ER match IS the Double Metaphone collision evidence the bar demands. Residual
(code-review F5, held): a ja library artist whose canonical name itself is kana
plus a stale catalog (renamed artist) degrades to an honest not-found where the
raw path's romanization might fuzzy-recover; a live-name kana canonical still
exact-matches tier 1 (Contains self-match), and romanizing the canonical
instead would break that exact hit, so verbatim stays.

GATES. /simplify (4 parallel angles): APPLIED the comment trim to one-line
pointers (SlotValueHelper's doc owns the pairing contract), the PlayAlbum
musicianSearch hoist (five rebuilds of one value to one), the PlaySong
searchMusician local, the QueryArtistLibrary uniform request-level read, the
shared ArtistSearch.IsKanaOriginQuery, and the test hoists (TestHelpers
.ResolvedSlot as the one ER-graph builder, consumed by both new test files;
HasAudioPlayerDirective via TestHelpers.GetPlayDirective). SKIPPED: a
ReadMusicianSlot-style BaseHandler helper (the fold is one null-coalescing
whose PLACEMENT is per-site semantics: PlayAlbum post-JF-489, FindSong
conditional legs; the genre-canonical precedent is per-site reads) and hoisting
Queen/Keane/CodesFromNames/IsDisambiguationAsk into TestHelpers (pays only if
KanaOriginAcceptanceTests also switches, churn outside the diff; the new file
deliberately mirrors the pinned reference file's fixtures). The raw control
test leg stays: same locale as its ER twin so ER is the ONLY delta (the
differential proof; the locale permutation is the reason it is not redundant
with KanaOriginAcceptanceTests Shape 1).

code-review high (6 findings): APPLIED F1 (the JF-492 album-title retry keeps
the RAW value: an ER-resolved slot is positive evidence the user named an
artist, so the "the slot carried a title" premise is void for the canonical,
and guessing the canonical as a title could auto-play a same-titled album by
another artist) and F6 (QueryArtistLibrary single musicianSearch local). HELD:
F2 (HandleFuzzyMiss receives the canonical and its confirm prompt speaks the
query: the pre-diff value at that site was already the romanized raw, the same
normalized class, and feeding the raw instead would desync the matcher's query
from the candidate set the canonical search produced, changing which candidate
wins), F3 (the JF-489 replace-case: with F1 applied the named wrongness
dissolves; when both a calling-word strip and an ER match fire, the canonical
is the stronger signal for the artist search and the not-found still speaks the
stripped raw), F4 (a shared musician-reader helper: see the simplify skip; the
pairing contract is owned in SlotValueHelper's doc; revisit at a 9th site),
F5 (documented above as the kana-canonical residual).

RAW-PATH CONTRACT: canonical null reduces every expression to its pre-change
value at every site (the Latin matrix and the simulator raw path are
byte-identical; the full suite green on both TFMs is the regression proof).

VERIFICATION TAIL: dotnet build 0 errors 0 warnings (both TFMs, final state);
dotnet test Jellyfin.Plugin.AlexaSkill.Tests 4664/4664 net9.0 and 4664/4664
net10.0 (baseline 4654; +10 net-new: 3 MusicianErCanonicalTests, 7
SlotValueHelperTests GetCanonicalValue additions). No interaction-model,
locale-string, or config changes, so the model/locale validators are unaffected
and no NLU fixtures moved. Orchestrator probes unchanged from the dispatch:
profile-nlu and the simulator raw path should be unchanged; the device ER probe
is Paolo's.

2026-09-28 gate-marker review round 2 (six findings, ALL applied as one
review commit; it SUPERSEDES the round-1 F1 shape: the JF-492 title retry no
longer merely keeps the raw value, it SKIPS entirely when the slot is
ER-resolved):

F1+F2 (the correctness pair): the F1 evidence rule was applied inconsistently
on the stale-catalog miss path. The raw-keep alone was vacuous in the 8
catalog-typed locales (raw == canonical modulo case, and the case-insensitive
SearchTerm erases the difference), so a stale-catalog artist miss could still
auto-play an unrelated album literally titled like the artist; and the
canonical was fed to CrossMedia.TrySongFallback at PlayArtistSongs and
QueryArtistLibrary, guessing an ER-resolved ARTIST name as a song TITLE (the
same wrong-play class; pre-change the romanized raw missed and the honest
not-found answered). BOTH title guesses now SKIP when the slot is
ER-resolved: the honest not-found answers. Pinned by
PlayAlbum_ErResolvedMusician_ArtistMiss_SkipsTitleRetry_HonestNotFound (a
bait album titled 'Queen' is mock-available; zero album queries are issued)
and QueryArtistLibrary_ErResolvedMusician_ArtistMiss_SkipsSongFallback_
HonestNotFound (a bait song titled 'Queen' at score 95 over the 65 bar; no
play). The kana-gated TrySongFallback copy inside ApplyKanaOriginAcceptance
needed no change (unreachable with a canonical by the flag invariant).

F3 (doc accuracy): SlotValueHelper's pairing doc and IsKanaOriginQuery's doc
claimed 'the canonical is a Latin library name by construction', false for a
kana-named library (the round-1 F5 residual admitted it). Both now state the
real invariant: the canonical feeds the search VERBATIM regardless of script
(a kana canonical exact-self-matches its own library name; a future defensive
Romanize(canonical) would destroy that hit), and ER resolution, not
Latinity, is what makes the JF-652 bar inert.

F4 (coverage): the two pins above, plus BOTH FindSong conditional legs
(first invocation, and AwaitingArtist through the session-attributes
overload): raw 'zzzqqq' + canonical 'Queen' resolves the artist (the
elicited session data carries the resolved artist id), proving the canonical
drove the search on each leg. The non-musician (transcript/anySlot) legs are
structurally guarded by the same ternary and resolve nothing canonical.

F5 (one definition): CrossMediaFallback's third kana-origin computation
migrated to ArtistSearch.IsKanaOriginQuery(null, slotText); the ONE-
definition doc claim is now true at every site.

F6 (test helper): TestHelpers.ResolvedSlot parameterized on the slot name
(default 'musician') so its generic-builder doc is honest.

VERIFICATION TAIL (round 2): build 0 errors 0 warnings both TFMs; suite
4668/4668 net9.0 and 4668/4668 net10.0 (round-1 state 4664 + 4 new
MusicianErCanonicalTests pins: the PlayAlbum retry-skip, the QueryArtistLibrary
fallback-skip, the two FindSong legs). One net10.0 leg of the first round-2
run stalled (36m duration, 4667/4668, the failing test name lost to the
output filter) and the immediate rerun of the SAME binary passed 4668/4668
in a normal 6m23s: a transient host-contention stall (the box was at 25G/31G
memory after three back-to-back suite runs), not a code failure; flagged here
rather than silently dropped.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Landed 2026-09-28 as merges 0d460851 + the marker-pass commit 48598cbd (pushed; deployed with the full checklist, config intact; post-deploy probes green: the raw simulator path keeps the JF-652 honest tie ask, the genre control plays, the Latin control plays Queen directly): the musician ER-canonical read. The genre-canonical pattern applied at all 7 musician-slot handlers (8 read sites, the grep-verified roster): canonical feeds the search verbatim (the ER-resolution invariant makes the kana bar inert; a kana canonical exact-self-matches its own name and the docs say so), raw keeps speech and session (the JF-642 F-1 class respected at every NotFound site). Two code-review rounds: the marker pass caught the title-guessing inconsistency (an ER-resolved artist guessed as a song/album TITLE on the stale-catalog miss path) - both the JF-492 album retry and TrySongFallback now SKIP when the canonical is present, bait-pinned against same-titled items; the ER coverage extended to PlayAlbum, QueryArtistLibrary, and both FindSong legs; one kana-origin definition everywhere (the genre gate's inline conjuncts routed through it in the marker simplify); ResolvedSlot parameterized with its doc claim corrected. Gates: /simplify (worker + the completion-gate marker pass, findings applied or reverted-with-reason) and code-review (worker + the skill marker in the orchestrator transcript, 6 findings applied), suites 4668/4668 both TFMs (orchestrator-verified; the net10 transient dispositioned as host contention by the clean rerun); the marker-pass targeted battery 41/41. Live ja flow now: catalog ER selects the artist intent (JF-646) AND the resolved canonical plays directly on a real device; the simulator raw path honestly asks on ties. The device ER probe is Paolo's.
<!-- SECTION:FINAL_SUMMARY:END -->
