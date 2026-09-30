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

    public static TheoryData<string> Wrappers => ["CountingFileSystem", "SlowLinkFileSystem"];

    private static IFileSystem Wrap(string kind, IFileSystem inner) => kind switch
    {
        "CountingFileSystem" => new CountingFileSystem(inner, new ReviewMetrics()),
        _ => new SlowLinkFileSystem(inner, TimeSpan.Zero, bandwidth: null),
    };

    [Theory]
    [MemberData(nameof(Wrappers))]
    public void CreateNewPrimitives_AreForwardedToTheInnerFileSystem(string kind)
    {
        var inner = DispatchProxy.Create<IFileSystem, RecordingProxy>();
        var calls = ((RecordingProxy)(object)inner).Calls;
        var wrapper = Wrap(kind, inner);
        // Every current and future "TryXxxNew(source, destination)" primitive of the interface (TryCopyNew today).
        var primitives = typeof(IFileSystem).GetMethods()
            .Where(method => method.Name.StartsWith("Try", StringComparison.Ordinal) && method.Name.EndsWith("New", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(primitives);

        foreach (var primitive in primitives)
        {
            calls.Clear();

            primitive.Invoke(wrapper, [@"C:\a.jpg", @"C:\b.jpg"]);

            Assert.Equal([primitive.Name], calls); // exactly one forwarded call: no FileExists + Copy emulation behind the wrapper
        }
    }
}
