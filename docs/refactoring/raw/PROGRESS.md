# Camera RAW support: progress record (PR #239)

State of the work on the RAW integration branch (`feat/raw-support-integration`, PR https://github.com/ConanKLOP2/PhotoReview/pull/239), written for handover. Facts come from git; anything not checked is marked NOT VERIFIED inside each file.

| File | What it records |
|---|---|
| `progress/01-review-and-fixes.md` | How the multi-agent review was organised and every fix wave, with commit hashes and the tests that guard them |
| `progress/02-tests-and-ci.md` | Test counts, flaky and environment-dependent tests and what was measured, the strict RAW corpus tests, the manual corpus workflow, how to reproduce locally |
| `progress/03-decisions-and-open-items.md` | Decisions taken, items deliberately not fixed or still open, behaviour changes to review before merge, docs that are now stale |
| `progress/05-multi-agent-review-2026-10-01.md` | Second review of the whole PR (10 area reviewers, 2 skeptics per finding): 18 confirmed and fixed, 4 split, 5 rejected with reasons, what was verified |
| `progress/04-in-progress-and-resume.md` | Exact current state, unfinished work (OC14 flake investigation stopped manually, manual corpus workflow never run), resume checklist for another machine, cleanup list |

Status 2026-10-01: PR #239 is OPEN and mergeable, CI green at `7a5109fe`, master has no commits the branch lacks. Start with file 04 to resume, or file 03 to decide what is left.
