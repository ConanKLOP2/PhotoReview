# RAW-10 Project skeleton + contracts + limits — progress
Branch: feat/raw-support-integration · PR: #239 · Agent model: opus
Last update: 2026-09-29 00:26 · State: READY FOR REVIEW / DONE

## Steps
- [x] 1. Create `PhotoReview.Imaging.Raw`, add to `PhotoReview.slnx`, reference from `App` and `Imaging.Tests`, add Architecture dependency rules (Rule 9: Raw -> Imaging/Core only, no other layer references Raw).
- [x] 2. Add contract types exactly matching WORK-RAW-SUPPORT.md §3 (`RawFormat`, `EmbeddedPreviewKind`, `PreviewColorSpace`, `EmbeddedPreview`, `ExifBlock`, `RawContainerInfo`, `IRawHeaderSource`, `IRawContainerReader`, `RawFileTypes`, `RawContainerLimits`).
- [x] 3. `SourceRawHeaderSource : IRawHeaderSource`: 64 KB block-aligned caching over `ISourceReader`, hard cap `MaxHeaderBytes` throws `InvalidDataException`, tracks bytes read.
- [x] 4. `InMemoryRawHeaderSource` + `SyntheticRawBuilder` (TIFF little/big endian, minimal ISO-BMFF, minimal RAF).
- [x] 5. `RawContainerReaderRegistry`: routes by `CanRead(first64Bytes, extension)`.

## Next action (for whoever resumes)
Wave 1 complete. Proceed to Wave 2: RAW-11 (TIFF readers), RAW-12 (CR3 ISO-BMFF reader), RAW-13 (RAF reader), RAW-20 (`DecodeRequest.SourceOrientation` + `ImageCacheKey.SourceKind`).

## Evidence / measurements
- Build: 0 warnings, 0 errors in Release.
- `PhotoReview.Architecture.Tests`: 64/64 passed (including Rule 9 and updated Core/Imaging/Platform isolation rules).
- `RawContractsAndHeaderSourceTests`: 7/7 passed.
- Mutation check: verified on synthetic test builder.

## Open problems
None.
