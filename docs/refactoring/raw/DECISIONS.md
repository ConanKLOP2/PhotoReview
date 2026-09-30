# RAW support — decisions to take before code (Q-RAW-01..07)

Each question lists options with concrete pros/cons and a recommendation. The user answers in chat; the lead
records the answer here (`**Decided:** X, date`) and, once all are answered, moves the summary into one
`decisions/Q-RAW.md` fragment (with frontmatter) and runs `tools/generate-open-decisions.ps1`.
Items marked *(after RAW-01)* should be confirmed once the survey numbers exist; the recommendation is the
working assumption until then.

---

## Q-RAW-01 — What does the viewer show for a RAW file?

| Option | What it means | Pros | Cons |
|---|---|---|---|
| **A. Embedded preview only** | Always show the camera's embedded JPEG; never demosaic | Fastest possible (reads ~1–6 MB of the file); zero native dependency; same speed as JPEG review | Bodies with small previews (e.g. 1616×1080) are blurry when zoomed; what you see is the camera's JPEG rendering, not the sensor data |
| **B. Embedded preview + full decode on zoom** | Preview for normal review; when zoom needs more pixels than the preview has, decode the sensor data (Q-RAW-02) | Review speed of A in 95 % of use; sharp zoom when it matters; reuses `ZoomDetailLoader` (ADR 0008) | Needs a full decoder (native or WIC); full decode takes ~1–3 s per 24–45 MP file; zoomed pixels come from a different renderer than the preview (colour may shift slightly) |
| C. Always full decode | Demosaic every RAW | "True" RAW view everywhere | 20–100× slower than A; breaks the review-speed priority; preload would read whole files (breaks "minimize disk reads") |

**Recommendation: B.** It keeps priorities 1–3 intact and only pays the full-decode cost when the user zooms
past the preview. **Decided (2026-09-28, user):** B.

## Q-RAW-02 — Which full decoder (only if Q-RAW-01 = B or C)? *(after RAW-01)*

| Option | Pros | Cons |
|---|---|---|
| **A. LibRaw (native DLL, like TurboJpeg)** | Broadest camera coverage (1000+ bodies, CR3 included); deterministic on every machine; `half_size` mode gives a 4× faster ¼-resolution decode; mature, used by darktable/RawTherapee-class tools | Ships a ~1.5–3 MB native DLL; licence LGPL-2.1 or CDDL-1.0 (dynamic linking of an unmodified DLL is fine, but licence text + source link must ship); must be fetched and SHA-pinned like `turbojpeg.dll`; native crash risk mitigated only by our probe + input checks |
| B. Windows WIC RAW codec ("Raw Image Extension" from the Microsoft Store) | Zero shipped binary; goes through the existing `WicDirectDecoder` (no new decode path); licensing is Microsoft's | Not installed by default on every Windows; camera coverage and speed vary by OS build; we cannot pin or test a version; some formats may decode only the embedded preview anyway |
| C. Both: WIC when its codec is present, LibRaw otherwise | Best coverage | Two full-decode paths to test and keep consistent; more code |
| D. None (Q-RAW-01 = A) | Simplest | Zoom beyond preview stays blurry |

**Recommendation: A (LibRaw)**, behind `RawFullDecode = OnZoom`, with RAW-30 probing WIC so the survey
can show whether B is good enough on the user's own machine. **Decided (2026-09-28, user):** A (LibRaw); RAW-30 probes WIC; revisit only if the survey shows WIC covers every format fast enough.
**Outcome (2026-09-29/30):** the RAW-30 probe ran once ([SURVEY.md](SURVEY.md) section 4: WIC fully decoded 2 of 23 corpus
files on the survey machine, 1 more was preview-only, 20 were unavailable), so A stands. The WIC RAW full decoder and codec registry built
on that probe were removed as unused (`54a1b1c6`; `git grep WicRawFullDecoder` finds only docs). The WIC comparison is survey history
only; the app has no WIC RAW full-decode path. LibRaw decodes are serialised process-wide by a single-slot gate
(`LibRawDecoder.s_fullDecodeGate`).

## Q-RAW-03 — What is "100 %" zoom for a RAW? (ADR 0008 says 100 % = 1 source pixel)

| Option | Pros | Cons |
|---|---|---|
| **A. Sensor (visible/default-crop) size** | Consistent with ADR 0008 and with the full decode; zoom % means the same in every tier; matches what other RAW viewers report | When the embedded preview is smaller than the sensor, 100 % shows an upscaled preview until the full decode arrives |
| B. Embedded preview size | 100 % is always sharp from the preview | Zoom % changes meaning when the full decode arrives; dimensions shown in the info overlay would not match the camera's specs |

**Recommendation: A.** `OriginalWidth/Height` = sensor visible size (DNG `DefaultCropSize`, CR3/CR2/NEF
visible area); the info line shows the preview size separately (`overlay.rawPreview`, "RAW preview 1620×1080"; hidden after the full decode and absent for a cache-restored image). **Decided (2026-09-28, user):** A.

## Q-RAW-04 — JPG+RAW pairs shot together (`IMG_0001.JPG` + `IMG_0001.CR3`)

| Option | Pros | Cons |
|---|---|---|
| A. Separate items (today's behaviour once RAW is listed) | No new file-action semantics; zero data-safety risk | Every photo appears twice; culling means deciding twice; deleting the JPG leaves the RAW behind |
| **B. Group, show the JPG, actions apply to the whole pair (+ `.xmp` sidecar)** | One decision per shot (the common culling workflow); JPG shows fastest | Needs multi-file journal transactions (RAW-41, data-safety critical); Undo must restore both; a partial failure must be visible |
| C. Group, show the RAW | Same as B, shows RAW data | RAW preview may be smaller than the JPG; slower |

**Recommendation: B, delivered as setting `RawPairMode`**; keep default `Separate` until RAW-41's crash/undo
tests and RAW-62 real-machine check pass. The user waived RAW-62 on 2026-09-29, so the shipped/default value
remains `Separate`; no real-machine acceptance is claimed. A key toggles which member of the pair is shown.
**Decided (2026-09-28, user):** pair handling is a user-facing option in the Settings window (`RawPairMode`:
Separate / Group–show JPG / Group–show RAW), not a fixed behaviour. Pairing rule (as proposed, accepted): same folder + same base name
(case-insensitive), exactly one JPEG + one RAW; a same-name `.xmp` follows the pair in every file action (only when it is unambiguous: exactly one `<base>.xmp`, else exactly one `<file>.<ext>.xmp`; with competing candidates the pair still groups but no sidecar is claimed, `CaptureGroupBuilder`);
a RAW without a JPEG is shown on its own.

## Q-RAW-05 — Which formats in the first release?

| Option | Formats | Pros | Cons |
|---|---|---|---|
| **A. Major 8** | CR2, CR3, NEF, ARW, DNG, RAF, ORF, RW2 | Covers Canon, Nikon, Sony, Fujifilm, OM/Olympus, Panasonic, phones (DNG) — >95 % of RAW users | 3 container families to parse (TIFF, ISO-BMFF, RAF) |
| B. Major 8 + PEF, NRW, SRW | + Pentax, Nikon compact, Samsung | Wider | More fixtures; rare formats; NRW/SRW previews vary |
| C. Only the user's own cameras | Smallest | Fastest to ship | Needs the list of the user's camera bodies; others unsupported |

**Recommendation: A**, with `RawFormat.Pef`/`Nrw` reserved in the enum so B is additive later.
**Decided (2026-09-28, user):** A (CR2, CR3, NEF, ARW, DNG, RAF, ORF, RW2). No camera list given yet: RAW-01 picks old + new bodies per format and adds the user's bodies whenever they are named.

## Q-RAW-06 — Adobe RGB embedded previews

| Option | Pros | Cons |
|---|---|---|
| **A. Detect (EXIF ColorSpace/Interop "R03") and convert with a bundled CC0 Adobe-RGB-compatible ICC through the existing WIC colour transform** | Correct colours; reuses the ICC path from ADR 0001 addendum 2026-09-24 | One more fixture/profile file; small per-decode cost (only for Adobe RGB files) |
| B. Ignore (treat as sRGB) | Zero work | Adobe RGB shots look desaturated — misleading when culling |

**Recommendation: A.** **Decided (2026-09-28, user):** A.

## Q-RAW-07 — Test corpus of real RAW files

| Option | Pros | Cons |
|---|---|---|
| **A. `tools/fetch-raw-samples.ps1` downloads a pinned list (URL + SHA-256) from raw.pixls.us (files published there as CC0) into a git-ignored folder; real-file tests are `Category=Native` (skip when absent)** | No large binaries in git; reproducible; licence-clean | Needs network once per machine; The regular CI does not run them (like other Native tests); the manual workflow `raw-corpus.yml` does, in strict mode (see [TESTING.md](../../TESTING.md)) |
| B. Commit small samples into the repo | Always available, CI can run them | RAW files are 10–60 MB each; bloats the repo; licence tracking per file |
| C. Synthetic containers only | Tiny, deterministic, CI-friendly | Cannot prove real cameras' quirks |

**Recommendation: A for real files + C for unit tests** (synthetic TIFF/BMFF/RAF containers wrapping a
FixtureGenerator JPEG, built in memory — these run in CI). The fetch script must verify the CC0 statement on
the source page for each file and record it next to its hash. **Decided (2026-09-28, user):** A + C.

## RAW-70 upgrade behaviour (2026-09-30)

`RawSupportEnabled` defaults to `true` (owner's intended RAW-70 default, not changed). An old `config.json` without the field therefore starts listing RAW files on the first launch after upgrading. No in-app one-time notice was added: the repo has no persisted "notice already shown" state, and the existing startup dialog (`SettingsStore.LastLoadRepairs`) is for repaired invalid values and would repeat each launch until the config file is saved. The behaviour is documented in the README upgrade note instead; the switch is Settings > Enable Camera RAW support.
