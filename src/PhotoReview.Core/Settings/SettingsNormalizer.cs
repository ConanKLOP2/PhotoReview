using PhotoReview.Core.Model;

namespace PhotoReview.Core.Settings;

/// <summary>
/// Repairs values of a freshly loaded <see cref="AppSettings"/> that a hand-edited or corrupt <c>config.json</c> can make
/// unusable (R2-F-04): a non-positive cache capacity makes the cache constructor throw, a <c>null</c> entry in
/// <c>Actions</c> throws while building the shortcut router. Every invalid value is replaced by its default so start-up
/// always succeeds; the names of the repaired settings are returned so the caller can warn the user.
/// </summary>
public static class SettingsNormalizer
{
    /// <summary>Normalises <paramref name="settings"/> in place; returns the names of the settings that were reset (empty when all valid).</summary>
    public static IReadOnlyList<string> Normalize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var fixedNames = new List<string>();

        if (settings.ImageCacheCapacityBytes <= 0)
        {
            settings.ImageCacheCapacityBytes = PerformanceOptions.ImageCacheCapacityBytes;
            fixedNames.Add(nameof(AppSettings.ImageCacheCapacityBytes));
        }
        // The device-dependent minimum (preload window) is applied where physical RAM is known (RamBudgetPolicy); here
        // only the device-independent range is enforced, clamping to the nearest bound rather than resetting.
        var ramPercent = Math.Clamp(settings.ImageCacheRamPercent,
            PerformanceOptions.MinImageCacheRamPercent, PerformanceOptions.MaxImageCacheRamPercent);
        if (ramPercent != settings.ImageCacheRamPercent)
        {
            settings.ImageCacheRamPercent = ramPercent;
            fixedNames.Add(nameof(AppSettings.ImageCacheRamPercent));
        }
        if (settings.SourceBytesCapacityBytes <= 0)
        {
            settings.SourceBytesCapacityBytes = PerformanceOptions.SourceBytesCapacityBytes;
            fixedNames.Add(nameof(AppSettings.SourceBytesCapacityBytes));
        }
        if (settings.MemoryReserveBytes < 0)
        {
            settings.MemoryReserveBytes = PerformanceOptions.MemoryReserveBytes;
            fixedNames.Add(nameof(AppSettings.MemoryReserveBytes));
        }
        if (settings.PreviewDiskCacheCapacityBytes < 0)
        {
            settings.PreviewDiskCacheCapacityBytes = PerformanceOptions.PreviewDiskCacheCapacityBytes;
            fixedNames.Add(nameof(AppSettings.PreviewDiskCacheCapacityBytes));
        }
        if (settings.PreloadWorkerCount <= 0)
        {
            settings.PreloadWorkerCount = PerformanceOptions.PreloadWorkerCount;
            fixedNames.Add(nameof(AppSettings.PreloadWorkerCount));
        }
        else if (settings.PreloadWorkerCount > PerformanceOptions.MaxPreloadWorkerCount)
        {
            settings.PreloadWorkerCount = PerformanceOptions.MaxPreloadWorkerCount;
            fixedNames.Add(nameof(AppSettings.PreloadWorkerCount));
        }
        if (!(settings.PreloadMemoryLoadLimit > 0 && settings.PreloadMemoryLoadLimit <= 1))
        {
            settings.PreloadMemoryLoadLimit = PerformanceOptions.PreloadMemoryLoadLimit;
            fixedNames.Add(nameof(AppSettings.PreloadMemoryLoadLimit));
        }
        var preloadForward = Math.Clamp(settings.PreloadForwardCount,
            PerformanceOptions.MinPreloadForwardCount, PerformanceOptions.MaxPreloadCount);
        if (preloadForward != settings.PreloadForwardCount)
        {
            settings.PreloadForwardCount = preloadForward;
            fixedNames.Add(nameof(AppSettings.PreloadForwardCount));
        }
        var preloadBackward = Math.Clamp(settings.PreloadBackwardCount,
            PerformanceOptions.MinPreloadBackwardCount, PerformanceOptions.MaxPreloadCount);
        if (preloadBackward != settings.PreloadBackwardCount)
        {
            settings.PreloadBackwardCount = preloadBackward;
            fixedNames.Add(nameof(AppSettings.PreloadBackwardCount));
        }

        if (!Enum.IsDefined(settings.InitialViewMode)) { settings.InitialViewMode = InitialViewMode.Fit; fixedNames.Add(nameof(AppSettings.InitialViewMode)); }
        if (!Enum.IsDefined(settings.LoadingMode)) { settings.LoadingMode = LoadingMode.Preview; fixedNames.Add(nameof(AppSettings.LoadingMode)); }
        if (!Enum.IsDefined(settings.ImageSortMode)) { settings.ImageSortMode = ImageSortMode.Name; fixedNames.Add(nameof(AppSettings.ImageSortMode)); }
        if (!Enum.IsDefined(settings.ScalingQuality)) { settings.ScalingQuality = ScalingQuality.HighQuality; fixedNames.Add(nameof(AppSettings.ScalingQuality)); }
        if (!Enum.IsDefined(settings.DecoderBackend)) { settings.DecoderBackend = DecoderBackend.WicDirect; fixedNames.Add(nameof(AppSettings.DecoderBackend)); }
        if (!Enum.IsDefined(settings.JournalDurability)) { settings.JournalDurability = JournalDurability.Fast; fixedNames.Add(nameof(AppSettings.JournalDurability)); }
        if (!Enum.IsDefined(settings.InstanceMode)) { settings.InstanceMode = InstanceMode.SingleWindow; fixedNames.Add(nameof(AppSettings.InstanceMode)); }

        if (settings.Actions is { } actions)
        {
            var removed = actions.RemoveAll(a => a is null);
            var invalidOperation = false;
            foreach (var action in actions)
            {
                action.Name ??= "";
                action.Shortcut ??= "";
                action.Destination ??= "";
                if (!Enum.IsDefined(action.Operation))
                {
                    action.Operation = FileOperationType.Move;
                    invalidOperation = true;
                }
            }
            if (removed > 0 || invalidOperation) fixedNames.Add(nameof(AppSettings.Actions));
        }

        if (settings.Shortcuts is { } shortcuts)
        {
            // Mandatory shortcuts: a null/blank value (hand-edited or corrupt file) would leave the action unreachable and
            // fail validation on the next Save, so it falls back to its default.
            var defaults = ShortcutMappings.Default();
            var resetShortcut = false;
            foreach (var property in typeof(ShortcutMappings).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (property.PropertyType != typeof(string) || !property.CanWrite || ShortcutMappings.IsOptional(property.Name) || property.Name == nameof(ShortcutMappings.MoveToFolder2)) continue; // MoveToFolder2: legacy alias, ignored by the validator
                if (!string.IsNullOrWhiteSpace((string?)property.GetValue(shortcuts))) continue;
                property.SetValue(shortcuts, property.GetValue(defaults));
                resetShortcut = true;
            }
            if (resetShortcut) fixedNames.Add(nameof(AppSettings.Shortcuts));
            shortcuts.MoveToFolder2 ??= ""; // legacy alias nobody reads: only keep it non-null
            // Optional shortcuts: an explicit null in a hand-edited file means "disabled", like "".
            shortcuts.LastImage = shortcuts.LastImage?.Trim() ?? "";
            shortcuts.ZoomActualSize = shortcuts.ZoomActualSize?.Trim() ?? "";
            shortcuts.ToggleInfoOverlay = shortcuts.ToggleInfoOverlay?.Trim() ?? "";
            shortcuts.ClickZoom = shortcuts.ClickZoom?.Trim() ?? "";
        }

        ShortcutKeyCanonical.CanonicalizeAll(settings); // Q-R25: Return/Enter, Prior/PageUp ... are one key

        // feat/mouse-zoom
        if (!Enum.IsDefined(settings.MouseWheelAction)) { settings.MouseWheelAction = MouseWheelAction.Zoom; fixedNames.Add(nameof(AppSettings.MouseWheelAction)); }
        if (!Enum.IsDefined(settings.KineticGlideSmoothing)) { settings.KineticGlideSmoothing = new AppSettings().KineticGlideSmoothing; fixedNames.Add(nameof(AppSettings.KineticGlideSmoothing)); }
        var clickZoom = Math.Clamp(settings.ClickZoomPercent, AppSettings.MinClickZoomPercent, AppSettings.MaxClickZoomPercent);
        if (clickZoom != settings.ClickZoomPercent)
        {
            settings.ClickZoomPercent = clickZoom;
            fixedNames.Add(nameof(AppSettings.ClickZoomPercent));
        }
        var arrowPanStep = Math.Clamp(settings.ArrowPanStepPercent, AppSettings.MinArrowPanStepPercent, AppSettings.MaxArrowPanStepPercent);
        if (arrowPanStep != settings.ArrowPanStepPercent)
        {
            settings.ArrowPanStepPercent = arrowPanStep;
            fixedNames.Add(nameof(AppSettings.ArrowPanStepPercent));
        }

        // feat/ui-dark-chrome-toolbar
        var toolbarDelay = Math.Clamp(settings.ToolbarAutoHideDelayMs, AppSettings.MinToolbarAutoHideDelayMs, AppSettings.MaxToolbarAutoHideDelayMs);
        if (toolbarDelay != settings.ToolbarAutoHideDelayMs)
        {
            settings.ToolbarAutoHideDelayMs = toolbarDelay;
            fixedNames.Add(nameof(AppSettings.ToolbarAutoHideDelayMs));
        }
        var infoDelay = Math.Clamp(settings.InfoOverlayAutoHideDelayMs, AppSettings.MinInfoOverlayAutoHideDelayMs, AppSettings.MaxInfoOverlayAutoHideDelayMs);
        if (infoDelay != settings.InfoOverlayAutoHideDelayMs)
        {
            settings.InfoOverlayAutoHideDelayMs = infoDelay;
            fixedNames.Add(nameof(AppSettings.InfoOverlayAutoHideDelayMs));
        }
        var toolbarOpacity = Math.Clamp(settings.ToolbarOpacityPercent, AppSettings.MinToolbarOpacityPercent, AppSettings.MaxToolbarOpacityPercent);
        if (toolbarOpacity != settings.ToolbarOpacityPercent)
        {
            settings.ToolbarOpacityPercent = toolbarOpacity;
            fixedNames.Add(nameof(AppSettings.ToolbarOpacityPercent));
        }
        var infoFontSize = Math.Clamp(settings.InfoOverlayFontSize, AppSettings.MinInfoOverlayFontSize, AppSettings.MaxInfoOverlayFontSize);
        if (infoFontSize != settings.InfoOverlayFontSize)
        {
            settings.InfoOverlayFontSize = infoFontSize;
            fixedNames.Add(nameof(AppSettings.InfoOverlayFontSize));
        }
        return fixedNames;
    }

    /// <summary>
    /// The optional shortcuts (<see cref="ShortcutMappings.OptionalNames"/>) were added after users had already bound
    /// their own actions: a config written before them loads their defaults (End, D1, I, M, Y), which may already belong to an
    /// action or another shortcut. Such an optional shortcut is disabled (set to empty) so the existing binding keeps
    /// working and the settings stay valid for <see cref="SettingsValidator"/>. Returns the names of the disabled shortcuts.
    /// </summary>
    public static IReadOnlyList<string> DisableConflictingOptionalShortcuts(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var disabled = new List<string>();
        if (settings.Shortcuts is not { } shortcuts) return disabled;

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Take(string? key)
        {
            if (!string.IsNullOrWhiteSpace(key)) taken.Add(ShortcutKeyCanonical.Canonicalize(key));
        }
        // Same set the validator checks (MoveToFolder2 is a legacy alias owned by the actions).
        Take(shortcuts.Next); Take(shortcuts.Previous); Take(shortcuts.SendToRecycleBin); Take(shortcuts.Compare);
        Take(shortcuts.NextFolder); Take(shortcuts.PreviousFolder); Take(shortcuts.FirstImage); Take(shortcuts.ZoomIn);
        Take(shortcuts.ZoomOut); Take(shortcuts.ToggleFit); Take(shortcuts.Skip); Take(shortcuts.Undo); Take(shortcuts.Fullscreen);
        foreach (var action in settings.Actions ?? []) Take(action?.Shortcut);

        string Resolve(string name, string? value)
        {
            var key = ShortcutKeyCanonical.Canonicalize(value);
            if (key.Length == 0) return "";
            if (!taken.Add(key))
            {
                disabled.Add(name);
                return "";
            }
            return key;
        }
        shortcuts.LastImage = Resolve(nameof(ShortcutMappings.LastImage), shortcuts.LastImage);
        shortcuts.ZoomActualSize = Resolve(nameof(ShortcutMappings.ZoomActualSize), shortcuts.ZoomActualSize);
        shortcuts.ToggleInfoOverlay = Resolve(nameof(ShortcutMappings.ToggleInfoOverlay), shortcuts.ToggleInfoOverlay);
        shortcuts.MoveToFolder = Resolve(nameof(ShortcutMappings.MoveToFolder), shortcuts.MoveToFolder);
        shortcuts.CopyToFolder = Resolve(nameof(ShortcutMappings.CopyToFolder), shortcuts.CopyToFolder);
        shortcuts.ClickZoom = Resolve(nameof(ShortcutMappings.ClickZoom), shortcuts.ClickZoom);
        return disabled;
    }
}
