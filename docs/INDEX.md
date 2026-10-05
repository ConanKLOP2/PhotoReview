# Documentation Index

Find a document by purpose. Read tiers: **T0** (every session, ≤18 KB total) · **T1** (one per task, ≤24 KB per file) · **T2** (history only: git log). Checked by `tools/docs-budget.ps1 -Check`; links by `tools/check-doc-links.ps1`.

| File | Purpose | Tier |
|------|---------|------|
| [`AGENTS.md`](../AGENTS.md) | Work mode, priorities, coding/test rules, agent workflow | T0 |
| [`task_on_progress.md`](../task_on_progress.md) | Current state, pending checks, critical rules | T0 |
| [`ACTIVE-TASKS.md`](ACTIVE-TASKS.md) | Open work only (single source) | T1, always cheap |
| [`refactoring/review-20261004/README.md`](refactoring/review-20261004/README.md) | Whole-project function review 2026-10-04: [WORK-REVIEW](refactoring/review-20261004/WORK-REVIEW.md) status table (findings vs master), ledger `functions.tsv.gz`, wave2 ledgers | T1, review task |
| [`architecture.md`](architecture.md) | System design, code map, invariants INV-1..12, settings table | T1, any code area |
| [`APP-MECHANISMS-VI.md`](APP-MECHANISMS-VI.md) | Core app flows and settings (Vietnamese) | T1, app flow |
| [`adr/`](adr/) | Decisions 0001 decoder · 0002 UI framework · 0003 journal startup · 0004 no Presentation project · 0005 UI-thread affinity · 0006 localization catalogs · 0007 I/O durability · 0008 zoom = source pixel · 0009 camera RAW support | T1, relevant area |
| [`LESSONS-CONCURRENCY-VI.md`](LESSONS-CONCURRENCY-VI.md) | NGINX (ít luồng, I/O không chặn, cô lập việc chặn) + double booking (nguyên tử, khóa lạc quan, định danh phiên bản), cách PhotoReview áp dụng, danh sách kiểm tra (Vietnamese, reusable) | T1, concurrency/I-O design |
| [`TRANSLATING.md`](TRANSLATING.md) | Add/fix a language (JSON catalogs) | T1, translations |
| [`TESTING.md`](TESTING.md) | Test categories, filters, local runners, hang guard | T1 |
| [`MUTATION-TESTING.md`](MUTATION-TESTING.md) | Stryker.NET: safe run recipe, 2026-10-03 baseline scores, highest-value gaps | T1, test quality |
| [`refactoring/OPEN-DECISIONS.md`](refactoring/OPEN-DECISIONS.md) + [`decisions/`](refactoring/decisions/) | Q-* index + one file per decision | T1, decision lookup |
| [`refactoring/PERF-STATUS.md`](refactoring/PERF-STATUS.md) + [`perf/`](refactoring/perf/) | Methodology + one file per measurement | T1, perf work |
| [`refactoring/HISTORY.md`](refactoring/HISTORY.md) | One line per finished group; how to recover removed plans from git | T1, "what was done before" |
| [`refactoring/I18N-PLAN.md`](refactoring/I18N-PLAN.md) | Multi-language design (L00-L12, done) | T1, UI text |
| [`refactoring/raw/`](refactoring/raw/PROGRESS.md) | Camera RAW (ADR 0009, merged in #239): `PROGRESS.md` (post-merge status, open items), `DECISIONS.md` (Q-RAW) | T1, RAW work |
| [`../README.md`](../README.md) | Overview, setup, build, settings | T1, first-time setup |
| [`../tests/Fixtures/README.md`](../tests/Fixtures/README.md) | Test fixtures and env vars | T1, tests |

Everything else that was finished (plans, review reports, per-task tables, ADR evidence, the review ledger) was deleted on 2026-09-27, 2026-10-01 and 2026-10-02 (review plans: `5a39e92c`; the 10-01 set: `2f2bf342`; earlier: `1de561c`); finished decision fragments are deleted too (2026-10-03: CI-TAG-VERSION-BATCH-PUSH, OPT-IMAGING-FIXES, OPT-TOOLING-TRGENERATOR-HASHSET, last copy `d19359dd`). Use `git log --follow -- <path>` or `git show <sha>:<path>`.
