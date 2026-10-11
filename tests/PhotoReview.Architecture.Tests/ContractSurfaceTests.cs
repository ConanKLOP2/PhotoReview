using PhotoReview.App.Composition;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Input;
using PhotoReview.App.Menus;
using PhotoReview.App.Services;
using PhotoReview.App.ViewModels;
using PhotoReview.App.Viewport;
using PhotoReview.App.Windowing;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Win32.Hosting;
using PhotoReview.Shell.Win32.Menus;
using PhotoReview.Shell.Win32.Overlay;
using PhotoReview.Shell.Win32.Startup;
using PhotoReview.Shell.WpfBridge;
using PhotoReview.TestSupport.Golden;

namespace PhotoReview.Architecture.Tests;

/// <summary>
/// L-CONTRACT (NO-WPF-EXEC-PLAN mục 3.3 + 5, WP-01): chữ ký các hợp đồng C-01..C-18 khớp file duyệt
/// <c>Approved/contracts.v1.txt</c>. File duyệt SINH từ reflection (<see cref="WriteApprovedSurface"/>, Category=Manual),
/// không viết tay. Đổi hợp đồng = PR nhỏ của lead: sửa khai báo, chạy lại test Manual để ghi file duyệt, giải thích trong PR.
/// Gói tính năng KHÔNG được sửa file duyệt (dừng và báo lead).
/// </summary>
public sealed class ContractSurfaceTests
{
    internal const string ApprovedRelativePath = "tests/PhotoReview.Architecture.Tests/Approved/contracts.v1.txt";

    /// <summary>Opt-in for <see cref="WriteApprovedSurface"/>: a filter that happens to match it must not rewrite the frozen file.</summary>
    internal const string RegenerateEnvironmentVariable = "PHOTOREVIEW_REGENERATE_CONTRACTS";

    private const string Header =
        "PhotoReview - hợp đồng NO-WPF v1.2 (v1 đóng băng ở WP-01; v1.1: C-07 vào App.Shared, C-09 IRenderSurfaceFactory, C-03 ExifOrientation; v1.2 (WP-09): IClipboardService sang App.Shared, khoá ở C-13). FILE SINH TỰ ĐỘNG - không sửa tay.\n" +
        "Sinh lại: dotnet test tests/PhotoReview.Architecture.Tests -c Release --filter \"FullyQualifiedName~ContractSurfaceTests.WriteApprovedSurface\"\n" +
        "Kế hoạch: docs/refactoring/decisions/NO-WPF-EXEC-PLAN.md mục 5.";

    /// <summary>Các kiểu thuộc từng hợp đồng (mục 5). Thêm/bớt kiểu ở đây = đổi hợp đồng.</summary>
    internal static readonly (string Id, Type[] Types)[] Contracts =
    [
        ("C-01", [typeof(PixelLayout), typeof(PixelBuffer)]),
        ("C-02", [typeof(IPlatformImageCodec), typeof(PixelLease), typeof(PixelBufferImageCodec), typeof(DecodedImage)]),
        ("C-03", [typeof(PixelOps), typeof(ExifOrientation)]),
        ("C-04", [typeof(UiPriority), typeof(IUiDispatcher)]),
        ("C-05", [typeof(FrameTick), typeof(FrameTickEventArgs), typeof(IFrameClock)]),
        ("C-06", [typeof(PointD), typeof(SizeD), typeof(RectD), typeof(PointerButton), typeof(KeyModifiers), typeof(KeyId), typeof(KeyIdMapping)]),
        ("C-07", [typeof(IImageSurface), typeof(IFitSurface), typeof(ViewportSnapshot)]),
        ("C-08", [typeof(ScrollBarPolicy), typeof(ViewportInput), typeof(ViewportLayout), typeof(ViewportLayoutEngine), typeof(ViewerStretchMode)]),
        ("C-09", [typeof(ColorF), typeof(ImageInterpolation), typeof(PresentResult), typeof(RenderSurfaceOptions), typeof(IRenderSurface), typeof(IRenderSurfaceFactory), typeof(IDrawContext)]),
        ("C-10", [typeof(IGpuImage), typeof(IGpuImageCache)]),
        ("C-11", [typeof(TextTrimming), typeof(TextStyle), typeof(ITextLayout), typeof(ITextRenderer),
                  typeof(OverlayLayoutContext), typeof(IOverlayElement), typeof(IAnimator), typeof(Easing)]),
        ("C-12", [typeof(MenuItemKind), typeof(MenuItemModel), typeof(ContextMenuBuildContext), typeof(ContextMenuModelBuilder), typeof(IPopupMenuHost)]),
        ("C-13", [typeof(IClipboardService), typeof(IZoomPromptService), typeof(ISecondaryWindowHost), typeof(SecondaryWindowHostFactory)]),
        ("C-14", [typeof(WindowShowState), typeof(WindowPlacementData), typeof(IWindowPlacementStore), typeof(WindowPlacementRules), typeof(IFullscreenController)]),
        ("C-15", [typeof(ShellCursor), typeof(WindowMessage), typeof(IWindowMessageHandler), typeof(IShellWindow)]),
        ("C-16", [typeof(SharedServiceOptions), typeof(SharedServiceRegistration), typeof(IPresentationSinkFactory),
                  typeof(MainViewModelSinkCallbacks), typeof(IPresentationSink), typeof(ShellStartupContext)]),
        ("C-17", [typeof(GoldenViewportCase), typeof(GoldenInputScript), typeof(GoldenSetup), typeof(GoldenInputStep), typeof(GoldenCheckpoint),
                  typeof(GoldenKeyName), typeof(GoldenMenuCase), typeof(ViewportInputDto), typeof(ViewportLayoutDto)]),
        ("C-18", [typeof(ShellPerfMarks)]),
    ];

    /// <summary>Kiểu chỉ khoá một phần: ExifOrientation còn nửa WPF (<c>ExifOrientationWpf.cs</c>) tới WP-06.</summary>
    internal static readonly IReadOnlyDictionary<Type, string[]> OnlyMembers = new Dictionary<Type, string[]>
    {
        [typeof(ExifOrientation)] = ["IsTransposed", "Normalize"],
    };

    internal static string ActualSurface() => ContractSurface.Render(Contracts, Header, OnlyMembers);

    private static string ApprovedPath => Path.Combine(RepoScan.Root, ApprovedRelativePath);

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact(DisplayName = "L-CONTRACT: contract signatures (C-01..C-18) match Approved/contracts.v1.txt (v1.2)")]
    [Trait("Category", "Architecture")]
    public void Contracts_MatchApprovedSurface()
    {
        Assert.True(File.Exists(ApprovedPath), $"Missing approved contract file {ApprovedRelativePath}");
        var approved = Normalize(File.ReadAllText(ApprovedPath));
        var actual = ActualSurface();
        if (string.Equals(approved, actual, StringComparison.Ordinal)) return;

        var actualPath = Path.Combine(AppContext.BaseDirectory, "contracts.v1.actual.txt");
        File.WriteAllText(actualPath, actual);

        var approvedLines = approved.Split('\n');
        var actualLines = actual.Split('\n');
        var removed = approvedLines.Except(actualLines, StringComparer.Ordinal).Select(l => "- " + l);
        var added = actualLines.Except(approvedLines, StringComparer.Ordinal).Select(l => "+ " + l);
        Assert.Fail(
            "A frozen NO-WPF contract changed (NO-WPF-EXEC-PLAN section 5). Feature packages must not change contracts: stop and " +
            $"report to the lead. Full actual surface: {actualPath}\n" + string.Join("\n", removed.Concat(added).Take(80)));
    }

    [Fact(DisplayName = "L-CONTRACT: every contract type is listed once")]
    [Trait("Category", "Architecture")]
    public void Contracts_ListEachTypeOnce()
    {
        var all = Contracts.SelectMany(c => c.Types).ToArray();

        Assert.Equal(all.Length, all.Distinct().Count());
        Assert.Equal(18, Contracts.Length); // C-01..C-18 (C-07 khoá ở v1.1)
    }

    /// <summary>Ghi lại file duyệt từ reflection. Chỉ lead chạy, trong PR đổi hợp đồng.</summary>
    [Fact(DisplayName = "L-CONTRACT (manual): regenerate Approved/contracts.v1.txt from the built assemblies")]
    [Trait("Category", "Manual")]
    public void WriteApprovedSurface()
    {
        // A broad filter such as FullyQualifiedName~ContractSurfaceTests also matches this test: without the opt-in it must
        // NOT touch the frozen file (it would silently approve whatever the working tree declares) and it fails loudly.
        Assert.True(
            Environment.GetEnvironmentVariable(RegenerateEnvironmentVariable) == "1",
            $"This manual test rewrites {ApprovedRelativePath}. Set {RegenerateEnvironmentVariable}=1 to regenerate it on purpose " +
            "(lead only, in a PR that changes a contract).");
        Directory.CreateDirectory(Path.GetDirectoryName(ApprovedPath)!);
        File.WriteAllText(ApprovedPath, ActualSurface());

        Assert.Equal(ActualSurface(), Normalize(File.ReadAllText(ApprovedPath)));
    }
}
