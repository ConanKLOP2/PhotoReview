# ADR 0009: Camera RAW support

- **Status:** Accepted, 2026-09-29; implementation PR #239.
- **Decision:** Enable Camera RAW support by default after completing the integration and quality gates.
- **Related decisions:** [Q-RAW-01..07](../refactoring/decisions/Q-RAW.md); [sample survey](../refactoring/raw/SURVEY.md).

## Context

PhotoReview previously listed common camera RAW extensions without a dependable preview/decode route. Camera files may be hundreds of megabytes, so review must avoid reading or decoding the full sensor payload until the user requests sensor-resolution detail. Existing image decoder behavior and the meaning of 100% zoom remain governed by [ADR 0001](0001-image-decoder.md) and [ADR 0008](0008-zoom-source-pixel.md).

## Decision

- Support CR2, CR3, NEF, ARW, DNG, RAF, ORF and RW2 when `RawSupportEnabled` is enabled; the setting now defaults to `true`, including when an older config omits it.
- Parse bounded container metadata and read only the selected embedded JPEG preview for normal review. Preserve sensor dimensions, container orientation, preview identity and source-byte accounting.
- Keep `RawFullDecode` at `Never` by default. When configured for `OnZoom`, use the pinned LibRaw backend to demosaic only the current RAW image at source resolution; do not preload or retain whole RAW files as source-byte cache entries.
- Convert Adobe RGB embedded previews through WIC using the bundled CC0 Adobe-compatible profile where the preview is tagged Adobe RGB and does not contain an ICC profile.
- Expose pairing as a setting. Keep `RawPairMode.Separate` as the default: pair-action handling is implemented, but the user waived RAW-62's real-machine navigation/zoom/pair-undo/RAM check. No real-machine acceptance is claimed; grouping remains an explicit opt-in.
- Keep RAW failure reporting explicit; do not route malformed or unsupported RAW bytes through an unrelated raster decoder.
- LibRaw is the only full-decode backend (a WIC RAW full decoder was probed in the survey and removed as unused, `54a1b1c6`); LibRaw decodes run one at a time process-wide. It is also the last resort for an ORF whose embedded JPEG the Windows decoder rejects (thumbnail API) and for a RAW with no usable embedded JPEG. When RAW support is disabled, `FormatRoutingDecoder` refuses RAW extensions with `NotSupportedException`.

## Consequences

- Existing and new installations can browse supported RAW files without changing settings; older configuration files receive the same enabled default.
- Normal browsing uses the embedded JPEG preview. Sensor-resolution decoding remains opt-in and incurs native decoder cost for the current file.
- Pair grouping remains off until the user chooses a grouping mode. This avoids changing catalog/action behavior based on an unperformed machine-specific check.
- Unit, integration, architecture, quality, corpus and release checks provide automated evidence. They do not establish manual GUI acceptance or performance on every camera body.
