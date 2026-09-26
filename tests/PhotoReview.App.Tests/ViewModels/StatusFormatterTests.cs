using PhotoReview.App.ViewModels;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

[Trait("Category", "HotPath")]
public sealed class StatusFormatterTests
{
    [Theory]
    [InlineData(0, "0 byte")]
    [InlineData(-100, "0 byte")]
    [InlineData(512, "512 byte")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(2500000, "2.38 MB")]
    [InlineData(1073741824L, "1 GB")]
    [InlineData(5368709120L, "5 GB")]
    public void FormatFileSize_FormatsAccurately(long bytes, string expected)
    {
        Assert.Equal(expected, StatusFormatter.FormatFileSize(bytes));
    }

    [Fact]
    public void ImageStatusMessages_MatchExactOriginalStrings()
    {
        Assert.Equal("1/10", StatusFormatter.IndexOnly(0, 10));
        Assert.Equal("1/10 · 1.5 MB · Đang tải", StatusFormatter.Loading(0, 10, 1572864));
        Assert.Equal("1/10 · 1.5 MB · Đang tải độ phân giải đầy đủ", StatusFormatter.LoadingFullRes(0, 10, 1572864));
        Assert.Equal("1/10 · 1.5 MB · photo.jpg", StatusFormatter.Ready(0, 10, 1572864, "photo.jpg"));
        Assert.Equal("1/10 · 1.5 MB · 1920×1080 · photo.jpg", StatusFormatter.WithDimensions(0, 10, 1572864, 1920, 1080, "photo.jpg"));
        Assert.Equal("1/10 | So sánh | left.jpg (1 MB) ↔ right.jpg (1 MB) [same hash] | nhấn để chọn",
            StatusFormatter.Compare(0, 10, "left.jpg", " (1 MB)", "right.jpg", " (1 MB)", " [same hash]"));
        Assert.Equal("Lỗi ảnh: photo.jpg — File hỏng", StatusFormatter.ImageError("photo.jpg", "File hỏng"));
    }

    [Fact]
    public void FolderAndActionMessages_MatchExactOriginalStrings()
    {
        Assert.Equal("Không tìm thấy ảnh được hỗ trợ trong thư mục này.", StatusFormatter.NoSupportedImages());
        Assert.Equal("Không mở được thư mục: Quyền truy cập bị từ chối", StatusFormatter.FolderOpenFailed("Quyền truy cập bị từ chối"));
        Assert.Equal("Không còn ảnh trong thư mục", StatusFormatter.NoImagesRemaining());
        Assert.Equal("Đã xử lý hết ảnh trong thư mục.", StatusFormatter.AllImagesProcessed());
        Assert.Equal("Đã xóa cache ảnh xem trước.", StatusFormatter.CacheCleared());
        Assert.Equal("Đã hủy: thư mục đã thay đổi trong lúc kiểm tra trùng lặp.", StatusFormatter.DuplicateCheckCanceledFolderChanged());
        Assert.Equal("Không có bản trùng lặp nào cùng hash phù hợp.", StatusFormatter.NoDuplicatesFound());
        Assert.Equal("Đã hủy xử lý hàng loạt.", StatusFormatter.BatchCanceled());
        Assert.Equal("Xử lý hàng loạt hoàn tất: 5 thành công, 0 lỗi.", StatusFormatter.BatchDone(5, 0));
        Assert.Equal("Đã ở thư mục cuối cùng cùng cấp.", StatusFormatter.SiblingFolderBoundary(1));
        Assert.Equal("Đã ở thư mục đầu tiên cùng cấp.", StatusFormatter.SiblingFolderBoundary(-1));
        Assert.Equal("Đã thực hiện: Move to Keep", StatusFormatter.ActionCompleted("Move to Keep"));
        Assert.Equal("Không thực hiện được Move to Keep: thao tác không hợp lệ.", StatusFormatter.ActionInvalidOperation("Move to Keep"));
        Assert.Equal("Không thực hiện được Move to Keep: Disk full", StatusFormatter.ActionFailed("Move to Keep", "Disk full"));
    }
}