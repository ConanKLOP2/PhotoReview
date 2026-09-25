using System.Reflection;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>FA-01: two journal instances (two processes) share one file; a reconcile must not overwrite a concurrent commit.</summary>
public sealed class JournalReconcileRaceTests
{
    private sealed class FakeClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    /// <summary>Forwards to the in-memory FS and runs a one-shot action when FileExists is first called (the barrier).</summary>
    public class InterceptingFs : DispatchProxy
    {
        public IFileSystem Inner { get; set; } = null!;
        public Action? OnFirstFileExistsForSource { get; set; }
        public string? Source { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IFileSystem.FileExists) && args![0] as string == Source && OnFirstFileExistsForSource is { } action)
            {
                OnFirstFileExistsForSource = null;
                action();
            }
            try { return targetMethod.Invoke(Inner, args); }
            catch (TargetInvocationException ex) { throw ex.InnerException!; }
        }
    }

    private static readonly DateTime Start = new(2026, 9, 25, 8, 0, 0, DateTimeKind.Utc);

    [Theory(DisplayName = "Reconcile does not mark Failed an operation another process committed meanwhile")]
    [InlineData(FileOperationType.Move)]
    [InlineData(FileOperationType.Copy)]
    public void Reconcile_ConcurrentCommit_NotOverwrittenByFailed(FileOperationType type)
    {
        var fs = new InMemoryFileSystem();
        var paths = new AppPaths(@"C:\Users\test\AppData\Local");
        var clock = new FakeClock(Start);
        var writer = new OperationJournal(paths, fs, clock);   // process A: owns the active operation
        var prepared = new JournalEntry("op1", type, JournalState.Prepared, @"C:\photos\a.jpg", @"C:\photos\sel\a.jpg", 5,
            Start.AddDays(-1), Start.AddMinutes(-1));
        writer.Append(prepared);

        var proxy = DispatchProxy.Create<IFileSystem, InterceptingFs>();
        var intercept = (InterceptingFs)(object)proxy;
        intercept.Inner = fs;
        intercept.Source = prepared.Source;
        // Process B has read op1 as Prepared and is judging it (source not yet inspected); now A finishes and commits.
        intercept.OnFirstFileExistsForSource = () => writer.Append(prepared with { State = JournalState.Committed, TimestampUtc = Start });
        var reconciler = new OperationJournal(paths, proxy, clock);

        var reconciled = reconciler.ReconcilePendingOperations();

        Assert.Empty(reconciled);
        Assert.Empty(reconciler.ReadFailedOperations());
        Assert.Empty(reconciler.ReadPendingOperations());
    }
}
