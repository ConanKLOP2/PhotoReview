using PhotoReview.Core.Abstractions;

namespace PhotoReview.Core.Tests.Fakes;

/// <summary>What is injected at the chosen mutating call (see <see cref="FaultInjectionFileSystem"/>).</summary>
public enum FaultKind
{
    /// <summary>The process dies right before the call: no effect, and every later call (reads too) throws.</summary>
    CrashBefore,

    /// <summary>The call takes full effect, then the process dies before it can see the result.</summary>
    CrashAfter,

    /// <summary>Copy/Move: a TRUNCATED destination is left (a copy cut short; the source is untouched), then the process dies. Append: half of the bytes reach the file, then the process dies.</summary>
    CrashPartial,

    /// <summary>Move only: the destination is complete but the source was not removed (cross-volume copy done, delete not reached), then the process dies.</summary>
    CrashMoveCopiedNotDeleted,

    /// <summary>The call fails with an <see cref="IOException"/> and has no effect; the process lives on (transient error).</summary>
    FailOnce,

    /// <summary>Like <see cref="CrashPartial"/> but the process survives (disk full mid-copy / mid-append).</summary>
    FailPartial,
}

/// <summary>A fault at mutating call number <see cref="At"/> (1-based, counted since <see cref="FaultInjectionFileSystem.Arm"/>).</summary>
public sealed record FaultPlan(int At, FaultKind Kind);

/// <summary>Thrown by every call of a "dead" process; the in-memory state of the caller is abandoned, only the disk survives.</summary>
public sealed class ProcessCrashedException() : IOException("simulated process death");

/// <summary>
/// Reusable fault injector for crash-consistency matrices. Wraps an <see cref="InMemoryFileSystem"/> ("the disk") and numbers every
/// MUTATING call (CreateDirectory, Move, Copy/TryCopyNew, Delete, OpenAppend, WriteAllTextAtomic, plus the external steps a fake
/// Recycle Bin reports through <see cref="Mutate"/>). One <see cref="FaultPlan"/> picks the call and what happens there. A crash kind
/// kills the "process": that call and every later one throw <see cref="ProcessCrashedException"/>, so whatever the code under test
/// does in its catch blocks cannot change the disk any more; a restart is simply a new journal/service over <see cref="Disk"/>.
/// A fail kind throws once and the process continues. Not thread-safe for concurrent mutations (the scenarios are sequential).
/// </summary>
public sealed class FaultInjectionFileSystem(InMemoryFileSystem disk) : IFileSystem
{
    private int _count;
    private FaultPlan? _plan;

    public InMemoryFileSystem Disk { get; } = disk;

    /// <summary>True once a crash kind fired: the process is dead.</summary>
    public bool Dead { get; private set; }

    /// <summary>Kinds of the mutating calls since <see cref="Arm"/>, in call order (1-based position = injection point).</summary>
    public List<string> Kinds { get; } = [];

    public int Mutations => _count;

    /// <summary>Starts counting from zero and installs the plan (null = healthy run, only counting).</summary>
    public void Arm(FaultPlan? plan)
    {
        _plan = plan;
        _count = 0;
        Kinds.Clear();
        Dead = false;
    }

    /// <summary>Whether <paramref name="kind"/> can be injected at a call of <paramref name="mutationKind"/> (a Move-only fault at a Delete is meaningless).</summary>
    public static bool Applies(FaultKind kind, string mutationKind) => kind switch
    {
        FaultKind.CrashPartial or FaultKind.FailPartial => mutationKind is "copy" or "move" or "append",
        FaultKind.CrashMoveCopiedNotDeleted => mutationKind == "move",
        // A crash after an append equals a crash before the next call (nothing is flushed by the call itself), so it adds no state.
        FaultKind.CrashAfter => mutationKind != "append",
        _ => true,
    };

    private void ThrowIfDead()
    {
        if (Dead) throw new ProcessCrashedException();
    }

    /// <summary>
    /// Runs one numbered mutation. <paramref name="effect"/> is the full effect; <paramref name="partial"/> is what a cut-short call
    /// leaves behind (null = not injectable as partial). The plan decides which of them happens and whether the process dies.
    /// </summary>
    public void Mutate(string kind, Action effect, Action? partial = null, Action? copiedNotDeleted = null)
    {
        ThrowIfDead();
        Kinds.Add(kind);
        var n = ++_count;
        if (_plan is not { } plan || plan.At != n)
        {
            effect();
            return;
        }

        switch (plan.Kind)
        {
            case FaultKind.CrashBefore:
                Dead = true;
                throw new ProcessCrashedException();
            case FaultKind.FailOnce:
                throw new IOException("simulated transient failure");
            case FaultKind.CrashAfter:
                effect();
                Dead = true;
                throw new ProcessCrashedException();
            case FaultKind.CrashPartial:
                (partial ?? effect)();
                Dead = true;
                throw new ProcessCrashedException();
            case FaultKind.FailPartial:
                (partial ?? effect)();
                throw new IOException("There is not enough space on the disk.");
            case FaultKind.CrashMoveCopiedNotDeleted:
                (copiedNotDeleted ?? effect)();
                Dead = true;
                throw new ProcessCrashedException();
            default:
                effect();
                break;
        }
    }

    public bool FileExists(string path) { ThrowIfDead(); return Disk.FileExists(path); }
    public bool DirectoryExists(string path) { ThrowIfDead(); return Disk.DirectoryExists(path); }
    public FileStat? GetFileStat(string path) { ThrowIfDead(); return Disk.GetFileStat(path); }
    public Stream OpenReadShared(string path, int bufferSize = 65536) { ThrowIfDead(); return Disk.OpenReadShared(path, bufferSize); }
    public string ReadAllText(string path) { ThrowIfDead(); return Disk.ReadAllText(path); }
    public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*") { ThrowIfDead(); return Disk.EnumerateFiles(directory, pattern); }
    public IEnumerable<string> EnumerateDirectories(string directory) { ThrowIfDead(); return Disk.EnumerateDirectories(directory); }

    public void CreateDirectory(string path) => Mutate("mkdir", () => Disk.CreateDirectory(path));
    public bool TryDeleteEmptyDirectory(string path) => Disk.TryDeleteEmptyDirectory(path);

    public void WriteAllTextAtomic(string path, string text, bool durable = true) =>
        Mutate("write", () => Disk.WriteAllTextAtomic(path, text, durable));

    public void Delete(string path) => Mutate("delete", () => Disk.Delete(path));

    public void Move(string source, string destination)
    {
        // Cut short: a truncated destination appears while the source is untouched (copy phase of a cross-volume move).
        Mutate("move", () => Disk.Move(source, destination),
            partial: () => Truncated(source, destination),
            copiedNotDeleted: () => { Disk.Copy(source, destination); });
    }

    public void Copy(string source, string destination) =>
        Mutate("copy", () => Disk.Copy(source, destination), partial: () => Truncated(source, destination));

    public bool TryCopyNew(string source, string destination, CopyCreationProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        var created = false;
        Mutate("copy",
            () => created = Disk.TryCopyNew(source, destination, proof),
            partial: () => Truncated(source, destination)); // unclaimed, like PhysicalFileSystem: a throw gives no creation proof
        return created;
    }

    private void Truncated(string source, string destination)
    {
        // The destination folder must exist for a real copy to start; if it does not, nothing is created.
        if (Disk.FileExists(destination) || !Disk.FileExists(source)) return;
        var stat = Disk.GetFileStat(source)!;
        var text = Disk.ReadAllText(source);
        Disk.AddFile(destination, text[..(text.Length / 2)], stat.LastWriteUtc);
    }

    public Stream OpenAppend(string path, bool durable)
    {
        Stream? stream = null;
        var torn = false;
        Mutate("append", () => stream = Disk.OpenAppend(path, durable), partial: () =>
        {
            stream = Disk.OpenAppend(path, durable);
            torn = true;
        });
        return torn ? new TornStream(stream!) : stream!;
    }

    /// <summary>Writes the first half of the buffer, flushes it to the "disk", then fails like a full disk / power loss.</summary>
    private sealed class TornStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count / 2);
            inner.Flush();
            throw new IOException("There is not enough space on the disk.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>All files below the given folders (non-recursive per folder), as path -> content; for state snapshots.</summary>
    public Dictionary<string, string> Snapshot(IEnumerable<string> folders)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            if (!Disk.DirectoryExists(folder)) continue;
            foreach (var file in Disk.EnumerateFiles(folder)) result[file] = Disk.ReadAllText(file);
        }

        return result;
    }
}
