using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Session;
using PhotoReview.Imaging;

namespace PhotoReview.App.Coordinators;

/// <summary>
/// Điều khiển điều hướng thư mục anh em (sibling folders) chứa ảnh.
/// Kiểm tra generation folder chống race condition khi folder bị đổi.
/// </summary>
public sealed class SiblingFolderNavigator
{
    private readonly GenerationClock _clock;
    private readonly ReviewCatalog _catalog;
    private readonly IFileSystem _fileSystem;
    private readonly ISiblingNavigatorSink _sink;
    private readonly Func<SessionState?> _getCurrentSession;

    public SiblingFolderNavigator(
        GenerationClock clock,
        ReviewCatalog catalog,
        IFileSystem fileSystem,
        ISiblingNavigatorSink sink,
        Func<SessionState?> getCurrentSession)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _getCurrentSession = getCurrentSession ?? throw new ArgumentNullException(nameof(getCurrentSession));
    }

    public async Task NavigateSiblingFolderAsync(int direction)
    {
        var currentSession = _getCurrentSession();
        var folder = currentSession?.Folder ?? (_catalog.Current != null ? Path.GetDirectoryName(_catalog.Current.Path) : null);
        if (string.IsNullOrWhiteSpace(folder)) return;

        var currentFolder = Path.GetFullPath(folder);
        var folderGeneration = _clock.CurrentFolder;

        var targetFolder = await Task.Run(() => FindNextImageFolder(currentFolder, direction));

        // Kiểm tra generation để tránh race condition khi người dùng đã chuyển folder khác giữa chừng
        if (!_clock.IsFolderCurrent(folderGeneration)) return;

        if (targetFolder is null)
        {
            _sink.SetStatusText(StatusFormatter.SiblingFolderBoundary(direction));
            return;
        }

        await _sink.OpenFolderAsync(targetFolder);
    }

    private string? FindNextImageFolder(string currentFolder, int direction)
    {
        var folders = SiblingFolderService.GetSorted(currentFolder);
        var index = IndexOfFolder(folders, currentFolder);
        return index < 0 ? null : FindImageFolder(folders, index, direction, CancellationToken.None);
    }

    /// <summary>
    /// The sibling image folders that <see cref="NavigateSiblingFolderAsync"/> would open from
    /// <paramref name="currentFolder"/> with direction -1 (<see cref="SiblingImageFolders.Previous"/>) and +1
    /// (<see cref="SiblingImageFolders.Next"/>): the same search, so the info overlay never disagrees with the key.
    /// Blocking directory I/O -- call it off the UI thread. Cost: one listing of the parent folder, then per direction the
    /// file listings of the siblings up to the first one that contains a supported image (metadata only; the listing
    /// stops at the first image file, no file content is read).
    /// </summary>
    public SiblingImageFolders FindSiblingImageFolders(string currentFolder, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentFolder);
        var fullFolder = Path.GetFullPath(currentFolder);
        var folders = SiblingFolderService.GetSorted(fullFolder);
        cancellationToken.ThrowIfCancellationRequested();
        var index = IndexOfFolder(folders, fullFolder);
        if (index < 0) return default;
        var previous = FindImageFolder(folders, index, -1, cancellationToken);
        var next = FindImageFolder(folders, index, 1, cancellationToken);
        return new SiblingImageFolders(previous, next);
    }

    private static int IndexOfFolder(IReadOnlyList<string> folders, string currentFolder) =>
        folders.ToList().FindIndex(path => string.Equals(Path.GetFullPath(path), Path.GetFullPath(currentFolder), StringComparison.OrdinalIgnoreCase));

    private string? FindImageFolder(IReadOnlyList<string> folders, int index, int direction, CancellationToken cancellationToken)
    {
        for (var i = index + direction; i >= 0 && i < folders.Count; i += direction)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var candidates = _fileSystem.EnumerateFiles(folders[i], "*")
                    .Where(ImageFileTypes.IsSupported);
                if (candidates.Any()) return folders[i];
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }
}

/// <summary>The sibling image folders PageUp (<see cref="Previous"/>) and PageDown (<see cref="Next"/>) would open; null = none.</summary>
public readonly record struct SiblingImageFolders(string? Previous, string? Next);
