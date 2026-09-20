using System.IO;

namespace PhotoReview.Core.Tests.Session;

/// <summary>The default constructor resolves its data root from PHOTOREVIEW_DATA_ROOT.</summary>
[Collection("GlobalState")]
[Trait("Category", "HotPath")]
public sealed class SessionStoreEnvironmentRootTests : IDisposable
{
    private readonly DataRootFixture _data = new();

    public void Dispose() => _data.Dispose();

    [Fact(DisplayName = "Session save/load")]
    public void SessionSaveAndLoad()
    {
        var root = _data.Path;
        var store = new SessionStore();
        var state = new SessionState
        {
            Folder = root,
            CurrentPath = Path.Combine(root, "one.jpg"),
            Skipped = [Path.Combine(root, "skip.jpg")]
        };
        store.Save(state);
        var loaded = store.Load(root);
        Assert.True(loaded.CurrentPath == state.CurrentPath && loaded.Skipped.Count == 1);
    }
}
