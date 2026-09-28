using System.Runtime.CompilerServices;

// IMG-07: lets tests call TurboJpegDecoder.ScanHeader directly (the combined ICC+EXIF marker walk used by
// Decode) to assert its output and to benchmark it against the pre-refactor separate-walk calls.
[assembly: InternalsVisibleTo("PhotoReview.Imaging.Tests")]
