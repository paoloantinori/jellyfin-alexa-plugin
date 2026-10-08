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

1. JF-814 part 2: season-less PlayEpisode sample family, all 17 templates.
   Surface: templates/, model_*.json, fixtures/, validate_interaction_models.py,
   generate_voice_reference.py. After merge, I own the LIVE deploy (model PUT +
   catalog-sync dance, see memory smapi-model-put-strips-catalog-wiring) and the
   profile-nlu battery on ALL slots.
2. JF-823: AudiobookTitle catalog wiring (4th catalog type + seed-survival arm).
   Surface: Alexa/Catalog/**, Entities/User.cs, CLAUDE.md anti-pattern #10.
   Decision already made in dispatch: ADD the seed arm (no Series-style skip).
   After merge, I own the DLL deploy + catalog sync + the LIVE A/B (AC#2, the
   JF-684 selection-gating risk: out-of-catalog titles must not degrade to
   NO_SELECTION) + probe matrix.
3. JF-821: favorite-toggle data==null wording. Decision made: option (a), the
   RatingNoItem-mirror dedicated key, all 17 locales. Surface: ResponseStrings.cs,
   Locale/*.json, FavoriteToggle*.

Each lane: worker gates (simplify + code-review high), then my gate-marker,
then tail, then merge --no-ff, then ONE full suite + Release build, then batch
deploy (DLL changes batch together; model PUT only after the template worker
lands), then push, then worktree cleanup.

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
