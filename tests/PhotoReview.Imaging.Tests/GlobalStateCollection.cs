namespace PhotoReview.Imaging.Tests;

/// <summary>Collection definition for tests that observe or mutate process-global state (GC finalizers, TaskScheduler.UnobservedTaskException).</summary>
[CollectionDefinition("GlobalState", DisableParallelization = true)]
public sealed class GlobalState
{
}
