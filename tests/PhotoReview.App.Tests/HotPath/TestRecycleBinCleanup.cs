using System.Collections;
using System.IO;
using System.Runtime.InteropServices;

namespace PhotoReview.App.Tests.HotPath;

/// <summary>
/// Removes from the REAL Recycle Bin only the items a Native test itself created. An item qualifies only when its
/// "deleted from" folder equals the test's own unique folder (full path, ordinal-ignore-case, no prefix or pattern
/// matching); every other item is left untouched. Removal deletes the item's own <c>$R</c>/<c>$I</c> pair from the
/// bin directory, so no confirmation dialog can block a test run.
/// </summary>
internal static class TestRecycleBinCleanup
{
    /// <summary>Bin-side files (<c>$R...</c> payload and <c>$I...</c> metadata) of the items deleted from <paramref name="exactFolder"/>.</summary>
    internal static List<string> FindBinFilesFor(string exactFolder)
    {
        var found = new List<string>();
        var shellType = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("Shell.Application is unavailable.");
        object? shell = Activator.CreateInstance(shellType);
        object? recycle = null;
        try
        {
            dynamic shellDynamic = shell!;
            recycle = shellDynamic.Namespace(10);
            if (recycle is null) return found;
            dynamic recycleDynamic = recycle;
            foreach (dynamic item in (IEnumerable)recycleDynamic.Items())
            {
                try
                {
                    var deletedFrom = (string?)item.ExtendedProperty("System.Recycle.DeletedFrom");
                    if (!string.Equals(deletedFrom, exactFolder, StringComparison.OrdinalIgnoreCase)) continue;
                    var binPath = (string?)item.Path;
                    if (string.IsNullOrEmpty(binPath)) continue;
                    found.Add(binPath);
                    var name = Path.GetFileName(binPath);
                    if (name.StartsWith("$R", StringComparison.Ordinal))
                        found.Add(Path.Combine(Path.GetDirectoryName(binPath)!, "$I" + name[2..]));
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException) { /* unreadable item: not ours to touch */ }
            }
        }
        finally
        {
            if (recycle is not null && Marshal.IsComObject(recycle)) Marshal.FinalReleaseComObject(recycle);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
        return found;
    }

    /// <summary>Removes this folder's items from the bin and returns the bin files that could not be removed.</summary>
    internal static List<string> RemoveItemsDeletedFrom(string exactFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exactFolder);
        var leftovers = new List<string>();
        foreach (var binFile in FindBinFilesFor(exactFolder))
        {
            try
            {
                if (!File.Exists(binFile)) continue;
                File.SetAttributes(binFile, FileAttributes.Normal);
                File.Delete(binFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { leftovers.Add(binFile); }
        }
        return leftovers;
    }

    /// <summary>Number of bin items currently deleted from <paramref name="exactFolder"/>.</summary>
    internal static int CountItemsDeletedFrom(string exactFolder)
        => FindBinFilesFor(exactFolder).Count(p => Path.GetFileName(p).StartsWith("$R", StringComparison.Ordinal));
}
