using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace PhotoReview.Shell.Interop.Graphics;

// WP-13b: DirectWrite (dwrite.h). "[n]" = SLOT vtable. ReservedNN = chỗ giữ slot. Mọi method [PreserveSig].
// Chuỗi là UTF-16 (StringMarshalling.Utf16) - đủ cho tiếng Việt có dấu.

internal enum DWriteFactoryType : uint
{
    Shared = 0,
    Isolated = 1,
}

internal enum DWriteFontWeight : uint
{
    Thin = 100,
    Light = 300,
    Regular = 400,
    Medium = 500,
    SemiBold = 600,
    Bold = 700,
}

internal enum DWriteFontStyle : uint
{
    Normal = 0,
    Oblique = 1,
    Italic = 2,
}

internal enum DWriteFontStretch : uint
{
    Normal = 5,
}

internal enum DWriteTextAlignment : uint
{
    Leading = 0,
    Trailing = 1,
    Center = 2,
    Justified = 3,
}

internal enum DWriteParagraphAlignment : uint
{
    Near = 0,
    Far = 1,
    Center = 2,
}

internal enum DWriteWordWrapping : uint
{
    Wrap = 0,
    NoWrap = 1,
}

internal enum DWriteTrimmingGranularity : uint
{
    None = 0,
    Character = 1,
    Word = 2,
}

[StructLayout(LayoutKind.Sequential)]
internal struct DWriteTextRange
{
    public uint StartPosition;
    public uint Length;

    public DWriteTextRange(uint startPosition, uint length)
    {
        StartPosition = startPosition;
        Length = length;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct DWriteTrimming
{
    public DWriteTrimmingGranularity Granularity;
    public uint Delimiter;
    public uint DelimiterCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DWriteTextMetrics
{
    public float Left;
    public float Top;
    public float Width;
    public float WidthIncludingTrailingWhitespace;
    public float Height;
    public float LayoutWidth;
    public float LayoutHeight;
    public uint MaxBidiReorderingDepth;
    public uint LineCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DWriteLineMetrics
{
    public uint Length;
    public uint TrailingWhitespaceLength;
    public uint NewlineLength;
    public float Height;
    public float Baseline;
    public int IsTrimmed;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DWriteHitTestMetrics
{
    public uint TextPosition;
    public uint Length;
    public float Left;
    public float Top;
    public float Width;
    public float Height;
    public uint BidiLevel;
    public int IsText;
    public int IsTrimmed;
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid(GraphicsGuids.DWriteTextFormat)]
internal unsafe partial interface IDWriteTextFormat
{
    [PreserveSig] int SetTextAlignment(DWriteTextAlignment textAlignment);                                    // [3]
    [PreserveSig] int SetParagraphAlignment(DWriteParagraphAlignment paragraphAlignment);                     // [4]
    [PreserveSig] int SetWordWrapping(DWriteWordWrapping wordWrapping);                                       // [5]
    [PreserveSig] int Reserved06SetReadingDirection(uint a);                                                  // [6]
    [PreserveSig] int Reserved07SetFlowDirection(uint a);                                                     // [7]
    [PreserveSig] int Reserved08SetIncrementalTabStop(float a);                                               // [8]
    [PreserveSig] int SetTrimming(DWriteTrimming* trimmingOptions, nint trimmingSign);                        // [9]
    [PreserveSig] int Reserved10SetLineSpacing(uint a, float b, float c);                                     // [10]
    [PreserveSig] DWriteTextAlignment GetTextAlignment();                                                     // [11]
    [PreserveSig] DWriteParagraphAlignment GetParagraphAlignment();                                           // [12]
    [PreserveSig] DWriteWordWrapping GetWordWrapping();                                                       // [13]
    [PreserveSig] uint Reserved14GetReadingDirection();                                                       // [14]
    [PreserveSig] uint Reserved15GetFlowDirection();                                                          // [15]
    [PreserveSig] float Reserved16GetIncrementalTabStop();                                                    // [16]
    [PreserveSig] int Reserved17GetTrimming(nint a, out nint b);                                              // [17]
    [PreserveSig] int Reserved18GetLineSpacing(nint a, nint b, nint c);                                       // [18]
    [PreserveSig] int Reserved19GetFontCollection(out nint a);                                                // [19]
    [PreserveSig] uint Reserved20GetFontFamilyNameLength();                                                   // [20]
    [PreserveSig] int Reserved21GetFontFamilyName(nint a, uint b);                                            // [21]
    [PreserveSig] DWriteFontWeight GetFontWeight();                                                           // [22]
    [PreserveSig] DWriteFontStyle GetFontStyle();                                                             // [23]
    [PreserveSig] DWriteFontStretch GetFontStretch();                                                         // [24]
    [PreserveSig] float GetFontSize();                                                                        // [25]
    [PreserveSig] uint Reserved26GetLocaleNameLength();                                                       // [26]
    [PreserveSig] int Reserved27GetLocaleName(nint a, uint b);                                                // [27]
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid(GraphicsGuids.DWriteTextLayout)]
internal unsafe partial interface IDWriteTextLayout : IDWriteTextFormat
{
    [PreserveSig] int SetMaxWidth(float maxWidth);                                                            // [28]
    [PreserveSig] int SetMaxHeight(float maxHeight);                                                          // [29]
    [PreserveSig] int Reserved30SetFontCollection(nint a, DWriteTextRange b);                                 // [30]
    [PreserveSig] int SetFontFamilyName(string fontFamilyName, DWriteTextRange textRange);                    // [31]
    [PreserveSig] int SetFontWeight(DWriteFontWeight fontWeight, DWriteTextRange textRange);                  // [32]
    [PreserveSig] int SetFontStyle(DWriteFontStyle fontStyle, DWriteTextRange textRange);                     // [33]
    [PreserveSig] int Reserved34SetFontStretch(uint a, DWriteTextRange b);                                    // [34]
    [PreserveSig] int SetFontSize(float fontSize, DWriteTextRange textRange);                                 // [35]
    [PreserveSig] int Reserved36SetUnderline(int a, DWriteTextRange b);                                       // [36]
    [PreserveSig] int Reserved37SetStrikethrough(int a, DWriteTextRange b);                                   // [37]
    [PreserveSig] int Reserved38SetDrawingEffect(nint a, DWriteTextRange b);                                  // [38]
    [PreserveSig] int Reserved39SetInlineObject(nint a, DWriteTextRange b);                                   // [39]
    [PreserveSig] int Reserved40SetTypography(nint a, DWriteTextRange b);                                     // [40]
    [PreserveSig] int Reserved41SetLocaleName(nint a, DWriteTextRange b);                                     // [41]
    [PreserveSig] float GetMaxWidth();                                                                        // [42]
    [PreserveSig] float GetMaxHeight();                                                                       // [43]
    [PreserveSig] int Reserved44GetFontCollection(uint a, out nint b, nint c);                                // [44]
    [PreserveSig] int Reserved45GetFontFamilyNameLength(uint a, out uint b, nint c);                          // [45]
    [PreserveSig] int Reserved46GetFontFamilyName(uint a, nint b, uint c, nint d);                            // [46]
    [PreserveSig] int Reserved47GetFontWeight(uint a, out uint b, nint c);                                    // [47]
    [PreserveSig] int Reserved48GetFontStyle(uint a, out uint b, nint c);                                     // [48]
    [PreserveSig] int Reserved49GetFontStretch(uint a, out uint b, nint c);                                   // [49]
    [PreserveSig] int Reserved50GetFontSize(uint a, out float b, nint c);                                     // [50]
    [PreserveSig] int Reserved51GetUnderline(uint a, out int b, nint c);                                      // [51]
    [PreserveSig] int Reserved52GetStrikethrough(uint a, out int b, nint c);                                  // [52]
    [PreserveSig] int Reserved53GetDrawingEffect(uint a, out nint b, nint c);                                 // [53]
    [PreserveSig] int Reserved54GetInlineObject(uint a, out nint b, nint c);                                  // [54]
    [PreserveSig] int Reserved55GetTypography(uint a, out nint b, nint c);                                    // [55]
    [PreserveSig] int Reserved56GetLocaleNameLength(uint a, out uint b, nint c);                              // [56]
    [PreserveSig] int Reserved57GetLocaleName(uint a, nint b, uint c, nint d);                                // [57]
    [PreserveSig] int Reserved58Draw(nint a, nint b, float c, float d);                                       // [58]
    [PreserveSig] int GetLineMetrics(DWriteLineMetrics* lineMetrics, uint maxLineCount, out uint actualLineCount); // [59]
    [PreserveSig] int GetMetrics(out DWriteTextMetrics textMetrics);                                          // [60]
    [PreserveSig] int Reserved61GetOverhangMetrics(nint a);                                                   // [61]
    [PreserveSig] int Reserved62GetClusterMetrics(nint a, uint b, out uint c);                                // [62]
    [PreserveSig] int Reserved63DetermineMinWidth(out float a);                                               // [63]
    [PreserveSig] int HitTestPoint(float pointX, float pointY, out int isTrailingHit, out int isInside,
        out DWriteHitTestMetrics hitTestMetrics);                                                             // [64]
    [PreserveSig] int HitTestTextPosition(uint textPosition, int isTrailingHit, out float pointX, out float pointY,
        out DWriteHitTestMetrics hitTestMetrics);                                                             // [65]
    [PreserveSig] int Reserved66HitTestTextRange(uint a, uint b, float c, float d, nint e, uint f, out uint g); // [66]
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid(GraphicsGuids.DWriteFactory)]
internal partial interface IDWriteFactory
{
    [PreserveSig] int GetSystemFontCollection(out nint fontCollection, int checkForUpdates);                  // [3]
    [PreserveSig] int Reserved04CreateCustomFontCollection(nint a, nint b, uint c, out nint d);               // [4]
    [PreserveSig] int Reserved05RegisterFontCollectionLoader(nint a);                                         // [5]
    [PreserveSig] int Reserved06UnregisterFontCollectionLoader(nint a);                                       // [6]
    [PreserveSig] int Reserved07CreateFontFileReference(nint a, nint b, out nint c);                          // [7]
    [PreserveSig] int Reserved08CreateCustomFontFileReference(nint a, uint b, nint c, out nint d);            // [8]
    [PreserveSig] int Reserved09CreateFontFace(uint a, uint b, nint c, uint d, uint e, out nint f);           // [9]
    [PreserveSig] int Reserved10CreateRenderingParams(out nint a);                                            // [10]
    [PreserveSig] int Reserved11CreateMonitorRenderingParams(nint a, out nint b);                             // [11]
    [PreserveSig] int Reserved12CreateCustomRenderingParams(float a, float b, float c, uint d, uint e, out nint f); // [12]
    [PreserveSig] int Reserved13RegisterFontFileLoader(nint a);                                               // [13]
    [PreserveSig] int Reserved14UnregisterFontFileLoader(nint a);                                             // [14]

    /// <summary>[15] <paramref name="fontCollection"/> = 0 -> bộ font hệ thống.</summary>
    [PreserveSig]
    int CreateTextFormat(string fontFamilyName, nint fontCollection, DWriteFontWeight fontWeight,
        DWriteFontStyle fontStyle, DWriteFontStretch fontStretch, float fontSize, string localeName,
        out nint textFormat);

    [PreserveSig] int Reserved16CreateTypography(out nint a);                                                 // [16]
    [PreserveSig] int Reserved17GetGdiInterop(out nint a);                                                    // [17]

    /// <summary>[18]</summary>
    [PreserveSig]
    int CreateTextLayout(string text, uint stringLength, IDWriteTextFormat textFormat, float maxWidth,
        float maxHeight, out nint textLayout);

    [PreserveSig] int Reserved19CreateGdiCompatibleTextLayout(nint a, uint b, nint c, float d, float e, float f, nint g, int h, out nint i); // [19]
    [PreserveSig] int Reserved20CreateEllipsisTrimmingSign(nint a, out nint b);                               // [20]
    [PreserveSig] int Reserved21CreateTextAnalyzer(out nint a);                                               // [21]
    [PreserveSig] int Reserved22CreateNumberSubstitution(uint a, nint b, int c, out nint d);                  // [22]
    [PreserveSig] int Reserved23CreateGlyphRunAnalysis(nint a, float b, nint c, uint d, uint e, float f, float g, out nint h); // [23]
}

internal static partial class DWrite
{
    [LibraryImport("dwrite.dll", EntryPoint = "DWriteCreateFactory")]
    private static partial int DWriteCreateFactoryNative(DWriteFactoryType factoryType, in Guid iid, out nint factory);

    public static IDWriteFactory CreateFactory(DWriteFactoryType type = DWriteFactoryType.Shared)
    {
        ComInterop.Check(DWriteCreateFactoryNative(type, GraphicsGuids.IidDWriteFactory, out nint raw));
        return ComInterop.Wrap<IDWriteFactory>(raw);
    }
}
