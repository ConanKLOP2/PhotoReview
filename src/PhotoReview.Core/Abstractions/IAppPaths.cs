namespace PhotoReview.Core.Abstractions;

/// <summary>
/// Cung cấp các đường dẫn file và thư mục chuẩn của ứng dụng PhotoReview.
/// </summary>
public interface IAppPaths
{
    /// <summary>File cấu hình config.json.</summary>
    string ConfigFile { get; }

    /// <summary>File nhật ký thao tác operations.jsonl.</summary>
    string JournalFile { get; }

    /// <summary>Thư mục lưu session (mỗi folder = 1 file JSON).</summary>
    string SessionsDir { get; }

    /// <summary>File log ứng dụng app.log.</summary>
    string LogFile { get; }

    /// <summary>Thư mục cache preview PNG trên đĩa.</summary>
    string PreviewCacheDir { get; }

    /// <summary>Thư mục cache thumbnail PNG trên đĩa.</summary>
    string ThumbnailCacheDir { get; }

    /// <summary>File lưu vị trí và kích thước cửa sổ window-placement.json.</summary>
    string WindowPlacementFile { get; }
}
