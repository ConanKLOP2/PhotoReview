using System.Runtime.InteropServices;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;

namespace PhotoReview.Platform.Windows;

/// <summary>
/// Triển khai <see cref="INaturalComparer"/> sử dụng API Windows Native StrCmpLogicalW từ shlwapi.dll.
/// Tạm đặt tại PhotoReview.App.Platform (sẽ chuyển tiếp ở task T33b).
/// </summary>
public sealed class WindowsNaturalComparer : INaturalComparer
{
    public static readonly WindowsNaturalComparer Instance = new();

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string x, string y);

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        try
        {
            var result = StrCmpLogicalW(x, y);
            return result != 0 ? result : StringComparer.OrdinalIgnoreCase.Compare(x, y);
        }
        catch (DllNotFoundException)
        {
            return ManagedNaturalComparer.Instance.Compare(x, y);
        }
        catch (EntryPointNotFoundException)
        {
            return ManagedNaturalComparer.Instance.Compare(x, y);
        }
    }
}
