---
id: JF-842
title: >-
  VideoAudioControllerTests StreamVideoAudio_FfmpegFailsFastWithoutOutput_Returns500
  is flaky under full-suite load on net10.0
status: To Do
labels:
  - tests
priority: low
---

## Description

Observed 2026-10-09 on the JF-814 worktree (agent-af77920eafddbeb3a): a full
`dotnet test` run failed exactly this test on net10.0
(`Jellyfin.Plugin.AlexaSkill.Tests/Controller/VideoAudioControllerTests.cs:638`),
while net9.0 was green in the same run and the two prior full runs were green on
BOTH TFMs. The test passes in isolation and on the immediate full-suite rerun
(5529/5529 net10.0). No source in its area was touched by JF-814 (handler/locale/
template change only), so this is a load/timing flake in the ffmpeg-fails-fast
test, not a regression from this task.

Fix shape: make the test hermetic and timing-tolerant (fake-script path injection
per the `WriteRecordingFakeFfmpeg` pattern; assert the 500 outcome without
depending on process-exit timing under parallel-suite CPU load).
