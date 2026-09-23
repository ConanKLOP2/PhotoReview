# AR03 — Small DI and boundary cleanup

**Finding:** F7 · **Decision:** none needed · **Size:** ~0.5 day, 3 independent commits (one PR `refactor/ar03-di-cleanup` or three) · **GUI:** no

## AR03a — one place decides `UseSourceBytesCache`

**Now** (`src/PhotoReview.App/App.xaml.cs`, `ConfigureServices`): the expression
`settings.UseSourceBytesCache ? sp.GetRequiredService<SourceBytesCache>() : null` is repeated for `FileHashService`, `ThumbnailCache`, `PreviewImageService` and the `PreloadScheduler` factory (`prefetchSourceBytes`).

**Change**
1. New `src/PhotoReview.Imaging/Caching/SourceBytesCachePolicy.cs`:
   ```csharp
   public sealed class SourceBytesCachePolicy(SourceBytesCache? cache)
   {
       public SourceBytesCache? Cache { get; } = cache;
       public bool Enabled => Cache is not null;
   }
   ```
2. Register once: `services.AddSingleton(sp => { var s = sp.GetRequiredService<SettingsStore>().Current; return new SourceBytesCachePolicy(s.UseSourceBytesCache ? new SourceBytesCache(s.SourceBytesCapacityBytes) : null); });` and remove the standalone `SourceBytesCache` registration (nothing else resolves it — verify with `grep -rn "GetRequiredService<SourceBytesCache>\|GetService<SourceBytesCache>" src tools tests`).
3. The four registrations use `sp.GetRequiredService<SourceBytesCachePolicy>().Cache`. Consumer constructors keep `SourceBytesCache?` — a null object would change I/O shape (whole-file buffer vs stream, see `architecture.md` "SourceBytesCache"), so consumers keep their existing branch.
4. Test (`App.Tests/CompositionRootTests`): with `UseSourceBytesCache=false`, `SourceBytesCachePolicy.Enabled == false`; with `true`, the same `SourceBytesCache` instance reaches `PreviewImageService`, `ThumbnailCache` and `FileHashService` (read private field via existing reflection helper pattern used at `CompositionRootTests.cs:96`).

**Not in scope:** the combined RAM budget of `SourceBytesCache` (16 GiB) + preview cache when both are enabled — needs measurements (`task_on_progress.md` deferred list).

## AR03b — `Platform.Windows` without WPF

**Now:** `src/PhotoReview.Platform.Windows/PhotoReview.Platform.Windows.csproj` sets `<UseWPF>true</UseWPF>`; `grep -rn "System.Windows" src/PhotoReview.Platform.Windows` → 0 hits.

**Change**
1. Delete `<UseWPF>true</UseWPF>` (keep `net10.0-windows`, `Platforms`, `RuntimeIdentifiers`).
2. Build. If a type from `WindowsBase`/`PresentationCore` is used implicitly, the build fails — then keep `UseWPF` and document why in the csproj.
3. Extend `tests/PhotoReview.Architecture.Tests/LayerDependencyTests.cs`: rule "Platform.Windows does not reference `PresentationFramework`, `PresentationCore`, `WindowsBase`" (same NetArchTest style as Rule 1 for Core).

## AR03c — startup message through `IDialogService`

**Now:** `App.xaml.cs:182` calls `System.Windows.MessageBox.Show("Folder này đang được mở trong một Photo Review khác.", …)` directly (instance lock).

**Change**
1. Replace with `_services.GetRequiredService<IDialogService>().ShowMessage("Photo Review", "Folder này đang được mở trong một Photo Review khác.");` (`IDialogService.ShowMessage(title, message)` exists in Core).
2. Leave `MessageBox` calls inside `SettingsWindow`, `RecoveryWindow`, `ActionProfilesWindow` code-behind (view-layer, acceptable). `WpfDialogService` remains the only non-window caller.
3. Architecture test: source scan — `MessageBox.Show(` allowed only in `Services/WpfDialogService.cs` and `*Window.xaml.cs`.

This is the low-risk part of WD02; mark WD02 "partially covered by AR03c" in `ACTIVE-TASKS.md`.

## Verification
`dotnet build PhotoReview.slnx -c Release` (0 warnings) · `dotnet test PhotoReview.slnx -c Release --filter "Category!=Manual"` · `.\tools\verify-all.ps1`.

## Acceptance
- Exactly one `UseSourceBytesCache` read in `App.ConfigureServices`.
- `Platform.Windows` builds without `UseWPF` (or a documented reason why not).
- New architecture rules green.
