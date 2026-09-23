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
| Q-S3 | TS | Journal test determinism? | 🔄 BLOCKED | ../archive/historical/TEST-SPEED-PLAN-2026-09-20.md |
| Q-AR1 | AR | Ship TurboJpeg in the release (A) or remove it from Settings (B)? | 🔄 Pending — recommend **A** | arch-review/AR01-turbojpeg-release.md |
| Q-AR2 | AR | Accept ADR 0005 (no `ConfigureAwait(false)` in App; WD01 no longer waits for OC14)? | 🔄 Pending — recommend **yes** | ../adr/0005-ui-thread-affinity.md, arch-review/AR04 |
| Q-AR3 | AR | One composition root for app/tests/benchmarks; replace ST06 public fields with read-only properties (revisits ST06/Q-ST3 field decision, keeps Cli→App)? | 🔄 Pending — recommend **yes** | arch-review/AR02-single-composition-root.md |
| Q-AR4 | AR | Single release location = CI path `src/PhotoReview.App/bin/Release/net10.0-windows/publish`; delete `outputs/release/`? | 🔄 Pending — recommend **yes** | arch-review/AR06-release-output-cleanup.md |
| Q-AR5 | AR | Task-group triage: keep/close per group (proposal table in AR07 §5) | 🔄 Pending — needs user | arch-review/AR07-docs-and-triage.md |

**Legend:** ✅ Decided · 🔄 Pending · ⏸ Blocked

**Most critical blocker:** OC14 (Undo unification) — blocks ST08/ST09, WD03-06, OC15-18, DT05 acceptance. WD01 is unblocked if Q-AR2 = yes (AR04 implements it).

**Decisions that unblock the most work:** Q-AR3 (AR02 → AR04) and Q-AR1 (AR01).

See [`docs/ACTIVE-TASKS.md`](../ACTIVE-TASKS.md) for current task status.
