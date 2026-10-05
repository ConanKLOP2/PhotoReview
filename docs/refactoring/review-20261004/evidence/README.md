# Review evidence

Pinned baseline and scope: [WORK-REVIEW](../WORK-REVIEW.md).

- `CoreReviewReproTests.cs.txt`: safe fake filesystem assertions demonstrating deletion of foreign destinations in single/group/recovery Copy. Observed-bug assertions pass; invert to survival assertions for future regression tests.
- `CoreReviewSettingsReproTests.cs.txt`: worker's fake-store NaN startup and unnormalized keyboard zoom probes. Worker ran 2/2; lead independently reran 2/2 using the worker-built baseline DLL.
- `ReviewQueueRace.cs.txt`: desired-behavior assertion fails when a queued Move crosses folder identity.
- `ReviewFitRace.cs.txt`: corrected fake render-yield fixture; desired-behavior assertion fails (2.0 expected, 0.4 actual).
- `memory-fallback-probe.ps1`: exact source slices for memory admission and fallback classifier with stub decoder interfaces; no real bitmap allocation.
- `atomic-temp-ownership-probe.ps1`: exact AtomicCacheFile source with minimal cache cleanup stub, writes only a newly created own TestResults fixture. Removes no user photos; fixture remains for inspection.
- `publish-ancestor-probe.ps1` and `.json`: safe ancestor-junction ownership decision probe. Never invokes destructive publishing; cleanup is limited to verified own fixture.
- `physical-copy-missing-source.json`: Windows File.Copy with a nonexistent source and a pre-existing own-fixture destination throws FileNotFoundException, HResult -2147024894. File.Copy itself preserved that file; the separate service repro demonstrates the unsafe cleanup afterward.
- `inventory.cs.txt` and `inventory.csproj.txt`: Roslyn inventory generator using local SDK assemblies. Copy into ignored TestResults/inventory before running; SDK HintPaths may need updating. No external packages required.

Repro drafts are evidence, not merged automated regressions, and do not count as a full mutation campaign. Runtime logs/TRX remain in lead `TestResults/review` (ignored); their outcomes are summarized in [validation](../validation.md). Function names/line numbers refer to the pinned baseline, not a future master checkout.
