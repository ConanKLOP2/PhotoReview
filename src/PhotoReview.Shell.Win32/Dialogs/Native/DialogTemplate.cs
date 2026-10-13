using System.Text;

namespace PhotoReview.Shell.Win32.Dialogs.Native;

/// <summary>Lớp điều khiển chuẩn của hộp thoại (atom "predefined class" của DLGITEMTEMPLATE).</summary>
internal enum DialogControlClass : ushort
{
    Button = 0x0080,
    Edit = 0x0081,
    Static = 0x0082,
    ListBox = 0x0083,
    ScrollBar = 0x0084,
    ComboBox = 0x0085,

    // WP-35 GĐ2: lớp common-control, KHÔNG phải atom (template ghi tên lớp, xem DialogTemplate.ClassName). Giá trị chỉ là
    // định danh nội bộ nằm ngoài vùng atom 0x0080..0x0085.
    ListView = 0x1001,
    Progress = 0x1002,
    Trackbar = 0x1003,
}

/// <summary>Một điều khiển con của template. Hình học KHÔNG nằm trong template: bố cục tính ở mã (dialog-unit + đo chuỗi).</summary>
internal readonly record struct DialogItem(DialogControlClass Class, int Id, string Text, uint Style, uint ExStyle = 0);

/// <summary>
/// WP-35 GĐ1: dựng DLGTEMPLATE (không Ex) trong bộ nhớ cho <c>CreateDialogIndirectParam</c>: header + từng DLGITEMTEMPLATE căn DWORD.
/// Template chỉ khai báo lớp, id, văn bản và kiểu; mọi toạ độ là 0 (bố cục tính khi WM_INITDIALOG vì phụ thuộc DPI/font/độ dài chuỗi).
/// </summary>
internal static class DialogTemplate
{
    /// <summary>Marker "lớp là atom" của DLGITEMTEMPLATE.</summary>
    internal const ushort AtomMarker = 0xFFFF;

    /// <summary>Kích thước header cố định: style 4 + exStyle 4 + cdit 2 + 4 x short + menu 2 + class 2.</summary>
    internal const int HeaderBytes = 4 + 4 + 2 + 8 + 2 + 2;

    public static byte[] Build(string title, uint style, uint exStyle, IReadOnlyList<DialogItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count > ushort.MaxValue)
        {
            throw new ArgumentException("Too many controls for a dialog template.", nameof(items));
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true);
        writer.Write(style);
        writer.Write(exStyle);
        writer.Write((ushort)items.Count);
        writer.Write((short)0); // x
        writer.Write((short)0); // y
        writer.Write((short)0); // cx
        writer.Write((short)0); // cy
        writer.Write((ushort)0); // menu: none
        writer.Write((ushort)0); // class: default dialog class
        WriteString(writer, title);
        foreach (var item in items)
        {
            AlignDword(stream, writer);
            writer.Write(item.Style);
            writer.Write(item.ExStyle);
            writer.Write((short)0);
            writer.Write((short)0);
            writer.Write((short)0);
            writer.Write((short)0);
            writer.Write((ushort)item.Id);
            if (ClassName(item.Class) is { } className)
            {
                WriteString(writer, className);
            }
            else
            {
                writer.Write(AtomMarker);
                writer.Write((ushort)item.Class);
            }

            WriteString(writer, item.Text);
            writer.Write((ushort)0); // no creation data
        }

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Tên lớp của điều khiển common (comctl32); null cho lớp chuẩn dùng atom.</summary>
    internal static string? ClassName(DialogControlClass controlClass) => controlClass switch
    {
        DialogControlClass.ListView => PhotoReview.Shell.Interop.ControlConstants.ListViewClass,
        DialogControlClass.Progress => PhotoReview.Shell.Interop.ControlConstants.ProgressClass,
        DialogControlClass.Trackbar => PhotoReview.Shell.Interop.ControlConstants.TrackbarClass,
        _ => null,
    };

    private static void WriteString(BinaryWriter writer, string? text)
    {
        foreach (var ch in text ?? string.Empty)
        {
            writer.Write((ushort)ch);
        }

        writer.Write((ushort)0);
    }

    private static void AlignDword(MemoryStream stream, BinaryWriter writer)
    {
        writer.Flush();
        while (stream.Length % 4 != 0)
        {
            writer.Write((byte)0);
        }
    }
}
