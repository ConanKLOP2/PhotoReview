---
id: CI-PARALLEL-TEST-JOBS
order: 1220
summary: |-
  CI: job `build-test-publish` tÃ¡ch thÃ nh 3 job test song song + `build-publish`; `build-test-publish` cÃ²n lÃ  job tá»•ng há»£p (CHÆ¯A XÃC NHáº¬N, 2026-10-13).
---

# CI-PARALLEL-TEST-JOBS - tÃ¡ch bÆ°á»›c test cá»§a ci.yml theo RUNNER (2026-10-13)

Tráº¡ng thÃ¡i: **CHÆ¯A XÃC NHáº¬N** (Ä‘á»•i workflow CI cáº§n chá»§ repo xem; PR khÃ´ng báº­t auto-merge). HoÃ n tÃ¡c: revert PR.

## 1. Váº¥n Ä‘á»

Job `build-test-publish` cháº¡y tuáº§n tá»± trÃªn 1 runner `windows-latest` (2 vCPU): run 38411060424 = 730 s (restore+cache 36 s,
Build Release cáº£ solution 136 s, test ~500 s, Publish 33 s). TÃ¡ch tiáº¿n trÃ¬nh trÃªn 1 runner khÃ´ng cÃ³ lá»£i (2 vCPU); tÃ¡ch theo
RUNNER (job riÃªng) thÃ¬ cÃ³.

## 2. Thay Ä‘á»•i

| Job | Viá»‡c | Ghi chÃº |
|---|---|---|
| `changes` (ubuntu) | PR chá»‰ Ä‘á»•i `docs/**`, `README.md`, `CLAUDE.md`, `task_on_progress.md` thÃ¬ `code=false` | push/dispatch luÃ´n `code=true`; `AGENTS.md` KHÃ”NG thuá»™c danh sÃ¡ch (Architecture test Ä‘á»c nÃ³) |
| `build-publish` | Build Release cáº£ `PhotoReview.slnx` (cá»•ng biÃªn dá»‹ch, warning = lá»—i), Show version, Publish, Verify, upload `release-publish` | khÃ´ng chá» test |
| `test-core` | Architecture, Core, Imaging, App | chá»‰ build 4 project test nÃ y + phá»¥ thuá»™c |
| `test-shell` | Shell, Shell.Integration, Shell.WpfBridge | |
| `test-integration` | Integration + bÆ°á»›c Integration+Slow (17 test, 76 s) | |
| `build-test-publish` | JOB Tá»”NG Há»¢P (ubuntu, `if: always()`): thÃ nh cÃ´ng chá»‰ khi `changes` + 4 job trÃªn Ä‘á»u `success` (hoáº·c cÃ¹ng `skipped` khi PR chá»‰ docs) | GIá»® tÃªn: lÃ  required check cá»§a master vÃ  `release.yml` tra tÃªn nÃ y trÃªn commit cá»§a tag |

- `tag-version` giá»¯ `needs: [build-test-publish, repo-checks]` (ngá»¯ nghÄ©a cÅ©: chá»‰ gáº¯n tag khi má»i thá»© xanh). `publish-aot` váº«n khÃ´ng náº±m trong needs.
- Giá»¯ nguyÃªn Má»ŒI test, Má»ŒI filter (`TEST_FILTER` chuyá»ƒn lÃªn `env` cáº¥p workflow), `--blame-hang-timeout 120s`, `--blame-hang-dump-type none`, trx.
  BÆ°á»›c safety-net Integration+Slow cháº¡y theo project cá»§a tá»«ng job (7 project cÃ²n láº¡i "No test matches" ~3 s má»—i cÃ¡i; giá»¯ Ä‘á»ƒ filter
  Ä‘á»•i khÃ´ng lÃ m rÆ¡i test).
- Artifact test Ä‘á»•i tÃªn: `test-results` thÃ nh `test-results-core|shell|integration` (khÃ´ng cÃ³ consumer; kiá»ƒm báº±ng grep).
- KhÃ´ng Ä‘á»•i `permissions`, SHA ghim action, `concurrency`.
- Test kiáº¿n trÃºc (`TestFilterDriftTests`): luáº­t "safety-net covers every project" gom má»i bÆ°á»›c safety-net cá»§a cÃ¡c job; thÃªm luáº­t
  `CI-PARALLEL` (má»—i project test cÃ³ Ä‘Ãºng 1 job cháº¡y bÆ°á»›c chÃ­nh + bÆ°á»›c safety-net; job tá»•ng há»£p `needs` má»i job build/test vÃ  cÃ³ `if: always()`).

## 3. Rá»§i ro

- Má»—i job test restore + build láº¡i pháº§n cá»§a nÃ³ (khÃ´ng chia sáº» build output giá»¯a job): tá»•ng phÃºt runner tÄƒng, thá»i gian tÆ°á»ng giáº£m.
- Cache NuGet: má»i job restore cáº£ solution Ä‘á»ƒ bá»™ package giá»‘ng nhau (job nÃ o lÆ°u cache trÆ°á»›c cÅ©ng lÆ°u báº£n Ä‘áº§y Ä‘á»§).
- Chá»‰ tÃ­nh `skipped` há»£p lá»‡ khi `changes.code=false`; job bá»‹ skip vÃ¬ lÃ½ do khÃ¡c lÃ m job tá»•ng há»£p Ä‘á».

## 4. Viá»‡c cho chá»§ repo

Required checks hiá»‡n lÃ  `build-test-publish` vÃ  `repo-checks`: KHÃ”NG cáº§n Ä‘á»•i gÃ¬ (tÃªn giá»¯ nguyÃªn). Tuá»³ chá»n: thÃªm
`test-core`, `test-shell`, `test-integration`, `build-publish` vÃ o required checks Ä‘á»ƒ tháº¥y tá»«ng job (khÃ´ng báº¯t buá»™c vÃ¬ job tá»•ng há»£p
Ä‘Ã£ bao trÃ¹m; lÆ°u Ã½ job bá»‹ skip trÃªn PR chá»‰ docs Ä‘Æ°á»£c GitHub coi lÃ  Ä‘áº¡t).

## 5. Sá»‘ Ä‘o

(Äiá»n sau khi CI cá»§a PR cháº¡y; xem mÃ´ táº£ PR.)
