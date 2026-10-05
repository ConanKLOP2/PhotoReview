$ErrorActionPreference = 'Stop'
$fallback = Get-Content src/PhotoReview.Imaging/Decoding/FallbackImageDecoder.cs -Raw
$memory = Get-Content src/PhotoReview.Imaging/Decoding/MemoryHeadroom.cs -Raw
$wic = Get-Content src/PhotoReview.Imaging/Decoding/Wic/WicDirectDecoder.cs -Raw
$start = $wic.IndexOf('    internal static void EnsureOutputFits(')
$end = $wic.IndexOf('    private static IWICImagingFactory CreateFactory()', $start)
$guard = $wic.Substring($start, $end - $start)
$stubs = @'
using System;
using System.IO;
using System.Runtime.InteropServices;
namespace PhotoReview.Imaging.Metadata { public class ExifSummary {} }
namespace PhotoReview.Imaging.Decoding {
public enum DecoderBackend { Wpf, WicDirect }
public class FileFormatException : Exception {}
public interface IDecodedImage { int PixelWidth {get;} int PixelHeight {get;} bool Downscaled {get;} int Orientation {get;} long EstimatedBytes {get;} object PlatformImage {get;} DecoderBackend ActualBackend {get;} int OriginalWidth {get;} int OriginalHeight {get;} bool IsDegradedFallback {get;} PhotoReview.Imaging.Metadata.ExifSummary Exif {get;} }
public record DecodeRequest(string Path) { public ReadOnlyMemory<byte>? Bytes {get;init;} }
public record ImageInfo;
public interface IImageDecoder { IDecodedImage Decode(DecodeRequest request); ImageInfo ReadInfo(string path); }
public interface ILog { void Warn(string text); }
public class NullLog : ILog { public static NullLog Instance = new(); public void Warn(string text) {} }
public class ReviewMetrics { public void RecordDecoderFallback(DecoderBackend backend) {} }
public class DecoderBusyException : Exception {}
public static class DecodeFailureSourceBytes { public static bool TryGet(Exception ex,out ReadOnlyMemory<byte> bytes) {bytes=default;return false;} }
public static class UserFacingError { public static T Localized<T>(T ex,Func<string> text) where T:Exception => ex; }
public static class Tr { public static string ErrDecoderOutputTooLarge(int w,int h,long b) => "low memory"; }
public class GuardedPrimary : IImageDecoder {
public IDecodedImage Decode(DecodeRequest r) { WicGuard.EnsureOutputFits(8192,4096,MemoryHeadroom.GuardThresholdBytes,() => (256L<<20,240L<<20)); throw new Exception("guard failed to refuse"); }
public ImageInfo ReadInfo(string path)=>new(); }
public class RecordingFallback : IImageDecoder {
public int Calls; public IDecodedImage Decode(DecodeRequest r) {Calls++;throw new ApplicationException("fallback entered");} public ImageInfo ReadInfo(string path)=>new(); }
public static class Probe { public static string Run() {var fallback=new RecordingFallback();var decoder=new FallbackImageDecoder(new GuardedPrimary(),DecoderBackend.WicDirect,fallback);try {decoder.Decode(new DecodeRequest("synthetic-no-io"));}catch(ApplicationException){}return "Low-RAM refusal entered fallback; calls="+fallback.Calls;} }
public static class WicGuard {
'@
$fallback = $fallback.Replace('using System.IO;', '').Replace('using System.Runtime.InteropServices;', '').Replace('namespace PhotoReview.Imaging.Decoding;', 'namespace PhotoReview.Imaging.Decoding {').Replace('ReviewMetrics?', 'ReviewMetrics').Replace('ILog?', 'ILog').Replace('ExifSummary?', 'ExifSummary')
$memory = $memory.Replace('namespace PhotoReview.Imaging.Decoding;', 'namespace PhotoReview.Imaging.Decoding {')
Add-Type -TypeDefinition ($stubs + $guard + "`n}`n}`n" + $fallback + "`n}`n" + $memory + "`n}`n")
[PhotoReview.Imaging.Decoding.Probe]::Run()
