# AR01 — Ship the TurboJpeg backend (or stop offering it)

**Finding:** F1 · **Decision:** Q-AR1 (recommended: option A) · **Branch:** `fix/ar01-turbojpeg-release` · **Size:** ~0.5 day · **GUI check:** 1 manual step

## 1. Problem (verified at `fbdf48e`)

| Where | What |
|---|---|
| `src/PhotoReview.App/PhotoReview.App.csproj` | References Core, Benchmarking, Imaging, Platform.Windows. **No** `Imaging.TurboJpeg`. |
| `src/PhotoReview.Imaging/Decoding/ImageDecoderFactory.cs:56-60` | `Type.GetType("PhotoReview.Imaging.TurboJpeg.TurboJpegDecoder, PhotoReview.Imaging.TurboJpeg")` → `null` when the assembly is not next to the exe → TurboJpeg not registered. |
| `ImageDecoderFactory.Create` (`:66-80`) | Unregistered backend → `_log.Warn(...)` + plain WPF decoder. Logging is off by default (INV-10) → invisible. |
| `src/PhotoReview.App/SettingsWindow.xaml.cs:96,126` | Combo index 2 = TurboJpeg is always offered. |
| `src/PhotoReview.App/bin/Release/net10.0-windows/publish/` | Contains App/Core/Imaging/Platform.Windows dlls only. |
| `tools/verify-release.ps1:9-10` | Required files = exe/dll/deps/runtimeconfig (+ runtime for self-contained). |
| `tests/PhotoReview.Imaging.Tests/Decoding/FactoryTests.cs:189` | `FactoryResolvesTurboJpegBackend` passes only because the **test** project references TurboJpeg — it cannot catch the release gap. |

Everything else is already in place: `TurboJpegDecoder` exists, `FallbackImageDecoder.IsFallbackable` (`:81-93`) already treats `DllNotFoundException`/`BadImageFormatException` as fallbackable, `Imaging.TurboJpeg.csproj` copies `native/x64/turbojpeg.dll` with `CopyToOutputDirectory=PreserveNewest`, CI runs `tools/fetch-native.ps1`.

## 2. Option A — ship it (recommended)

### Step A1 — reference the project
- `src/PhotoReview.App/PhotoReview.App.csproj`: add `<ProjectReference Include="..\PhotoReview.Imaging.TurboJpeg\PhotoReview.Imaging.TurboJpeg.csproj" />`.
- Build and check `src/PhotoReview.App/bin/Release/net10.0-windows/` contains `PhotoReview.Imaging.TurboJpeg.dll` **and** `turbojpeg.dll` (transitive `None` + `CopyToOutputDirectory` normally flows; if `turbojpeg.dll` is missing, add the same `<None Include="..\..\native\x64\turbojpeg.dll" Link="turbojpeg.dll" CopyToOutputDirectory="PreserveNewest" />` item to App.csproj).
- `dotnet publish ... -o <dir>` and check both files are in `<dir>` too.

### Step A2 — replace reflection with explicit registration
- `ImageDecoderFactory.CreateDefaultProviders()` (`:48-63`): delete the `Type.GetType`/`Activator.CreateInstance` block; default providers = Wpf + WicDirect only.
- Add to `IImageDecoderFactory` (Imaging): `bool IsRegistered(DecoderBackend backend);` implemented as `_registry.ContainsKey(backend)`.
- `src/PhotoReview.App/App.xaml.cs` section "6. Imaging & Decoding": replace
  `new ImageDecoderFactory(sp.GetService<ILog>(), sp.GetService<ReviewMetrics>())` with
  `new ImageDecoderFactory(DecoderProviders.Create(log), log, metrics)` where `DecoderProviders` is a new `internal static` class in `src/PhotoReview.App/Composition/DecoderProviders.cs` returning `(Wpf, …)`, `(WicDirect, …)` and `(TurboJpeg, () => new TurboJpegDecoder())` **only if** `TurboJpegAvailability.Probe()` succeeds (step A3).
- `grep -rn "new ImageDecoderFactory(" src tools tests` and update every call: tests that need TurboJpeg pass providers explicitly; `FactoryTests.FactoryResolvesTurboJpegBackend` becomes "factory with explicit TurboJpeg provider wraps it in FallbackImageDecoder".

### Step A3 — probe the native library once
Without a probe, a missing/incompatible `turbojpeg.dll` makes **every** decode throw and catch `DllNotFoundException` before falling back (cost on the hot path).
- New `src/PhotoReview.Imaging.TurboJpeg/TurboJpegAvailability.cs`: `public static bool Probe(out string? reason)` — `NativeLibrary.TryLoad("turbojpeg.dll", typeof(TurboJpegNative).Assembly, null, out var h)`, then create and destroy one decompressor handle; cache the result in a `static readonly Lazy<(bool, string?)>`.
- `DecoderProviders.Create`: if the probe fails, do not register TurboJpeg and log **once** via `App.LogStartupErrorForced`-style forced log (same pattern as `LogDiagModeForced`) so the reason is visible even with logging off.

### Step A4 — Settings reflects reality
- `WpfDialogService.ShowSettings` (`Services/WpfDialogService.cs:102`) creates `new SettingsWindow(store)`: pass `IImageDecoderFactory` (resolve from the `IServiceProvider` it already holds).
- `SettingsWindow`: disable combo item 2 when `!factory.IsRegistered(DecoderBackend.TurboJpeg)` and set its tooltip to "TurboJPEG không khả dụng trong bản cài đặt này". Keep index mapping at `:96,126` unchanged.
- If a saved config has `DecoderBackend=TurboJpeg` but it is unregistered: behaviour stays "fallback to WPF", but the status bar/diagnostics must show the actual backend (already recorded by `ReviewMetrics` backend counters — verify in `DiagnosticsWindow`).

### Step A5 — release gate
- `tools/verify-release.ps1`: `$required += @('PhotoReview.Imaging.TurboJpeg.dll', 'turbojpeg.dll')`.
- Also compare `turbojpeg.dll` SHA-256 with the expected hash in `tools/fetch-native.ps1` (move the hash to one place, e.g. `native/turbojpeg.sha256`, read by both scripts).

### Step A6 — tests that lock the boundary
- `tests/PhotoReview.Architecture.Tests/DecoderRegistrationTests.cs` (new):
  1. `Release_RegistersEveryBackendOfferedInSettings`: `var s = new ServiceCollection(); App.ConfigureServices(s);` resolve `IImageDecoderFactory`; for each `Enum.GetValues<DecoderBackend>()` assert `IsRegistered` (this test runs from the test output folder, which gets `turbojpeg.dll` through the App reference → it actually checks the App graph).
  2. `ImageDecoderFactory_HasNoReflectionLoading`: source scan of `src/PhotoReview.Imaging/**/*.cs` for `Type.GetType(` and `Activator.CreateInstance(` → empty.
- `tests/PhotoReview.Imaging.Tests/Decoding/FactoryTests.cs`: add `Create_UnregisteredBackend_ReturnsWpfDecoder` (current fallback behaviour) and `IsRegistered_ReflectsProviders`.

### Step A7 — docs
- `docs/architecture.md`: the `App ├─> Imaging.TurboJpeg` arrow becomes true; remove the "known gap" note for F1.
- `docs/adr/0001-image-decoder.md`: append "2026-09-xx: TurboJpeg registered explicitly in App (AR01); reflection loading removed."
- `ACTIVE-TASKS.md` AR01 → DONE with SHA; `task_on_progress.md` one line.

## 3. Option B — stop offering it
1. `SettingsWindow.xaml(.cs)`: remove the TurboJpeg item; map a saved `TurboJpeg` value to `Wpf` on load (`SettingsStore` migration + test).
2. README (both languages): TurboJpeg = benchmark-only backend.
3. Keep `Imaging.TurboJpeg` for `Benchmark.Cli`; still delete the `Type.GetType` block (dead code in the app).

## 4. Verification

```powershell
dotnet build PhotoReview.slnx -c Release                   # 0 warnings
dotnet test PhotoReview.slnx -c Release --filter "Category!=Manual"
dotnet publish src/PhotoReview.App/PhotoReview.App.csproj -c Release --self-contained false -o src/PhotoReview.App/bin/Release/net10.0-windows/publish
.\tools\verify-release.ps1 -ReleaseDirectory 'src/PhotoReview.App/bin/Release/net10.0-windows/publish'
```

**Manual (real machine, ~5 min):** run the published exe → Settings → TurboJPEG enabled → open a folder of 24 MP JPEGs → Diagnostics shows decodes on `TurboJpeg`; open a JPEG with embedded ICC → counted as fallback to WPF (expected, see `TurboJpegDecoder.HasEmbeddedIccProfile`). Rename `turbojpeg.dll` → restart → item disabled, one forced log line explains why.

## 5. Acceptance criteria
- Publish folder contains `PhotoReview.Imaging.TurboJpeg.dll` and `turbojpeg.dll`; `verify-release.ps1` fails if either is removed.
- No `Type.GetType`/`Activator.CreateInstance` in `src/PhotoReview.Imaging`.
- Architecture test enumerates all `DecoderBackend` values against the App graph.
- Default backend stays `Wpf` → zero behaviour change for users who never touched the setting.

## 6. Risks / rollback
- Release size +~0.7 MB (turbojpeg.dll). Acceptable.
- AnyCPU App + x64-only native dll: fine on x64 Windows (the only supported target); the probe covers exotic cases.
- Rollback = revert the PR; config values remain valid because the enum is unchanged.
