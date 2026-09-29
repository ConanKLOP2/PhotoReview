# RAW agents — work protocol (mandatory for every RAW-* agent and for the lead)

Why this file exists: long agent runs can end abruptly (context/token exhaustion, crash, the user stopping a
session). Anything not committed **and pushed** is lost, and a successor cannot tell where the work stopped.
This protocol makes every task resumable by a fresh agent from `git` + one progress file, with at most one
step of work lost.

## 1. Setup (first 3 commands of every task)

```bash
git fetch origin
git checkout -b feat/raw-<NN>-<slug> origin/master      # e.g. feat/raw-11-tiff-readers; never stack on another RAW branch
# resuming instead? -> git checkout -B feat/raw-<NN>-<slug> origin/feat/raw-<NN>-<slug>, then read the progress file
```

Then read, in order: `AGENTS.md`, `docs/adr/0009-camera-raw-support.md`, historical card in `git show HEAD:docs/refactoring/raw/TASKS.md`,
`raw/progress/RAW-<NN>.md` if it exists (resume), and only the files your card lists.

## 2. The commit-every-step rule

- Your card's **Steps** are numbered. After **each** step: build, run the step's tests, update the progress
  file, **commit and push**. Do not start step N+1 with step N uncommitted.
- Additionally commit + push at least every **20 minutes of work or every ~15 tool calls**, whichever comes
  first, even mid-step (use a `wip(raw-NN):` prefix when the build or tests are not green yet).
- Before any long-running command (full test suite, benchmark, mutation run), commit + push first.
- Push every commit immediately: `git push -u origin HEAD` (first time), then `git push`. Never force-push.
- Commit message format:
  - green step: `feat(raw-NN): step K — <what>` (or `test(raw-NN)`, `docs(raw-NN)`, `fix(raw-NN)`)
  - not green: `wip(raw-NN): step K — <what>; <what is broken>`
  - every message ends with a blank line and `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`
- Open the PR (`gh pr create --base master --draft`) right after the **first** green step, so work is
  visible and CI runs early; mark it ready (`gh pr ready`) only when the card's acceptance criteria are met.
  PR body ends with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.

## 3. Progress file (raw/progress/RAW-NN.md, committed with every step)

Create it in your first commit from this template and keep it current — it is the handoff:

```markdown
# RAW-NN <title> — progress
Branch: feat/raw-NN-<slug> · PR: <url or "not yet"> · Agent model: <sonnet|opus|haiku>
Last update: <YYYY-MM-DD HH:MM> · State: IN PROGRESS | BLOCKED | READY FOR REVIEW | DONE

## Steps
- [x] 1. <step> — <commit sha> — tests: <which, pass count>
- [ ] 2. <step>  ← CURRENT: <exactly what is half-done, which file/function>
- [ ] 3. ...

## Next action (for whoever resumes)
<one or two concrete sentences: the very next command or edit>

## Evidence / measurements
<numbers, corpus files used, offsets verified, mutation checks: "broke X → test Y failed">

## Open problems
<anything blocking, with error text; questions for the lead>
```

Rules: one progress file per task, only its owner edits it; keep it under ~6 KB (summarize old evidence);
never put secrets or user file paths from outside the repo into it (corpus file names are fine).

## 4. Running out of budget — hand off, don't vanish

- If you estimate you have used ~70 % of your context, or you were told to stop: finish or revert the
  current edit, commit (`wip` if needed), push, make **Next action** exact, and end your turn with a short
  handoff report (branch, last sha, next action). Do not start a new step.
- The lead resumes with a fresh agent using the same card + "resume from progress file" (template below).
- A successor trusts the progress file only after checking it against `git log` and a build.

## 5. Tests and verification (unchanged repo rules, repeated here because agents skip them)

- Every test run is bounded: `--blame-hang --blame-hang-timeout 120s --blame-hang-dump-type none`, or
  `tools/verify-all.ps1`. Local runs never exclude `Category=UI`; prefer the hidden-desktop runner
  (`tools/run-tests-hidden.ps1`) once it is on master.
- Iterate on the test projects you touch; before marking the PR ready run the full CI filter
  `Category!=Manual&Category!=Native&Category!=Slow` on the solution, plus `Category=Native` for tests that
  need the RAW corpus or a native DLL, plus `Category=Slow` if you touched preload/cache/file actions.
- `dotnet build PhotoReview.slnx -c Release` must show 0 warnings (Release treats warnings as errors).
- Every new test is mutation-checked (break the code → the test fails → restore); record it in the progress
  file. Push first, then finish all mutation checks — never skip them to save time.
- Do not edit `task_on_progress.md`, `docs/ACTIVE-TASKS.md`, `PERF-STATUS.md` (except one bullet when your
  card says so), or another task's files. New shared-file members are appended at the end.
- Clean up background processes/loops you started. Never search the filesystem root (`find /`).

## 6. Lead duties

- Spawn each wave's tasks in parallel worktrees with the prompt template below; model per the card.
- On each hand-back: read the diff yourself, rebuild, rerun the gate on the branch (hidden runner, sequential
  across branches to avoid CPU contention), then report to the user. Never relay "N tests pass" unverified.
- Resume abandoned tasks from their progress file; merge conflicts are resolved by the lead (or a strongest-
  model agent), never by force-push.
- After a wave merges: tick it in WORK-RAW-SUPPORT.md §4 in the next task's PR (not a separate edit).

## 7. Prompt template (lead fills the `<>` parts)

```text
You are implementing task RAW-<NN> "<title>" of the PhotoReview RAW-support plan (C# .NET 10 WPF, Windows).
Repo root = your worktree. Follow docs/refactoring/raw/AGENT-PROTOCOL.md exactly — especially: commit AND push
after every numbered step and at least every 20 minutes, keep docs/refactoring/raw/progress/RAW-<NN>.md current
in every commit, and hand off cleanly if you near your context limit.
<"Start fresh" | "RESUME: branch feat/raw-<NN>-<slug> exists; read the progress file and git log, verify with a
build, continue from 'Next action'">.
Your card: docs/refactoring/raw/TASKS.md § RAW-<NN>. Contracts: WORK-RAW-SUPPORT.md §3 (names are final).
Decisions in force: <paste the Decided lines from raw/DECISIONS.md>.
You may delegate mechanical sub-tasks (extra tests, mutation checks) only to sonnet/haiku sub-agents, which
must follow the same protocol on your branch (no parallel edits to the same file).
Final reply (≤15 lines): PR URL, last sha, steps done/remaining, test counts, mutation results, open problems.
```
