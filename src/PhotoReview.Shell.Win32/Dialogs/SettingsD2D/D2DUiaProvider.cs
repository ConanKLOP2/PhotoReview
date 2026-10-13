using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Dialogs.SettingsD2D;

/// <summary>
/// SPIKE UIA: provider UI Automation THẬT cho trang tự vẽ (một HWND, nhiều phần tử không HWND). Cây: cửa sổ (fragment root, ControlType
/// Window) -> nhóm (Group) -> control (CheckBox/Edit/ComboBox/Slider/Text/Button); nút chân là con trực tiếp của cửa sổ. Nguồn dữ liệu là
/// <see cref="D2DSettingsView.AccessibleNodes"/> (chụp mới mỗi lần cần, không giữ trạng thái riêng). Pattern: Toggle (CheckBox), Value
/// (Edit; ComboBox chỉ đọc), ExpandCollapse (ComboBox), RangeValue (Slider), Invoke (Button).
/// <para>
/// Luồng: UIA gọi provider từ thread RPC (MTA), còn view chỉ an toàn trên thread UI. Mọi lời gọi vào view được chuyển sang thread UI
/// bằng <c>SendMessage(WM_GETOBJECT, work, magic)</c> tới cửa sổ (<see cref="OnUi{T}"/>) - thread UI phải bơm thông điệp (như app thật).
/// Cài vào cửa sổ qua <see cref="D2DSettingsHost.GetObjectHandler"/> (seam WM_GETOBJECT sẵn có), không sửa WndProc.
/// </para>
/// </summary>
internal sealed class D2DUiaProvider : IDisposable
{
    private const int WmGetObject = 0x003D;
    private const int UiaRootObjectId = -25;
    private const int MarshalMagic = 0x44325541; // "D2UA": lParam riêng của lời gọi chuyển thread, không trùng OBJID_* hợp lệ
    private const int FlushMagic = 0x44325546;   // "D2UF": xả hàng đợi sự kiện (PostMessage)
    private const int PropNameId = 30005;
    private const int PropControlType = 30003;
    private const int PropAutomationId = 30011;
    private const int PropIsEnabled = 30010;
    private const int PropHasKeyboardFocus = 30008;
    private const int PropIsKeyboardFocusable = 30009;
    private const int PropIsOffscreen = 30022;
    private const int PropIsControlElement = 30016;
    private const int PropIsContentElement = 30017;
    private const int PropFrameworkId = 30024;
    private const int PropValueValue = 30045;
    private const int PropValueIsReadOnly = 30046;
    private const int PropRangeValue = 30047;
    private const int PropRangeIsReadOnly = 30048;
    private const int PropRangeMinimum = 30049;
    private const int PropRangeMaximum = 30050;
    private const int PropExpandCollapseState = 30070;
    private const int PropToggleState = 30086;

    private const int PatternInvoke = 10000;
    private const int PatternValue = 10002;
    private const int PatternRangeValue = 10003;
    private const int PatternExpandCollapse = 10005;
    private const int PatternToggle = 10015;

    private const int EventFocusChanged = 20005;

    private const int HrElementNotEnabled = unchecked((int)0x80040200);
    private const int HrElementNotAvailable = unchecked((int)0x80040201);
    private const int HrNotSupported = unchecked((int)0x80040204);
    private const int HrInvalidArg = unchecked((int)0x80070057);

    /// <summary><c>FrameworkId</c> của mọi phần tử do provider này phát.</summary>
    public const string FrameworkId = "PhotoReviewD2D";

    private static readonly StrategyBasedComWrappers Wrappers = new();
    private static readonly Guid SimpleIid = new("d6dd68d1-86fd-4332-8666-9abedea2d24c");
    private static readonly Guid FragmentIid = new("f7063da8-8359-439c-9297-bbc5299a7d87");
    private static readonly Guid FragmentRootIid = new("620ce2a5-ab8f-40a9-86cb-de3c75599b58");

    private readonly D2DSettingsHost _window;
    private readonly int _uiThreadId;
    private readonly Dictionary<string, D2DUiaNode> _nodes = [];
    private readonly Dictionary<string, bool> _lastEnabled = [];
    private readonly D2DUiaRoot _root;
    private bool _disposed;

    private D2DUiaProvider(D2DSettingsHost window)
    {
        _window = window;
        _uiThreadId = Environment.CurrentManagedThreadId;
        _root = new D2DUiaRoot(this);
    }

    /// <summary>Cài provider vào cửa sổ (gọi sau khi tạo cửa sổ, trên thread UI). Trả đối tượng để gỡ bằng <see cref="Dispose"/>.</summary>
    public static D2DUiaProvider Install(D2DSettingsHost window)
    {
        var provider = new D2DUiaProvider(window);
        window.GetObjectHandler = provider.OnWmGetObject;
        window.View.Changed += provider.OnViewChanged;
        window.View.FocusMoved += provider.OnViewFocusMoved;
        provider.RememberEnabled();
        return provider;
    }

    /// <summary>Gỡ khỏi cửa sổ và ngắt các phần tử khỏi UIA (gọi trên thread UI, trước khi huỷ cửa sổ).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        FlushEvents(); // giải phóng BSTR còn trong hàng đợi (không phát)
        _window.GetObjectHandler = null;
        _window.View.Changed -= OnViewChanged;
        _window.View.FocusMoved -= OnViewFocusMoved;
        foreach (var node in _nodes.Values.Cast<D2DUiaElement>().Append(_root))
        {
            var ptr = ComPointer(node, SimpleIid);
            if (ptr != 0)
            {
                _ = D2DUiaNative.UiaDisconnectProvider(ptr);
                _ = Marshal.Release(ptr);
            }
        }
    }

    // ---- WM_GETOBJECT ---------------------------------------------------------------------------------------------------

    private nint? OnWmGetObject(nint wParam, nint lParam, nint hwnd)
    {
        var id = (int)(lParam & 0xFFFFFFFF);
        if (id == MarshalMagic)
        {
            ((UiaWork)GCHandle.FromIntPtr(wParam).Target!).Run();
            return 1;
        }

        if (id == FlushMagic)
        {
            FlushEvents();
            return 1;
        }

        if (id == UiaRootObjectId && !_disposed)
        {
            var simple = ComPointer(_root, SimpleIid);
            if (simple == 0)
            {
                return null;
            }

            try
            {
                return D2DUiaNative.UiaReturnRawElementProvider(hwnd, wParam, lParam, simple);
            }
            finally
            {
                _ = Marshal.Release(simple);
            }
        }

        return null;
    }

    private sealed class UiaWork(Func<object?> body)
    {
        public object? Value { get; private set; }

        public Exception? Error { get; private set; }

        public bool Done { get; private set; }

        public void Run()
        {
            try
            {
                Value = body();
            }
            catch (Exception ex)
            {
                Error = ex;
            }

            Done = true;
        }
    }

    /// <summary>Chạy <paramref name="body"/> trên thread UI (trực tiếp nếu đã ở đó, nếu không SendMessage tới cửa sổ).</summary>
    internal T OnUi<T>(Func<T> body)
    {
        if (_disposed || _window.IsDestroyed)
        {
            throw new UiaProviderException("The UI Automation element is no longer available.", HrElementNotAvailable);
        }

        if (Environment.CurrentManagedThreadId == _uiThreadId)
        {
            return body();
        }

        var work = new UiaWork(() => body());
        var handle = GCHandle.Alloc(work);
        try
        {
            _ = User32.SendMessage(_window.Hwnd, WmGetObject, GCHandle.ToIntPtr(handle), MarshalMagic);
        }
        finally
        {
            handle.Free();
        }

        if (!work.Done)
        {
            throw new UiaProviderException("The UI Automation element is no longer available.", HrElementNotAvailable);
        }

        if (work.Error is { } error)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }

        return (T)work.Value!;
    }

    internal void OnUi(Action body) => OnUi<object?>(() =>
    {
        body();
        return null;
    });

    // ---- COM con trỏ ----------------------------------------------------------------------------------------------------

    /// <summary>Con trỏ giao diện <paramref name="iid"/> của đối tượng (ref +1 cho người gọi); 0 khi không có.</summary>
    internal static nint ComPointer(object instance, Guid iid)
    {
        var unknown = Wrappers.GetOrCreateComInterfaceForObject(instance, CreateComInterfaceFlags.None);
        try
        {
            return Marshal.QueryInterface(unknown, in iid, out var result) == 0 ? result : 0;
        }
        finally
        {
            _ = Marshal.Release(unknown);
        }
    }

    internal static nint UnknownPointer(object instance) => Wrappers.GetOrCreateComInterfaceForObject(instance, CreateComInterfaceFlags.None);

    private static nint FragmentPointer(D2DUiaElement? element) => element is null ? 0 : ComPointer(element, FragmentIid);

    // ---- cây ------------------------------------------------------------------------------------------------------------

    private sealed class Tree
    {
        public List<D2DAccessibleNode> Nodes { get; } = [];

        public Dictionary<string, D2DAccessibleNode> ById { get; } = [];

        /// <summary>Khoá "" = gốc (cửa sổ).</summary>
        public Dictionary<string, List<string>> Children { get; } = new() { [string.Empty] = [] };

        public Dictionary<string, string> Parent { get; } = [];
    }

    private Tree BuildTree()
    {
        var tree = new Tree();
        var group = string.Empty;
        foreach (var node in _window.View.AccessibleNodes())
        {
            tree.Nodes.Add(node);
            tree.ById[node.AutomationId] = node;
            string parent;
            if (node.Level <= 1)
            {
                parent = string.Empty;
                group = node.Role == D2DRole.Group ? node.AutomationId : string.Empty;
            }
            else
            {
                parent = group;
            }

            tree.Parent[node.AutomationId] = parent;
            tree.Children[parent].Add(node.AutomationId);
            tree.Children[node.AutomationId] = [];
        }

        return tree;
    }

    private D2DUiaElement Element(string id) => id.Length == 0 ? _root : NodeFor(id);

    private D2DUiaNode NodeFor(string id)
    {
        if (!_nodes.TryGetValue(id, out var node))
        {
            node = new D2DUiaNode(this, id, _nodes.Count + 1);
            _nodes[id] = node;
        }

        return node;
    }

    private static D2DAccessibleNode Data(Tree tree, string id)
        => tree.ById.TryGetValue(id, out var data) ? data : throw new UiaProviderException("Element not available.", HrElementNotAvailable);

    // ---- thuộc tính -----------------------------------------------------------------------------------------------------

    internal static int ControlTypeOf(D2DRole role) => role switch
    {
        D2DRole.Window => 50032,
        D2DRole.Group => 50026,
        D2DRole.CheckBox => 50002,
        D2DRole.Text => 50020,
        D2DRole.Edit => 50004,
        D2DRole.ComboBox => 50003,
        D2DRole.Slider => 50015,
        D2DRole.Button => 50000,
        D2DRole.ScrollBar => 50014,
        _ => 50033,
    };

    private static bool IsEmpty(PxRect r) => r.Width <= 0 || r.Height <= 0;

    internal UiaVariant GetProperty(D2DUiaElement element, int propertyId) => OnUi(() => PropertyOnUi(element, propertyId));

    private UiaVariant PropertyOnUi(D2DUiaElement element, int propertyId)
    {
        if (element.IsRoot)
        {
            return propertyId switch
            {
                PropNameId => UiaVariant.FromString(_window.View.Title()),
                PropControlType => UiaVariant.FromInt(ControlTypeOf(D2DRole.Window)),
                PropIsEnabled => UiaVariant.FromBool(true),
                PropIsControlElement or PropIsContentElement => UiaVariant.FromBool(true),
                PropFrameworkId => UiaVariant.FromString(FrameworkId),
                _ => default,
            };
        }

        var data = Data(BuildTree(), element.Id);
        switch (propertyId)
        {
            case PropNameId:
                return UiaVariant.FromString(data.Name);
            case PropAutomationId:
                return UiaVariant.FromString(data.AutomationId);
            case PropControlType:
                return UiaVariant.FromInt(ControlTypeOf(data.Role));
            case PropIsEnabled:
                return UiaVariant.FromBool(data.IsEnabled);
            case PropHasKeyboardFocus:
                return UiaVariant.FromBool(data.HasFocus);
            case PropIsKeyboardFocusable:
                return UiaVariant.FromBool(data.IsFocusable);
            case PropIsOffscreen:
                return UiaVariant.FromBool(IsEmpty(data.ClientBounds));
            case PropIsControlElement or PropIsContentElement:
                return UiaVariant.FromBool(true);
            case PropFrameworkId:
                return UiaVariant.FromString(FrameworkId);
            case PropToggleState when data.IsChecked is { } on:
                return UiaVariant.FromInt(on ? 1 : 0);
            case PropValueValue when data.Role is D2DRole.Edit or D2DRole.ComboBox && data.Value is { } text:
                return UiaVariant.FromString(text);
            case PropValueIsReadOnly when data.Role == D2DRole.ComboBox:
                return UiaVariant.FromBool(true);
            case PropValueIsReadOnly when data.Role == D2DRole.Edit:
                return UiaVariant.FromBool(false);
            case PropExpandCollapseState when data.IsExpanded is { } open:
                return UiaVariant.FromInt(open ? 1 : 0);
            case PropRangeValue or PropRangeMinimum or PropRangeMaximum or PropRangeIsReadOnly when Slider(element.Id) is { } slider:
                return propertyId switch
                {
                    PropRangeValue => UiaVariant.FromDouble(slider.Value),
                    PropRangeMinimum => UiaVariant.FromDouble(slider.Min),
                    PropRangeMaximum => UiaVariant.FromDouble(slider.Max),
                    _ => UiaVariant.FromBool(false),
                };
            default:
                return default;
        }
    }

    internal nint GetPattern(D2DUiaElement element, int patternId) => OnUi(() =>
    {
        if (element.IsRoot)
        {
            return 0;
        }

        var role = Data(BuildTree(), element.Id).Role;
        object? pattern = (patternId, role) switch
        {
            (PatternToggle, D2DRole.CheckBox) => ((D2DUiaNode)element).Toggle ??= new D2DUiaTogglePattern(this, element.Id),
            (PatternValue, D2DRole.Edit or D2DRole.ComboBox) => ((D2DUiaNode)element).Value ??= new D2DUiaValuePattern(this, element.Id),
            (PatternRangeValue, D2DRole.Slider) => ((D2DUiaNode)element).Range ??= new D2DUiaRangePattern(this, element.Id),
            (PatternExpandCollapse, D2DRole.ComboBox) => ((D2DUiaNode)element).Expand ??= new D2DUiaExpandPattern(this, element.Id),
            (PatternInvoke, D2DRole.Button) => ((D2DUiaNode)element).Invoke ??= new D2DUiaInvokePattern(this, element.Id),
            _ => null,
        };
        return pattern is null ? 0 : UnknownPointer(pattern);
    });

    // ---- hình học + điều hướng + focus ---------------------------------------------------------------------------------

    internal UiaRect BoundingRectangle(D2DUiaElement element) => OnUi(() =>
    {
        PxRect client;
        if (element.IsRoot)
        {
            client = new PxRect(0, 0, _window.View.ClientWidth, _window.View.ClientHeight);
        }
        else
        {
            client = Data(BuildTree(), element.Id).ClientBounds;
            if (IsEmpty(client))
            {
                return default;
            }
        }

        var screen = _window.ClientToScreenRect(client);
        return new UiaRect { Left = screen.X, Top = screen.Y, Width = screen.Width, Height = screen.Height };
    });

    internal nint Navigate(D2DUiaElement element, int direction) => OnUi(() =>
    {
        var tree = BuildTree();
        D2DUiaElement? target = null;
        if (element.IsRoot)
        {
            var kids = tree.Children[string.Empty];
            target = direction switch
            {
                3 when kids.Count > 0 => Element(kids[0]),
                4 when kids.Count > 0 => Element(kids[^1]),
                _ => null,
            };
        }
        else
        {
            var parent = tree.Parent.TryGetValue(element.Id, out var p) ? p : throw new UiaProviderException("Element not available.", HrElementNotAvailable);
            var siblings = tree.Children[parent];
            var index = siblings.IndexOf(element.Id);
            var kids = tree.Children[element.Id];
            target = direction switch
            {
                0 => Element(parent),
                1 when index + 1 < siblings.Count => Element(siblings[index + 1]),
                2 when index > 0 => Element(siblings[index - 1]),
                3 when kids.Count > 0 => Element(kids[0]),
                4 when kids.Count > 0 => Element(kids[^1]),
                _ => null,
            };
        }

        return FragmentPointer(target);
    });

    internal void SetFocus(D2DUiaElement element) => OnUi(() =>
    {
        if (element.IsRoot)
        {
            _ = User32.SetFocus(_window.Hwnd);
            return;
        }

        var widget = Widget(element.Id);
        if (!widget.Enabled)
        {
            throw new UiaProviderException("Element is not enabled.", HrElementNotEnabled);
        }

        if (widget.Focusable)
        {
            _window.View.SetFocus(widget);
        }
    });

    internal nint ElementFromPoint(double x, double y) => OnUi(() =>
    {
        var origin = _window.ClientToScreenRect(new PxRect(0, 0, 0, 0));
        var cx = (int)Math.Floor(x) - origin.X;
        var cy = (int)Math.Floor(y) - origin.Y;
        var tree = BuildTree();
        for (var i = tree.Nodes.Count - 1; i >= 0; i--)
        {
            var node = tree.Nodes[i];
            if (!IsEmpty(node.ClientBounds) && node.ClientBounds.Contains(cx, cy))
            {
                return FragmentPointer(NodeFor(node.AutomationId));
            }
        }

        return FragmentPointer(_root);
    });

    internal nint GetFocusedElement() => OnUi(() =>
        _window.View.Focused is { } focused ? FragmentPointer(NodeFor(focused.Id)) : 0);

    internal nint RuntimeId(D2DUiaElement element)
    {
        if (element.IsRoot)
        {
            return 0; // gốc đặt trên HWND: UIA tự sinh runtime id từ HWND
        }

        const int UiaAppendRuntimeId = 3;
        var array = D2DUiaNative.SafeArrayCreateVector(3 /* VT_I4 */, 0, 2);
        unsafe
        {
            var values = stackalloc int[2] { UiaAppendRuntimeId, element.Sequence };
            for (var i = 0; i < 2; i++)
            {
                _ = D2DUiaNative.SafeArrayPutElement(array, &i, &values[i]);
            }
        }

        return array;
    }

    internal nint HostProvider()
        => D2DUiaNative.UiaHostProviderFromHwnd(_window.Hwnd, out var host) == 0 ? host : 0;

    internal nint RootFragmentRootPointer() => ComPointer(_root, FragmentRootIid);

    // ---- hành động pattern (thread UI) ----------------------------------------------------------------------------------

    private D2DWidget Widget(string id)
        => _window.View.Find(id) ?? throw new UiaProviderException("Element not available.", HrElementNotAvailable);

    private D2DSliderWidget? Slider(string id) => _window.View.Find(id) as D2DSliderWidget;

    internal void DoToggle(string id) => OnUi(() =>
    {
        if (Widget(id) is not D2DCheckWidget check || !_window.View.UiaToggle(check))
        {
            throw new UiaProviderException("Element is not enabled.", HrElementNotEnabled);
        }
    });

    internal int ToggleState(string id) => OnUi(() => Widget(id) is D2DCheckWidget { Checked: true } ? 1 : 0);

    internal void DoInvoke(string id) => OnUi(() =>
    {
        if (Widget(id) is not D2DButtonWidget button || !_window.View.UiaInvoke(button))
        {
            throw new UiaProviderException("Element is not enabled.", HrElementNotEnabled);
        }
    });

    internal void DoSetText(string id, string text) => OnUi(() =>
    {
        switch (Widget(id))
        {
            case D2DFieldWidget field:
                if (!_window.View.UiaSetText(field, text, out var rejected))
                {
                    throw rejected
                        ? new UiaProviderException("Value rejected.", HrInvalidArg)
                        : new UiaProviderException("Element is not enabled.", HrElementNotEnabled);
                }

                if (_window.HostedCount > 0)
                {
                    _window.PushValuesToHosted();
                }

                break;
            default:
                throw new UiaProviderException("Read-only value.", HrNotSupported);
        }
    });

    internal string ReadText(string id) => OnUi(() => Widget(id) switch
    {
        D2DFieldWidget field => field.Box.Text,
        D2DComboWidget combo => combo.SelectedText,
        _ => throw new UiaProviderException("Value not supported.", HrNotSupported),
    });

    internal bool IsReadOnly(string id) => OnUi(() => Widget(id) is not D2DFieldWidget);

    internal double RangeValue(string id) => OnUi(() => Slider(id)?.Value ?? throw new UiaProviderException("Not a slider.", HrNotSupported));

    internal double RangeMin(string id) => OnUi(() => Slider(id)?.Min ?? throw new UiaProviderException("Not a slider.", HrNotSupported));

    internal double RangeMax(string id) => OnUi(() => Slider(id)?.Max ?? throw new UiaProviderException("Not a slider.", HrNotSupported));

    internal void DoSetRange(string id, double value) => OnUi(() =>
    {
        var slider = Slider(id) ?? throw new UiaProviderException("Not a slider.", HrNotSupported);
        if (double.IsNaN(value) || value < slider.Min || value > slider.Max)
        {
            throw new UiaProviderException("Value out of range.", HrInvalidArg);
        }

        if (!_window.View.UiaSetSlider(slider, (int)Math.Round(value)))
        {
            throw new UiaProviderException("Element is not enabled.", HrElementNotEnabled);
        }
    });

    internal void DoExpand(string id, bool expand) => OnUi(() =>
    {
        if (Widget(id) is not D2DComboWidget combo || !_window.View.UiaSetExpanded(combo, expand))
        {
            throw new UiaProviderException("Element is not enabled.", HrElementNotEnabled);
        }
    });

    internal int ExpandState(string id) => OnUi(() => Data(BuildTree(), id).IsExpanded == true ? 1 : 0);

    // ---- sự kiện (thread UI) --------------------------------------------------------------------------------------------
    // Sự kiện KHÔNG phát ngay trong lời gọi provider: UIA phát sự kiện đồng bộ tới client, mà client cùng tiến trình (và mọi client
    // đang chờ chính lời gọi này) bị chặn tới khi lời gọi trả về - kết quả đo được là ~2 s chờ hết hạn cho MỖI lần Toggle/SetValue.
    // Nên xếp hàng và PostMessage một thông điệp xả (cùng seam WM_GETOBJECT, lParam = FlushMagic): xả sau khi lời gọi đã trả.

    private readonly List<PendingEvent> _pending = [];
    private bool _flushPosted;

    private readonly record struct PendingEvent(string Id, int PropertyId, UiaVariant Value, bool Focus);

    private void RememberEnabled()
    {
        _lastEnabled.Clear();
        foreach (var node in _window.View.AccessibleNodes())
        {
            _lastEnabled[node.AutomationId] = node.IsEnabled;
        }
    }

    private void Enqueue(PendingEvent item)
    {
        _pending.Add(item);
        if (!_flushPosted)
        {
            _flushPosted = true;
            _ = User32.PostMessage(_window.Hwnd, WmGetObject, 0, FlushMagic);
        }
    }

    private void OnViewFocusMoved(D2DWidget? widget)
    {
        if (widget is not null && !_disposed && D2DUiaNative.UiaClientsAreListening())
        {
            Enqueue(new PendingEvent(widget.Id, 0, default, true));
        }
    }

    private void OnViewChanged(D2DWidget widget)
    {
        if (_disposed || !D2DUiaNative.UiaClientsAreListening())
        {
            RememberEnabled();
            return;
        }

        // Giá trị mới của phần tử vừa đổi (BSTR cấp ở đây, giải phóng sau khi phát).
        switch (widget)
        {
            case D2DCheckWidget check:
                Enqueue(new PendingEvent(widget.Id, PropToggleState, UiaVariant.FromInt(check.Checked ? 1 : 0), false));
                break;
            case D2DFieldWidget field:
                Enqueue(new PendingEvent(widget.Id, PropValueValue, UiaVariant.FromString(field.Box.Text), false));
                break;
            case D2DComboWidget combo:
                Enqueue(new PendingEvent(widget.Id, PropValueValue, UiaVariant.FromString(combo.SelectedText), false));
                break;
            case D2DSliderWidget slider:
                Enqueue(new PendingEvent(widget.Id, PropRangeValue, UiaVariant.FromDouble(slider.Value), false));
                break;
        }

        // Phần tử con bị bật/tắt theo tick cha.
        foreach (var node in _window.View.AccessibleNodes())
        {
            if (_lastEnabled.TryGetValue(node.AutomationId, out var before) && before != node.IsEnabled)
            {
                Enqueue(new PendingEvent(node.AutomationId, PropIsEnabled, UiaVariant.FromBool(node.IsEnabled), false));
            }
        }

        RememberEnabled();
    }

    private unsafe void FlushEvents()
    {
        _flushPosted = false;
        var batch = _pending.ToArray();
        _pending.Clear();
        foreach (var item in batch)
        {
            var value = item.Value;
            var ptr = _disposed ? 0 : ComPointer(NodeFor(item.Id), SimpleIid);
            try
            {
                if (ptr != 0)
                {
                    _ = item.Focus
                        ? D2DUiaNative.UiaRaiseAutomationEvent(ptr, EventFocusChanged)
                        : D2DUiaNative.UiaRaiseAutomationPropertyChangedEvent(ptr, item.PropertyId, default, value);
                }
            }
            finally
            {
                if (ptr != 0)
                {
                    _ = Marshal.Release(ptr);
                }

                if (value.Vt == UiaVariant.VtBstr)
                {
                    _ = D2DUiaNative.VariantClear(&value);
                }
            }
        }
    }
}
/// <summary>Phần tử UIA (fragment) - gốc chung của cửa sổ và các nút.</summary>
[GeneratedComClass]
internal abstract partial class D2DUiaElement(D2DUiaProvider owner, string id, int sequence) : IRawElementProviderSimple, IRawElementProviderFragment
{
    private const int ServerSide = 0x2;

    protected D2DUiaProvider Owner { get; } = owner;

    /// <summary>AutomationId ("" = cửa sổ).</summary>
    public string Id { get; } = id;

    public int Sequence { get; } = sequence;

    public bool IsRoot => Id.Length == 0;

    public int GetProviderOptions() => ServerSide;

    public nint GetPatternProvider(int patternId) => Owner.GetPattern(this, patternId);

    public UiaVariant GetPropertyValue(int propertyId) => Owner.GetProperty(this, propertyId);

    public nint GetHostRawElementProvider() => IsRoot ? Owner.HostProvider() : 0;

    public nint Navigate(int direction) => Owner.Navigate(this, direction);

    public nint GetRuntimeId() => Owner.RuntimeId(this);

    public UiaRect GetBoundingRectangle() => Owner.BoundingRectangle(this);

    public nint GetEmbeddedFragmentRoots() => 0;

    public void SetFocus() => Owner.SetFocus(this);

    public nint GetFragmentRoot() => Owner.RootFragmentRootPointer();
}

/// <summary>Cửa sổ (fragment root).</summary>
[GeneratedComClass]
internal sealed partial class D2DUiaRoot(D2DUiaProvider owner) : D2DUiaElement(owner, string.Empty, 0), IRawElementProviderFragmentRoot
{
    public nint ElementProviderFromPoint(double x, double y) => Owner.ElementFromPoint(x, y);

    public nint GetFocus() => Owner.GetFocusedElement();
}

/// <summary>Nhóm/control/nút (theo AutomationId).</summary>
[GeneratedComClass]
internal sealed partial class D2DUiaNode(D2DUiaProvider owner, string id, int sequence) : D2DUiaElement(owner, id, sequence)
{
    public D2DUiaTogglePattern? Toggle { get; set; }

    public D2DUiaValuePattern? Value { get; set; }

    public D2DUiaRangePattern? Range { get; set; }

    public D2DUiaExpandPattern? Expand { get; set; }

    public D2DUiaInvokePattern? Invoke { get; set; }
}

[GeneratedComClass]
internal sealed partial class D2DUiaTogglePattern(D2DUiaProvider owner, string id) : IToggleProvider
{
    public void Toggle() => owner.DoToggle(id);

    public int GetToggleState() => owner.ToggleState(id);
}

[GeneratedComClass]
internal sealed partial class D2DUiaValuePattern(D2DUiaProvider owner, string id) : IValueProvider
{
    public void SetValue(string value) => owner.DoSetText(id, value);

    public nint GetValue() => D2DUiaNative.SysAllocString(owner.ReadText(id));

    public int GetIsReadOnly() => owner.IsReadOnly(id) ? 1 : 0;
}

[GeneratedComClass]
internal sealed partial class D2DUiaRangePattern(D2DUiaProvider owner, string id) : IRangeValueProvider
{
    public void SetValue(double value) => owner.DoSetRange(id, value);

    public double GetValue() => owner.RangeValue(id);

    public int GetIsReadOnly() => 0;

    public double GetMaximum() => owner.RangeMax(id);

    public double GetMinimum() => owner.RangeMin(id);

    public double GetLargeChange() => Math.Max(1, (owner.RangeMax(id) - owner.RangeMin(id)) / 10);

    public double GetSmallChange() => 1;
}

[GeneratedComClass]
internal sealed partial class D2DUiaExpandPattern(D2DUiaProvider owner, string id) : IExpandCollapseProvider
{
    public void Expand() => owner.DoExpand(id, true);

    public void Collapse() => owner.DoExpand(id, false);

    public int GetExpandCollapseState() => owner.ExpandState(id);
}

[GeneratedComClass]
internal sealed partial class D2DUiaInvokePattern(D2DUiaProvider owner, string id) : IInvokeProvider
{
    public void Invoke() => owner.DoInvoke(id);
}

/// <summary>Lỗi trả về UIA bằng đúng HRESULT (<c>UIA_E_ELEMENTNOTENABLED</c>, <c>UIA_E_ELEMENTNOTAVAILABLE</c>, ...); bộ sinh COM đổi ngoại lệ thành HRESULT.</summary>
internal sealed class UiaProviderException : Exception
{
    public UiaProviderException(string message, int hresult)
        : base(message)
    {
        HResult = hresult;
    }
}