---
id: JF-642
title: >-
  JF-642 - ja-JP noun-qualified artist carriers stolen by PlayByGenre (the
  free-text genre capture): even canonical pre-existing forms route to genre,
  artist intent unreachable by noun voice
status: Done
assignee: []
created_date: '2026-09-27 06:19'
updated_date: '2026-09-27 19:11'
labels:
  - nlu
  - ja-JP
  - device-found
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-27 from the JF-399 residue-pass live verification (same-turn rule).

LIVE EVIDENCE (profile-nlu on the live rebuilt model, two artists probed): ja-JP noun-qualified artist carriers are broadly stolen by PlayByGenreIntent - 'クイーン の曲を再生して' -> PlayByGenre{genre:'クイーン'}, 'ビートルズ の曲を再生して' -> PlayByGenre, 'ビートルズ の音楽を聴かせて' -> PlayByGenre. Even the CANONICAL pre-existing carrier ('{musician} の曲を再生して', present since before this pass) is stolen.

INVESTIGATION COMPLETE (2026-09-27, same night; all probes on the live model + minix simulator):

1. Model layer (all 17 locales enumerated): genre = AMAZON.Genre (built-in free-text) in 16 locales, AMAZON.SearchQuery in it-IT. Carrier-skeleton collisions with PlayArtistSongs exist in 15/17 locales (only fr-FR/fr-CA clean) - the latent shape is repo-wide, not ja-only.
2. Selection-level steal observed ONLY in ja-JP. Controls (profile-nlu, colliding carrier + artist name): en-US and hi-IN select PlayArtistSongsIntent (catalog-backed musician ER wins); es-MX, pt-BR, de-DE select PlaySongIntent with genre merely considered (benign: the cross-media artist fallback rescues). ja selects PlayByGenre every time, and 'ジャズ の曲を再生して' returned an EMPTY consideredIntents list - the artist intent is not even competing.
3. The ja steal is CARRIER-INDEPENDENT: 'クイーン のトラックを再生して' (a carrier PlayByGenre does not have) ALSO selected PlayByGenre{genre:'クイーン'} via slot-spanning. Carrier re-qualification alone cannot fix this; the free-text built-in slot swallows a prefix out of any shape.
4. The ja steal is SCRIPT-DEPENDENT: Latin 'queen の曲を再生して' routes correctly to PlayArtistSongsIntent{musician:'queen'} (AMAZON.Musician recognizes Latin names and wins selection); katakana loses. Japanese ASR transcribes foreign artist names as katakana, so the naturalized on-device form is the broken one.
5. End-to-end severity (minix simulator, ja-JP): genre='クイーン' -> NotFoundGenre (the JF-463 cross-media rescue FIRES but cannot cross the script gap); genre='ジャズ' -> NotFoundGenre (native-script genre values also fail: raw katakana never matches the library's Latin 'Jazz'); genre='Jazz' (Latin) -> plays a jazz track. PlayArtistSongs musician='クイーン' -> artist not-found. Net: katakana artist voice and katakana genre voice are BOTH broken end-to-end in ja; Latin transcriptions work.
6. Fix proof already live in the same probes: PlayMoodMusicIntent's custom Mood type considered クイーン and its authority answered ER_SUCCESS_NO_MATCH. A custom type cannot steal; a built-in free-text one does.

FIX DIRECTION (routing layer): convert the genre slot to a custom slot type with localized genre vocabulary - the JF-354/356 Mood precedent, applied to genre. Open scope decision for the worker: ja-only (divergence precedent: it-IT AlbumName, anti-pattern #10 note) vs all-17 (the collision skeleton is latent everywhere; en/it survive only via catalog-backed musician ER). Constraint: the handler reads the RAW slot value and passes it straight to Jellyfin's Genres filter, so a custom type needs either canonical values = library genre names with spoken synonyms, or a localized genre map in the handler (LocalizedMoodMap pattern).

COMPANION (separate mechanism, separate task JF-643): the search-layer script gap - katakana query values cannot fuzzy/phonetic-match Latin library names on ANY path (artist search, genre query, cross-media fallback; simulator-verified). Fixing routing alone still leaves naturalized ja artist voice at not-found.

PRIORITY: medium. ja-JP artist intent is effectively unreachable by naturalized voice today; genre queries in native script are equally dead.
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
2026-09-27 orchestrator simplify round SUPERSEDES two earlier holds: the two sibling genre slots (PlayRandom, PlayByDecade) now read the ER canonical at their Genres feeds (raw kept for speech, the code-review F-1 fix), so the F4 hold ("siblings unwired") and the template RESIDUAL paragraph from the worker round no longer describe the code. What remains unwired is the JF-643 kana resolution TIER on the siblings (the JF-645 residual, already tracked).

DESIGN SETTLED by the orchestrator 2026-09-27 (post-investigation, pre-dispatch; dispatch waits for JF-643 to merge because both touch PlayByGenreIntentHandler):

SCOPE: ja-JP ONLY. The steal is selection-broken only in ja; the other 16 locales have working discriminators (catalog ER in en/it; PlaySong selection + cross-media fallback in es/pt/de). Converting all 17 in one change risks genre-recall regressions in 16 healthy locales and needs 17 vocabularies. Per-locale type divergence has precedent (it-IT AlbumName, anti-pattern #10 note). The all-17 latent collision stays documented in this task as the follow-up decision.

THE CHANGE (Mood/JF-354 mechanics applied to genre):
1. templates/ja-JP.yaml: PlayByGenreIntent's genre slot type AMAZON.Genre -> new custom type (name: GenreType). Values ~20-24, canonical = library-friendly Latin genre names (Rock, Pop, Jazz, Classical, Hip Hop, Rap, Electronic, Dance, Folk, Country, Metal, Punk, Reggae, Blues, Soul, Funk, Latin, R&B, Soundtrack, J-Pop, Anime, Enka); synonyms carry the katakana spoken forms (ロック, ジャズ, クラシック...) plus romaji variants, so NLU-side ER resolves ジャズ -> canonical Jazz. Regenerate model_ja-JP.json via the generator. Document the divergence in the template header (the per-locale header convention).
2. PlayByGenreIntentHandler: on ER_SUCCESS_MATCH read the canonical value (the BrowseLibraryIntentHandler.GetCanonicalSlotValue pattern), raw value as fallback. Composes with JF-643's query normalization: canonical feeds the Genres query directly; residual raw katakana goes to JF-643's vocabulary-resolution tier (bare romanization alone does NOT clear the server-side exact Genres equality: jazu does not equal Jazz; the TIER re-queries with the canonical tag).
3. Validator gate: run scripts/validate_interaction_models.py. If the cross-locale type divergence (16x AMAZON.Genre vs ja GenreType) raises an ERROR, STOP and report back (a validator exception/decision is needed first); a warning is acceptable with the divergence documented.
4. NLU fixtures: tests/integration/fixtures/ja-JP.yaml genre rows + katakana artist carrier rows updated to the expected post-fix routing.

ORCHESTRATOR VERIFICATION BATTERY (post-merge, post-deploy): profile-nlu ja: クイーン の曲を再生して -> PlayArtistSongsIntent (steal gone); クイーン のトラックを再生して -> PlayArtistSongsIntent; ジャズ の曲を再生して -> PlayByGenreIntent with ER canonical Jazz; queen の曲を再生して -> PlayArtistSongsIntent unchanged; then minix simulator: genre=ジャズ plays jazz. Failure of any row = the fix does not ship.

2026-09-27 orchestrator: investigation phase closed (evidence in Description); design settled as ja-JP-only custom GenreType (plan section); dispatch sequenced after JF-643 merges (shared handler file). CI-red incident on main fixed in passing (14a84a40: xUnit1013 IDisposable one-word).

## Implementation Notes (worker, 2026-09-27, landed on the worktree branch)

EXECUTED per the settled design, one mechanical extension the validator forced:
the within-locale slot-type consistency rule (same slot name = same type per
locale, validator ERROR) required converting ALL THREE ja genre slots
(PlayByGenre, PlayRandom, PlayByDecade), not PlayByGenre alone. The handler
change stays scoped to PlayByGenreIntentHandler as designed; the two sibling
handlers keep raw-value reads (the JF-643 residual list + the template header
RESIDUAL note record it).

1. templates/ja-JP.yaml: genre slot type AMAZON.Genre -> GenreType on the three
   intents; new GenreType block appended after AudiobookTitle; header carries
   the divergence note (ja-only type, the it-IT AlbumName precedent). Model
   regenerated via the generator; regen-equality byte-identical; no samples
   changed, so VOICE_COMMANDS.md and the docs mirrors are untouched (verified:
   the voice-reference generator run produced no diff).
2. PlayByGenreIntentHandler: canonicalGenre = SlotValueHelper.GetCanonicalValue
   (ER_SUCCESS_MATCH canonical, raw fallback) read at slot extraction; the
   canonical feeds Romanize -> the exact Genres query; the JF-643 kana tier
   gate gained canonicalGenre == null (an ER-resolved query is exact by
   construction; the interplay note's documented bypass). Speech and the
   JF-463 artist fallback stay raw-keyed. The GetCanonicalSlotValue pattern
   was hoisted into Alexa/Util/SlotValueHelper.GetCanonicalValue (shared with
   BrowseLibraryIntentHandler; the fourth-copy decision JF-637 deferred, made
   here as 'hoist'); it also rejects empty/whitespace canonicals (review F5).
3. VALIDATOR OUTCOME (the gate): PASS, no ERROR. 294 warnings vs the 278
   baseline; the +16 are exactly one 'Missing slot type present in other
   locales: GenreType' warning per non-ja locale (the documented divergence,
   warning-level per the design's acceptance). validate_locales: PASS, no new
   gaps. validate_versions: PASS (1.0.0.0).
4. tests/integration/fixtures/ja-JP.yaml: header divergence paragraph updated
   (DIVERGENCE CLOSED note) + 4 rows matching the orchestrator battery:
   クイーン の曲を再生して -> PlayArtistSongsIntent, クイーン のトラックを再生して
   -> PlayArtistSongsIntent, queen の曲を再生して -> PlayArtistSongsIntent (control),
   ジャズ の曲を再生して -> PlayByGenreIntent genre filled. Marked 'requires the
   model deploy; first live suite run is the probe' (the JF-399 precedent).

VOCABULARY AS LANDED (22 values; canonical = Latin library genre name,
synonyms = katakana spoken forms + romaji variants derived from
KatakanaRomanizer's Hepburn syllable values, word boundaries spaced as spoken):
Rock [ロック, rokku]; Pop [ポップ, poppu]; Jazz [ジャズ, jazu]; Classical
[クラシック, kurashikku]; Hip Hop [ヒップホップ, hippu hoppu]; Rap [ラップ, rappu];
Electronic [エレクトロニック, エレクトロ, erekutoronikku, erekutoro]; Dance [ダンス,
dansu]; Folk [フォーク, foku]; Country [カントリー, kantori]; Metal [メタル, metaru,
ヘヴィメタル, ヘビーメタル]; Punk [パンク, panku]; Reggae [レゲエ, regee]; Blues
[ブルース, burusu]; Soul [ソウル, souru]; Funk [ファンク, fanku]; Latin [ラテン,
raten]; R&B [アールアンドビー, リズムアンドブルース]; Soundtrack [サウンドトラック,
サントラ]; J-Pop [ジェイポップ, jeipoppu]; Anime [アニメ]; Enka [演歌]. All value and
synonym strings <= 14 chars (140 cap), fullwidth katakana only (no halfwidth
kana, no fullwidth alphanumerics, no U+3000; machine-checked).

VERIFICATION (this worktree): dotnet build 0 warnings 0 errors both TFMs;
dotnet test 'Passed! - Failed: 0, Passed: 4498, Skipped: 0, Total: 4498' on
BOTH net9.0 and net10.0 (baseline 4496 + 2 new JF-642 tests:
HandleAsync_ErMatchedGenre_FeedsCanonicalToQuery_SingleExactQuery_JF642 pins
the canonical-only query sequence + tier never firing;
HandleAsync_ErMatchedGenre_TagMissingFromLibrary_SkipsKanaTier_JF642 pins the
bypass on tag-miss with raw-value speech). NLU dry-run: 8 passed, 1132
skipped (fixtures valid).

GATES: /simplify four angles (reuse/simplification/efficiency/altitude
dispatched): applied the SlotValueHelper hoist (3 angles converged), the
DispatchGenreFlow vocabulary-hook reuse in the new tests, the duplicated
tier-rationale comment merge, the template block-comment trim + header
RESIDUAL clause; skipped the request-builder merge (per-class builders are
the suite idiom, verified by the simplification angle). Efficiency angle:
nothing measurable added; the ja hot path is a net work REDUCTION (ER-matched
requests run one exact query instead of miss + vocabulary scan + re-query).
code-review high: 6 findings; APPLIED F5 (empty/whitespace canonical guard in
SlotValueHelper.GetCanonicalValue) and F6 (CLAUDE.md ER-pattern pointer
updated to the shared helper; BrowseLibrary's private copy is gone). HELD
with reasons: F1 (unconditional ER-canonical read in the 16 AMAZON.Genre
locales; any delta requires Amazon's canonical to differ substantively from
the spoken text while the spoken text matched the tag; recommend an en-US
genre battery probe, e.g. 'play jazz music', to close it live), F2 (the
canonical-miss tier bypass IS the interplay note's documented semantics;
named residual: a library whose tags differ in spelling from the canonicals,
e.g. 'Hip-Hop' vs canonical 'Hip Hop', not-founds on the ER path; one-line
follow-up if wanted: drop the canonicalGenre == null term and pass the
romanized raw to the tier), F3 (long-tail genre recall: an unmatched custom
value still fills the slot with raw text via ER_SUCCESS_NO_MATCH, the
Mood-クイーン evidence in this task's Description; recommend a ボサノバ
long-tail battery probe before trusting it end-to-end), F4 (PlayRandom /
PlayByDecade unwired: the settled scope + documented residual).

BATTERY-RISK ROWS for the orchestrator's live verification: (a) the en-US
AMAZON.Genre ER shape (F1); (b) a long-tail ja genre utterance, e.g. ボサノバ
の音楽を再生して, still routing to PlayByGenre with the slot filled (F3);
(c) a tag-variant library genre (e.g. tag 'Hip-Hop') spoken as ヒップホップ
known to not-found on the ER path by design (F2); (d) the wrapper battery
(nlu_wrapper_battery.py) for the ja one-shot forms, per the interaction-model
hook reminder.

2026-09-27 LIVE BATTERY OUTCOME (deployed d4ba78fd, ja-JP model rebuilt SUCCEEDED, live model verified carrying GenreType on all 3 slots): the GENRE half works - ジャズ routes PlayByGenre with ER canonical (the tier bypass saves the round trip), ロック routes genre, Latin queen unchanged, and the en-US probe (play jazz music -> PlayRandom genre=jazz with NO resolution) supports the unconditional-read design assumption. THE HEADLINE STEAL ROW FAILED: クイーン の曲を再生して selects PlayByGenre genre=クイーン deterministically (3/3 probes) and ビートルズ の曲を再生して likewise - the Mood-precedent premise (a restricted custom type cannot steal) is REFUTED at NLU selection: custom slot types are OPEN for filling; the type changes resolution, not selection. The end-to-end user outcome still lands through the handler chain (raw kana -> JF-463 fallback -> JF-643 romanizer), with the JF-652 precision caveat. The routing-layer steal fix is therefore JF-646 (catalog-side katakana synonyms so AMAZON.Musician wins selection via ER, the en-US/hi-IN shape), priority raised there. This task's shipped value stands: genre canonicalization, the divergence guard, the sibling canonical reads, and the refutation itself (which redirects the steal fix to the catalog layer before more model-layer work was spent).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed by the 2026-09-27 backlog audit (tree-verified): the work shipped as merge d4ba78fd and is live in tree (GenreType on all three ja genre slots, ER-canonical reads at all three handlers, SlotValueHelper.GetCanonicalValue shared, CLAUDE.md anti-pattern #10 guard). The battery outcome is recorded honestly in the notes: the genre half works (ER canonical, tier bypass, sibling reads); the steal premise (a custom type prevents NLU selection steals) was REFUTED live and the steal fix redirected to JF-646 (catalog-side katakana synonyms, now high priority); the long-tail tier residual lives in JF-645. Nothing further is owned here.
<!-- SECTION:FINAL_SUMMARY:END -->
