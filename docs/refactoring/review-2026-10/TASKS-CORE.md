# Review 2026-10: Core tasks (RV-C*, RV-S*)

Conventions, gates and PR mapping: [`PLAN.md`](PLAN.md). Paths are under `src/PhotoReview.Core/` unless stated;
tests under `tests/PhotoReview.Core.Tests/`. Line numbers at `151f4964`.

---

## PR 1 `fix/rv-fileactions-undo` **[strong]** — RV-C01, C02, C03, C06, C07, C08

### RV-C01 — Move undo fails when the destination volume rounds mtime · MED · CONFIRMED · needs RV-D1
- **Where:** `FileActions/UndoService.cs:123` (fingerprint stored from `result.LastWriteUtc`, the SOURCE stamp),
  `:204` (fallback from the committed journal record), `:208-213` (single undo compare), `:362-363`, `:369`
  (group undo); `FileActions/FileActionService.cs:384` (`RestoreMovedMembers` compensation after a mid-group failure).
- **Problem:** the destination's `LastWriteUtc` is compared for exact equality with the pre-move source stamp. A
  cross-volume Move (allowed: `ActionDestinationPolicy` accepts rooted destinations) onto FAT (2 s) or exFAT (10 ms)
  rounds the stamp, so Ctrl+Z always reports "destination changed after move", and a failed group Move leaves members
  "stuck" at the destination. `RecoveryFileCheck.cs:127` already skips the destination mtime for this reason.
- **Step 1 — reproducer tests** (`FileActions/UndoServiceTests.cs`, `FileActions/UndoServiceGroupTests.cs`,
  `FileActions/FileActionServiceTests.cs`): make the in-memory file system round the destination stamp
  (add an opt-in hook to `tests/PhotoReview.Core.Tests/Fakes/InMemoryFileSystem.cs`, next to its existing hooks
  covered by `IO/InMemoryFileSystemHookTests.cs`, e.g.
  `Func<string, DateTime, DateTime>? StampOnMove`, that truncates to 2 s for paths under a "FAT" root).
  - `UndoMoveAsync_DestinationOnFatRoundedStamp_RestoresFile`
  - `UndoGroupMoveAsync_DestinationOnFatRoundedStamp_RestoresAllMembers`
  - `ExecuteGroupAsync_MoveFailsMidway_FatDestination_CompensationRestoresMovedMembers`
  - Guard (must stay red→green semantics): `UndoMoveAsync_DestinationSizeChanged_Refuses` and
    `UndoMoveAsync_DestinationStampMovedBy3Seconds_Refuses` (proves the tolerance is bounded).
- **Step 2 — fix (option A of RV-D1):** add `internal static class FileFingerprint` in `FileActions/` with
  `MatchesMovedDestination(FileStat stat, long size, DateTime recordedUtc)` = `stat.Length == size &&
  Math.Abs((stat.LastWriteUtc - recordedUtc).Ticks) <= TimeSpan.FromSeconds(2).Ticks`. Use it at EVERY
  destination-side compare (UndoService 208-213, 362-363; FileActionService 384). Leave source-side compares
  (same volume as when recorded: FileActionService 369/424/434, RecoveryRetryService 71/128/139/196, UndoService 369/556)
  exact; list each site in the PR description as "source-side, unchanged" or "destination-side, tolerant".
- **Step 3:** mutation: change the tolerance to 0 → the three FAT tests fail; change to 5 s → the 3 s guard fails.
- **Done when:** all five tests green; `decisions/RV-D1.md` added; OPEN-DECISIONS regenerated.
- **Note:** keep the 2026-09-30 decision "group Copy reconcile compares by size only" (raw/PROGRESS) consistent; mention it in RV-D1.md.

### RV-C02 — Single Move/Copy cancelled before it starts leaves a Recovery item · LOW · CONFIRMED
- **Where:** `FileActions/FileActionService.cs:634-637` (`ExecuteAsync`, the `OperationCanceledException` path).
- **Problem:** `Task.Run(..., ct)` cancelled before the delegate runs → disk untouched, but a `Failed(CancelledByUser)`
  line is journaled and shows in Recovery. `ExecuteGroupAsync` uses `tx.DismissRolledBack` for this case.
- **Step 1:** `FileActionServiceTests.ExecuteAsync_MoveCancelledBeforeStart_DismissesJournalEntry` and
  `..._CopyCancelledBeforeStart_...`: pass an already-cancelled token; assert result is cancelled, source unchanged,
  destination absent, and `journal.ReadPendingAndFailedOperations()` is empty.
- **Step 2:** in the OCE handler, re-stat: if the source still matches the preflight stat (size + exact mtime, same
  volume) and the destination does not exist → `tx.DismissRolledBack(...)` (same call shape as the group path);
  otherwise keep the current Failed write.
- **Step 3:** test that a cancel AFTER the move happened still writes Failed (use a file-system hook that cancels the
  token after `Move` returns): `ExecuteAsync_MoveCancelledAfterMove_KeepsFailedEntry`.

### RV-C03 — Single Copy failure leaves a partial destination · LOW · PLAUSIBLE
- **Where:** `FileActions/FileActionService.cs:534` (single Copy), `FileActions/RecoveryRetryService.cs:255`.
- **Problem:** the group path removes created copies (`RemoveCreatedCopies`); the single path does not. A truncated
  destination then makes Recovery report `Conflict` and the retry refuses with `CoreRecoveryDestinationExists`.
- **Step 1:** add an `InMemoryFileSystem` hook that writes N bytes to the destination then throws `IOException`
  ("disk full"). Test `ExecuteAsync_CopyFailsMidway_RemovesPartialDestination`: destination absent afterwards,
  journal entry Failed, `RecoveryFileCheck` verdict for it is retryable (not Conflict).
- **Step 2:** after a failed single Copy: delete the destination only if (a) it did not exist at preflight and
  (b) its length differs from the source length (never delete a complete copy; never delete a pre-existing file).
  Same rule in `RecoveryRetryService` around line 255.
- **Step 3:** guard test `ExecuteAsync_CopyFails_DestinationExistedBefore_IsNotDeleted`.

### RV-C06 — `UndoMoveAsync` clears an unrelated last-undo record · LOW · CONFIRMED
- **Where:** `FileActions/UndoService.cs:251`.
- **Step 1:** `UndoServiceTests.UndoMoveAsync_CalledDirectlyWhileLastActionIsRecycle_KeepsRecycleUndo`: record a Move,
  then a Recycle; call `UndoMoveAsync` for the Move; `UndoLastAsync` must still undo the Recycle.
- **Step 2:** clear `_lastUndoAction` only when it is the Move record just undone (same Source/Destination).

### RV-C07 — Single Move undo does not recreate a removed source folder · LOW · PLAUSIBLE
- **Where:** `FileActions/UndoService.cs:217-236` (single) vs `:394` (group creates it).
- **Step 1:** `UndoMoveAsync_SourceFolderDeletedAfterMove_RecreatesFolderAndRestores`.
- **Step 2:** `_fileSystem.CreateDirectory(Path.GetDirectoryName(move.Source)!)` before the move-back, as the group path does.

### RV-C08 — Recovery retries bypass the INV-4 file-action gate · LOW · CONFIRMED
- **Where:** `FileActions/RecoveryRetryService.cs` (whole class); gate is `FileActionService.TryBegin`.
- **Problem:** only the modal Recovery window prevents an overlap with a running action.
- **Step 1:** `RecoveryRetryServiceTests.Retry_WhileFileActionInProgress_RefusesWithBusy`: hold `TryBegin`, call retry,
  expect a busy result and no file-system calls.
- **Step 2:** take `TryBegin` inside the retry service (release in `finally`); map "busy" to an existing localized
  error or add `core.recovery.busy` (en + vi).
- **Step 3:** App side: the Recovery window shows that error (covered by RV-T64).

---

## PR 2 `fix/rv-settings-load` (sonnet) — RV-S01, S03, S04

### RV-S01 — Unknown enum in config loads as the zero member · MED · CONFIRMED · needs RV-D2
- **Where:** `Model/LenientEnumConverter.cs:21` (parameterless ctor → `default(T)`), used through type-level
  `[JsonConverter(typeof(LenientEnumConverter<X>))]` on each enum in `Model/*.cs`; `Settings/SettingsNormalizer.cs:85-95,144-196`
  (`Enum.IsDefined` branches never fire because 0 is defined).
- **Affected (zero member ≠ `AppSettings` default):** `LoadingMode` Fast≠Preview, `ImageSortMode` Name≠Default,
  `ScalingQuality` Linear≠HighQuality, `DecoderBackend` Wpf≠WicDirect, `KeyboardZoomAnchor` Pointer≠ViewportCentre,
  `KineticGlideSmoothing` Off≠Predict (currently pinned as "unknown → zero" by `Settings/MouseSettingsTests.cs:195`).
  Not affected (zero = default): InitialViewMode, RawFullDecode, RawPairMode, FitWidthAnchor, ImageTransition,
  InstanceMode, JournalDurability, MouseWheelAction.
- **Step 1:** `Settings/SettingsStoreFailureTests.cs`: theory over the 6 properties with values `"garbage"`, `""`,
  `null`, `42`, `{}` → after `Load`, property equals `new AppSettings().<Prop>` and `LastLoadRepairs` contains the
  property name (option A).
- **Step 2 (recommended design):** make the settings deserialization produce an UNDEFINED sentinel for unparsable
  input so the existing normalizer branches do the reset and reporting:
  - add `LenientEnumConverter<T>.ForSettings()` (or a ctor flag) whose fallback is `(T)(object)int.MinValue`
    (not defined for any of these enums);
  - register it via a settings-only `JsonSerializerOptions` in `SettingsStore` (option converters take precedence over
    type attributes only if registered per property — verify; if not, add `[JsonConverter(typeof(SettingsEnumConverter<X>))]`
    on the 6 `AppSettings` properties);
  - make sure every enum property of `AppSettings` has an `Enum.IsDefined` branch in `SettingsNormalizer` that resets
    to `new AppSettings()` value and adds the name (add the missing ones).
  - Do NOT change journal/metrics deserialization (`FileActions/JournalLineParser.cs:85`, dictionary keys).
- **Step 3:** update `MouseSettingsTests.cs:195` to the decided contract; add `Save` round-trip test that a repaired
  value is written back as its PascalCase name.
- **Done when:** RV-D2 recorded; startup repair dialog shows the property (manual check optional, list in PR).

### RV-S03 — Reset of a blank mandatory shortcut can collide · LOW · PLAUSIBLE
- **Where:** `Settings/SettingsNormalizer.cs:115-128`; `DisableConflictingOptionalShortcuts` handles optional ones only.
- **Step 1:** `SettingsRobustnessTests.Load_BlankPreviousShortcutAndActionOnLeft_NoDuplicateBinding`: config with
  `"Previous":""` and an action bound to `Left` → after Load, `SettingsValidator.ValidateShortcuts` returns null.
- **Step 2:** after resetting mandatory shortcuts, run the duplicate check; on a clash keep the mandatory default and
  clear the clashing ACTION shortcut (actions are user-defined, mandatory navigation must work), adding
  `Actions[i].Shortcut` to `LastLoadRepairs`.
- **Step 3:** test blank action `Shortcut`/`Name` and two actions on one key → reported, not left for Save to reject.

### RV-S04 — A throwing `Changed` handler makes `Load` fall back to defaults · LOW · PLAUSIBLE
- **Where:** `Settings/SettingsStore.cs:112-114` (`Changed?.Invoke` inside the IO `try`).
- **Step 1:** `SettingsStoreTests.Load_ChangedHandlerThrowsIOException_KeepsLoadedValues`.
- **Step 2:** move the `Changed` invocation after the `try/catch`, so a handler exception propagates to the caller
  without touching `Current`/`_keepCorruptFile` (do not swallow it: a broken handler must stay visible in tests).

---

## PR 9 `fix/rv-core-misc` (sonnet; RV-C04 **[strong]**) — RV-C04, C05, C09, S02, S05, S06, S07

### RV-C04 — Journal torn-tail repair runs only once per process · LOW · PLAUSIBLE
- **Where:** `FileActions/OperationJournal.cs:211-240` (`AppendLines`, `_tailChecked`).
- **Problem:** in PerFolder multi-instance mode another process can crash mid-append after this process set
  `_tailChecked`; the next append glues onto the torn line and both records are lost.
- **Step 1:** `OperationJournalTests.Append_AfterOtherWriterLeftTornTail_StartsOnNewLine`: append once (sets the flag),
  write `"{\"partial"` with no newline through a second `FileStream`, append again → both complete lines parse,
  the torn fragment is isolated.
- **Step 2:** under the existing append lock, read the last byte on every append (1-byte read at `Length-1`, cheap)
  and prepend `\n` if it is not `\n`; drop `_tailChecked`. Measure: note the extra read in the PR (no perf regression
  expected; appends are per user action).

### RV-C05 — `SessionStore.Load` throws on a malformed folder string · LOW · PLAUSIBLE
- **Where:** `Session/SessionStore.cs:87-90` (`GetPath` → `Path.GetFullPath` outside the try).
- **Step 1:** `SessionStoreTests.Load_FolderWithEmbeddedNul_ReturnsNoSession` and `..._OverlongPath_...`.
- **Step 2:** move `GetPath` inside the try; catch `ArgumentException`, `NotSupportedException`, `PathTooLongException`
  (same set as `SessionWriter.KeyOf`).
- **Step 3:** `WriteAllTextAtomic_RenameFails_LeavesNoTempFile`.

### RV-C09 — `ReviewCatalog.Remove` sets `CurrentIndex` wrongly for a non-current removal · LOW · CONFIRMED
- **Where:** `Catalog/ReviewCatalog.cs:282-284`.
- **Step 1:** `ReviewCatalogTests.Remove_EntryBeforeCurrent_KeepsSameCurrentPath` and `Remove_EntryAfterCurrent_...`.
- **Step 2:** if `removedIndex < CurrentIndex` → `CurrentIndex--`; if `== CurrentIndex` → current rule; if `>` → unchanged.
  Update the doc comment.

### RV-S02 — `DiagOptions` parses with the current culture · LOW · CONFIRMED
- **Where:** `Diagnostics/DiagOptions.cs:84` (`int.TryParse`), `:51` (`Describe` interpolation).
- **Fix:** `int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)`;
  `string.Create(CultureInfo.InvariantCulture, $"...")` in `Describe`.
- **Test:** `tests/PhotoReview.App.Tests/DiagOptionsTests.cs` → `ReadFromEnvironment_UnderArabicCulture_ParsesWorkers`
  (set `CultureInfo.CurrentCulture` to a culture with native digits inside the test, restore in finally; `[Collection("GlobalState")]`).

### RV-S05 — Forward protocol: lone surrogate makes `Encode` throw · LOW · PLAUSIBLE
- **Where:** `Instance/ForwardedPathProtocol.cs:35` (`StrictUtf8.GetBytes`), `:70-75` (`IsAcceptablePath`).
- **Step 1:** `ForwardingCoreTests.Encode_PathWithLoneSurrogate_ThrowsArgumentException`,
  `TryDecode_BytesOfLoneSurrogate_ReturnsFalse`, `IsAcceptablePath_LoneSurrogate_ReturnsFalse`.
- **Step 2:** reject lone surrogates in `IsAcceptablePath` (scan with `char.IsSurrogatePair` / `Rune.DecodeFromUtf16`);
  `Encode` calls it first and throws `ArgumentException`.
- **Note:** the second instance then opens its own window for that path (as today), which is acceptable; mention in PR.

### RV-S06 — Circular reparse point makes `ResolveRealPath` throw · LOW · PLAUSIBLE
- **Where:** `IO/PhysicalFileSystem.cs:342` (`ResolveLinkTarget(returnFinalTarget: true)`); caller
  `FileActions/ActionDestinationPolicy.ValidateNoEscapeViaReparsePoint`.
- **Step 1:** first check how the caller handles `IOException` today (read it). New `Category=Native` test class
  `tests/PhotoReview.Integration.Tests/PhysicalFileSystemReparseTests.cs`: symlink loop `a→b→a` in a temp dir
  (skip if symlink creation needs privilege: detect and return early with a message) → `ValidateNoEscapeViaReparsePoint`
  returns `EscapesSourceFolder` (fail closed), no exception.
- **Step 2:** catch `IOException` in `ResolveIfReparsePoint` and return a value the policy treats as "cannot verify"
  → reject. Fail closed is mandatory (SEC-01).

### RV-S07 — `ImmediateUiScheduler.InvokeAsync` throws synchronously · LOW · PLAUSIBLE
- **Where:** `Abstractions/ImmediateUiScheduler.cs:18`.
- **Fix:** `try { action(); return Task.CompletedTask; } catch (Exception ex) { return Task.FromException(ex); }`.
- **Test:** `ImmediateUiSchedulerTests.InvokeAsync_ActionThrows_ReturnsFaultedTask` (new small class in Core.Tests).
- **Check:** run the whole App.Tests suite; tests that relied on the synchronous throw must be updated to await.
