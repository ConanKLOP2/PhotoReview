using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoReview.App.Coordinators;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;

namespace PhotoReview.App.ViewModels;

/// <summary>
/// State of the on-image info overlays: the bottom-left file block (<see cref="IsFileInfoVisible"/>) and the
/// bottom-right folder block (<see cref="FolderInfoText"/>: current folder plus the sibling image folders that
/// PreviousFolder/NextFolder -- PageUp/PageDown by default -- would open).
/// </summary>
/// <remarks>
/// <para>Siblings are found once per folder open by the caller's finder (<see cref="SiblingFolderNavigator.FindSiblingImageFolders"/>,
/// the same search the keys use), on the thread pool: never on the UI thread, never blocking the folder open or photo
/// switching. A neutral placeholder is shown meanwhile. A newer <see cref="SetFolder"/> cancels the search and a result
/// that still arrives for a folder the user already left is dropped (request counter, not the folder generation, which
/// file actions also advance). While the folder block is hidden nothing is searched at all (no disk I/O).</para>
/// <para>Known limitation (documented, deliberate): the siblings are NOT searched again after file actions. A move or
/// recycle normally changes only the current folder (relative destinations are its sub-folders), and the siblings'
/// emptiness only changes if the user edits them outside the app; re-listing the parent and sibling folders after every
/// action would cost disk reads on the review hot path. The display is refreshed on the next folder open, and the keys
/// themselves always search at press time, so an outdated display never makes PageUp/PageDown open a wrong folder.</para>
/// <para>ADR 0005: UI-affine (no <c>ConfigureAwait(false)</c>); the continuation after the background search runs on the
/// UI thread.</para>
/// </remarks>
public sealed class InfoOverlayViewModel : ObservableObject
{
    // Space between the parts of the folder line (prev · current · next); layout, not text.
    private const string PartSeparator = "     ";

    private readonly Func<AppSettings> _settings;
    private readonly Func<string, CancellationToken, SiblingImageFolders> _findSiblings;
    private string? _folder;          // folder currently open (null = none)
    private string? _siblingsFolder;  // folder _siblings belong to (null = not computed)
    private SiblingImageFolders _siblings;
    private string? _pendingFolder;   // folder a search is running for
    private CancellationTokenSource? _cts;
    private long _request;
    private string _folderInfoText = string.Empty;

    /// <param name="settings">Current settings (read on every refresh, never cached).</param>
    /// <param name="findSiblings">Blocking sibling search; always invoked on the thread pool.</param>
    public InfoOverlayViewModel(Func<AppSettings> settings, Func<string, CancellationToken, SiblingImageFolders> findSiblings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _findSiblings = findSiblings ?? throw new ArgumentNullException(nameof(findSiblings));
    }

    /// <summary>Bottom-left file block (position/count, size, name, dimensions) is shown.</summary>
    public bool IsFileInfoVisible => _settings() is { ShowInfoOverlay: true, ShowFileInfo: true };

    /// <summary>Bottom-right folder block is shown (enabled in settings and a folder is open).</summary>
    public bool IsFolderInfoVisible => IsFolderInfoEnabled && _folder is not null;

    private bool IsFolderInfoEnabled => _settings() is { ShowInfoOverlay: true, ShowFolderInfo: true };

    /// <summary>Folder line; empty while hidden or when no folder is open.</summary>
    public string FolderInfoText
    {
        get => _folderInfoText;
        private set => SetProperty(ref _folderInfoText, value);
    }

    /// <summary>The latest background sibling search (completed when idle). Test seam: await it instead of a delay.</summary>
    internal Task PendingSiblings { get; private set; } = Task.CompletedTask;

    /// <summary>A folder was opened (or null = none): drops the previous folder's siblings and searches the new ones if shown.</summary>
    public void SetFolder(string? folder)
    {
        CancelPending();
        _folder = string.IsNullOrWhiteSpace(folder) ? null : folder;
        _siblingsFolder = null;
        _siblings = default;
        Refresh();
    }

    /// <summary>Settings changed (visibility switches or shortcut keys) or the language switched: re-render from state.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(IsFileInfoVisible));
        OnPropertyChanged(nameof(IsFolderInfoVisible));
        if (_folder is not { } folder || !IsFolderInfoEnabled)
        {
            CancelPending(); // hidden: never touch the disk for it
            FolderInfoText = string.Empty;
            return;
        }
        if (string.Equals(_siblingsFolder, folder, StringComparison.Ordinal))
        {
            FolderInfoText = Format(folder, _siblings);
            return;
        }
        // Publish the placeholder BEFORE starting the search: without a SynchronizationContext the search continuation
        // can finish on the pool while this method is still running, and a placeholder written afterwards would
        // overwrite the finished result.
        FolderInfoText = Tr.MainFolderInfoCurrent(DisplayName(folder)) + PartSeparator + Tr.MainFolderInfoPending;
        if (!string.Equals(_pendingFolder, folder, StringComparison.Ordinal)) Start(folder);
    }

    private void Start(string folder)
    {
        var cts = new CancellationTokenSource();
        _cts = cts;
        _pendingFolder = folder;
        var request = ++_request;
        PendingSiblings = SearchAsync(folder, request, cts.Token);
    }

    private async Task SearchAsync(string folder, long request, CancellationToken cancellationToken)
    {
        SiblingImageFolders result;
        try
        {
            result = await Task.Run(() => _findSiblings(folder, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result = default; // unreadable parent: show the folder without siblings rather than a stuck placeholder
        }
        // Back on the UI thread (ADR 0005). A newer SetFolder/hide bumped the request: this result is for a folder the
        // user already left (or a search that was cancelled but finished anyway) -- drop it.
        if (request != _request) return;
        _cts?.Dispose();
        _cts = null;
        _pendingFolder = null;
        _siblings = result;
        _siblingsFolder = folder;
        Refresh();
    }

    private void CancelPending()
    {
        _request++;
        _pendingFolder = null;
        if (_cts is null) return;
        _cts.Cancel();
        _cts.Dispose();
        _cts = null;
    }

    private string Format(string folder, SiblingImageFolders siblings)
    {
        var shortcuts = _settings().Shortcuts;
        var parts = new List<string>(3);
        if (siblings.Previous is { } previous && KeyName(shortcuts?.PreviousFolder) is { } previousKey)
            parts.Add(Tr.MainFolderInfoPrevious(previousKey, DisplayName(previous)));
        parts.Add(Tr.MainFolderInfoCurrent(DisplayName(folder)));
        if (siblings.Next is { } next && KeyName(shortcuts?.NextFolder) is { } nextKey)
            parts.Add(Tr.MainFolderInfoNext(nextKey, DisplayName(next)));
        return string.Join(PartSeparator, parts);
    }

    private static string? KeyName(string? shortcut) => string.IsNullOrWhiteSpace(shortcut) ? null : shortcut.Trim();

    private static string DisplayName(string folder)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(folder);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? folder : name; // a drive root has no name
    }
}
