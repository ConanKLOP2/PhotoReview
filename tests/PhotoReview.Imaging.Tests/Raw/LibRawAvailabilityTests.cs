using System.IO;
using System.Runtime.InteropServices;
using PhotoReview.Imaging.LibRaw;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>LibRawAvailability must reject a wrong or stale native library up front instead of failing at decode time.</summary>
public sealed class LibRawAvailabilityTests
{
    [Fact]
    public void RequiredExports_CoverEveryImportedEntryPointAndTheVersionQuery()
    {
        var exports = LibRawAvailability.RequiredExports;

        Assert.Contains("libraw_open_wfile", exports);
        Assert.Contains("libraw_dcraw_make_mem_image", exports);
        Assert.Contains("libraw_version", exports);
        Assert.True(exports.Count >= 19, "expected all DllImport entry points, found " + exports.Count);
    }

    [Fact(DisplayName = "WP-34: LibRawNativeMethods.EntryPoints lists exactly the EntryPoint of every [LibraryImport] declared in the source")]
    public void EntryPoints_MatchTheLibraryImportDeclarations()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PhotoReview.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "src", "PhotoReview.Imaging.LibRaw", "LibRawNativeMethods.cs"));

        var declared = System.Text.RegularExpressions.Regex
            .Matches(source, "\\[LibraryImport\\(LibraryName, EntryPoint = \"(?<name>[a-z_0-9]+)\"")
            .Select(m => m.Groups["name"].Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(declared);
        Assert.Equal(declared, LibRawNativeMethods.EntryPoints.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(LibRawNativeMethods.EntryPoints.Length, LibRawNativeMethods.EntryPoints.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact(DisplayName = "I1: the LibRaw assembly restricts native loads to its own folder (no PATH / working-directory search)")]
    public void LibRawAssembly_DefaultDllImportSearchPath_IsAssemblyDirectoryOnly()
    {
        var attribute = typeof(LibRawAvailability).Assembly
            .GetCustomAttributes(typeof(DefaultDllImportSearchPathsAttribute), inherit: false)
            .Cast<DefaultDllImportSearchPathsAttribute>()
            .SingleOrDefault();

        Assert.NotNull(attribute);
        Assert.Equal(DllImportSearchPath.AssemblyDirectory, attribute!.Paths);
    }

    [Theory]
    [InlineData("0.22.2", true, 0, 22)]
    [InlineData("0.21.4-Release", true, 0, 21)]
    [InlineData("1.0", true, 1, 0)]
    [InlineData("0", false, 0, 0)]
    [InlineData("abc.def", false, 0, 0)]
    [InlineData("", false, 0, 0)]
    [InlineData(null, false, 0, 0)]
    public void TryParseVersion_ParsesMajorMinorOnly(string? text, bool ok, int major, int minor)
    {
        // "0.21.4-Release" has a non-numeric patch component only: major/minor still parse.
        Assert.Equal(ok, LibRawAvailability.TryParseVersion(text, out var parsedMajor, out var parsedMinor));
        if (ok) Assert.Equal((major, minor), (parsedMajor, parsedMinor));
    }

    [Fact]
    [Trait("Category", "Native")]
    public void ProbeLoadedLibrary_PinnedLibRaw_IsAvailable()
    {
        var handle = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "libraw.dll"));
        try
        {
            var (available, reason) = LibRawAvailability.ProbeLoadedLibrary(handle, "libraw.dll");

            Assert.True(available, reason);
            Assert.Null(reason);
        }
        finally { NativeLibrary.Free(handle); }
    }

    [Fact]
    [Trait("Category", "Native")]
    public void ProbeLoadedLibrary_LibraryWithoutLibRawExports_NamesTheMissingEntryPoints()
    {
        var handle = NativeLibrary.Load("kernel32.dll");
        try
        {
            var (available, reason) = LibRawAvailability.ProbeLoadedLibrary(handle, "kernel32.dll");

            Assert.False(available);
            Assert.Contains("libraw_open_wfile", reason, StringComparison.Ordinal);
            Assert.Contains("does not export", reason, StringComparison.Ordinal);
        }
        finally { NativeLibrary.Free(handle); }
    }
}
