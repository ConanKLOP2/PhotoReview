namespace PhotoReview.Core.Catalog;

public enum ExplorerOrderStatus
{
    Available,
    NoMatchingWindow,
    NativeViewUnavailable,
    InvalidSnapshot,
    TimedOut,
    Canceled,
    Failed
}

public enum ExplorerSortDirection
{
    Unknown,
    Ascending,
    Descending
}

public enum ExplorerGroupState
{
    None,
    Active,
    Unknown
}

public sealed record ExplorerSortColumn(Guid PropertySet, uint PropertyId, ExplorerSortDirection Direction);

public sealed record ExplorerQueryProgress(int ItemsRead, int ItemCount, int ComCalls);

public sealed record ExplorerViewSnapshot(
    string Folder,
    IReadOnlyList<string> OrderedPaths,
    IReadOnlyList<ExplorerSortColumn> SortColumns,
    ExplorerGroupState GroupState,
    ExplorerOrderStatus Status,
    string? Reason,
    DateTime CapturedUtc);
