using System.Runtime.CompilerServices;

// NO-WPF-EXEC-PLAN mục 3.1: kiểu internal dời từ App sang App.Shared (WP-07/WP-09) vẫn cần cho App WPF, shell Win32
// (assembly "PhotoReview", NE-2) và các bộ test/benchmark của cả hai bản.
[assembly: InternalsVisibleTo("PhotoReview.App")]
[assembly: InternalsVisibleTo("PhotoReview")]
[assembly: InternalsVisibleTo("PhotoReview.App.Tests")]
[assembly: InternalsVisibleTo("PhotoReview.Shell.Tests")]
[assembly: InternalsVisibleTo("PhotoReview.Integration.Tests")]
[assembly: InternalsVisibleTo("PhotoReview.Shell.Integration.Tests")]
[assembly: InternalsVisibleTo("PhotoReview.Benchmark.Cli")]
