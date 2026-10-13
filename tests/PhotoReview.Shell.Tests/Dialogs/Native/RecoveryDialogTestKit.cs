using System.IO;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Tests.Hosting;
using PhotoReview.Shell.Win32.Dialogs;
using PhotoReview.Shell.Win32.Dialogs.Native;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;

namespace PhotoReview.Shell.Tests.Dialogs.Native;

/// <summary>
/// Hộp thoại xác nhận/thông báo giả của WP-35 GĐ4: ghi lại mọi lần hỏi (chủ, tiêu đề, nội dung, loại) và trả lời theo
/// <see cref="Answer"/>. Không bao giờ mở TaskDialog thật (test không có comctl32 v6 và không được chặn).
/// </summary>
internal sealed class FakeMessages : ITaskDialogPort
{
    private readonly object _gate = new();
    private readonly List<ShownMessage> _calls = [];

    /// <summary>Trả lời cho hộp xác nhận (true = Yes). Nhận (tiêu đề, nội dung).</summary>
    public Func<string, string, bool> Answer { get; set; } = static (_, _) => true;

    /// <summary>Gọi NGAY khi hộp hiện (trước khi trả lời): test kiểm trạng thái tại thời điểm hỏi.</summary>
    public Action<ShownMessage>? OnShow { get; set; }

    /// <summary>Ép kết quả của hộp xác nhận (vd. IDCANCEL khi đóng bằng X); null = theo <see cref="Answer"/>.</summary>
    public int? ConfirmationResult { get; set; }

    public IReadOnlyList<ShownMessage> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public int Show(nint owner, string title, string message, TaskDialogKind kind)
    {
        var call = new ShownMessage(owner, title, message, kind);
        lock (_gate)
        {
            _calls.Add(call);
        }

        OnShow?.Invoke(call);
        if (kind != TaskDialogKind.Confirmation)
        {
            return WindowMessages.IdOk;
        }

        if (ConfirmationResult is { } forced)
        {
            return forced;
        }

        return Answer(title, message) ? WindowMessages.IdYes : WindowMessages.IdNo;
    }
}

internal sealed record ShownMessage(nint Owner, string Title, string Message, TaskDialogKind Kind);

/// <summary>IFileSystem chuyển tiếp, với móc ở <c>GetFileStat</c> (kiểm tra nền): chặn/ném theo ý test.</summary>
internal sealed class HookFileSystem(IFileSystem inner, Action<string> onStat) : IFileSystem
{
    public bool FileExists(string path) => inner.FileExists(path);

    public bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public FileStat? GetFileStat(string path)
    {
        onStat(path);
        return inner.GetFileStat(path);
    }

    public void Move(string source, string destination) => inner.Move(source, destination);

    public void Copy(string source, string destination) => inner.Copy(source, destination);

    public void Delete(string path) => inner.Delete(path);

    public Stream OpenReadShared(string path, int bufferSize = 65536) => inner.OpenReadShared(path, bufferSize);

    public Stream OpenAppend(string path, bool durable) => inner.OpenAppend(path, durable);

    public void WriteAllTextAtomic(string path, string text, bool durable = true) => inner.WriteAllTextAtomic(path, text, durable);

    public string ReadAllText(string path) => inner.ReadAllText(path);

    public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*") => inner.EnumerateFiles(directory, pattern);

    public IEnumerable<(string Path, FileStat? Stat)> EnumerateFilesWithStat(string directory, Func<string, bool> include,
        Action<SkippedEntry> onSkipped) => inner.EnumerateFilesWithStat(directory, include, onSkipped);

    public bool TryProbeReadable(string path, out string? failure) => inner.TryProbeReadable(path, out failure);

    public IEnumerable<(string Path, FileStat? Stat)> EnumerateReadableFilesWithStat(string directory, Func<string, bool> include,
        Action<SkippedEntry> onSkipped) => inner.EnumerateReadableFilesWithStat(directory, include, onSkipped);

    public IEnumerable<string> EnumerateDirectories(string directory) => inner.EnumerateDirectories(directory);

    public void CreateDirectory(string path) => inner.CreateDirectory(path);
}

/// <summary>
/// Giàn thử của cửa sổ Recovery Win32 (WP-35 GĐ4): UI thread thật của shell (dispatcher + vòng lặp + bối cảnh đồng bộ như
/// production), chủ là một cửa sổ popup, thư mục tạm RIÊNG (<c>wp35g4-recovery-*</c>) chứa tệp ảnh giả + journal thật nằm trong đó.
/// AN TOÀN DỮ LIỆU: mọi đường dẫn nằm trong <see cref="Root"/>; không có Thùng rác thật (không ai truyền <c>IRecycleBin</c>);
/// hộp xác nhận là <see cref="FakeMessages"/>; clipboard/Explorer là bản ghi lại.
/// </summary>
internal sealed class RecoveryRig : IAsyncDisposable
{
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    public static readonly DateTime Stamp = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly List<RecoveryDialog> _dialogs = [];
    private OperationJournal? _journal;
    private RecoveryRetryService? _service;

    public UiThread Ui { get; private set; } = null!;

    public nint Owner { get; private set; }

    public string Root { get; private set; } = string.Empty;

    public FakeMessages Messages { get; } = new();

    public List<string> Copied { get; } = [];

    public List<string> ExplorerArgs { get; } = [];

    public Exception? ExplorerError { get; set; }

    public static async Task<RecoveryRig> StartAsync()
    {
        var rig = new RecoveryRig { Ui = await UiThread.StartAsync() };
        rig.Owner = await rig.Ui.InvokeAsync(() => CreateOwner());
        rig.Root = Path.Combine(Path.GetTempPath(), "wp35g4-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rig.Root);
        return rig;
    }

    /// <summary>Journal THẬT trong thư mục tạm (AppPaths(Root)): dữ liệu của "bản sao journal".</summary>
    public OperationJournal Journal => _journal ??= new OperationJournal(new AppPaths(Root), new PhysicalFileSystem(), new SystemClock());

    /// <summary>RecoveryRetryService thật trên đĩa thật của thư mục tạm; KHÔNG có Thùng rác (không truyền IRecycleBin).</summary>
    public RecoveryRetryService Service => _service ??= new RecoveryRetryService(Journal, new PhysicalFileSystem(), new SystemClock());

    /// <summary>Tệp journal (trong thư mục tạm).</summary>
    public string JournalFile => new AppPaths(Root).JournalFile;

    public string Write(string relative, string content = "photo-bytes")
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Mục journal (mặc định Move Failed) lấy kích thước/giờ ghi theo tệp thật còn tồn tại (nguồn, rồi đích).</summary>
    public JournalEntry Entry(string id, string source, string? destination, FileOperationType type = FileOperationType.Move,
        JournalState state = JournalState.Failed, string? error = "disk error", string? errorCode = null, bool? undo = null,
        long? size = null, IReadOnlyList<JournalGroupMember>? members = null, bool? permanent = null)
    {
        source = Path.IsPathRooted(source) ? source : Path.Combine(Root, source);
        var info = File.Exists(source) ? new FileInfo(source) : destination is not null && File.Exists(destination) ? new FileInfo(destination) : null;
        return new JournalEntry(id, type, state, source, destination, size ?? info?.Length ?? 11, info?.LastWriteTimeUtc ?? Stamp, Stamp,
            error, errorCode, permanent, undo, GroupId: members is null ? null : "grp-" + id, GroupMembers: members);
    }

    /// <summary>Mọi tệp của người dùng (trừ thư mục dữ liệu của journal): tên + kích thước + giờ ghi, đã sắp xếp.</summary>
    public string Tree()
    {
        var data = Path.Combine(Root, "PhotoReview") + Path.DirectorySeparatorChar;
        return string.Join("|", Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(data, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(Root, path) + ":" + new FileInfo(path).Length + ":" + File.GetLastWriteTimeUtc(path).Ticks));
    }

    public Task<RecoveryDialog> OpenAsync(IReadOnlyList<JournalEntry> entries, Func<JournalEntry, Task<RecoveryRetryResult>>? retry = null,
        Func<IReadOnlyList<JournalEntry>, DismissOutcome>? dismiss = null, IFileSystem? fileSystem = null, Func<bool>? allowPermanentDelete = null,
        bool autoCheck = true, INativeDialogChrome? chrome = null) => Ui.InvokeAsync(() =>
    {
        var dialog = new RecoveryDialog(entries, retry, dismiss, fileSystem, allowPermanentDelete,
            new RecoveryDialogSeams(Messages, text =>
                {
                    Copied.Add(text);
                    return true;
                },
                args =>
                {
                    ExplorerArgs.Add(args);
                    if (ExplorerError is not null)
                    {
                        throw ExplorerError;
                    }
                },
                autoCheck),
            chrome);
        _dialogs.Add(dialog);
        dialog.ShowModeless(Owner, Ui.Loop);
        return dialog;
    });

    public Task<T> OnUi<T>(Func<T> func) => Ui.InvokeAsync(func);

    public Task OnUi(Action action) => Ui.InvokeAsync(action);

    /// <summary>Chờ lượt kiểm tra nền gần nhất của <paramref name="dialog"/> kết thúc (có giới hạn).</summary>
    public async Task CheckDoneAsync(RecoveryDialog dialog)
    {
        var task = await OnUi(() => dialog.LastCheckTask!);
        await task.WaitAsync(Bound);
    }

    /// <summary>Chờ lượt Retry gần nhất kết thúc (có giới hạn).</summary>
    public async Task RetryDoneAsync(RecoveryDialog dialog)
    {
        var task = await OnUi(() => dialog.LastRetryTask!);
        await task.WaitAsync(Bound);
    }

    /// <summary>Chọn các hàng hiển thị như người dùng (Ctrl+bấm): đặt chọn trong listbox rồi gửi LBN_SELCHANGE.</summary>
    public Task PickAsync(RecoveryDialog dialog, params int[] visibleIndices) => OnUi(() =>
    {
        var list = Item(dialog, RecoveryDialog.ListId);
        ListBoxControl.SelectAll(list, false);
        foreach (var index in visibleIndices)
        {
            ListBoxControl.SetSelected(list, index, true);
        }

        Command(dialog, RecoveryDialog.ListId, ControlConstants.LbnSelChange);
    });

    public Task ClickAsync(RecoveryDialog dialog, int id) => Ui.InvokeAsync(() => Command(dialog, id));

    /// <summary>Chọn mục bộ lọc (0 = All, 1 = Can retry, 2 = Already done, 3 = Problems) như người dùng.</summary>
    public Task FilterAsync(RecoveryDialog dialog, int index) => OnUi(() =>
    {
        ComboBoxControl.Select(Item(dialog, RecoveryDialog.FilterId), index);
        Command(dialog, RecoveryDialog.FilterId, ControlConstants.CbnSelChange);
    });

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Ui.InvokeAsync(() =>
            {
                foreach (var dialog in _dialogs)
                {
                    dialog.Close();
                }

                DestroyOwner(Owner);
            });
        }
        finally
        {
            await Ui.DisposeAsync();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
