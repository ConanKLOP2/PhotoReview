using System.Runtime.InteropServices;
using System.Xml.Linq;
using PhotoReview.Shell.Win32.Hosting;

namespace PhotoReview.Shell.Tests;

/// <summary>
/// WP-01 (NO-WPF-EXEC-PLAN, NE-2 = a): exe của shell tên PhotoReview, manifest nhúng khai báo DPI PerMonitorV2 và
/// comctl32 v6. Manifest được đọc lại từ tài nguyên Win32 RT_MANIFEST của assembly đã build (không đọc file nguồn).
/// </summary>
[Trait("Category", "HotPath")]
public sealed partial class ShellAssemblyTests
{
    private static readonly XNamespace AsmV1 = "urn:schemas-microsoft-com:asm.v1";
    private static readonly XNamespace WindowsSettings2016 = "http://schemas.microsoft.com/SMI/2016/WindowsSettings";

    [Fact]
    public void ShellAssembly_IsNamedPhotoReview()
    {
        Assert.Equal("PhotoReview", typeof(IShellWindow).Assembly.GetName().Name);
    }

    [Fact]
    public void EmbeddedManifest_DeclaresPerMonitorV2DpiAwareness()
    {
        var manifest = ReadEmbeddedManifest(typeof(IShellWindow).Assembly.Location);

        var dpiAwareness = manifest.Descendants(WindowsSettings2016 + "dpiAwareness").SingleOrDefault();
        Assert.NotNull(dpiAwareness);
        Assert.StartsWith("PerMonitorV2", dpiAwareness.Value.Trim(), StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedManifest_DependsOnCommonControlsV6()
    {
        var manifest = ReadEmbeddedManifest(typeof(IShellWindow).Assembly.Location);

        var commonControls = manifest.Descendants(AsmV1 + "dependentAssembly")
            .Elements(AsmV1 + "assemblyIdentity")
            .SingleOrDefault(e => (string?)e.Attribute("name") == "Microsoft.Windows.Common-Controls");
        Assert.NotNull(commonControls);
        Assert.Equal("6.0.0.0", (string?)commonControls.Attribute("version"));
    }

    private static XDocument ReadEmbeddedManifest(string modulePath)
    {
        var module = LoadLibraryEx(modulePath, 0, LoadLibraryAsDatafile | LoadLibraryAsImageResource);
        Assert.True(module != 0, $"LoadLibraryEx failed for {modulePath} (error {Marshal.GetLastPInvokeError()})");
        try
        {
            var resource = FindResource(module, CreateProcessManifestResourceId, RtManifest);
            Assert.True(resource != 0, $"{modulePath} has no RT_MANIFEST resource #1");
            var size = SizeofResource(module, resource);
            var data = LockResource(LoadResource(module, resource));
            Assert.True(size > 0 && data != 0, "RT_MANIFEST resource is empty");

            var bytes = new byte[size];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            using var stream = new MemoryStream(bytes);
            return XDocument.Load(stream);
        }
        finally
        {
            FreeLibrary(module);
        }
    }

    private const uint LoadLibraryAsDatafile = 0x00000002;
    private const uint LoadLibraryAsImageResource = 0x00000020;
    private const nint CreateProcessManifestResourceId = 1;
    private const nint RtManifest = 24;

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadLibraryEx(string fileName, nint reserved, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "FreeLibrary")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeLibrary(nint module);

    [LibraryImport("kernel32.dll", EntryPoint = "FindResourceW")]
    private static partial nint FindResource(nint module, nint name, nint type);

    [LibraryImport("kernel32.dll", EntryPoint = "SizeofResource")]
    private static partial int SizeofResource(nint module, nint resource);

    [LibraryImport("kernel32.dll", EntryPoint = "LoadResource")]
    private static partial nint LoadResource(nint module, nint resource);

    [LibraryImport("kernel32.dll", EntryPoint = "LockResource")]
    private static partial nint LockResource(nint resourceData);
}
