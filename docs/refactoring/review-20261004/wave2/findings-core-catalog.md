# Findings: Core Catalog and Model (wave 2)

OK 131 / ISSUE 11 / NEEDS-EVIDENCE 3 / REMOVED 0 (145 functions; the ISSUE and NEEDS-EVIDENCE rows are the affected functions of 8 findings, all P3; no P1 or P2). The files in this shard did not change between baseline `5d291076` and master `3db54da1`, so there are no `N-core-catalog-*` rows and nothing REMOVED. None of the findings duplicates R01-R10 of WORK-REVIEW and no open PR touched them (#284, #294, #296, #299, #300). Ledger: [ledger-core-catalog.tsv](ledger-core-catalog.tsv). Evidence (never compiled): `evidence/core-catalog-repro.cs.txt`, `evidence/core-catalog-catalog-differential.cs.txt`. Source: report of the review agent, written into the repo by the lead because the agent's Write tool refused report files. "Reproduced" means run in a scratch console referencing Core; no repository test was run.

A randomized differential probe of `ReviewCatalog` (3 pair modes x 300 rounds x 60 operations: Remove, RestoreMembers, Restore, RemovePaths, ReplaceOrder, UpdateMetadata, SetCurrent, with capture groups) checked the index, the `Paths` cache, `CurrentIndex` and duplicates after every operation: 0 violations, so the deferred-removal index has no defect found.

## W2-CC-01 (P3, reproduced) Normalized Explorer order is matched against raw catalog paths
F01430, F01431. `ExplorerSnapshotValidator.TryValidateCore` returns the normalized strings (`ExplorerSnapshotValidator.cs:52,86,97`), not the caller's scanned strings. `ReviewCatalog.ReplaceOrder` (`ReviewCatalog.cs:392-396`) matches them verbatim against `CatalogEntry.Path`, so it rejects the order when the catalog carries a `\\?\` prefix. Measured: `validate=True`, `ReplaceOrder=False`; the Explorer order is silently ignored.
- Remedy: keep the normalized-to-original mapping and return the original strings in Explorer order.

## W2-CC-02 (P3, reproduced) Sibling-folder navigation can land in a Hidden/System folder
F01512. `SiblingFolderService.GetSorted` (`SiblingFolderService.cs:14`) uses `Directory.EnumerateDirectories(string)`, an overload that does not skip Hidden/System directories. The probe returned `.hid,a`, so the next-folder key can land in a hidden folder that contains photos.
- Remedy: use `EnumerationOptions` that skips Hidden|System. Whether hidden folders should be allowed is a user decision.

## W2-CC-03 (P3, reproduced) Flag-enum JSON converters for ExifInfoFields and TitleBarFields
F01994, F01995, F02008, F02009.
- Write: `Enum.ToString()` emits the alias names `"Default"` or `"All"`. The meaning of those constants changes between versions (ModifiedDate was added after release). For `TitleBarFields`, `Default` and `FolderName` share value 1 so the runtime picks one of the two names.
- Read: the number `-1` becomes `All`; the string `"FileName, Foo"` resets the whole value to Default and loses the valid name, with no repair recorded.
- Remedy: write explicit flag names, parse each name and keep the valid ones, treat negative numbers as unreadable.

## W2-CC-04 (P3, NEEDS-EVIDENCE, traced by reading) UpdateMetadata does not bump StructuralVersion
F01490. `ReviewCatalog.UpdateMetadata` (`ReviewCatalog.cs:158-168`) replaces Length/LastWriteUtc without bumping `StructuralVersion`. `PreloadScheduler` builds cache keys from the `EntriesSnapshot`, so it keeps the old stat until the next structural change. Effect: wasted preload decodes and cache misses; the displayed image is not wrong.
- Remedy: add a `MetadataVersion`, or let the cache key stat again.

## W2-CC-05 (P3, measured) ComparePairService.BuildIndex rebuilt on the UI thread
F01410. `ComparePairService.BuildIndex` (`ComparePairService.cs:42`) is rebuilt by `ImagePresenter.GetComparePair` (`ImagePresenter.cs:752-768`) on the UI thread at the first key after each structural change. It is reached through `CurrentHasComparePair` on every KeyDown (`MainWindow.xaml.cs:624`) and is rebuilt even when no numbered files exist. Release measurement: 229 ms for 100k paths, 86 ms for 10k (including JIT).
- Remedy: skip when no stem matches the numbered pattern, or build off the UI thread.

## W2-CC-06 (P3, reproduced) DragDropInputService treats a trailing separator as a different folder
F01422, F01424. `DragDropInputService.Parse` (`DragDropInputService.cs:33`) compares raw strings, so the same folder with and without a trailing `\` counts as "ignored" and produces a spurious warning. Measured: `ignored=1 warning=True`.
- Remedy: compare `TrimEndingDirectorySeparator(Normalize(path))`.

## W2-CC-07 (P3, reproduced, latent) ExplorerReason.Format does not escape `|`
F01428. `ExplorerReason.Format` (`ExplorerReason.cs:31`) does not escape `|`, so `Parse(Format(code, "a|b"))` yields two details. No current caller puts `|` in a detail.
- Remedy: escape or strip `|`, add a round-trip test.

## W2-CC-08 (P3, NEEDS-EVIDENCE, traced by reading) Lazy catalog state mutated on read paths without AssertOwnerThread
F01485, F01511. `EnsureIndex` (`ReviewCatalog.cs:98-133`), `Paths` and `EntriesSnapshot` (`ReviewCatalog.cs:528`) mutate lazy state on read paths without calling `AssertOwnerThread`. The test `Bound_ReadFromOtherThread_DoesNotThrow` says "preload snapshot runs off the UI thread", but `PreloadScheduler.PreloadAroundCore` calls the delegate synchronously on the UI thread. No off-UI-thread caller was found, so it is not proven.
- Remedy: correct the test comment, or add the assert.

## Checked and not findings
- `ComparePairService.Find` and the string overloads of `ImageSortService.Sort` have no production caller (tests and the fuzz oracle only).
- `TryValidate` returning `IncompleteSnapshot` for a null snapshot is intentional and tested.
- The `rawEnabled=true` default of `RestoreMembers` is a trap for new callers, but the 3 production callers pass the real setting.
- Equivalence of `WindowsNaturalComparer` (App DI) and `ManagedNaturalComparer` (catalog sort) was not checked (outside this shard).
- Only `LatestExplorerSnapshot` has no Core-level test; it is tested in App (`FolderLoadCoordinatorTests.LatestExplorer`).
