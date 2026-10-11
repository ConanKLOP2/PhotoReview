namespace PhotoReview.App.Services;

/// <summary>
/// AR02a: production seam replacing <c>MainWindowTestHooks.OnPresented</c>. The composition
/// root wires this into <see cref="WpfPresentationSink"/> so callers (tests, benchmarks) can
/// observe presentation completions via a DI override instead of a constructor-only hook.
/// </summary>
public interface IPresentationObserver
{
    void OnPresented(string path);
}

/// <summary>Default production observer: does nothing.</summary>
public sealed class NullPresentationObserver : IPresentationObserver
{
    public static readonly NullPresentationObserver Instance = new();

    public void OnPresented(string path) { }
}
