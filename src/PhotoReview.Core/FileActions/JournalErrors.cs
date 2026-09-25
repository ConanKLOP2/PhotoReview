using System.Collections.Frozen;
using System.IO;
using PhotoReview.Core.Localization;

namespace PhotoReview.Core.FileActions;

/// <summary>
/// Stable error codes persisted in the operation journal (ADR 0006, decision Q-L3). The journal is machine data
/// (AGENTS.md rule 4): a record stores <see cref="JournalEntry.ErrorCode"/> plus invariant English text in
/// <see cref="JournalEntry.Error"/>, never the UI language. The UI localizes by code via <see cref="Describe(JournalEntry)"/>;
/// records written before codes existed (no code, text in whatever language was current) show their stored text.
/// Codes are persisted: never rename or reuse one.
/// </summary>
public static class JournalErrors
{
    /// <summary>A pending Recycle Bin operation found its source still present during startup reconcile.</summary>
    public const string SourceStillExistsAfterRecovery = "SourceStillExistsAfterRecovery";

    /// <summary>A pending Move/Copy could not be confirmed during startup reconcile; it is not replayed.</summary>
    public const string PendingUnconfirmed = "PendingUnconfirmed";

    /// <summary>After Move/Copy the destination size did not match the source.</summary>
    public const string VerifySizeChanged = "VerifySizeChanged";

    /// <summary>After a recovery retry the destination check failed.</summary>
    public const string RetryVerifyFailed = "RetryVerifyFailed";

    /// <summary>A relative Move/Copy destination resolved outside the source folder; rejected before anything is journaled.</summary>
    public const string DestinationOutsideSource = "DestinationOutsideSource";

    /// <summary>
    /// A Move copied the file (cross-volume MoveFileEx) but could not delete the source (read-only, opened without
    /// FILE_SHARE_DELETE): both copies exist, so the Move did not happen from the user's point of view.
    /// </summary>
    public const string MoveSourceNotRemoved = "MoveSourceNotRemoved";

    private static readonly FrozenDictionary<string, string> s_keys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [MoveSourceNotRemoved] = TrKeys.CoreFileActionMoveSourceNotRemoved,
        [SourceStillExistsAfterRecovery] = TrKeys.CoreJournalSourceStillExists,
        [PendingUnconfirmed] = TrKeys.CoreJournalPendingUnconfirmed,
        [VerifySizeChanged] = TrKeys.CoreFileActionVerifyFailedSizeChanged,
        [RetryVerifyFailed] = TrKeys.CoreRecoveryVerifyFailed,
        [DestinationOutsideSource] = TrKeys.CoreFileActionDestinationOutsideSource,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>True when <paramref name="code"/> is a code this build knows how to describe.</summary>
    public static bool IsKnown(string? code) => code is not null && s_keys.ContainsKey(code);

    /// <summary>The invariant English text stored next to <paramref name="code"/> in the journal.</summary>
    public static string EnglishText(string code) => Text(BuiltInCatalog.EnglishLocalizer, code);

    /// <summary>The text for <paramref name="code"/> in the current UI language (<see cref="Localizer.Current"/>).</summary>
    public static string LocalizedText(string code) => Text(Localizer.Current, code);

    /// <summary>
    /// UI text for a journal error: the localized text of a known <paramref name="code"/>; otherwise (old record
    /// without a code, a code from a newer build, or a raw OS message) the <paramref name="storedText"/> as written.
    /// </summary>
    public static string? Describe(string? code, string? storedText) =>
        code is not null && s_keys.TryGetValue(code, out var key) ? Localizer.Current.Get(key) : storedText;

    /// <inheritdoc cref="Describe(string?, string?)"/>
    public static string? Describe(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Describe(entry.ErrorCode, entry.Error);
    }

    /// <summary>
    /// The (code, text) pair to persist for a failure: a <see cref="JournalCodedException"/> is stored as its code
    /// plus English text; any other exception keeps its own message (OS text) with no code.
    /// </summary>
    internal static (string? Code, string Text) ForJournal(Exception ex) =>
        ex is JournalCodedException coded && IsKnown(coded.Code) ? (coded.Code, EnglishText(coded.Code)) : (null, ex.Message);

    private static string Text(Localizer localizer, string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return s_keys.TryGetValue(code, out var key)
            ? localizer.Get(key)
            : throw new ArgumentException($"Unknown journal error code '{code}'.", nameof(code));
    }
}

/// <summary>
/// An IO failure with a stable journal code. <see cref="Exception.Message"/> is the UI text (current language,
/// shown in the status bar / Recovery window); the journal stores <see cref="Code"/> plus English text instead.
/// </summary>
public sealed class JournalCodedException : IOException
{
    /// <param name="code">One of the <see cref="JournalErrors"/> codes (not a message).</param>
    public JournalCodedException(string code)
        : base(JournalErrors.LocalizedText(code))
    {
        Code = code;
    }

    public JournalCodedException()
    {
        Code = string.Empty;
    }

    public JournalCodedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = string.Empty;
    }

    public string Code { get; }
}
