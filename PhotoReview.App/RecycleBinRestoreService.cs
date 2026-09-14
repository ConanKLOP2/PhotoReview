using System.Collections;
using System.IO;
using System.Runtime.InteropServices;

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
            foreach (dynamic item in (IEnumerable)recycleDynamic.Items())
            {
                try
                {
                    var deletedFrom = (string?)item.ExtendedProperty("System.Recycle.DeletedFrom");
                    var name = (string?)item.Name;
                    if (!string.Equals(deletedFrom, originalPath, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(Path.Combine(deletedFrom ?? string.Empty, name ?? string.Empty), originalPath, StringComparison.OrdinalIgnoreCase)) continue;
                    var size = Convert.ToInt64(item.Size);
                    if (size != expectedSize) continue;
                    var restoredVerb = false;
                    foreach (dynamic verb in (IEnumerable)item.Verbs())
                    {
                        var verbName = ((string?)verb.Name ?? string.Empty).Trim().ToLowerInvariant();
                        if (!verbName.Contains("restore") && !verbName.Contains("khôi phục") && !verbName.Contains("zurück")) continue;
                        verb.DoIt();
                        restoredVerb = true;
                        Release(verb);
                        break;
                    }
                    if (!restoredVerb) item.InvokeVerb("Restore");
                    return File.Exists(originalPath) || WaitForRestore(originalPath, expectedLastWriteUtc);
                }
                finally { Release(item); }
            }
            return false;
        }
        catch (Exception ex) { AppLog.Error($"Recycle Bin restore failed: {Path.GetFileName(originalPath)}", ex); return false; }
        finally { Release(recycle); Release(shell); }
    }

    private static bool WaitForRestore(string path, DateTime expectedLastWriteUtc)
    {
        for (var i = 0; i < 10; i++)
        {
            if (File.Exists(path)) return true;
            Thread.Sleep(50);
        }
        return File.Exists(path);
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
}
