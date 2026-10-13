using PhotoReview.App.Presenters;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Dialogs.SettingsUi.Pages;

/// <summary>
/// Trang "Display" (WPF: <c>DisplayPage</c>): lớp thông tin trên ảnh, thanh công cụ (tự ẩn + độ mờ), thông tin tự ẩn, nạp/chất lượng,
/// dòng EXIF, tiêu đề cửa sổ. Thuộc tính: ShowInfoOverlay, ShowFileInfo, ShowFolderInfo, InfoOverlayFontSize, ShowZoomIndicator,
/// ToolbarAutoHide(+DelayMs), InfoOverlayAutoHide(+DelayMs), ToolbarOpacityPercent, LoadingMode, ScalingQuality, ShowExifInfo,
/// ExifInfoFields, TitleBarFields.
/// </summary>
internal sealed class DisplaySettingsPage : SettingsPageBase
{
    private int _showInfoOverlay;
    private int _showFileInfo;
    private int _showFolderInfo;
    private int _fontSize;
    private int _showZoomIndicator;
    private int _toolbarAutoHide;
    private int _toolbarDelay;
    private int _opacity;
    private int _opacityValue;
    private int _infoAutoHide;
    private int _infoDelay;
    private int _loadingMode;
    private int _scalingQuality;
    private int _showExif;
    private IReadOnlyList<int> _exifChecks = [];
    private IReadOnlyList<int> _titleChecks = [];

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

    public override string Key => "Display";

    /// <summary>Id điều khiển (test).</summary>
    internal (int ShowInfoOverlay, int ShowFileInfo, int ShowFolderInfo, int FontSize, int ShowZoomIndicator, int ToolbarAutoHide, int ToolbarDelay,
        int Opacity, int OpacityValue, int InfoAutoHide, int InfoDelay, int LoadingMode, int ScalingQuality, int ShowExif) Ids
        => (_showInfoOverlay, _showFileInfo, _showFolderInfo, _fontSize, _showZoomIndicator, _toolbarAutoHide, _toolbarDelay, _opacity,
            _opacityValue, _infoAutoHide, _infoDelay, _loadingMode, _scalingQuality, _showExif);

    internal IReadOnlyList<int> ExifChecks => _exifChecks;

    internal IReadOnlyList<int> TitleChecks => _titleChecks;

    public override void Declare(SettingsPageBuilder page)
    {
        page.Group(() => Tr.SettingsNavDisplay, g =>
        {
            _showInfoOverlay = g.Check(() => Tr.SettingsShowInfoOverlayLabel);
            _showFileInfo = g.Check(() => Tr.SettingsShowFileInfoLabel, indentDlu: 12);
            _showFolderInfo = g.Check(() => Tr.SettingsShowFolderInfoLabel, indentDlu: 12);
            _fontSize = g.Edit(() => Tr.SettingsInfoOverlayFontSizeLabel, widthDlu: 40).Control;
            _showZoomIndicator = g.Check(() => Tr.SettingsShowZoomIndicatorLabel);
        });

        page.Group(() => Tr.SettingsGroupToolbar, g =>
        {
            _toolbarAutoHide = g.Check(() => Tr.SettingsToolbarAutoHideLabel);
            _toolbarDelay = g.Edit(() => Tr.SettingsToolbarAutoHideDelayLabel, widthDlu: 45).Control;
            g.Note(() => Tr.SettingsToolbarAutoHideHint);
            // Q-R34: toolbar opacity while visible, [MinToolbarOpacityPercent, 100]; applies on Save.
            var slider = g.Slider(() => Tr.SettingsToolbarOpacityLabel, AppSettings.MinToolbarOpacityPercent, AppSettings.MaxToolbarOpacityPercent);
            _opacity = slider.Slider;
            _opacityValue = slider.Value;
            g.Note(() => SettingsPresenter.ToolbarOpacityHint());
        });

        page.Group(() => Tr.SettingsGroupInfoOverlay, g =>
        {
            _infoAutoHide = g.Check(() => Tr.SettingsInfoOverlayAutoHideLabel);
            _infoDelay = g.Edit(() => Tr.SettingsInfoOverlayAutoHideDelayLabel, widthDlu: 45).Control;
            g.Note(() => Tr.SettingsInfoOverlayAutoHideHint);
        });

        page.Group(() => Tr.SettingsGroupDisplayPerformance, g =>
        {
            // Item order is the contract with SettingsFormState.LoadingModeIndex / ScalingQualityIndex.
            _loadingMode = g.Combo(() => Tr.SettingsLoadingModeLabel,
            [
                () => Tr.EnumLoadingModeFast,
                () => Tr.EnumLoadingModePreview,
                () => Tr.SettingsLoadingModeOriginalWithHint,
            ]).Control;
            _scalingQuality = g.Combo(() => Tr.SettingsScalingQualityLabel,
            [
                () => Tr.EnumScalingQualityHighQuality,
                () => Tr.EnumScalingQualityLinear,
            ]).Control;
        });

        page.Group(() => Tr.SettingsGroupExif, g =>
        {
            _showExif = g.Check(() => Tr.SettingsShowExifInfoLabel);
            g.Note(() => Tr.SettingsGroupExifHint, aligned: false);
            _exifChecks = g.CheckFlow(
            [
                () => Tr.SettingsExifFieldFileName, () => Tr.SettingsExifFieldDateTaken, () => Tr.SettingsExifFieldModifiedDate,
                () => Tr.SettingsExifFieldDimensions, () => Tr.SettingsExifFieldCamera, () => Tr.SettingsExifFieldLens,
                () => Tr.SettingsExifFieldIso, () => Tr.SettingsExifFieldFocalLength, () => Tr.SettingsExifFieldAperture,
                () => Tr.SettingsExifFieldShutterSpeed,
            ], indentDlu: 12);
        });

        page.Group(() => Tr.SettingsGroupTitleBar, g =>
        {
            g.Note(() => Tr.SettingsGroupTitleBarHint, aligned: false);
            _titleChecks = g.CheckFlow(
            [
                () => Tr.SettingsTitleBarFieldFolderName, () => Tr.SettingsTitleBarFieldFolderPath, () => Tr.SettingsTitleBarFieldIndexCount,
                () => Tr.SettingsExifFieldFileName, () => Tr.SettingsTitleBarFieldFileSize, () => Tr.SettingsExifFieldDimensions,
                () => Tr.SettingsExifFieldModifiedDate, () => Tr.SettingsExifFieldDateTaken, () => Tr.SettingsExifFieldCamera,
                () => Tr.SettingsExifFieldLens, () => Tr.SettingsExifFieldIso, () => Tr.SettingsExifFieldFocalLength,
                () => Tr.SettingsExifFieldAperture, () => Tr.SettingsExifFieldShutterSpeed,
            ]);
        });
    }

    public override void Load(SettingsFormState state)
    {
        Context.SetChecked(_showInfoOverlay, state.ShowInfoOverlay);
        Context.SetChecked(_showFileInfo, state.ShowFileInfo);
        Context.SetChecked(_showFolderInfo, state.ShowFolderInfo);
        Context.SetText(_fontSize, state.InfoOverlayFontSizeText);
        Context.SetChecked(_showZoomIndicator, state.ShowZoomIndicator);
        Context.SetChecked(_toolbarAutoHide, state.ToolbarAutoHide);
        Context.SetText(_toolbarDelay, state.ToolbarAutoHideDelayText);
        Context.SetChecked(_infoAutoHide, state.InfoOverlayAutoHide);
        Context.SetText(_infoDelay, state.InfoOverlayAutoHideDelayText);
        Context.SetSliderValue(_opacity, (int)Math.Round(state.ToolbarOpacityValue));
        Context.SetComboIndex(_loadingMode, state.LoadingModeIndex);
        Context.SetComboIndex(_scalingQuality, state.ScalingQualityIndex);
        Context.SetChecked(_showExif, state.ShowExifInfo);
        for (var i = 0; i < ExifOrder.Length; i++)
        {
            Context.SetChecked(_exifChecks[i], state.ExifInfoFields.HasFlag(ExifOrder[i]));
        }

        for (var i = 0; i < TitleOrder.Length; i++)
        {
            Context.SetChecked(_titleChecks[i], state.TitleBarFields.HasFlag(TitleOrder[i]));
        }

        UpdateDependents();
        UpdateOpacityValue();
    }

    public override void Collect(SettingsFormState state)
    {
        state.ShowInfoOverlay = Context.IsChecked(_showInfoOverlay);
        state.ShowFileInfo = Context.IsChecked(_showFileInfo);
        state.ShowFolderInfo = Context.IsChecked(_showFolderInfo);
        state.InfoOverlayFontSizeText = Context.Text(_fontSize);
        state.ShowZoomIndicator = Context.IsChecked(_showZoomIndicator);
        state.ToolbarAutoHide = Context.IsChecked(_toolbarAutoHide);
        state.ToolbarAutoHideDelayText = Context.Text(_toolbarDelay);
        state.InfoOverlayAutoHide = Context.IsChecked(_infoAutoHide);
        state.InfoOverlayAutoHideDelayText = Context.Text(_infoDelay);
        state.ToolbarOpacityValue = Context.SliderValue(_opacity);
        state.LoadingModeIndex = Context.ComboIndex(_loadingMode);
        state.ScalingQualityIndex = Context.ComboIndex(_scalingQuality);
        state.ShowExifInfo = Context.IsChecked(_showExif);
        var exif = ExifInfoFields.None;
        for (var i = 0; i < ExifOrder.Length; i++)
        {
            if (Context.IsChecked(_exifChecks[i]))
            {
                exif |= ExifOrder[i];
            }
        }

        state.ExifInfoFields = exif;
        var title = TitleBarFields.None;
        for (var i = 0; i < TitleOrder.Length; i++)
        {
            if (Context.IsChecked(_titleChecks[i]))
            {
                title |= TitleOrder[i];
            }
        }

        state.TitleBarFields = title;
    }

    public override bool OnCommand(int id, int notification)
    {
        if (notification != DialogConstants.BnClicked)
        {
            return false;
        }

        if (id == _showInfoOverlay || id == _toolbarAutoHide || id == _infoAutoHide || id == _showExif)
        {
            UpdateDependents();
            return true;
        }

        return false;
    }

    public override void OnScroll(int id)
    {
        if (id == _opacity)
        {
            UpdateOpacityValue();
        }
    }

    public override void OnLanguageChanged() => UpdateOpacityValue();

    public override int? FocusTarget(SettingsFocusField field) => field switch
    {
        SettingsFocusField.InfoOverlayAutoHideDelay => _infoDelay,
        _ => null,
    };

    private void UpdateOpacityValue()
        => Context.SetText(_opacityValue, SettingsPresenter.ToolbarOpacityValueText(Context.SliderValue(_opacity)));

    /// <summary>Sub-options only matter when their parent is on (as the WPF page enables/disables them).</summary>
    private void UpdateDependents()
    {
        var overlay = Context.IsChecked(_showInfoOverlay);
        Context.SetEnabled(_showFileInfo, overlay);
        Context.SetEnabled(_showFolderInfo, overlay);
        Context.SetEnabled(_toolbarDelay, Context.IsChecked(_toolbarAutoHide));
        Context.SetEnabled(_infoDelay, Context.IsChecked(_infoAutoHide));
        var exif = Context.IsChecked(_showExif);
        foreach (var id in _exifChecks)
        {
            Context.SetEnabled(id, exif);
        }
    }
}
