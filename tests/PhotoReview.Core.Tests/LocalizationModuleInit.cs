using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace PhotoReview.Core.Tests;

internal static class LocalizationModuleInit
{
    // I18N (ADR 0006, L06): these tests assert the Vietnamese Core messages that existed before extraction, so the
    // whole assembly runs with the shipped Vietnamese catalog (same as PhotoReview.App.Tests). Tests that need
    // English (or the ambient default) switch Localizer.Current inside the serial [Collection("GlobalState")].
    //
    // Culture matrix: set PHOTOREVIEW_TEST_CULTURE (e.g. tr-TR, de-DE, ja-JP, ar-SA) to run the whole suite with that
    // regional format on every thread; production code that writes machine-read text must not depend on it.
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "Test assembly: pins the UI language before any test runs; xunit v2 has no assembly fixture.")]
    internal static void Init()
    {
        TestLocalization.UseVietnamese();
        PrewarmThreadPool();
        var name = Environment.GetEnvironmentVariable("PHOTOREVIEW_TEST_CULTURE");
        if (string.IsNullOrWhiteSpace(name)) return;
        CultureInfo culture;
        try
        {
            culture = CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException ex)
        {
            // A module initializer failure surfaces as an opaque TypeInitializationException for the whole assembly.
            throw new InvalidOperationException($"PHOTOREVIEW_TEST_CULTURE='{name}' is not a valid culture name (e.g. tr-TR, de-DE, ja-JP).", ex);
        }

        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.CurrentCulture = culture;
    }

    // Tests that block a pool worker on a ManualResetEventSlim gate (Task.Run + Wait) starve the default ThreadPool when
    // every test project runs in parallel; a newly queued Task.Run then waits for hill-climbing thread injection (~0.5-1 s
    // per thread) and trips a wall-clock guard. Raising the minimum makes workers available immediately (same fix as
    // PhotoReview.App.Tests; failures were 10-second timeouts that pass alone).
    private static void PrewarmThreadPool()
    {
        var minThreads = Math.Max(Environment.ProcessorCount * 4, 32);
        ThreadPool.SetMinThreads(minThreads, minThreads);
    }
}
