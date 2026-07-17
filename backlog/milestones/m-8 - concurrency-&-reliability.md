---
id: m-8
title: "Concurrency & Reliability"
---

## Description

Close latent concurrency and persistence fragilities masked by the skill's effectively single-user reality: unsynchronized Users collection on the request hot path, singleton stateless-by-luck handlers, VideoAudioCache TOCTOU + atime-based LRU, non-atomic position-file writes, untracked fire-and-forget ffmpeg tasks, SSML-escape coverage audit.
