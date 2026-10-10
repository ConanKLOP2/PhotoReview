using System.Reflection;
using System.Runtime.InteropServices;

namespace PhotoReview.Architecture.Tests;

/// <summary>
/// NO-WPF-EXEC-PLAN mục 3.3 (WP-01): luật kiến trúc của các project mới, kiểm trên assembly đã build (reflection):
/// L-SHELL (Shell.* không WPF/WinForms/App WPF; chỉ WpfBridgeLoader được dùng Shell.WpfBridge), L-SHARED (App.Shared không
/// WPF, không App/Shell), L-AOT (IsAotCompatible, không [ComImport], P/Invoke chỉ qua LibraryImport, không dynamic).
/// </summary>
public sealed class ShellRulesTests
{
    private static readonly Assembly AppShared = typeof(PhotoReview.App.Input.PointD).Assembly;
    private static readonly Assembly ShellInterop = Assembly.Load("PhotoReview.Shell.Interop"); // interop là internal
    private static readonly Assembly ShellRendering = typeof(PhotoReview.Shell.Rendering.IRenderSurface).Assembly;
    private static readonly Assembly ShellWin32 = typeof(PhotoReview.Shell.Win32.Hosting.IShellWindow).Assembly;

    /// <summary>Assembly WPF/WinForms và các assembly phía WPF của PhotoReview (mục 3.1).</summary>
    private static readonly string[] WpfSideAssemblies =
    [
        "PresentationCore", "PresentationFramework", "WindowsBase", "System.Xaml", "System.Windows.Forms",
        "PhotoReview.App", "PhotoReview.App.WpfWindows", "PhotoReview.Imaging.Wpf",
    ];

    public static TheoryData<string> ShellAssemblyNames => new() { "PhotoReview.Shell.Interop", "PhotoReview.Shell.Rendering", "PhotoReview" };

    public static TheoryData<string> AotAssemblyNames => new() { "PhotoReview.App.Shared", "PhotoReview.Shell.Interop", "PhotoReview.Shell.Rendering", "PhotoReview" };

    private static Assembly ByName(string name) =>
        new[] { AppShared, ShellInterop, ShellRendering, ShellWin32 }.Single(a => a.GetName().Name == name);

    private static string[] ReferencedNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(r => r.Name ?? string.Empty).ToArray();

    [Theory(DisplayName = "L-SHELL: Shell.Interop/Rendering/Win32 reference no WPF, WinForms, App WPF or Shell.WpfBridge assembly outside the bridge loader")]
    [MemberData(nameof(ShellAssemblyNames))]
    [Trait("Category", "Architecture")]
    public void ShellAssembly_DoesNotReferenceWpfSide(string assemblyName)
    {
        var assembly = ByName(assemblyName);
        var forbidden = assembly == ShellWin32 ? WpfSideAssemblies : [.. WpfSideAssemblies, "PhotoReview.Shell.WpfBridge"];

        Assert.Empty(ReferencedNames(assembly).Intersect(forbidden, StringComparer.Ordinal));
    }

    [Fact(DisplayName = "L-SHELL: in Shell.Win32 only Dialogs/WpfBridgeLoader depends on PhotoReview.Shell.WpfBridge (NE-5)")]
    [Trait("Category", "Architecture")]
    public void ShellWin32_OnlyBridgeLoaderDependsOnWpfBridge()
    {
        var result = Types.InAssembly(ShellWin32)
            .That().DoNotHaveName("WpfBridgeLoader")
            .ShouldNot().HaveDependencyOn("PhotoReview.Shell.WpfBridge")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Only PhotoReview.Shell.Win32.Dialogs.WpfBridgeLoader may touch the WPF bridge (lazy, NoInlining, C-13): " +
            string.Join(", ", result.FailingTypeNames ?? []));
        Assert.All(ShellWin32.GetTypes().Where(t => t.Name == "WpfBridgeLoader"),
            t => Assert.Equal("PhotoReview.Shell.Win32.Dialogs", t.Namespace));
    }

    [Fact(DisplayName = "L-SHARED: PhotoReview.App.Shared references no WPF assembly, PhotoReview.App or PhotoReview.Shell.*")]
    [Trait("Category", "Architecture")]
    public void AppShared_DoesNotReferenceWpfAppOrShell()
    {
        var references = ReferencedNames(AppShared);

        Assert.Empty(references.Intersect(WpfSideAssemblies, StringComparer.Ordinal));
        Assert.DoesNotContain(references, r => r.StartsWith("PhotoReview.Shell", StringComparison.Ordinal) || r == "PhotoReview");
    }

    [Theory(DisplayName = "L-AOT: new shared/shell assemblies are built with IsAotCompatible (IsTrimmable metadata)")]
    [MemberData(nameof(AotAssemblyNames))]
    [Trait("Category", "Architecture")]
    public void AotAssembly_IsMarkedTrimmable(string assemblyName)
    {
        var metadata = ByName(assemblyName).GetCustomAttributes<AssemblyMetadataAttribute>();

        Assert.Contains(metadata, m => m.Key == "IsTrimmable" && string.Equals(m.Value, "True", StringComparison.OrdinalIgnoreCase));
    }

    [Theory(DisplayName = "L-AOT: no [ComImport] interface/class, no DllImport outside LibraryImport, no dynamic")]
    [MemberData(nameof(AotAssemblyNames))]
    [Trait("Category", "Architecture")]
    public void AotAssembly_UsesOnlySourceGeneratedInterop(string assemblyName)
    {
        var assembly = ByName(assemblyName);
        var types = assembly.GetTypes();

        Assert.Empty(types.Where(t => t.IsImport).Select(t => t.FullName));
        Assert.Empty(RuntimeMarshallingPInvokes(types));
        Assert.DoesNotContain("Microsoft.CSharp", ReferencedNames(assembly));
    }

    /// <summary>
    /// P/Invoke không đi qua LibraryImport: một phương thức pinvokeimpl phải hoặc chính là khai báo [LibraryImport] (generator
    /// chuyển thẳng khi chữ ký blittable), hoặc là hàm cục bộ "__PInvoke" mà generator sinh bên trong khai báo [LibraryImport].
    /// </summary>
    internal static IEnumerable<string> RuntimeMarshallingPInvokes(IEnumerable<Type> types) =>
        types.SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.Attributes.HasFlag(MethodAttributes.PinvokeImpl))
            .Where(m => !m.IsDefined(typeof(LibraryImportAttribute)) && !m.Name.Contains("__PInvoke", StringComparison.Ordinal))
            .Select(m => $"{m.DeclaringType?.FullName}.{m.Name}");

    private static class LegacyInteropSample
    {
        [DllImport("user32.dll")]
        internal static extern int GetDoubleClickTime();
    }

    [Fact(DisplayName = "L-AOT: the P/Invoke check flags a classic [DllImport] declaration")]
    [Trait("Category", "Architecture")]
    public void PInvokeCheck_FlagsDllImport()
    {
        Assert.Single(RuntimeMarshallingPInvokes([typeof(LegacyInteropSample)]));
        Assert.Empty(RuntimeMarshallingPInvokes([ShellInterop.GetType("PhotoReview.Shell.Interop.User32", throwOnError: true)!]));
    }
}
