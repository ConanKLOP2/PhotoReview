using Xunit;

namespace PhotoReview.Integration.Tests;

/// <summary>Collection definition for tests that mutate global state (PHOTOREVIEW_DATA_ROOT, AppLog).</summary>
[CollectionDefinition("GlobalState", DisableParallelization = true)]
public sealed class GlobalStateCollection
{
}
