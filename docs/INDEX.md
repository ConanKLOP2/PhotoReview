# Documentation Index

Find a document by purpose. Read tiers: **T0** (every session, ≤16 KB total) · **T1** (one per task, ≤24 KB per file) · **T2** (history only). Checked by `tools/docs-budget.ps1 -Check`; links by `tools/check-doc-links.ps1`.

| File | Purpose | Tier |
|------|---------|------|
| [`AGENTS.md`](../AGENTS.md) | Work mode, priorities, coding/test rules, agent workflow | T0 |
| [`task_on_progress.md`](../task_on_progress.md) | Current state, pending checks, critical rules | T0 |
| [`ACTIVE-TASKS.md`](ACTIVE-TASKS.md) | Open work only (single source) | T1, always cheap |
| [`architecture.md`](architecture.md) | System design, code map, invariants INV-1..12, settings table | T1, any code area |
| [`APP-MECHANISMS-VI.md`](APP-MECHANISMS-VI.md) | Core app flows and settings (Vietnamese) | T1, app flow |
| [`adr/`](adr/) | Decisions 0001 decoder · 0002 UI framework · 0003 journal startup · 0004 no Presentation project · 0005 UI-thread affinity · 0006 localization catalogs · 0007 I/O durability · 0008 zoom = source pixel | T1, relevant area |
| [`TRANSLATING.md`](TRANSLATING.md) | Add/fix a language (JSON catalogs) | T1, translations |
| [`refactoring/OPEN-DECISIONS.md`](refactoring/OPEN-DECISIONS.md) | Q-* decisions: one open row + one line per decided | T1, decision lookup |
| [`refactoring/PERF-STATUS.md`](refactoring/PERF-STATUS.md) | Perf baselines and measured conclusions | T1, perf work |
| [`refactoring/HISTORY.md`](refactoring/HISTORY.md) | One line per finished group; how to recover removed plans from git | T1, "what was done before" |
| [`refactoring/I18N-PLAN.md`](refactoring/I18N-PLAN.md) | Multi-language design (L00-L12, done) | T1, UI text |
| `refactoring/arch-review/AR02, AR04, AR11` | Kept plans referenced by ADR 0005 and code comments | T2 |
| [`../README.md`](../README.md) | Overview, setup, build, settings | T1, first-time setup |
| [`../tests/Fixtures/README.md`](../tests/Fixtures/README.md) | Test fixtures and env vars | T1, tests |
| `archive/evidence/` (`decoder-bench`, `D-diagnosis-REPORT`, `T66-final`), `archive/historical/` (`PERF-DIAGNOSIS-TASKS`, `test-parity`), `refactoring/archive/STRUCTURE-OPTIMIZE-PLAN-2026-09-20.md` | Evidence for ADR 0001/0002 and files cited by code comments | T2, do not read unless referenced |

Everything else that was finished (plans, review reports, per-task tables) was deleted on 2026-09-27; use `git log --follow -- <path>` or `git show 1de561c:<path>`.
