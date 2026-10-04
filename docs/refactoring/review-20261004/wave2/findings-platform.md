# Findings: Platform.Windows (wave 2)

103 ledger rows: 94 OK, 8 ISSUE, 1 NEEDS-EVIDENCE, 0 REMOVED (the ISSUE rows are the affected functions of 5 independent findings: 1 P2, 4 P3 and 1 NEEDS-EVIDENCE P3; no P1). Platform.Windows has no commits since baseline `5d291076`, so there are no `N-platform-*` rows. No open PR touched Platform.Windows when checked (#284, #294, #296, #299, #300 and the fix branches). Ledger: [ledger-platform.tsv](ledger-platform.tsv). Source: report of the review agent, written into the repo by the lead because the agent's Write tool refused report files. Only the `GetVolumePathNameW` junction behaviour in P-RB-01 was reproduced (read-only probe, junction removed); every other finding is traced by reading. Nothing touched the real Recycle Bin and no test suite was run.

Accepted trade-offs are marked OK in the ledger with a citation: the Q-R27 marker degrading to pre-Q-R27 reconcile (`ILiveOperationRegistry` docs); permanent delete only after the Q-R8 opt-in (`FileActionService.cs:588-592`); the 200 ms pump join (R2-F-30); the fail-closed capacity guard (F-WIN-2, ADR 0007 amendment); the restore timeout, absorbed by `UndoService.cs:326-330`.

## P-RB-01 (P2, ISSUE) Recycle eligibility and the capacity guard look at different volumes
Functions: F03132 (root cause), F03120, F03088. Code: `WindowsRecycleBin.cs:235-249` (`RecycleEligibility.CanRecycle`), `:36-58` (`SendToRecycleBin`), `RecycleBinCapacity.cs:84-118` (`EvaluateCore`) and `:181-189`.
- `CanRecycle` uses `Path.GetPathRoot`, the textual root, and requires `DriveType.Fixed`. The F-WIN-2 guard resolves the real volume with `GetVolumePathNameW`, which follows junctions and mounted folders.
- A path like `C:\link\usb\x.jpg`, a junction to a USB stick or network drive, or a volume mounted as a folder, looks like fixed `C:` to `CanRecycle`. The guard resolves the USB volume, which has no `BitBucket\Volume\{guid}` entry, and `EvaluateCore` treats "no entry" as a default bin (`Fits` for size-less checks, or 5% of quota).
- If the shell resolves the real volume, it deletes permanently without a prompt while the journal records a Recycle, so Ctrl+Z cannot restore. This end is only traced by reading.
- **Partly reproduced:** on the review machine a junction to `D:\` gave `GetVolumePathNameW = D:\` against `GetPathRoot = C:\`. The probe was read-only and the junction was removed. What the shell does next was not run. Evidence: `wave2/evidence/P-RB-01-junction-eligibility.cs.txt` (a probe to repeat with a USB or network target; it never calls the shell).
- Remedy: decide eligibility from the mount point `GetVolumePathName` resolves, and require that volume to be Fixed. Have `EvaluateCore` return Unknown for a non-fixed volume. Add `RecycleEligibilityTests` cases with an injected resolver.

## P-EXP-01 (P3, ISSUE) Failed Explorer metadata reads look like "none"
Functions F03039, F03042. Code: `ExplorerOrderService.cs:355` and `:398` (`GetGroupBy(...) >= 0 && ...`), `:482-489` (`ReadSortColumns` returns `[]`). A failing `GetGroupBy` or `GetSortColumns` HRESULT reports `GroupState.None` or an empty array instead of `Unknown`. Only `DiagnosticsWindow.xaml.cs:35` shows these values today; order data is safe because `ExplorerSnapshotValidator` demands set equality with the scanned files.
- Remedy: map failures to `Unknown`.

## P-COM-01 (P3, ISSUE) COM enumerator RCWs are not released explicitly
Functions F03036, F03125. Code: `ExplorerOrderService.cs:311` (the `Cast<object>` enumerator over `Windows()`), `WindowsRecycleBin.cs:119` (the `foreach` over `Items()`). Every other RCW in these paths is `FinalReleaseComObject`'d; these `IEnumVARIANT` RCWs are left to the finalizer.
- Remedy: obtain the enumerator explicitly and `Release` it in the existing `finally` blocks.

## P-DISP-01 (P3, ISSUE) vblank clock failures are never logged
Function F03101. Code: `WindowsDisplayClock.cs:40-48`; callers at `:106-110` (`TryOpen` false), `:113-117` (non-zero wait result) and `:122-125` (DLL or entry point missing). They mark the monitor failed and fall back to DWM timing, which describes the primary display only, with no diagnostic.
- Remedy: one `_log.Warn` in `MarkFailed`, with the monitor handle and the failing step.

## P-DISP-02 (P3, NEEDS-EVIDENCE) The vblank thread may park forever while the display is off
Function F03103. Code: `WindowsDisplayClock.cs:76-121`, with the blocking `D3DKMTWaitForVerticalBlankEvent` call at `:113`. With the monitor off or the session locked there may be no vblank, so the idle-exit check at `:90-96` is never reached; `_thread` stays set and the stale `_published` phase keeps being returned. Not proven: it needs a display-sleep run, which was not attempted because it would blank the screen.
- Remedy if confirmed: wait with a timeout so the idle exit stays reachable.

## Suspicions dropped, with evidence
- Restore running on the UI thread: a probe showed an MTA caller of `Shell.Application` is not served by the main STA (`F03125-sta-hosting-probe.cs.txt`).
- `WindowsNaturalComparer` inconsistent ordering: a 220-string fuzz found 0 transitivity and 0 antisymmetry violations (`F03116-natural-comparer-fuzz.cs.txt`).
- Win11 tabs returning the wrong view: `ExplorerSnapshotValidator` rejects any snapshot whose paths or set differ from the scanned files.
- `WaitForRestore` 1.8 s timeout: absorbed by `UndoService.cs:326-330`.

## Test gap, not a defect
`ExplorerNativeVtable` has no direct test. The agent counted the vtable slots, IIDs and SIGDN/SVGIO constants against the documented method order and found them correct. A regression there would only show at runtime as a fallback or `NativeViewUnavailable`.
