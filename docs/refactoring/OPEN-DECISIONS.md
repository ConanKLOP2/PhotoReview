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

**Legend:** ✅ Decided/Accepted · 🔄 Pending · ⏸ Blocked

Q-R1..Q-R6: the user answered "follow the recommendation" on 2026-09-24, so each default is the plan's recommendation. Implementation waves: 1a/1b/2b are in progress on `review/2026-09-25-integration`; the others are not started.

**All Q-D1..D4, Q-ST1..4, Q-T1..4, Q-OC14, Q-S3, Q-AR1..5, Q-L1..L8, Q-IO1, Q-Z1 are decided** — full table (30 rows, resolution + rationale link per row) archived in [`archive/OPEN-DECISIONS-detail.md`](archive/OPEN-DECISIONS-detail.md). T89 GUI acceptance stays with the user.
