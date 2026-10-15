---
id: JF-890
title: >-
  JF-893: sibling idle-guard families (FavoriteToggle, MediaInfo, PlaylistEdit)
  still act on stale-audio residue; adopt or document the halt-report-aware
  bound
status: Done
assignee: []
created_date: '2026-10-15 17:30'
updated_date: '2026-10-15 19:01'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
<!-- SECTION:DESCRIPTION:BEGIN -->
Found by the JF-892 simplify/altitude review (2026-10-15). The JF-892 doctrine (an explicit halt report with no corroborating live evidence withdraws the frozen AudioPlayer token as liveness proof; see PlaybackLaunchBuilder.IsStaleAudioPlaybackResidue, consumed by the ONE loop/shuffle write-family gate ProgressReporter.TryAnswerModeWriteMediumGate) is scoped to the write family only. The OTHER HasCurrentPlaybackEvidence-guarded families still act on the same stale-audio-residue shape: on an idle device after music (frozen song token, playerActivity=STOPPED, empty session, Audio-routed ledger) the guard's TOKEN leg passes (PlaybackLaunchBuilder.HasCurrentPlaybackEvidence treats any token as evidence) and ResolveCurrentPlayingItem's token leg resolves the dead item, so FavoriteToggle favorites it, MediaInfo reports it as now-playing, and PlaylistEdit mutates the queue around it. Verified from source: FavoriteToggleIntentHandler.cs:96 (guard) then :104 (resolver call); the same structure in MediaInfoIntentHandler.cs:97 and PlaylistEditHandlerBase.cs:174. Unlike JF-890's video residual this one is not recorded as an accepted residual anywhere. Decide per family: adopt the halt-report-aware evidence leg (with its own red proofs per family; the JF-789 recency fold was evaluated and rejected on this predicate's doc, but the halt-report fold never was) or document it as a deliberate accepted residual. Do NOT fold it into the write-family gate; these families do not call that gate.
<!-- SECTION:DESCRIPTION:END -->

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
JF-893 decision record (2026-10-15): DOCUMENT for all three families, decided from the code paths, not from the review hint. The load-bearing observation: the stale-residue shape (explicit halt report, frozen token, empty session, flag off) IS the just-stopped shape. An idle device seconds after "stop" reports exactly STOPPED over the frozen token (the JF-890/JF-892 frozen-token doctrine), so adopting the halt-report bound per family refuses the family's just-stopped interaction, which is each family's canonical use.

- FavoriteToggle: DOCUMENT. Adopting would regress the exact JF-629 fix (favorite-after-PlaybackStopped answering not-found was the bug JF-629 repaired; the halt-reported token is the referent "questa" resolves to right after the song ends). The stale action is an IsFavorite write on the last-heard item: benign, reversible by the twin intent, and no by-name favorite intent exists as an alternate route. Site comment at the guard in FavoriteToggleIntentHandler.cs; pin HandleAsync_HaltReportedFrozenToken_StillFavorites_JF893 (theory over STOPPED/FINISHED/IDLE).
- MediaInfo: DOCUMENT. The action is read-only (speech plus card, no writes); the halt-reported token is the only handle for the just-finished "what was that song" ask, which adopting would answer NoMediaPlaying to zero safety gain; the cold-device shape (no token at all) already answers NoMediaPlaying. Tolerated residual: the present-tense NowPlaying phrasing over a halted report. Site comment at the guard in MediaInfoIntentHandler.cs; pin Handle_HaltReportedFrozenToken_StillReportsTrack_JF893.
- PlaylistEdit: DOCUMENT. The task's corruption premise dissolves at the code paths: neither write leg touches the device queue or any ordering invariant. AddCurrent calls AddItemToPlaylistAsync (a Jellyfin playlist add of a real, resolvable library item); RemoveCurrent calls RemoveItemFromPlaylistAsync. Both are item-scoped curation, both confirmations NAME the item so a misfire is announced and reversible (the same token referent re-adds). Server-semantics caveat (tag-verified at Emby.Server.Implementations/Playlists/PlaylistManager.cs during the JF-893 code review): a duplicate add is absorbed on Jellyfin 10.11.x (existingIds filter) but plain-appends on 12.x, and a remove strips every matching entry on both lines, so repeated idle misfires can stack copies on 12.x (minix runs 12.1.0); accepted at LOW severity since each copy is named in its confirmation and one remove clears them all. The just-stopped "add this to playlist X" is the canonical use of AddCurrent, and the remove twin has NO by-name alternate intent (AddSongToPlaylist covers adds only), so wiring the bound here would strand a just-stopped intentional remove with no voice recovery: the JF-876/JF-882 alternate-route rule. Rationale at the ResolveCurrentItem XML doc in PlaylistEditHandlerBase.cs (the family's established rationale layer); pins AddCurrent_HaltReportedFrozenToken_StillAdds_JF893 and RemoveCurrent_HaltReportedFrozenToken_StillRemoves_JF893.

Doctrine boundary (canonical home: the HasCurrentPlaybackEvidence doc, one JF-893 BOUNDARY FACT paragraph; each site comment carries a pointer plus its per-family rationale): the halt report withdraws the frozen token as proof that something is PLAYING (the JF-890/JF-892 read the loop/shuffle write family adopted because their writes are playback-state-scoped and corrupting once halted), but it remains the referent for item-scoped actions on "the item this device last played". The write-family gate and IsStaleAudioPlaybackResidue are untouched (JF-892 owns them).

Recency-bounded middle ground, evaluated and REJECTED (JF-893 code review finding 2; recorded here so the next family deciding at this door inherits the full option space): refuse residue only when the ledger stamp is aged (DeviceQueueManager.IsWithinLastPlayedRecencyWindow, the JF-789 machinery) would keep the just-stopped interaction while refusing the days-idle stray utterance. Rejected because (a) the stamp measures LAUNCH age, not HALT age: nothing clears it when playback ends (the JF-789 step 5 recorded lesson), so an aged-stamp refusal also refuses the just-stopped ask after any session longer than the window (a 3-hour queue session stopped five minutes ago), reintroducing the exact JF-629 bug class for long sessions; (b) splitting correctly needs a HALT-age signal (a timestamp written when the JF-655 flag is cleared), new event-side state plus a new predicate leg; (c) the long-idle harm is bounded (announced, reversible writes; MediaInfo writes nothing), so at LOW severity the new state does not pay for itself. If a future device round shows real harm from long-idle stray utterances, the halt-age timestamp is the shape to build.

Red proof: the four pins were run against a scratch adopt (IsStaleAudioPlaybackResidue wired into all three guards) and went 12/12 RED, then the scratch was reverted and the suite went green: the pins genuinely exercise the residue path and a future adoption cannot land silently. Each pin also carries a shape-guard assertion (Assert.True(IsStaleAudioPlaybackResidue(...)), JF-893 code review finding 3) so the guarantee survives predicate-leg drift (e.g. RecordLastPlayed someday arming the flag): the pin reddens itself instead of silently vacating. Full suite 6000/6000 on net9.0 and net10.0 (build 0 warnings).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
EXECUTED on branch jf893 (86b35440), merged to main 6f2185dd, pushed. Decision: DOCUMENT for all three families, decided from the code paths (the review hint's split was overridden): the stale-residue shape IS the just-stopped shape, so adopting the JF-892 bound would refuse each family's canonical just-stopped interaction (FavoriteToggle would regress the JF-629 fix; MediaInfo would lose the only handle for the just-finished ask; PlaylistEdit's remove twin has no by-name alternate - the JF-876/JF-882 rule). The task's queue-corruption premise dissolved at the code paths (Jellyfin-playlist writes only, named and reversible; tag-verified caveat: duplicates dedupe on 10.11.x, plain-append on 12.x, remove strips on both; the false AddCurrentToPlaylist class doc corrected). Deliverables: accepted-residual comments at the three guard sites, the canonical JF-893 BOUNDARY FACT on HasCurrentPlaybackEvidence's doc (a fourth family must decide, not inherit), four shape-class pins with shape-guard asserts. Red-proof: 12/12 RED under a scratch adopt, green reverted. The recency middle ground evaluated and rejected (ledger stamp = launch age, not halt age). Gates: /simplify (4 agents) + /code-review high (3 applied, confirmed back). Tests 6000/6000 BOTH TFMs; build 0/0. No deploy needed (comments + tests only; the corrected class doc rides the next batch).
<!-- SECTION:FINAL_SUMMARY:END -->
