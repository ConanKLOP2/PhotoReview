namespace PhotoReview.Core.Abstractions;

/// <summary>
/// Chứa thông tin kích thước và thời điểm ghi cuối cùng của file (UTC).
/// </summary>
public sealed record FileStat(long Length, DateTime LastWriteUtc);

/// <summary>
/// Trừu tượng hóa các thao tác hệ thống tệp tin cần thiết trong PhotoReview.
/// </summary>
public interface IFileSystem
{
    /// <summary>Kiểm tra xem tệp tin có tồn tại hay không.</summary>
    bool FileExists(string path);

    /// <summary>Kiểm tra xem thư mục có tồn tại hay không.</summary>
    bool DirectoryExists(string path);

    /// <summary>Lấy thông tin kích thước và thời điểm sửa đổi của file (trả về null nếu file không tồn tại).</summary>
    FileStat? GetFileStat(string path);

    /// <summary>Di chuyển hoặc đổi tên tệp tin.</summary>
    void Move(string source, string destination);

    /// <summary>Sao chép tệp tin.</summary>
    void Copy(string source, string destination);

    /// <summary>Xóa vĩnh viễn tệp tin.</summary>
    void Delete(string path);

    /// <summary>
    /// Mở luồng đọc tệp tin với cờ chia sẻ không khóa (ReadWrite | Delete) và SequentialScan.
    /// </summary>
    Stream OpenReadShared(string path, int bufferSize = 65536);

    /// <summary>
    /// Mở luồng ghi nối tiếp bền vững (Append mode với WriteThrough).
    /// </summary>
    Stream OpenAppendDurable(string path);

    /// <summary>
    /// Ghi nội dung văn bản ra tệp tin nguyên tử (ghi ra file tạm trước rồi đổi tên đè).
    /// </summary>
    void WriteAllTextAtomic(string path, string text);

    /// <summary>Đọc toàn bộ nội dung văn bản trong tệp tin.</summary>
    string ReadAllText(string path);

    /// <summary>Đọc từng dòng văn bản trong tệp tin dưới dạng lười (lazy enumeration).</summary>
    IEnumerable<string> ReadLines(string path);

    /// <summary>Liệt kê các tệp tin trong thư mục khớp với mẫu tìm kiếm.</summary>
    IEnumerable<string> EnumerateFiles(string directory, string pattern = "*");

    /// <summary>Liệt kê các thư mục con trong thư mục chỉ định.</summary>
    IEnumerable<string> EnumerateDirectories(string directory);

    /// <summary>Tạo thư mục (kể cả các thư mục cha nếu chưa có).</summary>
    void CreateDirectory(string path);
}
