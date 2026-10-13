using System.Globalization;
using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Localization;

namespace PhotoReview.App.Presenters;

/// <summary>
/// One Recovery list row without a UI type: <see cref="Check"/> stays null ("checking") until the background check reports.
/// A view wraps it (WPF: <c>RecoveryRow</c> adds change notification and brushes).
/// </summary>
internal sealed class RecoveryRowModel(JournalEntry entry)
{
    private RecoveryCheckResult? _result;

    /// <summary>Raised on the thread that called <see cref="SetResult"/> after the check result changed.</summary>
    public event Action? ResultChanged;

    public JournalEntry Entry { get; } = entry;

    public RecoveryCheckResult? Check => _result;

    public string Title => Entry.GroupMembers is { Count: > 0 } members
        ? Tr.RecoveryGroupTitle(Path.GetFileName(Entry.Source), members.Count)
        : Path.GetFileName(Entry.Source);

    public string Subtitle => Tr.RecoveryRowSubtitle(RecoveryPresentation.StateText(Entry.State), RecoveryPresentation.OperationText(Entry.Type));

    public string BadgeText => _result is null ? Tr.RecoveryRowChecking : RecoveryPresentation.VerdictText(_result.Verdict);

    public RecoveryTone BadgeTone => _result is null ? RecoveryTone.Neutral : RecoveryPresentation.VerdictTone(_result.Verdict);

    public void SetResult(RecoveryCheckResult? result)
    {
        _result = result;
        ResultChanged?.Invoke();
    }
}

/// <summary>Everything the details panel shows for the selected row (a view only copies it into controls).</summary>
internal sealed record RecoveryDetails(
    string ErrorText,
    bool ErrorVisible,
    string VerdictText,
    RecoveryTone VerdictTone,
    string ExplainText,
    string ActionText,
    RecoveryPathView? Source,
    bool SourceVisible,
    RecoveryPathView? Destination,
    bool DestinationVisible,
    bool NoDestinationVisible,
    string? GroupHeading,
    IReadOnlyList<RecoveryGroupMemberView>? GroupMembers);

/// <summary>The outcome of the Retry flow as the dialog must show it.</summary>
/// <param name="CloseWindow">Success: the window closes.</param>
/// <param name="Recheck">A refused retry re-runs the live check.</param>
internal sealed record RecoveryRetryNotice(string Title, string Message, bool IsWarning, bool CloseWindow, bool Recheck);

/// <summary>The outcome of a confirmed Clear: an error to show (nothing removed) or the count of rows skipped as stale.</summary>
internal sealed record RecoveryDismissResult(string? Error, int SkippedCount);

/// <summary>The few things the live check needs from a view.</summary>
internal interface IRecoveryCheckView
{
    /// <summary>The progress/summary line next to the Re-check button.</summary>
    void SetCheckStatus(string text);

    /// <summary>The Re-check button.</summary>
    void SetRecheckEnabled(bool enabled);

    /// <summary>A run finished normally: rebuild the visible list (the filter may now match differently).</summary>
    void CheckCompleted();
}

/// <summary>
/// WP-35 G0: the Recovery window's behaviour without a window -- the rows, the filter, the details model, the live
/// read-only check, Clear and Retry. Moved from the WPF code-behind <b>unchanged in order and conditions</b> (ADR 0003/0005/0007):
/// the background check never writes a file, Retry runs once on the supplied delegate, the window refuses to close while it runs,
/// and a row is dropped after Clear/superseded exactly as before. The retry delegate itself (RecoveryRetryService) is untouched.
/// </summary>
internal sealed class RecoveryWindowPresenter
{
    private readonly List<RecoveryRowModel> _rows;
    private readonly Func<JournalEntry, Task<RecoveryRetryResult>>? _retry;
    private readonly Func<IReadOnlyList<JournalEntry>, DismissOutcome>? _dismiss;
    private readonly RecoveryFileCheck _checker;
    private readonly Func<bool>? _allowPermanentDelete;
    private Action? _cancelCheck;

    public RecoveryWindowPresenter(IReadOnlyList<JournalEntry> entries, Func<JournalEntry, Task<RecoveryRetryResult>>? retry = null,
        Func<IReadOnlyList<JournalEntry>, DismissOutcome>? dismiss = null, IFileSystem? fileSystem = null, Func<bool>? allowPermanentDelete = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _rows = entries.Select(entry => new RecoveryRowModel(entry)).ToList();
        _retry = retry;
        _dismiss = dismiss;
        _allowPermanentDelete = allowPermanentDelete;
        _checker = new RecoveryFileCheck(fileSystem ?? new PhysicalFileSystem());
    }

    /// <summary>All rows still in the journal snapshot (the filter is applied by <see cref="Shown"/>).</summary>
    public IReadOnlyList<RecoveryRowModel> Rows => _rows;

    /// <summary>True while a retry is running: Retry/Clear are disabled and the window refuses to close.</summary>
    public bool IsRetrying { get; private set; }

    public bool CanClose => !IsRetrying;

    // ---- List ----

    /// <summary>The rows the filter lets through, in journal order.</summary>
    public List<RecoveryRowModel> Shown(RecoveryFilter filter) =>
        _rows.Where(row => RecoveryPresentation.Matches(filter, row.Check?.Verdict)).ToList();

    public string SummaryText => _rows.Count == 0 ? Tr.RecoverySummaryEmpty : Tr.RecoverySummaryCount(_rows.Count);

    /// <summary>The "nothing matches this filter" text is shown when there are rows but none is visible.</summary>
    public bool FilterEmptyVisible(int shownCount) => _rows.Count > 0 && shownCount == 0;

    /// <summary>Retry / Clear selected / Clear all availability.</summary>
    public (bool Retry, bool ClearSelected, bool ClearAll) ButtonStates(RecoveryRowModel? singleSelected, int selectedCount) =>
        (!IsRetrying && _retry is not null && RecoveryPresentation.AllowsRetry(singleSelected?.Check),
         !IsRetrying && _dismiss is not null && selectedCount > 0,
         !IsRetrying && _dismiss is not null && _rows.Count > 0);

    // ---- Details ----

    /// <summary>The details panel for <paramref name="row"/> (a row not yet checked shows its journal data and "checking").</summary>
    public static RecoveryDetails Details(RecoveryRowModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var entry = row.Entry;
        var result = row.Check;
        var error = JournalErrors.Describe(entry);
        var errorText = error is null ? string.Empty : Tr.RecoveryDetailError(error);
        var errorVisible = error is not null;

        if (result is null)
        {
            var source = new RecoveryPathView(Tr.RecoveryDetailSource, entry.Source, string.Empty, RecoveryTone.Neutral, string.Empty, RecoveryPresentation.JournalText(entry), null);
            var destination = entry.Destination is null
                ? null
                : new RecoveryPathView(Tr.RecoveryDetailDestination, entry.Destination, string.Empty, RecoveryTone.Neutral, string.Empty, RecoveryPresentation.JournalText(entry), null);
            return new RecoveryDetails(errorText, errorVisible, Tr.RecoveryRowChecking, RecoveryTone.Neutral, Tr.RecoveryDetailChecking, string.Empty,
                source, SourceVisible: true, destination, DestinationVisible: destination is not null, NoDestinationVisible: destination is null,
                GroupHeading: null, GroupMembers: null);
        }

        IReadOnlyList<RecoveryGroupMemberView>? members = null;
        string? heading = null;
        if (result.GroupMembers is { Count: > 0 } groupMembers)
        {
            members = groupMembers.Select(item => new RecoveryGroupMemberView(
                RecoveryPresentation.GroupStatusText(item.Verdict, item.Member, item.Source, item.Destination), item.Member.Source,
                item.Member.Destination ?? Tr.RecoveryDetailNoDestination)).ToArray();
            heading = Tr.RecoveryGroupMembers(groupMembers.Count);
        }
        var isGroup = members is not null;
        RecoveryPathView? sourceView = null;
        RecoveryPathView? destinationView = null;
        if (!isGroup)
        {
            sourceView = RecoveryPresentation.PathView(Tr.RecoveryDetailSource, result.Source, entry, folderMissingNote: false);
            destinationView = result.Destination is null
                ? null
                : RecoveryPresentation.PathView(Tr.RecoveryDetailDestination, result.Destination, entry, result.DestinationFolderMissing);
        }
        return new RecoveryDetails(errorText, errorVisible, RecoveryPresentation.VerdictText(result.Verdict), RecoveryPresentation.VerdictTone(result.Verdict),
            RecoveryPresentation.ExplainText(result.Verdict), RecoveryPresentation.ActionText(result.Verdict),
            sourceView, SourceVisible: !isGroup, destinationView, DestinationVisible: destinationView is not null,
            NoDestinationVisible: !isGroup && destinationView is null, heading, members);
    }

    // ---- Live read-only check ----

    /// <summary>
    /// Read-only live check of every entry, on a worker thread; each result is posted back to the UI as it arrives.
    /// A newer run (Re-check) or closing the window cancels the previous one. No file is ever modified.
    /// Must be called on the UI thread (the progress callbacks are marshalled back to the calling context).
    /// </summary>
    public async Task RunChecksAsync(IRecoveryCheckView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        _cancelCheck?.Invoke();
        using var cts = new CancellationTokenSource();
        _cancelCheck = () =>
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* that run already finished */ }
        };
        var token = cts.Token;
        var rows = _rows.ToList();
        var total = rows.Count;
        foreach (var row in rows) row.SetResult(null);
        if (total == 0)
        {
            view.SetCheckStatus(string.Empty);
            return;
        }

        view.SetRecheckEnabled(false);
        var done = 0;
        view.SetCheckStatus(Tr.RecoveryCheckProgress(0, total));
        var progress = new Progress<(RecoveryRowModel Row, RecoveryCheckResult Outcome)>(item =>
        {
            if (token.IsCancellationRequested) return;
            item.Row.SetResult(item.Outcome);
            view.SetCheckStatus(Tr.RecoveryCheckProgress(++done, total));
        });
        try
        {
            await Task.Run(() =>
            {
                foreach (var row in rows)
                {
                    token.ThrowIfCancellationRequested();
                    ((IProgress<(RecoveryRowModel, RecoveryCheckResult)>)progress).Report((row, _checker.Check(row.Entry)));
                }
            }, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // A checker failure (access denied on a share, ...) must not leave Re-check disabled for good. Only the run that
            // is still current touches the button: a newer run owns it, and a cancelled one is going away.
            AppLog.Error("Recovery live check failed", ex);
            if (!token.IsCancellationRequested)
            {
                view.SetRecheckEnabled(true);
                view.SetCheckStatus(string.Empty);
            }
            return;
        }
        if (token.IsCancellationRequested) return;
        view.SetRecheckEnabled(true);
        view.SetCheckStatus(Tr.RecoveryCheckDone(total, DateTime.Now.ToString("T", CultureInfo.CurrentCulture)));
        view.CheckCompleted();
    }

    /// <summary>The window closed: stop the running check.</summary>
    public void CancelCheck() => _cancelCheck?.Invoke();

    // ---- Clear (dismiss) ----

    /// <summary>The confirmation question for clearing <paramref name="rows"/>, or null when Clear is unavailable or nothing is selected.</summary>
    public string? DismissPrompt(IReadOnlyCollection<RecoveryRowModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (_dismiss is null || rows.Count == 0) return null;
        return Tr.RecoveryDismissConfirmMessage(rows.Count);
    }

    /// <summary>
    /// Runs the dismiss delegate after the user confirmed. A row is removed from the list when it was actually dismissed, or when the
    /// journal's current state no longer matches what this window snapshotted (skipped-as-stale) -- either way the row this window
    /// is holding no longer describes that Id and must not stay looking current. A throwing delegate removes nothing.
    /// </summary>
    public RecoveryDismissResult Dismiss(IReadOnlyList<RecoveryRowModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (_dismiss is null || rows.Count == 0) return new RecoveryDismissResult(null, 0);
        DismissOutcome outcome;
        try { outcome = _dismiss(rows.Select(row => row.Entry).ToList()); }
        catch (Exception ex)
        {
            return new RecoveryDismissResult(ex.Message, 0);
        }
        var byId = rows.Select(row => row.Entry.Id).ToHashSet(StringComparer.Ordinal);
        _rows.RemoveAll(row => byId.Contains(row.Entry.Id));
        return new RecoveryDismissResult(null, outcome.Skipped.Count);
    }

    // ---- Retry ----

    /// <summary>True when Retry can be started for <paramref name="row"/> right now (the button handler's guard).</summary>
    public bool CanStartRetry(RecoveryRowModel? row) => _retry is not null && row is not null && !IsRetrying;

    /// <summary>The Retry confirmation text for <paramref name="row"/>.</summary>
    public string RetryConfirmText(RecoveryRowModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return RecoveryPresentation.RetryConfirmText(row.Entry, row.Check, _allowPermanentDelete?.Invoke() == true);
    }

    /// <summary>
    /// Runs the retry delegate while keeping the UI thread free (a retried cross-drive copy can take minutes): <paramref name="setBusy"/>
    /// is told true before and false after (the view disables Retry/Clear and shows the wait cursor). A superseded result drops the row
    /// (P02: another window already handled this Id) and calls <paramref name="rowsChanged"/>.
    /// </summary>
    public async Task<RecoveryRetryResult> ExecuteRetryAsync(JournalEntry entry, Action<bool> setBusy, Action rowsChanged)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(setBusy);
        ArgumentNullException.ThrowIfNull(rowsChanged);
        if (_retry is null) throw new InvalidOperationException("Retry is not available.");
        IsRetrying = true;
        setBusy(true);
        RecoveryRetryResult result;
        try { result = await _retry(entry); }
        finally
        {
            IsRetrying = false;
            setBusy(false);
        }
        if (result.Superseded)
        {
            _rows.RemoveAll(row => string.Equals(row.Entry.Id, entry.Id, StringComparison.Ordinal));
            rowsChanged();
        }
        return result;
    }

    /// <summary>What to tell the user after a retry returned, and what to do next.</summary>
    public static RecoveryRetryNotice DescribeRetry(RecoveryRetryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Superseded)
            return new RecoveryRetryNotice(Tr.RecoveryRetryStaleTitle, result.Message, IsWarning: false, CloseWindow: false, Recheck: false);
        var journalWarning = result.Succeeded && !result.JournalPersisted;
        var title = result.Succeeded
            ? journalWarning ? Tr.DialogRetryDoneJournalFailedTitle : Tr.DialogRetrySucceededTitle
            : Tr.DialogRetryRejectedTitle;
        return new RecoveryRetryNotice(title, result.Message, IsWarning: !(result.Succeeded && !journalWarning),
            CloseWindow: result.Succeeded, Recheck: !result.Succeeded);
    }
}
