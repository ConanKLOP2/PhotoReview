using System.Text;
using PhotoReview.Shell.Win32.Dialogs.Native;

namespace PhotoReview.Shell.Tests.Dialogs.Native;

/// <summary>WP-35 GĐ1: DLGTEMPLATE dựng trong bộ nhớ đúng bố cục (header, từng DLGITEMTEMPLATE căn DWORD, chuỗi UTF-16).</summary>
public sealed class DialogTemplateTests
{
    private static ushort U16(byte[] b, int offset) => BitConverter.ToUInt16(b, offset);

    private static uint U32(byte[] b, int offset) => BitConverter.ToUInt32(b, offset);

    private static string Sz(byte[] b, ref int offset)
    {
        var text = new StringBuilder();
        while (U16(b, offset) != 0)
        {
            text.Append((char)U16(b, offset));
            offset += 2;
        }

        offset += 2;
        return text.ToString();
    }

    [Fact]
    public void Header_HasStyleExStyleCountZeroGeometryAndTitle()
    {
        var bytes = DialogTemplate.Build("Tiêu đề", 0x80C80000, 0x1, []);

        Assert.Equal(0x80C80000u, U32(bytes, 0));
        Assert.Equal(1u, U32(bytes, 4));
        Assert.Equal(0, U16(bytes, 8)); // cdit
        for (var i = 10; i < 18; i += 2)
        {
            Assert.Equal(0, U16(bytes, i)); // x y cx cy: geometry is computed at run time
        }

        Assert.Equal(0, U16(bytes, 18)); // menu
        Assert.Equal(0, U16(bytes, 20)); // class
        var offset = DialogTemplate.HeaderBytes;
        Assert.Equal("Tiêu đề", Sz(bytes, ref offset));
        Assert.Equal(bytes.Length, offset);
    }

    [Fact]
    public void Items_AreDwordAligned_InOrder_WithIdClassAtomTextAndNoCreationData()
    {
        DialogItem[] items =
        [
            new(DialogControlClass.Static, 100, "Nhãn", 0x50000000),
            new(DialogControlClass.Edit, 101, "", 0x50010080, 0x200),
            new(DialogControlClass.Button, 2, "Huỷ", 0x50010000),
            new(DialogControlClass.ListBox, 302, "x", 0x50A10101),
        ];
        var bytes = DialogTemplate.Build("T", 0, 0, items);

        Assert.Equal(4, U16(bytes, 8));
        var offset = DialogTemplate.HeaderBytes;
        Sz(bytes, ref offset); // title
        foreach (var item in items)
        {
            while (offset % 4 != 0)
            {
                Assert.Equal(0, bytes[offset]); // padding is zero
                offset++;
            }

            Assert.Equal(item.Style, U32(bytes, offset));
            Assert.Equal(item.ExStyle, U32(bytes, offset + 4));
            for (var i = 8; i < 16; i += 2)
            {
                Assert.Equal(0, U16(bytes, offset + i));
            }

            Assert.Equal(item.Id, U16(bytes, offset + 16));
            Assert.Equal(DialogTemplate.AtomMarker, U16(bytes, offset + 18));
            Assert.Equal((ushort)item.Class, U16(bytes, offset + 20));
            offset += 22;
            Assert.Equal(item.Text, Sz(bytes, ref offset));
            Assert.Equal(0, U16(bytes, offset)); // cbExtra
            offset += 2;
        }

        Assert.Equal(bytes.Length, offset);
    }

    [Fact]
    public void PredefinedClassAtoms_AreTheWin32Ones()
    {
        Assert.Equal(0x0080, (int)DialogControlClass.Button);
        Assert.Equal(0x0081, (int)DialogControlClass.Edit);
        Assert.Equal(0x0082, (int)DialogControlClass.Static);
        Assert.Equal(0x0083, (int)DialogControlClass.ListBox);
    }

    [Fact]
    public void AnEmptyOrNullTitleAndText_AreStoredAsASingleTerminator()
    {
        var bytes = DialogTemplate.Build(null!, 0, 0, [new DialogItem(DialogControlClass.Static, 1, null!, 0)]);

        var offset = DialogTemplate.HeaderBytes;
        Assert.Equal(string.Empty, Sz(bytes, ref offset));
    }
}
