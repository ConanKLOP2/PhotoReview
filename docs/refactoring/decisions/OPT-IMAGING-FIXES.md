---
id: OPT-IMAGING-FIXES
order: 30
summary: |-
  OPT-1: Eliminate double disk I/O for large JPEG headers (>8 MB) by extending exponential-growth pattern instead of re-reading entire file.
  OPT-2: Add SourceBytesCache.CreateKey overloads accepting pre-computed stats to avoid redundant FileInfo allocation (Q-R29 pattern).
---

# OPT-IMAGING-FIXES: TurboJPEG Header Double-Read and SourceBytesCache Allocation Optimization

**Date:** 2026-09-28  
**Status:** Implemented  
**Track:** perf/imaging-turbojpeg-sourcebytescache

## Summary

Two low-risk optimization implementations targeting the imaging pipeline:

1. **OPT-1:** Eliminate double disk I/O for JPEG headers larger than 8 MB by extending the exponential-growth pattern in `ReadHeaderArea` instead of re-reading the entire file as a fallback.
2. **OPT-2:** Avoid redundant `FileInfo` allocation in `SourceBytesCache.CreateKey` by adding overloads that accept pre-computed file stats (following the precedent established in `ThumbnailCache.GetAsync`).

## OPT-1: TurboJPEG Header Fallback Double-Read

### Problem
In `TurboJpegDecoder.ReadInfo()`, when a JPEG has a large header area (>8 MB, e.g., from large embedded EXIF or APPn segments):

1. `ReadHeaderArea()` reads up to the 8 MB cap using exponential growth
2. If the header parse fails, the code calls `File.ReadAllBytes(path)` on the **entire file**
3. For a 1 GB file, this means ~2x disk I/O: first 8 MB exponential read, then full 1 GB read

### Solution
Added `ExtendHeaderArea()` method that continues the exponential-growth pattern from the initial buffer, removing the `HeaderReadCap` limit for the fallback case:

- Reuses the already-read initial buffer
- Continues doubling from the position where `ReadHeaderArea` stopped
- No re-read of the initial 8 MB chunk; only reads the remaining bytes
- Stops when header is complete (marker walk returns false) or file end is reached

### Changes
- `TurboJpegDecoder.cs`:
  - Modified `ReadInfo()` to call `ExtendHeaderArea()` instead of `File.ReadAllBytes()` on parse failure
  - Added `ExtendHeaderArea(string path, byte[] initialBuffer)` private method
  
### Impact
- **Behavior:** Unchanged — returns identical bytes
- **I/O:** Eliminates redundant re-read for large headers
- **RAM:** Initial buffer allocation unchanged
- **Scope:** Internal implementation detail; no public contract change

### Test Results
- Mutation check: Temporarily replaced `ExtendHeaderArea` with `File.ReadAllBytes`; all 580 imaging tests still pass (both approaches produce identical bytes)
- Full test suite: 4024 tests pass, 0 warnings

## OPT-2: SourceBytesCache FileInfo Allocation

### Problem
`SourceBytesCache.CreateKey(string path)` always allocates a new `FileInfo` and calls `Path.GetFullPath()`, even when the caller already has this data available (e.g., from a prior stat collection on the navigation thread).

Callers like `PreviewImageService` may already possess `FileStat` from concurrent navigation work, but cannot reuse it because the public API only accepts a path.

### Solution
Added overloads following the precedent of `ThumbnailCache.GetAsync(string, FileStat?)`:

1. **Public overload:** `GetOrRead(string path, long length, long lastWriteUtcTicks, SourceReadPriority priority)`
2. **Public overload:** `GetOrRead(string path, long length, long lastWriteUtcTicks)` (defaults to Viewer priority)
3. **Public overload:** `TryPrefetch(string path, long length, long lastWriteUtcTicks)`
4. **Private overload:** `CreateKey(string path, long length, long lastWriteUtcTicks)`

### Changes
- `SourceBytesCache.cs`:
  - Added three public method overloads
  - Added private `CreateKey` overload that accepts pre-computed stats
  - Existing no-stat overloads unchanged (backward compatible)

### Impact
- **Behavior:** Unchanged — key derivation identical for matching stats
- **Allocation:** Eliminates `FileInfo` allocation when caller provides stats
- **Scope:** Q-R29 option C-2 pattern; internal seam for callers with existing metadata
- **Compatibility:** Fully backward compatible; no breaking changes

### Usage Pattern
Future caller optimization (not yet wired in this PR):
```csharp
// Before: allocates new FileInfo
var bytes = _cache.GetOrRead(path);

// After: reuses caller's stat, no FileInfo allocation
if (_currentFileInfo is FileStat stat)
    bytes = _cache.GetOrRead(path, stat.Length, stat.LastWriteUtc.Ticks);
```

### Test Results
- Mutation check: Temporarily made overload ignore pre-computed stats and re-read fresh ones; all 580 imaging tests still pass (both approaches produce identical cache keys)
- Full test suite: 4024 tests pass, 0 warnings

## Precedents
- **OPT-1 pattern:** Incremental buffer growth with unbounded extension is consistent with design goal "minimize disk reads" (AGENTS.md § Mandatory Process)
- **OPT-2 pattern:** Follows Q-R29 option C-2 precedent in `ThumbnailCache.GetAsync(string, FileStat?)` (lines 85-100)

## Verification Checklist
- [x] Build: `dotnet build PhotoReview.slnx -c Release` — 0 warnings, 0 errors
- [x] Tests: `dotnet test PhotoReview.slnx -c Release --filter "Category!=Manual&Category!=Native&Category!=Slow"` — 4024 pass
- [x] Doc links: `tools/check-doc-links.ps1` — no broken links introduced
- [x] Doc budget: `tools/docs-budget.ps1 -Check` — within limits
- [x] Open decisions: `tools/check-open-decisions.ps1` — table generated and valid

## References
- AGENTS.md: Mandatory Process rules (disk I/O, RAM utilization)
- Q-R29: Navigation perf work; C-2 seam for stat-based operations
- ThumbnailCache.GetAsync: Model for stat-accepting overload pattern
