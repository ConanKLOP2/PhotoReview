# ADR 0004: Presentation Project Separation Investigation

**Date**: 2026-09-20  
**Status**: Investigation Complete — Recommendation: **DO NOT** separate at this time  
**Related Tasks**: ST12 (investigation-only), ST06 (remove CLI reflection), ST09 (extract controllers)

## Context

After ST06 (remove CLI reflection from MainWindow) and ST09 (extract FileActionController, DuplicateCleanupController, SiblingFolderNavigator), we investigated whether to create a `Presentation` project (`net10.0-windows`, no XAML or Window classes) to separate ViewModels/Coordinators from the main App project.

**Motivation**: Reduce WPF dependency, simplify `Benchmark.Cli` and test references to App.

## Investigation Results

### 1. Types Candidate for Separation (16 files)

**ViewModels** (4 files):
- `MainViewModel.cs` — claims K-2 compliance (WPF-independent per code comment)
- `CompareViewModel.cs` — state holder, WPF reference free
- `ViewerState.cs` — display state, no WPF
- `StatusFormatter.cs` — utility, no WPF

**Coordinators** (12 files):
- `FolderLoadCoordinator.cs`, `ImagePresenter.cs` — no WPF
- `FileActionController.cs`, `DuplicateCleanupController.cs`, `SiblingFolderNavigator.cs` — no WPF
- Sink interfaces: `IFileActionSink.cs`, `IDuplicateCleanupSink.cs`, `ISiblingNavigatorSink.cs`, `IPresentationSink.cs`, `IFolderLoadSink.cs` — no WPF
- `PreloadControllerAdapter.cs` — no WPF

**Only WPF-dependent**: `Services/WpfPresentationSink.cs` (must stay in App).

### 2. Circular Reference Analysis: **ZERO RISK**

- **Core** does NOT reference App ✓
- **Imaging** does NOT reference App ✓
- **Platform.Windows** does NOT reference App ✓

Lower layers are already isolated → safe to move types.

### 3. Dependency Bottleneck: App.xaml.cs & MainWindow.xaml.cs

Both are **heavily entangled** with MainViewModel. Every navigation/command flows through:
1. App.xaml.cs composition root creates MainViewModel
2. MainWindow.xaml.cs binds DataContext, wires events, calls ViewModel methods on every user action
3. ~60 direct `_viewModel.` calls in MainWindow.xaml.cs alone

**Problem**: Moving MainViewModel to Presentation would require:
- App still needs full reference to Presentation (not gain)
- XAML bindings (MainWindow.xaml: `DataContext="{Binding ...}"`) need public APIs or clumsy internal visibility
- `MainViewModelCompositionRoot` would split between projects, complicating DI wiring

### 4. Build/Test Impact Analysis

**Current dependencies**:
- `Benchmark.Cli` → `PhotoReview.App` (ST06 removes reflection; Cli still references App for types)
- App.Tests, Integration.Tests → App (bindings, composition)

**If we moved Presentation**:
- App still requires Presentation reference (circular or at least mutual dependency)
- Tests gain minimal benefit (still need both App + Presentation)
- CLI would still reference both App (for composition, dialogs) and Presentation (for ViewModels)
- **Result**: No dependency reduction; just more projects to maintain

### 5. Build Performance Estimate

- **Presentation project**: ~0.5 additional targets in build graph
- **XAML binding resolution**: Complications without simplification
- **Net build time delta**: ~0–5% slower (added project overhead vs minimal code removal from App)

### 6. Test Impact

- **Core.Tests**: Would need Presentation reference (currently only App)
- **App.Tests**: Already reference App; moving VMs doesn't simplify fixture setup
- **Integration.Tests**: Multi-project setup complicates scenario setup
- **No test runtime improvement**: Still use `MainViewModel` in place via composition or builders

## Decision

**Recommendation: DO NOT separate Presentation at this time.**

### Rationale

1. **No actual decoupling**: App.xaml.cs and MainWindow.xaml.cs cannot move and are entangled with ViewModels. Moving types to Presentation creates a sibling project, not a dependency reduction.

2. **Circular dependency trap**: Presentation would need public API for ViewModel properties, and App would still reference Presentation. Benefits of isolation disappear.

3. **XAML binding complexity**: Binding to ViewModel from XAML in App (pointing to types in Presentation) is feasible but requires InternalsVisibleTo or public API design, adding boilerplate.

4. **Negligible build benefit**: One additional project in build graph costs more than splitting 16 files buys. CLI and tests still reference App.

5. **ST06 already solves the main pain**: Removing CLI reflection eliminates the need for private MainWindow members. CLI can reference App at library level without reflection tricks.

6. **Simplicity wins**: Keeping ViewModels/Coordinators in App keeps composition root, XAML binding and tests in one place. Cost of change (file moves, namespace updates, InternalsVisibleTo, DI rewiring) exceeds ongoing maintenance burden of current structure.

## Alternative: Incremental Improvement (Recommended)

Instead of Presentation separation, pursue:
1. **ST07**: Make MainViewModel dependencies explicit (required vs optional) — reduces parameter noise
2. **ST08–ST09**: Extract FileActionController, DuplicateCleanupController — already done, improves MainViewModel readability
3. **WD01–WD06**: Improve WpfDialogService layering — removes WPF from business logic layers (Core/Imaging stay clean)

These changes achieve the goal (cleaner architecture, testability) without the overhead of a new project.

## Conclusion

The Presentation project would be **architecturally cleaner on paper but operationally heavier in practice**. The current structure, after ST06–ST09, is already well-layered (Core/Imaging separate from App) and the ViewModel/Coordinator split into controllable pieces. Further separation is premature.

**Revisit only if**:
- Separate CLI tool needs to deploy without App.xaml (post-issue: none identified)
- XAML reuse across multiple apps (not planned)
- Significant WPF performance issue traced to Presentation code (not observed)

---

**Investigated by**: Claude Haiku 4.5 on branch `codex/st01-clean-dead-code` (2026-09-20)  
**Evidence**: Project reference analysis, grep results, 16-file type inventory, circular dependency check.
