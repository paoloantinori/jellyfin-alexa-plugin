---
id: JF-752
title: >-
  JF-752 - site-level laziness pin for BuildSessionMissResponse (the side
  effects, not just the response shape) + the EventAwareDegradeRoster's
  inline-evasion boundary pairing
status: To Do
assignee: []
created_date: '2026-10-04'
labels:
  - test-coverage
  - pipeline
dependencies:
  - JF-708
references:
  - backlog/tasks/jf-708 - unify-the-refusal-translation-family-one-policy-for-warmingtoken-refusals-and-the-event-aware-degrade.md
priority: low
---

## Description

Filed 2026-10-04 by the orchestrator from the JF-708 gate-marker (finding 2 of 4,
low severity; the reserve number the JF-708 worker left unused).

FINDING: the session-miss laziness guarantee (BuildSessionMissTell's dead-token
branch reads DeviceQueueManager.GetLastPlayedItemId and writes the AccountRelink
Information log, which must never fire on event requests; the pre-JF-708 early
return guaranteed it, JF-708's Func laziness keeps it) is pinned only at the
CORE's generic level (EventAwareDegradeCoreTests) and structurally by the IL
conjunction roster. A future edit that hoists the dead-token evidence OUT of the
factory (e.g. computing hadPreviousPlay in BuildSessionMissResponse before the
DegradeForEventRequest call) references neither IsEventRequest nor
BuildKeepAliveResponse, so BOTH guards stay green while every session-miss event
request re-fires the log and the queue read.

THE WORK: a site-level pin driving BuildSessionMissResponse (via its public
caller or reflection) with an event request, a token-bearing user, and an
observable DeviceQueueManager seam, asserting (a) the keep-alive response shape
AND (b) GetLastPlayedItemId was never called and the AccountRelink log never
fired; plus the non-event twin asserting both DO fire. The harness question
(Plugin.Instance is a static singleton; the DeviceQueueManager seam needs either
the shared-instance mock the plugin collection provides or a seam extraction) is
the pin's design decision.

PAIRS WITH: the EventAwareDegradeRoster's SECOND BOUNDARY (documented on the
roster since the JF-708 tail): an inline pattern evasion (a fifth degrade copy
spelling the type test itself while calling BuildKeepAliveResponse directly)
escapes the IL conjunction scan; a site-level side-effect pin per folded site is
the only guard class that catches per-site divergence regardless of how it is
spelled. Scope: the four JF-708 folded sites as they exist then; a blanket
side-effect framework is NOT wanted.
