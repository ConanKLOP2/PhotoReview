using System.IO;
using PhotoReview.Imaging.Metadata;
using PhotoReview.Imaging.Tests.Raw.Tiff;
using static PhotoReview.Imaging.Tests.Raw.Tiff.TiffBytes;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// Canon CR3: CMT1 IFD0 holds camera/date only, CMT2 IFD0 holds the exposure fields directly (no 0x8769 pointer).
/// RawExif must merge both blocks; ExifParser needs an "IFD is the Exif IFD" mode for CMT2.
/// </summary>
public sealed class Cr3ExifMergeTests
{
    private static RawContainerInfo Info(params ExifBlock[] blocks) =>
        new(RawFormat.Cr3, 100, 100, 1, [], blocks);

    /// <summary>512-byte TIFF; IFD0 at 8 with ISO 800, f/2.8 (28/10), 1/250, focal 50, lens name, DateTimeOriginal.</summary>
    private static byte[] BuildExposureTiff()
    {
        var t = new TiffBytes(true, 512).Header(8)
            .Ifd(8, 0,
                At(0x829A, 5, 1, 200), At(0x829D, 5, 1, 208), Short(0x8827, 800),
                At(0x9003, 2, 20, 216), At(0x920A, 5, 1, 240), At(0xA434, 2, 8, 250));
        t.U32(200, 1).U32(204, 250).U32(208, 28).U32(212, 10).U32(240, 50).U32(244, 1);
        t.Put(216, "2020:01:02 03:04:05\0"u8).Put(250, "RF50mm\0\0"u8);
        return t.ToArray();
    }

    private static byte[] BuildCameraTiff()
    {
        var t = new TiffBytes(true, 256).Header(8)
            .Ifd(8, 0, At(0x010F, 2, 6, 100), At(0x0110, 2, 9, 110), At(0x0132, 2, 20, 130));
        t.Put(100, "Canon\0"u8).Put(110, "Canon EOS\0"u8).Put(130, "2019:05:06 07:08:09\0"u8);
        return t.ToArray();
    }

    private static (InMemoryRawHeaderSource Source, RawContainerInfo Info) Layout(bool cmt2IsExif)
    {
        byte[] cmt1 = BuildCameraTiff();
        byte[] cmt2 = BuildExposureTiff();
        byte[] file = [.. cmt1, .. cmt2];
        return (new InMemoryRawHeaderSource(file),
            Info(new ExifBlock(0, cmt1.Length, true), new ExifBlock(cmt1.Length, cmt2.Length, true, IfdIsExif: cmt2IsExif)));
    }

    [Fact]
    public void TryReadExif_Cr3StyleCmt1PlusCmt2_MergesCameraAndExposure()
    {
        var (source, info) = Layout(cmt2IsExif: true);
        var exif = RawExif.TryReadExif(source, info);

        Assert.NotNull(exif);
        Assert.Equal("Canon", exif.CameraMake);
        Assert.Equal("Canon EOS", exif.CameraModel);
        Assert.Equal(800, exif.Iso);
        Assert.Equal(new ExifRational(28, 10), exif.FNumber);
        Assert.Equal(new ExifRational(1, 250), exif.ExposureTime);
        Assert.Equal(new ExifRational(50, 1), exif.FocalLength);
        Assert.Equal("RF50mm", exif.LensModel);
        Assert.NotNull(exif.DateTaken);
    }

    [Fact]
    public void TryReadExif_Cr3WhereCmt1AndCmt2DatesDiffer_PrefersTheCaptureTimeOfCmt2()
    {
        // BuildCameraTiff has DateTime 2019:05:06 (0x0132); BuildExposureTiff has DateTimeOriginal 2020:01:02 (0x9003).
        var (source, info) = Layout(cmt2IsExif: true);

        var exif = RawExif.TryReadExif(source, info);

        Assert.Equal(new DateTime(2020, 1, 2, 3, 4, 5), exif!.DateTaken);
    }

    [Fact]
    public void TryReadExif_Cmt2WithoutDate_KeepsTheCmt1Date()
    {
        byte[] cmt1 = BuildCameraTiff();
        var t = new TiffBytes(true, 128).Header(8).Ifd(8, 0, Short(0x8827, 100));
        byte[] cmt2 = t.ToArray();
        var source = new InMemoryRawHeaderSource([.. cmt1, .. cmt2]);

        var exif = RawExif.TryReadExif(source, Info(new ExifBlock(0, cmt1.Length, true), new ExifBlock(cmt1.Length, cmt2.Length, true, true)));

        Assert.Equal(new DateTime(2019, 5, 6, 7, 8, 9), exif!.DateTaken);
    }

    [Fact]
    public void TryReadExif_Cmt2WithoutIfdIsExifFlag_DoesNotReadExposure()
    {
        var (source, info) = Layout(cmt2IsExif: false);
        var exif = RawExif.TryReadExif(source, info);

        Assert.NotNull(exif);
        Assert.Equal("Canon", exif.CameraMake);
        Assert.Null(exif.Iso);
    }

    [Fact]
    public void TryReadExif_FirstBlockWinsPerField()
    {
        byte[] a = BuildExposureTiff();
        var t = new TiffBytes(true, 128).Header(8).Ifd(8, 0, Short(0x8827, 100));
        byte[] b = t.ToArray();
        var source = new InMemoryRawHeaderSource([.. a, .. b]);
        var exif = RawExif.TryReadExif(source, Info(new ExifBlock(0, a.Length, true, true), new ExifBlock(a.Length, b.Length, true, true)));

        Assert.Equal(800, exif!.Iso);
    }

    /// <summary>TIFF whose IFD0 (read as the Exif IFD) holds only DateTimeOriginal; <paramref name="date"/> is "yyyy:MM:dd HH:mm:ss".</summary>
    private static byte[] BuildDateOnlyExifTiff(string date)
    {
        var t = new TiffBytes(true, 128).Header(8).Ifd(8, 0, At(0x9003, 2, 20, 100));
        t.Put(100, System.Text.Encoding.ASCII.GetBytes(date + "\0"));
        return t.ToArray();
    }

    [Fact]
    public void TryReadExif_TwoIfdIsExifBlocksFirstWithDate_TheFirstDateWins()
    {
        byte[] a = BuildDateOnlyExifTiff("2020:01:02 03:04:05");
        byte[] b = BuildDateOnlyExifTiff("2021:06:07 08:09:10");
        var source = new InMemoryRawHeaderSource([.. a, .. b]);

        var exif = RawExif.TryReadExif(source, Info(new ExifBlock(0, a.Length, true, true), new ExifBlock(a.Length, b.Length, true, true)));

        Assert.Equal(new DateTime(2020, 1, 2, 3, 4, 5), exif!.DateTaken);
    }

    [Fact]
    public void TryReadExif_DatelessFirstBlockThenTwoIfdIsExifBlocksWithDates_TheFirstExifDateWins()
    {
        // The first block adopted (through Merge) a date from an IfdIsExif block: a later IfdIsExif block must not override it.
        var cameraOnly = new TiffBytes(true, 128).Header(8).Ifd(8, 0, At(0x010F, 2, 6, 100));
        cameraOnly.Put(100, "Canon\0"u8);
        byte[] cam = cameraOnly.ToArray();
        byte[] a = BuildDateOnlyExifTiff("2020:01:02 03:04:05");
        byte[] b = BuildDateOnlyExifTiff("2021:06:07 08:09:10");
        var source = new InMemoryRawHeaderSource([.. cam, .. a, .. b]);

        var exif = RawExif.TryReadExif(source, Info(
            new ExifBlock(0, cam.Length, true),
            new ExifBlock(cam.Length, a.Length, true, true),
            new ExifBlock(cam.Length + a.Length, b.Length, true, true)));

        Assert.Equal("Canon", exif!.CameraMake);
        Assert.Equal(new DateTime(2020, 1, 2, 3, 4, 5), exif.DateTaken);
    }

    [Fact]
    public void TryReadExif_HostileSecondBlock_KeepsFirstSummary()
    {
        byte[] cmt1 = BuildCameraTiff();
        var source = new InMemoryRawHeaderSource([.. cmt1, .. new byte[64]]);
        var exif = RawExif.TryReadExif(source, Info(new ExifBlock(0, cmt1.Length, true), new ExifBlock(cmt1.Length, 64, true, true)));

        Assert.Equal("Canon", exif!.CameraMake);
    }

    [Fact]
    public void TryParseTiffBlock_IfdIsExif_ReadsExposureFromIfd0()
    {
        var exif = ExifParser.TryParseTiffBlock(BuildExposureTiff(), ifdIsExif: true);
        Assert.Equal(800, exif!.Iso);
        Assert.Null(ExifParser.TryParseTiffBlock(BuildExposureTiff(), ifdIsExif: false));
    }

    [Theory]
    [Trait("Category", "Native")]
    [InlineData("Canon - EOS M50 - CRAW (3_2).CR3", "Canon EOS M50", 1000, 4, 1, 1, 60, 15, "EF-M15-45mm f/3.5-6.3 IS STM")]
    [InlineData("Canon - EOS R6 - 3_2.CR3", "Canon EOS R6", 100, 25, 1, 1, 4, 200, "RF70-200mm F2.8 L IS USM")]
    public void CorpusCr3_ExifSummary_HasExposureFieldsFromCmt2(
        string file, string model, int iso, uint fNum, uint fDen, uint expNum, uint expDen, uint focal, string lens)
    {
        if (RawCorpus.TryGetFile(file) is not { } path) return;

        using var fs = File.OpenRead(path);
        using var source = new SourceRawHeaderSource(fs);
        var info = new PhotoReview.Imaging.Raw.Bmff.Cr3ContainerReader().Read(source, CancellationToken.None);
        var exif = RawExif.TryReadExif(source, info);

        Assert.NotNull(exif);
        Assert.Equal("Canon", exif.CameraMake);
        Assert.Equal(model, exif.CameraModel);
        Assert.Equal(iso, exif.Iso);
        Assert.Equal(new ExifRational(fNum, fDen), exif.FNumber);
        Assert.Equal(new ExifRational(expNum, expDen), exif.ExposureTime);
        Assert.Equal(new ExifRational(focal, 1), exif.FocalLength);
        Assert.Equal(lens, exif.LensModel);
        Assert.NotNull(exif.DateTaken);
    }
}
