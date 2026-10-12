using System.Reflection;
using System.Text.RegularExpressions;

namespace PhotoReview.Architecture.Tests;

/// <summary>
/// NO-WPF-EXEC-PLAN WP-34 (L-AOT): every project that ends up in PhotoReview.exe is built with <c>IsAotCompatible</c> (the IL2xxx/IL3xxx
/// analyzers fail the Release build), declares P/Invoke only through <c>[LibraryImport]</c> (classic <c>[DllImport]</c> is banned
/// in src/ altogether), and does not use reflection-style APIs the AOT compiler cannot see through. The authoritative whole-graph
/// gate is the AOT publish counted by tools/check-aot-warnings.ps1 (needs MSVC, so it is not a unit test).
/// </summary>
public sealed class AotCompatibilityRulesTests
{
    /// <summary>Projects that ship inside the AOT executable (the WPF side - App, App.WpfWindows, Imaging.Wpf, Shell.WpfBridge - cannot be AOT).</summary>
    public static readonly string[] AotProjects =
    [
        "PhotoReview.Core", "PhotoReview.Imaging", "PhotoReview.Imaging.Raw", "PhotoReview.Imaging.LibRaw", "PhotoReview.Imaging.TurboJpeg",
        "PhotoReview.Platform.Windows", "PhotoReview.App.Shared", "PhotoReview.Shell.Interop", "PhotoReview.Shell.Rendering", "PhotoReview.Shell.Win32",
    ];

    public static TheoryData<string> AotProjectNames => [.. AotProjects];

    [Fact(DisplayName = "L-AOT: the checked project list covers every project that ships in PhotoReview.exe and none of the WPF side")]
    [Trait("Category", "Architecture")]
    public void AotProjectList_CoversTheExecutableGraph()
    {
        string[] required =
        [
            "PhotoReview.Core", "PhotoReview.Imaging", "PhotoReview.Imaging.Raw", "PhotoReview.Imaging.LibRaw", "PhotoReview.Imaging.TurboJpeg",
            "PhotoReview.Platform.Windows", "PhotoReview.App.Shared", "PhotoReview.Shell.Interop", "PhotoReview.Shell.Rendering", "PhotoReview.Shell.Win32",
        ];

        Assert.Equal(required.Order(StringComparer.Ordinal), AotProjects.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(AotProjects, p => p is "PhotoReview.App" or "PhotoReview.App.WpfWindows" or "PhotoReview.Imaging.Wpf" or "PhotoReview.Shell.WpfBridge");
    }

    // ---- IsAotCompatible is on ----------------------------------------------------------------------------------------------

    [Theory(DisplayName = "L-AOT: the project enables IsAotCompatible (trim + AOT analyzers on)")]
    [MemberData(nameof(AotProjectNames))]
    [Trait("Category", "Architecture")]
    public void AotProject_EnablesIsAotCompatible(string project)
    {
        var text = File.ReadAllText(Path.Combine(RepoScan.Root, "src", project, project + ".csproj"));

        Assert.True(IsAotCompatibleEnabled(text), project + " must set <IsAotCompatible>true</IsAotCompatible>.");
    }

    internal static bool IsAotCompatibleEnabled(string csprojText) =>
        Regex.IsMatch(csprojText, @"<IsAotCompatible>\s*true\s*</IsAotCompatible>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact(DisplayName = "L-AOT: the IsAotCompatible check recognises only an enabled property")]
    [Trait("Category", "Architecture")]
    public void IsAotCompatibleCheck_RejectsDisabledAndMissing()
    {
        Assert.True(IsAotCompatibleEnabled("<PropertyGroup><IsAotCompatible>true</IsAotCompatible></PropertyGroup>"));
        Assert.False(IsAotCompatibleEnabled("<PropertyGroup><IsAotCompatible>false</IsAotCompatible></PropertyGroup>"));
        Assert.False(IsAotCompatibleEnabled("<PropertyGroup><IsTrimmable>true</IsTrimmable></PropertyGroup>"));
    }

    // ---- no runtime-marshalled P/Invoke, no ComImport (built assemblies) --------------------------------------------------------

    [Theory(DisplayName = "L-AOT: the built assembly has no [ComImport] type, no P/Invoke outside LibraryImport and no Microsoft.CSharp (dynamic)")]
    [MemberData(nameof(AotProjectNames))]
    [Trait("Category", "Architecture")]
    public void AotAssembly_HasOnlySourceGeneratedInterop(string project)
    {
        var assembly = Assembly.Load(project == "PhotoReview.Shell.Win32" ? "PhotoReview" : project); // PhotoReview.exe is the Shell.Win32 project
        var types = assembly.GetTypes();

        Assert.Empty(types.Where(t => t.IsImport).Select(t => t.FullName));
        Assert.Empty(ShellRulesTests.RuntimeMarshallingPInvokes(types));
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), r => r.Name == "Microsoft.CSharp");
    }

    // ---- source rules -------------------------------------------------------------------------------------------------------

    private static readonly Regex ClassicDllImport = new(
        @"\[\s*(?:global::)?(?:System\.Runtime\.InteropServices\.)?DllImport(?:Attribute)?\s*\(",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static string Code(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*') || trimmed.StartsWith("/*", StringComparison.Ordinal)) return "";
        var comment = line.IndexOf("//", StringComparison.Ordinal);
        return comment < 0 ? line : line[..comment];
    }

    internal static bool IsClassicDllImport(string line) => ClassicDllImport.IsMatch(Code(line));

    [Fact(DisplayName = "L-AOT: no classic [DllImport] anywhere in src/ - P/Invoke is [LibraryImport] only (source-generated, no IL stub at run time)")]
    [Trait("Category", "Architecture")]
    public void Src_HasNoClassicDllImport()
    {
        var violations = RepoScan.FindLineViolations(IsClassicDllImport, skipFile: null, "src");

        Assert.True(violations.Count == 0, "Use [LibraryImport] (partial method; blittable or StringMarshalling.Utf16):\n" + string.Join("\n", violations));
    }

    [Fact(DisplayName = "L-AOT: the DllImport check flags the attribute in its spellings and ignores comments and DefaultDllImportSearchPaths")]
    [Trait("Category", "Architecture")]
    public void DllImportCheck_FlagsAttributeOnly()
    {
        Assert.True(IsClassicDllImport("    [DllImport(\"user32.dll\")]"));
        Assert.True(IsClassicDllImport("[System.Runtime.InteropServices.DllImport(\"a.dll\", SetLastError = true)]"));
        Assert.True(IsClassicDllImport("[DllImportAttribute(\"a.dll\")]"));
        Assert.False(IsClassicDllImport("[LibraryImport(\"user32.dll\")]"));
        Assert.False(IsClassicDllImport("[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]"));
        Assert.False(IsClassicDllImport("    // was [DllImport(\"user32.dll\")] before WP-34"));
        Assert.False(IsClassicDllImport("    /// <c>[DllImport(\"x\")]</c>"));
        Assert.False(IsClassicDllImport("     * [DllImport(\"x\")] inside a block comment"));
        Assert.False(IsClassicDllImport("/* [DllImport(\"x\")] */"));
    }

    private static readonly (string Name, Regex Pattern)[] UnsafeAotApis =
    [
        ("Activator.CreateInstance", new Regex(@"\bActivator\s*\.\s*CreateInstance\b", RegexOptions.CultureInvariant)),
        ("Assembly.Load*", new Regex(@"\bAssembly\s*\.\s*(Load|LoadFrom|LoadFile|ReflectionOnlyLoad)\w*\s*\(", RegexOptions.CultureInvariant)),
        ("Type.GetType(string)", new Regex(@"\bType\s*\.\s*GetType\s*\(", RegexOptions.CultureInvariant)),
        ("Type.GetTypeFromProgID/CLSID", new Regex(@"\bType\s*\.\s*GetTypeFrom(ProgID|CLSID)\b", RegexOptions.CultureInvariant)),
        ("MakeGenericType/MakeGenericMethod", new Regex(@"\.\s*MakeGeneric(Type|Method)\s*\(", RegexOptions.CultureInvariant)),
        ("Expression.Compile", new Regex(@"\bExpression\s*\.\s*Lambda\b|\bExpression<", RegexOptions.CultureInvariant)),
        ("dynamic", new Regex(@"(?<![\w.""])dynamic\s+\w|\(dynamic\)|<dynamic>", RegexOptions.CultureInvariant)),
        ("Enum.Parse(Type, ...)", new Regex(@"\bEnum\s*\.\s*(Parse|TryParse|GetValues|GetNames)\s*\(\s*typeof\b", RegexOptions.CultureInvariant)),
        ("Marshal.GetDelegateForFunctionPointer", new Regex(@"\bMarshal\s*\.\s*GetDelegateForFunctionPointer\b", RegexOptions.CultureInvariant)),
        ("Marshal.GetObjectForIUnknown", new Regex(@"\bMarshal\s*\.\s*(GetObjectForIUnknown|GetTypedObjectForIUnknown|GetComInterfaceForObject)\b", RegexOptions.CultureInvariant)),
        ("Marshal.PtrToStructure/StructureToPtr(Type)", new Regex(@"\bMarshal\s*\.\s*PtrToStructure\s*\(\s*[^,)]+,\s*typeof\b", RegexOptions.CultureInvariant)),
        ("MethodInfo.Invoke / InvokeMember", new Regex(@"\.\s*(InvokeMember)\s*\(", RegexOptions.CultureInvariant)),
        ("JsonSerializer without a source-generated context", new Regex(@"\bJsonSerializer\s*\.\s*(Serialize|Deserialize)(Async)?\s*(<[^>]*>)?\s*\((?![^;]*(JsonContext|TypeInfo|\.Default|Context\b|Info\b))", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)),
    ];

    internal static IEnumerable<string> UnsafeAotApisIn(string line)
    {
        var code = Code(line);
        if (code.Length == 0) yield break;
        foreach (var (name, pattern) in UnsafeAotApis)
        {
            if (pattern.IsMatch(code)) yield return name;
        }
    }

    [Fact(DisplayName = "L-AOT: AOT projects use no reflection-style API ILC cannot analyse (Activator, Assembly.Load, Type.GetType, MakeGeneric*, dynamic, non-source-gen JSON)")]
    [Trait("Category", "Architecture")]
    public void AotProjects_AvoidUnsafeAotApis()
    {
        var violations = new List<string>();
        foreach (var project in AotProjects)
        {
            violations.AddRange(RepoScan.FindLineViolations(l => UnsafeAotApisIn(l).Any(), skipFile: null, "src/" + project));
        }

        Assert.True(violations.Count == 0,
            "Replace with a static choice, a source generator or [DynamicallyAccessedMembers] (and re-run tools/check-aot-warnings.ps1):\n" +
            string.Join("\n", violations));
    }

    [Theory(DisplayName = "L-AOT: the unsafe-API check flags each banned call and passes the AOT-safe equivalent")]
    [InlineData("var o = Activator.CreateInstance(type);", "Activator.CreateInstance")]
    [InlineData("var a = Assembly.LoadFrom(path);", "Assembly.Load*")]
    [InlineData("var t = Type.GetType(\"X\");", "Type.GetType(string)")]
    [InlineData("var t = Type.GetTypeFromProgID(\"Shell.Application\");", "Type.GetTypeFromProgID/CLSID")]
    [InlineData("var g = open.MakeGenericType(arg);", "MakeGenericType/MakeGenericMethod")]
    [InlineData("Expression<Func<int>> e = () => 1;", "Expression.Compile")]
    [InlineData("dynamic shell = Create();", "dynamic")]
    [InlineData("var v = Enum.Parse(typeof(Kind), text);", "Enum.Parse(Type, ...)")]
    [InlineData("var d = Marshal.GetDelegateForFunctionPointer(p, t);", "Marshal.GetDelegateForFunctionPointer")]
    [InlineData("var o = Marshal.GetObjectForIUnknown(p);", "Marshal.GetObjectForIUnknown")]
    [InlineData("var s = Marshal.PtrToStructure(p, typeof(Info));", "Marshal.PtrToStructure/StructureToPtr(Type)")]
    [InlineData("var json = JsonSerializer.Serialize(state);", "JsonSerializer without a source-generated context")]
    [InlineData("var x = JsonSerializer.Deserialize<AppSettings>(json);", "JsonSerializer without a source-generated context")]
    [Trait("Category", "Architecture")]
    public void UnsafeAotApiCheck_FlagsBannedCalls(string line, string expected)
    {
        Assert.Contains(expected, UnsafeAotApisIn(line));
    }

    [Theory(DisplayName = "L-AOT: the unsafe-API check passes source-generated JSON, generic Enum helpers, comments and identifiers that merely contain the words")]
    [InlineData("var s = JsonSerializer.Serialize(state, SessionJsonContext.Default.SessionState);")]
    [InlineData("var s = JsonSerializer.Deserialize(json, info);")]
    [InlineData("var name = Enum.GetNames<PreviewState>();")]
    [InlineData("var v = Enum.Parse<PreviewState>(name);")]
    [InlineData("// Activator.CreateInstance and dynamic are banned here")]
    [InlineData("/// <see cref=\"Type.GetType(string)\"/>")]
    [InlineData("var dynamicRange = 12;")]
    [InlineData("var name = \"dynamic range\";")]
    [Trait("Category", "Architecture")]
    public void UnsafeAotApiCheck_PassesSafeCode(string line)
    {
        Assert.Empty(UnsafeAotApisIn(line));
    }

    // ---- the whole-graph gate script ------------------------------------------------------------------------------------------

    [Fact(DisplayName = "L-AOT: tools/check-aot-warnings.ps1 publishes the shell with ShellAot, allows 0 warnings and finds MSVC through vswhere")]
    [Trait("Category", "Architecture")]
    public void AotGateScript_KeepsItsContract()
    {
        var text = File.ReadAllText(Path.Combine(RepoScan.Root, "tools", "check-aot-warnings.ps1"));

        Assert.Contains("-p:ShellAot=true", text, StringComparison.Ordinal);
        Assert.Contains("[int]$MaxWarnings = 0", text, StringComparison.Ordinal);
        Assert.Contains("vswhere", text, StringComparison.Ordinal);
        Assert.Contains("Microsoft Visual Studio\\Installer", text, StringComparison.Ordinal);
        Assert.Contains("/nodeReuse:false", text, StringComparison.Ordinal);
    }
}
