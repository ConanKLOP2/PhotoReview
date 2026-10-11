using PhotoReview.App.Input;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Rendering;

namespace PhotoReview.Shell.Win32.Viewing;

/// <summary>Ô nào của compare.</summary>
internal enum CompareSide
{
    Left = 0,
    Right = 1,
}

/// <summary>
/// WP-21: bố cục compare = <c>ComparePanel</c> của <c>MainWindow.xaml</c> (Border Margin 8 phủ cả vùng client, Grid hai cột
/// bằng nhau, mỗi ô là Border Margin 4 BorderThickness 2 chứa Image Stretch=Uniform). Thuần (không GPU): hộp của mỗi ô,
/// hộp nội dung và hình ảnh đặt giữa theo tỉ lệ. Đơn vị DIP, gốc ở góc client.
/// </summary>
internal static class CompareLayout
{
    /// <summary><c>ComparePanel Margin="8"</c>.</summary>
    internal const double PanelMargin = 8;

    /// <summary><c>CompareLeftBorder Margin="4"</c>.</summary>
    internal const double CellMargin = 4;

    /// <summary><c>BorderThickness="2"</c>.</summary>
    internal const double BorderThickness = 2;

    /// <summary>Phần của client mà panel phủ (nền canvas, che ảnh chính).</summary>
    internal static RectD Panel(SizeD client) =>
        new(PanelMargin, PanelMargin, Math.Max(0, client.Width - (2 * PanelMargin)), Math.Max(0, client.Height - (2 * PanelMargin)));

    /// <summary>Hộp ngoài của Border của ô (gồm viền 2 DIP): ô lưới trừ <see cref="CellMargin"/> mỗi cạnh.</summary>
    internal static RectD Border(SizeD client, CompareSide side)
    {
        var panel = Panel(client);
        var column = panel.Width / 2;
        var cellX = panel.X + (side == CompareSide.Right ? column : 0);
        return new RectD(cellX + CellMargin, panel.Y + CellMargin, Math.Max(0, column - (2 * CellMargin)),
            Math.Max(0, panel.Height - (2 * CellMargin)));
    }

    /// <summary>Hộp nội dung (trong viền) mà Image Stretch=Uniform lấp vào.</summary>
    internal static RectD Content(SizeD client, CompareSide side)
    {
        var border = Border(client, side);
        return new RectD(border.X + BorderThickness, border.Y + BorderThickness,
            Math.Max(0, border.Width - (2 * BorderThickness)), Math.Max(0, border.Height - (2 * BorderThickness)));
    }

    /// <summary>
    /// Hình ảnh trong ô: <c>Stretch=Uniform</c> (co/giãn giữ tỉ lệ cho vừa nội dung, căn giữa), bitmap tính theo DIP = pixel
    /// (mọi PixelBuffer của app ở 96 DPI). Rỗng khi ảnh hay nội dung rỗng.
    /// </summary>
    internal static RectD Image(SizeD client, CompareSide side, int pixelWidth, int pixelHeight)
    {
        var content = Content(client, side);
        if (pixelWidth <= 0 || pixelHeight <= 0 || content.Width <= 0 || content.Height <= 0)
        {
            return new RectD(content.X, content.Y, 0, 0);
        }

        var scale = Math.Min(content.Width / pixelWidth, content.Height / pixelHeight);
        var width = pixelWidth * scale;
        var height = pixelHeight * scale;
        return new RectD(content.X + ((content.Width - width) / 2), content.Y + ((content.Height - height) / 2), width, height);
    }

    /// <summary>Ô chứa điểm (kể cả viền: <c>MouseLeftButtonDown</c> của Border), hoặc null.</summary>
    internal static CompareSide? HitTest(SizeD client, PointD point)
    {
        var left = Border(client, CompareSide.Left);
        if (left.Width > 0 && left.Height > 0 && left.Contains(point))
        {
            return CompareSide.Left;
        }

        var right = Border(client, CompareSide.Right);
        if (right.Width > 0 && right.Height > 0 && right.Contains(point))
        {
            return CompareSide.Right;
        }

        return null;
    }
}

/// <summary>Một khung compare: hai ảnh (có thể null khi đang nạp) và ô nào đang được chọn.</summary>
internal readonly record struct CompareFrame(PixelBuffer? Left, PixelBuffer? Right, bool LeftSelected, bool RightSelected);

/// <summary>
/// WP-21: vẽ <c>ComparePanel</c>: nền canvas phủ panel (che mọi thứ bên dưới), viền mỗi ô (LimeGreen khi chọn, #555555 khi không;
/// <c>CompareBorderBrushConverter</c>) rồi ảnh Uniform trong ô. Lớp này đứng TRÊN ảnh chính và lớp ảnh cũ (thứ tự XAML).
/// </summary>
internal static class CompareLayer
{
    internal static readonly ColorF Canvas = new(0x10 / 255f, 0x10 / 255f, 0x10 / 255f, 1f);

    /// <summary><c>Brushes.LimeGreen</c> (#32CD32).</summary>
    internal static readonly ColorF Selected = new(0x32 / 255f, 0xCD / 255f, 0x32 / 255f, 1f);

    /// <summary>#555555.</summary>
    internal static readonly ColorF Unselected = new(0x55 / 255f, 0x55 / 255f, 0x55 / 255f, 1f);

    /// <summary>Số ảnh thật sự được vẽ (0..2).</summary>
    internal static int Draw(IGpuImageCache cache, IDrawContext context, SizeD client, in CompareFrame frame,
        ImageInterpolation interpolation)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(context);
        var panel = CompareLayout.Panel(client);
        if (panel.Width <= 0 || panel.Height <= 0)
        {
            return 0;
        }

        context.FillRectangle(panel, Canvas);
        var drawn = 0;
        drawn += DrawCell(cache, context, client, CompareSide.Left, frame.Left, frame.LeftSelected, interpolation);
        drawn += DrawCell(cache, context, client, CompareSide.Right, frame.Right, frame.RightSelected, interpolation);
        return drawn;
    }

    private static int DrawCell(IGpuImageCache cache, IDrawContext context, SizeD client, CompareSide side, PixelBuffer? pixels,
        bool selected, ImageInterpolation interpolation)
    {
        var border = CompareLayout.Border(client, side);
        if (border.Width <= 0 || border.Height <= 0)
        {
            return 0;
        }

        DrawBorder(context, border, selected ? Selected : Unselected);
        if (pixels is null || pixels.IsDisposed)
        {
            return 0;
        }

        var image = CompareLayout.Image(client, side, pixels.Width, pixels.Height);
        var content = CompareLayout.Content(client, side);
        context.PushClip(content);
        try
        {
            return ImageLayer.TryDraw(cache, context, pixels, image, interpolation) ? 1 : 0;
        }
        finally
        {
            context.PopClip();
        }
    }

    /// <summary>Viền trong hộp (không phình ra ngoài), như Border WPF: trên, dưới, rồi trái/phải giữa hai cạnh.</summary>
    private static void DrawBorder(IDrawContext context, RectD border, ColorF color)
    {
        var thickness = Math.Min(CompareLayout.BorderThickness, Math.Min(border.Width, border.Height) / 2);
        context.FillRectangle(new RectD(border.X, border.Y, border.Width, thickness), color);
        context.FillRectangle(new RectD(border.X, border.Bottom - thickness, border.Width, thickness), color);
        var inner = border.Height - (2 * thickness);
        if (inner > 0)
        {
            context.FillRectangle(new RectD(border.X, border.Y + thickness, thickness, inner), color);
            context.FillRectangle(new RectD(border.Right - thickness, border.Y + thickness, thickness, inner), color);
        }
    }
}
