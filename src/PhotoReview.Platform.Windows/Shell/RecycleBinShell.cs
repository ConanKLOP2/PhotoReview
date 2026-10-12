using System.Runtime.InteropServices;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Platform.Windows.Shell;

// WP-33: khôi phục từ Thùng rác KHÔNG late-bound. Bản cũ: Shell.Application + `dynamic` (Namespace(10).Items(), ExtendedProperty,
// Verbs(), InvokeVerb). Mới: IShellItem2 / IEnumShellItems / IContextMenu có kiểu rõ ([GeneratedComInterface]). Phần chính sách
// (chọn ứng viên, đúng-một-khớp, chọn verb, chờ tệp xuất hiện) tách khỏi COM qua IRecycleBinSession để kiểm bằng fake.

/// <summary>Một phiên đọc Thùng rác (thư mục ảo của shell). Giải phóng phiên trả mọi ref COM còn giữ.</summary>
internal interface IRecycleBinSession : IDisposable
{
    /// <summary>Các mục trong thùng rác; mỗi mục do người gọi <c>Dispose</c>.</summary>
    IEnumerable<IRecycleBinEntry> Entries();
}

/// <summary>Một mục trong thùng rác. Đọc thuộc tính có thể ném lỗi theo từng mục (xem <see cref="RecycleItemFailure"/>).</summary>
internal interface IRecycleBinEntry : IDisposable
{
    /// <summary>System.Recycle.DeletedFrom: thư mục gốc; null khi shell không có.</summary>
    string? DeletedFrom();

    /// <summary>Tên hiển thị (bỏ đuôi khi Explorer ẩn đuôi đã biết).</summary>
    string? DisplayName();

    bool TryGetSize(out long size);

    /// <summary>System.DateModified dạng UTC.</summary>
    bool TryGetModifiedUtc(out DateTime utc);

    /// <summary>Mở menu verb của mục; người gọi <c>Dispose</c>.</summary>
    IRecycleVerbs OpenVerbs();
}

internal interface IRecycleVerbs : IDisposable
{
    /// <summary>Tên verb theo thứ tự menu, bỏ dải phân cách (ngôn ngữ của OS).</summary>
    IReadOnlyList<string> Names { get; }

    void InvokeByIndex(int index);

    void InvokeCanonical(string verb);
}

internal static class RecycleBinRestorer
{
    /// <param name="openSession">Mở phiên thùng rác (null = thùng rác không khả dụng).</param>
    internal static bool Restore(Func<IRecycleBinSession?> openSession, string originalPath, long expectedSize,
        DateTime expectedLastWriteUtc, ILog log)
    {
        IRecycleBinSession? session = null;
        try
        {
            session = openSession();
            if (session is null) return false;
            var candidates = new List<IRecycleBinEntry>();
            try
            {
                foreach (var entry in session.Entries())
                {
                    var kept = false;
                    try
                    {
                        var deletedFrom = entry.DeletedFrom();
                        var name = entry.DisplayName();
                        if (!entry.TryGetSize(out var size)) continue;
                        if (!entry.TryGetModifiedUtc(out var modified)) continue;
                        var candidate = new RecycleCandidate(deletedFrom, name, size, modified);
                        if (RecycleCandidateSelector.IsMatch(candidate, originalPath, expectedSize, expectedLastWriteUtc))
                        {
                            candidates.Add(entry);
                            kept = true;
                        }
                    }
                    catch (Exception ex) when (RecycleItemFailure.IsPerItem(ex))
                    {
                        log.Error("Recycle Bin item inspection failed", ex);
                    }
                    finally
                    {
                        // Only the selected candidate is needed later; every other shell item wrapper is released now.
                        if (!kept) entry.Dispose();
                    }
                }

                if (candidates.Count != 1) return false;
                using (var verbs = candidates[0].OpenVerbs())
                    RestoreVerb.Invoke(verbs.Names, verbs.InvokeByIndex, verbs.InvokeCanonical);
                return WindowsRecycleBin.WaitForRestore(originalPath, expectedSize, expectedLastWriteUtc);
            }
            finally
            {
                foreach (var candidate in candidates) candidate.Dispose();
            }
        }
        catch (Exception ex)
        {
            log.Error($"Recycle Bin restore failed: {Path.GetFileName(originalPath)}", ex);
            return false;
        }
        finally
        {
            session?.Dispose();
        }
    }
}

/// <summary>Thùng rác thật qua COM: <c>SHGetKnownFolderItem(FOLDERID_RecycleBinFolder)</c> + <c>IEnumShellItems</c> + <c>IContextMenu</c>.</summary>
internal sealed class ComRecycleBinSession : IRecycleBinSession
{
    private IShellItem? _folder;

    private ComRecycleBinSession(IShellItem folder) => _folder = folder;

    /// <summary>Chạy trên luồng STA riêng (xem <see cref="ShellStaRunner"/>); mọi đối tượng COM sống và chết trong cùng lần gọi.</summary>
    internal static bool Restore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc, ILog log)
        => ShellStaRunner.Run(() => RecycleBinRestorer.Restore(Open, originalPath, expectedSize, expectedLastWriteUtc, log));

    internal static IRecycleBinSession? Open()
    {
        var hr = ShellCom.SHGetKnownFolderItem(ShellIds.FolderIdRecycleBin, 0, 0, ShellIds.IidShellItem, out var pointer);
        ShellCom.Check(hr);
        return pointer == 0 ? null : new ComRecycleBinSession(ShellCom.Wrap<IShellItem>(pointer));
    }

    public IEnumerable<IRecycleBinEntry> Entries()
    {
        var folder = _folder ?? throw new ObjectDisposedException(nameof(ComRecycleBinSession));
        ShellCom.Check(folder.BindToHandler(0, ShellIds.BhidEnumItems, ShellIds.IidEnumShellItems, out var enumPointer));
        var enumerator = ShellCom.Wrap<IEnumShellItems>(enumPointer);
        try
        {
            while (true)
            {
                var hr = enumerator.Next(1, out var itemPointer, out var fetched);
                ShellCom.Check(hr);
                if (hr != 0 || fetched == 0 || itemPointer == 0) yield break; // S_FALSE: end of the bin
                yield return new ComRecycleBinEntry(ShellCom.Wrap<IShellItem2>(itemPointer));
            }
        }
        finally
        {
            ShellCom.Release(enumerator);
        }
    }

    public void Dispose()
    {
        ShellCom.Release(_folder);
        _folder = null;
    }
}

internal sealed class ComRecycleBinEntry(IShellItem2 item) : IRecycleBinEntry
{
    private IShellItem2? _item = item;

    private IShellItem2 Item => _item ?? throw new ObjectDisposedException(nameof(ComRecycleBinEntry));

    public string? DeletedFrom() => ReadString(Item, ShellIds.DeletedFrom, failureIsNull: true);

    public string? DisplayName()
    {
        ShellCom.Check(Item.GetDisplayName(ShellIds.SigdnParentRelative, out var name));
        return TakeString(name);
    }

    public bool TryGetSize(out long size)
    {
        size = 0;
        if (Item.GetUInt64(ShellIds.Size, out var value) < 0 || value > long.MaxValue) return false;
        size = (long)value;
        return true;
    }

    public bool TryGetModifiedUtc(out DateTime utc)
    {
        utc = default;
        if (Item.GetFileTime(ShellIds.DateModified, out var fileTime) < 0 || fileTime <= 0) return false;
        try { utc = DateTime.FromFileTimeUtc(fileTime); }
        catch (ArgumentOutOfRangeException) { return false; }
        return true;
    }

    public IRecycleVerbs OpenVerbs()
    {
        ShellCom.Check(Item.BindToHandler(0, ShellIds.BhidSfUiObject, ShellIds.IidContextMenu, out var menuPointer));
        return new ComRecycleVerbs(ShellCom.Wrap<IContextMenu>(menuPointer));
    }

    public void Dispose()
    {
        ShellCom.Release(_item);
        _item = null;
    }

    internal static string? ReadString(IShellItem2 item, PropertyKey key, bool failureIsNull)
    {
        var hr = item.GetString(key, out var value);
        if (hr < 0)
        {
            if (failureIsNull) return null;
            ShellCom.Check(hr);
        }
        return TakeString(value);
    }

    /// <summary>PWSTR do CoTaskMemAlloc -> chuỗi managed, rồi giải phóng.</summary>
    internal static string? TakeString(nint pointer)
    {
        if (pointer == 0) return null;
        try { return Marshal.PtrToStringUni(pointer); }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }
}

/// <summary>
/// Verb của một mục: menu ngữ cảnh (<c>IContextMenu::QueryContextMenu</c> vào một HMENU tạm), tên lấy từ <c>GetMenuItemInfo</c>,
/// gọi theo offset id hoặc theo verb chuẩn (<c>undelete</c>) - đúng cách <c>FolderItem.Verbs()</c>/<c>InvokeVerb</c> làm bên trong.
/// </summary>
internal sealed class ComRecycleVerbs : IRecycleVerbs
{
    private const uint IdFirst = 1;
    private const uint IdLast = 0x7FFF;
    private const uint MiimId = 0x2, MiimString = 0x40, MiimFtype = 0x100;
    private const uint MftSeparator = 0x800;
    private const int ShowNormal = 1;
    private const int NameBufferChars = 260;

    private IContextMenu? _menu;
    private nint _hmenu;
    private readonly List<uint> _ids = [];
    private readonly List<string> _names = [];

    internal ComRecycleVerbs(IContextMenu menu)
    {
        _menu = menu;
        try
        {
            _hmenu = ShellCom.CreatePopupMenu();
            if (_hmenu == 0) throw new InvalidOperationException("CreatePopupMenu failed.");
            ShellCom.Check(menu.QueryContextMenu(_hmenu, 0, IdFirst, IdLast, 0));
            ReadMenu();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public IReadOnlyList<string> Names => _names;

    private unsafe void ReadMenu()
    {
        var count = ShellCom.GetMenuItemCount(_hmenu);
        var buffer = new char[NameBufferChars];
        for (var position = 0; position < count; position++)
        {
            fixed (char* text = buffer)
            {
                buffer[0] = '\0';
                var info = new MenuItemInfo
                {
                    Size = (uint)sizeof(MenuItemInfo),
                    Mask = MiimId | MiimString | MiimFtype,
                    TypeData = (nint)text,
                    Cch = NameBufferChars,
                };
                if (!ShellCom.GetMenuItemInfo(_hmenu, (uint)position, true, ref info)) continue;
                if ((info.Type & MftSeparator) != 0) continue;
                _ids.Add(info.Id);
                _names.Add(new string(text));
            }
        }
    }

    public void InvokeByIndex(int index)
    {
        var menu = _menu ?? throw new ObjectDisposedException(nameof(ComRecycleVerbs));
        var info = new CmInvokeCommandInfo
        {
            Size = (uint)Marshal.SizeOf<CmInvokeCommandInfo>(),
            Verb = (nint)(_ids[index] - IdFirst), // MAKEINTRESOURCE(offset)
            Show = ShowNormal,
        };
        ShellCom.Check(menu.InvokeCommand(in info));
    }

    public void InvokeCanonical(string verb)
    {
        var menu = _menu ?? throw new ObjectDisposedException(nameof(ComRecycleVerbs));
        var ansi = Marshal.StringToHGlobalAnsi(verb);
        try
        {
            var info = new CmInvokeCommandInfo
            {
                Size = (uint)Marshal.SizeOf<CmInvokeCommandInfo>(),
                Verb = ansi,
                Show = ShowNormal,
            };
            ShellCom.Check(menu.InvokeCommand(in info));
        }
        finally
        {
            Marshal.FreeHGlobal(ansi);
        }
    }

    public void Dispose()
    {
        if (_hmenu != 0)
        {
            ShellCom.DestroyMenu(_hmenu);
            _hmenu = 0;
        }
        ShellCom.Release(_menu);
        _menu = null;
    }
}
