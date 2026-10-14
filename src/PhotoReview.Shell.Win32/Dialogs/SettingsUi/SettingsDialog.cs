using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using PhotoReview.App;
using PhotoReview.App.Input;
using PhotoReview.App.Localization;
using PhotoReview.App.Presenters;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Core.Updates;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs.Native;

namespace PhotoReview.Shell.Win32.Dialogs.SettingsUi;

/// <summary>Phần phụ thuộc/seam của <see cref="SettingsDialog"/> (null = thật).</summary>
internal sealed record SettingsDialogOptions
{
    /// <summary>Hộp thoại Open/Save (Import/Export, chọn exe sau này).</summary>
    public IShellFileDialog? Files { get; init; }

    /// <summary>Cảnh báo modal (TaskDialog); test thay bản ghi lại.</summary>
    public IDialogWarningSink? Warnings { get; init; }

    /// <summary><see cref="SettingsTarget.ExternalEditor"/> = mở trang Files (không đổi trang được nhớ).</summary>
    public SettingsTarget Target { get; init; } = SettingsTarget.Default;

    /// <summary>Danh sách trang thay cho <see cref="SettingsPageCatalog"/> (test); phải theo thứ tự <see cref="SettingsPresenter.Pages"/>.</summary>
    public IReadOnlyList<ISettingsPageView>? Pages { get; init; }

    /// <summary>RAM vật lý giả (test trang Performance: khoảng thanh trượt cache RAM tất định); null = đo máy thật.</summary>
    public long? PhysicalMemoryBytes { get; init; }

    /// <summary>Mở trang tải về (mặc định: ShellExecute).</summary>
    public Action<string>? OpenUrl { get; init; }

    /// <summary>Chạy Explorer với tham số (mặc định <see cref="SettingsPresenter.TryStartExplorer"/>); trả lý do lỗi hoặc null.</summary>
    public Func<string, string?>? StartExplorer { get; init; }

    /// <summary>Chuỗi phiên bản ở chân cột trái và dùng cho kiểm tra cập nhật (mặc định: build info của assembly).</summary>
    public string? Version { get; init; }

    /// <summary>Ctrl có đang giữ không (test thay để mô phỏng Ctrl+Tab).</summary>
    public Func<bool>? ControlKeyDown { get; init; }

    /// <summary>
    /// WP-35 GĐ3b: mở Action profiles MODAL lồng trên cửa sổ Settings (owner, danh sách hiện tại) -&gt; danh sách đã sửa hoặc null khi huỷ
    /// (cổng Win32 gắn <c>ShowActionProfiles</c> của nó; null = trang Shortcuts báo "không mở được trình sửa").
    /// </summary>
    public Func<nint, IReadOnlyList<ReviewAction>, IReadOnlyList<ReviewAction>?>? ShowActionProfiles { get; init; }
}

/// <summary>
/// WP-35 GĐ3a (phương án B của spike S3, CHƯA XÁC NHẬN): KHUNG cửa sổ Settings Win32 gốc (view của <see cref="SettingsPresenter"/>; WPF:
/// <c>SettingsWindow</c>). Modal, kéo giãn; danh sách điều hướng trái + trang phải CUỘN DỌC (mỗi trang một cửa sổ chứa con có thanh cuộn
/// riêng, điều khiển trang là con của cửa sổ chứa nên bị cắt đúng vùng); Save (=OK) / Apply / Cancel, Defaults, Reset trang, Import,
/// Export; nhớ trang cuối (<see cref="SettingsPresenter.RememberPage"/>); Ctrl+Tab đổi trang; đổi ngôn ngữ làm mọi chữ đổi theo.
/// Trang là <see cref="ISettingsPageView"/> (một file mỗi trang); trang chưa có hiện ghi chú "chưa có" và GIỮ nguyên giá trị đã nạp
/// (<see cref="CollectAll"/> bắt đầu từ <c>LoadFields()</c> nên Save không làm mất cài đặt của trang chưa chuyển).
/// </summary>
internal sealed unsafe class SettingsDialog : NativeDialog
{
    // ---- id của khung (id trang = 1000 * (chỉ số trang + 1) + n) ----
    internal const int HeaderId = 100;
    internal const int NavId = 101;
    internal const int VersionId = 102;
    internal const int PageTitleId = 103;
    internal const int ScrollId = 104;
    internal const int ViewportBase = 110; // 110 + chỉ số trang
    internal const int ApplyId = 120;
    internal const int DefaultsId = 121;
    internal const int ResetPageId = 122;
    internal const int ExportId = 123;
    internal const int ImportId = 124;
    internal const int SaveId = IdOk;
    internal const int CancelId = IdCancel;
    internal const int PageIdStride = 1000;

    private const uint WmAppLanguage = WindowMessages.WmApp + 0x35;
    private const uint WmAppQueue = WindowMessages.WmApp + 0x36;
    private const nuint ForwardAllSubclassId = 0x53543335; // "ST35"
    private const nuint WheelSubclassId = 0x57483335; // "WH35"
    private const nuint KeyBoxSubclassId = 0x4B423335; // "KB35"

    private readonly HashSet<int> _keyBoxes = [];
    private readonly HashSet<int> _warningTexts = [];
    private nint _keyBoxBrush;
    private string? _actionsJson;
    private readonly SettingsPresenter _presenter;
    private readonly IReadOnlyList<ISettingsPageView> _pages;
    private readonly SettingsPageBuilder[] _builders;
    private readonly List<DialogItem> _items = [];
    private readonly Dictionary<int, SettingsLayoutElement> _groupsById = [];
    private readonly HashSet<int> _hidden = [];
    private readonly Dictionary<nint, (int X, int Y, int W, int H)> _placed = [];

    /// <summary>Handles of the page comboboxes (see <see cref="PageSurface.Place"/>: a combobox re-sizes itself while it is placed).</summary>
    private readonly HashSet<nint> _pageCombos = [];
    private readonly Dictionary<int, IReadOnlyList<Func<string>>> _comboItems = [];
    private readonly HashSet<int> _notes = [];
    private readonly int[] _scroll;
    private readonly int[] _contentHeight;
    private readonly bool[] _dirty;
    private readonly bool[] _created;
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly IDialogWarningSink _warnings;
    private readonly SettingsPageContext _context;
    private int _active = -1;
    private bool _saved;
    private bool _loading;
    private bool _subscribed;
    private bool _attached;
    private bool _inLanguageRefresh;
    private int _viewportWidth;
    private int _viewportHeight;

    public SettingsDialog(AppSettings current, SettingsStore? store = null, IImageDecoderFactory? decoderFactory = null,
        LocalizationService? localization = null, IUpdateChecker? updateChecker = null, SettingsDialogOptions? options = null,
        INativeDialogChrome? chrome = null)
        : base(chrome)
    {
        Options = (options ?? new SettingsDialogOptions()) with { };
        Files = Options.Files;
        DecoderFactory = decoderFactory;
        _presenter = new SettingsPresenter(current, store, localization, updateChecker, Options.PhysicalMemoryBytes);
        _warnings = Options.Warnings ?? TaskDialogWarningSink.Instance;
        Options = Options with { Version = Options.Version ?? BuildInfo.GetVersion(typeof(SettingsDialog).Assembly) };
        _pages = Options.Pages ?? SettingsPageCatalog.CreatePages();
        if (_pages.Count != SettingsPresenter.Pages.Count)
        {
            throw new ArgumentException("A settings page is required for every SettingsPresenter.Pages entry.", nameof(options));
        }

        _context = new SettingsPageContext(this);
        _builders = new SettingsPageBuilder[_pages.Count];
        _scroll = new int[_pages.Count];
        _contentHeight = new int[_pages.Count];
        _dirty = new bool[_pages.Count];
        _created = new bool[_pages.Count];
        BuildItems();
    }

    /// <summary>Presenter (test seam: ShellIntegration, ExecutablePath...).</summary>
    internal SettingsPresenter Presenter => _presenter;

    /// <summary>Bản làm việc của cài đặt: sau Save/Apply thành công đây là bản đã lưu.</summary>
    public AppSettings Settings => _presenter.Settings;

    /// <summary>True khi đã có ít nhất một lần Save/Apply thành công (kể cả khi sau đó bấm Cancel): người gọi nạp lại cấu hình.</summary>
    public bool Saved => _saved;

    internal SettingsDialogOptions Options { get; private set; }

    internal IShellFileDialog? Files { get; }

    internal IImageDecoderFactory? DecoderFactory { get; }

    /// <summary>Các trang theo thứ tự điều hướng (test).</summary>
    internal IReadOnlyList<ISettingsPageView> Pages => _pages;

    /// <summary>Chỉ số trang đang hiện.</summary>
    internal int ActivePageIndex => _active;

    /// <summary>Khoá trang đang hiện.</summary>
    internal string ActivePageKey => _active >= 0 ? _pages[_active].Key : string.Empty;

    /// <summary>Vị trí cuộn (px) của trang <paramref name="index"/>.</summary>
    internal int ScrollOffset(int index) => _scroll[index];

    /// <summary>Chiều cao nội dung (px) của trang <paramref name="index"/> ở bố cục gần nhất.</summary>
    internal int ContentHeight(int index) => _contentHeight[index];

    /// <summary>Chiều cao vùng nhìn (px) của trang.</summary>
    internal int ViewportHeight => _viewportHeight;

    internal static int ViewportId(int pageIndex) => ViewportBase + pageIndex;

    /// <summary>Id mọi điều khiển đã khai báo của trang <paramref name="index"/>, theo thứ tự khai báo (= thứ tự Tab; test).</summary>
    internal IReadOnlyList<DialogItem> PageItems(int index) => _builders[index].Items;

    /// <summary>Id điều khiển đầu tiên của trang (+1 = điều khiển đầu).</summary>
    internal static int PageIdBase(int pageIndex) => PageIdStride * (pageIndex + 1);

    protected override string Title => Tr.SettingsWindowTitle;

    protected override IReadOnlyList<DialogItem> Items => _items;

    protected override bool Resizable => true;

    protected override IReadOnlyList<int> HeaderControlIds => [HeaderId, PageTitleId];

    protected override int InitialFocusId => NavId;

    protected override int DefaultButtonId => SaveId;

    // ---- khai báo ---------------------------------------------------------------------------------------------------------

    private void BuildItems()
    {
        const uint child = DialogStyles.Child;
        const uint tab = DialogConstants.WsTabStop;
        const uint text = DialogStyles.Child | DialogConstants.SsNoPrefix;
        _items.Add(new(DialogControlClass.Static, HeaderId, Tr.SettingsHeader, text));
        _items.Add(new(DialogControlClass.ListBox, NavId, string.Empty,
            child | tab | DialogConstants.LbsNotify | DialogConstants.LbsNoIntegralHeight | ControlConstants.LbsOwnerDrawFixed
            | ControlConstants.LbsHasStrings, DialogConstants.WsExClientEdge));
        _items.Add(new(DialogControlClass.Static, VersionId, Options.Version is { Length: > 0 } v ? v : string.Empty, text));
        _items.Add(new(DialogControlClass.Static, PageTitleId, string.Empty, text));
        _items.Add(new(DialogControlClass.ScrollBar, ScrollId, string.Empty, child | ScrollConstants.SbsVert));
        for (var i = 0; i < _pages.Count; i++)
        {
            _items.Add(new(DialogControlClass.Static, ViewportId(i), string.Empty,
                text | ScrollConstants.WsClipChildren | ScrollConstants.WsClipSiblings, DialogConstants.WsExControlParent));
        }

        for (var i = 0; i < _pages.Count; i++)
        {
            var builder = new SettingsPageBuilder(PageIdBase(i));
            _pages[i].Declare(builder);
            _builders[i] = builder;
            foreach (var pair in builder.ComboItems)
            {
                _comboItems[pair.Key] = pair.Value;
            }

            foreach (var note in builder.Notes)
            {
                _notes.Add(note);
            }

            _keyBoxes.UnionWith(builder.KeyBoxes);
            _warningTexts.UnionWith(builder.WarningTexts);

            foreach (var element in Flatten(builder.Elements))
            {
                if (element is SettingsGroupElement group)
                {
                    _groupsById[group.TitleId] = group;
                }
            }
        }

        _items.Add(new(DialogControlClass.Button, DefaultsId, Tr.SettingsRestoreDefaults, child | tab | DialogConstants.BsPushButton));
        _items.Add(new(DialogControlClass.Button, ResetPageId, Tr.SettingsResetPage, child | tab | DialogConstants.BsPushButton));
        _items.Add(new(DialogControlClass.Button, ExportId, Tr.SettingsExportSettings, child | tab | DialogConstants.BsPushButton));
        _items.Add(new(DialogControlClass.Button, ImportId, Tr.SettingsImportSettings, child | tab | DialogConstants.BsPushButton));
        _items.Add(new(DialogControlClass.Button, CancelId, Tr.CommonCancel, child | tab | DialogConstants.BsPushButton));
        _items.Add(new(DialogControlClass.Button, ApplyId, Tr.CommonApply, child | tab | DialogConstants.BsPushButton));
        _items.Add(new(DialogControlClass.Button, SaveId, Tr.CommonSave, child | tab | DialogConstants.BsDefPushButton));
    }

    private static IEnumerable<SettingsLayoutElement> Flatten(IEnumerable<SettingsLayoutElement> elements)
    {
        foreach (var element in elements)
        {
            yield return element;
            if (element is SettingsGroupElement group)
            {
                foreach (var inner in Flatten(group.Children))
                {
                    yield return inner;
                }
            }
        }
    }

    // ---- khởi tạo ---------------------------------------------------------------------------------------------------------

    protected override void OnInitialized()
    {
        // Cửa sổ chứa trang: tra id xuyên cửa sổ, chuyển tiếp thông điệp điều khiển con lên hộp thoại. Điều khiển của một trang chỉ được
        // TẠO khi trang được mở lần đầu (EnsurePageCreated): mở cửa sổ không phải trả giá mọi trang (cột mốc "mở <= 150 ms").
        for (var i = 0; i < _pages.Count; i++)
        {
            var viewport = User32.GetDlgItem(Hwnd, ViewportId(i));
            AddChildHost(viewport);
            Forward(viewport, everything: true);
            User32.ShowWindow(viewport, WindowMessages.SwHide);
        }

        Forward(Item(NavId));
        for (var i = 0; i < _pages.Count; i++)
        {
            ListBoxControl.Add(Item(NavId), _pages[i].Title);
        }

        var key = Options.Target == SettingsTarget.ExternalEditor ? "Files" : SettingsPresenter.InitialPageKey;
        var index = Math.Max(0, _pages.ToList().FindIndex(page => page.Key == key));
        ListBoxControl.SelectSingle(Item(NavId), index);
        Localizer.CurrentChanged += OnLocalizerChanged;
        _subscribed = true;
        _attached = true;
        ActivatePage(index, remember: Options.Target != SettingsTarget.ExternalEditor, layout: false); // the framework lays out once, right after OnInitialized
        if (Options.Target == SettingsTarget.ExternalEditor)
        {
            // The framework puts the focus on the navigation list right after OnInitialized; the editor field takes it once the loop runs.
            PostToUi(() => FocusExternalEditor());
        }
    }

    /// <summary>
    /// "Open in external editor" without an editor configured: focuses the editor path field of the Files page (creating the page controls
    /// if needed) like the WPF window (<c>FocusExternalEditorOnLoad</c>). False when no page handles it.
    /// </summary>
    internal bool FocusExternalEditor()
    {
        for (var i = 0; i < _pages.Count; i++)
        {
            if (_pages[i].Key != "Files")
            {
                continue;
            }

            EnsurePageCreated(i);
            if (_active != i)
            {
                ActivatePage(i, remember: false);
            }

            return _pages[i].FocusExternalEditor();
        }

        return false;
    }

    /// <summary>
    /// Tạo điều khiển của trang <paramref name="index"/> (một lần): <c>CreateWindowEx</c> theo danh sách khai báo, con của cửa sổ chứa
    /// trang, font + giao diện tối, danh sách combobox/thanh trượt, rồi <see cref="ISettingsPageView.Attach"/> + nạp giá trị hiện tại
    /// của bản làm việc (nên trang tạo sau Defaults/Import vẫn hiện đúng).
    /// </summary>
    internal void EnsurePageCreated(int index)
    {
        if (_created[index] || Hwnd == 0)
        {
            return;
        }

        _created[index] = true;
        var viewport = Item(ViewportId(index));
        var items = _builders[index].Items;
        var controls = new List<DialogChromeControl>(items.Count);
        var groupBoxes = new List<nint>();
        var instance = Kernel32.GetModuleHandle(0);
        foreach (var item in items)
        {
            // The text provider again (not the text captured at Declare time): the language may have changed since the dialog was built.
            var text = _builders[index].Texts.TryGetValue(item.Id, out var provider) ? provider() : item.Text;
            // WS_CLIPSIBLINGS on every page control (a dialog template adds it itself, CreateWindowEx does not): the controls of a page are
            // siblings that overlap (a group box frame is a full-rect fill under the controls of its group) and are moved one by one on
            // scroll. Without it a window's paint DC and the copy-bits blit of SetWindowPos include the siblings above it, so a scroll
            // step copied and left stale pixels of other controls (ghost text/frames, duplicated lines).
            var control = User32.CreateWindowEx(item.ExStyle, DialogTemplate.WindowClassName(item.Class), text,
                item.Style | ScrollConstants.WsClipSiblings, 0, 0, 0, 0, viewport, item.Id, instance, 0);
            if (control == 0)
            {
                continue;
            }

            RegisterChild(control);
            if (item.Class == DialogControlClass.Button && (item.Style & 0xFu) == ControlConstants.BsGroupBox)
            {
                groupBoxes.Add(control);
            }

            controls.Add(new DialogChromeControl(control, item.Class));
            if (item.Class == DialogControlClass.ComboBox)
            {
                _pageCombos.Add(control);
                Forward(control);
            }
            else if (item.Class == DialogControlClass.Edit && (item.Style & DialogConstants.EsMultiline) == 0)
            {
                // Tall single-line boxes (number / key boxes): the text is centred vertically (it sticks to the top edge otherwise).
                EditTextCentering.Attach(control);
            }
        }

        // CreateWindowEx puts every new child BELOW its earlier siblings, so a group box (declared before its controls) ends up ABOVE them
        // and WS_CLIPSIBLINGS would clip every one of them away (blank page). Only after all siblings exist can the frames go to the
        // bottom of the z-order (one at a time, nothing is created after them): the controls of a group stay on top of its frame and keep
        // their clipping. Group boxes are not tab stops, so the Tab order is unchanged.
        foreach (var groupBox in groupBoxes)
        {
            User32.SetWindowPos(groupBox, WindowMessages.HwndBottom, 0, 0, 0, 0,
                WindowMessages.SwpNoMove | WindowMessages.SwpNoSize | WindowMessages.SwpNoActivate);
        }

        ApplyChrome(controls);
        var builder = _builders[index];
        foreach (var (id, texts) in builder.ComboItems)
        {
            var combo = Item(id);
            ComboBoxControl.Clear(combo);
            foreach (var text in texts)
            {
                ComboBoxControl.Add(combo, text());
            }

            ComboBoxControl.Select(combo, 0);
        }

        foreach (var (id, range) in builder.Sliders)
        {
            TrackbarControl.Configure(Item(id), range.Min, range.Max, 1, 1, Math.Max(1, (range.Max - range.Min) / 10));
        }

        foreach (var id in builder.KeyBoxes)
        {
            HookKeyBox(Item(id));
        }

        _pages[index].Attach(_context);
        _loading = true;
        try
        {
            _pages[index].Load(WorkingFields());
        }
        finally
        {
            _loading = false;
        }

        _dirty[index] = true;
    }

    /// <summary>Tạo mọi trang (test và tính năng cần duyệt hết điều khiển).</summary>
    internal void EnsureAllPagesCreated()
    {
        for (var i = 0; i < _pages.Count; i++)
        {
            EnsurePageCreated(i);
        }
    }

    /// <summary>Trang đã được tạo điều khiển chưa (test).</summary>
    internal bool IsPageCreated(int index) => _created[index];

    protected override void OnClosed()
    {
        if (_subscribed)
        {
            Localizer.CurrentChanged -= OnLocalizerChanged;
            _subscribed = false;
        }

        _presenter.CancelUpdateCheck();
        foreach (var page in _pages)
        {
            page.OnClosed();
        }

        if (_keyBoxBrush != 0)
        {
            Gdi32.DeleteObject(_keyBoxBrush);
            _keyBoxBrush = 0;
        }
    }

    // ---- nạp / thu giá trị ------------------------------------------------------------------------------------------------

    /// <summary>Form của bản làm việc cho một trang mới tạo: <c>LoadFields()</c> + JSON action đang sửa (nếu đã đổi qua trình sửa Action profiles).</summary>
    private SettingsFormState WorkingFields()
    {
        var state = _presenter.LoadFields();
        if (_actionsJson is not null)
        {
            state.ActionsJson = _actionsJson;
        }
        else
        {
            _actionsJson = state.ActionsJson;
        }

        return state;
    }

    /// <summary>
    /// JSON action đang làm việc (ô chữ JSON của trang Files và nút "Edit action profiles" của trang Shortcuts cùng sửa MỘT giá trị:
    /// như ô <c>ActionsText</c> chung của bản WPF). Khởi tạo từ bản làm việc; Defaults/Import đặt lại.
    /// </summary>
    internal string ActionsJson => _actionsJson ??= _presenter.LoadFields().ActionsJson;

    /// <summary>JSON action theo các ô hiện có của mọi trang (kể cả sửa tay chưa lưu); ghi nhớ làm giá trị làm việc.</summary>
    internal string RefreshActionsJsonFromPages()
    {
        var json = CollectAll().ActionsJson;
        _actionsJson = json;
        return json;
    }

    /// <summary>
    /// Một trang (trình sửa Action profiles) đổi JSON action: ghi vào bản làm việc rồi nạp lại MỌI trang khác đã tạo từ form hiện tại
    /// (các sửa chưa lưu khác được giữ nguyên) để ô chữ JSON, cảnh báo trùng phím... thấy giá trị mới.
    /// </summary>
    internal void SetActionsJson(string json, ISettingsPageView? source)
    {
        ArgumentNullException.ThrowIfNull(json);
        _actionsJson = json;
        var state = CollectAll();
        state.ActionsJson = json; // a page with its own JSON box collected its stale text above
        _loading = true;
        try
        {
            for (var i = 0; i < _pages.Count; i++)
            {
                if (_created[i] && !ReferenceEquals(_pages[i], source))
                {
                    _pages[i].Load(state);
                }
            }
        }
        finally
        {
            _loading = false;
        }

        _dirty.AsSpan().Fill(true);
        RelayoutActivePage();
    }

    private void LoadAll(SettingsFormState state)
    {
        _actionsJson = state.ActionsJson;
        _loading = true;
        try
        {
            for (var i = 0; i < _pages.Count; i++)
            {
                if (_created[i])
                {
                    _pages[i].Load(state);
                }
            }
        }
        finally
        {
            _loading = false;
        }

        _dirty.AsSpan().Fill(true);
        if (_active >= 0)
        {
            RelayoutActivePage();
        }
    }

    /// <summary>Form đầy đủ: giá trị đã nạp của bản làm việc, rồi mỗi trang ghi đè phần của nó (trang chưa chuyển giữ nguyên).</summary>
    internal SettingsFormState CollectAll()
    {
        var state = WorkingFields();
        for (var i = 0; i < _pages.Count; i++)
        {
            if (_created[i])
            {
                _pages[i].Collect(state);
            }
        }

        return state;
    }

    // ---- điều hướng -------------------------------------------------------------------------------------------------------

    internal void ActivatePage(int index, bool remember = true, bool layout = true)
    {
        if (index < 0 || index >= _pages.Count)
        {
            return;
        }

        if (_active >= 0 && _active != index)
        {
            User32.ShowWindow(Item(ViewportId(_active)), WindowMessages.SwHide);
        }

        EnsurePageCreated(index);
        _active = index;
        if (remember)
        {
            SettingsPresenter.RememberPage(_pages[index].Key);
        }

        if (ListBoxControl.SelectedIndex(Item(NavId)) != index)
        {
            ListBoxControl.SelectSingle(Item(NavId), index);
        }

        SetItemText(PageTitleId, _pages[index].Title);
        _scroll[index] = 0;
        _dirty[index] = true;
        _pages[index].OnActivated();
        User32.ShowWindow(Item(ViewportId(index)), WindowMessages.SwShow);
        if (layout)
        {
            RunLayout();
        }
    }

    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab: trang kế (vòng tròn).</summary>
    internal void CyclePage(bool forward)
        => ActivatePage(SettingsPresenter.NextPageIndex(Math.Max(0, _active), _pages.Count, forward));

    /// <summary>Test: đưa một phím qua đúng bước tiền xử lý mà vòng lặp dialog làm trước <c>IsDialogMessage</c>.</summary>
    internal bool HandleKeyForTest(in Msg message) => OnDialogKey(message);

    protected override bool OnDialogKey(in Msg message)
    {
        const uint VkTab = 0x09;
        const int VkControl = 0x11;
        const int VkShift = 0x10;
        if (message.Message == WindowMessages.WmKeyDown && message.WParam == (nint)VkTab)
        {
            var control = Options.ControlKeyDown?.Invoke() ?? (User32.GetKeyState(VkControl) < 0);
            if (!control)
            {
                return false; // plain Tab is never captured: it moves focus (also out of a key box)
            }

            CyclePage(forward: User32.GetKeyState(VkShift) >= 0);
            return true;
        }

        // A key box takes the key BEFORE IsDialogMessage sees it: Enter would press Save, arrows/Space would be dialog navigation.
        return message.Message is WindowMessages.WmKeyDown or WindowMessages.WmSysKeyDown && IsKeyBox(message.Hwnd)
            && CaptureKey(message.Hwnd, message.WParam, message.LParam);
    }

    // ---- ô bắt phím (trang Shortcuts) --------------------------------------------------------------------------------------------

    /// <summary><paramref name="hwnd"/> có phải ô bắt phím của một trang đã tạo không.</summary>
    internal bool IsKeyBox(nint hwnd)
    {
        if (hwnd == 0 || _keyBoxes.Count == 0)
        {
            return false;
        }

        var id = User32.GetDlgCtrlID(hwnd);
        return id != 0 && _keyBoxes.Contains(id) && Item(id) == hwnd;
    }

    /// <summary>
    /// Ô bắt phím: WM_KEYDOWN/WM_SYSKEYDOWN -&gt; <see cref="KeyIdMapping"/> -&gt; <see cref="SettingsPresenter.CaptureShortcut"/> (cùng bộ ghi của
    /// bản WPF: tên phím chuẩn hoá, KHÔNG có Ctrl/Shift/Alt; Tab/Esc/phím bổ trợ/Win... trả null = không nuốt, giữ chuyển focus và Esc huỷ).
    /// IME báo VK_PROCESSKEY thì lấy phím thật bằng <c>ImmGetVirtualKey</c>. Ghi chữ -&gt; EN_CHANGE như một lần sửa.
    /// </summary>
    internal bool CaptureKey(nint hwnd, nint wParam, nint lParam)
    {
        var id = User32.GetDlgCtrlID(hwnd);
        if (id == 0 || !_keyBoxes.Contains(id))
        {
            return false;
        }

        var virtualKey = (int)(wParam & 0xFFFF);
        if (virtualKey == Imm32.VkProcessKey)
        {
            var real = (int)Imm32.ImmGetVirtualKey(hwnd);
            if (real != 0)
            {
                virtualKey = real;
            }
        }

        var key = KeyIdMapping.FromVirtualKey(virtualKey, (lParam & 0x01000000) != 0);
        if (SettingsPresenter.CaptureShortcut(key, key.ToString()) is not { } text)
        {
            return false;
        }

        SetItemText(id, text);
        User32.SendMessage(hwnd, DialogConstants.EmSetSel, 0, -1);
        return true;
    }

    private void HookKeyBox(nint box)
    {
        if (box == 0)
        {
            return;
        }

        // No IME context: a Vietnamese/Telex IME can neither compose into the box nor swallow the key.
        _ = Imm32.ImmAssociateContext(box, 0);
        CommonControls.SetWindowSubclass(box, &KeyBoxProc, KeyBoxSubclassId, SelfRefData);
    }

    [UnmanagedCallersOnly]
    private static nint KeyBoxProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint refData)
    {
        try
        {
            switch (message)
            {
                case WindowMessages.WmKeyDown:
                case WindowMessages.WmSysKeyDown:
                    if (refData != 0 && GCHandle.FromIntPtr((nint)refData).Target is SettingsDialog dialog && dialog.CaptureKey(hwnd, wParam, lParam))
                    {
                        return 0;
                    }

                    break;
                case WindowMessages.WmChar:
                case WindowMessages.WmSysChar:
                case 0x0103: // WM_DEADCHAR
                case 0x0107: // WM_SYSDEADCHAR
                case 0x0286: // WM_IME_CHAR
                case 0x010D: // WM_IME_STARTCOMPOSITION
                case 0x010F: // WM_IME_COMPOSITION
                case 0x0302: // WM_PASTE
                case 0x0300: // WM_CUT
                case 0x0303: // WM_CLEAR
                case 0x0304: // WM_UNDO
                    return 0; // no text ever reaches the box except through CaptureKey
                case ControlConstants.WmNcDestroy:
                    CommonControls.RemoveWindowSubclass(hwnd, &KeyBoxProc, id);
                    break;
            }
        }
        catch (Exception)
        {
            // Ngoại lệ không được vượt ranh giới unmanaged.
        }

        return CommonControls.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    /// <summary>Màu của ô bắt phím (chỉ đọc nên gửi WM_CTLCOLORSTATIC): như ô nhập, chữ sáng.</summary>
    private nint KeyBoxColors(nint hdc)
    {
        _ = Gdi32.SetTextColor(hdc, DialogColors.TextStrong);
        _ = Gdi32.SetBkColor(hdc, DialogColors.Input);
        if (_keyBoxBrush == 0)
        {
            _keyBoxBrush = Gdi32.CreateSolidBrush(DialogColors.Input);
        }

        return _keyBoxBrush;
    }

    // ---- lệnh -------------------------------------------------------------------------------------------------------------

    protected override bool OnCommand(int id, int notification)
    {
        switch (id)
        {
            case NavId when notification == ControlConstants.LbnSelChange:
                ActivatePage(Math.Max(0, ListBoxControl.SelectedIndex(Item(NavId))));
                return true;
            case ApplyId when notification == DialogConstants.BnClicked:
                _ = TrySaveAll();
                return true;
            case DefaultsId when notification == DialogConstants.BnClicked:
                LoadAll(_presenter.ApplyDefaults());
                return true;
            case ResetPageId when notification == DialogConstants.BnClicked:
                ResetActivePage();
                return true;
            case ExportId when notification == DialogConstants.BnClicked:
                ExportSettings();
                return true;
            case ImportId when notification == DialogConstants.BnClicked:
                ImportSettings();
                return true;
        }

        if (_loading)
        {
            return true; // sự kiện EN_CHANGE/CBN do chính việc nạp giá trị gây ra
        }

        var index = PageOf(id);
        return index >= 0 && _pages[index].OnCommand(id, notification);
    }

    private static int PageOf(int id) => id >= PageIdStride ? (id / PageIdStride) - 1 : -1;

    protected override void OnScroll(int id, int code)
    {
        if (id == ScrollId)
        {
            ScrollByBar(code);
            return;
        }

        var index = PageOf(id);
        if (index >= 0 && index < _pages.Count)
        {
            _pages[index].OnScroll(id);
        }
    }

    protected override bool OnOk() => TrySaveAll();

    internal bool TrySaveAll()
    {
        var result = _presenter.TrySave(CollectAll());
        switch (result.Outcome)
        {
            case SettingsSaveOutcome.Invalid:
                Warn(Tr.DialogSettingsInvalidTitle, result.Message ?? string.Empty);
                FocusField(result.Focus);
                return false;
            case SettingsSaveOutcome.SaveFailed:
                Warn(Tr.DialogSettingsSaveFailedTitle, result.Message ?? string.Empty);
                return false;
            default:
                _saved = true;
                return true;
        }
    }

    private void FocusField(SettingsFocusField field)
    {
        if (field == SettingsFocusField.None)
        {
            return;
        }

        for (var i = 0; i < _pages.Count; i++)
        {
            if (_pages[i].FocusTarget(field) is { } id)
            {
                ActivatePage(i);
                FocusControl(id);
                return;
            }
        }
    }

    /// <summary>"Đặt lại trang này": mặc định built-in của CHỈ trang đang hiện (tính trên bản sao nên bản làm việc không đổi tới khi Save).</summary>
    internal void ResetActivePage()
    {
        if (_active < 0)
        {
            return;
        }

        var scratch = new SettingsPresenter(_presenter.Settings, store: null, localization: null);
        var defaults = scratch.ApplyDefaults();
        _loading = true;
        try
        {
            _pages[_active].Load(defaults);
        }
        finally
        {
            _loading = false;
        }

        RelayoutActivePage();
    }

    private void ExportSettings()
    {
        if (Files is null)
        {
            return;
        }

        var path = Files.PickSaveFile(Hwnd, Tr.DialogExportSettingsTitle, FileFilterText.Parse(Tr.DialogFileFilterJson), "json",
            "PhotoReview-settings.json", SettingsPresenter.SettingsFileDialogDirectory);
        if (path is null)
        {
            return;
        }

        if (_presenter.ExportSettingsTo(path) is { } error)
        {
            Warn(Tr.DialogSettingsInvalidTitle, error);
        }
    }

    private void ImportSettings()
    {
        if (Files is null)
        {
            return;
        }

        var path = Files.PickOpenFile(Hwnd, Tr.DialogImportSettingsTitle, FileFilterText.Parse(Tr.DialogFileFilterJson),
            SettingsPresenter.SettingsFileDialogDirectory);
        if (path is null)
        {
            return;
        }

        ImportSettingsFrom(path);
    }

    /// <summary>Thân của Import khi đã có file (test seam: không lái được hộp thoại chọn file).</summary>
    internal void ImportSettingsFrom(string path)
    {
        var result = _presenter.ImportSettingsFrom(path, () => LoadAll(_presenter.LoadFields()));
        if (result.Error is not null)
        {
            Warn(Tr.DialogSettingsInvalidTitle, result.Error);
        }
        else if (result.RepairsNotice is not null)
        {
            Warn(Tr.DialogImportSettingsTitle, result.RepairsNotice);
        }
    }

    internal void Warn(string title, string message) => _warnings.ShowWarning(Hwnd, title, message);

    // ---- chuyển tiếp thông điệp / hàng đợi UI / ngôn ngữ ---------------------------------------------------------------------

    /// <summary>Subclass điều khiển: <paramref name="everything"/> = chuyển tiếp mọi thông điệp "gửi cho cha" (cửa sổ chứa trang), ngược lại chỉ con lăn.</summary>
    private void Forward(nint control, bool everything = false)
    {
        if (control == 0)
        {
            return;
        }

        CommonControls.SetWindowSubclass(control, &ForwardProc, everything ? ForwardAllSubclassId : WheelSubclassId, SelfRefData);
    }

    /// <summary>
    /// Subclass của cửa sổ chứa trang / danh sách điều hướng / combobox: thông điệp mà điều khiển con gửi lên "cha" (cửa sổ chứa không
    /// có DlgProc) được chuyển nguyên cho hộp thoại; con lăn chuột được hộp thoại dùng để cuộn trang (trừ combobox đang thả).
    /// </summary>
    [UnmanagedCallersOnly]
    private static nint ForwardProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint refData)
    {
        try
        {
            if (refData != 0 && GCHandle.FromIntPtr((nint)refData).Target is SettingsDialog dialog && dialog.Hwnd != 0)
            {
                var forward = false;
                if (message == DialogConstants.WmCtlColorStatic && id == ForwardAllSubclassId && dialog.IsKeyBox(lParam))
                {
                    return dialog.KeyBoxColors(wParam);
                }

                if (message == WindowMessages.WmMouseWheel)
                {
                    forward = !ComboBoxControl.IsDropped(hwnd);
                }
                else if (id == ForwardAllSubclassId)
                {
                    forward = message is WindowMessages.WmCommand or ControlConstants.WmNotify or ControlConstants.WmDrawItem
                        or ControlConstants.WmMeasureItem or ControlConstants.WmHScroll or ControlConstants.WmVScroll
                        or >= DialogConstants.WmCtlColorMsgBox and <= DialogConstants.WmCtlColorStatic;
                }

                if (forward && hwnd != dialog.Hwnd)
                {
                    return User32.SendMessage(dialog.Hwnd, message, wParam, lParam);
                }
            }

            if (message == ControlConstants.WmNcDestroy)
            {
                CommonControls.RemoveWindowSubclass(hwnd, &ForwardProc, id);
            }
        }
        catch (Exception)
        {
            // Ngoại lệ không được vượt ranh giới unmanaged.
        }

        return CommonControls.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    protected override bool OnWindowMessage(uint message, nint wParam, nint lParam, out nint result)
    {
        result = 0;
        switch (message)
        {
            case WindowMessages.WmMouseWheel:
                ScrollByWheel((short)((long)wParam >> 16 & 0xFFFF));
                return true;
            case WmAppQueue:
                while (_queue.TryDequeue(out var action))
                {
                    action();
                }

                return true;
            case WmAppLanguage:
                RefreshLocalizedTexts();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Chạy <paramref name="action"/> trên UI thread của cửa sổ; bỏ qua nếu cửa sổ đã đóng. An toàn từ mọi luồng.</summary>
    internal void PostToUi(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var hwnd = Hwnd;
        if (hwnd == 0)
        {
            return;
        }

        _queue.Enqueue(action);
        User32.PostMessage(hwnd, WmAppQueue, 0, 0);
    }

    private void OnLocalizerChanged(object? sender, EventArgs e)
    {
        var hwnd = Hwnd;
        if (hwnd != 0)
        {
            User32.PostMessage(hwnd, WmAppLanguage, 0, 0); // có thể gọi từ luồng bất kỳ
        }
    }

    /// <summary>Đổi ngôn ngữ (hoặc nạp lại bản dịch): đặt lại mọi chữ, danh sách combobox, tiêu đề trang, rồi bố cục lại.</summary>
    internal void RefreshLocalizedTexts()
    {
        if (Hwnd == 0 || _inLanguageRefresh)
        {
            return;
        }

        _inLanguageRefresh = true;
        try
        {
            User32.SetWindowText(Hwnd, Title);
            ApplyTextsToControls();
            SetItemText(HeaderId, Tr.SettingsHeader);
            SetItemText(DefaultsId, Tr.SettingsRestoreDefaults);
            SetItemText(ResetPageId, Tr.SettingsResetPage);
            SetItemText(ExportId, Tr.SettingsExportSettings);
            SetItemText(ImportId, Tr.SettingsImportSettings);
            SetItemText(CancelId, Tr.CommonCancel);
            SetItemText(ApplyId, Tr.CommonApply);
            SetItemText(SaveId, Tr.CommonSave);
            var nav = Item(NavId);
            ListBoxControl.Clear(nav);
            foreach (var page in _pages)
            {
                ListBoxControl.Add(nav, page.Title);
            }

            if (_active >= 0)
            {
                ListBoxControl.SelectSingle(nav, _active);
                SetItemText(PageTitleId, _pages[_active].Title);
            }

            foreach (var (id, items) in _comboItems)
            {
                var combo = Item(id);
                if (combo == 0)
                {
                    continue; // the page has not been opened yet: its combos are filled (localized) when it is created
                }

                var selected = ComboBoxControl.SelectedIndex(combo);
                ComboBoxControl.Clear(combo);
                foreach (var text in items)
                {
                    ComboBoxControl.Add(combo, text());
                }

                ComboBoxControl.Select(combo, Math.Max(0, selected));
            }

            for (var i = 0; i < _pages.Count; i++)
            {
                if (_created[i])
                {
                    _pages[i].OnLanguageChanged();
                }
            }

            _dirty.AsSpan().Fill(true);
            RunLayout();
        }
        finally
        {
            _inLanguageRefresh = false;
        }
    }

    private void ApplyTextsToControls()
    {
        foreach (var builder in _builders)
        {
            foreach (var (id, text) in builder.Texts)
            {
                SetItemText(id, text());
            }
        }
    }

    // ---- truy cập điều khiển cho trang ------------------------------------------------------------------------------------------

    internal string GetControlText(int id) => GetItemText(id);

    internal void SetControlText(int id, string text)
        => SetItemText(id, IsButton(Item(id)) ? text.Replace("&", "&&", StringComparison.Ordinal) : text);

    internal bool IsControlChecked(int id) => ButtonStateControl.IsChecked(Item(id));

    internal void SetControlChecked(int id, bool value) => ButtonStateControl.SetChecked(Item(id), value);

    internal int ComboSelectedIndex(int id) => ComboBoxControl.SelectedIndex(Item(id));

    internal void SetComboSelectedIndex(int id, int index) => ComboBoxControl.Select(Item(id), index);

    internal void SetComboItems(int id, IReadOnlyList<string> items, int selected)
    {
        var combo = Item(id);
        ComboBoxControl.Clear(combo);
        foreach (var text in items)
        {
            ComboBoxControl.Add(combo, text);
        }

        ComboBoxControl.Select(combo, items.Count == 0 ? -1 : Math.Clamp(selected, 0, items.Count - 1));
    }

    internal int SliderValue(int id) => TrackbarControl.Position(Item(id));

    internal void SetSliderValue(int id, int value) => TrackbarControl.SetPosition(Item(id), value);

    internal void SetSliderRange(int id, int min, int max)
    {
        var slider = Item(id);
        var position = TrackbarControl.Position(slider);
        TrackbarControl.Configure(slider, min, max, 1, 1, Math.Max(1, (max - min) / 10));
        TrackbarControl.SetPosition(slider, Math.Clamp(position, min, max));
    }

    /// <summary>Tooltip dạng callback (chữ hỏi lại mỗi lần hiện) cho một điều khiển thường (nút tick...).</summary>
    internal void SetControlTooltip(int id, Func<string> text) => SetItemTooltips(id, _ => text());

    internal void SetControlEnabled(int id, bool enabled) => User32.EnableWindow(Item(id), enabled);

    internal bool IsControlEnabled(int id) => User32.IsWindowEnabled(Item(id));

    internal void SetControlVisible(int id, bool visible)
    {
        if (_groupsById.TryGetValue(id, out var group))
        {
            group.Visible = visible;
        }
        else if (visible)
        {
            _hidden.Remove(id);
        }
        else
        {
            _hidden.Add(id);
        }

        RelayoutActivePage();
    }

    internal bool IsControlVisible(int id)
        => _groupsById.TryGetValue(id, out var group) ? group.Visible : !_hidden.Contains(id);

    internal void FocusControl(int id)
    {
        var control = Item(id);
        if (control == 0)
        {
            return;
        }

        EnsureVisible(id);
        User32.SetFocus(control);
    }

    internal void RelayoutActivePage()
    {
        if (_active >= 0)
        {
            _dirty[_active] = true;
            RunLayout();
        }
    }

    // ---- bố cục -----------------------------------------------------------------------------------------------------------------

    private int Margin => Dx(10);

    private int FieldHeight => Math.Max(Dy(14), LineHeight + Dy(6));

    private int ButtonHeight => Math.Max(Dy(14), LineHeight + Dy(6));

    private int ScrollBarWidth => Math.Max(Dx(10), 16);

    private int ButtonWidthOf(int id) => Math.Max(Dx(50), MeasureTextWidth(GetItemText(id)) + Dx(16));

    private static readonly int[] LeftButtons = [DefaultsId, ResetPageId, ExportId, ImportId];

    private static readonly int[] RightButtons = [CancelId, ApplyId, SaveId];

    private int RowWidth(int[] ids) => ids.Sum(ButtonWidthOf) + ((ids.Length - 1) * Dx(6));

    protected override (int Width, int Height) PreferredClientSize() => (Dx(540), Dy(330));

    protected override (int Width, int Height) MinimumClientSize()
        => (Math.Max(Dx(360), Math.Max(RowWidth(LeftButtons), RowWidth(RightButtons)) + (2 * Margin)), Dy(230));

    protected override void LayoutControls(int clientWidth, int clientHeight)
    {
        var m = Margin;
        var buttonHeight = ButtonHeight;
        var leftWidth = RowWidth(LeftButtons);
        var rightWidth = RowWidth(RightButtons);
        var singleRow = leftWidth + Dx(16) + rightWidth <= clientWidth - (2 * m);

        // Hàng nút dưới cùng (một hàng; hẹp thì nhóm trái nằm trên nhóm phải).
        var rightTop = clientHeight - m - buttonHeight;
        PlaceRow(clientWidth - m - rightWidth, rightTop, buttonHeight, RightButtons);
        int leftTop;
        if (singleRow)
        {
            leftTop = rightTop;
        }
        else
        {
            leftTop = rightTop - Dy(4) - buttonHeight;
        }

        PlaceRow(m, leftTop, buttonHeight, LeftButtons);
        var contentBottom = leftTop - Dy(8);

        // Cột trái: tiêu đề, danh sách điều hướng, phiên bản.
        var navWidth = Math.Clamp(clientWidth / 4, Dx(90), Dx(130));
        var y = m;
        var header = MeasureItemHeight(HeaderId, navWidth);
        Place(HeaderId, m, y, navWidth, header);
        y += header + Dy(6);
        var version = string.IsNullOrEmpty(GetItemText(VersionId)) ? 0 : MeasureItemHeight(VersionId, navWidth);
        var navBottom = contentBottom - (version > 0 ? version + Dy(4) : 0);
        Place(NavId, m, y, navWidth, Math.Max(Dy(40), navBottom - y));
        SetListItemHeight(NavId, LineHeight + Dy(6));
        if (version > 0)
        {
            Place(VersionId, m, contentBottom - version, navWidth, version);
        }
        else
        {
            Place(VersionId, m, contentBottom, navWidth, 0);
        }

        // Cột phải: tiêu đề trang, cửa sổ chứa trang + thanh cuộn.
        var rightX = m + navWidth + m;
        var rightW = Math.Max(Dx(100), clientWidth - rightX - m);
        var titleHeight = MeasureItemHeight(PageTitleId, rightW);
        Place(PageTitleId, rightX, m, rightW, titleHeight);
        var viewportY = m + titleHeight + Dy(6);
        var scrollW = ScrollBarWidth;
        _viewportWidth = Math.Max(Dx(100), rightW - scrollW - Dx(4));
        _viewportHeight = Math.Max(Dy(40), contentBottom - viewportY);
        for (var i = 0; i < _pages.Count; i++)
        {
            Place(ViewportId(i), rightX, viewportY, _viewportWidth, _viewportHeight);
        }

        Place(ScrollId, rightX + rightW - scrollW, viewportY, scrollW, _viewportHeight);
        if (_active >= 0)
        {
            LayoutPage(_active);
        }
    }

    private void PlaceRow(int x, int y, int height, int[] ids)
    {
        foreach (var id in ids)
        {
            var width = ButtonWidthOf(id);
            Place(id, x, y, width, height);
            x += width + Dx(6);
        }
    }

    /// <summary>Đặt điều khiển của trang <paramref name="index"/> theo vùng nhìn hiện tại và vị trí cuộn; cập nhật thanh cuộn.</summary>
    private void LayoutPage(int index)
    {
        if (!_attached || _viewportWidth <= 0)
        {
            return;
        }

        var surface = new PageSurface(this);
        var width = Math.Max(Dx(100), _viewportWidth - Dx(2));
        for (var pass = 0; pass < 2; pass++)
        {
            surface.Origin = -_scroll[index];
            var cursor = 0;
            foreach (var element in _builders[index].Elements)
            {
                cursor += element.Place(surface, 0, surface.Origin + cursor, width);
            }

            _contentHeight[index] = cursor;
            var max = Math.Max(0, cursor - _viewportHeight);
            if (_scroll[index] <= max)
            {
                break;
            }

            _scroll[index] = max;
        }

        _dirty[index] = false;
        UpdateScrollBar(index);
    }

    private void UpdateScrollBar(int index)
    {
        var bar = Item(ScrollId);
        var content = _contentHeight[index];
        var needed = content > _viewportHeight;
        User32.ShowWindow(bar, needed ? WindowMessages.SwShowNa : WindowMessages.SwHide);
        if (!needed)
        {
            return;
        }

        var info = new ScrollInfo
        {
            Size = (uint)sizeof(ScrollInfo),
            Mask = ScrollConstants.SifRange | ScrollConstants.SifPage | ScrollConstants.SifPos,
            Min = 0,
            Max = content - 1,
            Page = (uint)Math.Max(1, _viewportHeight),
            Pos = _scroll[index],
        };
        User32.SetScrollInfo(bar, ScrollConstants.SbCtl, &info, true);
    }

    private void SetScroll(int value)
    {
        if (_active < 0)
        {
            return;
        }

        var max = Math.Max(0, _contentHeight[_active] - _viewportHeight);
        var clamped = Math.Clamp(value, 0, max);
        if (clamped == _scroll[_active])
        {
            return;
        }

        _scroll[_active] = clamped;
        LayoutPage(_active);
    }

    private void ScrollByBar(int code)
    {
        if (_active < 0)
        {
            return;
        }

        var info = new ScrollInfo { Size = (uint)sizeof(ScrollInfo), Mask = ScrollConstants.SifPos | ScrollConstants.SifTrackPos };
        User32.GetScrollInfo(Item(ScrollId), ScrollConstants.SbCtl, &info);
        var line = Math.Max(1, LineHeight * 2);
        var current = _scroll[_active];
        var target = code switch
        {
            ScrollConstants.SbLineUp => current - line,
            ScrollConstants.SbLineDown => current + line,
            ScrollConstants.SbPageUp => current - Math.Max(line, _viewportHeight - line),
            ScrollConstants.SbPageDown => current + Math.Max(line, _viewportHeight - line),
            ScrollConstants.SbTop => 0,
            ScrollConstants.SbBottom => int.MaxValue,
            ScrollConstants.SbThumbTrack or ScrollConstants.SbThumbPosition => info.TrackPos,
            _ => current,
        };
        SetScroll(target);
    }

    private void ScrollByWheel(int delta)
    {
        // 120 = một nấc = 3 dòng.
        var lines = delta / 120.0 * 3;
        SetScroll(_scroll[Math.Max(0, _active)] - (int)Math.Round(lines * LineHeight));
    }

    /// <summary>Cuộn trang đang hiện sao cho điều khiển <paramref name="id"/> nằm trong vùng nhìn (focus theo Tab/lỗi).</summary>
    internal void EnsureVisible(int id)
    {
        var control = Item(id);
        if (control == 0 || _active < 0 || PageOf(id) != _active)
        {
            return;
        }

        var viewport = Item(ViewportId(_active));
        Rect rect = default;
        User32.GetWindowRect(control, &rect);
        var topLeft = new Point { X = rect.Left, Y = rect.Top };
        User32.ScreenToClient(viewport, &topLeft);
        var top = topLeft.Y;
        var bottom = top + (rect.Bottom - rect.Top);
        if (top < 0)
        {
            SetScroll(_scroll[_active] + top - Dy(4));
        }
        else if (bottom > _viewportHeight)
        {
            SetScroll(_scroll[_active] + (bottom - _viewportHeight) + Dy(4));
        }
    }

    protected override uint StaticTextColor(int id)
        => _notes.Contains(id) ? DialogColors.FromKey("Dark.TextMuted")
            : _warningTexts.Contains(id) ? DialogColors.Warning
            : base.StaticTextColor(id);

    /// <summary>Tooltip chữ cố định cho điều khiển của trang (nút xoá phím...). Gọi một lần khi trang tạo; không tự đổi theo ngôn ngữ.</summary>
    internal void SetControlTooltip(int id, string text) => SetTooltip(id, text);

    /// <summary>Id của ô bắt phím (test).</summary>
    internal bool IsKeyBoxId(int id) => _keyBoxes.Contains(id);

    /// <summary>Bề mặt đo/đặt của <see cref="SettingsLayoutElement"/> trên cửa sổ thật.</summary>
    private sealed class PageSurface(SettingsDialog dialog) : ISettingsLayoutSurface
    {
        public int Origin { get; set; }

        public int Dx(int dialogUnits) => dialog.Dx(dialogUnits);

        public int Dy(int dialogUnits) => dialog.Dy(dialogUnits);

        public int LineHeight => dialog.LineHeight;

        public int FieldHeight => dialog.FieldHeight;

        public string TextOf(int id) => dialog.GetItemText(id);

        public int TextHeight(int id, int width) => dialog.MeasureItemHeight(id, width);

        public int TextWidth(string text) => dialog.MeasureTextWidth(text);

        public bool IsHidden(int id) => dialog._hidden.Contains(id);

        public int ComboDropHeight(int id)
        {
            var combo = dialog.Item(id);
            if (combo == 0)
            {
                return 0;
            }

            var itemHeight = (int)User32.SendMessage(combo, 0x0154 /* CB_GETITEMHEIGHT */, 0, 0);
            if (itemHeight <= 0)
            {
                itemHeight = dialog.LineHeight + dialog.Dy(2);
            }

            var count = Math.Clamp(ComboBoxControl.Count(combo), 1, 12);
            return (count * itemHeight) + dialog.Dy(4);
        }

        public void Place(int id, int x, int y, int width, int height)
        {
            var control = dialog.Item(id);
            if (control == 0)
            {
                return;
            }

            if (dialog._hidden.Contains(id))
            {
                Hide(id);
                return;
            }

            // One SetWindowPos per control (position + size + show) and none when nothing changed: a page of ~80 controls is laid out
            // on every resize/scroll/language change and creating-then-moving was the bulk of the open time.
            var rect = (X: x, Y: y, W: Math.Max(0, width), H: Math.Max(0, height));
            if (dialog._placed.TryGetValue(control, out var last) && last == rect)
            {
                return;
            }

            User32.SetWindowPos(control, 0, rect.X, rect.Y, rect.W, rect.H,
                WindowMessages.SwpNoZOrder | WindowMessages.SwpNoActivate | WindowMessages.SwpShowWindow);
            dialog._placed[control] = rect;
            if (dialog._pageCombos.Contains(control))
            {
                // A dropdown-list combobox shrinks itself to its closed height INSIDE the SetWindowPos above (the requested height is the
                // list height); the paint that move queued is lost with it and the combobox stayed blank until something else repainted it.
                User32.InvalidateRect(control, null, false);
            }
        }

        public void Hide(int id)
        {
            var control = dialog.Item(id);
            if (control == 0)
            {
                return;
            }

            // Controls are created WS_VISIBLE at 0,0: one that was never placed (a hidden group at Attach) still has to be hidden.
            var wasPlaced = dialog._placed.Remove(control);
            var styleVisible = ((uint)User32.GetWindowLongPtr(control, WindowMessages.GwlStyle) & WindowMessages.WsVisible) != 0;
            if (wasPlaced || styleVisible)
            {
                User32.ShowWindow(control, WindowMessages.SwHide);
            }
        }
    }
}
