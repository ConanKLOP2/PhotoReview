using PhotoReview.App.Presenters;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Dialogs.SettingsUi.Pages;

/// <summary>
/// Trang "Mouse" (WPF: <c>MousePage</c>): nhóm Zoom (click zoom, bước phím mũi tên/zoom, neo, Fit width 1/2, giữ zoom, chuột giữa), nhóm
/// chuột và chuyển ảnh (con lăn, kinetic, mũi tên ở mép, chuyển ảnh mờ dần, touchpad) và nhóm menu chuột phải (một ô tick cho MỖI mục của
/// <see cref="ContextMenuItems.Items"/>, dựng bằng mã). Thuộc tính: ClickZoomPercent, ClickToZoomEnabled, ArrowPanStepPercent,
/// KeyboardZoomAnchor, FitWidthAnchor, FitWidthAnchor2, KeepZoomAcrossImages, KeyboardZoomStepPercent, MiddleClickAction,
/// ClickZoomKeyTogglesFit, MouseWheelAction, KineticPanEnabled, KineticGlideSmoothing, ArrowKeyNavigatesAtZoomEdge, ImageTransition(+Ms),
/// TouchpadSwipeEnabled(+DistancePerImage), HiddenContextMenuItems (+ ba cờ Show*MenuItems cũ do <c>ContextMenuItems</c> suy ra).
/// </summary>
internal sealed class MouseSettingsPage : SettingsPageBase
{
    private int _clickZoomPercent;
    private int _clickToZoom;
    private int _arrowPanStep;
    private int _keyboardZoomAnchor;
    private int _zoomShortcutsSummary;
    private int _fitWidthAnchor;
    private int _keepZoom;
    private int _keyboardZoomStep;
    private int _fitWidthAnchor2;
    private int _middleClick;
    private int _clickZoomKeyTogglesFit;
    private int _mouseWheel;
    private int _kineticPan;
    private int _kineticSmoothing;
    private int _arrowKeyAtEdge;
    private int _imageTransition;
    private int _imageTransitionMs;
    private int _touchpadSwipe;
    private int _touchpadDistance;
    private readonly Dictionary<ContextMenuItemId, int> _menuChecks = [];

    public override string Key => "Mouse";

    /// <summary>Id điều khiển (test).</summary>
    internal (int ClickZoomPercent, int ClickToZoom, int ArrowPanStep, int KeyboardZoomAnchor, int ZoomShortcutsSummary, int FitWidthAnchor,
        int KeepZoom, int KeyboardZoomStep, int FitWidthAnchor2, int MiddleClick, int ClickZoomKeyTogglesFit, int MouseWheel, int KineticPan,
        int KineticSmoothing, int ArrowKeyAtEdge, int ImageTransition, int ImageTransitionMs, int TouchpadSwipe, int TouchpadDistance) Ids
        => (_clickZoomPercent, _clickToZoom, _arrowPanStep, _keyboardZoomAnchor, _zoomShortcutsSummary, _fitWidthAnchor, _keepZoom,
            _keyboardZoomStep, _fitWidthAnchor2, _middleClick, _clickZoomKeyTogglesFit, _mouseWheel, _kineticPan, _kineticSmoothing,
            _arrowKeyAtEdge, _imageTransition, _imageTransitionMs, _touchpadSwipe, _touchpadDistance);

    /// <summary>Ô tick của menu chuột phải theo mục (test).</summary>
    internal IReadOnlyDictionary<ContextMenuItemId, int> MenuChecks => _menuChecks;

    public override void Declare(SettingsPageBuilder page)
    {
        page.Group(() => Tr.SettingsGroupZoom, g =>
        {
            _clickZoomPercent = g.Edit(() => Tr.SettingsClickZoomPercentLabel, widthDlu: 45).Control;
            g.Note(() => Tr.SettingsClickZoomPercentHint);
            _clickToZoom = g.Check(() => Tr.SettingsClickToZoomLabel);
            _arrowPanStep = g.Edit(() => Tr.SettingsArrowPanStepPercentLabel, widthDlu: 45).Control;
            g.Note(() => Tr.SettingsArrowPanStepPercentHint);
            // Item order is the contract with SettingsFormState.KeyboardZoomAnchorIndex (Pointer, ViewportCentre).
            _keyboardZoomAnchor = g.Combo(() => Tr.SettingsKeyboardZoomAnchorLabel,
            [
                () => Tr.EnumKeyboardZoomAnchorPointer,
                () => Tr.EnumKeyboardZoomAnchorViewportCentre,
            ]).Control;
            g.Note(() => Tr.SettingsKeyboardZoomAnchorHint);
            // Read-only summary of the zoom shortcuts (typed on the Shortcuts page): a title line, then the lines.
            g.Text(() => Tr.SettingsZoomShortcutsTitle);
            _zoomShortcutsSummary = g.Text();
            g.Note(() => Tr.SettingsZoomShortcutsHint, aligned: false);
            _fitWidthAnchor = g.Combo(() => Tr.SettingsFitWidthAnchorLabel, AnchorItems()).Control;
            g.Note(() => Tr.SettingsFitWidthAnchorHint);
            _keepZoom = g.Check(() => Tr.SettingsKeepZoomAcrossImagesLabel);
            g.Note(() => Tr.SettingsKeepZoomAcrossImagesHint, aligned: false);
            _keyboardZoomStep = g.Edit(() => Tr.SettingsKeyboardZoomStepPercentLabel, widthDlu: 45).Control;
            g.Note(() => SettingsPresenter.KeyboardZoomStepPercentHint());
            _fitWidthAnchor2 = g.Combo(() => Tr.SettingsFitWidthAnchor2Label, AnchorItems()).Control;
            g.Note(() => Tr.SettingsFitWidthAnchor2Hint);
            // Item order is the contract with SettingsFormState.MiddleClickActionIndex (= the MiddleClickAction enum value).
            _middleClick = g.Combo(() => Tr.SettingsMiddleClickActionLabel,
            [
                () => Tr.EnumMiddleClickActionNone,
                () => Tr.EnumMiddleClickActionClickZoom,
                () => Tr.EnumMiddleClickActionActualSize,
                () => Tr.EnumMiddleClickActionFit,
                () => Tr.EnumMiddleClickActionFitWidth,
                () => Tr.EnumMiddleClickActionFitWidth2,
                () => Tr.EnumMiddleClickActionPreviousImage,
                () => Tr.EnumMiddleClickActionNextImage,
                () => Tr.EnumMiddleClickActionPreviousFolder,
                () => Tr.EnumMiddleClickActionNextFolder,
                () => Tr.EnumMiddleClickActionOpenFolder,
            ]).Control;
            g.Note(() => Tr.SettingsMiddleClickActionHint);
            _clickZoomKeyTogglesFit = g.Check(() => Tr.SettingsClickZoomKeyTogglesFitLabel);
            g.Note(() => Tr.SettingsClickZoomKeyTogglesFitHint, aligned: false);
        });

        page.Group(() => Tr.SettingsGroupMouseZoom, g =>
        {
            _mouseWheel = g.Combo(() => Tr.SettingsMouseWheelActionLabel,
            [
                () => Tr.EnumMouseWheelActionZoom,
                () => Tr.EnumMouseWheelActionNavigate,
            ]).Control;
            g.Note(() => Tr.SettingsMouseWheelActionHint);
            _kineticPan = g.Check(() => Tr.SettingsKineticPanLabel);
            _kineticSmoothing = g.Combo(() => Tr.SettingsKineticGlideSmoothingLabel,
            [
                () => Tr.EnumKineticGlideSmoothingOff,
                () => Tr.EnumKineticGlideSmoothingPredict,
            ]).Control;
            g.Note(() => Tr.SettingsKineticGlideSmoothingHint);
            _arrowKeyAtEdge = g.Check(() => Tr.SettingsArrowKeyNavigatesAtZoomEdgeLabel);
            g.Note(() => Tr.SettingsArrowKeyNavigatesAtZoomEdgeHint, aligned: false);
            _imageTransition = g.Combo(() => Tr.SettingsImageTransitionLabel,
            [
                () => Tr.EnumImageTransitionNone,
                () => Tr.EnumImageTransitionFade,
            ]).Control;
            g.Note(() => Tr.SettingsImageTransitionHint);
            _imageTransitionMs = g.Edit(() => Tr.SettingsImageTransitionMsLabel, widthDlu: 45).Control;
            _touchpadSwipe = g.Check(() => Tr.SettingsTouchpadSwipeLabel);
            g.Note(() => Tr.SettingsTouchpadSwipeHint, aligned: false);
            _touchpadDistance = g.Edit(() => Tr.SettingsTouchpadSwipeDistanceLabel, widthDlu: 45).Control;
            g.Note(() => Tr.SettingsTouchpadSwipeDistanceHint);
        });

        page.Group(() => Tr.SettingsGroupContextMenu, g =>
        {
            g.Note(() => Tr.SettingsContextMenuHint, aligned: false);
            // One tick box per right-click menu item, built from the single item list (menu order; zoom children indented).
            foreach (var row in SettingsPresenter.ContextMenuRows(includeShellOnly: true))
            {
                var id = row.Id;
                _menuChecks[id] = g.Check(() => SettingsPresenter.ContextMenuItemLabel(id), indentDlu: row.IsChild ? 12 : 0);
            }
        });
    }

    private static IReadOnlyList<Func<string>> AnchorItems() =>
    [
        () => Tr.EnumFitWidthAnchorCentre,
        () => Tr.EnumFitWidthAnchorTopThird,
        () => Tr.EnumFitWidthAnchorBottomThird,
    ];

    public override void Attach(SettingsPageContext context)
    {
        base.Attach(context);
        // A locked item stays enabled-looking and re-ticks itself (OnCommand), because a disabled window never shows a tooltip.
        foreach (var row in SettingsPresenter.ContextMenuRows(includeShellOnly: true))
        {
            if (row.Locked && _menuChecks.TryGetValue(row.Id, out var id))
            {
                context.SetTooltip(id, () => Tr.SettingsContextMenuSettingsLockedTooltip);
            }
        }
    }

    public override void Load(SettingsFormState state)
    {
        Context.SetText(_clickZoomPercent, state.ClickZoomPercentText);
        Context.SetChecked(_clickToZoom, state.ClickToZoomEnabled);
        Context.SetText(_arrowPanStep, state.ArrowPanStepText);
        Context.SetComboIndex(_keyboardZoomAnchor, state.KeyboardZoomAnchorIndex);
        Context.SetComboIndex(_fitWidthAnchor, state.FitWidthAnchorIndex);
        Context.SetChecked(_keepZoom, state.KeepZoomAcrossImages);
        Context.SetText(_keyboardZoomStep, state.KeyboardZoomStepPercentText);
        Context.SetComboIndex(_fitWidthAnchor2, state.FitWidthAnchor2Index);
        Context.SetComboIndex(_middleClick, state.MiddleClickActionIndex);
        Context.SetChecked(_clickZoomKeyTogglesFit, state.ClickZoomKeyTogglesFit);
        Context.SetComboIndex(_mouseWheel, state.MouseWheelActionIndex);
        Context.SetChecked(_kineticPan, state.KineticPanEnabled);
        Context.SetComboIndex(_kineticSmoothing, state.KineticGlideSmoothingIndex);
        Context.SetChecked(_arrowKeyAtEdge, state.ArrowKeyNavigatesAtZoomEdge);
        Context.SetComboIndex(_imageTransition, state.ImageTransitionIndex);
        Context.SetText(_imageTransitionMs, state.ImageTransitionMsText);
        Context.SetChecked(_touchpadSwipe, state.TouchpadSwipeEnabled);
        Context.SetText(_touchpadDistance, state.TouchpadSwipeDistanceText);
        foreach (var (item, id) in _menuChecks)
        {
            Context.SetChecked(id, state.ContextMenuShown.Contains(item));
        }

        SetSummary(state.ShortcutTexts);
        UpdateDependents();
    }

    public override void Collect(SettingsFormState state)
    {
        state.ClickZoomPercentText = Context.Text(_clickZoomPercent);
        state.ClickToZoomEnabled = Context.IsChecked(_clickToZoom);
        state.ArrowPanStepText = Context.Text(_arrowPanStep);
        state.KeyboardZoomAnchorIndex = Context.ComboIndex(_keyboardZoomAnchor);
        state.FitWidthAnchorIndex = Context.ComboIndex(_fitWidthAnchor);
        state.KeepZoomAcrossImages = Context.IsChecked(_keepZoom);
        state.KeyboardZoomStepPercentText = Context.Text(_keyboardZoomStep);
        state.FitWidthAnchor2Index = Context.ComboIndex(_fitWidthAnchor2);
        state.MiddleClickActionIndex = Context.ComboIndex(_middleClick);
        state.ClickZoomKeyTogglesFit = Context.IsChecked(_clickZoomKeyTogglesFit);
        state.MouseWheelActionIndex = Context.ComboIndex(_mouseWheel);
        state.KineticPanEnabled = Context.IsChecked(_kineticPan);
        state.KineticGlideSmoothingIndex = Context.ComboIndex(_kineticSmoothing);
        state.ArrowKeyNavigatesAtZoomEdge = Context.IsChecked(_arrowKeyAtEdge);
        state.ImageTransitionIndex = Context.ComboIndex(_imageTransition);
        state.ImageTransitionMsText = Context.Text(_imageTransitionMs);
        state.TouchpadSwipeEnabled = Context.IsChecked(_touchpadSwipe);
        state.TouchpadSwipeDistanceText = Context.Text(_touchpadDistance);
        state.ContextMenuShown.Clear();
        foreach (var (item, id) in _menuChecks)
        {
            if (Context.IsChecked(id))
            {
                state.ContextMenuShown.Add(item);
            }
        }
    }

    public override bool OnCommand(int id, int notification)
    {
        if (id == _imageTransition && notification == ControlConstants.CbnSelChange)
        {
            UpdateDependents();
            return true;
        }

        if (notification == DialogConstants.BnClicked && _menuChecks.TryGetValue(ContextMenuItemId.Settings, out var settingsId)
            && id == settingsId)
        {
            // Settings can never be hidden: the user could not get back to this window.
            Context.SetChecked(settingsId, true);
            return true;
        }

        return false;
    }

    public override void OnActivated() => SetSummary(Context.CurrentShortcutTexts());

    public override void OnLanguageChanged()
    {
        SetSummary(Context.CurrentShortcutTexts()); // the dialog relayouts right after the language refresh
    }

    public override int? FocusTarget(SettingsFocusField field) => field switch
    {
        SettingsFocusField.ArrowPanStep => _arrowPanStep,
        SettingsFocusField.KeyboardZoomStep => _keyboardZoomStep,
        SettingsFocusField.ImageTransitionMs => _imageTransitionMs,
        SettingsFocusField.TouchpadSwipeDistance => _touchpadDistance,
        _ => null,
    };

    private void SetSummary(IReadOnlyDictionary<string, string> shortcutTexts)
    {
        var text = SettingsPresenter.ZoomShortcutSummary(shortcutTexts);
        if (Context.Text(_zoomShortcutsSummary) != text)
        {
            Context.SetText(_zoomShortcutsSummary, text);
        }
    }

    /// <summary>The fade duration only matters when the transition is Fade (index 1), as the WPF page enables it.</summary>
    private void UpdateDependents() => Context.SetEnabled(_imageTransitionMs, Context.ComboIndex(_imageTransition) == 1);
}
