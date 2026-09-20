# PhotoReview

PhotoReview is a Windows WPF application for browsing, comparing, and organizing photos in Explorer order. The application prioritizes fast image rendering, recoverable file operations, and entirely local data processing.

## Key Features

- Reads file order from Windows Explorer when a valid native snapshot is available; falls back to an internal sorting order during wait times or when Shell is unavailable.
- Features Fast, Preview, and Original modes; bounded RAM/disk image caching; memory pressure checks for preloading.
- Delete operations move files to the Recycle Bin; Move/Copy/Delete actions are journaled to support Undo and recovery.
- Supports side-by-side Compare, optional hash/dimension verification, batch duplicate cleanup with a confirmation step, zoom/Fit/fullscreen views, and keyboard shortcuts.
- Images and file paths are processed locally; internal diagnostics are only enabled when configured.

See [Architecture](docs/architecture.md) and [Image Loading Mechanisms, Safety Invariants, and Benchmark Guide](docs/APP-MECHANISMS-VI.md) before modifying the pipeline or interpreting performance metrics.

## Requirements

- Windows 10/11 x64.
- .NET 10 Windows Desktop Runtime for the framework-dependent build; self-contained builds include the runtime.

## Build, Test, and Publish

```powershell
dotnet build PhotoReview.slnx -c Release
.\tools\verify-all.ps1
dotnet publish src/PhotoReview.App/PhotoReview.App.csproj -c Release --self-contained false -o src/PhotoReview.App/bin/Release/net10.0-windows/publish
.\tools\verify-release.ps1 -ReleaseDirectory 'src/PhotoReview.App/bin/Release/net10.0-windows/publish'
```

Framework-dependent artifacts are placed in `src/PhotoReview.App/bin/Release/net10.0-windows/publish`. Verification for self-contained builds or smoke/fault-injection tests uses separate scripts and paths in `tools/`; a successful build/test pass does not replace runtime benchmarks or GUI acceptance testing.

## Benchmarks

```powershell
dotnet run --project tools/PhotoReview.Benchmark.Cli/PhotoReview.Benchmark.Cli.csproj -c Release -- --benchmark-list-profiles
dotnet run --project tools/PhotoReview.Benchmark.Cli/PhotoReview.Benchmark.Cli.csproj -c Release -- --benchmark-all 'C:\path\to\image-folder' 'C:\path\to\output-folder'
```

Keep the machine, fixtures, viewport, mode, and cache state consistent when comparing runs. The final runtime refactoring results are documented in [T66](docs/refactoring/results/final.md).

## Performance and Display Settings

- `DecoderBackend`: Defaults to `Wpf`; can be set to `WicDirect` or `TurboJpeg`. Non-WPF backends automatically fall back to WPF when encountering supported codec errors.
- `ScalingQuality`: Defaults to `HighQuality` for visual fidelity; `Linear` reduces rendering overhead during zoom and pan operations.
- `UseSourceBytesCache`: Defaults to `false`. When enabled, the application keeps source file bytes in RAM with a 16 GiB quota to eliminate repeated disk reads; enable only after measuring real workloads.

## File Associations (Optional)

Register "Open with" for `.jpg`, `.jpeg`, and `.png`:

```powershell
.\outputs\install-photo-review-association.ps1 -ExePath 'C:\path\to\PhotoReview.App.exe'
```

Unregister:

```powershell
.\outputs\uninstall-photo-review-association.ps1
```

## Known Limitations

Explorer ordering depends on open folder windows and valid Shell snapshots; fallback ordering is used if Explorer is not ready or if the snapshot encounters an error or timeout. Contract tests do not guarantee GUI behavior, perceived first-image latency, or P95 timings; these conclusions require controlled runtime measurements.
