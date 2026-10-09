---
id: JF-806
title: >-
  JF-806 - the song, artist, and playlist confirm legs lack the music-disabled gate
  (the confirm-must-match-ask gap left open while books, podcasts, and albums gated)
status: Done
assignee: []
created_date: '2026-10-07'
labels:
  - tech-debt
  - playback
dependencies:
  - JF-805
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/YesIntentHandler.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-805 round (2026-10-07), same-turn per the
review-recommendation rule.

YesIntentHandler's disambiguation confirm legs are gated inconsistently: the
book leg gates BooksEnabled (JF-611, adopted in JF-795), the podcast leg gates
PodcastsEnabled (JF-611), and the MusicAlbum leg gates MusicEnabled (JF-805),
but the SONG, ARTIST, and PLAYLIST switch arms still launch with no gate at
all. CORRECTED BY THE JF-805/806 GATE-MARKERS - the playlist ask does NOT gate (read the trailing corrections before any work here); the original premise read: Their direct asks all gate at entry under JF-467 (PlaySong,
PlayArtistSongs, PlayPlaylist via IfMediaTypeDisabled(c => c.MusicEnabled)),
so a "yes" on a song/artist/playlist prompt that was opened before an admin
disabled music launches media the direct ask would refuse, answering
differently from the ask on the disabled axis, the exact class JF-611/JF-795/
JF-805 closed for the other three media types.

FIX SHAPE: the same three-line IfMediaTypeDisabled(c => c.MusicEnabled,
request) block the JF-805 MusicAlbum branch carries, applied to the three
ungated switch arms (or folded into whatever shared confirm-leg seam a future
round extracts; the JF-805 altitude review noted three such gate blocks now
exist in HandleAsync and a Task-returning gate wrapper could own them once).
Playlists are cross-type by content but the PlayPlaylist ask gates on music
today, so the confirm should match whatever the ask answers; verify the ask's
gate before copying it. Pin per leg the same way
HandleAsync_DisambiguationAlbumType_MusicAlbumConfirm_MusicDisabled_
AnswersMediaTypeNotAvailable pins the album leg.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no new session-attribute shapes; the gates answer through existing refusal Tells and never write attributes)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient changes)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no interaction model change; the constraint "no locale/model/speech changes" held, all gates reuse existing strings)
- [x] #7 E2E test added for new intent or handler logic (N/A: no new intent or handler surface; the confirm-leg gate behavior is pinned at unit level, the JF-805 worker-split precedent)
- [x] #8 Locale response strings added to all 17 locales (N/A: reuses SkillWarmingUp via the pipeline translation, MediaTypeNotAvailable, and FeatureDisabled verbatim)
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Final Summary

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

Shipped 2026-10-07 on the JF-806 worktree (commits 9b369cde..031ef164, NOT
merged: the orchestrator merges). Suites: 5452/5452 BOTH TFMs at the final
state (baseline 5439 + 13 new pins); Release -warnaserror build 0 warnings /
0 errors.

AXIS 1 (the music gate, the task's core, rescoped per the JF-805 marker):
the YesIntent MediaTypeSong and MediaTypeArtist arms hoisted out of the
residual switch into leg blocks gated by the byte-identical JF-467 call
their asks pay (IfMediaTypeDisabled(c => c.MusicEnabled, request);
PlaySong gates at its line 143, PlayArtistSongs at its line 195, both read
and verified). The playlist arm stays ungated: the rescope was re-verified
this round (zero IfMediaTypeDisabled/IfFeatureDisabled/GuardIndexReady/
MusicEnabled hits in PlayPlaylistIntentHandler, ShufflePlayIntentHandler,
and BuildPlaylistPlayResponseAsync; playlists are cross-type always-allowed),
so gating only the confirm would CREATE the divergence the rule prohibits.

AXIS 2 (the warming gate, folded from the marker): GuardIndexReady(
_artistIndex) on the collection-fetch confirm legs: the book leg, the
MusicAlbum routed composition, and the artist arm before its unpaged
whole-catalog ArtistIds query, plus the belt-and-braces F3 extension onto
the defensive PlayAlbum arm (a future producer cannot re-open either axis).
The artist index stands in for the shared cold database (the PlayAlbum
Layer-1 coarse precedent). YesIntentHandler added to
WarmingGateCoverageTests.ExpectedGatedHandlers (the IL roster scan
discovers the gates; both directions green).

WARMING-PLACEMENT DECISION (recorded as instructed): the gates sit at the
LEG dispatch inside HandleAsync, not at method entry, because the resume
confirmation and pagination continuation run earlier and are point-lookup
shapes (one GetItemById plus the launch build; the pagination page rides
the SAME shared helper as the ungated ShowMoreIntent twin). Per-leg gate
ORDER matches each ask's observable answer in the warming+disabled
intersection: book leg books-gate-then-warming (the ungated ask answers
FeatureDisabled), album leg warming-then-music (PlayAlbum answers
SkillWarmingUp), artist leg music-then-warming (PlayArtistSongs answers
MediaTypeNotAvailable). The song/video/podcast/playlist legs stay
warming-ungated by the database-surface criterion (point lookups or
ungated asks), each recorded in a comment and locked by transparency pins
(song, video, resume, pagination); the song leg's bounded intersection
divergence from its ask is documented in its comment (code-review F3).

RED PROOFS (unmodified tree, both TFMs, failure messages recorded in the
test commit): axis 1: the song and artist confirms LAUNCHED with music
disabled ("the disabled Tell must carry no directives" on both pins);
axis 2: the album, book, and artist confirms ran their cold-DB compositions
with no refusal ("Assert.Throws() Failure: No exception was thrown",
Expected SkillWarmingUpException, on all three pins). Post-fix all green.

GATES: /simplify (4 parallel agents; 1 applied: CreateSingleMatchAttrs now
delegates to the pre-existing CreateDisambiguationAttrs; 3 reasoned skips
recorded: the disabled-Tell assertion block at N=2 with a third occurrence
outside the diff, the codebase-wide c => c.MusicEnabled idiom, and the
per-file private warming/ready mock factories). /code-review high (5
findings: F2 applied as the three gate-ORDER intersection pins; F3 and F5
applied as documented tradeoffs in the gate comments; F4 applied as the
video/resume/pagination decision comments plus two transparency pins; F1
filed same-turn as JF-808). The /simplify altitude agent's out-of-scope
observation filed same-turn as JF-807 (the book ask carries no Layer-1
warming gate while its confirm now does).

FILINGS: JF-807 (the book ask's missing Layer-1 warming gate), JF-808 (the
playlist play path warming-ungated end to end). Next free numbers verified
against backlog/tasks before each filing (807 and 808 both free).

JF-805 GATE-MARKER CORRECTIONS (2026-10-07, same-turn): (1) PREMISE FIX - the playlist leg's premise is WRONG: PlayPlaylistIntentHandler does NOT gate at entry on MusicEnabled (read end to end; zero IfMediaTypeDisabled/MusicEnabled hits in it, ShufflePlayIntentHandler, and BuildPlaylistPlayResponseAsync), and playlists are documented cross-type always-allowed (BaseHandler.IsTypeAllowed). Gating only the playlist CONFIRM arm would CREATE the confirm-vs-ask divergence the rule prohibits. RESCOPE: song+artist legs only (their asks do gate); the playlist leg needs the ask gated first if ever, as its own decision. (2) FOLDED AXIS (marker finding 2): the confirm path runs the ask's full cold-database surface with NO warming gate - the direct asks are Layer-1 gated (GuardIndexReady; WarmingGateCoverageTests), YesIntentHandler is absent from ExpectedGatedHandlers, and JF-805's routed composition widened the ungated work to the JF-796 deep fetch plus per-track UserData reads; the restart-mid-session confirm hits the cold DB inside the Alexa window (the JF-419 class). The confirm-leg seam round this task anticipates should carry the warming-gate axis for BOTH the book and album confirm legs (the JF-795 twin shares the shape).

GATE-MARKER TAIL (2026-10-07): the closure claim that every warming-ungated leg 'recorded its decision in a comment' overstated for podcast and playlist - both legs NOW carry their in-code decision records (applied this tail); the song leg additionally gained its intersection pin (the documented F3 bounded divergence is now machine-locked like its siblings).
<!-- SECTION:NOTES:END -->
