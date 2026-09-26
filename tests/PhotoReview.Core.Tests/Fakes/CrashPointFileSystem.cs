using System.IO;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Core.Tests.Fakes;

/// <summary>
/// Fault-injecting decorator over <see cref="InMemoryFileSystem"/> for crash-at-every-step tests. Every mutating call
/// (CreateDirectory, Move, Copy, Delete, OpenAppend, WriteAllTextAtomic) is numbered. When
/// <see cref="CrashBeforeMutation"/> is N, mutation number N (1-based) and everything after it (reads included) throws
/// <see cref="IOException"/>: the process "died" right before that call, so the inner file system holds exactly the
/// state a real crash would leave. <see cref="TornAppendAtMutation"/> makes that append write only half of its bytes and
/// then fail (disk full / power loss mid-write). It fires once, on the first append whose mutation number is at least that value.
/// </summary>
public sealed class CrashPointFileSystem(InMemoryFileSystem inner) : IFileSystem
{
    private int _mutations;
    private bool _tornDone;

    public InMemoryFileSystem Inner { get; } = inner;
    public int CrashBeforeMutation { get; set; } = int.MaxValue;
    public int TornAppendAtMutation { get; set; } = -1;

    /// <summary>Number of mutating calls attempted so far (the crashed one included).</summary>
    public int Mutations => Volatile.Read(ref _mutations);

    public bool Crashed => Mutations >= CrashBeforeMutation;

    /// <summary>Every mutating call attempted, as "kind|path" (destination paths of Move/Copy included as "kind|source>destination").</summary>
    public List<string> Log { get; } = [];

    private void Mutate(string kind, string path)
    {
        lock (Log) Log.Add(kind + "|" + path);
        var n = Interlocked.Increment(ref _mutations);
        if (n >= CrashBeforeMutation) throw new IOException("simulated crash");
    }

    private void Read()
    {
        if (Crashed) throw new IOException("simulated crash");
    }

    public bool FileExists(string path) { Read(); return Inner.FileExists(path); }
    public bool DirectoryExists(string path) { Read(); return Inner.DirectoryExists(path); }
    public FileStat? GetFileStat(string path) { Read(); return Inner.GetFileStat(path); }
    public void Move(string source, string destination) { Mutate("move", source + ">" + destination); Inner.Move(source, destination); }
    public void Copy(string source, string destination) { Mutate("copy", source + ">" + destination); Inner.Copy(source, destination); }
    public void Delete(string path) { Mutate("delete", path); Inner.Delete(path); }
    public void CreateDirectory(string path) { Mutate("mkdir", path); Inner.CreateDirectory(path); }
    public void WriteAllTextAtomic(string path, string text, bool durable = true) { Mutate("write", path); Inner.WriteAllTextAtomic(path, text, durable); }
    public Stream OpenReadShared(string path, int bufferSize = 65536) { Read(); return Inner.OpenReadShared(path, bufferSize); }
    public string ReadAllText(string path) { Read(); return Inner.ReadAllText(path); }
    public IEnumerable<string> ReadLines(string path) { Read(); return Inner.ReadLines(path); }
    public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*") { Read(); return Inner.EnumerateFiles(directory, pattern); }
    public IEnumerable<string> EnumerateDirectories(string directory) { Read(); return Inner.EnumerateDirectories(directory); }
    public Stream OpenAppendDurable(string path) => OpenAppend(path, durable: true);

    public Stream OpenAppend(string path, bool durable)
    {
        Mutate("append", path);
        var stream = Inner.OpenAppend(path, durable);
        if (_tornDone || TornAppendAtMutation < 0 || Mutations < TornAppendAtMutation) return stream;
        _tornDone = true;
        return new TornStream(stream);
    }

    /// <summary>Writes the first half of the buffer to the inner stream, then fails like a full disk.</summary>
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
            inner.Flush(); // the torn half reaches the disk
            throw new IOException("There is not enough space on the disk.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
