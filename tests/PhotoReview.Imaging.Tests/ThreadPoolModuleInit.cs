using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace PhotoReview.Imaging.Tests;

internal static class ThreadPoolModuleInit
{
    // Tests that block a pool worker on a ManualResetEventSlim gate (Task.Run + Wait) starve the default ThreadPool when
    // every test project runs in parallel; a newly queued Task.Run then waits for hill-climbing thread injection (~0.5-1 s
    // per thread) and trips a wall-clock guard. Raising the minimum makes workers available immediately (same fix as
    // PhotoReview.App.Tests; failures were 10-second timeouts that pass alone).
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "Test assembly: configures the pool before any test runs; xunit v2 has no assembly fixture.")]
    internal static void Init()
    {
        var minThreads = Math.Max(Environment.ProcessorCount * 4, 32);
        ThreadPool.SetMinThreads(minThreads, minThreads);
    }
}
