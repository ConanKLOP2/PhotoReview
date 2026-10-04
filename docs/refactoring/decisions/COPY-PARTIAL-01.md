---
id: COPY-PARTIAL-01
order: 103
summary: |-
  A Copy that throws after writing part of its destination (disk full) keeps that partial file, because File.Copy gives no proof the destination was created by this call; Recovery shows a Conflict and the user deletes the file by hand (option c, user decision 2026-10-04).
---

# COPY-PARTIAL-01 (audit D-01) — what happens to a half-written Copy destination

## Current state

`PhysicalFileSystem.TryCopyNew` (`src/PhotoReview.Core/IO/PhysicalFileSystem.cs`) calls `File.Copy` (Win32 `CopyFile` with
`COPY_FILE_FAIL_IF_EXISTS`) and raises `CopyCreationProof` only after `File.Copy` returned. A copy that throws after it created the
destination (disk full) therefore never raises the proof, and the proof-guarded cleanup (`PartialDestinationCleanup`,
`FileActionService` single/group paths, `RecoveryRetryService.CopyNewOrRemovePartial`) does not run. The truncated destination stays,
the journal entry is Failed, `RecoveryFileCheck` reports Conflict (source exists, destination exists with a different size) and
"retry" refuses with "destination exists". The source is never touched. The in-memory test fake used to raise the proof at the partial
write, so the tests asserted a cleanup that the real file system never performed (wave 2 review D-01, introduced by PR #290 / R01).

Why a proof is needed: `File.Copy` cannot tell whether a throw happened before or after it created the destination, and a file that
appeared at the destination from another process looks the same as our partial copy (same path, shorter than the source). Deleting
on a guess would delete a foreign file (the R01 data-safety class). A kept partial costs the user one manual delete; a wrongly deleted
file cannot be recovered.

## Options

| | Change | Pros | Cons |
|---|---|---|---|
| a | Treat post-creation HRESULTs (`ERROR_DISK_FULL` 0x70, `ERROR_HANDLE_DISK_FULL` 0x27) as proof the destination was created by this call | Small change; keeps `CopyFile` (speed, attributes, streams) | Not provably safe: disk-full can also be raised when nothing was created (no room for the directory entry / MFT record), so a foreign shorter file that appears right after would be claimed and deleted. A unit test through a seam only tests our own HRESULT mapping, not Win32, and a real disk-full cannot be provoked safely on a dev machine |
| b | Copy through a `FileMode.CreateNew` stream and mark the proof right after the open | Exact ownership proof (CreateNew creates or fails atomically) | We would have to reproduce what `CopyFileEx` does for us: the documentation of `CopyFileEx` lists extended attributes, OLE structured storage, NTFS alternate data streams, security resource attributes and file attributes as preserved; the stream copy loses them unless copied by hand. We must also set the last-write time ourselves (the journal and Recovery compare it). Needs `Native`/`Slow` tests on a real volume and is slower for large RAW files |
| c | Keep as is, document, make the fake match reality | No risk of deleting a foreign file; no production change | A disk-full partial stays until the user deletes it; Recovery shows it as Conflict |

Note on ACLs: the Win32 pages for `CopyFile`/`CopyFileEx` (checked 2026-10-04) list file attributes and security resource attributes
(`ATTRIBUTE_SECURITY_INFORMATION`, Windows 8 and later) as copied, and say nothing about copying the DACL, so ACL fidelity is not what
separates (b) from `CopyFile` in the documentation; we did not test it empirically. The same pages do not say what `CopyFile` does with
the destination when the copy fails for lack of space (only that a cancelled `CopyFileEx` deletes it and `PROGRESS_STOP` leaves it).

## Decision

Option (c), chosen by the user on 2026-10-04. Production code is unchanged. `InMemoryFileSystem` (`CopyPartialHook`,
`CopyFailsAfterBytes`) now raises `CopyCreationProof` only for a copy that completed, as the real file system does, and the tests assert the
real behaviour: the partial destination is kept, the source is intact, the journal entry is Failed, Recovery shows Conflict, and a foreign
file is never deleted. The cleanup is still exercised where a proof is raised and the later verification fails (the source shrank during the copy).

## When to revisit (b)

Only if a half-written destination is actually observed after a real disk-full (or similar) failure. Then (b) needs: Native tests on a
real volume, resetting the last-write time, and either accepting the loss of alternate data streams and attributes or copying them
explicitly. Option (a) stays rejected unless Win32 documents that disk-full is only raised after the destination was created.

## User-facing behaviour

If a Copy fails midway (for example the disk is full), delete the incomplete file at the destination yourself; PhotoReview lists it in
Recovery as a Conflict and will not overwrite or delete it.
