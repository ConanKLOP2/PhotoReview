using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Core.IO;

/// <summary>
/// Windows implementation of <see cref="IJournalCompactionFiles"/>. The replacement is renamed with
/// <c>SetFileInformationByHandle(FileRenameInfoEx, REPLACE_IF_EXISTS | POSIX_SEMANTICS)</c>: one atomic NTFS rename that,
/// unlike <see cref="File.Move(string, string, bool)"/> (MoveFileEx), succeeds while the target is open with delete
/// sharing (the compaction lock and the journal readers) and, unlike <see cref="File.Replace(string, string, string?)"/>,
/// has no multi-step failure mode that can leave the target name missing. Needs Windows 10 1709+ on NTFS; anywhere else
/// the rename fails, the target is untouched and compaction is simply skipped.
/// </summary>
public sealed class PhysicalJournalCompactionFiles : IJournalCompactionFiles
{
    public Stream OpenReadDenyWriters(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
    }

    public IStagedReplacement CreateStagedReplacement(string tempPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempPath);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Journal compaction needs the Windows rename primitive.");
        var fullPath = Path.GetFullPath(tempPath);
        var handle = NativeMethods.CreateFileW(fullPath, NativeMethods.GenericWrite | NativeMethods.Delete, NativeMethods.FileShareRead,
            IntPtr.Zero, NativeMethods.CreateNew, NativeMethods.FileAttributeNormal, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetHRForLastWin32Error();
            handle.Dispose();
            throw new IOException("Could not create the journal compaction file " + fullPath, error);
        }
        return new StagedReplacement(fullPath, handle);
    }

    private sealed class StagedReplacement(string path, SafeFileHandle handle) : IStagedReplacement
    {
        private readonly FileStream _stream = new(handle, FileAccess.Write, 64 * 1024);
        private bool _replaced;
        private bool _disposed;

        public void Write(ReadOnlySpan<byte> bytes) => _stream.Write(bytes);

        public void FlushToDisk() => _stream.Flush(flushToDisk: true);

        public void ReplaceAtomically(string destination)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(destination);
            ObjectDisposedException.ThrowIf(_disposed, this);
            _stream.Flush(flushToDisk: true);
            var name = Path.GetFullPath(destination);
            // FILE_RENAME_INFO: { union { BOOLEAN ReplaceIfExists; DWORD Flags; }; HANDLE RootDirectory; DWORD FileNameLength; WCHAR FileName[1]; }
            var rootOffset = IntPtr.Size; // the DWORD union is padded to pointer alignment
            var lengthOffset = rootOffset + IntPtr.Size;
            var nameOffset = lengthOffset + sizeof(int);
            var nameBytes = name.Length * sizeof(char);
            var size = nameOffset + nameBytes + sizeof(char);
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                for (var i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
                Marshal.WriteInt32(buffer, 0, NativeMethods.FileRenameFlagReplaceIfExists | NativeMethods.FileRenameFlagPosixSemantics);
                Marshal.WriteIntPtr(buffer, rootOffset, IntPtr.Zero);
                Marshal.WriteInt32(buffer, lengthOffset, nameBytes);
                Marshal.Copy(name.ToCharArray(), 0, buffer + nameOffset, name.Length);
                if (!NativeMethods.SetFileInformationByHandle(_stream.SafeFileHandle, NativeMethods.FileRenameInfoEx, buffer, (uint)size))
                    throw new IOException("Could not replace " + name + " with the compacted journal", Marshal.GetHRForLastWin32Error());
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            _replaced = true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _stream.Dispose();
            }
            catch (IOException)
            {
                // A failed final flush of a replacement that is deleted below anyway.
            }
            if (_replaced) return;
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left behind: the next compaction removes stale replacement files.
            }
        }
    }

    private static class NativeMethods
    {
        internal const uint GenericWrite = 0x40000000;
        internal const uint Delete = 0x00010000;
        internal const uint FileShareRead = 0x1;
        internal const uint CreateNew = 1;
        internal const uint FileAttributeNormal = 0x80;
        internal const int FileRenameInfoEx = 22;
        internal const int FileRenameFlagReplaceIfExists = 0x1;
        internal const int FileRenameFlagPosixSemantics = 0x2;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFileInformationByHandle(SafeFileHandle file, int fileInformationClass, IntPtr information, uint bufferSize);
    }
}
