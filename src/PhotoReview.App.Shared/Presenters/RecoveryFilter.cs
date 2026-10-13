namespace PhotoReview.App;

/// <summary>Verdict filter offered by the Recovery window. Order matches the ComboBox items.</summary>
public enum RecoveryFilter
{
    All,
    CanRetry,
    AlreadyDone,
    Problems,
}

/// <summary>
/// The colour hint of a verdict or path status (WP-35 G0: a tone instead of a WPF brush, so the mapping has no UI type).
/// A verdict is always shown as text; the colour is only a hint.
/// </summary>
public enum RecoveryTone
{
    Neutral,
    Good,
    Info,
    Warn,
    Bad,
}
