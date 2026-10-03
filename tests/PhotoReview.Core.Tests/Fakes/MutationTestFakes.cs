using System.Reflection;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Core.Tests.Fakes;

/// <summary>Thread-safe <see cref="ILog"/> that records every message, so tests can assert what was (not) logged.</summary>
public sealed class MutationRecordingLog : ILog
{
    private readonly object _gate = new();
    private readonly List<string> _info = [];
    private readonly List<string> _warn = [];
    private readonly List<(string Message, Exception? Exception)> _errors = [];

    public bool Enabled => true;

    public IReadOnlyList<string> Infos { get { lock (_gate) return [.. _info]; } }
    public IReadOnlyList<string> Warnings { get { lock (_gate) return [.. _warn]; } }
    public IReadOnlyList<(string Message, Exception? Exception)> Errors { get { lock (_gate) return [.. _errors]; } }

    public void Info(string message) { lock (_gate) _info.Add(message); }
    public void Warn(string message) { lock (_gate) _warn.Add(message); }
    public void Error(string message, Exception? ex = null) { lock (_gate) _errors.Add((message, ex)); }
}

/// <summary>
/// Forwards every <see cref="IFileSystem"/> call to an inner file system, recording the call and optionally letting a test
/// replace the result (<see cref="Intercept"/>). For tests that must observe which calls a component makes, which the
/// in-memory fake cannot show (it creates directories itself, for instance).
/// </summary>
public class MutationFileSystemProxy : DispatchProxy
{
    private IFileSystem _inner = null!;
    private readonly List<string> _calls = [];
    private readonly object _gate = new();

    /// <summary>Called before the forward with (method name, arguments); a handled result short-circuits the call (it may also throw).</summary>
    public Func<string, object?[], InterceptResult>? Intercept { get; set; }

    public readonly record struct InterceptResult(bool Handled, object? Value);

    /// <summary>Method names in call order.</summary>
    public IReadOnlyList<string> Calls { get { lock (_gate) return [.. _calls]; } }

    public static IFileSystem Create(IFileSystem inner, out MutationFileSystemProxy handle)
    {
        var proxy = Create<IFileSystem, MutationFileSystemProxy>();
        handle = (MutationFileSystemProxy)(object)proxy;
        handle._inner = inner;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        args ??= [];
        lock (_gate) _calls.Add(targetMethod.Name);
        if (Intercept?.Invoke(targetMethod.Name, args) is { Handled: true } handled) return handled.Value;
        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}