using System.Runtime.InteropServices;

namespace PhotoReview.Imaging.Pixels;

/// <summary>
/// Khối bộ nhớ native của một <see cref="PixelBuffer"/> (WP-02, C-01): <see cref="NativeMemory.AlignedAlloc"/> căn
/// <see cref="Alignment"/> byte + <see cref="GC.AddMemoryPressure(long)"/>; <see cref="SafeHandle"/> bảo đảm giải phóng
/// đúng một lần, ở Dispose hoặc ở finalizer (critical) khi chủ sở hữu quên Dispose.
/// Không phải kiểu hợp đồng: chỉ <see cref="PixelBuffer"/> và test dùng.
/// </summary>
internal sealed class NativePixelMemory : SafeHandle
{
    /// <summary>Căn lề địa chỉ: 64 byte (một dòng cache; đủ cho AVX-512 và yêu cầu của WIC/D2D).</summary>
    internal const int Alignment = 64;

    private static long _liveCount;

    /// <summary>Khối chưa cấp phát (handle 0, không tốn bộ nhớ, giải phóng là no-op). Chỉ để test dựng PixelBuffer giả kích thước lớn.</summary>
    internal NativePixelMemory()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    /// <summary>
    /// Số khối đang sống (cấp phát thành công trừ đã giải phóng) trong tiến trình. Rẻ (Interlocked), luôn bật để test
    /// rò rỉ/finalizer chạy được ở Release (thẻ WP-02 ghi "Debug-only", xem NOWPF-WP02-PIXELBUFFER).
    /// </summary>
    internal static long LiveCount => Interlocked.Read(ref _liveCount);

    /// <summary>Số byte đã xin (0 cho khối giả).</summary>
    internal long ByteCount { get; private set; }

    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <summary>Cấp phát <paramref name="byteCount"/> byte (&gt; 0); ném <see cref="OutOfMemoryException"/> khi hệ thống từ chối (<see cref="InsufficientMemoryException"/> - lớp con - khi kích thước vượt không gian địa chỉ của tiến trình 32-bit).</summary>
    internal static unsafe NativePixelMemory Allocate(long byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCount);
        if ((ulong)byteCount > nuint.MaxValue) throw new InsufficientMemoryException($"Cannot address {byteCount} bytes in this process.");

        var memory = new NativePixelMemory();
        // AlignedAlloc ném OutOfMemoryException khi thất bại; SetHandle ngay sau đó (không có điểm ném ở giữa) nên không rò.
        var address = NativeMemory.AlignedAlloc((nuint)byteCount, Alignment);
        memory.SetHandle((nint)address);
        memory.ByteCount = byteCount;
        GC.AddMemoryPressure(byteCount);
        Interlocked.Increment(ref _liveCount);
        return memory;
    }

    protected override unsafe bool ReleaseHandle()
    {
        NativeMemory.AlignedFree((void*)handle);
        GC.RemoveMemoryPressure(ByteCount);
        Interlocked.Decrement(ref _liveCount);
        return true;
    }
}
