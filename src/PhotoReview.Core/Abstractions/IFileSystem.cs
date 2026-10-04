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

    /// <summary>
    /// Sao chép KHÔNG ghi đè, với ngữ nghĩa "tạo mới": trả false (không đụng tới gì) khi <paramref name="destination"/>
    /// đã tồn tại tại thời điểm tạo; trả true khi lần gọi này đã tạo ra đích; mọi lỗi khác là ngoại lệ và có thể để lại
    /// đích dở dang DO LẦN GỌI NÀY tạo ra. Nhờ vậy caller chứng minh được một file đích là của mình (không xóa nhầm file lạ
    /// xuất hiện giữa lúc kiểm tra và lúc copy). Mặc định (wrapper/fake): kiểm tra tồn tại rồi <see cref="Copy"/> — không
    /// nguyên tử; <see cref="PhotoReview.Core.IO.PhysicalFileSystem"/> ghi đè bằng File.Copy nguyên tử (fail-if-exists).
    /// </summary>
    bool TryCopyNew(string source, string destination)
    {
        if (FileExists(destination)) return false;
        Copy(source, destination);
        return true;
    }

    /// <summary>Xóa vĩnh viễn tệp tin.</summary>
    void Delete(string path);

    /// <summary>
    /// Mở luồng đọc tệp tin với cờ chia sẻ không khóa (ReadWrite | Delete) và SequentialScan.
    /// </summary>
    Stream OpenReadShared(string path, int bufferSize = 65536);

    /// <summary>
    /// Mở luồng ghi nối tiếp (Append mode); <paramref name="durable"/> = true thêm WriteThrough (bền vững), false = không
    /// WriteThrough (dữ liệu chỉ tới cache của OS sau Flush thường; ADR 0007 chế độ Nhanh).
    /// </summary>
    Stream OpenAppend(string path, bool durable);

    /// <summary>
    /// Ghi nội dung văn bản ra tệp tin nguyên tử (ghi ra file tạm trước rồi đổi tên đè).
    /// </summary>
    /// <remarks>
    /// durable=true: WriteThrough + Flush(true) (an toàn khi mất điện; Settings).
    /// durable=false: vẫn nguyên tử nhưng không fsync (Session, ADR 0007 mục 2).
    /// </remarks>
    void WriteAllTextAtomic(string path, string text, bool durable = true);

    /// <summary>Đọc toàn bộ nội dung văn bản trong tệp tin.</summary>
    string ReadAllText(string path);

    /// <summary>Liệt kê các tệp tin trong thư mục khớp với mẫu tìm kiếm.</summary>
    IEnumerable<string> EnumerateFiles(string directory, string pattern = "*");

    /// <summary>
    /// Liệt kê nhanh cho lần mở folder (AR16): các tệp thỏa <paramref name="include"/> kèm stat, KHÔNG dò khả
    /// năng đọc từng file. Lỗi giữa chừng khi liệt kê (sau mục đầu tiên) được báo qua <paramref name="onSkipped"/>
    /// (<see cref="SkippedKind.ListingInterrupted"/>) và giữ phần đã đọc; lỗi trước mục đầu tiên ném ra (ADR 0007
    /// mục 3). Mặc định (fake trong bộ nhớ) = EnumerateFiles + GetFileStat cho từng file, rồi lọc.
    /// </summary>
    IEnumerable<(string Path, FileStat? Stat)> EnumerateFilesWithStat(
        string directory, Func<string, bool> include, Action<SkippedEntry> onSkipped) =>
        EnumerateFiles(directory, "*").Select(path => (path, GetFileStat(path))).Where(f => include(f.path));

    /// <summary>
    /// Dò một tệp có mở đọc được không (một lần open/close, không đọc byte). Trả false và lý do kỹ thuật
    /// trong <paramref name="failure"/> khi tệp bị khóa/không quyền/lỗi I/O (ADR 0007 mục 3). Mặc định true
    /// (fake trong bộ nhớ); <see cref="PhotoReview.Core.IO.PhysicalFileSystem"/> ghi đè.
    /// </summary>
    bool TryProbeReadable(string path, out string? failure)
    {
        failure = null;
        return true;
    }

    /// <summary>
    /// Như <see cref="EnumerateFilesWithStat(string, Func{string, bool}, Action{SkippedEntry})"/> nhưng chỉ trả
    /// các tệp đọc được: tệp mà <see cref="TryProbeReadable"/> báo lỗi được BỎ QUA và báo qua
    /// <paramref name="onSkipped"/> thay vì làm hỏng cả lần quét (ADR 0007 mục 3). Lần mở folder không còn gọi
    /// hàm này (AR16: probe chạy nền sau frame đầu); giữ cho các caller/test khác.
    /// </summary>
    IEnumerable<(string Path, FileStat? Stat)> EnumerateReadableFilesWithStat(
        string directory, Func<string, bool> include, Action<SkippedEntry> onSkipped)
    {
        ArgumentNullException.ThrowIfNull(onSkipped);
        return EnumerateFilesWithStat(directory, include, onSkipped).Where(f =>
        {
            if (TryProbeReadable(f.Path, out var failure)) return true;
            onSkipped(new SkippedEntry(f.Path, failure ?? string.Empty));
            return false;
        });
    }

    /// <summary>Liệt kê các thư mục con trong thư mục chỉ định.</summary>
    IEnumerable<string> EnumerateDirectories(string directory);

    /// <summary>Tạo thư mục (kể cả các thư mục cha nếu chưa có).</summary>
    void CreateDirectory(string path);

    /// <summary>
    /// SEC-01: trả về đường dẫn tuyệt đối của <paramref name="path"/> SAU KHI phân giải mọi reparse point
    /// (symlink/junction) đã tồn tại trong các đoạn của đường dẫn — kể cả đoạn cuối cùng (file/thư mục đích).
    /// <see cref="PhotoReview.Core.FileActions.ActionDestinationPolicy.Validate(string?)"/> chỉ kiểm tra dạng chuỗi (không có "..", không rooted) và
    /// KHÔNG đủ để đảm bảo containment: một thư mục con là junction trỏ ra ngoài thư mục ảnh khiến Move/Copy
    /// thật sự ghi ra ngoài dù chuỗi đích trông như nằm trong. Đoạn đường dẫn chưa tồn tại trên đĩa được giữ
    /// nguyên (không có gì để phân giải — một junction "chưa có" không thể đưa đường dẫn ra ngoài). Mặc định
    /// (fake trong bộ nhớ, không có reparse point thật) trả về <see cref="Path.GetFullPath(string)"/> không đổi;
    /// <see cref="PhotoReview.Core.IO.PhysicalFileSystem"/> ghi đè bằng cách đi qua từng đoạn thư mục đã tồn tại
    /// và theo <see cref="FileSystemInfo.ResolveLinkTarget"/> khi đoạn đó là reparse point.
    /// </summary>
    string ResolveRealPath(string path) => Path.GetFullPath(path);
}
