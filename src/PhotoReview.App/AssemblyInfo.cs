using System.Runtime.CompilerServices;
using System.Windows;

// AR02d: the xUnit suites resolve MainWindow through AppHost.BuildServices (TestAppHost/CompositionRootTests)
// on an STA thread; InternalsVisibleTo remains for other internal test seams (e.g. MainWindowHelpers).
[assembly: InternalsVisibleTo("PhotoReview.App.Tests")]
[assembly: InternalsVisibleTo("PhotoReview.Integration.Tests")]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,
    ResourceDictionaryLocation.SourceAssembly
)]
