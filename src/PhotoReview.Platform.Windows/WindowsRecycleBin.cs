using System.Collections;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualBasic.FileIO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Platform.Windows;

/// <summary>
/// Triển khai IRecycleBin cho Windows qua Microsoft.VisualBasic.FileIO và Shell.Application.
/// </summary>
public sealed class WindowsRecycleBin : IRecycleBin
{
    public static readonly WindowsRecycleBin Instance = new();
    private readonly ILog _log;

    public WindowsRecycleBin(ILog? log = null)
    {
        _log = log ?? NullLog.Instance;
    }

    public void SendToRecycleBin(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
    }

    public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalPath);
        object? shell = null, recycle = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return false;
            shell = Activator.CreateInstance(shellType);
            dynamic shellDynamic = shell!;
            recycle = shellDynamic.Namespace(10);
            if (recycle is null) return false;
            dynamic recycleDynamic = recycle;
            object? items = recycleDynamic.Items();
            try
            {
                foreach (dynamic item in (IEnumerable)items!)
                {
                    try
                    {
                        var deletedFrom = (string?)item.ExtendedProperty("System.Recycle.DeletedFrom");
                        var name = (string?)item.Name;
                        if (!string.Equals(deletedFrom, originalPath, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(Path.Combine(deletedFrom ?? string.Empty, name ?? string.Empty), originalPath, StringComparison.OrdinalIgnoreCase)) continue;
                        var sizeText = Convert.ToString(item.Size, CultureInfo.InvariantCulture);
                        if (!long.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long size) || size != expectedSize) continue;
                        var restoredVerb = false;
                        foreach (dynamic verb in (IEnumerable)item.Verbs())
                        {
                            var verbName = ((string?)verb.Name ?? string.Empty).Trim().ToLowerInvariant().Replace("&", string.Empty);
                            if (!verbName.Contains("restore", StringComparison.OrdinalIgnoreCase) &&
                                !verbName.Contains("khôi", StringComparison.OrdinalIgnoreCase) &&
                                !verbName.Contains("wiederher", StringComparison.OrdinalIgnoreCase)) continue;
                            verb.DoIt();
                            restoredVerb = true;
                            Release(verb);
                            break;
                        }
                        if (!restoredVerb) item.InvokeVerb("Restore");
                        return WaitForRestore(originalPath, expectedLastWriteUtc);
                    }
                    finally { Release(item); }
                }
                return false;
            }
            finally { Release(items); }
        }
        catch (Exception ex)
        {
            _log.Error($"Recycle Bin restore failed: {Path.GetFileName(originalPath)}", ex);
            return false;
        }
        finally { Release(recycle); Release(shell); }
    }

    private static bool WaitForRestore(string path, DateTime expectedLastWriteUtc)
    {
        for (var i = 0; i < 10; i++)
        {
            if (IsExpectedFile(path, expectedLastWriteUtc)) return true;
            Thread.Sleep(50);
        }
        return IsExpectedFile(path, expectedLastWriteUtc);
    }

    private static bool IsExpectedFile(string path, DateTime expectedLastWriteUtc)
    {
        try { return File.Exists(path) && new FileInfo(path).LastWriteTimeUtc == expectedLastWriteUtc; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
}
