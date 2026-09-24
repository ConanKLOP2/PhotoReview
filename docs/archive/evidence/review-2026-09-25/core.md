# Code Review — PhotoReview.Core / PhotoReview.Platform.Windows (2026-09-25)

Scope: `src/PhotoReview.Core`, `src/PhotoReview.Platform.Windows` (tests read only as evidence).
Reference: `AGENTS.md`, `docs/architecture.md`, ADR 0003 (journal tail-read), ADR 0005 (thread affinity), ADR 0007 (I/O durability).

## Critical

None found. Journal Prepared→mutation→Committed ordering, INV-4 gating, and Recycle Bin verify logic all held up under inspection.

## High

### CORE-01 — `UndoService` breaks the ADR 0005 Core threading rule (missing `ConfigureAwait(false)`), and this is invisible to the test gate
`src/PhotoReview.Core/FileActions/UndoService.cs:185`, `:227`

```csharp
await Task.Run(() => _fileSystem.Move(move.Destination, move.Source));
...
var restored = await Task.Run(() => _recycleBin.TryRestore(action.Source, action.Size, action.LastWriteUtc));
```

ADR 0005 states Core/Platform must *always* `ConfigureAwait(false)`. Every other `await Task.Run(...)` in `FileActionService` (lines 121, 131, 192) does this correctly; these two in `UndoService` do not. The continuation after these awaits (`_lastUndoAction = null; return ...` / cache/state updates) will resume on whatever `SynchronizationContext` was captured — i.e. the WPF UI thread's context when called from `MainViewModel`. This mostly "works" because the caller is already on the UI thread, but it silently reintroduces a UI-thread dependency into Core, defeats the purpose of `Task.Run` (no actual parallelism benefit — the continuation still marshals back to the dispatcher), and will genuinely misbehave (extra `Dispatcher.Invoke` marshaling, or a deadlock if a future caller ever blocks synchronously on this task from the UI thread, e.g. `.Result`/`.Wait()`) since none of the guard rails apply here.

`AppThreadAffinityTests` only scans `src/PhotoReview.App`, so a Core-side ADR 0005 violation like this has zero automated coverage — it can recur silently.

**Fix:** add `.ConfigureAwait(false)` to both call sites; also pass the request's `CancellationToken` through (currently these two `Task.Run` calls take no token at all, unlike `FileActionService`'s equivalents).
**Test to add:** extend (or copy) `AppThreadAffinityTests`'s source-scan approach to also assert `src/PhotoReview.Core` and `src/PhotoReview.Platform.Windows` await sites use `ConfigureAwait(false)` — this is a static/text-scan test, not a hard behavioral one, but it's exactly the class of regression this bug is.
**Effort:** S. **Risk:** none — purely additive.

### CORE-02 — `SessionWriter.Dispose()` can deadlock or drop the final session write under load
`src/PhotoReview.Core/Session/SessionWriter.cs:83-87`, `:120`

`Dispose()` calls `Flush()` synchronously, which calls `WritePending()` → `WriteBatch()` → `_writeLock.Wait()` (a blocking wait, no timeout). If `Dispose()` runs on the UI thread during app shutdown while a debounced write from `RunTimerAsync` is concurrently inside `WriteBatch` doing a slow atomic file write (e.g. slow disk, AV scanning the write), `Dispose()` blocks the UI thread for the duration of that I/O with no upper bound. This is exactly the kind of "block UI thread on I/O" pattern ADR 0005/0007 tries to avoid elsewhere (see the explicit off-thread handling for `PowerLossSafe` journal writes).
Separately, `_writeLock` (`SemaphoreSlim`) is never disposed — a minor resource leak per `SessionWriter` instance, but `SessionWriter` is effectively a singleton for app lifetime so low real-world impact.

**Fix:** give `_writeLock.Wait()` in the shutdown path a bounded timeout (e.g. `Wait(TimeSpan.FromSeconds(2))`) and log+continue rather than block indefinitely; dispose `_writeLock` in `Dispose()`.
**Test to add:** a test that starts a slow `SessionStore.Save` (blocking fake), calls `Dispose()` concurrently, and asserts `Dispose()` returns within a bounded time.
**Effort:** S. **Risk:** low — only touches shutdown path.

## Medium

### CORE-03 — `FileActionService`/`RecoveryRetryService` destination is not validated against the source folder root, allowing a misconfigured/corrupted `ReviewAction.Destination` to write outside the intended tree
`src/PhotoReview.Core/FileActions/FileActionService.cs:82-100`, `src/PhotoReview.Core/Settings/ReviewAction.cs:10`

`request.Destination` (e.g. `"Loai-2"`, `"Backup"`) comes from user-editable `AppSettings.Actions` (JSON config file). The only checks are "not same folder as source" (`IsSamePath`) and "not already existing". A `Destination` value such as `..\..\SomeOtherFolder` or an absolute path elsewhere on disk is accepted and silently used (`Path.IsPathRooted` branch, or `Path.Combine` + `..` resolution via `Path.GetFullPath`). Since `config.json` is user-writable (and the ADR 0007 corrupt-config path already anticipates a mangled config file), a bad edit or bad migration can move files to an unexpected location without any warning. This is a design gap more than an external-attacker vector, but it directly affects a "file actions" hot path called out in the review scope (path traversal in action destinations).

**Fix:** when `Destination` is relative, reject (or warn and clamp) values that resolve outside `Path.GetDirectoryName(source)`'s subtree unless explicitly rooted; surface this to the user at Settings-save time rather than at execute time.
**Test to add:** `FileActionServiceTests` case with `Destination = "..\\..\\outside"` asserting either rejection or a documented, intentional behavior.
**Effort:** M (needs a product decision on whether escaping should ever be legal, e.g. for Backup-to-another-drive use cases). **Risk:** medium — could break legitimate configs that rely on escaping to a sibling folder; needs a settings-side opt-in rather than a hard block.

### CORE-04 — `OperationJournal.ReadCommittedMovesReverse` can return duplicate/wrong-order entries when a single JSON line spans a bigger fetch than expected due to lack of overlap correctness check
`src/PhotoReview.Core/FileActions/OperationJournal.cs:121-161`

The reverse reader doubles its window and re-reads from `stream.Length - window` each time, but it does not de-duplicate entries found in the smaller window that get read again (fully) in the doubled window before the first line boundary — this is only correct because each retry restarts entirely from `start` and reparses, so it doesn't currently double-count. However, the loop's exit condition `entries.Count >= StartupCommittedMoveLimit || start == 0` combined with the final `Skip(entries.Count - StartupCommittedMoveLimit)` computes the drop count from the **last (smallest, in file order since the buffer is scanned forward)** entries rather than reliably the oldest — because the forward scan inside a backward window produces entries in file (ascending) order for that window, `Skip` correctly keeps the most recent `StartupCommittedMoveLimit`. This part is actually correct on inspection — downgrading this from a bug to a note: **the real issue is performance**, not correctness: for a journal where committed Moves are sparse (e.g. mostly Recycle/Copy operations), the window-doubling can degrade toward a full linear rescan of the tail (each doubling re-parses everything already parsed), i.e. O(n log n) reparsing instead of O(n) in the worst case, partially undermining the ADR 0003 performance goal. Not a correctness bug; downgraded to Low (see CORE-08).

*(Kept here as a documentation note rather than removed, since the initial read suggested a bug; retained severity Medium was not warranted — see Low section for the actual finding.)*

### CORE-05 — `WindowsRecycleBin.TryRestore` COM object walk can leak the RecycleBin's own child items on early return paths
`src/PhotoReview.Platform.Windows/WindowsRecycleBin.cs:43-70`

Inside `foreach (dynamic item in (IEnumerable)items!)`, items that are *not* selected as a match (i.e. `candidates` that don't get chosen, and every non-matching `item` that fails `IsMatch`) are never `Release()`d — only the final `candidates` list is released in the `finally` block (line 95), and only for items that made it into `candidates`. Every enumerated `item` that did **not** match `IsMatch` is a COM RCW that is left for the GC/finalizer instead of `Marshal.FinalReleaseComObject`, which for `Shell.Application` sub-items can pin the underlying Explorer Recycle Bin COM objects noticeably longer than necessary (COM interop is a known source of Explorer-adjacent handle/resource pressure, called out in AGENTS.md's RAM/disk-I/O priorities in spirit if not by name).

**Fix:** wrap the loop body so every enumerated `item` not added to `candidates` is released before continuing to the next iteration (e.g. `try { ... } finally { if (not added) Release(item); }`).
**Test to add:** this needs a `Category=Native` COM test (per repo convention) that recycles N files, restores 1, and checks Recycle Bin folder handle/RCW counts don't grow unboundedly — or at minimum a code-review note since this is hard to unit test.
**Effort:** S. **Risk:** low.

### CORE-06 — `RecoveryRetryService.RetryMoveOrCopy` performs synchronous, potentially slow I/O (`Copy`/`Move`) with no cancellation and no thread hand-off
`src/PhotoReview.Core/FileActions/RecoveryRetryService.cs:34-109`

Unlike `FileActionService.ExecuteAsync`, which always wraps the actual mutation in `Task.Run(...)`, `RecoveryRetryService.RetryMoveOrCopy` is fully synchronous and calls `_fileSystem.Copy`/`_fileSystem.Move` directly on the calling thread. If the Recovery window's "Retry" button handler calls this directly from the UI thread (which is architecturally likely, since it's a simple synchronous method), a large file being retried across drives will freeze the UI for the duration of the copy — exactly the class of "review speed" regression AGENTS.md's mandatory priorities call out, and inconsistent with how the primary action path was built.

**Fix:** either make this async (`Task<RecoveryRetryResult>`) with the mutation in `Task.Run(...).ConfigureAwait(false)`, matching `FileActionService`, or verify/document that all callers already dispatch it off the UI thread.
**Test to add:** an `AppThreadAffinityTests`-style check, or an integration test asserting the Recovery window invokes this off the UI thread.
**Effort:** M. **Risk:** low-medium (changes a public method's signature/call sites).

## Low

### CORE-07 — `FileActionService`/`RecoveryRetryService` duplicate the Move/Copy verify-and-journal sequence
`src/PhotoReview.Core/FileActions/FileActionService.cs:119-161`, `src/PhotoReview.Core/FileActions/RecoveryRetryService.cs:70-107`

Both classes independently implement: create Prepared entry → perform Copy/Move → stat destination → compare `Length` → throw/return coded failure → append Committed, with near-identical try/catch-around-journal-append fallback logic. This is a maintainability smell (a fix to one, e.g. also comparing `LastWriteUtc` post-move, is easy to apply to only one copy) rather than a correctness bug today.

**Fix:** extract a shared `MutationVerifier`/`JournalTransaction` helper used by both.
**Effort:** M. **Risk:** low (pure refactor, needs good test coverage first).

### CORE-08 — `OperationJournal.ReadCommittedMovesReverse` re-parses the same bytes on every window doubling
`src/PhotoReview.Core/FileActions/OperationJournal.cs:126-160`

See note under CORE-04: each retry re-reads and re-`JsonSerializer.Deserialize`s the entire (doubled) window from scratch rather than only the newly-added bytes before the previous window's first line boundary. For a journal that is mostly non-Move entries (e.g. a heavy Recycle/Copy user), this degrades toward re-parsing large parts of the tail multiple times. ADR 0003's target is "<100ms for a 100k-line journal"; worth re-measuring with a Move-sparse fixture, not just a Move-heavy one.

**Fix:** track the previously-parsed start offset and only parse the new prefix on each retry, prepending to the previous result.
**Test to add:** a perf test fixture with e.g. 1 Move per 1000 Recycle entries, asserting the ADR 0003 latency budget still holds.
**Effort:** M. **Risk:** low.

### CORE-09 — `InstanceLock.Dispose()` swallows `ApplicationException` broadly
`src/PhotoReview.Platform.Windows/InstanceLock.cs:19-23`

`catch (ApplicationException) { }` on `ReleaseMutex()` silently swallows *any* `ApplicationException`, not just the specific "mutex not owned by calling thread" case `ReleaseMutex` can throw. This is unlikely to matter in practice (the mutex is process-local and `IsOwner` gates the call), but a blanket catch on a broad exception type without at least a log line makes future debugging of an actual ownership bug invisible.

**Fix:** log via `FileLog`/`ILog` even in the swallow case, or narrow further if a more specific exception type is available.
**Effort:** S. **Risk:** none.

### CORE-10 — `SettingsStore.Load` treats `IOException`/`UnauthorizedAccessException` as full-defaults-and-return, discarding user settings, but does not attempt the corrupt-file backup path other errors get
`src/PhotoReview.Core/Settings/SettingsStore.cs:62-68`

Compare with the `JsonException` branch (lines 50-61), which backs up the corrupt file before falling through to defaults+`Save`. The `IOException`/`UnauthorizedAccessException` branch instead directly returns in-memory defaults **without ever calling `Save`** — meaning a transient I/O error (e.g. antivirus lock at startup) can cause the app to run with defaults for the session, but the next `Load()` (if the transient condition clears) does not know anything happened; more importantly, if the user then changes and saves settings, `Save()` will overwrite the (possibly fine) original file's content is fine since Save always writes current in-memory state — but the *original* file is never inspected/backed-up, so if the "inaccessible" condition was actually a symptom of real corruption/lock rather than a race, there's no diagnostic trail like the corrupt-json case gets.

**Fix:** at minimum, this is intentional per the inline comment ("using in-memory defaults for this session") and is arguably fine — reclassify as informational; recommend a one-line log difference (info vs error) so operators can distinguish transient lock vs the JSON-corrupt path from logs alone.
**Effort:** S. **Risk:** none.

### CORE-11 — `WindowsMemoryProbe.GetSnapshot()` has no test coverage for the `GlobalMemoryStatusEx` failure path, and callers (`HasHeadroom`, `IsMemoryPressureHigh`) treat a P/Invoke failure as "no headroom" / "pressure high", which silently disables the 16GB-RAM-usage strategy from AGENTS.md rather than surfacing the failure
`src/PhotoReview.Platform.Windows/WindowsMemoryProbe.cs:35-52`

If `GlobalMemoryStatusEx` ever fails (extremely rare but possible under resource exhaustion), `HasHeadroom` returns `false` and `IsMemoryPressureHigh` returns `true` — i.e. the app silently falls back to the most conservative (low-RAM) caching behavior exactly at the moment AGENTS.md's "maximize RAM utilization" strategy matters most, with only a log line (`_log.Error`) and no user-visible signal or metric. This is a defensible fail-safe default, but it's undocumented as intentional and has no test.

**Fix:** add a code comment confirming "fail closed" is intentional; consider surfacing a one-time diagnostic banner if this path is hit repeatedly.
**Test to add:** none required for the P/Invoke itself (can't fake `GlobalMemoryStatusEx` without an abstraction seam), but the pure logic (`HasHeadroom`/`IsMemoryPressureHigh` given a snapshot) has no unit test at all — `MemorySnapshot`-based unit tests are feasible without touching the P/Invoke.
**Effort:** S. **Risk:** none.

### CORE-12 — `WindowsNaturalComparer.Compare` catches only `DllNotFoundException`/`EntryPointNotFoundException`, not a general P/Invoke marshaling failure
`src/PhotoReview.Platform.Windows/WindowsNaturalComparer.cs:22-36`

If `shlwapi.dll`'s `StrCmpLogicalW` throws something else transiently (e.g. `AccessViolationException` is uncatchable anyway, but a `SEHException` wrapping a transient COM/OS hiccup is plausible in exotic environments), sort order for the whole catalog could throw uncaught mid-navigation. Low likelihood; flagged only because file/Explorer-order correctness is explicitly in scope.

**Effort:** S (broaden catch or leave as-is with a comment justifying the narrow catch). **Risk:** none — informational only, not requiring immediate action.

## Missing/weak test coverage

- No test found that exercises `UndoService.UndoMoveAsync`/`UndoLastAsync` from a captured `SynchronizationContext` to catch the CORE-01 regression (a `WindowsFormsSynchronizationContext`-style fake context is enough to assert `ConfigureAwait(false)` is respected without needing a real UI thread).
- No `SessionWriter.Dispose()` concurrency/timeout test (CORE-02).
- No test for `FileActionService`/`RecoveryRetryService` with a `Destination` that resolves outside the source's folder tree (CORE-03).
- No test for `OperationJournal.ReadCommittedMovesReverse` performance/behavior on a Move-sparse large journal (CORE-08), only (presumably) Move-heavy fixtures per the ADR 0003 acceptance test description.

## Summary

- Critical: 0
- High: 2
- Medium: 4
- Low: 6
