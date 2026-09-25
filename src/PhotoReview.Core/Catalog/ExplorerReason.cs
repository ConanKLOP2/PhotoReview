namespace PhotoReview.Core.Catalog;

/// <summary>
/// Stable, language-neutral reason codes for an unavailable/invalid Explorer snapshot
/// (<see cref="ExplorerViewSnapshot.Reason"/>, <see cref="ExplorerSnapshotValidator"/>). A reason is
/// <c>code</c> or <c>code|detail|detail</c>; the UI maps the code to catalog text and inserts the technical
/// details (exception type, HRESULT, counts) verbatim, so the library never carries user-visible prose.
/// </summary>
public static class ExplorerReason
{
    public const string Timeout = "timeout";
    public const string Canceled = "canceled";
    /// <summary>Detail: exception type name.</summary>
    public const string QueryFailed = "query-failed";
    public const string ShellUnavailable = "shell-unavailable";
    /// <summary>Details: exception type name, HRESULT.</summary>
    public const string NativeViewFailed = "native-view-failed";
    /// <summary>Detail: number of Explorer windows inspected.</summary>
    public const string NoMatchingWindow = "no-matching-window";
    /// <summary>Details: COM call name, HRESULT.</summary>
    public const string ComCallFailed = "com-call-failed";
    /// <summary>Details: HRESULT, item count.</summary>
    public const string EmptyView = "empty-view";
    public const string EmptyPath = "empty-path";
    public const string OutsideFolder = "outside-folder";
    public const string DuplicateItem = "duplicate-item";
    public const string IncompleteSnapshot = "incomplete-snapshot";

    private const char Separator = '|';

    public static string Format(string code, params string[] details) =>
        details.Length == 0 ? code : code + Separator + string.Join(Separator, details);

    /// <summary>Splits a reason into its code and details (a text without a known shape is returned as its own code).</summary>
    public static (string Code, string[] Details) Parse(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        var parts = reason.Split(Separator);
        return (parts[0], parts[1..]);
    }
}
