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
        var name = Environment.GetEnvironmentVariable("PHOTOREVIEW_TEST_CULTURE");
        if (string.IsNullOrWhiteSpace(name)) return;
        var culture = CultureInfo.GetCultureInfo(name);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.CurrentCulture = culture;
    }
}
