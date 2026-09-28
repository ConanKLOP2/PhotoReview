---
id: Q-RAW-01..07
order: 35
summary: |-
  Camera RAW support approved (reverses Q-R52's RAW part): show the embedded JPEG preview (header + preview range reads only), LibRaw full decode on zoom, 100 % = sensor size, JPG+RAW pair handling as a Settings option, 8 formats (CR2/CR3/NEF/ARW/DNG/RAF/ORF/RW2), Adobe RGB converted, CC0 corpus fetched + synthetic tests.
---

# Q-RAW-01..07 — camera RAW support (2026-09-28)

Decided by the user in chat on 2026-09-28 (Q-RAW-04 explicitly; the rest "as recommended").
Options, pros/cons and the full wording of each answer: [raw/DECISIONS.md](../raw/DECISIONS.md).
Plan and task cards: [WORK-RAW-SUPPORT.md](../WORK-RAW-SUPPORT.md).

| ID | Decision |
|---|---|
| Q-RAW-01 | B — embedded preview for review; full decode only when zoom needs more pixels |
| Q-RAW-02 | A — LibRaw native backend (fetched + SHA-pinned like turbojpeg.dll); WIC RAW codec still probed by RAW-30 |
| Q-RAW-03 | A — 100 % zoom = sensor visible size (ADR 0008); preview size shown separately in the overlay |
| Q-RAW-04 | Settings option `RawPairMode` (Separate / Group–show JPG / Group–show RAW); same folder + same base name; `.xmp` follows the pair; default Separate until RAW-41 + RAW-62 pass |
| Q-RAW-05 | A — CR2, CR3, NEF, ARW, DNG, RAF, ORF, RW2 (PEF/NRW reserved) |
| Q-RAW-06 | A — Adobe RGB previews converted with a bundled CC0 profile via the WIC colour transform |
| Q-RAW-07 | A + C — pinned CC0 corpus fetched by script (Native tests) + in-memory synthetic containers (CI) |

This reverses the "RAW declined for now" part of [Q-R52](Q-R41-Q-R52-user-feedback.md), which required
exactly this architecture decision first. Other new formats (WebP/HEIC/JXL/AVIF/PSD) stay declined.
