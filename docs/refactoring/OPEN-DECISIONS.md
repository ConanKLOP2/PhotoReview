# Open Decisions

Consolidation of decisions (Q-*) across task groups. Status of task groups: [`docs/ACTIVE-TASKS.md`](../ACTIVE-TASKS.md).

| ID | Group | Question | Status | Plan |
|---|---|---|---|---|
| Q-OC15 | OC | UI pattern cleanup scope? | ✅ Done in #73 (OC15-18) | [`OPTIMIZE-CLEAN-SUMMARY.md`](OPTIMIZE-CLEAN-SUMMARY.md) |
| Q-Z1 | Zoom | 100 % = preview pixels or source pixels? | ✅ Option A: 1 source pixel, original decoded on demand (#43 + #47), 2026-09-24 | [ADR 0008](../adr/0008-zoom-source-pixel.md) |
| Q-R1 | Review | Transparent images in the preview disk cache (IMG-01) | ✅ ACCEPTED 2026-09-24 — (a) never persist previews with alpha | [`REVIEW-2026-09-25-PLAN.md`](REVIEW-2026-09-25-PLAN.md) §6 |
| Q-R2 | Review | Where can an action destination point (CORE-03) | ✅ ACCEPTED 2026-09-24 — (a) relative paths stay inside the photo folder, absolute allowed; checked at save and run time | same |
| Q-R3 | Review | Run the 6 Integration tests in CI; keep `Stress`? | ✅ ACCEPTED 2026-09-24 — (a) run them in CI, drop the `Stress` category | same |
| Q-R4 | Review | `outputs/` folder (HYG-06) | ✅ ACCEPTED 2026-09-24 — (a) move scripts and example config to `deploy/`, update README | same |
| Q-R5 | Review | Session write at shutdown on a slow disk (CORE-02) | ✅ ACCEPTED 2026-09-24 — (a) wait up to 2 s, then skip the last write | same |
| Q-R6 | Review | Accessibility scope (APP-01/02) | ✅ ACCEPTED 2026-09-24 — (a) user-facing windows (Main, Settings, Action Profiles, Recovery, Batch Review); Benchmark/Diagnostics best-effort | same |
| Q-R7 | Review r2 | Opaque PNG/WebP previews are never disk-cached (`Pbgra32`, R2-A-03) | ✅ ACCEPTED 2026-09-25 — (a) scan alpha on the downscaled bitmap; implemented in #77 | [round2/adversarial.md](../archive/evidence/review-2026-09-25/round2/adversarial.md) |
| Q-R8 | Review r2 | Recycle on removable/network/UNC drives (R2-F-05): fixed drives unchanged; other drives are now refused (file kept) because Windows deletes there permanently | ✅ ACCEPTED 2026-09-25 — (c) refuse by default + opt-in setting "allow permanent delete" (default off); implemented in #79 | [round2/fresh.md](../archive/evidence/review-2026-09-25/round2/fresh.md) |
| Q-R9 | Review r2 | TEST-03 Recycle Bin orphan sweep | ❌ DECLINED 2026-09-25 — a sweep deletes items from the real bin (a subagent mutation run left the bin without `$R` files); tests only clean their own items, the user empties the bin | — |
| Q-R10 | Review r2 | Opening another photo from Explorer while the app is running (R2-F-09): forward the path to the running instance (needs a named-pipe IPC) or keep the current message | ✅ ACCEPTED 2026-09-25 — (a) forward to the running instance via named pipe; implemented in #78 | [round2/fresh.md](../archive/evidence/review-2026-09-25/round2/fresh.md) |
| Q-R11 | Review r2 | Session resume is discarded when Explorer order applies (R2-F-10; intentional per 3943fb6); benchmark profiles run with disk cache off (R2-F-22); dead knob `MemoryReserveBytes`, 0.80 vs 0.90 headroom (R2-F-33); per-solution version computation (R2-F-35) | ✅ DECIDED 2026-09-25 — leave as is (no change) | same |
| Q-R12 | Review r3 | Forward client times out after the path was written | ✅ ACCEPTED 2026-09-25 — C: new `ForwardOutcome.Unknown`, exit without the "already open" dialog | branch `review/2026-09-25-round3` |
| Q-R13 | Review r3 | Undo depth | ✅ ACCEPTED 2026-09-25 — A: keep single-step undo (intentional) | same |
| Q-R14 | Review r3 | Duplicate cleanup on drives without a Recycle Bin | ✅ ACCEPTED 2026-09-25 — A: stay refused, one up-front message instead of per-file errors | same |
| Q-R15 | Review r3 | Group B fixes (Settings errors, Space/Enter, zoom decode retry, shortcut capture, journal enum, stat) | ✅ ACCEPTED 2026-09-25 — A: all; stat catch-all left (File.Exists never reports access errors, nothing to distinguish) | same |
| Q-R16 | Review r3 | Group C low-priority items | ✅ ACCEPTED 2026-09-25 — C: all done (persist queue 8, bounded Dispose, ZoomOut from Fit, `\photos`, per-path source-bytes evict) | same |
| Q-R17 | Review r3 | Whole-folder preload estimate (10x compressed size) is ~8x too pessimistic for viewport-sized previews | ✅ ACCEPTED 2026-09-25 — D: box bound `entries x w x h x 4` until 8 previews are measured at the current box, then measured mean x 1.25 (capped at the bound unless the images measure above it); ×10 compressed only for unbounded boxes; branch `perf/q-r17-preload-estimate`, perf run on the user machine still due | [ROUND3-4](REVIEW-2026-09-25-ROUND3-4.md) |

**Legend:** ✅ Decided/Accepted · 🔄 Pending · ⏸ Blocked

Q-R1..Q-R6: the user answered "follow the recommendation" on 2026-09-24; all merged in #76. Q-R7..Q-R11 (round 2) decided 2026-09-25: R7 a, R8 c, R10 a, R11 leave as is.

**All Q-D1..D4, Q-ST1..4, Q-T1..4, Q-OC14, Q-S3, Q-AR1..5, Q-L1..L8, Q-IO1, Q-Z1 are decided** — full table (30 rows, resolution + rationale link per row) archived in [`archive/OPEN-DECISIONS-detail.md`](archive/OPEN-DECISIONS-detail.md). T89 GUI acceptance stays with the user.
