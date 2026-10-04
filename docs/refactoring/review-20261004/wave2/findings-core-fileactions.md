# Findings: Core/FileActions (wave 2)

226 functions reviewed in `src/PhotoReview.Core/FileActions`: OK 207, ISSUE 10, NEEDS-EVIDENCE 9, REMOVED 0 (19 flagged rows map to 10 findings, W2-FA-01..10; highest severity P3, no P1/P2). Master `fc1d7a53`. Ledger: [ledger-core-fileactions.tsv](ledger-core-fileactions.tsv). R01 is verified fixed on master (#290, `CopyCreationProof`; all three cleanup sites re-read) and is not re-reported. No new `N-core-fileactions-NNN` rows. Open PRs #294, #296, #299, #300, #284 do not touch FileActions. Source: report of the review agent, written into the repo by the lead because the agent's Write tool refused report files.

Reproduction method: a scratch console project outside the repo referencing the built `PhotoReview.Core.dll`, own temp files only (no Recycle Bin, no real folders); sources in `evidence/repro-journalparser-commalist.cs.txt` and `evidence/repro-undo-recycle-verdict-and-surrogate.cs.txt` (never compiled by the build). No Stryker and no repository test run. W2-FA-01, 02 and 03 (serialization part only) were reproduced; W2-FA-04, 05, 06 and 10 were traced by reading; W2-FA-07, 08 and 09 are suspected, not proven.

Rows with a finding: ISSUE F01639, F01657, F01687, F01688, F01707, F01709, F01739, F01792, F01799, F01808. NEEDS-EVIDENCE F01677, F01681, F01705, F01706, F01756, F01763, F01769, F01813, F01816.

## W2-FA-01 (P3, reproduced) Journal line parser accepts comma-list enum text
F01707, F01709. `JournalLineParser.cs:81-88` (`IsKnown`) and `:97-103` (`RecordingConverter.Read`). `IsKnown` uses `Enum.TryParse`, which accepts comma lists and ORs the values even for a non-`[Flags]` enum. `LenientEnumConverter.ParseText` accepts only a single name or number and returns the default member (Move/Prepared) otherwise, so recognition and value disagree.
- Scenario: a corrupt line with `"Type":"Recycle","State":"Prepared,Committed"` is accepted and read as Recycle/Prepared, an invented pending operation. `"Move,Recycle"` is read as Move. The writer never emits this.
- Reproduced by reflection on `JournalLineParser.TryParse`.
- Remedy: make `IsKnown` use the converter's lookup, or reject any text containing `,` first. Add a `JournalLineParserTests` case.

## W2-FA-02 (P3, reproduced) Undo Recycle group aggregated as RecycleUnverifiable
F01792, F01799. `RecoveryFileCheck.cs:163-170` (`AggregateGroupVerdict`). An Undo Recycle group with every member back at its original path (all AlreadyDone) is aggregated as `RecycleUnverifiable`, because the generic rule maps `Type==Recycle` to that verdict.
- Scenario: a group undo fails part-way and the user restores the rest by hand. Recovery then says "probably in the Recycle Bin, cannot verify" although all files are on disk. No data loss.
- Reproduced: member verdicts AlreadyDone, AlreadyDone, aggregate RecycleUnverifiable. `RecoveryFileCheckMutationTests` asserts only the per-member verdicts.
- Remedy: return AlreadyDone when `entry.Undo == true` in the generic branch, and assert the aggregate in that test.

## W2-FA-03 (P3, serialization reproduced) Unpaired UTF-16 surrogate in a journaled path
F01739. `OperationJournal.cs:233-235` (`AppendLines`, `JsonSerializer.Serialize`). An unpaired surrogate in a path is silently written as U+FFFD, so the journaled Source/Destination does not name the real file. Windows allows such file names.
- Scenario: the process dies mid-move of `a<U+D800>.jpg`. Reconcile and Recovery then judge a path that does not exist, so they cannot confirm or recover the real file. They never act on a wrong file. The reconcile outcome was traced by reading only.
- Remedy: reject such paths before journaling with a coded error, or serialize with an encoder that throws. Add a test next to `JournalAppendRobustnessTests`.

## W2-FA-04 (P3, traced) Compaction drops the Prepared line that group repair needs
F01687, F01688. `JournalCompactionPlan.cs:88-96` (`IsDroppable`), with `OperationJournal.cs:508-538` and `JournalStartupRecovery.cs:26-30`. The plan drops a Prepared line that carries `GroupMembers` once the Id has a later Committed line. `RepairGroupLinesSettledByOlderBuild` needs that earlier line (`groupHistory`) to detect a group that an older build settled without members. The class comment's four-reader argument omits this reader.
- Scenario: an older build settles a group Committed from its first member only. The new build's repair append then fails (disk full or AV lock, so `TryReconcileAppend` returns false). `JournalStartupRecovery` still runs Compact, which erases the evidence. A possibly half-moved capture is no longer detectable.
- Remedy: keep the last members-carrying line of an Id whose latest line lacks members, or skip compaction after a failed repair append. Add a plan test.

## W2-FA-05 (P3, traced) RecoveryRetryService reports "source changed" for every non-CanRetry verdict
F01808. `RecoveryRetryService.cs:124-126`. Any non-CanRetry verdict (AlreadyDone, Lost, Conflict, DestinationChanged, Unknown) returns `Tr.CoreRecoverySourceChanged`. It shows up only in races, since the Recovery window offers retry only for CanRetry.
- Remedy: map AlreadyDone to AlreadyHandled/Superseded and give Conflict/DestinationChanged their own texts.

## W2-FA-06 (P3, traced) Unlocalized argument error and folder created before Prepared
F01657. `FileActionService.cs:86-87`. A group with fewer than 2 paths throws an English `ArgumentException("A grouped file action requires at least two paths.")`; its Message is shown as the failure text, unlocalized. Nothing is journaled or mutated. Likely unreachable from `FileActionController`.
- Secondary: `CreateDirectory(destinationFolder)` at line 165 runs before `tx.BeginAsync`, so a journal append failure leaves an empty folder. `ExecuteAsync` does the same at line 522.
- Remedy: use a `Tr` string or a Rejected result, and optionally create the folder after Prepared.

## W2-FA-07 (P3, NEEDS-EVIDENCE) NUL in a group member path can abort reconcile at every start
F01705, F01706, F01756, F01763, F01769. `JournalLineParser.cs:70,78-79`; `OperationJournal.cs:446, 513-538, 574-594`. Entry-level paths reject NUL, but member paths in group lines do not. `GetFileStat` is expected to throw `ArgumentException` for NUL. `RepairGroupLinesSettledByOlderBuild` is called outside the per-entry try block.
- One corrupt group line could then abort `ReconcilePendingOperations` on every start. `JournalStartupRecovery` logs it and returns `[]`, and the compaction in the same `Task.Run` is skipped.
- Not reproduced: it needs a NUL member path plus the older-build signature.
- Remedy: validate member Source and Destination with `IsUsablePath`, and guard each repair iteration like `ReconcileOne`.

## W2-FA-08 (P3, NEEDS-EVIDENCE) Retry does not re-run the destination policy checks
F01813, F01816. `RecoveryRetryService.cs:188-258` (esp. 221-229) and `292-331`. Retry replays the journal's destination without re-running `ActionDestinationPolicy.Validate` or `ValidateNoEscapeViaReparsePoint` (SEC-01), which the first run enforces. A relative destination folder replaced by a junction after the original action would be followed outside the photo folder. Hardening only, since the same user controls those files. Not reproduced.
- Remedy: re-run the checks before a retry's first mutation. Needs a journaled "was relative" flag.

## W2-FA-09 (P3, NEEDS-EVIDENCE) Single-file path lacks the group path's post-condition checks
F01677, F01681. `FileActionService.cs:546-554` and `613-617`. A failed single Move has no partial-destination cleanup, unlike the group path. Single Recycle and DeletePermanently are not re-checked with `FileExists(source)`, unlike the group path at lines 217/222. `WindowsRecycleBin.SendToRecycleBin` already throws `CoreRecycleNotDeleted` when the file is still there, so this is defence in depth. Not reproduced.
- Remedy: add the post-condition check to the single path. Consider a proof flag for Move partial cleanup (compare W2-FA, R01 fix #290).

## W2-FA-10 (P3, traced) DuplicateFinder size scan ignores cancellation
F01639. `DuplicateFinder.cs:45-69`. The size-scan lambda runs under `Task.Run(..., token)`, which only cancels before the delegate starts. The loop never checks the token, so cancelling a large or slow scan is noticed only after it ends. No data effect.
- Remedy: check the token per path and add a cancellation test.

## Scope notes
Not reviewed in this shard: Platform-side `TryRestore` identity matching, `CopyCreationProof`, and `IJournalCompactionFiles` (other shards).
