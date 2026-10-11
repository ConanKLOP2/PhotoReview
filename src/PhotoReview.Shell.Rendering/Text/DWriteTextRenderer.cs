using System.Globalization;
using System.Runtime.InteropServices;
using PhotoReview.App.Input;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Interop.Graphics;

namespace PhotoReview.Shell.Rendering;

/// <summary>
/// WP-17 (C-11): <see cref="ITextRenderer"/> bằng DirectWrite. Layout độc lập thiết bị (đo bằng DIP, không phụ thuộc DPI
/// hay thế hệ thiết bị D2D) nên mất thiết bị không đụng tới cache; <see cref="D2DDrawContext.DrawText"/> vẽ lease qua
/// <see cref="INativeTextLayout"/>.
/// <para>
/// Quy ước: <see cref="TextTrimming.CharacterEllipsis"/> = MỘT dòng, cắt "…" theo ký tự khi vượt <c>maxWidth</c> (như
/// <c>TextBlock TextTrimming=CharacterEllipsis</c> không wrap). <see cref="TextTrimming.None"/> với <c>maxWidth</c> hữu hạn =
/// ngắt dòng theo từ ở <c>maxWidth</c>; <c>maxWidth</c> vô hạn/NaN = không giới hạn (một dòng, không cắt).
/// </para>
/// Dùng trên luồng UI (cache khoá nội bộ chỉ để Dispose lease từ luồng khác không hỏng đếm ref).
/// </summary>
public sealed class DWriteTextRenderer : ITextRenderer, IDisposable
{
    /// <summary>Chiều rộng thay cho "không giới hạn" (DirectWrite cần số hữu hạn).</summary>
    internal const float Unbounded = 1_000_000f;

    private const int DefaultCacheCapacity = 256;

    private readonly IDWriteFactory _factory;
    private readonly string _locale;
    private readonly TextLayoutCache _cache;
    private readonly Func<TextLayoutKey, TextLayoutEntry> _build;
    private bool _disposed;

    /// <summary>Locale = <see cref="CultureInfo.CurrentUICulture"/> (rỗng -> en-us).</summary>
    public DWriteTextRenderer()
        : this(null, DefaultCacheCapacity)
    {
    }

    /// <param name="localeName">Tên locale DirectWrite (vd. "vi-VN"); null = ngôn ngữ UI hiện tại.</param>
    /// <param name="cacheCapacity">Số layout tối đa trong cache LRU.</param>
    public DWriteTextRenderer(string? localeName, int cacheCapacity)
    {
        _locale = string.IsNullOrWhiteSpace(localeName)
            ? (CultureInfo.CurrentUICulture.Name is { Length: > 0 } ui ? ui : "en-us")
            : localeName;
        _cache = new TextLayoutCache(cacheCapacity);
        _build = Build;
        _factory = DWrite.CreateFactory();
    }

    /// <summary>Số lần CreateLayout trúng cache.</summary>
    internal long CacheHits => _cache.Hits;

    /// <summary>Số lần phải dựng layout mới.</summary>
    internal long CacheMisses => _cache.Misses;

    internal int CachedLayoutCount => _cache.Count;

    /// <summary>Layout đã bị loại khỏi cache nhưng còn lease (chưa trả COM).</summary>
    internal int PendingReleaseCount => _cache.PendingRelease;

    public ITextLayout CreateLayout(string text, TextStyle style, double maxWidth, TextTrimming trimming)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(style);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!(style.FontSizeDip > 0) || double.IsInfinity(style.FontSizeDip))
        {
            throw new ArgumentOutOfRangeException(nameof(style), style.FontSizeDip, "FontSizeDip must be a finite positive number.");
        }

        if (!Enum.IsDefined(trimming))
        {
            throw new ArgumentOutOfRangeException(nameof(trimming), trimming, null);
        }

        string family = string.IsNullOrWhiteSpace(style.FontFamily) ? "Segoe UI" : style.FontFamily;
        var key = new TextLayoutKey(text, family, (float)style.FontSizeDip, style.Bold, NormalizeWidth(maxWidth), trimming);
        TextLayoutEntry entry = _cache.Acquire(key, _build);
        return new DWriteTextLayoutLease(entry, _cache);
    }

    /// <summary>Bỏ cache (layout đang được giữ sống tới khi lease cuối Dispose) và trả factory.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cache.Dispose();
        ComInterop.Release(_factory);
    }

    /// <summary>NaN/vô hạn/quá lớn = không giới hạn; âm = 0.</summary>
    internal static float NormalizeWidth(double maxWidth)
    {
        if (double.IsNaN(maxWidth) || maxWidth >= Unbounded)
        {
            return Unbounded;
        }

        return maxWidth <= 0 ? 0f : (float)maxWidth;
    }

    private unsafe TextLayoutEntry Build(TextLayoutKey key)
    {
        ComInterop.Check(_factory.CreateTextFormat(key.FontFamily, 0, key.Bold ? DWriteFontWeight.Bold : DWriteFontWeight.Regular,
            DWriteFontStyle.Normal, DWriteFontStretch.Normal, key.FontSizeDip, _locale, out nint formatRaw));
        IDWriteTextFormat format = ComInterop.Wrap<IDWriteTextFormat>(formatRaw);
        IDWriteTextLayout? layout = null;
        nint sign = 0;
        try
        {
            ComInterop.Check(_factory.CreateTextLayout(key.Text, (uint)key.Text.Length, format, key.MaxWidth, Unbounded,
                out nint layoutRaw));
            layout = ComInterop.Wrap<IDWriteTextLayout>(layoutRaw);
            if (key.Trimming == TextTrimming.CharacterEllipsis)
            {
                // Một dòng, cắt theo ký tự + dấu "…" (cần inline object: không có sign thì DirectWrite chỉ cắt cụt).
                ComInterop.Check(layout.SetWordWrapping(DWriteWordWrapping.NoWrap));
                sign = DWriteRawCalls.CreateEllipsisTrimmingSign(_factory, layout);
                var trimming = new DWriteTrimming { Granularity = DWriteTrimmingGranularity.Character, Delimiter = 0, DelimiterCount = 0 };
                ComInterop.Check(layout.SetTrimming(&trimming, sign));
            }

            ComInterop.Check(layout.GetMetrics(out DWriteTextMetrics metrics));
            bool trimmed = false;
            if (key.Trimming == TextTrimming.CharacterEllipsis && metrics.LineCount >= 1)
            {
                DWriteLineMetrics line;
                int hr = layout.GetLineMetrics(&line, 1, out _);
                trimmed = hr >= 0 && line.IsTrimmed != 0;
            }

            var entry = new TextLayoutEntry(key, key.Text, layout, new SizeD(metrics.Width, metrics.Height), (int)metrics.LineCount, trimmed);
            layout = null;
            return entry;
        }
        finally
        {
            if (sign != 0)
            {
                Marshal.Release(sign);
            }

            ComInterop.Release(layout);
            ComInterop.Release(format);
        }
    }
}

/// <summary>
/// Một lease trên layout trong cache: <see cref="Dispose"/> trả ref (COM trả khi layout đã bị loại khỏi cache và hết
/// lease). Cùng text/style/width/trimming cho các lease khác nhau nhưng dùng chung một layout DirectWrite.
/// </summary>
internal sealed class DWriteTextLayoutLease : ITextLayout, INativeTextLayout
{
    private TextLayoutEntry? _entry;
    private readonly TextLayoutCache _cache;
    private readonly string _text;
    private readonly SizeD _size;

    internal DWriteTextLayoutLease(TextLayoutEntry entry, TextLayoutCache cache)
    {
        _entry = entry;
        _cache = cache;
        _text = entry.Text;
        _size = entry.Size;
        LineCount = entry.LineCount;
        IsTrimmed = entry.IsTrimmed;
    }

    public SizeD Size => _size;

    public string Text => _text;

    /// <summary>Số dòng sau khi ngắt/cắt.</summary>
    public int LineCount { get; }

    /// <summary>True khi <see cref="TextTrimming.CharacterEllipsis"/> đã phải cắt chữ.</summary>
    public bool IsTrimmed { get; }

    public bool IsDisposed => _entry is null;

    public IDWriteTextLayout NativeLayout =>
        (_entry ?? throw new ObjectDisposedException(nameof(DWriteTextLayoutLease))).Native;

    /// <summary>Layout dùng chung bên dưới (test: hai lease cùng khoá trỏ cùng một entry).</summary>
    internal object? SharedEntry => _entry;

    public void Dispose()
    {
        TextLayoutEntry? entry = Interlocked.Exchange(ref _entry, null);
        if (entry is not null)
        {
            _cache.Release(entry);
        }
    }
}
