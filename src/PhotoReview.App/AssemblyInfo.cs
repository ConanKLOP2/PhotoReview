using System.Runtime.CompilerServices;
using System.Windows;

// T14a: the xUnit suite hosts MainWindow on an STA thread through its internal test seam (MainWindowTestHooks).
[assembly: InternalsVisibleTo("PhotoReview.App.Tests")]
[assembly: InternalsVisibleTo("PhotoReview.Integration.Tests")]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,
    ResourceDictionaryLocation.SourceAssembly
)]
