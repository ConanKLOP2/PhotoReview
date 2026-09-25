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
        // R2-F-05: removable drives and network shares have no Recycle Bin. FileSystem.DeleteFile with
        // OnlyErrorDialogs maps to SHFileOperation FOF_ALLOWUNDO | FOF_NOCONFIRMATION, which then deletes such a file
        // PERMANENTLY without the usual prompt, while the journal would record a successful "recycle". Refuse instead;
        // fixed drives take exactly the same call as before.
        if (!RecycleEligibility.CanRecycle(path, RecycleEligibility.QueryDriveType))
            throw new IOException(PhotoReview.Core.Localization.Tr.CoreRecycleUnsupportedDrive(Path.GetFileName(path)));
        FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        // A cancelled/aborted shell operation returns without an exception; never report success for a file still in place.
        if (File.Exists(path))
            throw new IOException(PhotoReview.Core.Localization.Tr.CoreRecycleNotDeleted(Path.GetFileName(path)));
    }

    public bool CanRecycle(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return RecycleEligibility.CanRecycle(path, RecycleEligibility.QueryDriveType);
    }

    /// <summary>Q-R8: permanent delete for drives without a Recycle Bin. Refuses fixed drives so it can never bypass the Recycle Bin there.</summary>
    public void DeletePermanently(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (CanRecycle(path))
            throw new InvalidOperationException("DeletePermanently is only for drives without a Recycle Bin.");
        File.Delete(path);
        if (File.Exists(path))
            throw new IOException(PhotoReview.Core.Localization.Tr.CoreRecycleNotDeleted(Path.GetFileName(path)));
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
            var candidates = new List<(object Item, RecycleCandidate Candidate)>();
            try
            {
                foreach (dynamic item in (IEnumerable)items!)
                {
                    var kept = false;
                    try
                    {
                        var deletedFrom = (string?)item.ExtendedProperty("System.Recycle.DeletedFrom");
                        var name = (string?)item.Name;
                        var sizeText = Convert.ToString(item.Size, CultureInfo.InvariantCulture);
                        var modified = item.ExtendedProperty("System.DateModified");
                        if (!long.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long size)) continue;
                        if (!DateTime.TryParse(Convert.ToString(modified, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime shellTime)) continue;
                        // The shell hands FILETIME properties over as an unzoned DATE that holds UTC digits, not
                        // local time (observed at UTC+7: 14:58:16 for a file written at 14:58:16Z). The local
                        // reading stays as an alternative so a Windows build that converts still matches.
                        var utcReading = DateTime.SpecifyKind(shellTime, DateTimeKind.Utc);
                        var localReading = DateTime.SpecifyKind(shellTime, DateTimeKind.Local).ToUniversalTime();
                        var candidate = new RecycleCandidate(deletedFrom, name, size, utcReading, localReading);
                        if (RecycleCandidateSelector.IsMatch(candidate, originalPath, expectedSize, expectedLastWriteUtc))
                        {
                            candidates.Add((item, candidate));
                            kept = true;
                        }
                    }
                    catch (Exception ex) when (ex is COMException or InvalidCastException or FormatException)
                    {
                        _log.Error("Recycle Bin item inspection failed", ex);
                    }
                    finally
                    {
                        // Only the selected candidate is needed later; every other shell item wrapper is released now.
                        if (!kept) Release(item);
                    }
                }

                if (candidates.Count != 1) return false;
                var selected = candidates[0].Item;
                dynamic selectedItem = selected;
                object? verbs = selectedItem.Verbs();
                var verbItems = new List<object>();
                try
                {
                    var verbNames = new List<string>();
                    foreach (dynamic verb in (IEnumerable)verbs!)
                    {
                        verbItems.Add(verb);
                        verbNames.Add((string?)verb.Name ?? string.Empty);
                    }
                    RestoreVerb.Invoke(verbNames,
                        index => { dynamic chosen = verbItems[index]; chosen.DoIt(); },
                        canonical => selectedItem.InvokeVerb(canonical));
                }
                finally
                {
                    foreach (var verb in verbItems) Release(verb);
                    Release(verbs);
                }
                return WaitForRestore(originalPath, expectedLastWriteUtc);
            }
            finally
            {
                foreach (var candidate in candidates) Release(candidate.Item);
                Release(items);
            }
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

/// <summary>
/// Decides whether the shell can really recycle a path (R2-F-05). Only local fixed drives have a Recycle Bin that
/// <c>SHFileOperation</c> uses without warning; removable, network, optical, RAM and unknown volumes delete permanently.
/// </summary>
internal static class RecycleEligibility
{
    private const string ExtendedPrefix = @"\\?\";
    private const string UncPrefix = @"\\";

    /// <summary>True only when <paramref name="driveTypeOf"/> reports <see cref="DriveType.Fixed"/> for the path's drive root; UNC paths and unknown roots are refused.</summary>
    internal static bool CanRecycle(string path, Func<string, DriveType?> driveTypeOf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(driveTypeOf);
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }

        // \\?\C:\dir\file -> C:\dir\file; \\server\share and \\?\UNC\... stay UNC and are refused.
        if (full.StartsWith(ExtendedPrefix, StringComparison.Ordinal) && full.Length >= 6 && full[5] == ':') full = full[4..];
        if (full.StartsWith(UncPrefix, StringComparison.Ordinal)) return false;
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root)) return false;
        return driveTypeOf(root) == DriveType.Fixed;
    }

    internal static DriveType? QueryDriveType(string root)
    {
        try { return new DriveInfo(root).DriveType; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>
/// Chooses how to restore a Recycle Bin item. The shell's verb NAMES follow the OS display language, so the name
/// list below is only the fast path; the language-independent fallback is the canonical verb <c>undelete</c>
/// (what <c>IContextMenu</c> maps the localized "Restore" to), which <c>FolderItem.InvokeVerb</c> accepts on every
/// language. The former fallback <c>InvokeVerb("Restore")</c> only ever worked on English.
/// </summary>
internal static class RestoreVerb
{
    internal const string CanonicalVerb = "undelete";

    // Lower-case fragments of the shell's localized "Restore" verb. Non-ASCII text is deliberate (OS-language
    // data, not our UI catalogs; I18N ADR 0006, allowlisted in localization-allowlist.txt).
    private static readonly string[] NameFragments =
    [
        "restore", "restaur", "ripristin", "wiederher", "herstel", "gendan", "återställ", "palauta", "przywr",
        "obnovi", "vissza", "geri yükle", "khôi", "восстанов", "відновит", "επαναφορ",
        "還原", "还原", "元に戻す", "복원", "استعادة", "שחזר",
    ];

    /// <summary>Removes accelerator markers such as <c>&amp;R</c> and CJK <c>(&amp;E)</c> and trims.</summary>
    internal static string Normalize(string? verbName)
    {
        if (string.IsNullOrWhiteSpace(verbName)) return string.Empty;
        var text = verbName.Replace("(&", "(", StringComparison.Ordinal).Replace("&", string.Empty, StringComparison.Ordinal);
        return text.Trim();
    }

    internal static bool IsRestoreName(string? verbName)
    {
        var name = Normalize(verbName);
        if (name.Length == 0) return false;
        foreach (var fragment in NameFragments)
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Index of the first verb whose name is a known Restore label, or -1.</summary>
    internal static int FindIndex(IReadOnlyList<string> verbNames)
    {
        for (var i = 0; i < verbNames.Count; i++)
            if (IsRestoreName(verbNames[i])) return i;
        return -1;
    }

    /// <summary>Invokes exactly one restore route and returns which: the matching verb by index, else the canonical verb.</summary>
    internal static string Invoke(IReadOnlyList<string> verbNames, Action<int> invokeByIndex, Action<string> invokeCanonical)
    {
        var index = FindIndex(verbNames);
        if (index >= 0)
        {
            invokeByIndex(index);
            return "verb:" + index.ToString(CultureInfo.InvariantCulture);
        }
        invokeCanonical(CanonicalVerb);
        return CanonicalVerb;
    }
}

internal readonly record struct RecycleCandidate(string? DeletedFrom, string? Name, long Size, DateTime LastWriteUtc, DateTime? AlternateLastWriteUtc = null);

internal static class RecycleCandidateSelector
{
    /// <summary>
    /// The shell reports System.DateModified with whole-second resolution, while NTFS write times carry 100 ns
    /// ticks, so exact equality never matched a real Recycle Bin item (Ctrl+Z after delete could not restore).
    /// A difference under one second covers both truncation and rounding by the shell.
    /// </summary>
    private static bool TimestampMatches(DateTime shellValueUtc, DateTime expectedUtc)
        => (shellValueUtc - expectedUtc).Duration() < TimeSpan.FromSeconds(1);

    /// <summary>
    /// <see cref="RecycleCandidate.DeletedFrom"/> (System.Recycle.DeletedFrom) is the item's original FOLDER, and
    /// <see cref="RecycleCandidate.Name"/> (FolderItem.Name) is the shell DISPLAY name, which drops the extension when
    /// Explorer hides known extensions (the Windows default). So the folder must equal the original folder and the
    /// name must be the file name with or without its extension; size and timestamp are still required, and the caller
    /// still restores only when exactly one item matches (review r7).
    /// </summary>
    internal static bool IsMatch(RecycleCandidate candidate, string originalPath, long expectedSize, DateTime expectedLastWriteUtc)
    {
        return FolderMatches(candidate.DeletedFrom, Path.GetDirectoryName(originalPath))
            && NameMatches(candidate.Name, Path.GetFileName(originalPath))
            && candidate.Size == expectedSize
            && (TimestampMatches(candidate.LastWriteUtc, expectedLastWriteUtc)
                || (candidate.AlternateLastWriteUtc is { } alternate && TimestampMatches(alternate, expectedLastWriteUtc)));
    }

    private static bool FolderMatches(string? deletedFrom, string? originalFolder) =>
        !string.IsNullOrEmpty(deletedFrom) && !string.IsNullOrEmpty(originalFolder)
        && string.Equals(Path.TrimEndingDirectorySeparator(deletedFrom), Path.TrimEndingDirectorySeparator(originalFolder), StringComparison.OrdinalIgnoreCase);

    private static bool NameMatches(string? displayName, string fileName) =>
        !string.IsNullOrEmpty(displayName)
        && (string.Equals(displayName, fileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(displayName, Path.GetFileNameWithoutExtension(fileName), StringComparison.OrdinalIgnoreCase));
}
