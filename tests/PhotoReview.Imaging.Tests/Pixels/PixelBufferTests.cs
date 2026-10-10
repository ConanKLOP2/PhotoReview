using System.Runtime.CompilerServices;
using PhotoReview.Imaging.Pixels;

namespace PhotoReview.Imaging.Tests.Pixels;

/// <summary>WP-02 / C-01: cấp phát, hình học, truy cập dòng/span, Dispose và vòng đời bộ nhớ native của <see cref="PixelBuffer"/>.</summary>
public sealed class PixelBufferTests
{
    // Dựng buffer "giả" (khối native chưa cấp phát) với kích thước tuỳ ý: thử nhánh > 2 GB của TryGetSpan mà không cấp phát thật.
    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern PixelBuffer CreateUnallocated(int width, int height, PixelLayout layout, NativePixelMemory memory);

    [Theory]
    [InlineData(1, 1, PixelLayout.Bgr32)]
    [InlineData(1, 9, PixelLayout.Pbgra32)]
    [InlineData(9, 1, PixelLayout.Bgr32)]
    [InlineData(37, 23, PixelLayout.Pbgra32)]
    public void Allocate_ReportsGeometryAndLayout(int width, int height, PixelLayout layout)
    {
        using var buffer = PixelBuffer.Allocate(width, height, layout);

        Assert.Equal(width, buffer.Width);
        Assert.Equal(height, buffer.Height);
        Assert.Equal(layout, buffer.Layout);
        Assert.Equal(width * 4, buffer.Stride);
        Assert.Equal((long)width * 4 * height, buffer.ByteCount);
        Assert.False(buffer.IsDisposed);
        Assert.NotEqual(0, buffer.Address);
        Assert.Equal(0, buffer.Address % NativePixelMemory.Alignment);
    }

    [Fact]
    public void Allocate_IdsAreUniqueAndIncreasing()
    {
        using var first = PixelBuffer.Allocate(2, 2, PixelLayout.Bgr32);
        using var second = PixelBuffer.Allocate(2, 2, PixelLayout.Bgr32);
        using var third = PixelBuffer.Allocate(2, 2, PixelLayout.Pbgra32);

        Assert.True(first.Id > 0);
        Assert.True(second.Id > first.Id);
        Assert.True(third.Id > second.Id);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 5)]
    [InlineData(5, -1)]
    [InlineData(int.MinValue, 1)]
    [InlineData(int.MaxValue / 4 + 1, 1)] // Stride = Width * 4 phải vừa int
    [InlineData(int.MaxValue, 1)]
    public void Allocate_RejectsInvalidSize(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelBuffer.Allocate(width, height, PixelLayout.Bgr32));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(255)]
    public void Allocate_RejectsUnknownLayout(int layout)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelBuffer.Allocate(4, 4, (PixelLayout)layout));
    }

    [Fact]
    public void GetRow_RowsAreContiguousAndCoverTheBuffer()
    {
        using var buffer = PixelBuffer.Allocate(5, 4, PixelLayout.Pbgra32);
        for (var y = 0; y < buffer.Height; y++)
        {
            var row = buffer.GetRow(y);
            Assert.Equal(buffer.Stride, row.Length);
            row.Fill((byte)(y + 1));
        }

        Assert.True(buffer.TryGetSpan(out var all));
        Assert.Equal(buffer.ByteCount, all.Length);
        for (var i = 0; i < all.Length; i++) Assert.Equal((byte)((i / buffer.Stride) + 1), all[i]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void GetRow_OutOfRange_Throws(int y)
    {
        using var buffer = PixelBuffer.Allocate(2, 3, PixelLayout.Bgr32);

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.GetRow(y));
    }

    [Fact]
    public void GetRow_LastRow_IsReadable()
    {
        using var buffer = PixelBuffer.Allocate(3, 3, PixelLayout.Bgr32);
        buffer.GetRow(2).Fill(7);

        Assert.True(buffer.TryGetSpan(out var all));
        Assert.Equal(7, all[^1]);
        Assert.Equal(7, all[2 * buffer.Stride]);
    }

    [Theory]
    [InlineData(23171, 23171, false)] // 2 147 580 964 byte
    [InlineData(23170, 23170, true)] // 2 147 395 600 byte
    [InlineData(536_870_911, 1, true)] // int.MaxValue - 3
    [InlineData(536_870_911, 2, false)]
    [InlineData(65536, 8193, false)]
    [InlineData(65536, 8192, false)] // đúng 2^31 = int.MaxValue + 1
    [InlineData(65536, 8191, true)]
    public void TryGetSpan_FalseOnlyWhenByteCountExceedsIntMax(int width, int height, bool expected)
    {
        using var fake = CreateUnallocated(width, height, PixelLayout.Bgr32, new NativePixelMemory());

        var ok = fake.TryGetSpan(out var span);

        Assert.Equal(expected, ok);
        // Không đọc/ghi span (khối giả không có bộ nhớ); chỉ kiểm độ dài.
        Assert.Equal(ok ? fake.ByteCount : 0, span.Length);
    }

    [Fact]
    public void TryGetSpan_FalseAt2GiB_TrueJustBelow()
    {
        using var over = CreateUnallocated(65536, 8192, PixelLayout.Pbgra32, new NativePixelMemory());
        using var under = CreateUnallocated(65536, 8191, PixelLayout.Pbgra32, new NativePixelMemory());

        Assert.Equal(int.MaxValue + 1L, over.ByteCount);
        Assert.False(over.TryGetSpan(out var none));
        Assert.True(none.IsEmpty);
        Assert.True(under.TryGetSpan(out var some));
        Assert.Equal(under.ByteCount, some.Length);
    }

    [Fact]
    public void Dispose_Twice_IsSafe_AndMarksDisposed()
    {
        var buffer = PixelBuffer.Allocate(3, 2, PixelLayout.Bgr32);

        buffer.Dispose();
        buffer.Dispose();

        Assert.True(buffer.IsDisposed);
        // Hình học vẫn đọc được sau Dispose (log/metrics).
        Assert.Equal(3, buffer.Width);
        Assert.Equal(2, buffer.Height);
        Assert.Equal(24, buffer.ByteCount);
    }

    [Fact]
    public void UseAfterDispose_ThrowsObjectDisposed()
    {
        var buffer = PixelBuffer.Allocate(3, 2, PixelLayout.Pbgra32);
        buffer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => buffer.Address);
        Assert.Throws<ObjectDisposedException>(() => buffer.GetRow(0));
        Assert.Throws<ObjectDisposedException>(() => buffer.TryGetSpan(out _));
    }
}

/// <summary>
/// WP-02: đếm khối native sống (<c>NativePixelMemory.LiveCount</c>) - Dispose trả đúng một lần, finalizer trả bộ nhớ khi
/// chủ quên Dispose. Bộ đếm là toàn tiến trình nên chạy trong collection không song song.
/// </summary>
[Collection("GlobalState")]
public sealed class PixelBufferLifetimeTests
{
    [Fact]
    public void AllocateAndDispose_ChangeLiveCountByExactlyOne()
    {
        FlushFinalizers();
        var before = NativePixelMemory.LiveCount;

        var buffer = PixelBuffer.Allocate(8, 8, PixelLayout.Bgr32);
        Assert.Equal(before + 1, NativePixelMemory.LiveCount);

        buffer.Dispose();
        Assert.Equal(before, NativePixelMemory.LiveCount);

        buffer.Dispose();
        Assert.Equal(before, NativePixelMemory.LiveCount);
    }

    [Fact]
    public void InvalidAllocate_DoesNotLeak()
    {
        FlushFinalizers();
        var before = NativePixelMemory.LiveCount;

        Assert.Throws<ArgumentOutOfRangeException>(() => PixelBuffer.Allocate(0, 4, PixelLayout.Bgr32));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelBuffer.Allocate(4, 4, (PixelLayout)9));

        Assert.Equal(before, NativePixelMemory.LiveCount);
    }

    [Fact]
    public void UnallocatedBlock_IsNotCounted_AndDisposesAsNoOp()
    {
        FlushFinalizers();
        var before = NativePixelMemory.LiveCount;
        var block = new NativePixelMemory();

        Assert.True(block.IsInvalid);
        block.Dispose();

        Assert.Equal(before, NativePixelMemory.LiveCount);
    }

    [Fact]
    public void Finalizer_ReleasesForgottenBuffers()
    {
        FlushFinalizers();
        var before = NativePixelMemory.LiveCount;

        AllocateAndForget(16);
        Assert.Equal(before + 16, NativePixelMemory.LiveCount);

        // Không chờ theo thời gian: mỗi vòng GC + chờ finalizer là xác định; vài vòng cho chắc với GC nhiều thế hệ.
        for (var i = 0; i < 5 && NativePixelMemory.LiveCount > before; i++) FlushFinalizers();

        Assert.Equal(before, NativePixelMemory.LiveCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateAndForget(int count)
    {
        for (var i = 0; i < count; i++) _ = PixelBuffer.Allocate(64, 64, PixelLayout.Pbgra32);
    }

    private static void FlushFinalizers()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
