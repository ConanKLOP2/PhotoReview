using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace PhotoReview.App.Tests;

internal static class LocalizationModuleInit
{
    // I18N (ADR 0006): these tests assert the Vietnamese UI text that existed before extraction, so the
    // whole assembly runs with the shipped Vietnamese catalog. Tests that need English use TestLocalization.Use.
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "Test assembly: pins the UI language before any test runs; xunit v2 has no assembly fixture.")]
    internal static void Init() => TestLocalization.UseVietnamese();
}
