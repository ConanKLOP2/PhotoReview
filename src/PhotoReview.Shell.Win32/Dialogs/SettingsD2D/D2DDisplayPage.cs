using PhotoReview.App.Presenters;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;

namespace PhotoReview.Shell.Win32.Dialogs.SettingsD2D;

/// <summary>
/// SPIKE: trang "Display" của Settings dựng trên <see cref="D2DSettingsView"/> (một bề mặt Direct2D). Cùng dữ liệu và quy tắc với
/// <c>DisplaySettingsPage</c> (cửa sổ con): cùng chuỗi <c>Tr.*</c>, cùng thứ tự mục combobox, cùng <see cref="SettingsFormState"/>
/// (Load/Collect), cùng phụ thuộc (tick cha bật/tắt ô con). KHÔNG đổi presenter.
/// </summary>
internal sealed class D2DDisplayPage
{
    private static readonly ExifInfoFields[] ExifOrder =
    [
        ExifInfoFields.FileName, ExifInfoFields.DateTaken, ExifInfoFields.ModifiedDate, ExifInfoFields.Dimensions, ExifInfoFields.Camera,
        ExifInfoFields.Lens, ExifInfoFields.Iso, ExifInfoFields.FocalLength, ExifInfoFields.Aperture, ExifInfoFields.ShutterSpeed,
    ];

    private static readonly TitleBarFields[] TitleOrder =
    [
        TitleBarFields.FolderName, TitleBarFields.FolderPath, TitleBarFields.IndexCount, TitleBarFields.FileName, TitleBarFields.FileSize,
        TitleBarFields.Dimensions, TitleBarFields.ModifiedDate, TitleBarFields.DateTaken, TitleBarFields.Camera, TitleBarFields.Lens,
        TitleBarFields.Iso, TitleBarFields.FocalLength, TitleBarFields.Aperture, TitleBarFields.ShutterSpeed,
    ];

    private readonly D2DCheckWidget _showInfoOverlay = new("ShowInfoOverlay", () => Tr.SettingsShowInfoOverlayLabel);
    private readonly D2DCheckWidget _showFileInfo = new("ShowFileInfo", () => Tr.SettingsShowFileInfoLabel, 12 * 3 / 2);
    private readonly D2DCheckWidget _showFolderInfo = new("ShowFolderInfo", () => Tr.SettingsShowFolderInfoLabel, 12 * 3 / 2);
    private readonly D2DFieldWidget _fontSize = new("InfoOverlayFontSize", () => Tr.SettingsInfoOverlayFontSizeLabel, 60, null, allowDecimal: true);
    private readonly D2DCheckWidget _showZoomIndicator = new("ShowZoomIndicator", () => Tr.SettingsShowZoomIndicatorLabel);
    private readonly D2DCheckWidget _toolbarAutoHide = new("ToolbarAutoHide", () => Tr.SettingsToolbarAutoHideLabel);
    private readonly D2DFieldWidget _toolbarDelay = new("ToolbarAutoHideDelay", () => Tr.SettingsToolbarAutoHideDelayLabel, 68, null);
    private readonly D2DSliderWidget _opacity = new("ToolbarOpacity", () => Tr.SettingsToolbarOpacityLabel,
        AppSettings.MinToolbarOpacityPercent, AppSettings.MaxToolbarOpacityPercent, v => SettingsPresenter.ToolbarOpacityValueText(v));
    private readonly D2DCheckWidget _infoAutoHide = new("InfoOverlayAutoHide", () => Tr.SettingsInfoOverlayAutoHideLabel);
    private readonly D2DFieldWidget _infoDelay = new("InfoOverlayAutoHideDelay", () => Tr.SettingsInfoOverlayAutoHideDelayLabel, 68, null);
    private readonly D2DComboWidget _loadingMode;
    private readonly D2DComboWidget _scalingQuality;
    private readonly D2DCheckWidget _showExif = new("ShowExifInfo", () => Tr.SettingsShowExifInfoLabel);
    private readonly D2DCheckFlowWidget _exifFlow = new("ExifFields", 18);
    private readonly D2DCheckFlowWidget _titleFlow = new("TitleBarFields", 0);

    public D2DDisplayPage(D2DSettingsView view)
    {
        View = view;
        _fontSize.AutomationLabel = () => Tr.SettingsInfoOverlayFontSizeLabel;
        _loadingMode = new D2DComboWidget("LoadingMode", () => Tr.SettingsLoadingModeLabel,
        [
            () => Tr.EnumLoadingModeFast,
            () => Tr.EnumLoadingModePreview,
            () => Tr.SettingsLoadingModeOriginalWithHint,
        ]);
        _scalingQuality = new D2DComboWidget("ScalingQuality", () => Tr.SettingsScalingQualityLabel,
        [
            () => Tr.EnumScalingQualityHighQuality,
            () => Tr.EnumScalingQualityLinear,
        ]);

        var display = new D2DGroupWidget("GroupDisplay", () => Tr.SettingsNavDisplay);
        display.Children.AddRange([_showInfoOverlay, _showFileInfo, _showFolderInfo, _fontSize, _showZoomIndicator]);

        var toolbar = new D2DGroupWidget("GroupToolbar", () => Tr.SettingsGroupToolbar);
        toolbar.Children.AddRange(
        [
            _toolbarAutoHide, _toolbarDelay, new D2DNoteWidget("ToolbarAutoHideHint", () => Tr.SettingsToolbarAutoHideHint),
            _opacity, new D2DNoteWidget("ToolbarOpacityHint", () => SettingsPresenter.ToolbarOpacityHint()),
        ]);

        var overlay = new D2DGroupWidget("GroupInfoOverlay", () => Tr.SettingsGroupInfoOverlay);
        overlay.Children.AddRange(
        [
            _infoAutoHide, _infoDelay, new D2DNoteWidget("InfoOverlayAutoHideHint", () => Tr.SettingsInfoOverlayAutoHideHint),
        ]);

        var perf = new D2DGroupWidget("GroupDisplayPerformance", () => Tr.SettingsGroupDisplayPerformance);
        perf.Children.AddRange([_loadingMode, _scalingQuality]);

        Func<string>[] exifNames =
        [
            () => Tr.SettingsExifFieldFileName, () => Tr.SettingsExifFieldDateTaken, () => Tr.SettingsExifFieldModifiedDate,
            () => Tr.SettingsExifFieldDimensions, () => Tr.SettingsExifFieldCamera, () => Tr.SettingsExifFieldLens,
            () => Tr.SettingsExifFieldIso, () => Tr.SettingsExifFieldFocalLength, () => Tr.SettingsExifFieldAperture,
            () => Tr.SettingsExifFieldShutterSpeed,
        ];
        for (var i = 0; i < exifNames.Length; i++)
        {
            _exifFlow.Checks.Add(new D2DCheckWidget("Exif" + ExifOrder[i], exifNames[i]));
        }

        var exif = new D2DGroupWidget("GroupExif", () => Tr.SettingsGroupExif);
        exif.Children.AddRange([_showExif, new D2DNoteWidget("ExifHint", () => Tr.SettingsGroupExifHint, aligned: false), _exifFlow]);

        Func<string>[] titleNames =
        [
            () => Tr.SettingsTitleBarFieldFolderName, () => Tr.SettingsTitleBarFieldFolderPath, () => Tr.SettingsTitleBarFieldIndexCount,
            () => Tr.SettingsExifFieldFileName, () => Tr.SettingsTitleBarFieldFileSize, () => Tr.SettingsExifFieldDimensions,
            () => Tr.SettingsExifFieldModifiedDate, () => Tr.SettingsExifFieldDateTaken, () => Tr.SettingsExifFieldCamera,
            () => Tr.SettingsExifFieldLens, () => Tr.SettingsExifFieldIso, () => Tr.SettingsExifFieldFocalLength,
            () => Tr.SettingsExifFieldAperture, () => Tr.SettingsExifFieldShutterSpeed,
        ];
        for (var i = 0; i < titleNames.Length; i++)
        {
            _titleFlow.Checks.Add(new D2DCheckWidget("Title" + TitleOrder[i], titleNames[i]));
        }

        var title = new D2DGroupWidget("GroupTitleBar", () => Tr.SettingsGroupTitleBar);
        title.Children.AddRange([new D2DNoteWidget("TitleBarHint", () => Tr.SettingsGroupTitleBarHint, aligned: false), _titleFlow]);

        foreach (var group in new[] { display, toolbar, overlay, perf, exif, title })
        {
            view.AddTop(group);
        }

        view.AddFooter(new D2DButtonWidget("ResetPage", () => Tr.SettingsResetPage));
        view.AddFooter(new D2DButtonWidget("Cancel", () => Tr.CommonCancel));
        view.AddFooter(new D2DButtonWidget("Apply", () => Tr.CommonApply));
        view.AddFooter(new D2DButtonWidget("Save", () => Tr.CommonSave, isDefault: true));
        view.Changed += _ => UpdateDependents();
    }

    public D2DSettingsView View { get; }

    public int WidgetCount => View.AllWidgets().Count();

    public void Load(SettingsFormState state)
    {
        _showInfoOverlay.Checked = state.ShowInfoOverlay;
        _showFileInfo.Checked = state.ShowFileInfo;
        _showFolderInfo.Checked = state.ShowFolderInfo;
        _fontSize.Box.Text = state.InfoOverlayFontSizeText;
        _showZoomIndicator.Checked = state.ShowZoomIndicator;
        _toolbarAutoHide.Checked = state.ToolbarAutoHide;
        _toolbarDelay.Box.Text = state.ToolbarAutoHideDelayText;
        _infoAutoHide.Checked = state.InfoOverlayAutoHide;
        _infoDelay.Box.Text = state.InfoOverlayAutoHideDelayText;
        _opacity.Value = Math.Clamp((int)Math.Round(state.ToolbarOpacityValue), _opacity.Min, _opacity.Max);
        _loadingMode.Selected = state.LoadingModeIndex;
        _scalingQuality.Selected = state.ScalingQualityIndex;
        _showExif.Checked = state.ShowExifInfo;
        for (var i = 0; i < ExifOrder.Length; i++)
        {
            _exifFlow.Checks[i].Checked = state.ExifInfoFields.HasFlag(ExifOrder[i]);
        }

        for (var i = 0; i < TitleOrder.Length; i++)
        {
            _titleFlow.Checks[i].Checked = state.TitleBarFields.HasFlag(TitleOrder[i]);
        }

        UpdateDependents();
        View.NeedsRedraw = true;
    }

    public void Collect(SettingsFormState state)
    {
        state.ShowInfoOverlay = _showInfoOverlay.Checked;
        state.ShowFileInfo = _showFileInfo.Checked;
        state.ShowFolderInfo = _showFolderInfo.Checked;
        state.InfoOverlayFontSizeText = _fontSize.Box.Text;
        state.ShowZoomIndicator = _showZoomIndicator.Checked;
        state.ToolbarAutoHide = _toolbarAutoHide.Checked;
        state.ToolbarAutoHideDelayText = _toolbarDelay.Box.Text;
        state.InfoOverlayAutoHide = _infoAutoHide.Checked;
        state.InfoOverlayAutoHideDelayText = _infoDelay.Box.Text;
        state.ToolbarOpacityValue = _opacity.Value;
        state.LoadingModeIndex = _loadingMode.Selected;
        state.ScalingQualityIndex = _scalingQuality.Selected;
        state.ShowExifInfo = _showExif.Checked;
        var exif = ExifInfoFields.None;
        for (var i = 0; i < ExifOrder.Length; i++)
        {
            if (_exifFlow.Checks[i].Checked)
            {
                exif |= ExifOrder[i];
            }
        }

        state.ExifInfoFields = exif;
        var title = TitleBarFields.None;
        for (var i = 0; i < TitleOrder.Length; i++)
        {
            if (_titleFlow.Checks[i].Checked)
            {
                title |= TitleOrder[i];
            }
        }

        state.TitleBarFields = title;
    }

    /// <summary>Ô con chỉ có nghĩa khi ô cha bật (như trang cửa sổ con).</summary>
    public void UpdateDependents()
    {
        _showFileInfo.Enabled = _showFolderInfo.Enabled = _showInfoOverlay.Checked;
        _toolbarDelay.Enabled = _toolbarAutoHide.Checked;
        _infoDelay.Enabled = _infoAutoHide.Checked;
        foreach (var check in _exifFlow.Checks)
        {
            check.Enabled = _showExif.Checked;
        }

        if (View.Focused is { Enabled: false } || View.Focused is { Focusable: false })
        {
            View.SetFocus(_showInfoOverlay);
        }
    }
}
