# RAW-21 RawDecoder + FormatRoutingDecoder — progress
Branch: feat/raw-support-integration · PR: #239 · Agent model: Codex
Last update: 2026-09-29 · State: IN PROGRESS

## Steps
- [x] 1. Select an embedded JPEG preview by requested box and orientation, resolving unknown JPEG dimensions from bounded SOF reads.
- [x] 2. Parse container metadata for `ReadInfo`; decode the chosen range and return sensor dimensions, orientation, RAW EXIF, downscale state and inner backend.
- [x] 3. Route RAW extensions behind the live `RawSupportEnabled` setting; keep standard decoder fallback inside the route so RAW container failures are not reinterpreted as ordinary images.
- [ ] 4. `RawFullDecode.Never` uses the largest preview; `OnZoom` remains on the preview until the selected full decoder is implemented in RAW-31.

## Evidence
- Added production composition wiring: `ImageDecoderFactory` decorates each selected backend with `FormatRoutingDecoder`; its `RawDecoder` shares the source reader and configured preview byte-range cache.
- App composition regression test confirms WPF and WIC backends are both routed through the RAW support gate.
- `PhotoReview.App.Tests` targeted routing test: 1 passed. Imaging `FactoryTests`: 16 passed.
- RAW parser/decoder unit and corpus test coverage exists in the RAW-21/reader commits; first-paint benchmark has not yet been recorded.

## Next action
Implement RAW-30 WIC full-decode probe, then RAW-31 LibRaw per Q-RAW-02=A and RAW-32 zoom integration. Run the RAW-21 corpus first-paint benchmark when a Native-capable corpus environment is available.

## Open problems
- The integration PR is still draft and the full RAW corpus first-paint benchmark remains unverified.
