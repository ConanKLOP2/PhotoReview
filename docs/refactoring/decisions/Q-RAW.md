---
id: Q-RAW-01..07
order: 35
summary: |-
  Camera RAW support implemented in open PR #239 (reverses Q-R52's RAW part): embedded JPEG preview, ORF LibRaw thumbnail fallback, optional full LibRaw decode on zoom, 100 % = sensor size, configurable JPG+RAW pairing (default Separate), 8 formats, Adobe RGB conversion, CC0 corpus + synthetic tests; RAW-62 real-machine check waived, not passed.
---

# Q-RAW-01..07 — camera RAW support (2026-09-28)

Decided by the user in chat on 2026-09-28 (Q-RAW-04 explicitly; the rest "as recommended").
Options, pros/cons and the full wording of each answer: [raw/DECISIONS.md](../raw/DECISIONS.md).
Implementation decision: [ADR 0009 — Camera RAW support](../../adr/0009-camera-raw-support.md). Historical plan and task cards are retained in Git history.

| ID | Decision |
|---|---|
| Q-RAW-01 | B — embedded preview for review; full decode only when zoom needs more pixels |
| Q-RAW-02 | A — LibRaw native backend (fetched + SHA-pinned like turbojpeg.dll); RAW-30 probed the WIC RAW codec for the survey only (2 of 23 files fully decoded); the WIC full decoder was removed as unused |
| Q-RAW-03 | A — 100 % zoom = sensor visible size (ADR 0008); preview size shown separately in the overlay |
| Q-RAW-04 | Settings option `RawPairMode` (Separate / Group–show JPG / Group–show RAW); same folder + same base name; `.xmp` follows the pair; default remains Separate because RAW-62's real-machine check was waived by the user |
| Q-RAW-05 | A — CR2, CR3, NEF, ARW, DNG, RAF, ORF, RW2 (PEF/NRW reserved) |
| Q-RAW-06 | A — Adobe RGB previews converted with a bundled CC0 profile via the WIC colour transform |
| Q-RAW-07 | A + C — pinned CC0 corpus fetched by script (Native tests) + in-memory synthetic containers (CI) |

This reverses the "RAW declined for now" part of [Q-R52](Q-R41-Q-R52-user-feedback.md), which required
exactly this architecture decision first. Other new formats (WebP/HEIC/JXL/AVIF/PSD) stay declined.

The user waived the RAW-62 real-machine check on 2026-09-29 and instructed the work to continue without it.
Automated validation is recorded in the PR/task history; it is not treated as real-machine or GUI acceptance.
