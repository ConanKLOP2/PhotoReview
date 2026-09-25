using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace PhotoReview.App.Tests;

internal static class LocalizationModuleInit
{
    // I18N (ADR 0006): these tests assert the Vietnamese UI text that existed before extraction, so the
    // whole assembly runs with the shipped Vietnamese catalog. Tests that need English use TestLocalization.Use.
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "Test assembly: pins the UI language before any test runs; xunit v2 has no assembly fixture.")]
    internal static void Init()
    {
        TestLocalization.UseVietnamese();
        PrewarmThreadPool();
    }

    // Several tests (e.g. ViewerQuickFeaturesTests' Finder fake) run a background search via Task.Run whose
    // delegate blocks a real pool worker thread on a ManualResetEventSlim gate to pin down continuation
    // ordering deterministically. That is fine in isolation, but a full-solution run executes many test
    // collections (this assembly, Integration.Tests, etc.) in parallel, and the CLR's default ThreadPool only
    // grows by about one thread every ~0.5-1s once starved (hill-climbing injection). With the default min
    // (== core count) already tied up by other tests' blocking gates, a newly queued Task.Run can sit
    // unscheduled long enough to trip a test's wall-clock WithTimeout guard -- a spurious failure that
    // disappears in an isolated or single-project run because there is not enough concurrent demand to starve
    // the pool. Raising the minimum makes new worker threads available immediately instead of waiting on
    // hill-climbing, removing that window without any sleep/poll in the tests themselves.
    private static void PrewarmThreadPool()
    {
        var minThreads = Math.Max(Environment.ProcessorCount * 4, 32);
        ThreadPool.SetMinThreads(minThreads, minThreads);
    }
}
