
namespace PhotoReview.Core.IO;

/// <summary>Classification of file-system failures shared by the physical file system and the compensation code.</summary>
public static class FileSystemErrors
{
    private const int Win32FacilityMask = 0x7FFF0000;
    private const int Win32Facility = 0x70000;

    /// <summary>
    /// True when <paramref name="ex"/> is the Windows "destination already exists" failure (ERROR_FILE_EXISTS 80 /
    /// ERROR_ALREADY_EXISTS 183). A no-overwrite Copy/Move raises it BEFORE touching anything, so whatever is at the
    /// destination then was not created by that call and must never be deleted by a compensation.
    /// </summary>
    public static bool IsDestinationExists(Exception ex) =>
        ex is IOException && (ex.HResult & 0xFFFF) is 80 or 183 && (ex.HResult & Win32FacilityMask) == Win32Facility;
}
