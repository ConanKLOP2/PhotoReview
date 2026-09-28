# Testing

Detail behind [`AGENTS.md` > Tests](../AGENTS.md#tests): categories, parallel local runs, and the hang guard.

## Categories

`HotPath` (fast unit tests), `Slow`, `Architecture`, `Integration`, `Manual`, `Native`, and `UI` (real WPF `Application`/STA-dispatcher tests — mostly `PhotoReview.Integration.Tests`, plus a couple in `PhotoReview.App.Tests`; always paired with `[Collection("GlobalState")]`). Filter on any of these with `dotnet test --filter "Category=X"`.

## Parallel local runs

`tools/verify-all.ps1 -Parallel` runs the 5 test projects concurrently (one `dotnet test` process each) — ~2.1x speedup (170s -> 81s, default filter); doesn't change xUnit's in-assembly `[Collection("GlobalState")]` serialization. Add `-Hidden` to keep UI-test windows off the shared desktop.

## Hidden desktop (local only)

`tools/run-tests-hidden.ps1 [dotnet-test-args]` (or `verify-all.ps1 -Hidden`) runs `dotnet test` on a private, non-interactive Win32 desktop (`CreateDesktop`): `Category=UI` real-WPF windows and `MessageBox`es never appear on your desktop or steal focus. Defaults to the CI filter and hang flags; exit code, output (also logged under `TestResults\hidden-desktop-runner\`) and the hang guard behave exactly like a normal run. CI is unchanged.

## Hang guard

`tests/test.runsettings` caps `dotnet test` at 120s/test, 20min/session, no flags needed; prefer `verify-all.ps1` ([detail](refactoring/decisions/TEST-HANG-GUARD.md)).
