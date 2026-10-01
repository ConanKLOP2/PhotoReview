using System.IO;
using System.Reflection;
using PhotoReview.Benchmarking;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.IO;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Ownership proofs (a "create-new" primitive that reports whether THIS call created the destination) only hold when every
/// <see cref="IFileSystem"/> decorator forwards the primitive to its inner file system: the interface default (exists-check
/// then Copy) is not atomic and would silently weaken the guarantee behind a wrapper.
/// </summary>
public sealed class FileSystemWrapperForwardingTests
{
    /// <summary>Records every interface member invoked on it; answers true/default so a wrapper can complete the call.</summary>
    public class RecordingProxy : DispatchProxy
    {
        public List<string> Calls { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls.Add(targetMethod!.Name);
            return targetMethod.ReturnType == typeof(bool) ? true
                : targetMethod.ReturnType.IsValueType && targetMethod.ReturnType != typeof(void) ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
        }
    }

    // The real file systems, not decorators in the forwarding sense: they implement the primitive themselves.
    private static readonly HashSet<string> NotDecorators = new(StringComparer.Ordinal)
    {
        "PhysicalFileSystem", "InMemoryFileSystem",
    };

    /// <summary>
    /// Every product (non-test) PhotoReview assembly found in the test output directory, loaded: not only the assemblies this
    /// test project references, so a decorator living in an otherwise unreferenced product assembly is found too.
    /// </summary>
    private static List<Assembly> ProductAssemblies()
    {
        var result = new List<Assembly>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "PhotoReview*.dll"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.EndsWith(".Tests", StringComparison.Ordinal) || name.StartsWith("PhotoReview.TestSupport", StringComparison.Ordinal)
                || !seen.Add(name)) continue;
            try { result.Add(Assembly.LoadFrom(file)); }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException) { continue; } // native/non-managed PhotoReview*.dll
        }

        return result;
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.OfType<Type>(); }
    }

    /// <summary>
    /// Concrete IFileSystem implementations that take another IFileSystem in a constructor (decorators), product assemblies only.
    /// Only a constructor parameter of exactly type <see cref="IFileSystem"/> counts: a decorator built through a factory
    /// (<c>Func&lt;IFileSystem&gt;</c>, a provider, a static Wrap method) is out of scope of this discovery and needs its own proof.
    /// </summary>
    private static List<(Type Type, ConstructorInfo Constructor)> DiscoverDecorators() =>
        ProductAssemblies()
            .SelectMany(SafeTypes)
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IFileSystem).IsAssignableFrom(type) && !NotDecorators.Contains(type.Name))
            .Select(type => (Type: type, Constructor: type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(ctor => ctor.GetParameters().Any(p => p.ParameterType == typeof(IFileSystem)))))
            .Where(entry => entry.Constructor is not null)
            .Select(entry => (entry.Type, entry.Constructor!))
            .ToList();

    private static object Create(ConstructorInfo constructor, IFileSystem inner)
    {
        var arguments = constructor.GetParameters().Select(parameter =>
            parameter.ParameterType == typeof(IFileSystem) ? inner
            : parameter.HasDefaultValue ? parameter.DefaultValue
            : parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType)
            : parameter.ParameterType.GetConstructor(Type.EmptyTypes) is not null ? Activator.CreateInstance(parameter.ParameterType)
            : null).ToArray();
        return constructor.Invoke(arguments);
    }

    [Fact]
    public void EveryFileSystemDecorator_ForwardsTheCreateNewPrimitivesToTheInnerFileSystem()
    {
        var decorators = DiscoverDecorators();
        // Sanity: discovery must find the decorators known today, or the reflection (not the decorators) is broken.
        Assert.True(decorators.Count >= 2, $"expected at least 2 IFileSystem decorators, found {decorators.Count}: discovery is broken");
        Assert.Contains(decorators, entry => entry.Type == typeof(CountingFileSystem));
        Assert.Contains(decorators, entry => entry.Type == typeof(SlowLinkFileSystem));
        // Every current and future "TryXxxNew(source, destination)" primitive of the interface (TryCopyNew today).
        var primitives = typeof(IFileSystem).GetMethods()
            .Where(method => method.Name.StartsWith("Try", StringComparison.Ordinal) && method.Name.EndsWith("New", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(primitives);

        var failures = new List<string>();
        foreach (var (type, constructor) in decorators)
        {
            var inner = DispatchProxy.Create<IFileSystem, RecordingProxy>();
            var calls = ((RecordingProxy)(object)inner).Calls;
            var wrapper = Create(constructor, inner);
            foreach (var primitive in primitives)
            {
                calls.Clear();
                primitive.Invoke(wrapper, [@"C:\a.jpg", @"C:\b.jpg"]);
                // Exactly one forwarded call: no FileExists + Copy emulation behind the wrapper.
                if (!calls.SequenceEqual([primitive.Name])) failures.Add($"{type.FullName}.{primitive.Name} -> inner calls [{string.Join(", ", calls)}]");
            }
        }

        Assert.True(failures.Count == 0, "IFileSystem decorators that do not forward the create-new primitive to their inner file system: " + string.Join("; ", failures));
    }
}
