using PhotoReview.App.ViewModels;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

[Trait("Category", "HotPath")]
public sealed class StatusFormatterTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(-100, "0 B")]
    [InlineData(512, "512 B")]
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
        Assert.Equal("1/10 · 1.5 MB · Đang tải bản rõ", StatusFormatter.LoadingFullRes(0, 10, 1572864));
        Assert.Equal("1/10 · 1.5 MB · photo.jpg", StatusFormatter.Ready(0, 10, 1572864, "photo.jpg"));
        Assert.Equal("1/10 · 1.5 MB · 1920×1080 · photo.jpg", StatusFormatter.WithDimensions(0, 10, 1572864, 1920, 1080, "photo.jpg"));
        Assert.Equal("1/10 · 1.5 MB · Zoom 1.25x", StatusFormatter.Zoom(0, 10, 1572864, 1.25));
        Assert.Equal("1/10 · 1.5 MB · Zoom 1x", StatusFormatter.Zoom(0, 10, 1572864, 1.0));
        Assert.Equal("1/10 | Compare | left.jpg (1 MB) ↔ right.jpg (1 MB) [same hash] | click để chọn",
            StatusFormatter.Compare(0, 10, "left.jpg", " (1 MB)", "right.jpg", " (1 MB)", " [same hash]"));
        Assert.Equal("Lỗi ảnh: photo.jpg — File hỏng", StatusFormatter.ImageError("photo.jpg", "File hỏng"));
    }

    [Fact]
    public void FolderAndActionMessages_MatchExactOriginalStrings()
    {
        Assert.Equal("Đang quét folder ảnh…", StatusFormatter.ScanningFolder());
        Assert.Equal("Không tìm thấy ảnh hỗ trợ trong folder này.", StatusFormatter.NoSupportedImages());
        Assert.Equal("Không mở được folder: Quyền truy cập bị từ chối", StatusFormatter.FolderOpenFailed("Quyền truy cập bị từ chối"));
        Assert.Equal("Không còn ảnh trong thư mục", StatusFormatter.NoImagesRemaining());
        Assert.Equal("Đã xử lý hết ảnh trong folder.", StatusFormatter.AllImagesProcessed());
        Assert.Equal("Đã xóa cache preview.", StatusFormatter.CacheCleared());
        Assert.Equal("Đã hủy: folder đã đổi trong lúc kiểm tra trùng lặp.", StatusFormatter.DuplicateCheckCanceledFolderChanged());
        Assert.Equal("Không có duplicate cùng hash phù hợp.", StatusFormatter.NoDuplicatesFound());
        Assert.Equal("Đã hủy xử lý hàng loạt.", StatusFormatter.BatchCanceled());
        Assert.Equal("Batch hoàn tất: 5 thành công, 0 lỗi.", StatusFormatter.BatchDone(5, 0));
        Assert.Equal("Đã ở folder cuối cùng cùng cấp.", StatusFormatter.SiblingFolderBoundary(1));
        Assert.Equal("Đã ở folder đầu tiên cùng cấp.", StatusFormatter.SiblingFolderBoundary(-1));
        Assert.Equal("Đã thực hiện: Move to Keep", StatusFormatter.ActionCompleted("Move to Keep"));
        Assert.Equal("Không thực hiện được Move to Keep: Operation không hợp lệ.", StatusFormatter.ActionInvalidOperation("Move to Keep"));
        Assert.Equal("Không thực hiện được Move to Keep: Disk full", StatusFormatter.ActionFailed("Move to Keep", "Disk full"));
        Assert.Equal("Không xử lý được file.jpg: Disk full", StatusFormatter.FileProcessingFailed("file.jpg", "Disk full"));
        Assert.Equal("Không có Move nào để hoàn tác.", StatusFormatter.UndoNoMoves());
        Assert.Equal("Không có Move/Delete vừa thực hiện để hoàn tác.", StatusFormatter.UndoNoActions());
        Assert.Equal("Không thể Undo: Destination changed", StatusFormatter.UndoFailed("Destination changed"));
        Assert.Equal("Không thể khôi phục Recycle Bin: file.jpg", StatusFormatter.RecycleRestoreFailed("file.jpg"));
    }
}