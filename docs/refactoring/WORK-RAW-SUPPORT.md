# WORK: Camera RAW support (RAW-*)

Status: **PLANNED** (2026-09-28). No code yet. Owner: lead session + multi-agent waves below.
Supersedes the "RAW declined for now" part of [Q-R52](decisions/Q-R41-Q-R52-user-feedback.md), which asked for
exactly this: a separate architecture decision (library, licensing, per-format test plan) before any code.

Read in this order, one file at a time (Token Diet T1):

| File | What | Who reads it |
|---|---|---|
| this file | goals, architecture, contracts, waves | everyone |
| [`raw/DECISIONS.md`](raw/DECISIONS.md) | Q-RAW-01..07: options, pros/cons, recommendation | user, lead |
| [`raw/TASKS.md`](raw/TASKS.md) | one card per task: files owned, steps, tests, acceptance, model | the agent on that task |
| [`raw/AGENT-PROTOCOL.md`](raw/AGENT-PROTOCOL.md) | commit-every-step rule, progress files, handoff, prompt template | every agent, lead |
| [`raw/FORMATS.md`](raw/FORMATS.md) | per-format container specs (where the previews and EXIF live) | RAW-11/12/13/15 agents |
| `raw/progress/RAW-xx.md` | live progress log of each task (created by the agent) | resuming agent, lead |

## 1. Goals (in AGENTS.md priority order)

1. **Minimize disk reads.** A RAW file is 20–120 MB, but a camera already embeds a finished JPEG preview
   (often full sensor size) inside it. Display that preview by reading **only the container header plus the
   preview byte range** (typically 64 KB + 1–6 MB), never the whole file, for normal review.
2. **Use RAM.** Cache the extracted preview JPEG bytes (not the whole RAW) in `SourceBytesCache`, and the
   decoded previews in the existing RAM/disk preview caches, exactly like JPEGs.
3. **Review speed.** RAW navigation must feel like JPEG navigation: same preload, same viewport-box decode,
   same cache identity. Target (confirmed or revised by RAW-01's measurements): first paint of a RAW whose
   embedded preview is full size ≤ 1.3× the time of the same-size JPEG; preload keeps up at the same pace.
4. **Correctness.** Right orientation (the container's, never applied twice), right dimensions for zoom
   (ADR 0008: 100 % = 1 source pixel), right colour space for Adobe RGB previews, and no crash, hang or
   unbounded allocation on hostile files (parsers are untrusted-input code).
5. **Data safety.** If JPG+RAW pairs are grouped (Q-RAW-04), a Move/Recycle/Undo of a pair is one journaled
   transaction: never leaves half a pair behind silently, survives a crash (INV-6).

Non-goals (for this plan): editing or developing RAW files (exposure, white balance, profiles);
writing XMP; formats outside Q-RAW-05's list; non-Windows platforms.

## 2. Architecture

```
FolderLoad ── ImageFileTypes (+RAW exts, gated by setting) ──► ReviewCatalog (+ pair groups, RAW-40)
                                                                   │
PreviewImageService / ThumbnailCache / ZoomDetailLoader            │ path
        │ DecodeRequest(Path, Box, Priority, Bytes?, SourceOrientation?)
        ▼
FormatRoutingDecoder (new, wraps the configured backend chain)
   ├─ non-RAW ext ─► existing chain: Fallback(WicDirect|TurboJpeg → Wpf)   (unchanged)
   └─ RAW ext ─────► RawDecoder (new, PhotoReview.Imaging.Raw)
          1. IRawContainerReader.Read(header bytes via ISourceReader, bounded growth)
               → RawContainerInfo { Format, SensorSize, Orientation, Previews[], ExifBlocks[], ColorSpace }
          2. pick preview: smallest JPEG preview ≥ requested box, else the largest
          3. read only that byte range (SourceBytesCache key = path+len+mtime+"#preview:<index>")
          4. inner backend.Decode(Bytes = previewJpeg, SourceOrientation = container orientation)
          5. result: OriginalWidth/Height = SensorSize (Q-RAW-03), Exif from container, Downscaled flag
          6. IsOriginal request (zoom beyond preview) → full decoder per Q-RAW-02 (LibRaw / WIC), else
             the largest preview (best effort, documented)
```

New projects (Architecture.Tests must allow them, same direction rules as `PhotoReview.Imaging.TurboJpeg`):

- `src/PhotoReview.Imaging.Raw` — managed only: container readers, preview selection, `RawDecoder`,
  `FormatRoutingDecoder`. References `PhotoReview.Imaging` (+ `Core`). No native code.
- `src/PhotoReview.Imaging.LibRaw` — only if Q-RAW-02 chooses LibRaw: native P/Invoke wrapper, probe,
  `fetch-libraw.ps1`, pinned SHA-256, licence files. Registered through `App.Composition.DecoderProviders`
  like TurboJpeg (probe first, log once, never crash).

## 3. Contracts (fixed before any parallel wave starts — RAW-10 lands them)

Names are final; agents must not rename them. New members are appended at the end of shared files.

```csharp
// PhotoReview.Imaging.Raw
public enum RawFormat { Unknown, Cr2, Cr3, Nef, Nrw, Arw, Dng, Raf, Orf, Rw2, Pef }
public enum EmbeddedPreviewKind { Jpeg, UncompressedRgb, Other }
public enum PreviewColorSpace { Unknown, Srgb, AdobeRgb }
public sealed record EmbeddedPreview(int Index, long Offset, long Length, EmbeddedPreviewKind Kind,
    int Width, int Height, PreviewColorSpace ColorSpace);          // Width/Height 0 = unknown until SOF read
public sealed record ExifBlock(long Offset, long Length, bool IsTiffHeader); // span of a TIFF/EXIF structure
public sealed record RawContainerInfo(RawFormat Format, int SensorWidth, int SensorHeight, int Orientation,
    IReadOnlyList<EmbeddedPreview> Previews, IReadOnlyList<ExifBlock> ExifBlocks);
public interface IRawHeaderSource { long Length { get; } ReadOnlySpan<byte> Read(long offset, int count); }
public interface IRawContainerReader
{
    RawFormat Format { get; }
    bool CanRead(ReadOnlySpan<byte> first64Bytes, string extension);
    RawContainerInfo Read(IRawHeaderSource source, CancellationToken ct);   // throws InvalidDataException
}
public static class RawFileTypes { public static IReadOnlySet<string> Extensions { get; } }  // ".cr2" ...
public static class RawContainerLimits                       // hostile-input caps, shared by every reader
{ public const int MaxIfdCount = 64, MaxEntriesPerIfd = 4096, MaxBoxDepth = 16, MaxHeaderBytes = 8 << 20; }
```

`IRawHeaderSource` is backed by a bounded, growing read through `ISourceReader` (Priority passed through), so
a reader asks for bytes by offset and the source reads only what is asked (block-aligned 64 KB), never the file.

Existing types gain (RAW-20, appended, defaults keep every current caller unchanged):

- `DecodeRequest.SourceOrientation` (`int?`, null = read from the stream as today). When set, decoders apply
  it instead of reading EXIF from the bytes; embedded previews usually carry no orientation of their own.
- `ImageCacheKey.SourceKind` (`byte`, 0 = file as-is, 1 = RAW embedded preview, 2 = RAW full decode) so a
  preview-derived bitmap and a demosaiced one can never alias (INV-1/INV-12).
- `AppSettings`: `RawSupportEnabled` (default **false** until RAW-70 flips it — dark launch keeps every
  merged PR shippable), `RawFullDecode` (`Never | OnZoom`), `RawPairMode` (`Separate | PreferJpeg | PreferRaw`).

## 4. Waves (details and file ownership in raw/TASKS.md)

Every PR is based on `origin/master` (no stacked PRs). A wave starts only after the previous wave's PRs are
merged. Tasks inside a wave own disjoint files, so they run in parallel worktrees without conflicts.

| Wave | Tasks (parallel) | Gate to start next wave |
|---|---|---|
| 0 | RAW-00 user answers DECISIONS.md · RAW-01 sample corpus + survey + WIC probe | decisions recorded; SURVEY.md merged |
| 1 | RAW-10 project + contracts + limits + Architecture rules (single agent) | merged |
| 2 | RAW-11 TIFF family · RAW-12 CR3 · RAW-13 RAF · RAW-20 DecodeRequest/ImageCacheKey fields | all merged |
| 3 | RAW-14 fuzz/robustness · RAW-15 EXIF from RAW · RAW-21 RawDecoder + routing · RAW-23 file types behind flag | all merged |
| 4 | RAW-22 cache/preload/RAM budget · RAW-50 settings/UI/i18n · RAW-30 WIC full-decode probe path | all merged |
| 5 | RAW-31 LibRaw backend (if chosen) · RAW-32 zoom full decode · RAW-40 pair detection | all merged |
| 6 | RAW-41 pair file actions (journal group) · RAW-42 pair UI | all merged |
| 7 | RAW-60 benchmark · RAW-61 quality gate · RAW-62 real-machine check | all green |
| 8 | RAW-70 enable by default, ADR 0009, docs, HISTORY line, delete this plan | done |

## 5. Risks and how the plan covers them

| Risk | Mitigation |
|---|---|
| Embedded preview smaller than sensor on some bodies (older Sony ARW 1616×1080) | RAW-01 survey measures it per body; Q-RAW-02 full decode on zoom; info overlay says "RAW preview W×H" |
| Orientation applied twice or not at all | single source of truth = container orientation passed as `SourceOrientation`; 8-orientation quality gate per format (RAW-61) |
| Adobe RGB previews look dull | `PreviewColorSpace.AdobeRgb` → WIC colour transform with a CC0 Adobe-RGB-compatible profile (Q-RAW-06) |
| Parser crash / hang / OOM on a hostile file | `RawContainerLimits`, checked arithmetic, cycle detection, fuzz (RAW-14), header read capped at 8 MB |
| RAM budget under-estimated (RAW compressed size ≠ decoded size) | RAW-22 estimates from preview dimensions known after the header parse, never from file length |
| Preload reads whole RAW files | only header + chosen preview range; full RAW bytes are read only for a full decode (never preloaded) |
| Half-moved JPG+RAW pair | RAW-41 group transaction in the journal (strongest model, data-safety review) |
| LibRaw licence (LGPL-2.1 / CDDL-1.0) | dynamic linking of an unmodified DLL, licence text shipped, source link in `native/README.md`; lead verifies before merge |
| Agents run out of tokens mid-task | [AGENT-PROTOCOL.md](raw/AGENT-PROTOCOL.md): commit + push after every step, progress file per task, resumable handoff |
