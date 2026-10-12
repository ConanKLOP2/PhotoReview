# Agent hygiene: no hung, orphaned or conflicting agents

Read this before starting a subagent or when briefing one. Rules come from real incidents (2026-10-10..12):
agents that looked "running for 5 hours" after finishing, test hosts killed by another agent, scripts overwritten in a
shared scratchpad, a whole CI test class timing out 30 s per test.

## 1. Why a finished agent can look hung

The desktop app marks an agent finished only when it stops with **no live background work of its own**. A background
`dotnet test`/`dotnet build`/`powershell` started with `run_in_background` (or a detached process) that outlives the final
report keeps the agent "running" in the UI, sometimes for hours. The work is done; the row is stale.

## 2. Rules for every agent (put them in the prompt)

1. **Bound everything.** Every command has a timeout (`timeout`, `--blame-hang-timeout 120s` for `dotnet test`, a loop with a
   deadline). No unbounded wait or poll loop. A command expected to exceed ~5 min runs in the background AND is polled with a
   bounded check; never `sleep` on a marker file forever.
2. **Own what you start.** Start background processes only when needed, record their PIDs, and stop them before the final
   report. Stop only processes you started: never `Get-Process testhost | Stop-Process` or `dotnet build-server shutdown`
   without a path/PID filter (they belong to other agents too). `tools/agent-finish-check.ps1 -Worktree <path> [-Stop]`
   lists and stops exactly the processes under your worktree.
3. **Finish with the check.** The last command before the final report is
   `powershell -ExecutionPolicy Bypass -File tools/agent-finish-check.ps1 -Worktree <your worktree>`. It must print
   `[PASS]`: no leftover process, nothing uncommitted, nothing unpushed, no zero-byte stray in the main checkout.
4. **Push first, then verify** (see `agents-run-to-completion`): commit and push before long mutation/test passes so a hang
   or kill never loses the work; then finish all checks, then report.
5. **Isolate shared state.** Several agents share `%TEMP%` and the scratchpad: use a private subfolder with a unique prefix
   and unique script names (`<package>-...`), a unique hidden-desktop name (`tools/run-tests-hidden.ps1 -DesktopName PR<N>...`),
   and a unique `--results-directory`. Never overwrite another agent's file; never run another agent's script.
6. **Absolute paths for the worktree.** A relative path run from the wrong cwd creates files in the main checkout (zero-byte
   `ShellMainWindow.cs` incident). Pass absolute paths to file-writing commands; the finish check flags strays.
7. **Do not touch the main checkout.** It holds the user's uncommitted work. Work in `.claude/worktrees/<name>`.
8. **Report the truth about hangs.** If something hung, say what, where (stack/log), and what was done; do not retry blindly.

## 3. Rules for the lead (this session)

- After an agent reports done, if the UI still shows it running: `TaskStop` it (the PR is already pushed), then run
  `tools/agent-finish-check.ps1` against its worktree.
- Give each concurrent agent a distinct file ownership list and a unique `order:` range for `decisions/*.md`.
- Never use a blanket kill. Identify by PID and command line.
- A CI timeout cascade (every later test fails after 30 s) means one test left process-wide state broken (a stuck render
  thread, a held lock): fix the first failing test's cleanup; make each test fail independently (see
  `tests/PhotoReview.Shell.Integration.Tests/Startup/ShellRun.cs` and `HangDiagnostics.cs`).

## 4. Test-code rules that prevent CI hangs

- Release graphics/COM in `OnClosing`/`finally` while the HWND is alive; `ClearState + Flush` before dropping a swap chain.
- Every test that starts a window/instance disposes it in `finally`; a failure in one must not poison the next.
- No `Thread.Sleep`/`Task.Delay` (TEST-FLAKY rule), no dependence on screen size (CI desktop is 1024x768; a window is clamped
  to the monitor plus ~20 px invisible frame).
