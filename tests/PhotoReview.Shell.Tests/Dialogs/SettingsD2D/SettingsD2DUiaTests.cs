using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs.SettingsD2D;
using PhotoReview.TestSupport;
using Xunit.Abstractions;
using static PhotoReview.Shell.Tests.Dialogs.SettingsD2D.D2DTestKit;

namespace PhotoReview.Shell.Tests.Dialogs.SettingsD2D;

/// <summary>
/// SPIKE D2D Settings - UIA: provider UI Automation THẬT của <see cref="D2DUiaProvider"/> được kiểm bằng một UIA CLIENT thật
/// (CUIAutomation, <c>[GeneratedComInterface]</c> trong file này; không System.Windows.Automation). Client chạy trên thread MTA, thread
/// STA của cửa sổ bơm thông điệp trong lúc chờ (provider chuyển mọi lời gọi sang thread UI bằng SendMessage), có hạn chót 30 s.
/// Số nhận dạng thuộc tính/pattern/ControlType viết lại ở đây bằng tay (đối chiếu UIAutomationClient.h), KHÔNG dùng hằng của provider.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "UI")]
[Trait("Category", "Slow")] // tiến trình client ngoài + UIA trễ ~2 s trên desktop ẩn: không vào bộ lọc mặc định
public sealed class SettingsD2DUiaTests(ITestOutputHelper output)
{
    private const int HrElementNotEnabled = unchecked((int)0x80040200);

    /// <summary>Client UIA ngoài tiến trình (System.Windows.Automation của Windows PowerShell 5.1): in TREE/NODE/TOGGLE để test đọc.</summary>
    private const string UiaClientScript = """
                param([long]$Hwnd)
                Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
                $sw = [Diagnostics.Stopwatch]::StartNew()
                $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$Hwnd)
                $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
                "TREE|$($sw.ElapsedMilliseconds)|$($all.Count)"
                foreach ($e in $all) {
                    $c = $e.Current
                    if ($c.FrameworkId -eq 'PhotoReviewD2D') { "NODE|$($c.AutomationId)|$($c.ControlType.ProgrammaticName)|$($c.Name)|$($c.IsEnabled)" }
                }
                $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ShowInfoOverlay')
                $box = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
                $pattern = $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
                $before = $pattern.Current.ToggleState
                $sw.Restart()
                $pattern.Toggle()
                "TOGGLE|$($sw.ElapsedMilliseconds)|$before|$($pattern.Current.ToggleState)"
                """;

    private static AppSettings Baseline() => new()
    {
        ShowInfoOverlay = true,
        ToolbarAutoHide = true,
        InfoOverlayAutoHide = false,
        ShowExifInfo = true,
        ToolbarOpacityPercent = 70,
    };

    private static T WithUia<T>(Func<D2DSettingsHost, UiaHost, T> body, AppSettings? settings = null)
        => WithWindow(new D2DSettingsHostOptions(Dark: true), (window, _) =>
        {
            using var provider = D2DUiaProvider.Install(window);
            using var host = new UiaHost(window);
            return body(window, host);
        }, settings ?? Baseline());

    // ---- (a) cây + tên -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Tree_EveryProviderNodeHasName_MatchesViewAndKeepsGroupHierarchy()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        WithUia((window, host) =>
        {
            var expected = window.View.AccessibleNodes();
            var snapshot = host.Call(api =>
            {
                var rootName = UiaApi.Str(api.Root, UiaIds.Name);
                var rootType = UiaApi.Int(api.Root, UiaIds.ControlType);
                var nodes = api.Provider().Select(e => (Id: UiaApi.Str(e, UiaIds.AutomationId), Name: UiaApi.Str(e, UiaIds.Name), Type: UiaApi.Int(e, UiaIds.ControlType))).ToList();
                var groupKids = api.Children(api.ById("GroupToolbar")).Select(e => UiaApi.Str(e, UiaIds.AutomationId)).ToList();
                var parentOfCheck = api.Parent(api.ById("ShowInfoOverlay"));
                var parentOfSave = api.Parent(api.ById("Save"));
                var rootKids = api.Children(api.Root).Select(e => UiaApi.Str(e, UiaIds.AutomationId)).ToList();
                return (rootName, rootType, nodes, groupKids,
                    ParentCheck: parentOfCheck is null ? null : UiaApi.Str(parentOfCheck, UiaIds.AutomationId),
                    ParentSaveIsRoot: parentOfSave is not null && UiaApi.Int(parentOfSave, UiaIds.ControlType) == UiaIds.WindowType,
                    rootKids);
            });

            Assert.Equal(Tr.SettingsNavDisplay, snapshot.rootName);
            Assert.Equal(UiaIds.WindowType, snapshot.rootType);
            Assert.Equal(expected.Count, snapshot.nodes.Count);
            Assert.All(snapshot.nodes, n => Assert.False(string.IsNullOrWhiteSpace(n.Name), $"{n.Id} has no name"));
            foreach (var node in expected)
            {
                var actual = snapshot.nodes.Single(n => n.Id == node.AutomationId);
                Assert.Equal(node.Name, actual.Name);
            }

            Assert.Equal(["ToolbarAutoHide", "ToolbarAutoHideDelay", "ToolbarAutoHideHint", "ToolbarOpacity", "ToolbarOpacityHint"], snapshot.groupKids);
            Assert.Equal("GroupDisplay", snapshot.ParentCheck);
            Assert.True(snapshot.ParentSaveIsRoot, "footer button's parent must be the window");
            Assert.Contains("GroupDisplay", snapshot.rootKids);
            Assert.Equal(["ResetPage", "Cancel", "Apply", "Save"], snapshot.rootKids.Where(s => s is "ResetPage" or "Cancel" or "Apply" or "Save").ToList());
            output.WriteLine($"{snapshot.nodes.Count} provider nodes under the window; types: "
                + string.Join(", ", snapshot.nodes.GroupBy(n => n.Type).Select(g => $"{g.Key}={g.Count()}")));
            return 0;
        });
    }

    // ---- (b) ControlType + giá trị pattern ----------------------------------------------------------------------------------

    [Fact]
    public void ControlTypes_AndPatternValues_MatchTheWidgets()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        WithUia((window, host) =>
        {
            var view = window.View;
            var check = (D2DCheckWidget)view.Find("ShowInfoOverlay")!;
            var delay = (D2DFieldWidget)view.Find("ToolbarAutoHideDelay")!;
            var slider = (D2DSliderWidget)view.Find("ToolbarOpacity")!;
            var combo = (D2DComboWidget)view.Find("LoadingMode")!;
            var result = host.Call(api =>
            {
                var overlay = api.ById("ShowInfoOverlay");
                var edit = api.ById("ToolbarAutoHideDelay");
                var loading = api.ById("LoadingMode");
                var opacity = api.ById("ToolbarOpacity");
                var save = api.ById("Save");
                var toggle = UiaApi.Pattern<IUiaClientToggle>(overlay, UiaIds.TogglePattern);
                var value = UiaApi.Pattern<IUiaClientValue>(edit, UiaIds.ValuePattern);
                var comboValue = UiaApi.Pattern<IUiaClientValue>(loading, UiaIds.ValuePattern);
                var expand = UiaApi.Pattern<IUiaClientExpand>(loading, UiaIds.ExpandCollapsePattern);
                var range = UiaApi.Pattern<IUiaClientRange>(opacity, UiaIds.RangeValuePattern);
                return new
                {
                    OverlayType = UiaApi.Int(overlay, UiaIds.ControlType),
                    EditType = UiaApi.Int(edit, UiaIds.ControlType),
                    ComboType = UiaApi.Int(loading, UiaIds.ControlType),
                    SliderType = UiaApi.Int(opacity, UiaIds.ControlType),
                    ButtonType = UiaApi.Int(save, UiaIds.ControlType),
                    GroupType = UiaApi.Int(api.ById("GroupToolbar"), UiaIds.ControlType),
                    NoteType = UiaApi.Int(api.ById("ToolbarAutoHideHint"), UiaIds.ControlType),
                    ToggleState = toggle!.GetCurrentToggleState(),
                    EditValue = UiaApi.Bstr(value!.GetCurrentValue()),
                    EditReadOnly = value.GetCurrentIsReadOnly(),
                    ComboValue = UiaApi.Bstr(comboValue!.GetCurrentValue()),
                    ComboReadOnly = comboValue.GetCurrentIsReadOnly(),
                    ExpandState = expand!.GetCurrentExpandCollapseState(),
                    RangeValue = range!.GetCurrentValue(),
                    RangeMin = range.GetCurrentMinimum(),
                    RangeMax = range.GetCurrentMaximum(),
                    OverlayHasNoInvoke = UiaApi.Pattern<IUiaClientInvoke>(overlay, UiaIds.InvokePattern) is null,
                    SaveHasInvoke = UiaApi.Pattern<IUiaClientInvoke>(save, UiaIds.InvokePattern) is not null,
                    SaveHasNoToggle = UiaApi.Pattern<IUiaClientToggle>(save, UiaIds.TogglePattern) is null,
                    SliderHasNoValue = UiaApi.Pattern<IUiaClientValue>(opacity, UiaIds.ValuePattern) is null,
                    EditValueProperty = UiaApi.Str(edit, UiaIds.ValueValue),
                    ToggleProperty = UiaApi.Int(overlay, UiaIds.ToggleState),
                };
            });

            Assert.Equal(UiaIds.CheckBoxType, result.OverlayType);
            Assert.Equal(UiaIds.EditType, result.EditType);
            Assert.Equal(UiaIds.ComboBoxType, result.ComboType);
            Assert.Equal(UiaIds.SliderType, result.SliderType);
            Assert.Equal(UiaIds.ButtonType, result.ButtonType);
            Assert.Equal(UiaIds.GroupType, result.GroupType);
            Assert.Equal(UiaIds.TextType, result.NoteType);
            Assert.Equal(check.Checked ? 1 : 0, result.ToggleState);
            Assert.Equal(check.Checked ? 1 : 0, result.ToggleProperty);
            Assert.Equal(delay.Box.Text, result.EditValue);
            Assert.Equal(delay.Box.Text, result.EditValueProperty);
            Assert.Equal(0, result.EditReadOnly);
            Assert.Equal(combo.SelectedText, result.ComboValue);
            Assert.Equal(1, result.ComboReadOnly);
            Assert.Equal(0, result.ExpandState);
            Assert.Equal(slider.Value, result.RangeValue);
            Assert.Equal(slider.Min, result.RangeMin);
            Assert.Equal(slider.Max, result.RangeMax);
            Assert.True(result.OverlayHasNoInvoke);
            Assert.True(result.SaveHasInvoke);
            Assert.True(result.SaveHasNoToggle);
            Assert.True(result.SliderHasNoValue);
            return 0;
        });
    }

    // ---- (c) pattern điều khiển view ---------------------------------------------------------------------------------------

    // Mỗi test chỉ làm 1-3 hành động UIA: trên desktop ẩn của runner, UIA chờ ~2 s trước MỖI hành động (cửa sổ không bao giờ là
    // foreground; trên desktop thật Toggle 21 ms, SetFocus 44 ms), và OnSta chỉ cho 60 s cho cả test.

    [Fact]
    public void Toggle_FlipsTheCheckBox_AndRaisesChangedOnce()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        WithUia((window, host) =>
        {
            var changes = new List<string>();
            window.View.Changed += w => changes.Add(w.Id);
            var check = (D2DCheckWidget)window.View.Find("ShowInfoOverlay")!;
            Assert.True(check.Checked);
            host.Call(api =>
            {
                UiaApi.Pattern<IUiaClientToggle>(api.ById("ShowInfoOverlay"), UiaIds.TogglePattern)!.Toggle();
                return 0;
            });
            Assert.False(check.Checked);
            Assert.Equal(["ShowInfoOverlay"], changes);
            var state = host.Call(api => UiaApi.Pattern<IUiaClientToggle>(api.ById("ShowInfoOverlay"), UiaIds.TogglePattern)!.GetCurrentToggleState());
            Assert.Equal(0, state);
            return 0;
        });
    }

    [Fact]
    public void SetValue_EditsTheNumberBox_AndLettersAreRejectedWithInvalidArg()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        WithUia((window, host) =>
        {
            var changes = new List<string>();
            window.View.Changed += w => changes.Add(w.Id);
            var delay = (D2DFieldWidget)window.View.Find("ToolbarAutoHideDelay")!;
            host.Call(api =>
            {
                UiaApi.Pattern<IUiaClientValue>(api.ById("ToolbarAutoHideDelay"), UiaIds.ValuePattern)!.SetValue("750");
                return 0;
            });
            Assert.Equal("750", delay.Box.Text);
            Assert.Equal(["ToolbarAutoHideDelay"], changes);

            // Ô số từ chối chữ: HRESULT lỗi trả về client, nội dung giữ nguyên, không phát Changed.
            changes.Clear();
            var rejected = host.Call(api =>
            {
                try
                {
                    UiaApi.Pattern<IUiaClientValue>(api.ById("ToolbarAutoHideDelay"), UiaIds.ValuePattern)!.SetValue("abc");
                    return 0;
                }
                catch (Exception ex) when (ex is COMException or ArgumentException)
                {
                    return ex.HResult; // E_INVALIDARG nên Marshal đổi thành ArgumentException
                }
            });
            Assert.Equal(unchecked((int)0x80070057), rejected);
            Assert.Equal("750", delay.Box.Text);
            Assert.Empty(changes);
            return 0;
        });
    }

    [Fact]
    public void RangeValue_And_ExpandCollapse_DriveTheSliderAndTheComboPopup()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        WithUia((window, host) =>
        {
            var view = window.View;
            var changes = new List<string>();
            view.Changed += w => changes.Add(w.Id);
            var slider = (D2DSliderWidget)view.Find("ToolbarOpacity")!;
            host.Call(api =>
            {
                UiaApi.Pattern<IUiaClientRange>(api.ById("ToolbarOpacity"), UiaIds.RangeValuePattern)!.SetValue(55);
                return 0;
            });
            Assert.Equal(55, slider.Value);
            Assert.Equal(["ToolbarOpacity"], changes);

            host.Call(api =>
            {
                UiaApi.Pattern<IUiaClientExpand>(api.ById("LoadingMode"), UiaIds.ExpandCollapsePattern)!.Expand();
                return 0;
            });
            Assert.True(view.PopupOpen);
            var expanded = host.Call(api => UiaApi.Pattern<IUiaClientExpand>(api.ById("LoadingMode"), UiaIds.ExpandCollapsePattern)!.GetCurrentExpandCollapseState());
            Assert.Equal(1, expanded);
            host.Call(api =>
            {
                UiaApi.Pattern<IUiaClientExpand>(api.ById("LoadingMode"), UiaIds.ExpandCollapsePattern)!.Collapse();
                return 0;
            });
            Assert.False(view.PopupOpen);
            return 0;
        });
    }

    [Fact]
    public void Invoke_OnAFooterButton_RaisesButtonInvoked()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        WithUia((window, host) =>
        {
            var invoked = new List<string>();
            window.View.ButtonInvoked += b => invoked.Add(b.Id);
            host.Call(api =>
            {
                UiaApi.Pattern<IUiaClientInvoke>(api.ById("Cancel"), UiaIds.InvokePattern)!.Invoke();
                return 0;
            });
            Assert.Equal(["Cancel"], invoked);
            return 0;
        });
    }
    // ---- client NGOÀI tiến trình (giống Narrator) ------------------------------------------------------------------------

    [Fact]
    public void OutOfProcessClient_SeesTheSameTree_AndTogglesTheCheckBox()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var dir = Path.Combine(Path.GetTempPath(), "d2d-uia-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var script = Path.Combine(dir, "client.ps1");
            File.WriteAllText(script, UiaClientScript);
            WithUia((window, host) =>
            {
                var check = (D2DCheckWidget)window.View.Find("ShowInfoOverlay")!;
                var before = check.Checked;
                var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Hwnd {window.Hwnd}")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(psi)!;
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                using var exited = new ManualResetEventSlim(false);
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => exited.Set();
                if (process.HasExited)
                {
                    exited.Set();
                }

                try
                {
                    UiaClientNative.PumpUntil(exited.WaitHandle, TimeSpan.FromSeconds(45));
                    if (!exited.IsSet)
                    {
                        throw new TimeoutException("out-of-process UIA client did not finish within 45 s (UI thread was pumping).");
                    }
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }

                var lines = stdout.GetAwaiter().GetResult().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
                output.WriteLine(string.Join(Environment.NewLine, lines.Where(l => !l.StartsWith("NODE", StringComparison.Ordinal))) + Environment.NewLine + stderr.GetAwaiter().GetResult());
                output.WriteLine($"{lines.Count(l => l.StartsWith("NODE", StringComparison.Ordinal))} NODE lines");
                var nodes = lines.Where(l => l.StartsWith("NODE|", StringComparison.Ordinal)).Select(l => l.Split('|')).ToList();
                var expected = window.View.AccessibleNodes();
                Assert.Equal(expected.Count, nodes.Count);
                Assert.All(nodes, n => Assert.False(string.IsNullOrWhiteSpace(n[3]), $"{n[1]} has no name"));
                Assert.Equal("ControlType.CheckBox", nodes.Single(n => n[1] == "ShowInfoOverlay")[2]);
                Assert.Equal("ControlType.Edit", nodes.Single(n => n[1] == "ToolbarAutoHideDelay")[2]);
                Assert.Equal("ControlType.ComboBox", nodes.Single(n => n[1] == "LoadingMode")[2]);
                Assert.Equal("ControlType.Slider", nodes.Single(n => n[1] == "ToolbarOpacity")[2]);
                Assert.Equal("ControlType.Button", nodes.Single(n => n[1] == "Save")[2]);
                Assert.Equal("False", nodes.Single(n => n[1] == "InfoOverlayAutoHideDelay")[4]);
                var toggle = lines.Single(l => l.StartsWith("TOGGLE|", StringComparison.Ordinal)).Split('|');
                Assert.Equal(before ? "On" : "Off", toggle[2]);
                Assert.Equal(before ? "Off" : "On", toggle[3]);
                Assert.Equal(!before, check.Checked);
                return 0;
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
    // ---- ứng dụng THẬT chạy ở tiến trình riêng (vòng GetMessage ở thread chính) ----------------------------------------------

    [Fact]
    public async Task SeparateProcessApp_ExposesTheTree_AndTogglesTheCheckBox()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "PhotoReview.exe");
        Assert.True(File.Exists(exe), exe + " is missing");
        var dir = Path.Combine(Path.GetTempPath(), "d2d-uia-app-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Process? app = null;
        try
        {
            var script = Path.Combine(dir, "client.ps1");
            File.WriteAllText(script, UiaClientScript);
            var start = new ProcessStartInfo(exe, "--settings-d2d") { UseShellExecute = false, CreateNoWindow = true };
            start.Environment["PHOTOREVIEW_SETTINGS_D2D"] = "dark";
            app = Process.Start(start)!;
            nint hwnd = 0;
            var clock = Stopwatch.StartNew();
            while (hwnd == 0 && clock.Elapsed < TimeSpan.FromSeconds(30) && !app.HasExited)
            {
                hwnd = UiaClientNative.FindTopLevelWindow("PhotoReview.SettingsD2D.Surface", (uint)app.Id);
                if (hwnd == 0)
                {
                    using var gate = new ManualResetEventSlim(false);
                    _ = gate.Wait(50);
                }
            }

            Assert.NotEqual(0, hwnd);
            var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Hwnd {hwnd}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var client = Process.Start(psi)!;
            var stdout = client.StandardOutput.ReadToEndAsync();
            var stderr = client.StandardError.ReadToEndAsync();
            Assert.True(client.WaitForExit(45000), "PowerShell UIA client did not finish within 45 s");
            var lines = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
            output.WriteLine(string.Join(Environment.NewLine, lines.Where(l => !l.StartsWith("NODE", StringComparison.Ordinal))) + Environment.NewLine + (await stderr));
            var nodes = lines.Where(l => l.StartsWith("NODE|", StringComparison.Ordinal)).Select(l => l.Split('|')).ToList();
            Assert.True(nodes.Count >= 50, $"expected the whole page, got {nodes.Count} nodes");
            Assert.All(nodes, n => Assert.False(string.IsNullOrWhiteSpace(n[3]), $"{n[1]} has no name"));
            Assert.Equal("ControlType.CheckBox", nodes.Single(n => n[1] == "ShowInfoOverlay")[2]);
            var toggle = lines.Single(l => l.StartsWith("TOGGLE|", StringComparison.Ordinal)).Split('|');
            Assert.NotEqual(toggle[2], toggle[3]);
            output.WriteLine($"separate process: tree {lines.Single(l => l.StartsWith("TREE|", StringComparison.Ordinal))}, toggle {toggle[1]} ms");
        }
        finally
        {
            if (app is not null && !app.HasExited)
            {
                app.Kill(entireProcessTree: true);
            }

            app?.Dispose();
            Directory.Delete(dir, recursive: true);
        }
    }
    // ---- (d) trạng thái tắt ------------------------------------------------------------------------------------------------

    [Fact]
    public void DisabledNode_ReportsNotEnabled_AndRefusesToggleAndSetValue()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        WithUia((window, host) =>
        {
            var view = window.View;
            var field = (D2DFieldWidget)view.Find("InfoOverlayAutoHideDelay")!;
            var before = field.Box.Text;
            Assert.False(field.Enabled);
            var changes = new List<string>();
            view.Changed += w => changes.Add(w.Id);
            var result = host.Call(api =>
            {
                var disabled = api.ById("InfoOverlayAutoHideDelay");
                var fileInfo = api.ById("ShowFileInfo"); // tắt theo ô cha ShowInfoOverlay? (bật) -> kiểm chiều ngược lại bên dưới
                var enabledOnWire = UiaApi.Bool(disabled, UiaIds.IsEnabled);
                var focusableOnWire = UiaApi.Bool(disabled, UiaIds.IsKeyboardFocusable);
                var parentEnabled = UiaApi.Bool(fileInfo, UiaIds.IsEnabled);
                var hr = 0;
                try
                {
                    UiaApi.Pattern<IUiaClientValue>(disabled, UiaIds.ValuePattern)!.SetValue("1");
                }
                catch (COMException ex)
                {
                    hr = ex.HResult;
                }

                return (enabledOnWire, focusableOnWire, parentEnabled, hr);
            });

            Assert.False(result.enabledOnWire);
            Assert.False(result.focusableOnWire);
            Assert.True(result.parentEnabled);
            Assert.Equal(HrElementNotEnabled, result.hr);
            Assert.Equal(before, field.Box.Text);
            Assert.Empty(changes);
            return 0;
        });
    }

    [Fact]
    public void TogglingTheParentThroughUia_UpdatesIsEnabledOfTheChildren()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        WithUia((window, host) =>
        {
            var result = host.Call(api =>
            {
                var autoHide = UiaApi.Pattern<IUiaClientToggle>(api.ById("InfoOverlayAutoHide"), UiaIds.TogglePattern)!;
                var delay = api.ById("InfoOverlayAutoHideDelay");
                var before = UiaApi.Bool(delay, UiaIds.IsEnabled);
                autoHide.Toggle();
                var after = UiaApi.Bool(delay, UiaIds.IsEnabled);
                return (before, after);
            });

            Assert.False(result.before);
            Assert.True(result.after);
            Assert.True(((D2DFieldWidget)window.View.Find("InfoOverlayAutoHideDelay")!).Enabled);
            return 0;
        });
    }

    // ---- focus + (e) hình chữ nhật -------------------------------------------------------------------------------------------

    [Fact]
    public void SetFocus_MovesTheViewFocus_AndBoundingRectanglesAreInsideTheWindowAtTheRightPlace()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        WithUia((window, host) =>
        {
            var view = window.View;
            var client = window.ClientToScreenRect(new PxRect(0, 0, view.ClientWidth, view.ClientHeight));
            var expected = view.AccessibleNodes();
            var result = host.Call(api =>
            {
                var target = api.ById("ToolbarAutoHideDelay");
                target.SetFocus();
                var rows = api.Provider().Select(e => (
                    Id: UiaApi.Str(e, UiaIds.AutomationId),
                    Rect: e.GetCurrentBoundingRectangle(),
                    Offscreen: UiaApi.Bool(e, UiaIds.IsOffscreen),
                    Focusable: UiaApi.Bool(e, UiaIds.IsKeyboardFocusable))).ToList();
                return (HasFocus: UiaApi.Bool(target, UiaIds.HasKeyboardFocus), rows);
            });

            Assert.Equal("ToolbarAutoHideDelay", view.Focused!.Id);
            Assert.True(result.HasFocus);
            Assert.Equal(expected.Count, result.rows.Count);
            foreach (var row in result.rows)
            {
                var wanted = expected.Single(n => n.AutomationId == row.Id);
                Assert.Equal(wanted.IsFocusable, row.Focusable);
                if (row.Offscreen)
                {
                    Assert.True(row.Rect.Width <= 0 || row.Rect.Height <= 0, $"{row.Id} is offscreen so its rectangle must be empty");
                    continue;
                }

                Assert.True(row.Rect.Width > 0 && row.Rect.Height > 0, $"{row.Id} has an empty rectangle");
                Assert.True(row.Rect.Left >= client.X && row.Rect.Top >= client.Y, $"{row.Id} starts outside the window client");
                Assert.True(row.Rect.Right <= client.Right && row.Rect.Bottom <= client.Bottom, $"{row.Id} ends outside the window client");
                var screen = window.ClientToScreenRect(wanted.ClientBounds);
                Assert.Equal(screen.X, row.Rect.Left);
                Assert.Equal(screen.Y, row.Rect.Top);
                Assert.Equal(screen.Width, row.Rect.Width);
                Assert.Equal(screen.Height, row.Rect.Height);
            }

            Assert.Contains(result.rows, r => !r.Offscreen);
            Assert.Contains(result.rows, r => r.Offscreen); // nhóm cuối (TitleBar) nằm dưới viewport 640x560
            return 0;
        });
    }

    // ---- sự kiện -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Events_PropertyChangedAndFocusChanged_ReachAClientListener()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        WithUia((window, host) =>
        {
            var props = new UiaPropertyListener();
            var focus = new UiaFocusListener();
            host.Call(api =>
            {
                int[] ids = [UiaIds.ToggleState, UiaIds.ValueValue, UiaIds.RangeValue, UiaIds.IsEnabled];
                unsafe
                {
                    fixed (int* p = ids)
                    {
                        api.Ui.AddPropertyChangedEventHandlerNativeArray(api.Root, 4, 0, props, p, ids.Length);
                    }
                }

                api.Ui.AddFocusChangedEventHandler(0, focus);
                return 0;
            });

            host.Call(api =>
            {
                UiaApi.Pattern<IUiaClientToggle>(api.ById("ShowInfoOverlay"), UiaIds.TogglePattern)!.Toggle();
                UiaApi.Pattern<IUiaClientValue>(api.ById("ToolbarAutoHideDelay"), UiaIds.ValuePattern)!.SetValue("640");
                api.ById("ToolbarOpacity").SetFocus();
                return 0;
            });

            // Sự kiện đến bất đồng bộ từ thread của UIA: bơm thông điệp có hạn chót.
            using var gate = new ManualResetEventSlim(false);
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(15)
                && !(props.Has("ShowInfoOverlay", UiaIds.ToggleState, "0") && props.Has("ToolbarAutoHideDelay", UiaIds.ValueValue, "640")
                    && props.Has("ShowFileInfo", UiaIds.IsEnabled, "False") && focus.Ids.Contains("ToolbarOpacity")))
            {
                UiaClientNative.PumpUntil(gate.WaitHandle, TimeSpan.FromMilliseconds(20));
            }

            output.WriteLine("property events: " + string.Join("; ", props.Seen));
            output.WriteLine("focus events: " + string.Join("; ", focus.Ids));
            Assert.True(props.Has("ShowInfoOverlay", UiaIds.ToggleState, "0"), "ToggleState event missing");
            Assert.True(props.Has("ToolbarAutoHideDelay", UiaIds.ValueValue, "640"), "Value event missing");
            Assert.True(props.Has("ShowFileInfo", UiaIds.IsEnabled, "False"), "dependent IsEnabled event missing");
            Assert.Contains("ToolbarOpacity", focus.Ids);
            return 0;
        });
    }
}

// ======================================================================================================================
// UIA CLIENT: ID (UIAutomationClient.h), giao diện COM, bộ gọi
// ======================================================================================================================

internal static class UiaIds
{
    public const int Name = 30005;
    public const int ControlType = 30003;
    public const int AutomationId = 30011;
    public const int IsEnabled = 30010;
    public const int HasKeyboardFocus = 30008;
    public const int IsKeyboardFocusable = 30009;
    public const int IsOffscreen = 30022;
    public const int FrameworkId = 30024;
    public const int ValueValue = 30045;
    public const int RangeValue = 30047;
    public const int ToggleState = 30086;

    public const int InvokePattern = 10000;
    public const int ValuePattern = 10002;
    public const int RangeValuePattern = 10003;
    public const int ExpandCollapsePattern = 10005;
    public const int TogglePattern = 10015;

    public const int ButtonType = 50000;
    public const int CheckBoxType = 50002;
    public const int ComboBoxType = 50003;
    public const int EditType = 50004;
    public const int SliderType = 50015;
    public const int TextType = 50020;
    public const int GroupType = 50026;
    public const int WindowType = 50032;
}

internal static class ClientVariantKinds
{
    public const ushort I4 = 3;
    public const ushort R8 = 5;
    public const ushort Bstr = 8;
    public const ushort Bool = 11;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ClientVariant
{
    public ushort Vt;
    public ushort R1;
    public ushort R2;
    public ushort R3;
    public nint Data;
    public nint Data2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ClientRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

internal static unsafe partial class UiaClientNative
{
    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    [LibraryImport("oleaut32.dll")]
    public static partial int VariantClear(ClientVariant* variant);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowEx(nint parent, nint after, string className, string? title);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    /// <summary>Cửa sổ cấp cao nhất của lớp này thuộc tiến trình <paramref name="processId"/> (0 nếu chưa có).</summary>
    public static nint FindTopLevelWindow(string className, uint processId)
    {
        nint hwnd = 0;
        while ((hwnd = FindWindowEx(0, hwnd, className, null)) != 0)
        {
            _ = GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == processId)
            {
                return hwnd;
            }
        }

        return 0;
    }

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessage(uint thread, uint message, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    private static partial uint GetCurrentThreadId();

    /// <summary>
    /// Vòng thông điệp THẬT (GetMessage) trên thread gọi tới khi <paramref name="done"/> báo hiệu hoặc hết <paramref name="limit"/>: mỗi
    /// SendMessage/PostMessage của provider hoặc của UIA đánh thức ngay (không có chu kỳ thăm dò), như vòng lặp của ứng dụng thật.
    /// </summary>
    public static void PumpUntil(WaitHandle done, TimeSpan limit)
    {
        var thread = GetCurrentThreadId();
        _ = Task.Run(() =>
        {
            _ = done.WaitOne(limit);
            _ = PostThreadMessage(thread, 0x0012 /* WM_QUIT */, 0, 0);
        });
        Msg message;
        while (User32.GetMessage(&message, 0, 0, 0) > 0)
        {
            _ = User32.TranslateMessage(&message);
            _ = User32.DispatchMessage(&message);
        }
    }
}

/// <summary>IUIAutomation (vtable theo UIAutomationClient.h): khai tới hàm cần dùng; khe không dùng là Sxx.</summary>
[GeneratedComInterface]
[Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee")]
internal partial interface IUiaClient
{
    void S03();
    void S04();
    void S05();
    IUiaClientElement ElementFromHandle(nint hwnd);                  // [6]
    void S07();
    void S08();
    void S09();
    void S10();
    void S11();
    void S12();
    void S13();
    IUiaClientTreeWalker GetControlViewWalker();                      // [14]
    void S15();
    void S16();
    void S17();
    void S18();
    void S19();
    void S20();
    nint CreateTrueCondition();                                       // [21]
    void S22();
    void S23();
    void S24();
    void S25();
    void S26();
    void S27();
    void S28();
    void S29();
    void S30();
    void S31();
    void S32();
    void S33();
    unsafe void AddPropertyChangedEventHandlerNativeArray(IUiaClientElement element, int scope, nint cacheRequest,
        IUiaPropertyHandler handler, int* properties, int count);     // [34]
    void S35();
    void S36();
    void S37();
    void S38();
    void AddFocusChangedEventHandler(nint cacheRequest, IUiaFocusHandler handler); // [39]
    void RemoveFocusChangedEventHandler(IUiaFocusHandler handler);   // [40]
    void RemoveAllEventHandlers();                                    // [41]
}

[GeneratedComInterface]
[Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e")]
internal partial interface IUiaClientElement
{
    void SetFocus();                                                  // [3]
    nint GetRuntimeId();                                              // [4]
    IUiaClientElement? FindFirst(int scope, nint condition);          // [5]
    IUiaClientElementArray FindAll(int scope, nint condition);        // [6]
    void S07();
    void S08();
    void S09();
    ClientVariant GetCurrentPropertyValue(int propertyId);            // [10]
    void S11();
    void S12();
    void S13();
    void S14();
    void S15();
    nint GetCurrentPattern(int patternId);                            // [16]
    void S17();
    void S18();
    void S19();
    void S20();
    void S21();
    void S22();
    void S23();
    void S24();
    void S25();
    void S26();
    void S27();
    void S28();
    void S29();
    void S30();
    void S31();
    void S32();
    void S33();
    void S34();
    void S35();
    void S36();
    void S37();
    void S38();
    void S39();
    void S40();
    void S41();
    void S42();
    ClientRect GetCurrentBoundingRectangle();                         // [43]
}

[GeneratedComInterface]
[Guid("14314595-b4bc-4055-95f2-58f2e42c9855")]
internal partial interface IUiaClientElementArray
{
    int GetLength();                                                  // [3]
    IUiaClientElement GetElement(int index);                          // [4]
}

[GeneratedComInterface]
[Guid("4042c624-389c-4afc-a630-9df854a541fc")]
internal partial interface IUiaClientTreeWalker
{
    IUiaClientElement? GetParentElement(IUiaClientElement element);   // [3]
}

[GeneratedComInterface]
[Guid("94cf8058-9b8d-4ab9-8bfd-4cd0a33c8c70")]
internal partial interface IUiaClientToggle
{
    void Toggle();                                                    // [3]
    int GetCurrentToggleState();                                      // [4]
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("a94cd8b1-0844-4cd6-9d2d-640537ab39e9")]
internal partial interface IUiaClientValue
{
    void SetValue(string value);                                      // [3]
    nint GetCurrentValue();                                           // [4] BSTR
    int GetCurrentIsReadOnly();                                       // [5]
}

[GeneratedComInterface]
[Guid("59213f4f-7346-49e5-b120-80555987a148")]
internal partial interface IUiaClientRange
{
    void SetValue(double value);                                      // [3]
    double GetCurrentValue();                                         // [4]
    int GetCurrentIsReadOnly();                                       // [5]
    double GetCurrentMaximum();                                       // [6]
    double GetCurrentMinimum();                                       // [7]
}

[GeneratedComInterface]
[Guid("619be086-1f4e-4ee4-bafa-210128738730")]
internal partial interface IUiaClientExpand
{
    void Expand();                                                    // [3]
    void Collapse();                                                  // [4]
    int GetCurrentExpandCollapseState();                              // [5]
}

[GeneratedComInterface]
[Guid("fb377fbe-8ea6-46d5-9c73-6499642d3059")]
internal partial interface IUiaClientInvoke
{
    void Invoke();                                                    // [3]
}

[GeneratedComInterface]
[Guid("40cd37d4-c756-4b0c-8c6f-bddfeeb13b50")]
internal partial interface IUiaPropertyHandler
{
    void HandlePropertyChangedEvent(IUiaClientElement sender, int propertyId, ClientVariant newValue); // [3]
}

[GeneratedComInterface]
[Guid("c270f6b5-5c69-4290-9745-7a7f97169468")]
internal partial interface IUiaFocusHandler
{
    void HandleFocusChangedEvent(IUiaClientElement sender);           // [3]
}

/// <summary>Bộ nghe thuộc tính: ghi "AutomationId|propertyId|giá trị chữ" từ thread của UIA.</summary>
[GeneratedComClass]
internal sealed partial class UiaPropertyListener : IUiaPropertyHandler
{
    private readonly ConcurrentQueue<string> _seen = new();

    public IReadOnlyCollection<string> Seen => _seen;

    public bool Has(string id, int propertyId, string value) => _seen.Contains($"{id}|{propertyId}|{value}");

    public void HandlePropertyChangedEvent(IUiaClientElement sender, int propertyId, ClientVariant newValue)
    {
        var text = newValue.Vt switch
        {
            ClientVariantKinds.Bstr => Marshal.PtrToStringBSTR(newValue.Data),
            ClientVariantKinds.Bool => ((short)newValue.Data != 0).ToString(),
            ClientVariantKinds.I4 => ((int)newValue.Data).ToString(CultureInfo.InvariantCulture),
            ClientVariantKinds.R8 => BitConverter.Int64BitsToDouble(newValue.Data).ToString(CultureInfo.InvariantCulture),
            _ => "?",
        };
        _seen.Enqueue($"{UiaApi.TryId(sender)}|{propertyId}|{text}");
    }
}

[GeneratedComClass]
internal sealed partial class UiaFocusListener : IUiaFocusHandler
{
    private readonly ConcurrentQueue<string> _ids = new();

    public IReadOnlyCollection<string> Ids => _ids;

    public void HandleFocusChangedEvent(IUiaClientElement sender) => _ids.Enqueue(UiaApi.TryId(sender));
}

/// <summary>Gọi client UIA trên thread MTA trong khi thread gọi (STA, chủ cửa sổ) bơm thông điệp; có hạn chót 30 s.</summary>
internal sealed class UiaHost(D2DSettingsHost window) : IDisposable
{
    private static readonly Guid CUIAutomation = new("ff48dba4-60ef-4201-aa87-54103eef594e");
    private static readonly Guid IUIAutomation = new("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");

    private IUiaClient? _ui;
    private UiaApi? _api;

    public T Call<T>(Func<UiaApi, T> body)
    {
        var task = Task.Run(() =>
        {
            _ = UiaClientNative.CoInitializeEx(0, 0 /* COINIT_MULTITHREADED */);
            _ui ??= Create();
            _api ??= new UiaApi(_ui, window.Hwnd); // cây tĩnh: giữ chỉ mục AutomationId giữa các lần gọi
            return body(_api);
        });
        using var done = new ManualResetEventSlim(false);
        _ = task.ContinueWith(_ => done.Set(), TaskScheduler.Default);
        UiaClientNative.PumpUntil(done.WaitHandle, TimeSpan.FromSeconds(30));
        if (!done.IsSet)
        {
            throw new TimeoutException("UIA client call did not finish within 30 s (UI thread was pumping).");
        }

        D2DTestKit.Pump();
        return task.GetAwaiter().GetResult();
    }

    private static unsafe IUiaClient Create()
    {
        var hr = UiaClientNative.CoCreateInstance(in CUIAutomation, 0, 1 /* CLSCTX_INPROC_SERVER */, in IUIAutomation, out var pointer);
        Marshal.ThrowExceptionForHR(hr);
        return ComInterfaceMarshaller<IUiaClient>.ConvertToManaged((void*)pointer)!;
    }

    public void Dispose()
    {
        if (_ui is null)
        {
            return;
        }

        try
        {
            _ = Call(_ =>
            {
                _ui.RemoveAllEventHandlers();
                return 0;
            });
        }
        catch (Exception ex) when (ex is COMException or TimeoutException)
        {
            // best effort: the window is going away anyway.
        }

        _ui = null;
        _api = null;
    }
}

internal sealed class UiaApi(IUiaClient ui, nint hwnd)
{
    public IUiaClient Ui => ui;

    public IUiaClientElement Root { get; } = ui.ElementFromHandle(hwnd);

    public static string TryId(IUiaClientElement element)
    {
        try
        {
            var v = element.GetCurrentPropertyValue(UiaIds.AutomationId);
            return ReadString(ref v);
        }
        catch (COMException)
        {
            return "<gone>";
        }
    }

    private static unsafe string ReadString(ref ClientVariant v)
    {
        var text = v.Vt == ClientVariantKinds.Bstr ? Marshal.PtrToStringBSTR(v.Data) : string.Empty;
        fixed (ClientVariant* p = &v)
        {
            _ = UiaClientNative.VariantClear(p);
        }

        return text;
    }

    public static string Str(IUiaClientElement e, int propertyId)
    {
        var v = e.GetCurrentPropertyValue(propertyId);
        return ReadString(ref v);
    }

    public static int Int(IUiaClientElement e, int propertyId)
    {
        var v = e.GetCurrentPropertyValue(propertyId);
        return v.Vt == ClientVariantKinds.I4 ? (int)v.Data : throw new InvalidOperationException($"property {propertyId}: vt={v.Vt}, expected I4");
    }

    public static bool Bool(IUiaClientElement e, int propertyId)
    {
        var v = e.GetCurrentPropertyValue(propertyId);
        return v.Vt == ClientVariantKinds.Bool ? (short)v.Data != 0 : throw new InvalidOperationException($"property {propertyId}: vt={v.Vt}, expected BOOL");
    }

    public static string Bstr(nint bstr)
    {
        var text = Marshal.PtrToStringBSTR(bstr);
        Marshal.FreeBSTR(bstr);
        return text;
    }

    public static T? Pattern<T>(IUiaClientElement e, int patternId)
        where T : class
    {
        var pointer = e.GetCurrentPattern(patternId);
        unsafe
        {
            return pointer == 0 ? null : ComInterfaceMarshaller<T>.ConvertToManaged((void*)pointer);
        }
    }

    private List<IUiaClientElement> Find(IUiaClientElement scope, int treeScope)
    {
        var condition = ui.CreateTrueCondition();
        try
        {
            var array = scope.FindAll(treeScope, condition);
            var list = new List<IUiaClientElement>();
            for (var i = 0; i < array.GetLength(); i++)
            {
                list.Add(array.GetElement(i));
            }

            return list;
        }
        finally
        {
            _ = Marshal.Release(condition);
        }
    }

    /// <summary>Mọi hậu duệ do provider phát (lọc theo FrameworkId; bỏ phần tử khung cửa sổ do HWND proxy cấp).</summary>
    public List<IUiaClientElement> Provider() => Find(Root, 4).Where(e => Str(e, UiaIds.FrameworkId) == "PhotoReviewD2D").ToList();

    public List<IUiaClientElement> Children(IUiaClientElement e) => Find(e, 2).Where(c => Str(c, UiaIds.FrameworkId) == "PhotoReviewD2D").ToList();

    private Dictionary<string, IUiaClientElement>? _byId;

    /// <summary>Tra theo AutomationId (cây tĩnh trong một lần gọi nên lập chỉ mục một lần để khỏi quét lại 50 phần tử mỗi lần).</summary>
    public IUiaClientElement ById(string id)
        => (_byId ??= Provider().ToDictionary(e => Str(e, UiaIds.AutomationId), e => e))[id];

    public IUiaClientElement? Parent(IUiaClientElement e) => ui.GetControlViewWalker().GetParentElement(e);
}
