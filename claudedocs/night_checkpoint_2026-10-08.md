# Night checkpoint 2026-10-08 (late): orchestrator state

Maintainer asleep; standing night mandate (work all night, cron re-armed,
session-only, every 15 min). Post-compaction re-entry: read THIS first, then the
git log.

## Landed and deployed tonight (all merged to main, pushed, live on minix build 436b35b9)

- JF-817 (audiobook concat prewrite windowing) + JF-819 (single-item prewrite
  windowing): the JF-778 live-edge death family closed on ALL THREE prewrite
  families. Deployed ~23:52 (net10.0 DLL, md5 8c111128a8d8b57443de7fafe425a90e,
  active verified, users=1, route smoke 400 OK).
- JF-788 wording + JF-816 residual (AudiobookTitle seed) merged earlier tonight;
  JF-788's DLL wording change rode tonight's deploy.
- Gate-marker tail for JF-819: floor pins strengthened (floor <= L, first-fetch
  empty-dir worst head), first-fetch pin gained read-funnel discriminator,
  JF-780 scope extended to the single-item family + single-file-book probe,
  JF-818 gained the per-poll drop-log noise item.
- Full suite on merged tree: 5526/5526 BOTH TFMs; Release -warnaserror 0/0.

## In flight (three parallel workers, disjoint surfaces)

0. CLOSED since this checkpoint was written: JF-821 (merged d7494986 + tail,
   pushed; the direction-neutral FavoriteNoItem apology; its deploy RIDES THE
   BATCH), JF-813 (decisions round, closed inline, pushed), JF-814 round 1
   (the dossier stop, merged and pushed). JF-824 filed + re-scoped (ledger
   half FIXED at the JF-821 tail; remaining scope = the per-locale
   direction-wording guard).
1. JF-814 round 2: season-less PlayEpisode family, the COMPLETE fix
   (handler elicit branch + DidNotCatchSeasonNumber in 17 locales + pin
   re-decision + templates + fixtures + mirrors). Surface: templates/,
   model_*.json, fixtures/, validate scripts, Locale/*.json,
   PlayEpisodeIntentHandler + its tests. After merge, I own the LIVE deploy
   (model PUT + catalog-sync dance, see memory
   smapi-model-put-strips-catalog-wiring) and the profile-nlu battery on ALL
   slots.
2. JF-823: AudiobookTitle catalog wiring (4th catalog type + seed-survival arm).
   Surface: Alexa/Catalog/**, Entities/User.cs, CLAUDE.md anti-pattern #10.
   Decision already made in dispatch: ADD the seed arm (no Series-style skip).
   After merge, I own the DLL deploy + catalog sync + the LIVE A/B (AC#2, the
   JF-684 selection-gating risk: out-of-catalog titles must not degrade to
   NO_SELECTION) + probe matrix.
3. JF-820: the foreign-generation stale-prewrite fix; decision made: option
   (a), resolve through the LIVE REGISTRATION's directory. Surface:
   VideoAudioController.cs + its tests.

Each lane: worker gates (simplify + code-review high), then my gate-marker,
then tail, then merge --no-ff, then ONE full suite + Release build, then
BATCH deploy (the DLL changes of JF-820 + JF-821 + JF-823 in one deploy;
the model PUT only after the JF-814 worker lands), then push, then worktree
cleanup. The box was healthy at 00:15 (load 2.17 falling, no tasks running).

## State at 02:00 (2026-10-09)

- BATCH DEPLOY DONE AND VERIFIED (md5 e5979454972e6dd63885d032b3460426,
  users=1, route smoke OK, startup clean): JF-820 + JF-821 + JF-823 DLL
  changes live on the box. Main at df8448de, pushed.
- JF-823's LIVE FOLLOW-UPS still pending (AC#2/AC#3): the catalog sync (the
  12h throttle WILL need the backdate dance: XML backup -> backdate
  LastCatalogSync -> chown abc:abc -> restart -> POST
  /ScheduledTasks/Running/77cc81a848fb7e84ccc51f052532e26 -> canary log) +
  the A/B probes (out-of-catalog titles NO_SELECTION risk, the 16 non-it
  generic-word fills, the guards). Runs TOGETHER with the JF-814 model PUT
  in ONE pass once JF-814's tail lands and merges: PUT the merged embedded
  models (rebuild endpoint, locale-scoped per locale) then ONE catalog sync
  then the profile-nlu battery over BOTH changes.
- JF-814 round 2: worker's tail IN FLIGHT (marker findings: F1 the JF-614
  slotValues echo fix + the series elicit, F4 the validator season-without-
  episode error check, F6 fixture rationale line, F7 the finalize commit,
  F2 record correction, F3 -> JF-841 amendment, F5 -> NEW JF-843 the
  absolute-vs-per-season ambiguity). After its report: my read of the tail,
  merge, full suite + Release, then the model PUT pass above.
- Filings tonight beyond the merges: JF-824 (re-scoped: per-locale direction
  guard), JF-825 (+22 seeds on the unbounded side), JF-826 (renumbered from
  the fourth collision; the mid-conversation dynamic-push overwrite),
  JF-841 (season-only gate), JF-842 (flaky ffmpeg test), JF-843 (pending,
  numbering ambiguity).

## State at 04:00 (2026-10-09)

- The LIVE PASS ran: 17-locale rebuild (17/17 SUCCEEDED) + full catalog sync
  (16/16 locales, 383 audiobooks, AudiobookTitle wired, 21 seed values
  appended live) + the battery. The JF-814 incident pins are LIVE-GREEN
  (both forms route PlayEpisodeIntent); season-ed forms green; the album
  guard green; "metti l'audiolibro di sapiens" (library, out-of-seed) routes
  PlayBook LIVE (AC#1 of JF-823 met).
- The battery's 6 reds decompose: the NextUp article-form steal (fix IN
  FLIGHT with the post-A/B worker), JF-844's four roll-residuals (star-wars
  pair, murderbot digit season, canzone steal [mechanism solved: routes
  PlayArtistSongs 'P!nk floyd', the JF-690 arbitration prompts on the tie,
  handler blameless], es-ES subjunctive), and the known simulate outage
  class. JF-823's AC#2 verdict recorded on its task file (commit 0cfe298a).
- IN FLIGHT: the post-A/B worker (the 16-locale generic-word seed arm + the
  NextUp article form across locales + fixture pins). After its gates:
  my gate-marker, merge, suite, deploy, ONE more roll (rebuild + backdated
  sync), then the JF-844 re-probe protocol + the A/B re-probes (generic
  words, sapiens, xyzzyfoo) + the successivo pin.
- The memory file smapi-model-put-strips-catalog-wiring was corrected (the
  task-id typo; the backdate alone triggers the sync at startup, no POST
  needed).

## Queue after these (nothing dispatched)

- JF-820 (foreign-generation stale prewrite, VideoAudioController; LOW priority;
  design decision (a) vs (b) unresolved, task analysis leans (a): resolve through
  the live registration's dir).
- JF-822: record-only filing (rejected alternative), stays To Do per the JF-789
  convention. No action.
- JF-818 (the three-family orchestration hoist): To Do, low; grew two more items
  tonight (log wording (a), span-scan (b), per-poll drop-log noise (c)).
- The 1.0 milestone remainder: the device round needs PAOLO (books cold,
  FollowMe two-Echo, T1 native transfer, T2 stream-kill, "ultimo episodio"
  announce, plus the JF-780 probes now including the single-file-book resume).
  Then JF-811 (the tag). Release notes: docs/release/release_notes_1.0.0.0.md
  (complete). Manifest placeholders deliberately NOT committed (issue #38
  lesson); minix's queued 1.0.0.0 install attempt keeps ERRing on the update
  task until the catalog mirror drops the retracted entries; KNOWN, pre-existing,
  self-resolving, do not "fix".

## Standing rules re-asserted tonight (do not relearn)

- Raw model PUT strips catalog wiring: always follow with the catalog sync dance
  (backdate LastCatalogSync in plugin XML with backup + chown abc:abc, restart,
  POST /ScheduledTasks/Running/77cc81a848fb7e84ccc51f052532e26, canary log).
- /Items/{id} POST on Jellyfin 12 is FULL-DTO replace; never partial-write.
- Deploy checklist: fresh config backup (?ApiKey= form), net10.0 build, scp +
  podman cp + chown -R abc:abc, restart, poll /System/Info, verify ACTIVE dll
  md5, route smoke, users=1. The deploy-gate hook demands the gate answer in the
  record before push; state gates + disposition, then retry the same command.
- Worker worktrees; MCP backlog writes go to the MAIN checkout only (file edits
  in worktrees merge with the branch).
- Cron (15 min) fires LOOP RESTART CHECK; on each firing, check lane states,
  merge what is ready, dispatch what is free.
