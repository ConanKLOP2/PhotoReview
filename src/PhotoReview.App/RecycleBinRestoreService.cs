using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using System.Globalization;

namespace PhotoReview.App;

public static class RecycleBinRestoreService
{
    public static bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc)
    {
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
                            if (!verbName.Contains("restore") && !verbName.Contains("khôi") && !verbName.Contains("wiederher")) continue;
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
        catch (Exception ex) { AppLog.Error($"Recycle Bin restore failed: {Path.GetFileName(originalPath)}", ex); return false; }
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
