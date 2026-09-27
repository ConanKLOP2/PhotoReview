using System.Runtime.CompilerServices;
using PhotoReview.TestSupport;

namespace PhotoReview.Core.Tests;

internal static class TestIsolationBootstrap
{
#pragma warning disable CA2255 // Intentional: must run before any test can resolve AppPaths.FromEnvironment().
    [ModuleInitializer]
    internal static void Init() => TestProcessIsolation.Install();
#pragma warning restore CA2255
}
