# Open Decisions

Consolidation of all pending decisions (Q-*) across active task groups. See linked plan files for full rationale.

| ID | Group | Question | Status | Plan |
|---|---|---|---|---|
| Q-D1 | DT (Docs) | Add read-tier + size rules to AGENTS.md? | ✅ YES | docs/AGENTS.md |
| Q-D2 | DT | Archive via `git mv` or delete? | ✅ YES (`git mv`) | docs/INDEX.md |
| Q-D3 | DT | Add CLAUDE.md? | ✅ YES | docs/CLAUDE.md (created) |
| Q-D4 | DT | Add `.ignore` for archive? | ✅ YES (verify tools respect) | DT08 task |
| Q-ST1 | ST | Separate PerfAnalysis project? | ✅ YES | STRUCTURE-OPTIMIZE-STATUS.md |
| Q-ST2 | ST | Move PerfCsvListener to Core? | ✅ YES | STRUCTURE-OPTIMIZE-STATUS.md |
| Q-ST3 | ST | Keep Cli→App dependency? | ✅ YES | STRUCTURE-OPTIMIZE-STATUS.md |
| Q-ST4 | ST | Ctrl+Z: Move-only or Move+Recycle? | ✅ Move+Recycle | STRUCTURE-OPTIMIZE-STATUS.md |
| Q-T1 | TC | Queue keypresses in fixture? | ✅ YES | ../archive/historical/TEST-CLEANUP-PLAN-2026-09-20.md |
| Q-T2 | TC | Read-count seam approach? | ✅ Real-file-in-temp | ../archive/historical/TEST-CLEANUP-PLAN-2026-09-20.md |
| Q-T3 | TC | Real photos via env var? | ✅ YES | ../archive/historical/TEST-CLEANUP-PLAN-2026-09-20.md |
| Q-T4 | TC | Use real Recycle Bin? | ✅ YES (via fixture) | ../archive/historical/TEST-CLEANUP-PLAN-2026-09-20.md |
| Q-OC14 | OC | Undo unification approach? | 🔄 IN PROGRESS | ../archive/historical/OPTIMIZE-CLEAN-PLAN-2026-09-20.md |
| Q-OC15 | OC | UI pattern cleanup scope? | 🔄 WAITING OC14 | ../archive/historical/OPTIMIZE-CLEAN-PLAN-2026-09-20.md |
| Q-S3 | TS | Journal test determinism? | ✅ YES (recommendation applied in #36, 2026-09-24) | ../archive/historical/TEST-SPEED-PLAN-2026-09-20.md |
| Q-AR1 | AR | Ship TurboJpeg in the release (A) or remove it from Settings (B)? | ✅ **A** (ship TurboJpeg) — 2026-09-23 | arch-review/AR01-turbojpeg-release.md |
| Q-AR2 | AR | Accept ADR 0005 (no `ConfigureAwait(false)` in App; WD01 no longer waits for OC14)? | ✅ **YES** — 2026-09-23 | ../adr/0005-ui-thread-affinity.md, arch-review/AR04 |
| Q-AR3 | AR | One composition root for app/tests/benchmarks; replace ST06 public fields with read-only properties (revisits ST06/Q-ST3 field decision, keeps Cli→App)? | ✅ **YES** — 2026-09-23 | arch-review/AR02-single-composition-root.md |
| Q-AR4 | AR | Single release location = CI path `src/PhotoReview.App/bin/Release/net10.0-windows/publish`; delete `outputs/release/`? | ✅ **YES** (CI path only) — 2026-09-23 | arch-review/AR06-release-output-cleanup.md |
| Q-AR5 | AR | Task-group triage: keep/close per group (proposal table in AR07 §5) | ✅ **Per AR07 §5 proposal table** — 2026-09-23 | arch-review/AR07-docs-and-triage.md |

**Legend:** ✅ Decided · 🔄 Pending · ⏸ Blocked

**Most critical blocker:** OC14 (kept, re-scoped to "Undo gate location") — still blocks ST08/ST09 and OC15-18. WD01 is unblocked (Q-AR2 = yes, AR04 implements it); WD03-06 closed 2026-09-23 (Q-AR5, no known dialog bug).

**All Q-AR1..Q-AR5 decided 2026-09-23.** Unblocked work: AR01 (Q-AR1), AR02→AR04 (Q-AR3), AR06 (Q-AR4). Next: run AR01, AR02a (stacked on AR03), AR06 on separate branches; AR02e needs the user's machine.

See [`docs/ACTIVE-TASKS.md`](../ACTIVE-TASKS.md) for current task status.
