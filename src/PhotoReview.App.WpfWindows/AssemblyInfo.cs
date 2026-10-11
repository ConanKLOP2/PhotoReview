using System.Runtime.CompilerServices;

// WP-19b: kieu internal cua cua so phu (RecoveryPresenter, DarkTitleBarChrome, ...) van cho App WPF, cau noi shell va cac bo test.
[assembly: InternalsVisibleTo("PhotoReview.App")]
[assembly: InternalsVisibleTo("PhotoReview.Shell.WpfBridge")]
[assembly: InternalsVisibleTo("PhotoReview.App.Tests")]
[assembly: InternalsVisibleTo("PhotoReview.Integration.Tests")]
[assembly: InternalsVisibleTo("PhotoReview.Shell.Tests")]
[assembly: InternalsVisibleTo("PhotoReview.Shell.Integration.Tests")]
[assembly: InternalsVisibleTo("PhotoReview.Shell.WpfBridge.Tests")]
