using System.IO;
using PhotoReview.Core.IO;
using PhotoReview.Core.Localization;

namespace PhotoReview.TestSupport;

/// <summary>
/// Test assemblies whose assertions predate I18N use the shipped Vietnamese catalog (copied to
/// <c>&lt;test bin&gt;\Languages</c> by the Core project), so extracting strings into catalogs stays a pure
/// refactor: every existing Vietnamese assertion must still pass unchanged.
/// </summary>
public static class TestLocalization
{
    private static readonly Lazy<Localizer> s_vietnamese = new(() => Load("vi"));

    public static Localizer Vietnamese => s_vietnamese.Value;

    public static Localizer English => BuiltInCatalog.EnglishLocalizer;

    public static void UseVietnamese() => Localizer.SetCurrent(Vietnamese);

    /// <summary>Sets <paramref name="localizer"/> until the returned scope is disposed.</summary>
    public static IDisposable Use(Localizer localizer)
    {
        var previous = Localizer.Current;
        Localizer.SetCurrent(localizer);
        return new Restore(previous);
    }

    private static Localizer Load(string code)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Languages");
        var localizer = new LanguageLoader(new PhysicalFileSystem(), dir, userDir: null).Load(code);
        if (!string.Equals(localizer.Code, code, StringComparison.Ordinal))
            throw new InvalidOperationException($"Catalog '{code}' not found in {dir}.");
        return localizer;
    }

    private sealed class Restore(Localizer previous) : IDisposable
    {
        public void Dispose() => Localizer.SetCurrent(previous);
    }
}
