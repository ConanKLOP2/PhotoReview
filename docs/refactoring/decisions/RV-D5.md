---
id: RV-D5
order: 55
summary: |-
  Decided 2026-10-01 (option A): a duplicate batch-recycle that finishes after the user opened another folder reports a late-completion status (`IDuplicateCleanupSink.ShowLateActionStatus`, text `status.lateBatchRecycled`: counts, "cannot be undone with Ctrl+Z") instead of returning silently; it stays non-undoable and reloads nothing.
---

# RV-D5 - late completion of the duplicate batch (RV-A10)

`DuplicateCleanupController.RemoveDuplicatesAsync` recycles the numbered copies one by one. If the user opens another folder
while the loop runs, the loop finishes (the files are already in the Recycle Bin) and used to return with no message.

Options considered:
- **A (chosen)** report the counts through a late-completion sink, like `FileActionController.ReportLateCompletion`. The sink
  method is `IDuplicateCleanupSink.ShowLateActionStatus` (default = plain `SetStatusText`; `MainViewModel` routes it to the
  same idle-only rule as the file-action late status, so the new folder's own status is never overwritten). The batch stays
  NON-undoable (it is in every folder state: there is no group Undo for a batch of independent Recycles); the text says so and
  points to the Recycle Bin. Nothing is reloaded in the folder the user is looking at.
- B also register a group Undo for the batch: new undo-stack semantics for N independent entries; larger change, not needed to
  stop the silence.

Tests: `DuplicateCleanupControllerTests.RemoveDuplicates_FolderChangedDuringBatch_ReportsLateCompletion` (folder switch after
the 1st of 3 recycles -> one late status with 3/0, no reload, no "Batch done"), `RemoveDuplicates_OneFileFails_StatusCountsTheFailure`.
