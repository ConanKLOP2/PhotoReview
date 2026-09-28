using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using PhotoReview.App;
using PhotoReview.App.Localization;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.IO;
using PhotoReview.Core.Localization;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// I18N L05: the XAML windows really load with <c>{loc:Tr}</c> texts. With Vietnamese pinned they show the exact
/// text they had before extraction (including ComboBoxItems, the ContextMenu and the Popup, which are not in the
/// visual tree), and a language switch updates them live without reopening the window (Q-L8).
/// Switches the ambient language, so it runs in the non-parallel "GlobalState" collection and restores Vietnamese.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class WindowLocalizationTests
{
    [Fact]
    public async Task DialogWindows_ShowCatalogText_AndFollowALiveLanguageSwitch()
    {
        using var temp = new TempRoot("i18n-windows");
        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                var localization = new LocalizationService(new PathsStub(temp.Combine("config.json")), new PhysicalFileSystem(), NullLog.Instance);
                var settings = new SettingsWindow(new AppSettings { UiLanguage = "auto" }, localization: localization);
                var recovery = new RecoveryWindow([]);
                var diagnostics = new DiagnosticsWindow(new ReviewMetrics().Snapshot());
                var batch = new BatchReviewWindow([Path.Combine(temp.Path, "gone.jpg")]);
                var actions = new ActionProfilesWindow(ReviewAction.Defaults());

                // Vietnamese (pinned by LocalizationModuleInit): the shipped vi.json text (L12 copy).
                Assert.Equal("Photo Review · Cài đặt", settings.Title);
                var settingsTexts = Texts(settings);
                Assert.Contains("Cài đặt", settingsTexts);
                Assert.Contains("Hiển thị & hiệu năng", settingsTexts);
                Assert.Contains("Tên (theo thứ tự Explorer nếu đang mở)", settingsTexts);
                Assert.Contains("Nhanh (Linear, mượt hơn khi thu phóng)", settingsTexts);
                Assert.Contains("Mở vị trí tệp log", settingsTexts);
                Assert.Contains("Phím tắt", settingsTexts);
                Assert.Contains("nhấn phím trực tiếp vào ô để cấu hình", settingsTexts);
                Assert.Contains("Hoàn tác (Ctrl + phím)", settingsTexts);
                Assert.Contains("Lưu", settingsTexts);
                Assert.Contains("Language / Ngôn ngữ", settingsTexts);
                Assert.Equal("Tự động (theo Windows)", Assert.IsType<LanguageOption>(settings.LanguageCombo.SelectedItem).DisplayName);
                Assert.Contains(settings.LanguageCombo.Items.Cast<LanguageOption>(), o => o.Code == "vi" && o.DisplayName == "Tiếng Việt");

                Assert.Equal("Phục hồi", recovery.Title);
                Assert.Equal("Không có thao tác đang chờ hoặc thất bại cần xem.", recovery.SummaryText.Text);
                Assert.Equal("Thử lại thao tác Di chuyển hoặc Sao chép bị lỗi", AutomationProperties.GetName(recovery.RetryButton));
                Assert.Contains("Đóng", Texts(recovery));
                Assert.Contains("Thử lại Di chuyển/Sao chép", Texts(recovery));
                Assert.Contains("Xóa các mục đã chọn khỏi danh sách phục hồi", Texts(recovery));
                Assert.Contains("Xóa tất cả mục khỏi danh sách phục hồi", Texts(recovery));

                Assert.Equal("Chưa truy vấn", diagnostics.ExplorerOrderText.Text);
                Assert.Equal("Không có", diagnostics.HitRateText.Text);
                Assert.Contains("Số lần đọc tệp nguồn", Texts(diagnostics));

                Assert.Equal("Xem trước xử lý hàng loạt", batch.Title);
                Assert.Equal("1 tệp sẽ được đưa vào Thùng rác. Hãy kiểm tra danh sách trước khi xác nhận.", batch.SummaryText.Text);
                Assert.Equal($"gone.jpg    (không còn tồn tại)    {Path.Combine(temp.Path, "gone.jpg")}", batch.FilesList.Items[0]);

                // Operation combo: order is load-bearing (SelectedIndex mapping); no separate legacy "Delete" item.
                Assert.Equal(["Di chuyển", "Sao chép", "Đưa vào Thùng rác"],
                    actions.OperationCombo.Items.Cast<ComboBoxItem>().Select(i => (string)i.Content));
                Assert.Contains("+ Thêm", Texts(actions));

                // Live switch: bound texts follow without reopening the windows.
                using (TestLocalization.Use(TestLocalization.English))
                {
                    await StaTestHost.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                    Assert.Equal("Retry the failed Move or Copy", AutomationProperties.GetName(recovery.RetryButton));
                    Assert.Equal("Close", Assert.Single(Texts(recovery), t => t == "Close"));
                    settingsTexts = Texts(settings);
                    Assert.Contains("Settings", settingsTexts);
                    Assert.Contains("Name (follows Explorer when it is open)", settingsTexts);
                    Assert.Contains("Save", settingsTexts);
                    Assert.Contains("Language / Ngôn ngữ", settingsTexts); // bilingual on purpose
                    Assert.Equal("Batch preview", batch.Title);
                    Assert.Contains("+ Add", Texts(actions));
                }
                await StaTestHost.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                Assert.Equal("Thử lại thao tác Di chuyển hoặc Sao chép bị lỗi", AutomationProperties.GetName(recovery.RetryButton));

                foreach (var window in new Window[] { settings, recovery, diagnostics, batch, actions }) window.Close();
            });
        }
        finally
        {
            TestLocalization.UseVietnamese();
        }
    }

    [Fact]
    public async Task MainWindow_ToolbarPopupAndContextMenuTexts_ComeFromTheCatalog()
    {
        using var dataRoot = new DataRootFixture();
        MainWindow? window = null;
        try
        {
            await StaTestHost.RunAsync(() =>
            {
                window = TestAppHost.CreateMainWindow(null);
                var root = (Grid)window.Content;
                // UX-FIXES-ROUND1 + Q-R48: Undo, Delete (primary destructive action), a Separator, Fit,
                // Zoom-to-level (header filled on open), the Zoom submenu, a Separator, the folder group
                // (Open/Next/Previous folder), a Separator, Q-R48's "Open in External Editor" (hidden unless
                // configured, but still present in the item list), a Separator, Settings.
                var menuItems = root.ContextMenu.Items.OfType<MenuItem>().ToList();
                Assert.Equal(10, menuItems.Count);
                var undo = menuItems[0];
                var delete = menuItems[1];
                var fit = menuItems[2];
                var zoomToLevel = menuItems[3];
                var zoomMenu = menuItems[4];
                var openFolder = menuItems[5];
                var nextFolder = menuItems[6];
                var previousFolder = menuItems[7];
                var openInExternalEditor = menuItems[8];
                var settings = menuItems[9];
                Assert.Equal("Hoàn tác thao tác vừa thực hiện", undo.Header);
                Assert.Equal("Hoàn tác thao tác vừa thực hiện", AutomationProperties.GetName(undo));
                Assert.Equal("Đưa vào Thùng rác", delete.Header);
                Assert.Equal("Đưa ảnh hiện tại vào Thùng rác", AutomationProperties.GetName(delete));
                Assert.Equal("Vừa khung hình", fit.Header);
                Assert.Equal("Đưa ảnh vừa khung cửa sổ", AutomationProperties.GetName(fit));
                Assert.NotNull(zoomToLevel);
                Assert.Equal("Thu phóng", zoomMenu.Header);
                // Populated before the first open: an empty MenuItem shows no submenu arrow and cannot open.
                Assert.True(zoomMenu.HasItems);
                Assert.Equal("Menu con Thu phóng", AutomationProperties.GetName(zoomMenu));
                Assert.Equal("Mở thư mục…", openFolder.Header);
                Assert.Equal("Mở thư mục ảnh", AutomationProperties.GetName(openFolder));
                Assert.Equal("Thư mục kế tiếp", nextFolder.Header);
                Assert.Equal("Thư mục trước", previousFolder.Header);
                Assert.Equal("Mở bằng trình chỉnh sửa ngoài", openInExternalEditor.Header);
                Assert.Equal("Mở ảnh hiện tại bằng trình chỉnh sửa ngoài", AutomationProperties.GetName(openInExternalEditor));
                Assert.Equal("Cài đặt…", settings.Header);
                Assert.Equal("Mở cài đặt", AutomationProperties.GetName(settings));
                Assert.Equal("Ảnh xem trước bên trái, nhấn để chọn", AutomationProperties.GetName(window.CompareLeftBorder));
                Assert.Equal("Ảnh xem trước bên phải, nhấn để chọn", AutomationProperties.GetName(window.CompareRightBorder));

                var texts = Texts(window);
                Assert.Contains("Mở thư mục ảnh", texts);   // automation name of the folder button and its tooltip
                Assert.Contains("Mở cài đặt", texts);       // automation name of the toolbar settings button
                Assert.Contains("Vừa khung", texts);        // Fit toolbar button
                Assert.Contains("Bỏ bản (1) trùng hash", texts); // inside the tools Popup
                Assert.Contains("Mở chẩn đoán hiệu năng", texts);
                return Task.CompletedTask;
            });
        }
        finally
        {
            var opened = window;
            if (opened is not null)
                await StaTestHost.RunAsync(() =>
                {
                    try { opened.Close(); } catch (InvalidOperationException) { }
                    return Task.CompletedTask;
                });
        }
    }

    /// <summary>Every user-visible string of the logical tree: Content, Header, Text/Runs, ToolTip, automation name.</summary>
    private static List<string> Texts(DependencyObject root)
    {
        var texts = new List<string>();
        Collect(root, texts);
        return texts;
    }

    private static void Collect(object node, List<string> texts)
    {
        if (node is not DependencyObject d) return;
        if (AutomationProperties.GetName(d) is { Length: > 0 } name) texts.Add(name);
        if (d is FrameworkElement { ToolTip: string tip }) texts.Add(tip);
        switch (d)
        {
            case Window { Title: { } title }: texts.Add(title); break;
            case HeaderedContentControl { Header: string header }: texts.Add(header); break;
            case HeaderedItemsControl { Header: string header }: texts.Add(header); break;
        }
        if (d is ContentControl { Content: string content }) texts.Add(content);
        if (d is TextBlock block)
        {
            if (block.Inlines.Count > 0) texts.AddRange(block.Inlines.OfType<Run>().Select(r => r.Text));
            else texts.Add(block.Text);
        }
        foreach (var child in LogicalTreeHelper.GetChildren(d)) Collect(child, texts);
    }

    private sealed class PathsStub(string configFile) : IAppPaths
    {
        public string ConfigFile => configFile;
        public string JournalFile => throw new NotSupportedException();
        public string SessionsDir => throw new NotSupportedException();
        public string LogFile => throw new NotSupportedException();
        public string PreviewCacheDir => throw new NotSupportedException();
        public string ThumbnailCacheDir => throw new NotSupportedException();
        public string WindowPlacementFile => throw new NotSupportedException();
    }
}
