# Testing

Detail behind [`AGENTS.md` > Tests](../AGENTS.md#tests): categories, parallel local runs, and the hang guard.

## Categories

`HotPath` (fast unit tests), `Slow`, `Architecture`, `Integration`, `Manual`, `Native`, and `UI` (real WPF `Application`/STA-dispatcher tests — mostly `PhotoReview.Integration.Tests`, plus a couple in `PhotoReview.App.Tests`; always paired with `[Collection("GlobalState")]`). Filter on any of these with `dotnet test --filter "Category=X"`.

## Parallel local runs

`tools/verify-all.ps1 -Parallel` runs the 5 test projects concurrently (one `dotnet test` process each) — ~2.1x speedup (170s -> 81s, default filter); doesn't change xUnit's in-assembly `[Collection("GlobalState")]` serialization. Caveat: `Category=UI` tests spin real WPF windows, so avoid `-Parallel` if another session is mid its own Integration-test run (can pop a visible `MessageBox` on the shared desktop — harmless, just click it).

## Hang guard

`tests/test.runsettings` caps `dotnet test` at 120s/test, 20min/session, no flags needed; prefer `verify-all.ps1` ([detail](refactoring/decisions/TEST-HANG-GUARD.md)).
