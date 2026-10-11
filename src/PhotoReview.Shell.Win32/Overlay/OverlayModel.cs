using PhotoReview.App.ViewModels;

namespace PhotoReview.Shell.Win32.Overlay;

/// <summary>
/// WP-23: ảnh chụp bất biến những gì overlay cần từ <c>MainViewModel</c>/<c>InfoOverlayViewModel</c>/<c>CompareViewModel</c>
/// (thay các <c>{Binding ...}</c> của <c>MainWindow.xaml</c>). Panel không biết view model: test dựng model trực tiếp.
/// </summary>
internal sealed record OverlayModel
{
    /// <summary><c>MainViewModel.HasImages</c>: đã mở thư mục có ảnh (ép toolbar/overlay hiện khi false).</summary>
    public bool HasImages { get; init; }

    /// <summary><c>StatusNeedsAttention</c>: đang có thông điệp người dùng phải đọc (giữ overlay hiện).</summary>
    public bool StatusNeedsAttention { get; init; }

    public bool CompareVisible { get; init; }

    /// <summary><c>IsStatusPanelVisible</c>.</summary>
    public bool StatusPanelVisible { get; init; }

    /// <summary><c>InfoOverlay.IsFileInfoVisible</c> (dòng trạng thái).</summary>
    public bool FileInfoVisible { get; init; }

    public string StatusText { get; init; } = string.Empty;

    /// <summary><c>InfoOverlay.FontSize</c>.</summary>
    public double FontSize { get; init; } = 12;

    /// <summary><c>InfoOverlay.ExifFontSize</c> (= FontSize - 1).</summary>
    public double ExifFontSize { get; init; } = 11;

    public bool ExifVisible { get; init; }

    public string ExifText { get; init; } = string.Empty;

    public bool HasSkippedEntries { get; init; }

    public string SkippedWarningText { get; init; } = string.Empty;

    public bool FolderInfoVisible { get; init; }

    public string FolderInfoText { get; init; } = string.Empty;

    public bool ZoomIndicatorVisible { get; init; }

    public string ZoomIndicatorText { get; init; } = string.Empty;

    public bool CapturePairVisible { get; init; }

    public string CapturePairText { get; init; } = string.Empty;

    public string CapturePairToolTip { get; init; } = string.Empty;

    /// <summary>Đọc mọi thuộc tính overlay từ view model (UI thread).</summary>
    public static OverlayModel From(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InfoOverlayViewModel info = viewModel.InfoOverlay;
        bool hasPair = viewModel.CurrentHasCapturePair;
        return new OverlayModel
        {
            HasImages = viewModel.HasImages,
            StatusNeedsAttention = viewModel.StatusNeedsAttention,
            CompareVisible = viewModel.Compare.IsVisible,
            StatusPanelVisible = viewModel.IsStatusPanelVisible,
            FileInfoVisible = info.IsFileInfoVisible,
            StatusText = viewModel.StatusText ?? string.Empty,
            FontSize = info.FontSize,
            ExifFontSize = info.ExifFontSize,
            ExifVisible = viewModel.IsExifLineVisible,
            ExifText = viewModel.ExifText ?? string.Empty,
            HasSkippedEntries = viewModel.HasSkippedEntries,
            SkippedWarningText = viewModel.SkippedWarningText ?? string.Empty,
            FolderInfoVisible = info.IsFolderInfoVisible,
            FolderInfoText = info.FolderInfoText ?? string.Empty,
            ZoomIndicatorVisible = info.IsZoomIndicatorVisible,
            ZoomIndicatorText = info.ZoomIndicatorText ?? string.Empty,
            CapturePairVisible = hasPair,
            CapturePairText = hasPair ? viewModel.CapturePairBadge : string.Empty,
            CapturePairToolTip = hasPair ? viewModel.CapturePairBadgeToolTip : string.Empty,
        };
    }
}
