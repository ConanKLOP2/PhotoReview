using System.IO;
using System.Text.RegularExpressions;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Core.FileActions;

/// <summary>
/// Cung cấp thuật toán tìm kiếm và lọc các tệp trùng lặp dựa trên kích thước và hash nội dung.
/// </summary>
public static class DuplicateFinder
{
    private static readonly Regex NumberedPattern = new(@" \(\d+\)$", RegexOptions.Compiled);

    /// <summary>
    /// Tìm kiếm danh sách các tệp trùng lặp cần xóa bỏ.
    /// </summary>
    /// <param name="files">Danh sách đường dẫn các tệp ứng viên.</param>
    /// <param name="removeNumbered">
    /// Nếu true: chọn xóa các tệp có hậu tố đánh số bản sao dạng " (1)", " (2)".
    /// Nếu false: chọn xóa các tệp gốc không có hậu tố đánh số.
    /// </param>
    /// <param name="hash">Hàm bất đồng bộ tính hash nội dung tệp.</param>
    /// <param name="fileSystem">Tùy chọn trừu tượng hóa hệ thống tệp tin (dùng cho testing).</param>
    /// <param name="cancellationToken">Token thông báo hủy bỏ thao tác.</param>
    /// <returns>Danh sách các đường dẫn tệp trùng lặp phù hợp điều kiện lọc.</returns>
    public static async Task<List<string>> FindAsync(
        IReadOnlyList<string> files,
        bool removeNumbered,
        Func<string, CancellationToken, Task<string>> hash,
        IFileSystem? fileSystem = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(hash);

        cancellationToken.ThrowIfCancellationRequested();

        if (files.Count == 0)
        {
            return [];
        }

        // Bước 1: Nhóm theo kích thước (chỉ giữ các nhóm có từ 2 tệp cùng kích thước trở lên)
        var sizeGroups = files
            .Select(path =>
            {
                try
                {
                    var size = fileSystem is not null
                        ? (fileSystem.GetFileStat(path)?.Length ?? -1L)
                        : (File.Exists(path) ? new FileInfo(path).Length : -1L);
                    return (Path: path, Size: size);
                }
                catch
                {
                    return (Path: path, Size: -1L);
                }
            })
            .Where(item => item.Size >= 0)
            .GroupBy(item => item.Size)
            .Where(group => group.Count() > 1)
            .Select(group => group.Select(item => item.Path).ToList())
            .ToList();

        // Bước 2: Với các tệp cùng kích thước, tính hash và nhóm theo hash
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in sizeGroups.SelectMany(group => group))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var hashValue = await hash(path, cancellationToken).ConfigureAwait(false);
                if (!groups.TryGetValue(hashValue, out var group))
                {
                    groups[hashValue] = group = [];
                }
                group.Add(path);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException)
            {
                // Bỏ qua nếu tệp bị lỗi I/O hoặc biến mất giữa chừng
            }
            catch (UnauthorizedAccessException)
            {
                // Bỏ qua nếu không có quyền truy cập
            }
        }

        // Bước 3: Lọc tệp theo quy tắc tên tệp (bản sao có số vs bản gốc)
        var remove = new List<string>();
        foreach (var group in groups.Values.Where(group => group.Count > 1))
        {
            var matching = group.Where(path =>
                NumberedPattern.IsMatch(Path.GetFileNameWithoutExtension(path)) == removeNumbered)
                .ToList();

            // Never return every member of a content group. This can happen when a
            // folder contains only numbered copies (or only originals), and the
            // selected naming rule otherwise matches the complete group. Keep the
            // first item in input order as a deterministic survivor.
            if (matching.Count == group.Count)
            {
                matching.RemoveAt(0);
            }

            remove.AddRange(matching);
        }

        return remove;
    }
}
