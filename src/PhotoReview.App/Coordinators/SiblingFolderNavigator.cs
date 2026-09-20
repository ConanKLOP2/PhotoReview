using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        var targetFolder = await Task.Run(() => FindNextImageFolder(currentFolder, direction)).ConfigureAwait(false);

        // Kiểm tra generation để tránh race condition khi người dùng đã chuyển folder khác giữa chừng
        if (!_clock.IsFolderCurrent(folderGeneration)) return;

        if (targetFolder is null)
        {
            _sink.SetStatusText(StatusFormatter.SiblingFolderBoundary(direction));
            return;
        }

        await _sink.OpenFolderAsync(targetFolder).ConfigureAwait(false);
    }

    private string? FindNextImageFolder(string currentFolder, int direction)
    {
        var folders = SiblingFolderService.GetSorted(currentFolder);
        var index = folders.ToList().FindIndex(path => string.Equals(Path.GetFullPath(path), Path.GetFullPath(currentFolder), StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;

        for (var i = index + direction; i >= 0 && i < folders.Count; i += direction)
        {
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
