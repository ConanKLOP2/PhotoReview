# App review — 2026-10-04

Baseline: `5d2910761f14bd99745b9c4bdcd636b36263e716`. Production read-only. All `src/PhotoReview.App` callable bodies inventoried by lead were read in their enclosing files. `STATIC-ONLY` means source semantics inspected with file/function context, not runtime PASS. Lambda/helper annotations explicitly inherit enclosing review. No build, GUI, native, recycle-bin or perf gate was run by this worker. Lead owns validation.

## Findings

### APP-R01 — P1 — A queued file command changes target folder

`src/PhotoReview.App/ViewModels/MainViewModel.cs:534-547`, also `:1193-1197`.

`RunActionAsync`, `RecycleAsync` and `MoveOrCopyToFolderAsync` put closures into FIFO gate. Source/compare selection are read only when that closure executes, without capturing the folder at submission. Repro: start blocked Move in A, enqueue second Move/Delete, open B through existing forwarded/drop path, then release A's Move. The second command moves/recycles B's current photo, although it was submitted while A was on screen. Folder switching while a file action runs is intentionally supported by INV-5 and existing tests. Gate guarantees mutual exclusion, not request scope.

Fix: capture a distinct folder-load/session identity at submission and reject queued commands when it changes, while still choosing next source live for repeated actions within the same folder. `CurrentFolder` alone cannot be that identity because `StopForAction` increments it for every action. Add deterministic two-command folder-switch regression. Safe draft `ReviewQueueRace.cs` reuses temp fixture + fake services; lead can copy into App.Tests and run bounded filter. No real bin.

Existing tests reviewed: `RunActionAsync_ConcurrentAction_IsQueued_PreservingInv4` checks same-folder FIFO only; `RunActionAsync_WhenFolderSwitchedDuringIo_RegistersUndo_AndLeavesNewFolderUntouched` checks a single operation, so neither guards the queued command's destination scope.

### APP-R02 — P2 — Superseded Fit reappears in scrollbar correction

`src/PhotoReview.App/Input/PointerInputController.cs:223-224`, `:234-235`, `:257-258`, `:280-285`.

`ZoomToImagePointAsync` returns without scrolling when a newer viewport operation changes version during render yield. Its callers nevertheless run `CorrectForSideScrollbarAsync`. When the fitted axis now overflows (e.g. later 200% zoom), that helper starts a fresh version and reapplies old Fit width/height, overriding the newer choice. Repro: hold first FitWidth render yield, supersede/set 200%, keep ExtentWidth > ViewportWidth, release. Final zoom becomes fitted width instead of 200%. Same hole in initial Fit width/height.

Fix: return operation success/version from first pass and require same version + loaded surface before correction; correction belongs to the existing operation. Safe draft `ReviewFitRace.cs`. Existing tests separately cover generic wheel supersession and Fit scrollbar overflow, never their combination.

### APP-R03 — P2 — File-action failure status written into a newer folder

`src/PhotoReview.App/Coordinators/FileActionController.cs:368-370`, `:401-410`, `:423-432`, `:446-454`.

After service completion the folder is checked once. The failure/unverified/error-postprocessing branches then await the pending next-image presenter and update status (and unverified-Move session) without rechecking folder. If source IO reports failure in A, next-image presentation stays blocked, and B opens before that presentation finishes, A's action error replaces B's ready/empty/error message. Unverified-Move branch can update the new session using fallback source from A when B is empty. Successful missing-partner warning branch correctly rechecks after its await, showing the intended pattern.

Fix: recheck `_clock.IsFolderCurrent(folderGen)` after every awaited presenter before touching session/status; retain actual action/Undo result independently. Current folder-switch tests switch before service completion, not after service completes while presenter is awaited. Static repro traced; dedicated runtime reproduction pending lead.

### APP-R04 — P2 — Undo result reopens old folder after a newer user open

`src/PhotoReview.App/ViewModels/MainViewModel.cs:581-599`.

`FileActionController.UndoLastAsync` checks stale folder after await and returns the result without modifying new catalog. `UndoCoreAsync` receives that successful result and then runs `RestoresOutsideFolder` against the folder captured before Undo; Recycle always qualifies. Repro: Undo recycled photo from A (or Move from prior A while currently in B); block restore; user opens C; release restore. Controller drops catalog changes, but caller reopens A anyway and overrides user's current selection. Existing `OC14_FolderSwitchMidUndo...` uses a same-folder Move, for which `RestoresOutsideFolder` is false, so misses Recycle/prior-folder Move.

Fix: capture folder-load identity in caller and recheck before opening undo source and before restoring partial-note status after its reload. This preserves normal deliberate Ctrl+Z reopening while user stays in same folder. Static repro traced; runtime regression pending.

### APP-R05 — P2 — Full-resolution bitmap swap erases crossfade identity

`src/PhotoReview.App/Coordinators/ImagePresenter.cs:130-151`, `:777-791`.

`ShowZoomDetailImageCore` calls `UpdateCurrentImage(image,w,h)` without `path`. `UpdateCurrentImage` overwrites `_presentedFilePath` with null although same file remains shown. Next navigation to a different photo calls transition decision with previousPath=null and skips enabled Fade. Repro: enable Fade, zoom current image until original replaces preview (or return Fit using held original), then Next. Same-source swap should be instant, but must retain file identity for next navigation.

Fix: preserve previous `_presentedFilePath` for non-null same-source swaps, or pass current navigation path while forcing `isFileChange=false`. Crossfade integration tests verify a normal Next and same-image zoom separately, not Next after the swap.

## Related Core findings

Core lane owns missing finite validation for `InfoOverlayFontSize` (NaN config can fail startup repair persistence), and missing normalization for `KeyboardZoomStepPercent` (negative value reverses ZoomIn/ZoomOut). App call-site trace supplied to Core; no duplicate issue here.

## Coverage and limits

Source inventory: 1203 bodies, including constructors, explicit accessors, local functions and lambdas. `review-app.tsv` contains exact IDs/path/line/signature with ISSUE or STATIC-ONLY after complete source pass. No independent native/GUI/T89 acceptance or mutation-proof claims.

App + Integration test inventory: 4820 bodies. Selective relevant FIFO/folder switch/Fit/crossfade oracle reads above completed; broad test review continues. Unreviewed bodies remain explicit in TSV, never marked PASS from gate alone. Existing tests' fakes/helpers may be reused by repro drafts; drafts are not baseline production changes.
