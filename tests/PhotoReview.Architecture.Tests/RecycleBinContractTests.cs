using System.Reflection;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Architecture.Tests;

/// <summary>
/// Audit B-03 (2026-10-10): <see cref="IRecycleBin"/> has "default for fakes" members (<c>CanRecycle</c> and <c>FitsInRecycleBin</c>
/// return true, <c>DeletePermanently</c> throws). A production implementation that forgets to override <c>CanRecycle</c> would
/// claim a Recycle Bin on EVERY drive (USB, NAS, UNC), and the shell would then delete permanently while the journal says
/// "recycle". Every non-test implementation must therefore declare all three members itself.
/// </summary>
public sealed class RecycleBinContractTests
{
    private static readonly string[] AssemblyNames =
    [
        "PhotoReview.Core", "PhotoReview.Platform.Windows", "PhotoReview.App", "PhotoReview.App.Shared",
        "PhotoReview.Shell.Win32", "PhotoReview.Shell.Rendering", "PhotoReview.Shell.Interop", "PhotoReview.Shell.WpfBridge",
        "PhotoReview.Imaging", "PhotoReview.Benchmarking",
    ];

    private static IEnumerable<Type> ProductionImplementations()
    {
        foreach (var name in AssemblyNames)
        {
            Assembly assembly;
            try { assembly = Assembly.Load(name); }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException) { continue; }

            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = [.. ex.Types.OfType<Type>()]; }

            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (typeof(IRecycleBin).IsAssignableFrom(type)) yield return type;
            }
        }
    }

    [Fact(DisplayName = "B-03: every production IRecycleBin overrides CanRecycle, FitsInRecycleBin and DeletePermanently")]
    [Trait("Category", "Architecture")]
    public void EveryProductionRecycleBin_DeclaresTheSafetyMembersItself()
    {
        var implementations = ProductionImplementations().ToList();
        Assert.NotEmpty(implementations); // WindowsRecycleBin at least: a rename must not turn this rule into a no-op

        var violations = new List<string>();
        foreach (var type in implementations)
        {
            var map = type.GetInterfaceMap(typeof(IRecycleBin));
            for (var i = 0; i < map.InterfaceMethods.Length; i++)
            {
                var member = map.InterfaceMethods[i].Name;
                if (member is not ("CanRecycle" or "FitsInRecycleBin" or "DeletePermanently")) continue;
                if (map.TargetMethods[i].DeclaringType == typeof(IRecycleBin))
                    violations.Add($"{type.FullName} relies on the IRecycleBin default for {member}");
            }
        }

        Assert.True(violations.Count == 0,
            "A production IRecycleBin must not inherit the 'default for fakes' behaviour (every drive would claim a Recycle Bin):\n"
            + string.Join("\n", violations));
    }

    [Fact(DisplayName = "B-03: the rule sees WindowsRecycleBin (guards against the scan silently finding nothing)")]
    [Trait("Category", "Architecture")]
    public void WindowsRecycleBin_IsAmongTheScannedImplementations() =>
        Assert.Contains(ProductionImplementations(), type => type.Name == "WindowsRecycleBin");
}
