using PhotoReview.App.Presenters;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;

namespace PhotoReview.Shell.Win32.Dialogs.SettingsUi.Pages;

/// <summary>
/// Trang "Performance" (WPF: <c>PerformancePage</c>): bộ giải mã ảnh, kích thước cache RAM (thanh trượt %, khoảng đổi theo cửa sổ preload và
/// RAM máy), số ảnh preload phía trước/phía sau. Thuộc tính: DecoderBackend, ImageCacheRamPercent, PreloadForwardCount,
/// PreloadBackwardCount. RAW/WebP/HEIC/so sánh nằm ở trang Files (như WPF); các tham số nội bộ (dung lượng cache đĩa, số worker, ...) chỉ
/// sửa tay trong config.json (SettingsFieldMap.InternalOnly) nên không có điều khiển.
/// </summary>
internal sealed class PerformanceSettingsPage : SettingsPageBase
{
    private const int EnChange = 0x0300;
    private const int CbnSelChange = 1;

    /// <summary>Chỉ số mục TurboJpeg trong combobox (hợp đồng <c>SettingsFormState.DecoderBackendIndex</c>).</summary>
    internal const int TurboJpegIndex = 2;

    private int _decoder;
    private int _turboNote;
    private int _ramSlider;
    private int _ramValue;
    private int _ramHint;
    private int _forward;
    private int _backward;
    private int _lastDecoder;
    private bool _turboAvailable = true;

    public override string Key => "Performance";

    /// <summary>Id điều khiển (test).</summary>
    internal (int Decoder, int TurboNote, int RamSlider, int RamValue, int RamHint, int Forward, int Backward) Ids
        => (_decoder, _turboNote, _ramSlider, _ramValue, _ramHint, _forward, _backward);

    public override void Declare(SettingsPageBuilder page)
    {
        page.Group(() => Tr.SettingsNavPerformance, g =>
        {
            // Item order is the contract with SettingsFormState.DecoderBackendIndex (Wpf, WicDirect, TurboJpeg).
            _decoder = g.Combo(() => Tr.SettingsDecoderBackendLabel,
            [
                () => Tr.EnumDecoderBackendWpf,
                () => Tr.EnumDecoderBackendWicDirect,
                () => Tr.EnumDecoderBackendTurboJpeg,
            ]).Control;

            // A Win32 combobox has no per-item enable: the page shows this note and refuses to switch to TurboJpeg when it is missing.
            _turboNote = g.Note(null);

            // RAM%: in-memory preview cache share of physical RAM, [system minimum, 90]; applies after restart.
            var slider = g.Slider(() => Tr.SettingsRamCacheLabel, PerformanceOptions.MinImageCacheRamPercent, PerformanceOptions.MaxImageCacheRamPercent);
            _ramSlider = slider.Slider;
            _ramValue = slider.Value;
            _ramHint = g.Note(null);

            _forward = g.Edit(() => Tr.SettingsPreloadForwardLabel, widthDlu: 45).Control;
            _backward = g.Edit(() => Tr.SettingsPreloadBackwardLabel, widthDlu: 45).Control;
            g.Note(() => SettingsPresenter.PreloadWindowHint());
        });
    }

    public override void Attach(SettingsPageContext context)
    {
        base.Attach(context);
        _turboAvailable = SettingsPresenter.IsTurboJpegAvailable(context.DecoderFactory);
        Context.SetVisible(_turboNote, !_turboAvailable);
        UpdateTurboNote();
    }

    public override void Load(SettingsFormState state)
    {
        Context.SetComboIndex(_decoder, state.DecoderBackendIndex);
        _lastDecoder = Context.ComboIndex(_decoder);
        Context.SetText(_forward, state.PreloadForwardText);
        Context.SetText(_backward, state.PreloadBackwardText);
        UpdateRange();
        Context.SetSliderValue(_ramSlider, (int)Math.Round(state.RamCachePercent));
        UpdateRamTexts();
    }

    public override void Collect(SettingsFormState state)
    {
        state.DecoderBackendIndex = Context.ComboIndex(_decoder);
        state.RamCachePercent = Context.SliderValue(_ramSlider);
        state.PreloadForwardText = Context.Text(_forward);
        state.PreloadBackwardText = Context.Text(_backward);
    }

    public override bool OnCommand(int id, int notification)
    {
        if (id == _decoder && notification == CbnSelChange)
        {
            var chosen = Context.ComboIndex(_decoder);
            if (chosen == TurboJpegIndex && !_turboAvailable && _lastDecoder != TurboJpegIndex)
            {
                Context.SetComboIndex(_decoder, _lastDecoder);
            }
            else
            {
                _lastDecoder = chosen;
            }

            return true;
        }

        if ((id == _forward || id == _backward) && notification == EnChange)
        {
            UpdateRange();
            UpdateRamTexts();
            return true;
        }

        return false;
    }

    public override void OnScroll(int id)
    {
        if (id == _ramSlider)
        {
            UpdateRamValue();
        }
    }

    public override void OnLanguageChanged()
    {
        UpdateTurboNote();
        UpdateRamTexts();
    }

    public override int? FocusTarget(SettingsFocusField field) => field switch
    {
        SettingsFocusField.PreloadForward => _forward,
        SettingsFocusField.PreloadBackward => _backward,
        _ => null,
    };

    /// <summary>The slider range follows the preload window: the app picks the minimum so the whole window fits in the cache.</summary>
    private void UpdateRange()
    {
        var (min, max) = Context.Presenter.RamCacheRange(Context.Text(_forward), Context.Text(_backward));
        Context.SetSliderRange(_ramSlider, min, max);
    }

    private void UpdateRamTexts()
    {
        var (min, max) = Context.Presenter.RamCacheRange(Context.Text(_forward), Context.Text(_backward));
        Context.SetText(_ramHint, Context.Presenter.RamCacheHintText(min, max));
        UpdateRamValue();
        Context.Relayout();
    }

    private void UpdateRamValue()
        => Context.SetText(_ramValue, Context.Presenter.RamCacheValueText(Context.SliderValue(_ramSlider)));

    private void UpdateTurboNote()
    {
        if (!_turboAvailable)
        {
            Context.SetText(_turboNote, Tr.SettingsDecoderBackendTurboJpegUnavailable);
        }
    }
}
