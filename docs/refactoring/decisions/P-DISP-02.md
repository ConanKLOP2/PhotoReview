---
id: P-DISP-02
order: 107
summary: |-
  Decided 2026-10-06 (option A): leave the vblank thread alone until there is evidence; `D3DKMTWaitForVerticalBlankEvent` may block forever (display off or monitor change) but this is not proven. Revisit if the arrow-key panning re-check still fails or a display-sleep run shows the stall.
---

# P-DISP-02 - vblank thread that may block forever

## Current state

`WindowsDisplayClock` waits on `D3DKMTWaitForVerticalBlankEvent`, which has no timeout. When the display is off or the monitor
changes, the wait may never return. The idle-exit check only runs between waits, and `_thread` stays non-null, so no new thread
starts for another monitor. The background thread exits with the process. NOT proven: it needs a display-sleep test. A link to the
jerky arrow-key glide (user check of 2026-09-27) is possible but unproven.

## Options

| | Change | Pros | Cons |
|---|---|---|---|
| A | Leave until there is evidence | No risk to a working clock | A real stall would stay |
| B | Abandon the blocked thread and start a new one (generation token) on monitor change | Recovers after a monitor change | Leaks a blocked thread; concurrency change in a perf-critical path, hard to test |
| C | Stop the thread on power/session-lock notifications | Covers display sleep | More plumbing (power events); does not cover a monitor change |

## Decision

Option A, chosen by the user on 2026-10-06.

## When to revisit / evidence plan

Revisit when the manual arrow-key panning re-check still fails on the current build, or a display-sleep run shows the stall.
Plan: run the app, put the display to sleep and wake it (user present), watch the `WindowsDisplayClock` log lines added by #314
(`MarkFailed`), and compare glide smoothness before and after.