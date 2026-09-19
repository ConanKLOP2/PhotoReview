using System.Runtime.CompilerServices;
using System.Windows;

// T14a: the xUnit suite hosts MainWindow on an STA thread through its internal test seam
// (MainWindowTestHooks, IProgressiveExplorerOrderProvider).
[assembly: InternalsVisibleTo("PhotoReview.Tests.Unit")]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,
    ResourceDictionaryLocation.SourceAssembly
)]
